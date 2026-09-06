using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;

namespace ChopItUp.Hub.Spawning;

/// <summary>The rules, and nothing but the rules. D2: only an owner message opens an exchange,
/// and an owner message always closes the one that was open. D8: a mention is the only trigger,
/// never one's own message, never a row that is not spawnable. D5: four turns, whoever holds the
/// last one is told so. D7: debounce, one in flight per (participant, room), minimum spacing per
/// participant. Returns notes for the caller to post as the hub; never posts itself.</summary>
public sealed class ExchangePolicy
{
    private readonly IReadOnlyDictionary<string, Participant> _roster;
    private readonly Mentions _mentions;
    private readonly SpawnLimits _limits;

    public ExchangePolicy(IReadOnlyList<Participant> roster, SpawnLimits limits)
    {
        _roster = roster.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _mentions = new Mentions(roster.Where(p => p.Kind != "system").Select(p => p.Id));
        _limits = limits;
    }

    /// <summary>A row the hub can spawn: a model with a model name of its own. App-backed rows are
    /// windows some program opens on the room, not something the hub starts.</summary>
    public static bool IsSpawnable(Participant p) => p.Kind == "model" && p.Model is not null;

    /// <summary>The room's exchange after this message, and the notes to post. The returned object is
    /// <paramref name="current"/> itself unless an owner message opened a new one.</summary>
    public (Exchange? Next, IReadOnlyList<string> Notes) OnMessage(Exchange? current, Message message, DateTimeOffset now, bool acceptMentions = true)
    {
        var notes = new List<string>();
        if (!_roster.TryGetValue(message.AuthorId, out var author) || author.Kind == "system") return (current, notes);

        // acceptMentions = false: the author is a spawn of an exchange that is no longer the room's
        // current one (superseded by the owner, D5/A6); its post lands, its mentions do not.
        var mentioned = acceptMentions
            ? _mentions.Find(message.Body)
                .Where(id => id != message.AuthorId && _roster.TryGetValue(id, out var p) && IsSpawnable(p))
                .ToList()
            : new List<string>();

        if (author.Kind == "human")
        {
            if (current is { Status: ExchangeStatus.Open })
            {
                // D5: the running spawn finishes (its completion lands on this object, which is no
                // longer open, so it cannot conclude or spawn); everything queued is dropped.
                current.Status = ExchangeStatus.Superseded;
                current.Pending.Clear();
            }
            if (mentioned.Count == 0) return (current, notes);
            var next = new Exchange { RoomId = message.RoomId, RootMessageId = message.Id, Budget = _limits.Budget };
            Accept(next, mentioned, message.Id, now, notes);
            return (next, notes);
        }

        // A model — spawn row or app-backed window — never opens an exchange (D2).
        if (current is not { Status: ExchangeStatus.Open }) return (current, notes);
        Accept(current, mentioned, message.Id, now, notes);
        return (current, notes);
    }

    private static void Accept(Exchange x, IReadOnlyList<string> mentioned, long messageId, DateTimeOffset now, List<string> notes)
    {
        var refused = new List<string>();
        foreach (var id in mentioned)
        {
            if (x.Pending.TryGetValue(id, out var pending))
            {
                pending.TriggerIds.Add(messageId);
                pending.LastTriggerAt = now;
                continue;
            }
            if (x.TurnsCommitted >= x.Budget) { refused.Add(id); continue; }
            var fresh = new PendingSpawn { LastTriggerAt = now };
            fresh.TriggerIds.Add(messageId);
            x.Pending[id] = fresh;
            x.TurnsCommitted++;
        }
        if (refused.Count > 0)
            notes.Add($"Budget of {x.Budget} turns is used up for the exchange started at #{x.RootMessageId}; not spawning {string.Join(", ", refused.Select(r => "@" + r))}. A new owner message starts a fresh exchange.");
    }

    /// <summary>Which pending spawns may launch now. <paramref name="inFlightInRoom"/> is the room's
    /// whole in-flight set, across exchanges — a superseded exchange's spawn still counts.</summary>
    public IReadOnlyList<SpawnRequest> Due(Exchange x, DateTimeOffset now, IReadOnlyDictionary<string, DateTimeOffset> lastStartByParticipant, IReadOnlySet<string> inFlightInRoom)
    {
        if (x.Status != ExchangeStatus.Open) return [];
        var due = new List<SpawnRequest>();
        foreach (var (id, pending) in x.Pending)
        {
            if (inFlightInRoom.Contains(id)) continue;
            if (now - pending.LastTriggerAt < _limits.Debounce) continue;
            if (lastStartByParticipant.TryGetValue(id, out var last) && now - last < _limits.MinSpacing) continue;
            due.Add(new SpawnRequest(x.RoomId, id, pending.TriggerIds.ToList(), x.RootMessageId, x.TurnsStarted + due.Count + 1, x.Budget - x.TurnsCommitted));
        }
        return due;
    }

    /// <summary>The earliest instant something pending could become due, or null when nothing is
    /// pending or everything pending waits on a completion (which wakes the loop by itself).</summary>
    public DateTimeOffset? NextWake(Exchange x, DateTimeOffset now, IReadOnlyDictionary<string, DateTimeOffset> lastStartByParticipant, IReadOnlySet<string> inFlightInRoom)
    {
        if (x.Status != ExchangeStatus.Open) return null;
        DateTimeOffset? wake = null;
        foreach (var (id, pending) in x.Pending)
        {
            if (inFlightInRoom.Contains(id)) continue;
            var at = pending.LastTriggerAt + _limits.Debounce;
            if (lastStartByParticipant.TryGetValue(id, out var last) && last + _limits.MinSpacing > at) at = last + _limits.MinSpacing;
            if (wake is null || at < wake) wake = at;
        }
        return wake;
    }

    public static void Started(Exchange x, SpawnRequest request)
    {
        x.Pending.Remove(request.ParticipantId);
        x.InFlight.Add(request.ParticipantId);
        x.TurnsStarted++;
    }

    /// <summary>A spawn ended, however it ended. Returns the conclusion note when this was the last
    /// thing the exchange was waiting on; null otherwise (including for a closed exchange).</summary>
    public static string? Finished(Exchange x, string participantId)
    {
        x.InFlight.Remove(participantId);
        if (x.Status != ExchangeStatus.Open || x.Pending.Count > 0 || x.InFlight.Count > 0) return null;
        x.Status = ExchangeStatus.Concluded;
        return $"Exchange concluded: {x.TurnsStarted} of {x.Budget} turns used.";
    }

    public static string Stop(Exchange x)
    {
        x.Status = ExchangeStatus.Stopped;
        x.Pending.Clear();
        return $"Exchange stopped by the owner: {x.TurnsStarted} of {x.Budget} turns used.";
    }
}
