using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Hub.Skills;

namespace ChopItUp.Hub.Spawning;

/// <summary>What the service found when it looked the message's slash command up. The policy needs
/// these outcomes and no filesystem. Every arm except <c>None</c> and <c>Found</c> is a refusal: the
/// owner asked for an instruction the hub cannot hand over intact, and spawning without it spends
/// real model turns on the wrong ask (D-c).</summary>
public abstract record SkillResolution
{
    public sealed record None : SkillResolution;
    public sealed record Found(ResolvedSkill Skill, string Arguments) : SkillResolution;
    public sealed record Unknown(string Name, IReadOnlyList<string> Known) : SkillResolution;
    public sealed record Tampered(string Name) : SkillResolution;

    /// <summary>The store could not be read at all (disk, permissions). Distinct from Unknown: the
    /// skill may well exist, and telling the owner "no such skill" would be a lie.</summary>
    public sealed record Unavailable(string Name, string Reason) : SkillResolution;

    public static readonly SkillResolution Nothing = new None();
}

/// <summary>What <see cref="ExchangePolicy.OnMessage"/> needs to know about the room's ACTIVE run, if
/// any (row 19, task 4). Null when no run is active here - a parked or ended run gates nothing at
/// this level; those refusals need <c>RunStore.Latest</c>, which this pure class never reads, so
/// <c>SpawnerService</c> decides them (impure) before the policy is consulted at all (pass 2's
/// F-9).</summary>
public sealed record RunContext(long RunId, string ConductorId, string CurrentPhase);

/// <summary>The rules, and nothing but the rules. D2: only an owner message opens an exchange. An
/// owner prompt closes only the open exchanges it shares a mentioned participant with; the rest keep
/// running beside the new one. D8: a mention is the only trigger,
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

    /// <summary>The room's exchanges after this message, and the notes to post. Returns the exchange
    /// this message opened, or null. <paramref name="room"/> is every exchange the service still holds
    /// for the room, open or not; only open ones are considered. <paramref name="target"/> is where a
    /// model's mentions go and is ignored for a human post: the service picks it (a spawn's own
    /// exchange, else the open exchange involving a model it mentions, else the newest open one).
    /// A human prompt supersedes each open exchange it shares a mentioned participant with before any
    /// refusal is decided, since the owner spoke to that model either way; a run-start that passes
    /// every check supersedes all of them.</summary>
    public (Exchange? Opened, IReadOnlyList<string> Notes) OnRoomMessage(IReadOnlyList<Exchange> room, Exchange? target, Message message, DateTimeOffset now,
        bool acceptMentions = true, SkillResolution? skill = null, RunContext? run = null, bool startsRun = false, bool hasDirectory = false)
    {
        var notes = new List<string>();
        if (!_roster.TryGetValue(message.AuthorId, out var author) || author.Kind == "system") return (null, notes);

        // acceptMentions = false: the author is a spawn of an exchange that is closed, or (inside a run)
        // no longer the room's newest one; its post lands, its mentions do not.
        var mentioned = acceptMentions
            ? _mentions.Find(message.Body)
                .Where(id => id != message.AuthorId && _roster.TryGetValue(id, out var p) && IsSpawnable(p))
                .ToList()
            : new List<string>();

        if (author.Kind != "human")
        {
            // A model, spawn row or app-backed window, never opens an exchange.
            if (target is not { Status: ExchangeStatus.Open }) return (null, notes);
            Accept(target, mentioned, message.Id, now, notes);
            return (null, notes);
        }

        // A post inside an active run that is not itself a valid run-start leaves everything untouched:
        // the service records it as a steer for the conductor's next trigger set.
        if (run is not null) return (null, notes);

        var open = room.Where(x => x.Status == ExchangeStatus.Open).ToList();
        foreach (var x in open.Where(x => mentioned.Any(x.Participants.Contains))) Supersede(x);

        // A run-start invocation needs a directory to bind the run to.
        if (startsRun && !hasDirectory)
        {
            var name = (skill as SkillResolution.Found)?.Skill.Name
                ?? throw new ArgumentException("startsRun requires a Found skill.", nameof(skill));
            notes.Add($"/{name} starts a run, which needs a room bound to a directory; this room has none.");
            return (null, notes);
        }

        // A skill the hub cannot hand over intact spends nothing. Any overlap supersede above
        // still stands.
        switch (skill)
        {
            case SkillResolution.Unknown u:
                notes.Add(u.Known.Count == 0
                    ? $"No skill named '/{u.Name}'; this hub has no skills installed. Import one with --import-skill."
                    : $"No skill named '/{u.Name}'. Installed: {string.Join(", ", u.Known.Select(k => "/" + k))}.");
                return (null, notes);
            case SkillResolution.Tampered t:
                notes.Add($"Skill /{t.Name} does not match what was imported; nothing was spawned. Re-import it with --import-skill before using it.");
                return (null, notes);
            case SkillResolution.Unavailable a:
                notes.Add($"Could not read skill /{a.Name}: {a.Reason}. Nothing was spawned.");
                return (null, notes);
        }

        // A run needs exactly one conductor; zero or many are both refused.
        if (startsRun && mentioned.Count != 1)
        {
            var name = (skill as SkillResolution.Found)?.Skill.Name
                ?? throw new ArgumentException("startsRun requires a Found skill.", nameof(skill));
            notes.Add(mentioned.Count == 0
                ? $"/{name} starts a run and needs exactly one conductor mentioned; none was."
                : $"/{name} starts a run and needs exactly one conductor mentioned; {mentioned.Count} were: {string.Join(", ", mentioned.Select(m => "@" + m))}.");
            return (null, notes);
        }

        if (mentioned.Count == 0)
        {
            // A skill with nobody to run it: say so, or the owner watches an invocation do nothing.
            if (skill is SkillResolution.Found idle)
                notes.Add($"/{idle.Skill.Name} needs a mention to run: nobody was addressed, so no exchange started.");
            return (null, notes);
        }

        // A run takes the whole room: nothing started before it keeps spawning beside the conductor.
        if (startsRun)
            foreach (var x in open.Where(x => x.Status == ExchangeStatus.Open)) Supersede(x);

        var found = skill as SkillResolution.Found;
        var next = new Exchange
        {
            RoomId = message.RoomId, RootMessageId = message.Id, Budget = _limits.Budget,
            Skill = found?.Skill,
        };
        if (found is not null)
            notes.Add($"Skill /{found.Skill.Name} is in force for this exchange; every turn of it is rendered the same instruction."
                + (found.Skill.Truncated ? $" Its text was cut to {SkillStore.MaxSkillChars} characters." : ""));
        Accept(next, mentioned, message.Id, now, notes);
        return (next, notes);
    }

    /// <summary>The one-exchange view of <see cref="OnRoomMessage"/>: <paramref name="current"/> is both
    /// the room's only exchange and a model post's target. Returns <paramref name="current"/> itself
    /// unless the message opened a new exchange.</summary>
    public (Exchange? Next, IReadOnlyList<string> Notes) OnMessage(Exchange? current, Message message, DateTimeOffset now, bool acceptMentions = true, SkillResolution? skill = null,
        RunContext? run = null, bool startsRun = false, bool hasDirectory = false)
    {
        var (opened, notes) = OnRoomMessage(current is null ? [] : [current], current, message, now, acceptMentions, skill, run, startsRun, hasDirectory);
        return (opened ?? current, notes);
    }

    /// <summary>The running spawn finishes (its completion lands on this object, which
    /// is no longer open, so it cannot conclude or spawn); everything queued is dropped.</summary>
    private static void Supersede(Exchange x)
    {
        x.Status = ExchangeStatus.Superseded;
        x.Pending.Clear();
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
            x.Participants.Add(id);
            x.TurnsCommitted++;
        }
        if (refused.Count > 0)
            notes.Add($"Budget of {x.Budget} turns is used up for the exchange started at #{x.RootMessageId}; not spawning {string.Join(", ", refused.Select(r => "@" + r))}. An owner message that mentions one of them starts a fresh exchange.");
    }

    /// <summary>Row 19, task 8: every id a conductor's post @-mentions that is spawnable - UNLIKE the
    /// mention set <see cref="OnMessage"/> builds for itself, a self-mention is deliberately kept
    /// rather than dropped, because <see cref="RefuseConductorPost"/>'s own rule ("mentions the
    /// conductor itself") needs to see it in order to refuse it - silently filtering it out here would
    /// make that rule unreachable.</summary>
    public IReadOnlyList<string> MentionedSpawnable(Message message) =>
        _mentions.Find(message.Body).Where(id => _roster.TryGetValue(id, out var p) && IsSpawnable(p)).ToList();

    /// <summary>Row 19, task 8 (D8/AC6): the class rules a conductor's post inside its run must pass,
    /// pure and side-effect free - null means valid, otherwise names which rule failed, in AC6's own
    /// order. <paramref name="mentioned"/> is <see cref="MentionedSpawnable"/>'s output (self kept).
    /// <paramref name="artifactAuthor"/> resolves a normalized path to who last touched it (task 1's
    /// RunStore), or null if never recorded; <paramref name="artifactExists"/> answers whether the
    /// path is present in the room's directory tree - either one satisfies "recorded or in the room
    /// tree" (P4). Never touches a database or a filesystem itself: those two functions are the
    /// service's impure edges, kept out of this pure class (D-b).</summary>
    public string? RefuseConductorPost(Message message, RunContext run, IReadOnlyList<string> mentioned,
        Func<string, string?> artifactAuthor, Func<string, bool> artifactExists)
    {
        if (!PhaseTag.TryParse(message.Body, out var tag))
            return "the first line must be a valid phase tag: 'phase: <kind>' or 'phase: <kind>/<name>'";
        if (mentioned.Contains(run.ConductorId))
            return "a conductor post cannot mention itself";
        if (tag!.Kind == "ping") return null;   // no mention required; every rule below is skipped

        if (mentioned.Count == 0)
            return $"phase {tag} needs a mention of who does the work";

        if (tag.Kind == "build" && !mentioned.Any(id => _roster.TryGetValue(id, out var p)
                && (ParticipantClasses.Has(p, ParticipantClasses.Plumbing) || ParticipantClasses.Has(p, ParticipantClasses.Visible))))
            return "phase build needs a mention of a plumbing- or visible-class row: " + QualifyingRows(p =>
                ParticipantClasses.Has(p, ParticipantClasses.Plumbing) || ParticipantClasses.Has(p, ParticipantClasses.Visible));

        if (tag.Kind == "critique")
        {
            var artifact = PhaseTag.Artifact(message.Body);
            if (artifact is null) return "a critique needs an artifact: line";
            var author = artifactAuthor(artifact);
            if (author is null && !artifactExists(artifact))
                return $"artifact '{artifact}' is neither recorded nor in the room tree";
            if (!mentioned.Any(id => _roster.TryGetValue(id, out var p) && ParticipantClasses.Has(p, ParticipantClasses.Judge) && id != author))
                return "a critique needs a judge mentioned other than the artifact's recorded author: " + QualifyingRows(p =>
                    ParticipantClasses.Has(p, ParticipantClasses.Judge) && p.Id != author);
        }

        return null;
    }

    /// <summary>Row 20, task 3 (AC4b, pass-2 B2): every roster row a refused build or critique post
    /// COULD have mentioned to satisfy the rule it just failed, in roster order, or a line telling the
    /// owner to class one when nothing qualifies — never a bare "none qualified" that gives no next
    /// step.</summary>
    private string QualifyingRows(Func<Participant, bool> qualifies)
    {
        var ids = _roster.Values.Where(qualifies).Select(p => p.Id).ToList();
        return ids.Count == 0 ? "none is classed; set one with --set-classes" : string.Join(", ", ids.Select(id => "@" + id));
    }

    /// <summary>Row 19, task 8 (AC4): the conductor's post passed every D8 rule and asks for work -
    /// same acceptance path as a fresh owner-started exchange (<see cref="Accept"/> seeds the
    /// mentioned rows as pending against the ordinary turn budget), but rooted at the conductor's own
    /// post rather than an owner's, and carrying no <see cref="Exchange.Skill"/>: workers see the run
    /// state (task 7) and the conductor's own words, not the raw skill fence.</summary>
    public (Exchange Next, IReadOnlyList<string> Notes) OpenForWorkers(string roomId, long rootMessageId, IReadOnlyList<string> mentioned, DateTimeOffset now)
    {
        var notes = new List<string>();
        var x = new Exchange { RoomId = roomId, RootMessageId = rootMessageId, Budget = _limits.Budget };
        Accept(x, mentioned, rootMessageId, now, notes);
        return (x, notes);
    }

    /// <summary>Row 19, task 5a: the hub re-spawning its run's conductor - no message roots this, so
    /// <see cref="OnMessage"/>'s human-only rule is untouched (P2). The conductor is the sole pending
    /// entry, budgeted for exactly the one turn it is being asked for; every id in
    /// <paramref name="triggerIds"/> is queued as its trigger. Sets <see cref="Exchange.Skill"/>
    /// (pass 1's M9): the skill is never posted into the room, only rendered into the prompt, so
    /// omitting it here would leave every re-spawn after the first exchange with no instruction at
    /// all.</summary>
    public static Exchange OpenForConductor(string roomId, string conductorId, long rootMessageId, IReadOnlyList<long> triggerIds, DateTimeOffset now, ResolvedSkill skill)
    {
        var x = new Exchange { RoomId = roomId, RootMessageId = rootMessageId, Budget = 1, Skill = skill };
        var pending = new PendingSpawn { LastTriggerAt = now };
        foreach (var id in triggerIds) pending.TriggerIds.Add(id);
        x.Pending[conductorId] = pending;
        x.Participants.Add(conductorId);
        x.TurnsCommitted = 1;
        return x;
    }

    /// <summary>Which pending spawns may launch now. <paramref name="inFlightInRoom"/> is the room's
    /// whole in-flight set, across exchanges — a superseded exchange's spawn still counts.
    /// <paramref name="exclusive"/> (a directory room, M9 decision 5): at most one spawn in the room
    /// at a time — nothing is due while anything is in flight, and only the first pending spawn
    /// launches per pass; the completion wakes the loop for the next.
    /// <paramref name="exclusiveOver"/> narrows what exclusivity waits on: the exchange's own
    /// in-flight set when it has a worktree (row 35); the room's whole set otherwise.</summary>
    public IReadOnlyList<SpawnRequest> Due(Exchange x, DateTimeOffset now, IReadOnlyDictionary<string, DateTimeOffset> lastStartByParticipant, IReadOnlySet<string> inFlightInRoom, bool exclusive = false, IReadOnlySet<string>? exclusiveOver = null)
    {
        if (x.Status != ExchangeStatus.Open) return [];
        if (exclusive && (exclusiveOver ?? inFlightInRoom).Count > 0) return [];
        var due = new List<SpawnRequest>();
        foreach (var (id, pending) in x.Pending)
        {
            if (inFlightInRoom.Contains(id)) continue;
            if (now - pending.LastTriggerAt < _limits.Debounce) continue;
            if (lastStartByParticipant.TryGetValue(id, out var last) && now - last < _limits.MinSpacing) continue;
            due.Add(new SpawnRequest(x.RoomId, id, pending.TriggerIds.ToList(), x.RootMessageId, x.TurnsStarted + due.Count + 1, x.Budget - x.TurnsCommitted));
            if (exclusive) break;
        }
        return due;
    }

    /// <summary>The earliest instant something pending could become due, or null when nothing is
    /// pending or everything pending waits on a completion (which wakes the loop by itself).
    /// <paramref name="exclusiveOver"/>: see <see cref="Due"/>.</summary>
    public DateTimeOffset? NextWake(Exchange x, DateTimeOffset now, IReadOnlyDictionary<string, DateTimeOffset> lastStartByParticipant, IReadOnlySet<string> inFlightInRoom, bool exclusive = false, IReadOnlySet<string>? exclusiveOver = null)
    {
        if (x.Status != ExchangeStatus.Open) return null;
        if (exclusive && (exclusiveOver ?? inFlightInRoom).Count > 0) return null;   // the completion wakes the loop
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

    /// <summary>Row 27: <paramref name="cause"/> is never defaulted - every call site must say who
    /// stopped the exchange (P7). <see cref="ExchangeStopCause.Owner"/> keeps the original text;
    /// <see cref="ExchangeStopCause.Run"/> is the same shape without attributing the stop to the
    /// owner, since the run itself parked or ended with this exchange still open.</summary>
    public static string Stop(Exchange x, ExchangeStopCause cause)
    {
        x.Status = ExchangeStatus.Stopped;
        x.StopCause = cause;
        x.Pending.Clear();
        return cause == ExchangeStopCause.Owner
            ? $"Exchange stopped by the owner: {x.TurnsStarted} of {x.Budget} turns used."
            : $"Exchange stopped by the run: {x.TurnsStarted} of {x.Budget} turns used.";
    }
}
