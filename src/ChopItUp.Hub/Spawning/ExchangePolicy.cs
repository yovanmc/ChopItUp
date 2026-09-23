using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Skills;
using ChopItUp.Hub.Skills;

namespace ChopItUp.Hub.Spawning;

/// <summary>What the service found when it looked the message's slash command up. The policy needs
/// these outcomes and no filesystem. Every arm except <c>None</c> and <c>Found</c> is a refusal: the
/// owner asked for an instruction the hub cannot hand over intact, and spawning without it spends
/// real model turns on the wrong ask.</summary>
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

/// <summary>What <see cref="ExchangePolicy.OnMessage"/> needs to know about the room's active run, if
/// any. Null when no run is active here: a parked or ended run gates nothing at this level. Those
/// refusals need <c>RunStore.Latest</c>, which this pure class never reads, so
/// <c>SpawnerService</c> decides them before the policy is consulted.</summary>
public sealed record RunContext(long RunId, string ConductorId, string CurrentPhase);

/// <summary>What a /continue found. The service maps each refusal to one note.</summary>
public enum ContinueOutcome { Continued, RunExchange, StillOpen, StillFinishing, NobodySpawnable }

/// <summary>The rules, and nothing but the rules. Only an owner message opens an exchange. An owner
/// prompt closes only the open exchanges it shares a mentioned participant with; the rest keep
/// running beside the new one. An owner reply joins the exchange it replies to instead. A leading
/// mention (see <see cref="Mentions.Leading"/>) is the only trigger, never one's own message, never
/// a row that is not spawnable. Eight turns by default (a turns: token in the leading run
/// overrides); whoever holds the last one is told so, and the addressee gets a synthesis turn when
/// another participant posted last. Debounce, one in flight per (participant, room), minimum
/// spacing per participant. Returns notes for the caller to post as the hub; never posts
/// itself.</summary>
public sealed class ExchangePolicy
{
    private readonly IReadOnlyDictionary<string, Participant> _roster;
    private readonly Mentions _mentions;
    private readonly SpawnLimits _limits;

    private readonly string _addressable;

    public ExchangePolicy(IReadOnlyList<Participant> roster, SpawnLimits limits)
    {
        _roster = roster.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _mentions = new Mentions(roster.Where(p => p.Kind != "system").Select(p => p.Id));
        _limits = limits;
        _addressable = string.Join(", ", roster.Where(IsSpawnable).Select(p => "@" + p.Id));
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
    /// every check supersedes all of them. <paramref name="joins"/> is the exchange that holds the
    /// message an owner post replies to, as the service resolved it (any status), or null when the
    /// post is not a reply or its target is in no exchange the service holds.</summary>
    public (Exchange? Opened, IReadOnlyList<string> Notes) OnRoomMessage(IReadOnlyList<Exchange> room, Exchange? target, Message message, DateTimeOffset now,
        bool acceptMentions = true, SkillResolution? skill = null, RunContext? run = null, bool startsRun = false, bool hasDirectory = false, Exchange? joins = null)
    {
        var notes = new List<string>();
        if (!_roster.TryGetValue(message.AuthorId, out var author) || author.Kind == "system") return (null, notes);

        // acceptMentions = false: the author is a spawn of an exchange that is closed, or (inside a run)
        // not the room's newest one; its post lands, its mentions do not.
        var leading = acceptMentions ? _mentions.Leading(message.Body) : Mentions.LeadingMentions.None;
        var mentioned = leading.Recipients
            .Where(id => id != message.AuthorId && _roster.TryGetValue(id, out var p) && IsSpawnable(p))
            .ToList();

        if (author.Kind != "human")
        {
            // A model, spawn row or app-backed window, never opens an exchange.
            if (target is not { Status: ExchangeStatus.Open })
            {
                if (mentioned.Count > 0) notes.Add(StrayMentionNote(author.Id, mentioned));
                return (null, notes);
            }
            target.MessageIds.Add(message.Id);
            // Only a spawnable author's post can buy the addressee a synthesis turn: an app-backed
            // window's stray post into an open exchange must never read as "someone else posted last"
            // to Finished.
            if (IsSpawnable(author)) target.LastModelPost = (message.AuthorId, message.Id);
            Accept(target, mentioned, message.Id, now, notes);
            return (null, notes);
        }

        // A post inside an active run that is not itself a valid run-start leaves everything untouched:
        // the service records it as a steer for the conductor's next trigger set.
        if (run is not null) return (null, notes);

        // A reply joins the exchange its target belongs to. A skill invocation or a run-start is
        // always a new prompt, and says so when it was a reply.
        if (message.ReplyToId is { } replyTo)
        {
            if (skill is SkillResolution.Found invoked)
                notes.Add($"A reply that invokes /{invoked.Skill.Name} does not join an exchange; it was handled as a new prompt.");
            else if (skill is null or SkillResolution.None && !startsRun)
            {
                if (joins is { Joinable: true })
                {
                    Join(joins, mentioned, message.Id, now, notes);
                    return (null, notes);
                }
                if (mentioned.Count > 0)
                    notes.Add($"Reply to #{replyTo}: that message is in no exchange this hub still holds, so this post was handled as a new prompt.");
            }
        }

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

        // A leading word that matched nobody is noted once per word, whatever else this post did or
        // refused; the mentioned recipients above still act.
        NoteUnknownLeadingWords(leading, notes);

        // With no leading recipient at all (spawnable or not) and no unknown leading word, which
        // already got its own note, a spawnable id elsewhere in the body is a reference, not an
        // address; named here so both refusal shapes below can carry it.
        var referenced = leading.Recipients.Count == 0 && leading.Unknown.Count == 0
            ? _mentions.Find(message.Body).Where(id => id != message.AuthorId && _roster.TryGetValue(id, out var p) && IsSpawnable(p)).ToList()
            : new List<string>();
        var referenceSuffix = referenced.Count == 0 ? ""
            : referenced.Count == 1 ? $" @{referenced[0]} appears inside the text, so it was read as a reference."
            : $" {string.Join(", ", referenced.Select(r => "@" + r))} appear inside the text, so they were read as references.";

        // A run needs exactly one conductor; zero or many are both refused.
        if (startsRun && mentioned.Count != 1)
        {
            var name = (skill as SkillResolution.Found)?.Skill.Name
                ?? throw new ArgumentException("startsRun requires a Found skill.", nameof(skill));
            notes.Add(mentioned.Count == 0
                ? $"/{name} starts a run and needs exactly one conductor mentioned; none was.{referenceSuffix}"
                : $"/{name} starts a run and needs exactly one conductor mentioned; {mentioned.Count} were: {string.Join(", ", mentioned.Select(m => "@" + m))}.");
            return (null, notes);
        }

        if (mentioned.Count == 0)
        {
            // A skill with nobody to run it: say so, or the owner watches an invocation do nothing.
            if (skill is SkillResolution.Found idle)
                notes.Add($"/{idle.Skill.Name} needs a mention to run: nobody was addressed, so no exchange started.{referenceSuffix}");
            else if (referenced.Count > 0)
                notes.Add($"Nobody was addressed:{referenceSuffix} Start the message with @{referenced[0]} to send it.");
            return (null, notes);
        }

        // A run takes the whole room: nothing started before it keeps spawning beside the conductor.
        if (startsRun)
            foreach (var x in open.Where(x => x.Status == ExchangeStatus.Open)) Supersede(x);

        // Read only now that every early return above has passed: a turns: N on a message that opens
        // nothing (no skill, nobody mentioned, a refused run-start) is prose the hub never remarks on.
        var budget = TurnsOrDefault(leading, notes);

        var found = skill as SkillResolution.Found;
        var next = new Exchange
        {
            RoomId = message.RoomId, RootMessageId = message.Id, Budget = budget,
            Skill = found?.Skill, Joinable = !startsRun, Addressee = startsRun ? null : mentioned[0],
        };
        next.MessageIds.Add(message.Id);
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

    /// <summary>The running spawn finishes (its completion lands on this object, which is not open,
    /// so it cannot conclude or spawn); everything queued is dropped.</summary>
    internal static void Supersede(Exchange x)
    {
        x.Status = ExchangeStatus.Superseded;
        DropQueuedSynthesis(x);
        x.Pending.Clear();
    }

    /// <summary>A synthesis still pending is being dropped with the rest of the queue; if it had
    /// grown the budget, that turn goes back, so the stop note and /continue count only real turns.
    /// <see cref="Exchange.TurnsCommitted"/> goes back with it: <see cref="Finished"/> increments
    /// both together when it grows the budget, so undoing only one would leave TurnsCommitted greater
    /// than Budget on the wire.</summary>
    private static void DropQueuedSynthesis(Exchange x)
    {
        if (!x.SynthesisGrewBudget || !x.Pending.Values.Any(p => p.Reason == SpawnReason.Synthesis)) return;
        x.Budget--;
        x.TurnsCommitted--;
        x.SynthesisGrewBudget = false;
    }

    /// <summary>A non-human post led with a mention of a spawnable participant, but there is no open
    /// exchange for that mention to join (only a human post opens one). Named so nothing here
    /// hardcodes which participant it happened to be.</summary>
    private static string StrayMentionNote(string authorId, IReadOnlyList<string> mentioned) =>
        $"@{authorId} mentioned {string.Join(", ", mentioned.Select(m => "@" + m))}, but no exchange is open for it to join and only a human post opens one; nothing was spawned.";

    /// <summary>A leading word that matched nobody is noted once per word, in
    /// <see cref="OnRoomMessage"/> and in <see cref="Continue"/> alike.</summary>
    private void NoteUnknownLeadingWords(Mentions.LeadingMentions leading, List<string> notes)
    {
        foreach (var word in leading.Unknown)
            notes.Add(word.Equals("hub", StringComparison.OrdinalIgnoreCase)
                ? "The hub cannot be addressed; it only posts notes."
                : $"No participant named @{word}. Address one of: {_addressable}.");
    }

    /// <summary>The leading run's own turns token if valid, else the hub's default, noting the refusal
    /// once when the token was out of range.</summary>
    private int TurnsOrDefault(Mentions.LeadingMentions leading, List<string> notes)
    {
        if (leading.Turns == TurnsToken.OutOfRange)
            notes.Add($"turns: must be a whole number from 1 to {ExchangeCommands.MaxTurns}; the default {_limits.Budget} applies.");
        return leading.Turns == TurnsToken.Valid ? leading.TurnsValue : _limits.Budget;
    }

    /// <summary>The wire's <c>continuable</c> field, used by both an exchange's own view and the
    /// snapshot's top level. <paramref name="runBlocks"/> is the service's own read of whether the
    /// room's run would resume on a /continue: active, or parked (a parked run resumes on any human
    /// post, /continue included).</summary>
    public static bool Continuable(Exchange x, bool runBlocks) =>
        x.Joinable && x.Status != ExchangeStatus.Open && x.InFlight.Count == 0 && !runBlocks;

    /// <summary>An owner reply lands in <paramref name="x"/> and nothing is superseded. Open: its
    /// mentions are accepted against the exchange's own remaining budget. Closed with nothing in flight:
    /// it reopens with its turns and skill kept, but only if a mention was actually accepted; otherwise
    /// it stays exactly as it was. Closed with a spawn still running: nothing is accepted and a note names
    /// the spawn. With no mention the reply is only recorded as a member.</summary>
    private static void Join(Exchange x, IReadOnlyList<string> mentioned, long messageId, DateTimeOffset now, List<string> notes)
    {
        x.MessageIds.Add(messageId);
        if (mentioned.Count == 0) return;
        if (x.Status == ExchangeStatus.Open)
        {
            Accept(x, mentioned, messageId, now, notes);
            return;
        }
        if (x.InFlight.Count > 0)
        {
            notes.Add($"Exchange #{x.RootMessageId} is still finishing {string.Join(", ", x.InFlight.Order(StringComparer.Ordinal).Select(id => "@" + id))}; reply again once it has.");
            return;
        }
        var (status, cause, committed) = (x.Status, x.StopCause, x.TurnsCommitted);
        x.Status = ExchangeStatus.Open;
        x.StopCause = null;
        x.TurnsCommitted = x.TurnsStarted;   // a stop or supersede dropped queued turns that never ran; only launched turns stay spent
        Accept(x, mentioned, messageId, now, notes);
        if (x.Pending.Count == 0) { (x.Status, x.StopCause, x.TurnsCommitted) = (status, cause, committed); return; }
        // A new leg: the addressee may owe a fresh wrap-up, and what was refused before has been replayed by the reply's own words.
        x.SynthesisUsed = false;
        x.LastModelPost = null;
        x.Refused.Clear();
    }

    /// <paramref name="refusedAt"/>: for a <see cref="SpawnReason.Continuation"/> call only, the
    /// original message that refused each replayed id, carried into the fresh
    /// <see cref="PendingSpawn.RefusedAt"/> and prepended to its <see cref="PendingSpawn.TriggerIds"/>,
    /// so a hand-off /continue could not fit this time keeps citing the refusal that actually happened
    /// to it, never this /continue message's own id.
    private static void Accept(Exchange x, IReadOnlyList<string> mentioned, long messageId, DateTimeOffset now, List<string> notes,
        SpawnReason reason = SpawnReason.Mention, IReadOnlyDictionary<string, long>? refusedAt = null)
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
            if (x.TurnsCommitted >= x.Budget)
            {
                refused.Add(id);
                // A /continue's own overflow is re-recorded by the caller, which still has each id's
                // original refusing message; recording it here would overwrite that with this
                // /continue message's own id.
                if (reason != SpawnReason.Continuation) x.Refused.TryAdd(id, messageId);
                continue;
            }
            var refusingId = refusedAt is not null && refusedAt.TryGetValue(id, out var r) ? r : (long?)null;
            var fresh = new PendingSpawn { LastTriggerAt = now, Reason = reason, RefusedAt = refusingId };
            if (refusingId is { } ra) fresh.TriggerIds.Add(ra);
            fresh.TriggerIds.Add(messageId);
            x.Pending[id] = fresh;
            x.Participants.Add(id);
            x.TurnsCommitted++;
        }
        if (refused.Count > 0)
            notes.Add($"Budget of {x.Budget} turns is used up for the exchange started at #{x.RootMessageId}; not spawning {string.Join(", ", refused.Select(r => "@" + r))}. An owner message that mentions one of them starts a fresh exchange; once it has concluded, /continue extends it.");
    }

    /// <summary>Every id a conductor's post @-mentions that is spawnable. Unlike the mention set
    /// <see cref="OnMessage"/> builds for itself, a self-mention is kept, because
    /// <see cref="RefuseConductorPost"/>'s rule ("mentions the conductor itself") needs to see it to
    /// refuse it. The mentions read are leading ones, via <see cref="Mentions.Leading"/>.</summary>
    public IReadOnlyList<string> MentionedSpawnable(Message message) =>
        _mentions.Leading(message.Body).Recipients.Where(id => _roster.TryGetValue(id, out var p) && IsSpawnable(p)).ToList();

    /// <summary>The whole-body reader, kept for target selection only (which open exchange an
    /// app-backed window's post lands in, <c>SpawnerService.AppBackedTarget</c>), never for dispatch,
    /// which is <see cref="MentionedSpawnable"/>'s leading-only reading.</summary>
    public IReadOnlyList<string> ReferencedSpawnable(Message message) =>
        _mentions.Find(message.Body).Where(id => _roster.TryGetValue(id, out var p) && IsSpawnable(p)).ToList();

    /// <summary>The class rules a conductor's post inside its run must pass, pure and side-effect
    /// free: null means valid, otherwise names which rule failed, in order.
    /// <paramref name="mentioned"/> is <see cref="MentionedSpawnable"/>'s output (self kept).
    /// <paramref name="artifactAuthor"/> resolves a normalized path to who last touched it, or null if
    /// never recorded; <paramref name="artifactExists"/> answers whether the path is present in the
    /// room's directory tree. Either one satisfies "recorded or in the room tree". Those two
    /// functions are the service's impure edges; this class never touches a database or a
    /// filesystem itself.</summary>
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

    /// <summary>Every roster row a refused build or critique post could have mentioned to satisfy the
    /// rule it just failed, in roster order, or a line telling the hub owner to class one when nothing
    /// qualifies, never a bare "none qualified" that gives no next step.</summary>
    private string QualifyingRows(Func<Participant, bool> qualifies)
    {
        var ids = _roster.Values.Where(qualifies).Select(p => p.Id).ToList();
        return ids.Count == 0 ? "none is classed; set one with --set-classes" : string.Join(", ", ids.Select(id => "@" + id));
    }

    /// <summary>The conductor's post passed every class rule and asks for work. Same acceptance path
    /// as a fresh owner-started exchange (<see cref="Accept"/> seeds the mentioned rows as pending
    /// against the ordinary turn budget), but rooted at the conductor's own post and carrying no
    /// <see cref="Exchange.Skill"/>: workers see the run state and the conductor's own words, not the
    /// raw skill fence.</summary>
    public (Exchange Next, IReadOnlyList<string> Notes) OpenForWorkers(string roomId, long rootMessageId, IReadOnlyList<string> mentioned, DateTimeOffset now)
    {
        var notes = new List<string>();
        var x = new Exchange { RoomId = roomId, RootMessageId = rootMessageId, Budget = _limits.Budget };
        Accept(x, mentioned, rootMessageId, now, notes);
        return (x, notes);
    }

    /// <summary>The hub re-spawning its run's conductor. No message roots this, so
    /// <see cref="OnMessage"/>'s human-only rule is untouched. The conductor is the sole pending
    /// entry, budgeted for exactly the one turn it is being asked for; every id in
    /// <paramref name="triggerIds"/> is queued as its trigger. Sets <see cref="Exchange.Skill"/>: the
    /// skill is never posted into the room, only rendered into the prompt, so omitting it here would
    /// leave every re-spawn after the first exchange with no instruction at all.</summary>
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
    /// whole in-flight set, across exchanges: a superseded exchange's spawn still counts.
    /// <paramref name="exclusive"/> (a directory room): at most one spawn in the room at a time, so
    /// nothing is due while anything is in flight, and only the first pending spawn launches per
    /// pass; the completion wakes the loop for the next.
    /// <paramref name="exclusiveOver"/> narrows what exclusivity waits on: the exchange's own
    /// in-flight set when it has a worktree; the room's whole set otherwise.</summary>
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
            // A synthesis spawn is always the exchange's last. Budget - TurnsCommitted can read
            // nonzero here (a free turn was left when it was queued), which would wrongly tell it
            // another turn follows.
            var remainingAfter = pending.Reason == SpawnReason.Synthesis ? 0 : x.Budget - x.TurnsCommitted;
            due.Add(new SpawnRequest(x.RoomId, id, pending.TriggerIds.ToList(), x.RootMessageId, x.TurnsStarted + due.Count + 1, remainingAfter, pending.Reason, pending.RefusedAt));
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
        if (request.Reason == SpawnReason.Synthesis) x.SynthesisGrewBudget = false;
    }

    /// <summary>A spawn ended, however it ended. <c>Note</c> is the conclusion note, or the synthesis
    /// note when this exchange still owes the addressee a wrap-up, or null when nothing changed;
    /// <c>Concluded</c> says which. The synthesis turn is queued as an ordinary pending spawn
    /// (reason Synthesis, triggered by the last model post, debounced like any other), takes a free turn
    /// when one is left and adds one to the budget otherwise, and is marked at once so it fires at most
    /// once per leg. <paramref name="posted"/>: whether the spawn that just ended posted anything; an
    /// addressee that ended silent is not retried as a synthesis.</summary>
    public static (string? Note, bool Concluded) Finished(Exchange x, string participantId, DateTimeOffset now, bool posted = true)
    {
        x.InFlight.Remove(participantId);
        if (x.Status != ExchangeStatus.Open || x.Pending.Count > 0 || x.InFlight.Count > 0) return (null, false);
        if (x.Addressee is { } a && !x.SynthesisUsed && x.LastModelPost is { } last && last.AuthorId != a && !(participantId == a && !posted))
        {
            var synthesis = new PendingSpawn { LastTriggerAt = now, Reason = SpawnReason.Synthesis };
            synthesis.TriggerIds.Add(last.MessageId);
            x.Pending[a] = synthesis;
            x.Participants.Add(a);
            if (x.TurnsCommitted >= x.Budget) { x.Budget++; x.SynthesisGrewBudget = true; }
            x.TurnsCommitted++;
            x.SynthesisUsed = true;
            return ($"Exchange started at #{x.RootMessageId}: the hand-offs ended with @{last.AuthorId}'s post; queuing @{a}'s synthesis turn.", false);
        }
        x.Status = ExchangeStatus.Concluded;
        return (x.SynthesisUsed
            ? $"Exchange concluded: {x.TurnsStarted} of {x.Budget} turns used; the last was @{x.Addressee}'s synthesis."
            : $"Exchange concluded: {x.TurnsStarted} of {x.Budget} turns used.", true);
    }

    /// <summary>Handles /continue on <paramref name="x"/>. Adds the message's turns token (else the
    /// default) to the budget, reopens, and queues: the message's own spawnable leading mentions if
    /// any, else the hand-offs the budget refused (each triggered by the message that made it and by
    /// this one), else the addressee. A message that named someone but nobody spawnable queues
    /// nothing. Pure: the service resolved which exchange this is and posts the notes. The
    /// unknown-word notes are read before the RunExchange/StillOpen/StillFinishing guards, so a
    /// /continue naming a typo still draws its note even when the state below refuses it outright; a
    /// refused /continue never becomes a member of the exchange (it did nothing to it).</summary>
    public (ContinueOutcome Outcome, IReadOnlyList<string> Notes) Continue(Exchange x, Message message, DateTimeOffset now)
    {
        var notes = new List<string>();
        var leading = _mentions.Leading(message.Body);
        NoteUnknownLeadingWords(leading, notes);

        if (!x.Joinable) { notes.Add($"Exchange started at #{x.RootMessageId} belongs to a run and cannot be continued."); return (ContinueOutcome.RunExchange, notes); }
        if (x.Status == ExchangeStatus.Open) { notes.Add($"Exchange started at #{x.RootMessageId} is still open with {Math.Max(0, x.Budget - x.TurnsCommitted)} turn(s) left; /continue once it has concluded."); return (ContinueOutcome.StillOpen, notes); }
        if (x.InFlight.Count > 0) { notes.Add($"Exchange started at #{x.RootMessageId} is still finishing {string.Join(", ", x.InFlight.Order(StringComparer.Ordinal).Select(id => "@" + id))}; /continue again once it has."); return (ContinueOutcome.StillFinishing, notes); }

        var mentioned = leading.Recipients
            .Where(id => id != message.AuthorId && _roster.TryGetValue(id, out var p) && IsSpawnable(p)).ToList();
        if (mentioned.Count == 0 && (leading.Recipients.Count > 0 || leading.Unknown.Count > 0))
        {
            notes.Add("/continue named nobody the hub can spawn; nothing was queued.");
            return (ContinueOutcome.NobodySpawnable, notes);
        }
        var extra = TurnsOrDefault(leading, notes);

        var replayed = mentioned.Count == 0 ? x.Refused.ToList() : [];
        List<string> queue = mentioned.Count > 0 ? mentioned
            : replayed.Count > 0 ? replayed.Select(kv => kv.Key).ToList()
            : x.Addressee is { } a ? [a] : [];
        System.Diagnostics.Debug.Assert(queue.Count > 0, "a joinable exchange always has an addressee");

        x.Budget += extra;
        x.Status = ExchangeStatus.Open;
        x.StopCause = null;
        x.TurnsCommitted = x.TurnsStarted;
        x.SynthesisUsed = false;
        x.SynthesisGrewBudget = false;
        x.LastModelPost = null;
        x.Refused.Clear();
        x.MessageIds.Add(message.Id);
        var refusedAt = replayed.Count > 0 ? replayed.ToDictionary(kv => kv.Key, kv => kv.Value) : null;
        Accept(x, queue, message.Id, now, notes, SpawnReason.Continuation, refusedAt);
        // Whatever this leg still could not fit keeps the original refusing id: Accept skips
        // recording refusals for a Continuation, so nothing here overwrites it with this /continue
        // message's own id.
        foreach (var (id, refusingId) in replayed)
            if (!x.Pending.ContainsKey(id)) x.Refused.TryAdd(id, refusingId);
        notes.Add($"Exchange started at #{x.RootMessageId} continued: {extra} more turn(s), {x.Budget} in all; queued {string.Join(", ", x.Pending.Keys.Select(id => "@" + id))}.");
        return (ContinueOutcome.Continued, notes);
    }

    /// <summary><paramref name="cause"/> is never defaulted: every call site must say who stopped the
    /// exchange. <see cref="ExchangeStopCause.Owner"/> keeps the original text;
    /// <see cref="ExchangeStopCause.Run"/> is the same shape without attributing the stop to the
    /// owner, since the run itself parked or ended with this exchange still open.</summary>
    public static string Stop(Exchange x, ExchangeStopCause cause)
    {
        x.Status = ExchangeStatus.Stopped;
        x.StopCause = cause;
        DropQueuedSynthesis(x);
        x.Pending.Clear();
        return cause == ExchangeStopCause.Owner
            ? $"Exchange stopped by the owner: {x.TurnsStarted} of {x.Budget} turns used."
            : $"Exchange stopped by the run: {x.TurnsStarted} of {x.Budget} turns used.";
    }
}
