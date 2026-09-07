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
    /// <paramref name="current"/> itself unless an owner message opened a new one.
    /// <paramref name="run"/>, <paramref name="startsRun"/>, <paramref name="hasDirectory"/> and
    /// <paramref name="artifactAuthor"/> are row 19 (task 4): every existing call site compiles
    /// unchanged because all four default. <paramref name="artifactAuthor"/> is unused before task 8
    /// (the model branch's phase-tag rules) - accepted here now so that branch's signature never has
    /// to change again.</summary>
    public (Exchange? Next, IReadOnlyList<string> Notes) OnMessage(Exchange? current, Message message, DateTimeOffset now, bool acceptMentions = true, SkillResolution? skill = null,
        RunContext? run = null, bool startsRun = false, bool hasDirectory = false, Func<string, string?>? artifactAuthor = null)
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
            // Row 19, task 4 (pass 2's F-9), step 3 of the ordered human branch: a post inside an
            // active run that is not itself a valid run-start (SpawnerService guarantees startsRun
            // is never true while run is not null - its own step 2, an impure RunStore check, already
            // refused that combination before this method was ever called) leaves EVERYTHING
            // untouched - no supersede, no skill switch, nothing. AC5: the service records it as a
            // steer for the conductor's next trigger set (task 6); this pure method only has to not
            // get in the way.
            if (run is not null) return (current, notes);

            if (current is { Status: ExchangeStatus.Open } && run is null)
            {
                // D5: the running spawn finishes (its completion lands on this object, which is no
                // longer open, so it cannot conclude or spawn); everything queued is dropped.
                // `run is null` is always true here (the early return above already caught the other
                // case) - a second line of defence, not the first (task 6's note).
                current.Status = ExchangeStatus.Superseded;
                current.Pending.Clear();
            }

            // Step 4: a run-start invocation needs a directory to bind the run to (AC2).
            if (startsRun && !hasDirectory)
            {
                var name = (skill as SkillResolution.Found)?.Skill.Name
                    ?? throw new ArgumentException("startsRun requires a Found skill.", nameof(skill));
                notes.Add($"/{name} starts a run, which needs a room bound to a directory; this room has none.");
                return (current, notes);
            }

            // Step 5: a skill the hub cannot hand over intact spends nothing: the owner asked for an
            // instruction, and spawning without it would burn real model calls on the wrong ask
            // (D-c). The supersede above still stands - the owner spoke (M5-D5).
            switch (skill)
            {
                case SkillResolution.Unknown u:
                    notes.Add(u.Known.Count == 0
                        ? $"No skill named '/{u.Name}'; this hub has no skills installed. Import one with --import-skill."
                        : $"No skill named '/{u.Name}'. Installed: {string.Join(", ", u.Known.Select(k => "/" + k))}.");
                    return (current, notes);
                case SkillResolution.Tampered t:
                    notes.Add($"Skill /{t.Name} does not match what was imported; nothing was spawned. Re-import it with --import-skill before using it.");
                    return (current, notes);
                case SkillResolution.Unavailable a:
                    notes.Add($"Could not read skill /{a.Name}: {a.Reason}. Nothing was spawned.");
                    return (current, notes);
            }

            // Step 6: a run needs exactly one conductor - zero or many are both refused (AC2).
            if (startsRun && mentioned.Count != 1)
            {
                var name = (skill as SkillResolution.Found)?.Skill.Name
                    ?? throw new ArgumentException("startsRun requires a Found skill.", nameof(skill));
                notes.Add(mentioned.Count == 0
                    ? $"/{name} starts a run and needs exactly one conductor mentioned; none was."
                    : $"/{name} starts a run and needs exactly one conductor mentioned; {mentioned.Count} were: {string.Join(", ", mentioned.Select(m => "@" + m))}.");
                return (current, notes);
            }

            // Step 7: otherwise, the pre-row-19 behaviour - unaffected whether or not this is a
            // run-start invocation, since a run-start invocation that reached here already has a
            // directory and exactly one mention.
            if (mentioned.Count == 0)
            {
                // A skill with nobody to run it: say so, or the owner watches an invocation do
                // nothing at all and cannot tell it from a hub that ignored them.
                if (skill is SkillResolution.Found idle)
                    notes.Add($"/{idle.Skill.Name} needs a mention to run: nobody was addressed, so no exchange started.");
                return (current, notes);
            }
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
            return "phase build needs a mention of a plumbing- or visible-class row";

        if (tag.Kind == "critique")
        {
            var artifact = PhaseTag.Artifact(message.Body);
            if (artifact is null) return "a critique needs an artifact: line";
            var author = artifactAuthor(artifact);
            if (author is null && !artifactExists(artifact))
                return $"artifact '{artifact}' is neither recorded nor in the room tree";
            if (!mentioned.Any(id => _roster.TryGetValue(id, out var p) && ParticipantClasses.Has(p, ParticipantClasses.Judge) && id != author))
                return "a critique needs a judge mentioned other than the artifact's recorded author";
        }

        return null;
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
        x.TurnsCommitted = 1;
        return x;
    }

    /// <summary>Which pending spawns may launch now. <paramref name="inFlightInRoom"/> is the room's
    /// whole in-flight set, across exchanges — a superseded exchange's spawn still counts.
    /// <paramref name="exclusive"/> (a directory room, M9 decision 5): at most one spawn in the room
    /// at a time — nothing is due while anything is in flight, and only the first pending spawn
    /// launches per pass; the completion wakes the loop for the next.</summary>
    public IReadOnlyList<SpawnRequest> Due(Exchange x, DateTimeOffset now, IReadOnlyDictionary<string, DateTimeOffset> lastStartByParticipant, IReadOnlySet<string> inFlightInRoom, bool exclusive = false)
    {
        if (x.Status != ExchangeStatus.Open) return [];
        if (exclusive && inFlightInRoom.Count > 0) return [];
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
    /// pending or everything pending waits on a completion (which wakes the loop by itself).</summary>
    public DateTimeOffset? NextWake(Exchange x, DateTimeOffset now, IReadOnlyDictionary<string, DateTimeOffset> lastStartByParticipant, IReadOnlySet<string> inFlightInRoom, bool exclusive = false)
    {
        if (x.Status != ExchangeStatus.Open) return null;
        if (exclusive && inFlightInRoom.Count > 0) return null;   // the completion wakes the loop
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
