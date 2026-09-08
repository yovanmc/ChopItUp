using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ChopItUp.Core.Memory;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Skills;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Hosting;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Realtime;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Security;
using ChopItUp.Hub.Skills;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;

namespace ChopItUp.Hub.Spawning;

/// <summary>What the UI and the API see. <see cref="Status"/> is <c>idle</c>, <c>open</c>,
/// <c>concluded</c>, <c>superseded</c> or <c>stopped</c>.</summary>
public sealed record ExchangeSnapshot(
    string RoomId, string Status, long? RootMessageId, int Budget, int TurnsUsed, int TurnsCommitted, int Remaining,
    IReadOnlyList<string> InFlight, IReadOnlyList<string> Pending, long Seq = 0);

/// <summary>The spawner (M5). One loop, one thread of control: posts, completions, stop requests and
/// timer ticks are one FIFO channel, handled in order; after each batch the loop launches whatever
/// <see cref="ExchangePolicy.Due"/> says and arms a timer for the next moment anything could become
/// due. State is per room, in memory (plan decision 3). Notes go into the room as the hub
/// participant; the launch itself goes through <see cref="IProcessRunner"/> so tests never start a
/// CLI. Nothing here reads a token out to a log: the only places a token goes are the per-spawn
/// <c>mcp.json</c> and the Codex environment, and every quoted output is scrubbed first.</summary>
public sealed class SpawnerService : BackgroundService
{
    private abstract record Event;
    private sealed record PostedEvent(Message Message) : Event;
    private sealed record FinishedEvent(SpawnHandle Handle, ProcessResult Result, TrailReport? Trail) : Event;
    private sealed record StopEvent(string RoomId, TaskCompletionSource<ExchangeSnapshot?> Reply) : Event;
    private sealed record TickEvent : Event;

    /// <summary>What the trail did around one spawn in a directory room (M9 decision 6); null when the
    /// room has no directory.</summary>
    private sealed record TrailReport(CommitOutcome? Owner, CommitOutcome Agent, int Commands, bool HeadMoved);

    private sealed class SpawnHandle
    {
        public required SpawnRequest Request { get; init; }
        public required Exchange Exchange { get; init; }
        public required Participant Participant { get; init; }
        public required string SpawnId { get; init; }
        public required string WorkDir { get; init; }
        public required string Token { get; init; }
        public required CancellationTokenSource Cancel { get; init; }
        public string? Directory { get; init; }
        // Row 19, task 9c: captured AT LAUNCH (Launch reads _runs.Active once, before this handle
        // exists), never re-derived once the spawn's own task body or OnFinished runs - a run can
        // park or end while its spawn is still alive, and the two "did not reply in time" notes at
        // OnFinished must describe the SAME timeout the spawn was actually given.
        public required TimeSpan Timeout { get; init; }
        public Task Run { get; set; } = Task.CompletedTask;
        public bool Posted { get; set; }
    }

    public const string ChangedEvent = "ExchangeChanged";
    private const int NoteReplyChars = 4_000;
    private const int NoteStderrChars = 600;

    private readonly MessageStore _store;
    private readonly IReadOnlyList<Participant> _roster;
    private readonly MessageSignal _signal;
    private readonly TokenStore _tokens;
    private readonly IProcessRunner _runner;
    private readonly ChopItUp.Hub.Hosting.HubOptions _options;
    private readonly SpawnLimits _limits;
    private readonly IServer _server;
    private readonly IHubContext<RoomHub> _hub;
    private readonly CliLocator _locate;
    private readonly MemoryStore _memory;
    private readonly RoomTrails _trails;
    private readonly Participant _owner;
    private readonly ExchangePolicy _policy;
    private readonly SkillStore _skills;
    private readonly TimeProvider _clock;
    private readonly RunStore _runs;
    private readonly RunLimits _runLimits;
    private readonly RunPolicy _runPolicy;
    private readonly Channel<Event> _events = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, Exchange> _rooms = new(StringComparer.Ordinal);
    // Row 19, task 6: messages posted by a human inside an active run, waiting for the conductor's
    // next trigger set. Empty until task 6 populates it; DriveRun (task 5) already drains it whenever
    // an OpenConductor decision consumes it, so the two tasks never have to touch this line twice.
    private readonly Dictionary<string, List<long>> _steers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Room, string Participant), SpawnHandle> _inFlight = new();
    // Row 19, task 8 (pass 2's F-2): the two per-phase counters RunState reports, kept here because
    // they are cheap in-memory state with no durable meaning (Architecture, plan). Keyed by run id
    // only, not (run, phase) - EnterPhase resets BOTH to 0 on every accepted post, not only on a tag
    // change (F-22), so a stale count from an earlier phase can never leak into a later one.
    private readonly Dictionary<long, int> _refusalsThisPhase = new();
    private readonly Dictionary<long, int> _silencesThisPhase = new();
    // Row 19, task 9b (pass 2's F-7): the instant each active run was last known BUSY (exchange
    // open or something in flight), so ArmWake can arm a bounded stall wake at
    // "SpawnTimeout after that instant" rather than "SpawnTimeout after ArmWake happened to run" -
    // the latter would let an unrelated room's activity (which re-runs ArmWake for every room) push
    // a genuinely stalled run's deadline forward forever. Cleared whenever the run is next observed
    // busy, or is no longer active (park/end) - see ArmWake.
    private readonly Dictionary<long, DateTimeOffset> _lastRunActivity = new();
    private readonly Dictionary<string, DateTimeOffset> _lastStart = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExchangeSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedCli> _clis = new(StringComparer.Ordinal);
    private CancellationTokenSource? _wake;

    public SpawnerService(MessageStore store, IReadOnlyList<Participant> roster, MessageSignal signal, TokenStore tokens,
        IProcessRunner runner, ChopItUp.Hub.Hosting.HubOptions options, SpawnLimits limits, IServer server, IHubContext<RoomHub> hub,
        CliLocator cliLocator, MemoryStore memory, RoomTrails trails, ParticipantStore participants, SkillStore skills, TimeProvider clock,
        RunStore runs, RunLimits runLimits)
    {
        _store = store; _roster = roster; _signal = signal; _tokens = tokens; _runner = runner;
        _options = options; _limits = limits; _server = server; _hub = hub; _locate = cliLocator; _memory = memory;
        _trails = trails; _owner = roster.First(p => p.Id == participants.OwnerId());
        _policy = new ExchangePolicy(roster, limits);
        _skills = skills;
        _clock = clock;
        _runs = runs;
        _runLimits = runLimits;
        _runPolicy = new RunPolicy(runLimits);
    }

    public ExchangeSnapshot Snapshot(string roomId) => _snapshots.TryGetValue(roomId, out var s) ? s : Idle(roomId);

    private int _live;

    /// <summary>True while any spawn process of any room may be alive. Counted at the launch and the
    /// finish themselves, not read off <c>_snapshots</c> (which <c>Publish</c> writes after the launch —
    /// critique pass 2, P2-4). A spawn process exists only inside this window, so a memory decision
    /// refused while it is true can never have come from one (M10, plan decision 13).</summary>
    public bool AnySpawnInFlight => Volatile.Read(ref _live) > 0;

    /// <summary>Stops the room's open exchange: kills its in-flight spawns, drops its pending ones,
    /// posts the note. Returns the new snapshot, or null when the room had no open exchange.</summary>
    public async Task<ExchangeSnapshot?> StopAsync(string roomId)
    {
        var reply = new TaskCompletionSource<ExchangeSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_events.Writer.TryWrite(new StopEvent(roomId, reply))) return null;
        return await reply.Task;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Nothing in <data>\spawns\ can be live at start: every spawn belongs to a hub process, and
        // this is a new one. Each leftover holds a Claude mcp.json with a bearer token (a crash, a
        // forced stop) — swept before the first launch, like ChopDb.SweepPartialBackups.
        TryDeleteDir(Path.Combine(_options.DataDir, "spawns"));
        _signal.Posted += OnPosted;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _signal.Posted -= OnPosted;
        _events.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
        _wake?.Cancel();
        _wake?.Dispose();
        _wake = null;
        foreach (var handle in _inFlight.Values)
        {
            try { handle.Cancel.Cancel(); } catch (ObjectDisposedException) { }   // the hub is going down; do not leave CLIs running
        }
        // Wait for the tree kills to land (they run on the launch tasks) so a Ctrl+C on a dev hub does
        // not leave a CLI posting into a room after the hub is gone (critique pass 2, m5).
        try { await Task.WhenAll(_inFlight.Values.Select(h => h.Run)).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { Console.Error.WriteLine("spawner: some spawns did not stop within 10 s of shutdown"); }
    }

    private void OnPosted(Message message) => _events.Writer.TryWrite(new PostedEvent(message));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Decision 6's crash window: the hub may have died between the CLI exiting and the
            // after-spawn commit. A log line for the owner to look at, never a commit and never a note
            // — the next spawn's pre-commit sweeps it in as the owner, as documented.
            foreach (var room in _store.ListRooms(includeArchived: true))
            {
                if (room.Directory is null) continue;
                if (await _trails.For(room.Directory).IsDirtyAsync(stoppingToken))
                    Console.Error.WriteLine($"room {room.Id}: {room.Directory} has uncommitted changes at startup (a spawn may have ended without its commit); the next spawn commits them as the owner");
            }
            while (await _events.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_events.Reader.TryRead(out var ev)) Guarded(() => Handle(ev), ev.GetType().Name);
                Guarded(LaunchDue, nameof(LaunchDue));
                Guarded(ArmWake, nameof(ArmWake));
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The loop is the hub's only spawner; an exception escaping it would stop the whole
    /// host (BackgroundServiceExceptionBehavior.StopHost, the default — critique pass 1, B3). A
    /// SQLITE_BUSY on a note, a bad room, a launch that throws: logged, and the loop goes on.</summary>
    private static void Guarded(Action step, string what)
    {
        try { step(); }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"spawner: {what} failed: {e.GetType().Name}: {e.Message}"); }
    }

    private void Handle(Event ev)
    {
        switch (ev)
        {
            case PostedEvent p: OnMessage(p.Message); break;
            case FinishedEvent f: OnFinished(f.Handle, f.Result, f.Trail); break;
            case StopEvent s:
                ExchangeSnapshot? reply = null;
                try { reply = OnStop(s.RoomId); }
                finally { s.Reply.TrySetResult(reply); }   // a throw here must not hang the HTTP caller
                break;
            case TickEvent: OnTick(); break;
        }
    }

    /// <summary>Row 19, task 9b: the periodic wake ArmWake arms (the wall-clock deadline, or the
    /// bounded stall wake) fires as a bare <see cref="TickEvent"/> with no room of its own - unlike
    /// the steer branch's immediate, single-run <c>DriveRun(runRow, Tick)</c> call (task 6), this
    /// checks EVERY active run, because one shared timer can be the earliest deadline for any of
    /// them. <see cref="RunPolicy"/>'s rows 1/2 (checked first, for every event) are what actually
    /// makes this useful: a run that is still busy gets <see cref="RunDecision.Nothing"/> here
    /// unless it has separately spent a hard cap, in which case THIS is what parks it (pass 2's
    /// F-7 - without a Tick ever reaching a busy-but-capped run, row 19's stall park and AC8's
    /// wall-clock park while idle are both unreachable in production).</summary>
    private void OnTick()
    {
        foreach (var run in _runs.ListActive()) DriveRun(run, new RunEvent.Tick());
    }

    private void OnMessage(Message m)
    {
        _rooms.TryGetValue(m.RoomId, out var current);
        // A post from a spawn of an exchange that is no longer current (superseded by the owner):
        // the message stands, its mentions are ignored (A6). The handle is the only thing that knows
        // which exchange spawned the author.
        bool acceptMentions = true;
        if (_inFlight.TryGetValue((m.RoomId, m.AuthorId), out var handle))
        {
            handle.Posted = true;
            acceptMentions = ReferenceEquals(handle.Exchange, current);
        }
        // Row 19's clock seam (task 2b): OnMessage is the run-start site (task 4's _runs.Start reads
        // this same instant), so it goes through the injected clock; LaunchDue/ArmWake stay on the
        // real wall clock until a run path needs them too.
        var now = _clock.GetUtcNow();
        var skill = ResolveSkill(m);
        var startsRun = skill is SkillResolution.Found found && found.Skill.IsRun;
        var activeRun = _runs.Active(m.RoomId);
        var run = activeRun is not null ? new RunContext(activeRun.Id, activeRun.ConductorId, activeRun.Phase) : null;

        // Row 19, task 8 (AC4/AC6): the conductor's own post, while its exchange is still this room's
        // CURRENT one (acceptMentions - a later post from the same spawn, after it has already
        // rooted, is prose and falls through to the ordinary branches below, never refused - pass 1's
        // M1). Bypasses ExchangePolicy.OnMessage's ordinary model branch entirely: that method knows
        // nothing about phases or classes, and P7 keeps every run DECISION inside RunPolicy.
        if (activeRun is not null && acceptMentions && m.AuthorId == activeRun.ConductorId)
        {
            HandleConductorPost(activeRun, m, now);
            return;
        }

        // Row 19, task 13 (AC11, pass 2's F-9): step 1 of the ordered human branch, checked before
        // anything below can post a note or touch a run's status. Ends an active OR parked run
        // outright, through the SAME RunPolicy.Decide path every other transition takes (P7) - never
        // resuming it first (RunPolicy's rows 1/2 are skipped for StopRequested, so a hard-capped
        // park gets End, not Park - the state AC11 most needs this to work on). With no run in this
        // room, RunCommands.IsStop still matched, but there is nothing to stop, so this falls straight
        // through to ResolveSkill's ordinary result below - "/stop" was never anything but an
        // unresolvable skill name before this task (SkillImport refuses to ever install one under
        // that name), so "no run" behaves exactly as it did before.
        if (RunCommands.IsStop(m.Body) && _roster.FirstOrDefault(p => p.Id == m.AuthorId)?.Kind == "human")
        {
            var stoppable = activeRun ?? (_runs.Latest(m.RoomId) is { Status: RunStatus.Parked } parkedForStop ? parkedForStop : null);
            if (stoppable is not null)
            {
                DriveRun(stoppable, new RunEvent.StopRequested());
                return;
            }
        }

        // Row 19, task 4 (pass 2's F-9), steps 2-3 of the ordered human branch: these two refusals
        // need RunStore.Latest, which ExchangePolicy (pure) never reads - decided here, before the
        // policy is consulted at all, so a run-start invocation can never land on top of an active OR
        // a parked run (ux_runs_one_active_per_room only guards 'active'; a second Start against a
        // parked room would otherwise succeed and leave two run rows for one room).
        if (startsRun)
        {
            if (activeRun is not null)
            {
                PostNote(m.RoomId, $"A run is already active in this room (#{activeRun.Id}); /stop it first.");
                return;
            }
            var latest = _runs.Latest(m.RoomId);
            if (latest is { Status: RunStatus.Parked })
            {
                PostNote(m.RoomId, $"A run is parked in this room (#{latest.Id}); resume it with a message or /stop it before starting another.");
                return;
            }
        }

        // Row 19, task 9f (AC15), restored to P7 (orchestrator diff-review finding): a human post
        // that reaches here has no active run in this room (the conductor-post branch above only
        // fires when one exists, and startsRun's own checks just above already returned for an
        // active-or-parked room) and is not itself a run-start. If the room's most recent run is
        // PARKED, this is exactly AC15's resume - decided through RunPolicy.Decide like every other
        // transition, never here (P7: "nothing else in this row may hold run decisions"). The two
        // misfires the original bypass worked around are now fixed at their source instead of routed
        // around: RunPolicy.Decide's rows 1/2 only apply to a Status == Active run (a parked run
        // cannot trip a cap again), and RunStore.ActiveElapsed freezes while parked, so a soft park's
        // Elapsed no longer grows for as long as it sits parked (pass 2's F-4). CarryOut resumes
        // (folding parked_seconds) only when the decision is OpenConductor; a Refuse posts the line
        // and does nothing else.
        if (activeRun is null && !startsRun && _roster.FirstOrDefault(p => p.Id == m.AuthorId)?.Kind == "human")
        {
            var latestForResume = _runs.Latest(m.RoomId);
            if (latestForResume is { Status: RunStatus.Parked } parkedRun)
            {
                DriveRun(parkedRun, new RunEvent.HumanPosted(m.Id));
                return;
            }
        }

        var hasDirectory = _store.GetRoom(m.RoomId)?.Directory is not null;
        var (next, notes) = _policy.OnMessage(current, m, now, acceptMentions, skill, run, startsRun, hasDirectory);
        if (next is null) _rooms.Remove(m.RoomId); else _rooms[m.RoomId] = next;
        foreach (var note in notes) PostNote(m.RoomId, note);
        if (next is not null || current is not null) Publish(m.RoomId);

        if (startsRun && next is not null && !ReferenceEquals(next, current))
        {
            // The policy just built the conductor's first exchange (steps 4-7 all passed: a
            // directory, a resolved skill, exactly one mention). Persist the run itself.
            var conductorId = next.Pending.Keys.Single();
            var skillFound = (SkillResolution.Found)skill!;
            var runRow = _runs.Start(m.RoomId, conductorId, skillFound.Skill.Name, skillFound.Arguments, m.Id, now);
            _runs.CountExchange(runRow.Id);
            PostNote(m.RoomId, $"Run #{runRow.Id} started: @{conductorId} conducts; caps {_runLimits.Spawns} spawns, "
                + $"{_runLimits.WallClock.TotalHours:0}h active time, phase re-entry {_runLimits.PhaseEntries}.");
            return;
        }

        // Row 19, task 6 (AC5): a human post inside an active run that did not start a new one is a
        // steer. ExchangePolicy already left `current` untouched above (its own step 3); this is the
        // impure half - record it, tell the owner, and if the conductor is idle, wake it now.
        if (run is not null && _roster.FirstOrDefault(p => p.Id == m.AuthorId)?.Kind == "human")
        {
            var runRow = activeRun!;
            var pending = _steers.TryGetValue(m.RoomId, out var list) ? list : (_steers[m.RoomId] = new List<long>());
            pending.Add(m.Id);
            var state = AssembleRunState(runRow);
            // Table row 14: Nothing while active. Routed through the policy anyway (P7) rather than
            // assumed by the service, even though the only reachable outcome here is a no-op.
            CarryOut(runRow, _runPolicy.Decide(state, new RunEvent.HumanPosted(m.Id), pending));
            PostNote(m.RoomId, $"Steer noted; @{run.ConductorId} is given it when the current exchange concludes.");
            if (!state.ExchangeOpen && !state.AnythingInFlight) DriveRun(runRow, new RunEvent.Tick());
        }
    }

    /// <summary>Everything <see cref="RunPolicy"/> needs to know about <paramref name="run"/> right
    /// now, assembled from the store and the loop's own in-memory state (row 19, tasks 5/6/8).
    /// <see cref="RunState.RefusalsThisPhase"/> is written by <see cref="HandleConductorPost"/> (task
    /// 8); <see cref="RunState.SilencesThisPhase"/> stays 0 here until task 10 raises
    /// <c>SpawnSilent</c> and writes it - both counters share the same reset rule (EnterPhase clears
    /// them together, F-22), which is why they live in sibling dictionaries rather than one each
    /// wired up independently.</summary>
    private RunState AssembleRunState(Run run) => new(
        run.Id, run.RoomId, run.ConductorId, run.Status, run.CapSpent,
        run.Phase, _runs.PhaseEntries(run.Id),
        run.SpawnsUsed, run.Exchanges, RunStore.ActiveElapsed(run, _clock.GetUtcNow()),
        RefusalsThisPhase: _refusalsThisPhase.GetValueOrDefault(run.Id), SilencesThisPhase: _silencesThisPhase.GetValueOrDefault(run.Id),
        ExchangeOpen: _rooms.TryGetValue(run.RoomId, out var x) && x.Status == ExchangeStatus.Open,
        AnythingInFlight: InFlightIn(run.RoomId).Count > 0,
        RootMessageId: run.RootMessageId);

    /// <summary>Row 19, task 7 (AC9): what the prompt shows about the run a spawn is launched inside.
    /// Read fresh at every launch, never cached, so a re-spawned conductor sees the counters as they
    /// stand right now rather than as they stood when the run started.</summary>
    private RunView BuildRunView(Run run, string participantId, DateTimeOffset now) => new(
        run.Id, run.ConductorId, participantId == run.ConductorId, run.SkillName, run.Arguments,
        run.Phase, _runs.PhaseEntries(run.Id).GetValueOrDefault(run.Phase), _runLimits.PhaseEntries,
        run.Exchanges, run.SpawnsUsed, _runLimits.Spawns,
        RunStore.ActiveElapsed(run, now), _runLimits.WallClock,
        _runs.Artifacts(run.Id), _runs.GateRuns(run.Id));

    /// <summary>Assembles the state, asks <see cref="RunPolicy"/>, carries the decision out - never
    /// decided here (P7) - and drains this room's pending steers when the decision consumed them
    /// (row 19, tasks 5/6).</summary>
    private void DriveRun(Run run, RunEvent ev)
    {
        var pending = _steers.TryGetValue(run.RoomId, out var list) ? list : new List<long>();
        var decision = _runPolicy.Decide(AssembleRunState(run), ev, pending);
        CarryOut(run, decision);
        if (decision is RunDecision.OpenConductor) pending.Clear();
    }

    /// <summary>What <see cref="RunPolicy"/> decided, carried out. <see cref="RunDecision.Nothing"/>,
    /// <see cref="RunDecision.OpenConductor"/> (tasks 4-6), <see cref="RunDecision.OpenWorkers"/>,
    /// <see cref="RunDecision.RefuseAndAsk"/>, <see cref="RunDecision.Park"/> (task 8/9) and
    /// <see cref="RunDecision.Refuse"/> (task 9f, restored to P7) are wired; <see cref="RunDecision.End"/>
    /// is a later task's (13), so this fails loudly rather than silently if it is reached before its
    /// task lands.
    ///
    /// <see cref="RunDecision.OpenConductor"/> resumes first when <paramref name="run"/> is still
    /// PARKED (row 15/AC15's only caller): <see cref="RunStore.Resume"/> folds the whole parked
    /// interval into <c>parked_seconds</c> and flips the row to active before the exchange opens -
    /// every other caller of this arm (tasks 4-6, 8, 10) already holds an ACTIVE run, so the check is
    /// a no-op for them.</summary>
    private void CarryOut(Run run, RunDecision decision)
    {
        switch (decision)
        {
            case RunDecision.Nothing:
                break;
            case RunDecision.OpenConductor oc:
                var target = run.Status == RunStatus.Parked ? _runs.Resume(run.Id, _clock.GetUtcNow()) : run;
                OpenConductorExchange(target, oc.RootMessageId, oc.TriggerIds);
                break;
            case RunDecision.OpenWorkers ow:
                OpenWorkersExchange(run, ow);
                break;
            case RunDecision.RefuseAndAsk ra:
                // "Ask it once more" needs nothing further here: the conductor's OWN exchange is
                // still open with it in flight (a refusal never touches _rooms), so when its spawn
                // eventually exits, task 5's ExchangeConcluded handling re-spawns it with this note as
                // the trigger (table row 8) - the natural loop already does the asking.
                PostNote(run.RoomId, ra.Note);
                break;
            case RunDecision.Refuse rf:
                // Row 16 (AC15): post the line and do nothing else - never resumed (P7).
                PostNote(run.RoomId, rf.Note);
                break;
            case RunDecision.Park park:
                ParkRun(run, park.Reason, park.CapSpent);
                break;
            case RunDecision.End end:
                EndRun(run, end.Reason);
                break;
            default:
                throw new NotSupportedException($"RunDecision {decision.GetType().Name} is not wired yet (row19-runs, a later task).");
        }
    }

    /// <summary>Row 19, task 8 (AC4/AC6): the conductor's own post, raised while its exchange is still
    /// the room's current one (acceptMentions - the same distinction <see cref="OnMessage"/> already
    /// draws at its top - is exactly "the first phase-tagged post of a conductor spawn"; a LATER post
    /// from the same spawn, after it has already rooted, is prose and falls through to the ordinary
    /// model branch, never refused - pass 1's M1). Checked against D8's class rules
    /// (<see cref="ExchangePolicy.RefuseConductorPost"/>, pure) and raised to <see cref="RunPolicy"/>
    /// as <see cref="RunEvent.ConductorPosted"/>; the decision is carried out. The refusal counter is
    /// written HERE (pass 2's F-2): the policy only reads it.</summary>
    private void HandleConductorPost(Run run, Message m, DateTimeOffset now)
    {
        var mentioned = _policy.MentionedSpawnable(m);
        var runContext = new RunContext(run.Id, run.ConductorId, run.Phase);
        var refusal = _policy.RefuseConductorPost(m, runContext, mentioned,
            path => _runs.ArtifactAuthor(run.Id, path), path => ArtifactInRoomTree(run, path));
        PhaseTag.TryParse(m.Body, out var tag);
        var ev = new RunEvent.ConductorPosted(m.Id, tag, mentioned, refusal);

        var pending = _steers.TryGetValue(run.RoomId, out var list) ? list : new List<long>();
        var decision = _runPolicy.Decide(AssembleRunState(run), ev, pending);

        if (refusal is not null)
            _refusalsThisPhase[run.Id] = _refusalsThisPhase.GetValueOrDefault(run.Id) + 1;
        else
        {
            // Accepted (including ping): count a phase entry and reset BOTH counters (pass 2's F-22 -
            // the silence path re-enters the SAME tag, so a change-only reset would leave it latched).
            _runs.EnterPhase(run.Id, tag!.ToString(), now);
            _refusalsThisPhase[run.Id] = 0;
            _silencesThisPhase[run.Id] = 0;
        }

        CarryOut(run, decision);
    }

    /// <summary>Row 19, task 10 (A2): the run's CONDUCTOR finished without posting. Table rows 10-12
    /// all still ask again with the SAME triggers it was given (<paramref name="triggerIds"/>), but
    /// the bookkeeping differs: the FIRST silence in a phase costs nothing (row 10); every one after
    /// that ALSO counts a phase entry for THIS ask (row 12) - which is what lets row 11 eventually
    /// reach the phase's own entry cap and park the run instead of asking forever. "At most twice
    /// per phase entry" (ticket 10) falls out of that rule for whatever RunLimits.PhaseEntries says -
    /// this never hardcodes "twice". Pass 1's B3 closed exactly this stall; the fold reintroduced it
    /// by leaving the counter declared, consumed and reset with no write site (pass 2's F-2) -
    /// written HERE, since RunPolicy only reads it. <see cref="AssembleRunState"/>'s
    /// <c>SilencesThisPhase</c> is read ONCE, before the increment, so <see cref="RunPolicy.Decide"/>
    /// sees how many silences already happened before this one - the same before-then-increment
    /// order <see cref="HandleConductorPost"/> uses for the refusal counter.</summary>
    private void HandleSpawnSilent(Run run, string participantId, IReadOnlyList<long> triggerIds)
    {
        var pending = _steers.TryGetValue(run.RoomId, out var list) ? list : new List<long>();
        var state = AssembleRunState(run);
        var decision = _runPolicy.Decide(state, new RunEvent.SpawnSilent(participantId, triggerIds), pending);

        // Row 12's "the service counts a phase entry for this ask": only past the first silence, and
        // only when the run is actually still being asked again (rows 1/2's hard caps still win
        // first here exactly as everywhere else in RunPolicy, and a Park needs no phase entry).
        if (state.SilencesThisPhase >= 1 && decision is RunDecision.OpenConductor)
            _runs.EnterPhase(run.Id, run.Phase, _clock.GetUtcNow());
        _silencesThisPhase[run.Id] = state.SilencesThisPhase + 1;

        CarryOut(run, decision);
    }

    /// <summary>Row 19, task 8 (AC4): the conductor's post passed every D8 rule and asks for work -
    /// rooted at that post (<see cref="RunDecision.OpenWorkers.RootMessageId"/>), never at the
    /// conductor's own re-spawn root.</summary>
    private void OpenWorkersExchange(Run run, RunDecision.OpenWorkers ow)
    {
        var (exchange, notes) = _policy.OpenForWorkers(run.RoomId, ow.RootMessageId, ow.Mentioned, _clock.GetUtcNow());
        _rooms[run.RoomId] = exchange;
        _runs.CountExchange(run.Id);
        foreach (var note in notes) PostNote(run.RoomId, note);
        Publish(run.RoomId);
    }

    /// <summary>Row 19: writes the parked row, stops the room's open exchange and cancels its
    /// in-flight spawns (pass 1's M3 - a park that touches neither would let an in-flight conductor's
    /// later post resolve <c>run == null</c> and take the plain model branch, launching more spawns
    /// from a parked run), posts the note naming the reason and @owner, and publishes. Landed in
    /// task 8 for AC6's second-bad-post-in-one-phase park; task 9's 9a audited it against the plan's
    /// full spec (spawn/wall-clock/phase-entry caps, all reached through the SAME <see
    /// cref="RunDecision.Park"/> arm via <see cref="CarryOut"/>) and found it already complete -
    /// task 9 adds the wakes that make every cap REACHABLE (<see cref="ArmWake"/>, <see
    /// cref="OnTick"/>) and the 30-minute in-run timeout, not a second park path.</summary>
    private void ParkRun(Run run, string reason, bool capSpent)
    {
        var now = _clock.GetUtcNow();
        _runs.Park(run.Id, reason, capSpent, now);
        if (_rooms.TryGetValue(run.RoomId, out var x) && x.Status == ExchangeStatus.Open)
            PostNote(run.RoomId, ExchangePolicy.Stop(x));
        foreach (var handle in _inFlight.Values.Where(h => h.Request.RoomId == run.RoomId).ToList())
            handle.Cancel.Cancel();
        PostNote(run.RoomId, $"Run #{run.Id} parked: {reason}. @{_owner.Id}");
        Publish(run.RoomId);
    }

    /// <summary>Row 19, task 13 (AC11): ends a run - active OR parked, and whether or not an exchange
    /// is open or anything is in flight - through the ONE path <see cref="RunDecision.End"/> ever
    /// reaches (the text `/stop`, and the API/button stop via <see cref="OnStop"/>). Mirrors
    /// <see cref="ParkRun"/>'s shape (stop the open exchange, cancel the room's in-flight spawns) but
    /// also clears the room's pending steers (ticket 13: "ending a run clears the pending steer
    /// list" - depends on task 6, which is the only other writer of <see cref="_steers"/>) and posts
    /// ONE note naming how much of each cap the run used rather than a bare "parked: reason" line.
    /// <see cref="RunStore.ActiveElapsed"/> is read against the SAME <paramref name="reason"/>-ending
    /// instant <see cref="RunStore.End"/> stamps, and against the run as it stood before ending (an
    /// ended run is never <see cref="RunStatus.Parked"/>, so it falls to that method's "not parked"
    /// arm: elapsed time since start, minus whatever was already parked).</summary>
    private void EndRun(Run run, string reason)
    {
        var now = _clock.GetUtcNow();
        var ended = _runs.End(run.Id, reason, now);
        if (_rooms.TryGetValue(run.RoomId, out var x) && x.Status == ExchangeStatus.Open)
            PostNote(run.RoomId, ExchangePolicy.Stop(x));
        foreach (var handle in _inFlight.Values.Where(h => h.Request.RoomId == run.RoomId).ToList())
            handle.Cancel.Cancel();
        _steers.Remove(run.RoomId);
        var elapsed = RunStore.ActiveElapsed(ended, now);
        var phaseEntries = _runs.PhaseEntries(ended.Id).GetValueOrDefault(ended.Phase);
        PostNote(run.RoomId, $"Run #{run.Id} ended: {reason}. Used {ended.SpawnsUsed}/{_runLimits.Spawns} spawns, "
            + $"{elapsed.TotalHours:0.0}/{_runLimits.WallClock.TotalHours:0.0}h active time, phase '{ended.Phase}' entered {phaseEntries}/{_runLimits.PhaseEntries} time(s).");
        Publish(run.RoomId);
    }

    /// <summary>Row 19, task 8 (D8's critique rule): whether <paramref name="path"/> is present in the
    /// room's own directory tree - satisfies "recorded or in the room tree" for an artifact nothing
    /// has recorded yet. Normalizes with the SAME <see cref="RunStore.Normalize"/> the authorship
    /// lookup uses, so the two checks can never disagree on one path (P4). A room with no directory,
    /// an absolute path, or one that walks above the room root, is never "in the tree".</summary>
    private bool ArtifactInRoomTree(Run run, string path)
    {
        var directory = _store.GetRoom(run.RoomId)?.Directory;
        if (directory is null) return false;
        var normalized = RunStore.Normalize(path);
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Contains("..")) return false;
        return File.Exists(Path.GetFullPath(Path.Combine(directory, normalized)));
    }

    /// <summary>Row 19, task 5a: the hub re-spawning its run's conductor - no message roots this, so
    /// <see cref="ExchangePolicy.OnMessage"/>'s human-only rule is untouched (P2). Re-resolves the
    /// skill by <see cref="Run.SkillName"/> at every launch, honouring the hash pin (5a): a skill that
    /// no longer matches what was imported parks the run rather than continuing with no instruction.</summary>
    private void OpenConductorExchange(Run run, long rootMessageId, IReadOnlyList<long> triggerIds)
    {
        var now = _clock.GetUtcNow();
        if (_skills.Read(run.SkillName) is not SkillRead.Ok ok)
        {
            _runs.Park(run.Id, $"skill /{run.SkillName} does not match what was imported", capSpent: false, now);
            PostNote(run.RoomId, $"Run #{run.Id} parked: skill /{run.SkillName} does not match what was imported; re-import it with --import-skill.");
            Publish(run.RoomId);
            return;
        }
        var exchange = ExchangePolicy.OpenForConductor(run.RoomId, run.ConductorId, rootMessageId, triggerIds, now, ok.Skill);
        _rooms[run.RoomId] = exchange;
        _runs.CountExchange(run.Id);
        Publish(run.RoomId);
        // Launching is left to LaunchDue on the next pass, never inline (task 5b) - a directory
        // room's one-spawn-at-a-time exclusivity must not be bypassed.
    }

    /// <summary>What the message's first line asks for, if anything. Only a human's post is ever
    /// resolved (a model's `/whatever` is prose, acceptance 3) — the policy itself never touches the
    /// filesystem (D-b), so this is the one place row 11's I/O happens.
    ///
    /// Safe to do on this loop's single thread: <see cref="SkillStore.Read"/> refuses on file length
    /// before reading a byte (<c>MaxSkillFileBytes</c>), so a local read plus one SHA-256 of at most
    /// 1 MB is sub-millisecond, and it happens once per exchange root, not per turn. That bound (and
    /// the same one in <c>List</c>) is what makes this placement correct — without it, a single
    /// oversized file in a store the threat model says is writable would stall every room's exchange
    /// handling and the owner's stop button behind a read and a hash. Do not remove either guard as
    /// "defensive".</summary>
    private SkillResolution ResolveSkill(Message m)
    {
        var author = _roster.FirstOrDefault(p => p.Id == m.AuthorId);
        if (author is not { Kind: "human" }) return SkillResolution.Nothing;
        if (!SlashCommands.TryParse(m.Body, out var invocation)) return SkillResolution.Nothing;
        try
        {
            return _skills.Read(invocation.Name) switch
            {
                SkillRead.Ok ok => new SkillResolution.Found(ok.Skill, invocation.Arguments),
                SkillRead.Tampered => new SkillResolution.Tampered(invocation.Name),
                // Exhaustive on purpose - NOT a `_ =>` catch-all. A future SkillRead arm falling
                // through to "No skill named /x" would be the hub telling the owner a lie by default.
                SkillRead.NotFound => new SkillResolution.Unknown(invocation.Name, _skills.List().Select(s => s.Name).ToList()),
                var other => throw new InvalidOperationException($"Unhandled SkillRead {other.GetType().Name}."),
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Deliberately broad, and deliberately NOT a degrade to Nothing. Nothing would open a
            // normal exchange and spend the model calls on a skill-less prompt, silently (D-c). A
            // narrow catch is just as bad in the other direction: anything it misses escapes to the
            // loop's Guarded, which logs and swallows the WHOLE PostedEvent - so no exchange opens,
            // no note is posted, the supersede never happens, and a previously-open exchange stays
            // Open in _rooms and keeps accepting model posts. That is silent state divergence.
            return new SkillResolution.Unavailable(invocation.Name, e.Message);
        }
    }

    private void LaunchDue()
    {
        var now = _clock.GetUtcNow();
        foreach (var x in _rooms.Values.ToList())
        {
            if (x.Status != ExchangeStatus.Open) continue;
            var inRoom = InFlightIn(x.RoomId);
            var exclusive = _store.GetRoom(x.RoomId)?.Directory is not null;
            var due = _policy.Due(x, now, _lastStart, inRoom, exclusive);
            foreach (var request in due) Launch(x, request, now);
            if (due.Count > 0) Publish(x.RoomId);
        }
    }

    private HashSet<string> InFlightIn(string roomId) =>
        _inFlight.Keys.Where(k => k.Room == roomId).Select(k => k.Participant).ToHashSet(StringComparer.Ordinal);

    private void Launch(Exchange x, SpawnRequest request, DateTimeOffset now)
    {
        var participant = _roster.First(p => p.Id == request.ParticipantId);
        var spawnId = $"{request.RoomId}-{request.RootMessageId}-{request.TurnNumber}-{Guid.NewGuid().ToString("N")[..8]}";
        var workDir = Path.GetFullPath(Path.Combine(_options.DataDir, "spawns", spawnId));
        ExchangePolicy.Started(x, request);
        _lastStart[participant.Id] = now;
        // Row 19, task 9c: captured HERE, at launch - not re-read inside the spawn's own task body
        // or at OnFinished - because a run can park or end while this spawn is still alive, and both
        // the timeout actually given to IProcessRunner and the notes OnFinished writes about it must
        // agree on the SAME value.
        var activeRun = _runs.Active(request.RoomId);
        var timeout = activeRun is not null ? _runLimits.SpawnTimeout : _limits.Timeout;
        // Row 19, task 11 (AC7/D10): a conductor thinks harder about its own loop, and a judge about
        // what it is asked to judge; everyone else, and anything outside a run, gets no effort flag
        // at all rather than an explicit default. Never xhigh or max.
        var effort = activeRun is not null && (participant.Id == activeRun.ConductorId || ParticipantClasses.Has(participant, ParticipantClasses.Judge))
            ? "high" : null;
        try
        {
            // Row 19, task 9d: counted before anything below can throw - a spawn that fails even to
            // start (a bad token, a directory that vanished) still used one of the run's spawns.
            if (activeRun is not null) _runs.CountSpawn(activeRun.Id);
            var token = _tokens.Tokens[participant.Id];
            Directory.CreateDirectory(workDir);
            var core = _memory.ReadCore();
            var room = _store.GetRoom(request.RoomId);
            var directory = room?.Directory;
            var runView = activeRun is not null ? BuildRunView(activeRun, participant.Id, _clock.GetUtcNow()) : null;
            var prompt = SpawnPrompt.Render(new SpawnPromptInput(
                participant, request.RoomId, room?.Name ?? request.RoomId, _store.ReadLast(request.RoomId, _limits.TranscriptMessages),
                request.TriggerIds, request.RootMessageId, request.TurnNumber, x.Budget, request.RemainingAfter, spawnId, _roster,
                core.Text, core.Truncated, _memory.ListTopics().Select(t => t.Slug).ToList(), Directory: directory, Skill: x.Skill, Run: runView), _limits);
            var label = $"{participant.Id}/{spawnId}";
            ProcessSpec spec;
            switch (participant.Host)
            {
                case "claude":
                {
                    // Row 20, task 3: computed BEFORE the mcp.json write so both the per-server
                    // `timeout` field (below) and the two environment variables ClaudeInDirectory sets
                    // (in the directory branch) agree on the same value - RunLimits.EffectiveGateTimeout
                    // (25 min by default), not the run's own 30-minute SpawnTimeout, leaving the 5-minute
                    // reserve documented on RunLimits. Null outside a run (a run always binds a
                    // directory, so this is never non-null with directory is null below).
                    var mcpToolTimeoutMs = activeRun is not null ? (int?)_runLimits.EffectiveGateTimeout.TotalMilliseconds : null;
                    var mcpPath = Path.Combine(workDir, "mcp.json");
                    File.WriteAllText(mcpPath, SpawnCommands.ClaudeMcpConfigJson(McpUrl(), token, mcpToolTimeoutMs));
                    if (directory is null)
                        spec = SpawnCommands.Claude(Cli("claude"), participant.Model!, mcpPath, workDir, prompt, label, effort);
                    else
                    {
                        var settingsPath = Path.Combine(workDir, "settings.json");     // scratch, never the room (decision 8)
                        File.WriteAllText(settingsPath, SpawnCommands.ClaudeSettingsJson(_options.DataDir));
                        // Row 19, task 12e: run_gate joins the allowlist only for an in-run spawn - the
                        // built-in tool set (--tools) is untouched either way, and an out-of-run
                        // directory spawn never sees the extra MCP tool at all.
                        var allowedTools = activeRun is not null ? SpawnCommands.ClaudeRunToolsAllowed : SpawnCommands.ClaudeDirectoryToolsAllowed;
                        spec = SpawnCommands.ClaudeInDirectory(Cli("claude"), participant.Model!, mcpPath, settingsPath, SpawnPrompt.DirectoryRules(directory), directory, prompt, label, effort, allowedTools, mcpToolTimeoutMs);
                    }
                    break;
                }
                case "codex":
                    // Row 19, task 12f (pass 1's M7); row 20 task 3 lowers the ceiling from SpawnTimeout
                    // to EffectiveGateTimeout (the same 5-minute reserve as the Claude side above): the
                    // CLI's default 60 s MCP tool-call timeout kills a long run_gate call well before it
                    // can finish. Raised for an in-run directory spawn only; every other Codex spawn
                    // keeps 60 s.
                    var toolTimeoutSeconds = activeRun is not null ? (int)_runLimits.EffectiveGateTimeout.TotalSeconds : 60;
                    spec = directory is null
                        ? SpawnCommands.Codex(Cli("codex"), participant.Model!, McpUrl(), token, workDir, Path.Combine(workDir, "last.txt"), prompt, label, effort)
                        : SpawnCommands.CodexInDirectory(Cli("codex"), participant.Model!, McpUrl(), token, directory, Path.Combine(workDir, "last.txt"), prompt, label, effort, toolTimeoutSeconds);
                    break;
                default:
                    throw new InvalidOperationException($"Participant '{participant.Id}' has host '{participant.Host}', which the spawner does not know how to start.");
            }
            var handle = new SpawnHandle
            {
                Request = request, Exchange = x, Participant = participant, SpawnId = spawnId, WorkDir = workDir, Token = token,
                Cancel = new CancellationTokenSource(), Directory = directory, Timeout = timeout,
            };
            _inFlight[(request.RoomId, participant.Id)] = handle;
            Console.Error.WriteLine($"spawn {spawnId}: {participant.Id} starting (turn {request.TurnNumber}/{x.Budget}, {request.RemainingAfter} after)");
            Interlocked.Increment(ref _live);
            var turn = request.TurnNumber; var budget = x.Budget; var roomId = request.RoomId; var host = participant.Host;
            handle.Run = Task.Run(async () =>
            {
                var result = new ProcessResult(null, false, false, "", "not started", TimeSpan.Zero);
                TrailReport? trail = null;
                try
                {
                    GitTrail? git = null;
                    CommitOutcome? owner = null;
                    string? headBefore = null;
                    if (directory is not null)
                    {
                        // Before: the owner's edits since the last spawn become their own commit (decision 6).
                        git = _trails.For(directory);
                        if (await git.IsDirtyAsync(CancellationToken.None))
                            owner = await git.CommitAllAsync(RoomCommits.OwnerMessage(_owner, roomId), RoomCommits.IdentityOf(_owner), allowEmpty: false, CancellationToken.None);
                        headBefore = await git.HeadAsync(CancellationToken.None);
                    }
                    try { result = await _runner.RunAsync(spec, timeout, handle.Cancel.Token); }
                    catch (Exception e) { result = new ProcessResult(null, false, false, "", "launch failed: " + e.Message, TimeSpan.Zero); }
                    if (git is not null)
                    {
                        // After: always a commit, empty or not, timed out or not — the trail says the spawn happened.
                        var commands = (host == "codex" ? SpawnOutput.CodexShellCommands(result.StandardOutput) : SpawnOutput.ClaudeShellCommands(result.StandardOutput))
                            .Select(c => c with { Command = Scrub(StripAnsi(c.Command), token) }).ToList();
                        var headMoved = await git.HeadAsync(CancellationToken.None) != headBefore;
                        var agent = await git.CommitAllAsync(RoomCommits.AgentMessage(participant, roomId, turn, budget, commands, headMoved), RoomCommits.IdentityOf(participant), allowEmpty: true, CancellationToken.None);
                        trail = new TrailReport(owner, agent, commands.Count, headMoved);

                        // Row 19, task 5c (P4): artifact authorship, from the SPAWN'S WHOLE DIFF - not
                        // one commit, so a host that commits its own work mid-spawn (Codex, or a rogue
                        // Claude Bash call) is still attributed correctly. This runs off the spawner
                        // loop (pass 2's F-21): the run lookup is _runs.Active (a database read, thread
                        // safe), never a read of _rooms/_inFlight, which only the loop thread mutates.
                        if (agent.Hash is not null && _runs.Active(roomId) is { } runForArtifacts)
                        {
                            var changed = headBefore is not null
                                ? await git.ChangedFilesAsync($"{headBefore}..{agent.Hash}", CancellationToken.None)
                                : await git.ChangedFilesInAsync(agent.Hash, CancellationToken.None);
                            var stampedAt = _clock.GetUtcNow();
                            foreach (var path in changed) _runs.RecordArtifact(runForArtifacts.Id, path, participant.Id, stampedAt);
                        }
                    }
                }
                catch (Exception e) { Console.Error.WriteLine($"spawn {spawnId}: trail error {e.GetType().Name}: {e.Message}"); }
                finally { _events.Writer.TryWrite(new FinishedEvent(handle, result, trail)); }   // always: OnFinished is the only place _live is decremented
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            PostNote(request.RoomId, $"@{participant.Id} could not be started: {e.GetType().Name}: {e.Message}");
            TryDeleteDir(workDir);
            var note = ExchangePolicy.Finished(x, participant.Id);
            if (note is not null) PostNote(request.RoomId, note);
        }
    }

    private void OnFinished(SpawnHandle h, ProcessResult r, TrailReport? trail)
    {
        var id = h.Participant.Id;
        var room = h.Request.RoomId;
        _inFlight.Remove((room, id));
        Interlocked.Decrement(ref _live);
        Console.Error.WriteLine($"spawn {h.SpawnId}: {id} ended exit={(r.ExitCode?.ToString() ?? "killed")} timedOut={r.TimedOut} cancelled={r.Cancelled} posted={h.Posted} in {r.Elapsed.TotalSeconds:0}s");

        if (r.TimedOut)
        {
            PostNote(room, h.Posted
                ? $"@{id} posted but did not exit within {Describe(h.Timeout)}; its process was stopped."
                : $"@{id} did not reply within {Describe(h.Timeout)} and was stopped.");
        }
        else if (r.Cancelled)
        {
            // Stopped by the owner or by shutdown; the stop note (or nothing) is the record.
        }
        else if (!h.Posted)
        {
            var final = h.Participant.Host == "codex"
                ? SpawnCommands.CodexFinalText(Path.Combine(h.WorkDir, "last.txt"))
                : SpawnCommands.ClaudeFinalText(r.StandardOutput);
            var exit = r.ExitCode?.ToString() ?? "none";
            if (final is not null)
                PostNote(room, $"@{id} replied without posting to the room (exit code {exit}). Its reply:\n\n{Truncate(Scrub(StripAnsi(final), h.Token), NoteReplyChars)}");
            else
            {
                var stderr = Scrub(StripAnsi(r.StandardError.Trim()), h.Token);
                PostNote(room, $"@{id} exited with code {exit} without replying."
                    + (stderr.Length > 0 ? $" Last output:\n\n{Tail(stderr, NoteStderrChars)}" : ""));
            }
        }

        TryDeleteDir(h.WorkDir);
        if (trail is not null) PostNote(room, HubNotes.Trail(id, trail.Owner, trail.Agent, trail.Commands, trail.HeadMoved));
        h.Cancel.Dispose();
        var note = ExchangePolicy.Finished(h.Exchange, id);
        if (note is not null)
        {
            PostNote(room, note);
            // Row 19, task 5b: wake the run's loop, but only when the exchange that just concluded is
            // still the room's CURRENT one - a conductor may already have rooted a newer exchange that
            // superseded it (pass 2's F-1); that newer exchange must be left alone.
            if (_runs.Active(room) is { } activeRun && ReferenceEquals(h.Exchange, _rooms.GetValueOrDefault(room)))
            {
                // Row 19, task 10 (A2): the run's CONDUCTOR finishing WITHOUT posting is a silence,
                // decided through RunEvent.SpawnSilent (ask again, then park once the phase's
                // allowance is spent) rather than the unconditional immediate reopen every OTHER
                // conclusion gets via ExchangeConcluded - a worker's turn, or the conductor's own
                // turn when it DID post (even a refused post is still "posted": HandleConductorPost
                // already asked it again itself, above this method entirely).
                if (!h.Posted && activeRun.ConductorId == id)
                    HandleSpawnSilent(activeRun, id, h.Request.TriggerIds);
                else
                {
                    var lastId = _store.ReadLast(room, 1).Select(msg => msg.Id).DefaultIfEmpty(h.Request.RootMessageId).First();
                    DriveRun(activeRun, new RunEvent.ExchangeConcluded(lastId));
                }
            }
        }
        Publish(room);
    }

    /// <summary>Room-scoped, not exchange-scoped (critique pass 2, M1): a superseded exchange's spawn
    /// is still a live CLI in this room, and an owner message with no mention leaves the room with no
    /// open exchange while one runs. Stop kills every in-flight spawn of the room, closes the open
    /// exchange if there is one, and answers null only when there is nothing at all to stop.
    ///
    /// Row 19, task 13 (AC11, ticket 13 - "the control must work even when there is nothing currently
    /// running"): checked FIRST, ahead of the ordinary exchange-only stop below. An active OR parked
    /// run in this room is ended through <see cref="EndRun"/> regardless of whether an exchange is
    /// open or anything is in flight - the exact case the ordinary branch's "nothing to stop" null
    /// (409 at the API) would otherwise hit for a parked run sitting with nothing open and nothing in
    /// flight. A room with no run at all falls through to the pre-row-19 behaviour, unchanged.</summary>
    private ExchangeSnapshot? OnStop(string roomId)
    {
        var run = _runs.Active(roomId) ?? (_runs.Latest(roomId) is { Status: RunStatus.Parked } parked ? parked : null);
        if (run is not null)
        {
            EndRun(run, "stopped by the owner");
            return Publish(roomId);
        }

        _rooms.TryGetValue(roomId, out var x);
        var live = _inFlight.Values.Where(h => h.Request.RoomId == roomId).ToList();
        var open = x is { Status: ExchangeStatus.Open };
        if (!open && live.Count == 0) return null;
        foreach (var handle in live) handle.Cancel.Cancel();
        var note = open
            ? ExchangePolicy.Stop(x!)
            : $"Exchange stopped by the owner: {live.Count} running spawn(s) of an earlier exchange stopped.";
        PostNote(roomId, note);
        return Publish(roomId);
    }

    private void ArmWake()
    {
        var now = _clock.GetUtcNow();
        DateTimeOffset? next = null;
        foreach (var x in _rooms.Values)
        {
            var exclusive = _store.GetRoom(x.RoomId)?.Directory is not null;
            var wake = _policy.NextWake(x, now, _lastStart, InFlightIn(x.RoomId), exclusive);
            if (wake is not null && (next is null || wake < next)) next = wake;
        }

        // Row 19, task 9b (pass 2's F-7): every ACTIVE run gets two wakes of its own, neither of
        // which the exchange-only loop above can see - a run with nothing open and nothing in
        // flight has no Open exchange in _rooms at all for _policy.NextWake to consider. The
        // wall-clock wake is unconditional (D9's 8-hour hard cap must trip even while the run is
        // busy - AC8 says "stop its open exchange and in-flight spawns", not "only while idle"); the
        // stall wake only arms while genuinely idle, SpawnTimeout after the last instant the run was
        // known busy - never "SpawnTimeout from right now", or an unrelated room's activity (which
        // reruns THIS method on every pass of the loop) would keep pushing a truly stalled run's
        // deadline forward forever, and it would never actually fire.
        var activeIds = new HashSet<long>();
        foreach (var run in _runs.ListActive())
        {
            activeIds.Add(run.Id);
            var wallClockDeadline = run.StartedAt + TimeSpan.FromSeconds(run.ParkedSeconds) + _runLimits.WallClock;
            if (next is null || wallClockDeadline < next) next = wallClockDeadline;

            var busy = (_rooms.TryGetValue(run.RoomId, out var rx) && rx.Status == ExchangeStatus.Open) || InFlightIn(run.RoomId).Count > 0;
            if (busy) { _lastRunActivity.Remove(run.Id); continue; }
            if (!_lastRunActivity.TryGetValue(run.Id, out var idleSince)) _lastRunActivity[run.Id] = idleSince = now;
            var stallDeadline = idleSince + _runLimits.SpawnTimeout;
            if (next is null || stallDeadline < next) next = stallDeadline;
        }
        foreach (var staleId in _lastRunActivity.Keys.Where(id => !activeIds.Contains(id)).ToList()) _lastRunActivity.Remove(staleId);

        _wake?.Cancel();
        _wake?.Dispose();
        _wake = null;
        if (next is null) return;
        var delay = next.Value - now;
        if (delay < TimeSpan.FromMilliseconds(10)) delay = TimeSpan.FromMilliseconds(10);
        var cts = new CancellationTokenSource();
        _wake = cts;
        // Row 19, task 9b: the TimeProvider overload, not the bare one - a FakeTimeProvider's
        // Advance() must be able to fire this without a test actually waiting out 8 hours.
        _ = Task.Delay(delay, _clock, cts.Token).ContinueWith(t => { if (!t.IsCanceled) _events.Writer.TryWrite(new TickEvent()); }, TaskScheduler.Default);
    }

    private long _seq;

    private ExchangeSnapshot Publish(string roomId)
    {
        // InFlight is the ROOM's live spawns (a superseded exchange's spawn included), not the current
        // exchange's list; Seq lets row 16 order a GET against an event (critique pass 2, M1, m10).
        var snapshot = (_rooms.TryGetValue(roomId, out var x)
            ? new ExchangeSnapshot(roomId, x.Status.ToString().ToLowerInvariant(), x.RootMessageId, x.Budget, x.TurnsStarted, x.TurnsCommitted,
                Math.Max(0, x.Budget - x.TurnsCommitted), InFlightIn(roomId).Order(StringComparer.Ordinal).ToList(), x.Pending.Keys.ToList())
            : Idle(roomId)) with { Seq = ++_seq };
        _snapshots[roomId] = snapshot;
        BroadcastAsync(roomId, snapshot);
        return snapshot;
    }

    private async void BroadcastAsync(string roomId, ExchangeSnapshot snapshot)
    {
        try { await _hub.Clients.Group(roomId).SendAsync(ChangedEvent, snapshot); }
        catch (Exception e) { Console.Error.WriteLine($"SignalR {ChangedEvent} to room '{roomId}' failed: {e.Message}"); }
    }

    private void PostNote(string roomId, string text)
    {
        try
        {
            var message = _store.Post(roomId, ChopDb.HubParticipantId, text);
            _signal.Publish(roomId, message);   // re-enters this loop as a PostedEvent; system authors are ignored by the policy
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A note is the trail, not the mechanism: losing one must not lose the exchange or the loop.
            Console.Error.WriteLine($"spawner: note to '{roomId}' not posted ({e.GetType().Name}: {e.Message}): {text.Split('\n')[0]}");
        }
    }

    private static ExchangeSnapshot Idle(string roomId) => new(roomId, "idle", null, 0, 0, 0, 0, [], []);

    private ResolvedCli Cli(string name)
    {
        if (!_clis.TryGetValue(name, out var cli)) _clis[name] = cli = _locate(name);
        return cli;
    }

    private string McpUrl()
    {
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var port = addresses.Select(a => new Uri(a)).FirstOrDefault(u => u.Host == "127.0.0.1")?.Port
            ?? addresses.Select(a => new Uri(a).Port).FirstOrDefault();
        if (port == 0) throw new InvalidOperationException("The hub has not bound a port yet.");
        return $"http://127.0.0.1:{port}/mcp";
    }

    private static string Scrub(string text, string token) =>
        string.IsNullOrEmpty(token) ? text : text.Replace(token, "<token>", StringComparison.Ordinal);

    // CLI subprocesses write ANSI colour codes to stderr/stdout; a CSI sequence (ESC '[' ... final byte)
    // or a bare ESC followed by one character (OSC/other) both get dropped before a note reaches the room.
    private static readonly Regex AnsiEscape = new(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|.)", RegexOptions.Compiled);

    private static string StripAnsi(string text) => AnsiEscape.Replace(text, "");

    private static string Describe(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} minute(s)" : $"{t.TotalSeconds:0} second(s)";

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        if (char.IsHighSurrogate(text[max - 1])) max--;   // never split a surrogate pair
        return text[..max] + "\n…(truncated)";
    }

    private static string Tail(string text, int max) => text.Length <= max ? text : "…" + text[^max..];

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"spawn dir '{dir}' not deleted: {e.Message}"); }
    }
}
