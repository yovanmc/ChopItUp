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

/// <summary>One exchange as the UI and the API see it: its own root, status, budget, turns,
/// in-flight and pending. <see cref="InFlight"/> is this exchange's own live spawns, unlike the
/// snapshot's room-wide list.</summary>
public sealed record ExchangeView(
    long RootMessageId, string Status, int Budget, int TurnsUsed, int TurnsCommitted, int Remaining,
    IReadOnlyList<string> InFlight, IReadOnlyList<string> Pending, string? StoppedBy, bool Continuable = false,
    IReadOnlyDictionary<string, DateTimeOffset>? InFlightStartedAt = null, string? Mode = null, IReadOnlyList<string>? ModeParticipants = null, bool Preparing = false);

/// <summary>What a per-exchange stop found. The API maps NotFound to 404, NothingToStop and
/// RunOwnsRoom to 409, Stopped to 200 with the snapshot.</summary>
public enum ExchangeStopOutcome { Stopped, NotFound, NothingToStop, RunOwnsRoom }

/// <summary>What the UI and the API see. <see cref="Status"/> is <c>idle</c>, <c>open</c>,
/// <c>concluded</c>, <c>superseded</c> or <c>stopped</c>. <see cref="StoppedBy"/> is the wire name
/// of the <see cref="ExchangeStopCause"/> that stopped it (<c>owner</c> or <c>run</c>), mapped by
/// name so an enum reordering never silently changes the JSON; null until stopped.
/// <see cref="Exchanges"/> lists every exchange the room still holds, in the order they were opened; a
/// reply that reopens one moves it to the end. The top-level fields describe the newest open one, else
/// the newest.</summary>
public sealed record ExchangeSnapshot(
    string RoomId, string Status, long? RootMessageId, int Budget, int TurnsUsed, int TurnsCommitted, int Remaining,
    IReadOnlyList<string> InFlight, IReadOnlyList<string> Pending, long Seq = 0, string? StoppedBy = null, IReadOnlyList<ExchangeView>? Exchanges = null, bool Continuable = false,
    IReadOnlyDictionary<string, DateTimeOffset>? InFlightStartedAt = null, string? Mode = null, IReadOnlyList<string>? ModeParticipants = null, bool Preparing = false);

/// <summary>One loop, one thread of control: posts, completions, stop requests and timer ticks are
/// one FIFO channel, handled in order; after each batch the loop launches whatever
/// <see cref="ExchangePolicy.Due"/> says and arms a timer for the next moment anything could become
/// due. State is per room, in memory. Notes go into the room as the hub participant; the launch
/// itself goes through <see cref="IProcessRunner"/> so tests never start a CLI. Nothing here reads a
/// token out to a log: the only places a token goes are the per-spawn <c>mcp.json</c> and the Codex
/// environment, and every quoted output is scrubbed first.</summary>
public sealed partial class SpawnerService : BackgroundService
{
    private abstract record Event;
    private sealed record PostedEvent(Message Message) : Event;
    private sealed record FinishedEvent(SpawnHandle Handle, ProcessResult Result, TrailReport? Trail) : Event;
    private sealed record StopEvent(string RoomId, TaskCompletionSource<ExchangeSnapshot?> Reply) : Event;
    private sealed record StopOneEvent(string RoomId, long RootMessageId, TaskCompletionSource<(ExchangeStopOutcome, ExchangeSnapshot?)> Reply) : Event;
    private sealed record TickEvent : Event;
    // A worktree close finished off the loop thread; carries the note to post (or null).
    private sealed record WorktreeClosedEvent(string RoomId, string? Note) : Event;

    /// <summary>What the trail did around one spawn in a directory room; null when the room has no
    /// directory. <see cref="Leased"/> is true when the spawn body ran in a leased worktree.</summary>
    private sealed record TrailReport(CommitOutcome? Owner, CommitOutcome Agent, int Commands, bool HeadMoved, bool Leased = false);

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
        // Captured at launch, never re-derived later: a run can park or end while its spawn is still
        // alive, and the "did not reply in time" notes at OnFinished must describe the timeout the
        // spawn was actually given.
        public required TimeSpan Timeout { get; init; }
        // The chip becomes "working" at Launch, before optional git preparation. This is elapsed
        // working time, not a claim about model execution or billable time.
        public required DateTimeOffset StartedAt { get; init; }
        public Task Run { get; set; } = Task.CompletedTask;
        public bool Posted { get; set; }
        public List<string> PostedTexts { get; } = new();
        // True when this spawn runs in its exchange's own worktree rather than the room directory.
        public bool InWorktree { get; init; }
    }

    public const string ChangedEvent = "ExchangeChanged";
    private const int NoteReplyChars = 4_000;
    private const int NoteStderrChars = 600;
    // A worktree lease refusal reported back through ProcessResult.StandardError, so OnFinished
    // (loop thread) can tell it apart from every other "no CLI ran" shape and post the dedicated note.
    private const string WorktreeRefused = "the exchange's worktree could not be created: ";

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
    private readonly ExchangeWorktrees _worktrees;
    private readonly Participant _owner;
    // _roster stays the startup snapshot for identity, peers and tokens, but role and persona text
    // must reflect a web-UI edit without a hub restart, so they are read live at launch through
    // _participants.EffectiveRole.
    private readonly ParticipantStore _participants;
    private readonly ExchangePolicy _policy;
    private readonly SkillStore _skills;
    private readonly TimeProvider _clock;
    private readonly RunStore _runs;
    private readonly RunLimits _runLimits;
    private readonly RunPolicy _runPolicy;
    private readonly Channel<Event> _events = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
    // Every exchange the loop still holds per room, oldest first. The newest is "the room's exchange"
    // for the run machinery and the top-level snapshot.
    private readonly Dictionary<string, List<Exchange>> _rooms = new(StringComparer.Ordinal);
    // The last JoinableKept exchanges an owner prompt opened per room, oldest first, whatever their
    // status, so an owner reply finds its exchange after AddExchange pruned it from _rooms. Run
    // exchanges are never here: a reply never reopens a conductor's or its workers' exchange.
    private readonly Dictionary<string, List<Exchange>> _joinable = new(StringComparer.Ordinal);
    internal const int JoinableKept = 50;
    // Messages posted by a human inside an active run, waiting for the conductor's next trigger set.
    // DriveRun drains it whenever an OpenConductor decision consumes it.
    private readonly Dictionary<string, List<long>> _steers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Room, string Participant), SpawnHandle> _inFlight = new();
    // The two per-phase counters RunState reports, in memory because they have no durable meaning.
    // Keyed by run id only, not (run, phase): EnterPhase resets both on every accepted post, not only
    // on a tag change, so a stale count from an earlier phase never leaks into a later one.
    private readonly Dictionary<long, int> _refusalsThisPhase = new();
    private readonly Dictionary<long, int> _silencesThisPhase = new();
    // The instant each active run was last known busy (exchange open or something in flight), so
    // ArmWake arms the stall wake at "SpawnTimeout after that instant", not "SpawnTimeout after
    // ArmWake ran". The latter would let another room's activity (which re-runs ArmWake for every
    // room) push a stalled run's deadline forward forever. Cleared when the run is next seen busy or
    // stops being active (park/end).
    private readonly Dictionary<long, DateTimeOffset> _lastRunActivity = new();
    private readonly Dictionary<string, DateTimeOffset> _lastStart = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExchangeSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedCli> _clis = new(StringComparer.Ordinal);
    private CancellationTokenSource? _wake;
    // Worktree closes running off the loop thread, swept lazily in CloseIdleWorktrees and waited on
    // briefly at shutdown.
    private readonly List<Task> _closing = new();
    /// <summary>Every exchange that launched in a worktree and has not been handed to a close yet,
    /// whether or not it is still in its room's <see cref="_rooms"/> list (a run's <c>replaceAll</c>
    /// can drop one while it is still open - it stays here until it closes or the hub restarts).</summary>
    private readonly List<Exchange> _worktreeExchanges = new();
    /// <summary>Rooms with a worktree close running; a room-directory launch waits while its room is
    /// here.</summary>
    private readonly HashSet<string> _closingRooms = new(StringComparer.Ordinal);

    public SpawnerService(MessageStore store, IReadOnlyList<Participant> roster, MessageSignal signal, TokenStore tokens,
        IProcessRunner runner, ChopItUp.Hub.Hosting.HubOptions options, SpawnLimits limits, IServer server, IHubContext<RoomHub> hub,
        CliLocator cliLocator, MemoryStore memory, RoomTrails trails, ExchangeWorktrees worktrees, ParticipantStore participants, SkillStore skills, TimeProvider clock,
        RunStore runs, RunLimits runLimits)
    {
        _store = store; _roster = roster; _signal = signal; _tokens = tokens; _runner = runner;
        _options = options; _limits = limits; _server = server; _hub = hub; _locate = cliLocator; _memory = memory;
        _trails = trails; _worktrees = worktrees; _owner = roster.First(p => p.Id == participants.OwnerId());
        _participants = participants;
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
    /// finish themselves, not read off <c>_snapshots</c> (which <c>Publish</c> writes after the
    /// launch). A spawn process exists only inside this window, so a memory decision refused while it
    /// is true can never have come from one.</summary>
    public bool AnySpawnInFlight => Volatile.Read(ref _live) > 0 || Volatile.Read(ref _preparingPanels) > 0;

    /// <summary>The hub owner's stop for a room. When the room has an active or a parked run, this ends
    /// the run through <see cref="EndRun"/> whether or not an exchange is open (see
    /// <see cref="OnStop"/>, which decides). With no run it kills the room's in-flight spawns, drops
    /// the pending ones, closes the open exchange and posts the note. Returns the new snapshot, or
    /// null when there was nothing to stop (no run, no open exchange, no spawn running; the API turns
    /// that into a 409) or when the service is shutting down and the event cannot be queued.</summary>
    public async Task<ExchangeSnapshot?> StopAsync(string roomId)
    {
        var reply = new TaskCompletionSource<ExchangeSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_events.Writer.TryWrite(new StopEvent(roomId, reply))) return null;
        return await reply.Task;
    }

    /// <summary>The owner's stop for ONE exchange: kills that exchange's own in-flight spawns,
    /// closes it if it is still open, and leaves every other exchange in the room alone. Works on a
    /// closed exchange whose spawn is still running. Refused while a run is active or parked here: the
    /// run's own stop is the one control there.</summary>
    public async Task<(ExchangeStopOutcome Outcome, ExchangeSnapshot? Snapshot)> StopExchangeAsync(string roomId, long rootMessageId)
    {
        var reply = new TaskCompletionSource<(ExchangeStopOutcome, ExchangeSnapshot?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_events.Writer.TryWrite(new StopOneEvent(roomId, rootMessageId, reply))) return (ExchangeStopOutcome.NothingToStop, null);
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
        _modeShutdown.Cancel();
        _events.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
        foreach (var x in _rooms.Values.SelectMany(list => list)) x.ModeLeg?.PreparationCancellation.Cancel();
        _wake?.Cancel();
        _wake?.Dispose();
        _wake = null;
        foreach (var handle in _inFlight.Values)
        {
            try { handle.Cancel.Cancel(); } catch (ObjectDisposedException) { }   // the hub is going down; do not leave CLIs running
        }
        // Wait for the tree kills to land (they run on the launch tasks) so a Ctrl+C on a dev hub does
        // not leave a CLI posting into a room after the hub is gone.
        try { await Task.WhenAll(_inFlight.Values.Select(h => h.Run)).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { Console.Error.WriteLine("spawner: some spawns did not stop within 10 s of shutdown"); }
        // Worktree closes run off this loop entirely (Task.Run, not an _inFlight spawn), so they need
        // their own short wait here or a Ctrl+C could leave one mid-merge.
        try { await Task.WhenAll(_closing.ToList()).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { Console.Error.WriteLine("spawner: some worktree closes did not finish within 10 s of shutdown"); }
    }

    private void OnPosted(Message message) => _events.Writer.TryWrite(new PostedEvent(message));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Crash window: the hub may have died between the CLI exiting and the after-spawn commit.
            // A log line for the hub owner to look at, never a commit and never a note; the next spawn's
            // pre-commit sweeps it in as the hub owner.
            foreach (var room in _store.ListRooms(includeArchived: true))
            {
                if (room.Directory is null) continue;
                if (await _trails.For(room.Directory).IsDirtyAsync(stoppingToken))
                    Console.Error.WriteLine($"room {room.Id}: {room.Directory} has uncommitted changes at startup (a spawn may have ended without its commit); the next spawn commits them as the owner");
                // A previous hub process may have died with an exchange worktree still registered
                // (or its merge mid-flight): commit, remove and report before this room can launch.
                // Wrapped so one room's recovery failing never stops the host (this runs before
                // Guarded exists to help) or the recovery of the other rooms.
                try
                {
                    var recovered = await _worktrees.RecoverAsync(room.Directory, stoppingToken);
                    if (recovered is not null) PostNote(room.Id, recovered);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"room {room.Id}: worktree recovery at startup failed: {e.GetType().Name}: {e.Message}");
                }
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
    /// host (BackgroundServiceExceptionBehavior.StopHost, the default). A SQLITE_BUSY on a note, a bad
    /// room, a launch that throws: logged, and the loop goes on.</summary>
    private static void Guarded(Action step, string what)
    {
        try { step(); }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"spawner: {what} failed: {e.GetType().Name}: {e.Message}"); }
    }

    private void Handle(Event ev)
    {
        switch (ev)
        {
            case PostedEvent p: if (!_pendingAnnouncements.Remove(p.Message.Id)) OnMessage(p.Message); break;
            case InvokeEvent call: call.Invoke(); break;
            case PanelPreparedEvent ready: OnPanelPrepared(ready); break;
            case FinishedEvent f: OnFinished(f.Handle, f.Result, f.Trail); break;
            case StopEvent s:
                ExchangeSnapshot? reply = null;
                try { reply = OnStop(s.RoomId); }
                finally { s.Reply.TrySetResult(reply); }   // a throw here must not hang the HTTP caller
                break;
            case StopOneEvent one:
                (ExchangeStopOutcome, ExchangeSnapshot?) oneReply = (ExchangeStopOutcome.NothingToStop, null);
                try { oneReply = OnStopOne(one.RoomId, one.RootMessageId); }
                finally { one.Reply.TrySetResult(oneReply); }   // a throw here must not hang the HTTP caller
                break;
            case TickEvent: OnTick(); break;
            case WorktreeClosedEvent w:
                _closingRooms.Remove(w.RoomId);
                foreach (var x in ExchangesIn(w.RoomId)) x.WaitsForClose = false;
                if (w.Note is not null) PostNote(w.RoomId, w.Note);
                // A second closed exchange of the same room (queued behind this one) closes next; the
                // loop's LaunchDue/ArmWake pass right after Handle then launches anything that waited.
                CloseIdleWorktrees(w.RoomId);
                break;
        }
    }

    /// <summary>Hands each closed, idle worktree exchange of <paramref name="roomId"/> to
    /// <see cref="ExchangeWorktrees.CloseAsync"/>, one at a time per room, off the loop thread. Does
    /// nothing (a later call from <see cref="OnFinished"/>, <see cref="Publish"/> or
    /// <see cref="AddExchange"/> retries) while a close of this room already runs, or while any spawn
    /// is in flight in the room directory itself (its before-commit must never race a close's own
    /// commit of the same tree).</summary>
    private void CloseIdleWorktrees(string roomId)
    {
        if (_closingRooms.Contains(roomId)) return;
        if (_inFlight.Values.Any(h => h.Request.RoomId == roomId && h.Directory is not null && !h.InWorktree)) return;
        var x = _worktreeExchanges.FirstOrDefault(e => e.RoomId == roomId && e.Status != ExchangeStatus.Open && e.InFlight.Count == 0);
        if (x is null) return;
        _worktreeExchanges.Remove(x);
        _closingRooms.Add(roomId);
        var runOwns = _runs.Active(roomId) is not null || _runs.Latest(roomId) is { Status: RunStatus.Parked };
        var (dir, root, status, leased, interrupted) = (x.WorktreeRoom!, x.RootMessageId, x.Status, x.WorktreeLeased, x.Interrupted);
        _closing.RemoveAll(t => t.IsCompleted);
        _closing.Add(Task.Run(async () =>
        {
            string? note;
            try
            {
                note = await _worktrees.CloseAsync(new ExchangeWorktrees.CloseRequest(dir, root, roomId, status, leased, interrupted, runOwns,
                    RoomCommits.OwnerMessage(_owner, roomId)), CancellationToken.None);
            }
            catch (Exception e) { note = $"Exchange #{root}: its worktree could not be closed: {e.GetType().Name}: {e.Message}"; }
            // The event channel closes once the hub starts shutting down, and this close may still be
            // running past that point; without this the note it carries would be dropped unrecorded.
            if (!_events.Writer.TryWrite(new WorktreeClosedEvent(roomId, note)) && note is not null)
                Console.Error.WriteLine($"room {roomId}: the hub was shutting down; a worktree close finished with a note that was not posted: {note}");
        }));
    }

    /// <summary>The periodic wake ArmWake arms (the wall-clock deadline, or the bounded stall wake)
    /// fires as a bare <see cref="TickEvent"/> with no room of its own, so unlike the steer branch's
    /// single-run <c>DriveRun(runRow, Tick)</c> this checks every active run: one shared timer can be
    /// the earliest deadline for any of them. <see cref="RunPolicy"/>'s hard-cap checks (first, for
    /// every event) make this useful: a busy run gets <see cref="RunDecision.Nothing"/> unless it has
    /// spent a hard cap, in which case this is what parks it. Without a Tick reaching a busy capped
    /// run, the stall park and the wall-clock park while idle would be unreachable.</summary>
    private void OnTick()
    {
        foreach (var run in _runs.ListActive()) DriveRun(run, new RunEvent.Tick());
    }

    private void OnMessage(Message m)
    {
        if (!RememberSignal(m.Id)) return;
        // An imported turn is history. It was stored and announced like any message (browsers,
        // wait_for_message), but nothing in it is addressed to anyone now: no mention, skill, /stop or
        // steer inside it reaches a run or the policy. Decided here, at the loop's one message entry,
        // ahead of every branch below.
        if (m.Imported) return;
        if (OnModeMessage(m)) return;
        if (_roster.Any(p => p.Id == m.AuthorId && p.Kind == "human") && GoverningCommand.TryParse(m.Body, out var contextCommand))
        {
            var action = contextCommand.Text.Length == 0 ? "cleared" : "set";
            PostNote(m.RoomId, $"Governing {contextCommand.Slot} {action} by message #{m.Id}. "
                + (contextCommand.Slot == "objective" ? "Earlier objective and correction superseded. " : "Earlier correction superseded. ")
                + "Applies to future launches; no participant was spawned.");
            return;
        }
        var exchanges = ExchangesIn(m.RoomId);
        var newest = Newest(m.RoomId);
        var activeRun = _runs.Active(m.RoomId);
        // A spawn's post belongs to the exchange that launched it. Outside a run its mentions count
        // while that exchange is open and still held for the room, so two side-by-side exchanges never
        // feed each other and a run exchange dropped when the run moved on stays mute after the run
        // ends. Inside a run the room keeps one current exchange (the newest): a conductor's later
        // post, after it rooted a workers exchange, is prose.
        bool acceptMentions = true;
        Exchange? target;
        if (_inFlight.TryGetValue((m.RoomId, m.AuthorId), out var handle))
        {
            handle.Posted = true;
            handle.Exchange.MessageIds.Add(m.Id);
            target = handle.Exchange;
            // A synthesis spawn's post lands and is a member (it is still the exchange's last model
            // post), but hands nothing on: it is the addressee's own wrap-up, not a fresh hand-off.
            acceptMentions = handle.Request.Reason != SpawnReason.Synthesis && (activeRun is null
                ? handle.Exchange.Status == ExchangeStatus.Open && exchanges.Contains(handle.Exchange)
                : ReferenceEquals(handle.Exchange, newest));
        }
        else target = activeRun is not null ? newest : AppBackedTarget(m.RoomId, m);
        // OnMessage is the run-start site (_runs.Start reads this same instant), so it goes through
        // the injected clock; LaunchDue/ArmWake stay on the real wall clock.
        var now = _clock.GetUtcNow();
        var skill = ResolveSkill(m);
        var startsRun = skill is SkillResolution.Found found && found.Skill.IsRun;
        var run = activeRun is not null ? new RunContext(activeRun.Id, activeRun.ConductorId, activeRun.Phase) : null;

        // The conductor's own post, while its exchange is still this room's current one
        // (acceptMentions; a later post from the same spawn, after it has rooted, is prose and falls
        // through to the ordinary branches below, never refused). Bypasses ExchangePolicy.OnMessage's
        // model branch entirely: that method knows nothing about phases or classes, and every run
        // decision stays inside RunPolicy.
        if (activeRun is not null && acceptMentions && m.AuthorId == activeRun.ConductorId)
        {
            HandleConductorPost(activeRun, m, now);
            return;
        }

        // Step 1 of the ordered human branch, checked before anything below can post a note or touch
        // a run's status. Ends an active or parked run outright through the same RunPolicy.Decide path
        // every other transition takes, never resuming it first (RunPolicy's hard-cap checks are
        // skipped for StopRequested, so a hard-capped park gets End, not Park). With no run in this
        // room there is nothing to stop; falling through to ResolveSkill would read as "no skill named
        // '/stop'", which names the wrong problem, so a dedicated note says so directly.
        if (RunCommands.IsStop(m.Body) && _roster.FirstOrDefault(p => p.Id == m.AuthorId)?.Kind == "human")
        {
            var stoppable = activeRun ?? (_runs.Latest(m.RoomId) is { Status: RunStatus.Parked } parkedForStop ? parkedForStop : null);
            if (stoppable is not null)
            {
                DriveRun(stoppable, new RunEvent.StopRequested());
                return;
            }
            PostNote(m.RoomId, "Nothing to stop: no run is active or parked in this room.");
            return;
        }

        // Steps 2-3 of the ordered human branch: these two refusals need RunStore.Latest, which the
        // pure ExchangePolicy never reads, so they are decided here before the policy is consulted. A
        // run-start never lands on top of an active or a parked run (ux_runs_one_active_per_room only
        // guards 'active'; a second Start against a parked room would leave two run rows for one room).
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

        // A human post that reaches here has no active run in this room (the conductor-post branch
        // only fires when one exists, and startsRun's checks above already returned for an
        // active-or-parked room) and is not itself a run-start. If the room's most recent run is
        // parked, this is a resume, decided through RunPolicy.Decide like every other transition.
        // RunPolicy.Decide's hard-cap checks apply only to an Active run (a parked run cannot trip a
        // cap again), and RunStore.ActiveElapsed freezes while parked. CarryOut resumes (folding
        // parked_seconds) only when the decision is OpenConductor; a Refuse posts the line and does
        // nothing else.
        if (activeRun is null && !startsRun && _roster.FirstOrDefault(p => p.Id == m.AuthorId)?.Kind == "human")
        {
            var latestForResume = _runs.Latest(m.RoomId);
            if (latestForResume is { Status: RunStatus.Parked } parkedRun)
            {
                DriveRun(parkedRun, new RunEvent.HumanPosted(m.Id));
                return;
            }
        }

        // A /continue post never reaches the policy's message path (it would resolve as an unknown
        // skill). Decided here, after the run branches above: a parked run resumes on it like on any
        // human post, an active run refuses it, and outside runs it targets the exchange the message
        // replies to, else the last owner-rooted one in room order (the room list is what Reopen
        // moves a reopened exchange to the end of; the remembered list only knows the opening order).
        if (ExchangeCommands.IsContinue(m.Body) && _roster.FirstOrDefault(p => p.Id == m.AuthorId)?.Kind == "human")
        {
            if (activeRun is not null)
            {
                PostNote(m.RoomId, $"A run is active in this room (#{activeRun.Id}); /continue applies to plain exchanges.");
                return;
            }
            Exchange? continued;
            if (m.ReplyToId is { } continueOf)
            {
                continued = JoinableFor(m.RoomId, continueOf);
                if (continued is null)
                {
                    PostNote(m.RoomId, $"Reply to #{continueOf}: that message is in no exchange this hub still holds, so there is nothing to continue.");
                    return;
                }
            }
            else continued = ExchangesIn(m.RoomId).LastOrDefault(x => x.Joinable)
                ?? (_joinable.TryGetValue(m.RoomId, out var remembered) ? remembered.LastOrDefault() : null);
            if (continued is null)
            {
                PostNote(m.RoomId, "Nothing to continue in this room: no exchange this hub remembers.");
                return;
            }
            var (outcome, continueNotes) = _policy.Continue(continued, m, now);
            if (outcome == ContinueOutcome.Continued) Reopen(m.RoomId, continued);
            foreach (var note in continueNotes) PostNote(m.RoomId, note);
            Publish(m.RoomId);
            return;
        }

        var hasDirectory = _store.GetRoom(m.RoomId)?.Directory is not null;
        var hadExchanges = exchanges.Count > 0;
        var joins = m.ReplyToId is { } replyTo ? JoinableFor(m.RoomId, replyTo) : null;
        var joinedClosed = joins is not null && joins.Status != ExchangeStatus.Open;
        var (opened, notes) = _policy.OnRoomMessage(exchanges, target, m, now, acceptMentions, skill, run, startsRun, hasDirectory, joins);
        if (opened is not null)
        {
            AddExchange(m.RoomId, opened);
            if (!startsRun) Remember(m.RoomId, opened);
        }
        var reopened = joinedClosed && joins!.Status == ExchangeStatus.Open;
        if (reopened) Reopen(m.RoomId, joins!);
        foreach (var note in notes) PostNote(m.RoomId, note);
        if (opened is not null || hadExchanges || reopened) Publish(m.RoomId);

        if (startsRun && opened is not null)
        {
            // The policy just built the conductor's first exchange (a directory, a resolved skill,
            // exactly one mention). Persist the run itself.
            var conductorId = opened.Pending.Keys.Single();
            var skillFound = (SkillResolution.Found)skill!;
            var runRow = _runs.Start(m.RoomId, conductorId, skillFound.Skill.Name, skillFound.Arguments, m.Id, now);
            _runs.CountExchange(runRow.Id);
            PostNote(m.RoomId, $"Run #{runRow.Id} started: @{conductorId} conducts; caps {_runLimits.Spawns} spawns, "
                + $"{_runLimits.WallClock.TotalHours:0}h active time, phase re-entry {_runLimits.PhaseEntries}.");
            return;
        }

        // A human post inside an active run that did not start a new one is a steer. ExchangePolicy
        // already left the room's exchanges untouched above; this is the impure half: record it, tell
        // the hub owner, and if the conductor is idle, wake it now.
        if (run is not null && _roster.FirstOrDefault(p => p.Id == m.AuthorId)?.Kind == "human")
        {
            var runRow = activeRun!;
            var pending = _steers.TryGetValue(m.RoomId, out var list) ? list : (_steers[m.RoomId] = new List<long>());
            pending.Add(m.Id);
            var state = AssembleRunState(runRow);
            // Nothing while active. Routed through the policy anyway rather than assumed by the
            // service, even though the only reachable outcome here is a no-op.
            CarryOut(runRow, _runPolicy.Decide(state, new RunEvent.HumanPosted(m.Id), pending));
            PostNote(m.RoomId, $"Steer noted; @{run.ConductorId} is given it when the current exchange concludes.");
            if (!state.ExchangeOpen && !state.AnythingInFlight) DriveRun(runRow, new RunEvent.Tick());
        }
    }

    /// <summary>Everything <see cref="RunPolicy"/> needs to know about <paramref name="run"/> right
    /// now, assembled from the store and the loop's own in-memory state.
    /// <see cref="RunState.RefusalsThisPhase"/> is written by <see cref="HandleConductorPost"/> and
    /// <see cref="RunState.SilencesThisPhase"/> by <see cref="HandleSpawnSilent"/>. Both counters
    /// share one reset rule (EnterPhase clears them together).</summary>
    private RunState AssembleRunState(Run run) => new(
        run.Id, run.RoomId, run.ConductorId, run.Status, run.CapSpent,
        run.Phase, _runs.PhaseEntries(run.Id),
        run.SpawnsUsed, run.Exchanges, RunStore.ActiveElapsed(run, _clock.GetUtcNow()),
        RefusalsThisPhase: _refusalsThisPhase.GetValueOrDefault(run.Id), SilencesThisPhase: _silencesThisPhase.GetValueOrDefault(run.Id),
        ExchangeOpen: Newest(run.RoomId) is { Status: ExchangeStatus.Open },
        AnythingInFlight: InFlightIn(run.RoomId).Count > 0,
        RootMessageId: run.RootMessageId);

    /// <summary>What the prompt shows about the run a spawn is launched inside. Read fresh at every
    /// launch, never cached, so a re-spawned conductor sees the counters as they stand now.</summary>
    private RunView BuildRunView(Run run, string participantId, DateTimeOffset now) => new(
        run.Id, run.ConductorId, participantId == run.ConductorId, run.SkillName, run.Arguments,
        run.Phase, _runs.PhaseEntries(run.Id).GetValueOrDefault(run.Phase), _runLimits.PhaseEntries,
        run.Exchanges, run.SpawnsUsed, _runLimits.Spawns,
        RunStore.ActiveElapsed(run, now), _runLimits.WallClock,
        _runs.Artifacts(run.Id), _runs.GateRuns(run.Id));

    /// <summary>Assembles the state, asks <see cref="RunPolicy"/>, carries the decision out (never
    /// decided here) and drains this room's pending steers when the decision consumed them.</summary>
    private void DriveRun(Run run, RunEvent ev)
    {
        var pending = _steers.TryGetValue(run.RoomId, out var list) ? list : new List<long>();
        var decision = _runPolicy.Decide(AssembleRunState(run), ev, pending);
        CarryOut(run, decision);
        if (decision is RunDecision.OpenConductor) pending.Clear();
    }

    /// <summary>What <see cref="RunPolicy"/> decided, carried out.
    ///
    /// <see cref="RunDecision.OpenConductor"/> resumes first when <paramref name="run"/> is still
    /// parked: <see cref="RunStore.Resume"/> folds the whole parked interval into
    /// <c>parked_seconds</c> and flips the row to active before the exchange opens. Every other
    /// caller of this arm already holds an active run, so the check is a no-op for them.</summary>
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
                // "Ask it once more" needs nothing further here: the conductor's own exchange is
                // still open with it in flight (a refusal never touches _rooms), so when its spawn
                // exits, the ExchangeConcluded handling re-spawns it with this note as the trigger.
                PostNote(run.RoomId, ra.Note);
                break;
            case RunDecision.Refuse rf:
                // Post the line and do nothing else: never resumed.
                PostNote(run.RoomId, rf.Note);
                break;
            case RunDecision.Park park:
                ParkRun(run, park.Reason, park.CapSpent);
                break;
            case RunDecision.End end:
                EndRun(run, end.Reason, end.Cause);
                break;
            default:
                throw new NotSupportedException($"RunDecision {decision.GetType().Name} is not wired yet (row19-runs, a later task).");
        }
    }

    /// <summary>The conductor's own post, raised while its exchange is still the room's current one
    /// (acceptMentions, the same distinction <see cref="OnMessage"/> draws at its top, is exactly
    /// "the first phase-tagged post of a conductor spawn"; a later post from the same spawn is prose
    /// and falls through to the ordinary model branch, never refused). Checked against the class
    /// rules (<see cref="ExchangePolicy.RefuseConductorPost"/>, pure) and raised to
    /// <see cref="RunPolicy"/> as <see cref="RunEvent.ConductorPosted"/>; the decision is carried out.
    /// The refusal counter is written here: the policy only reads it.</summary>
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
            // Accepted (including ping): count a phase entry and reset both counters (the silence
            // path re-enters the same tag, so a change-only reset would leave it latched).
            _runs.EnterPhase(run.Id, tag!.ToString(), now);
            _refusalsThisPhase[run.Id] = 0;
            _silencesThisPhase[run.Id] = 0;
        }

        CarryOut(run, decision);
    }

    /// <summary>The run's conductor finished without posting. Every silence asks again with the same
    /// triggers (<paramref name="triggerIds"/>), but the bookkeeping differs: the first silence in a
    /// phase costs nothing, and every one after that also counts a phase entry for this ask, which is
    /// what lets the phase's entry cap eventually park the run instead of asking forever. The
    /// counter is written here, since RunPolicy only reads it. <see cref="AssembleRunState"/>'s
    /// <c>SilencesThisPhase</c> is read once, before the increment, so <see cref="RunPolicy.Decide"/>
    /// sees how many silences happened before this one (the same order
    /// <see cref="HandleConductorPost"/> uses for the refusal counter).</summary>
    private void HandleSpawnSilent(Run run, string participantId, IReadOnlyList<long> triggerIds)
    {
        var pending = _steers.TryGetValue(run.RoomId, out var list) ? list : new List<long>();
        var state = AssembleRunState(run);
        var decision = _runPolicy.Decide(state, new RunEvent.SpawnSilent(participantId, triggerIds), pending);

        // Count a phase entry only past the first silence, and only when the run is actually being
        // asked again (the hard caps still win first, and a Park needs no phase entry).
        if (state.SilencesThisPhase >= 1 && decision is RunDecision.OpenConductor)
            _runs.EnterPhase(run.Id, run.Phase, _clock.GetUtcNow());
        _silencesThisPhase[run.Id] = state.SilencesThisPhase + 1;

        CarryOut(run, decision);
    }

    /// <summary>The conductor's post passed every class rule and asks for work: rooted at that post
    /// (<see cref="RunDecision.OpenWorkers.RootMessageId"/>), never at the conductor's own re-spawn
    /// root.</summary>
    private void OpenWorkersExchange(Run run, RunDecision.OpenWorkers ow)
    {
        var (exchange, notes) = _policy.OpenForWorkers(run.RoomId, ow.RootMessageId, ow.Mentioned, _clock.GetUtcNow());
        AddExchange(run.RoomId, exchange, replaceAll: true);
        _runs.CountExchange(run.Id);
        foreach (var note in notes) PostNote(run.RoomId, note);
        Publish(run.RoomId);
    }

    /// <summary>Writes the parked row, stops the room's open exchange and cancels its in-flight spawns
    /// (a park that touched neither would let an in-flight conductor's later post resolve
    /// <c>run == null</c> and take the plain model branch, launching more spawns from a parked run),
    /// posts the note naming the reason and @owner, and publishes. Every cap reaches this through the
    /// one <see cref="RunDecision.Park"/> arm in <see cref="CarryOut"/>.</summary>
    private void ParkRun(Run run, string reason, bool capSpent)
    {
        var now = _clock.GetUtcNow();
        _runs.Park(run.Id, reason, capSpent, now);
        if (Newest(run.RoomId) is { Status: ExchangeStatus.Open } x)
            PostNote(run.RoomId, ExchangePolicy.Stop(x, ExchangeStopCause.Run));
        foreach (var handle in _inFlight.Values.Where(h => h.Request.RoomId == run.RoomId).ToList())
            handle.Cancel.Cancel();
        PostNote(run.RoomId, $"Run #{run.Id} parked: {reason}. @{_owner.Id}");
        Publish(run.RoomId);
    }

    /// <summary>Ends a run, active or parked, whether or not an exchange is open or anything is in
    /// flight, through the one path <see cref="RunDecision.End"/> reaches (the text `/stop`, and the
    /// API/button stop via <see cref="OnStop"/>). Mirrors <see cref="ParkRun"/> (stop the open
    /// exchange, cancel the room's in-flight spawns) but also clears the room's pending steers and
    /// posts one note naming how much of each cap the run used. <see cref="RunStore.ActiveElapsed"/>
    /// is read against the same instant <see cref="RunStore.End"/> stamps, and against the run as it
    /// stood before ending (an ended run is never <see cref="RunStatus.Parked"/>, so it falls to that
    /// method's "not parked" arm: elapsed time since start, minus whatever was already parked).</summary>
    private void EndRun(Run run, string reason, ExchangeStopCause cause)
    {
        var now = _clock.GetUtcNow();
        var ended = _runs.End(run.Id, reason, now);
        if (Newest(run.RoomId) is { Status: ExchangeStatus.Open } x)
            PostNote(run.RoomId, ExchangePolicy.Stop(x, cause));
        foreach (var handle in _inFlight.Values.Where(h => h.Request.RoomId == run.RoomId).ToList())
            handle.Cancel.Cancel();
        _steers.Remove(run.RoomId);
        var elapsed = RunStore.ActiveElapsed(ended, now);
        var phaseEntries = _runs.PhaseEntries(ended.Id).GetValueOrDefault(ended.Phase);
        PostNote(run.RoomId, $"Run #{run.Id} ended: {reason}. Used {ended.SpawnsUsed}/{_runLimits.Spawns} spawns, "
            + $"{elapsed.TotalHours:0.0}/{_runLimits.WallClock.TotalHours:0.0}h active time, phase '{ended.Phase}' entered {phaseEntries}/{_runLimits.PhaseEntries} time(s).");
        Publish(run.RoomId);
    }

    /// <summary>Whether <paramref name="path"/> is present in the room's own directory tree, which
    /// satisfies "recorded or in the room tree" for an artifact nothing has recorded yet. Normalizes
    /// with the same <see cref="RunStore.Normalize"/> the authorship lookup uses, so the two checks
    /// never disagree on one path. A room with no directory, an absolute path, or one that walks
    /// above the room root, is never "in the tree".</summary>
    private bool ArtifactInRoomTree(Run run, string path)
    {
        var directory = _store.GetRoom(run.RoomId)?.Directory;
        if (directory is null) return false;
        var normalized = RunStore.Normalize(path);
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Contains("..")) return false;
        return File.Exists(Path.GetFullPath(Path.Combine(directory, normalized)));
    }

    /// <summary>The hub re-spawning its run's conductor. No message roots this, so
    /// <see cref="ExchangePolicy.OnMessage"/>'s human-only rule is untouched. Re-resolves the skill by
    /// <see cref="Run.SkillName"/> at every launch, honouring the hash pin: a skill that no longer
    /// matches what was imported parks the run rather than continuing with no instruction.</summary>
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
        AddExchange(run.RoomId, exchange, replaceAll: true);
        _runs.CountExchange(run.Id);
        Publish(run.RoomId);
        // Launching is left to LaunchDue on the next pass, never inline: a directory room's
        // one-spawn-at-a-time exclusivity must not be bypassed.
    }

    /// <summary>What the message's first line asks for, if anything. Only a human's post is ever
    /// resolved (a model's `/whatever` is prose). The policy itself never touches the filesystem, so
    /// this is the one place skill I/O happens.
    ///
    /// Safe on this loop's single thread: <see cref="SkillStore.Read"/> refuses on file length before
    /// reading a byte (<c>MaxSkillFileBytes</c>), so a local read plus one SHA-256 of at most 1 MB is
    /// sub-millisecond, once per exchange root. That bound (and the same one in <c>List</c>) is what
    /// makes this placement correct: without it, one oversized file in a writable store would stall
    /// every room's exchange handling and the owner's stop button. Do not remove either guard as
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
            // Deliberately broad, and deliberately not a degrade to Nothing, which would open a normal
            // exchange and spend model calls on a skill-less prompt, silently. A narrow catch fails
            // the other way: anything it misses escapes to Guarded, which swallows the whole
            // PostedEvent, so no exchange opens, no note posts, the supersede never happens, and an
            // open exchange stays Open in _rooms accepting model posts.
            return new SkillResolution.Unavailable(invocation.Name, e.Message);
        }
    }

    /// <summary>Whether an exchange of <paramref name="roomId"/> launches in its own worktree rather
    /// than the room directory itself: outside a run (no run active or parked here), in a room bound
    /// to a directory. When true, exclusivity narrows to the exchange's own in-flight set
    /// (<see cref="ExchangePolicy.Due"/>'s <c>exclusiveOver</c>) so side-by-side exchanges each still
    /// run one spawn at a time within themselves; a run keeps the whole room exclusive.</summary>
    private bool UsesWorktrees(string roomId, string? directory) =>
        directory is not null && _runs.Active(roomId) is null && _runs.Latest(roomId) is not { Status: RunStatus.Parked };

    private void LaunchDue()
    {
        var now = _clock.GetUtcNow();
        foreach (var x in _rooms.Values.SelectMany(list => list).ToList())
        {
            if (x.Status != ExchangeStatus.Open) continue;
            var inRoom = InFlightIn(x.RoomId);
            var directory = _store.GetRoom(x.RoomId)?.Directory;
            if (x.ModeLeg is { Preparing: true }) continue;
            var exclusive = directory is not null && x.ModeLeg?.Settings.Mode != "panel";
            var over = UsesWorktrees(x.RoomId, directory) ? x.InFlight : null;
            // A worktree launch still waits while a spawn is in flight in the room directory itself
            // (a run just ended or stopped, its cancelled conductor not yet finished); otherwise the
            // worktree launch's before-commit would commit that spawn's in-progress edits as the hub owner's.
            if (over is not null && _inFlight.Values.Any(h => h.Request.RoomId == x.RoomId && h.Directory is not null && !h.InWorktree)) continue;
            // A room-directory launch (over is null) waits while a worktree close of this room is
            // running: both write the same tree. A worktree launch never waits on a close: it only
            // touches its own worktree and the gated owner commit.
            if (exclusive && over is null && _closingRooms.Contains(x.RoomId)) continue;
            if (x.WaitsForClose) continue;
            var due = _policy.Due(x, now, _lastStart, inRoom, exclusive, over);
            foreach (var request in due) Launch(x, request, now);
            if (due.Count > 0) Publish(x.RoomId);
        }
    }

    private HashSet<string> InFlightIn(string roomId) =>
        _inFlight.Keys.Where(k => k.Room == roomId).Select(k => k.Participant).ToHashSet(StringComparer.Ordinal);

    private IReadOnlyList<Exchange> ExchangesIn(string roomId) =>
        _rooms.TryGetValue(roomId, out var list) ? list : [];

    /// <summary>The room's most recently opened exchange: what a run means by the room's current
    /// exchange, and what the snapshot's top-level fields describe.</summary>
    private Exchange? Newest(string roomId) =>
        _rooms.TryGetValue(roomId, out var list) && list.Count > 0 ? list[^1] : null;

    /// <summary>Appends <paramref name="x"/> and drops every exchange that is closed with nothing left
    /// in flight, so a closed exchange stays visible until the next one opens. <paramref name="replaceAll"/>
    /// is for the run's own exchanges: a run room holds exactly one exchange, so an older run exchange
    /// still open with queued entries is never launched, and its spawn's later mentions count for
    /// nothing (it is not in the room's list).</summary>
    private void AddExchange(string roomId, Exchange x, bool replaceAll = false)
    {
        MaintainModeResources(roomId);
        if (!_rooms.TryGetValue(roomId, out var list)) _rooms[roomId] = list = new List<Exchange>();
        // Give an already-idle worktree exchange a last chance to close before it is dropped
        // (replaceAll) or pruned: closing never depends on _rooms, but a room with nothing left open
        // never otherwise revisits CloseIdleWorktrees.
        CloseIdleWorktrees(roomId);
        if (replaceAll) list.Clear();
        else list.RemoveAll(e => e.Status != ExchangeStatus.Open && e.InFlight.Count == 0);
        list.Add(x);
    }

    /// <summary>What the snapshot's top-level fields describe: the newest OPEN exchange, else the newest
    /// one. The shipped exchange bar reads only those fields, so a concluded newest exchange must not
    /// hide an older one that is still running. A reopened exchange counts as the newest.</summary>
    private Exchange? Displayed(string roomId) =>
        ExchangesIn(roomId).LastOrDefault(e => e.Status == ExchangeStatus.Open) ?? Newest(roomId);

    /// <summary>Where an app-backed model's post goes (no spawn handle): the newest open exchange that
    /// involves a model it mentions, else the newest open exchange, else none.</summary>
    private Exchange? AppBackedTarget(string roomId, Message m)
    {
        var open = ExchangesIn(roomId).Where(e => e.Status == ExchangeStatus.Open).Reverse().ToList();
        var mentioned = _policy.ReferencedSpawnable(m);
        return open.FirstOrDefault(e => mentioned.Any(e.Participants.Contains)) ?? open.FirstOrDefault();
    }

    /// <summary>The exchange of <paramref name="roomId"/> that holds <paramref name="messageId"/>:
    /// the room's own list first (so an exchange still held there is found even after it left the
    /// remembered window), then the remembered ones; null when none does (a hub note, one older than the
    /// last <see cref="JoinableKept"/>, one a previous hub process held). A run's exchange can be returned;
    /// the policy refuses to join it (<see cref="Exchange.Joinable"/>).</summary>
    private Exchange? JoinableFor(string roomId, long messageId) =>
        ExchangesIn(roomId).LastOrDefault(x => x.MessageIds.Contains(messageId))
        ?? (_joinable.TryGetValue(roomId, out var list) ? list.LastOrDefault(x => x.MessageIds.Contains(messageId)) : null);

    private void Remember(string roomId, Exchange x)
    {
        if (!_joinable.TryGetValue(roomId, out var list)) _joinable[roomId] = list = new List<Exchange>();
        list.Add(x);
        if (list.Count > JoinableKept) list.RemoveAt(0);
    }

    /// <summary>A closed exchange an owner reply just reopened becomes the room's newest entry
    /// again (AddExchange may have pruned it). Its interrupted mark is dropped: the owner chose to continue.
    /// If its worktree was already handed to a close, that bookkeeping starts over: the next launch leases
    /// <c>chopitup/x&lt;root&gt;</c> again, continuing a kept branch only when this exchange really leased it
    /// (a refused first lease never adopts someone else's branch), and while a close is still running in
    /// the room (it may be this exchange's own) the launch waits for it.</summary>
    private void Reopen(string roomId, Exchange x)
    {
        if (!_rooms.TryGetValue(roomId, out var list)) _rooms[roomId] = list = new List<Exchange>();
        list.Remove(x);
        list.Add(x);
        x.Interrupted = false;                         // the owner chose to continue from whatever state it left
        if (_worktreeExchanges.Contains(x)) return;    // never handed to a close: its worktree is still its own
        x.ContinuesBranch = x.ContinuesBranch || x.WorktreeLeased;   // only a branch this exchange really leased
        x.WorktreeRoom = null;
        x.WorktreeLeased = false;
        x.WaitsForClose = _closingRooms.Contains(roomId);
    }

    private void Launch(Exchange x, SpawnRequest request, DateTimeOffset now)
    {
        if (x.ModeLeg?.Settings.Mode == "panel") { LaunchPanel(x, request, now); return; }
        var participant = _roster.First(p => p.Id == request.ParticipantId);
        var spawnId = $"{request.RoomId}-{request.RootMessageId}-{request.TurnNumber}-{Guid.NewGuid().ToString("N")[..8]}";
        var workDir = Path.GetFullPath(Path.Combine(_options.DataDir, "spawns", spawnId));
        ExchangePolicy.Started(x, request);
        _lastStart[participant.Id] = now;
        var activeRun = _runs.Active(request.RoomId);
        // A conductor thinks harder about its own loop, and a judge about what it is asked to judge;
        // everyone else, and anything outside a run, gets no effort flag rather than an explicit
        // default. Never xhigh or max. EffortPolicy owns the rule so the Roles dialog shows the same
        // one this site applies.
        var effort = EffortPolicy.AtLaunch(participant, inRun: activeRun is not null, conductor: activeRun is not null && participant.Id == activeRun.ConductorId);
        try
        {
            // Counted before anything below can throw: a spawn that fails even to start (a bad
            // token, a directory that vanished) still used one of the run's spawns.
            if (activeRun is not null) _runs.CountSpawn(activeRun.Id);
            var token = _tokens.BearerFor(participant.Id);
            Directory.CreateDirectory(workDir);
            var core = _memory.ReadCore();
            var room = _store.GetRoom(request.RoomId);
            var directory = room?.Directory;
            // Capture the same selected limit for the runner and its timeout note. An active run
            // always wins, even when its room has a directory.
            var timeout = activeRun is not null ? _runLimits.SpawnTimeout
                : directory is not null ? _limits.EffectiveOutsideDirectoryTimeout : _limits.Timeout;
            // Outside a run, a directory room's spawn edits its exchange's own worktree, not the room
            // directory itself. LaunchDue's exclusiveOver uses the same test (UsesWorktrees), so the
            // two must never disagree.
            var inWorktree = UsesWorktrees(request.RoomId, directory);
            var tree = inWorktree ? ExchangeWorktrees.PathFor(directory!, x.RootMessageId) : directory;
            if (inWorktree)
            {
                x.WorktreeRoom = directory;
                // Registered once, at its first worktree launch. CloseIdleWorktrees reads this list,
                // not _rooms, so an exchange a run's replaceAll later drops stays closable.
                if (!_worktreeExchanges.Contains(x)) _worktreeExchanges.Add(x);
            }
            // A directory room's spawn also gets the room's own topic, cut at RoomChars. A room id
            // that is not a slug (the table has no CHECK) gets no section, not no spawn.
            RoomMemory? roomMemory = null;
            if (directory is not null && MemoryStore.TopicSlug.IsMatch(MemoryStore.RoomTopic(request.RoomId)))
            {
                var roomTopic = MemoryStore.RoomTopic(request.RoomId);
                var text = _memory.ReadTopic(roomTopic, MemoryStore.RoomChars);
                roomMemory = new RoomMemory(roomTopic, text?.Text ?? "", text?.Truncated ?? false);
            }
            var runView = activeRun is not null ? BuildRunView(activeRun, participant.Id, _clock.GetUtcNow()) : null;
            // Read fresh at launch, not from the startup-static _roster, so an owner edit through the
            // API takes effect on the next spawn with no hub restart.
            var standing = new SpawnPrompt.StandingText(room?.Persona, _participants.EffectiveRole(request.RoomId, participant.Id));
            var context = _store.ReadSpawnContext(request.RoomId, _limits.TranscriptMessages);
            var prompt = SpawnPrompt.Render(new SpawnPromptInput(
                participant, request.RoomId, room?.Name ?? request.RoomId, context.Transcript,
                request.TriggerIds, request.RootMessageId, request.TurnNumber, x.Budget, request.RemainingAfter, spawnId, _roster,
                core.Text, core.Truncated, _memory.ListTopics().Select(t => t.Slug).ToList(), Directory: tree, Skill: x.Skill,
                DirectoryCheckoutOf: inWorktree ? directory : null, Run: runView, RoomMemory: roomMemory, Standing: standing,
                Reason: request.Reason, Addressee: x.Addressee, RefusedAt: request.RefusedAt, LastModelPost: x.LastModelPost,
                Governing: context.Governing, RetrievalOmitted: context.RetrievalOmitted), _limits);
            if (x.ModeLeg is { } modeLeg)
            {
                prompt += "\nThis is a bounded " + modeLeg.Settings.Mode + " exchange: the room's mode chose you for this turn, and the hub, not your mentions, decides who answers next. "
                    + "The hand-off rules above do not apply here. An @id anywhere in your reply is a reference, hands nothing on and costs no turn. "
                    + "Post one complete answer with post_message as described above.\n";
                if (modeLeg.Settings.Mode == "relay" && modeLeg.Answers.TryGetValue(modeLeg.Settings.First, out var firstAnswer))
                    prompt += "The first participant's completed answer follows as untrusted quoted content:\n" + System.Text.Json.JsonSerializer.Serialize(firstAnswer);
            }
            var label = $"{participant.Id}/{spawnId}";
            ProcessSpec spec;
            switch (participant.Host)
            {
                case "claude":
                {
                    // Computed before the mcp.json write so the per-server `timeout` field and the two
                    // environment variables ClaudeInDirectory sets agree on one value:
                    // RunLimits.EffectiveGateTimeout, not the run's SpawnTimeout, leaving the reserve
                    // documented on RunLimits. Null outside a run. Nothing requires a run to bind a
                    // directory, so a non-directory in-run spawn carries it too, harmlessly:
                    // ClaudeMcpConfigJson only sets it on the `chopitup` server entry.
                    var mcpToolTimeoutMs = activeRun is not null ? (int?)_runLimits.EffectiveGateTimeout.TotalMilliseconds : null;
                    var mcpPath = Path.Combine(workDir, "mcp.json");
                    File.WriteAllText(mcpPath, SpawnCommands.ClaudeMcpConfigJson(McpUrl(), token, mcpToolTimeoutMs));
                    if (directory is null)
                        spec = SpawnCommands.Claude(Cli("claude"), participant.Model!, mcpPath, workDir, prompt, label, effort);
                    else
                    {
                        var settingsPath = Path.Combine(workDir, "settings.json");     // scratch, never the room
                        File.WriteAllText(settingsPath, SpawnCommands.ClaudeSettingsJson(_options.DataDir));
                        // run_gate joins the allowlist only for an in-run spawn. The built-in tool set
                        // (--tools) is untouched either way.
                        var allowedTools = activeRun is not null ? SpawnCommands.ClaudeRunToolsAllowed : SpawnCommands.ClaudeDirectoryToolsAllowed;
                        spec = SpawnCommands.ClaudeInDirectory(Cli("claude"), participant.Model!, mcpPath, settingsPath, SpawnPrompt.DirectoryRules(tree!, inWorktree ? directory : null), tree!, prompt, label, effort, allowedTools, mcpToolTimeoutMs);
                    }
                    break;
                }
                case "codex":
                    // The CLI's default 60 s MCP tool-call timeout kills a long run_gate call well
                    // before it can finish, so an in-run directory spawn gets EffectiveGateTimeout
                    // (the same reserve as the Claude side above). Every other Codex spawn keeps 60 s.
                    var toolTimeoutSeconds = activeRun is not null ? (int)_runLimits.EffectiveGateTimeout.TotalSeconds : 60;
                    spec = directory is null
                        ? SpawnCommands.Codex(Cli("codex"), participant.Model!, McpUrl(), token, workDir, Path.Combine(workDir, "last.txt"), prompt, label, effort)
                        : SpawnCommands.CodexInDirectory(Cli("codex"), participant.Model!, McpUrl(), token, tree!, Path.Combine(workDir, "last.txt"), prompt, label, effort, toolTimeoutSeconds);
                    break;
                default:
                    throw new InvalidOperationException($"Participant '{participant.Id}' has host '{participant.Host}', which the spawner does not know how to start.");
            }
            var handle = new SpawnHandle
            {
                Request = request, Exchange = x, Participant = participant, SpawnId = spawnId, WorkDir = workDir, Token = token,
                Cancel = new CancellationTokenSource(), Directory = tree, InWorktree = inWorktree, Timeout = timeout, StartedAt = now,
            };
            _inFlight[(request.RoomId, participant.Id)] = handle;
            Console.Error.WriteLine($"spawn {spawnId}: {participant.Id} starting (turn {request.TurnNumber}/{x.Budget}, {request.RemainingAfter} after)");
            Interlocked.Increment(ref _live);
            var turn = request.TurnNumber; var budget = x.Budget; var roomId = request.RoomId; var host = participant.Host; var continueBranch = x.ContinuesBranch;
            handle.Run = Task.Run(async () =>
            {
                var result = new ProcessResult(null, false, false, "", "not started", TimeSpan.Zero);
                TrailReport? trail = null;
                try
                {
                    GitTrail? git = null;
                    CommitOutcome? owner = null;
                    string? headBefore = null;
                    var leased = false;
                    if (directory is not null)
                    {
                        // Before: the hub owner's edits since the last spawn become their own commit, in
                        // the room directory itself whether or not this spawn runs in a worktree.
                        var roomGit = _trails.For(directory);
                        if (await roomGit.IsDirtyAsync(CancellationToken.None))
                            owner = await roomGit.CommitAllAsync(RoomCommits.OwnerMessage(_owner, roomId), author: null, allowEmpty: false, cancellation: CancellationToken.None);
                        if (inWorktree)
                        {
                            var lease = await _worktrees.EnsureAsync(directory, request.RootMessageId, CancellationToken.None, continueBranch);
                            if (lease.Refusal is not null)
                            {
                                // No CLI starts anywhere; the finally below still reports FinishedEvent.
                                result = new ProcessResult(null, false, false, "", WorktreeRefused + lease.Refusal, TimeSpan.Zero);
                                return;
                            }
                            git = _trails.ForWorktree(directory, tree!);
                            leased = true;
                        }
                        else git = roomGit;
                        headBefore = await git.HeadAsync(CancellationToken.None);
                    }
                    // RoomId/ParticipantId are set at this one line so the hub's owner-peer check's refusal
                    // note can name the spawn if this credential is later stolen and replayed.
                    var launched = false;
                    try { result = await _runner.RunAsync(spec with { RoomId = roomId, ParticipantId = participant.Id }, timeout, handle.Cancel.Token); launched = true; }
                    catch (Exception e) { result = new ProcessResult(null, false, false, "", "launch failed: " + e.Message, TimeSpan.Zero); }
                    if (git is not null)
                    {
                        // After: always a commit, empty or not, timed out or not — the trail says the spawn happened.
                        var commands = (host == "codex" ? SpawnOutput.CodexShellCommands(result.StandardOutput) : SpawnOutput.ClaudeShellCommands(result.StandardOutput))
                            .Select(c => c with { Command = Scrub(StripAnsi(c.Command), token) }).ToList();
                        var headMoved = await git.HeadAsync(CancellationToken.None) != headBefore;
                        // The repository's own identity is the author; the host is credited by
                        // trailer only when its process actually launched and the turn changed
                        // something (CommitAllAsync decides that part).
                        var trailer = launched ? RoomCommits.CoAuthorTrailer(host) : null;
                        var agent = await git.CommitAllAsync(RoomCommits.AgentMessage(participant, roomId, turn, budget, commands, headMoved), author: null, allowEmpty: true,
                            trailers: trailer is null ? null : [trailer], cancellation: CancellationToken.None);
                        trail = new TrailReport(owner, agent, commands.Count, headMoved, leased);

                        // Artifact authorship comes from the spawn's whole diff, not one commit, so a
                        // host that commits its own work mid-spawn (Codex, or a rogue Claude Bash call)
                        // is still attributed correctly. This runs off the spawner loop: the run lookup
                        // is _runs.Active (a thread-safe database read), never _rooms/_inFlight, which
                        // only the loop thread mutates.
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
            // The spawn never launched, so it never posted; a silent addressee is not retried as its
            // own synthesis turn.
            var (note, _) = ExchangePolicy.Finished(x, participant.Id, now, posted: false);
            if (note is not null) PostNote(request.RoomId, note);
        }
    }

    private void OnFinished(SpawnHandle h, ProcessResult r, TrailReport? trail)
    {
        var id = h.Participant.Id;
        var room = h.Request.RoomId;
        _inFlight.Remove((room, id));
        Interlocked.Decrement(ref _live);
        if (h.Exchange.ModeLeg is not null) { OnModeFinished(h, r, trail); return; }
        // A spawn cancelled or timed out may have left a half-written worktree, so its exchange's
        // close must keep the branch unmerged whatever ExchangeStatus says; a spawn that really ran in
        // a leased worktree marks its exchange so a close knows to look for one at all.
        if (r.Cancelled || r.TimedOut) h.Exchange.Interrupted = true;
        if (trail?.Leased == true) h.Exchange.WorktreeLeased = true;
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
        // A worktree lease refused before any CLI started: never claim a process ran.
        else if (r.ExitCode is null && r.StandardError.StartsWith(WorktreeRefused, StringComparison.Ordinal))
            PostNote(room, $"@{id} was not started: {r.StandardError}.");
        else if (!h.Posted)
        {
            var isCodex = h.Participant.Host == "codex";
            var final = isCodex
                ? SpawnCommands.CodexFinalText(Path.Combine(h.WorkDir, "last.txt"))
                : SpawnCommands.ClaudeFinalText(r.StandardOutput);
            var exit = r.ExitCode?.ToString() ?? "none";
            // Codex's own stdout names the reason it never replied (a turn.failed or error event);
            // that beats the raw stderr tail below, which is often empty or unrelated noise.
            var codexFailure = final is null && isCodex ? SpawnOutput.CodexFailure(r.StandardOutput) : null;
            if (final is not null)
                PostNote(room, $"@{id} replied without posting to the room (exit code {exit}). Its reply:\n\n{Truncate(Scrub(StripAnsi(final), h.Token), NoteReplyChars)}");
            else if (codexFailure is not null)
                PostNote(room, $"@{id} exited with code {exit} without replying. Codex reported: {Truncate(Scrub(StripAnsi(codexFailure), h.Token), NoteStderrChars)}");
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
        // Finished may return a synthesis-queuing note with Concluded false: the exchange is still
        // open, waiting on the addressee's wrap-up turn, so the run branch below is gated on
        // Concluded, not on the note.
        var (note, concluded) = ExchangePolicy.Finished(h.Exchange, id, _clock.GetUtcNow(), posted: h.Posted);
        // This exchange may have just gone idle (concluded, superseded or stopped with nothing left in
        // flight): try to hand its worktree to a close now. Also releases a close (or a worktree
        // launch) of another exchange that was waiting on this spawn because it ran in the room
        // directory itself.
        CloseIdleWorktrees(room);
        if (note is not null)
        {
            PostNote(room, note);
            // Wake the run's loop only when the exchange that just concluded is still the room's
            // current one: a conductor may already have rooted a newer exchange that superseded it,
            // and that one must be left alone.
            if (concluded && _runs.Active(room) is { } activeRun && ReferenceEquals(h.Exchange, Newest(room)))
            {
                // The conductor finishing without posting is a silence, decided through
                // RunEvent.SpawnSilent (ask again, then park once the phase's allowance is spent)
                // rather than the immediate reopen every other conclusion gets via ExchangeConcluded
                // (a worker's turn, or the conductor's turn when it did post; after a refused post
                // HandleConductorPost already asked again itself).
                if (!h.Posted && activeRun.ConductorId == id)
                    HandleSpawnSilent(activeRun, id, h.Request.TriggerIds);
                else
                {
                    var lastId = _store.ReadLast(room, 1).Select(msg => msg.Id).DefaultIfEmpty(h.Request.RootMessageId).First();
                    DriveRun(activeRun, new RunEvent.ExchangeConcluded(lastId));
                }
            }
        }
        // A room-directory spawn finishing releases a deferred close (or worktree launch) of another
        // exchange in this room.
        CloseIdleWorktrees(room);
        Publish(room);
    }

    /// <summary>Room-scoped, not exchange-scoped: a superseded exchange's spawn is still a live CLI in
    /// this room, and an owner message with no mention leaves the room with no open exchange while
    /// one runs. Stop kills every in-flight spawn of the room, closes every open exchange, and
    /// answers null only when there is nothing at all to stop.
    ///
    /// A run is checked first: an active or parked run in this room is ended through
    /// <see cref="EndRun"/> whether or not an exchange is open or anything is in flight, since a
    /// parked run with nothing open would otherwise hit the ordinary branch's "nothing to stop" null
    /// (409 at the API). A room with no run falls through to the exchange stop.</summary>
    private ExchangeSnapshot? OnStop(string roomId)
    {
        var run = _runs.Active(roomId) ?? (_runs.Latest(roomId) is { Status: RunStatus.Parked } parked ? parked : null);
        if (run is not null)
        {
            EndRun(run, "stopped by the owner", ExchangeStopCause.Owner);
            return Publish(roomId);
        }

        var open = ExchangesIn(roomId).Where(e => e.Status == ExchangeStatus.Open).ToList();
        var live = _inFlight.Values.Where(h => h.Request.RoomId == roomId).ToList();
        if (open.Count == 0 && live.Count == 0) return null;
        foreach (var handle in live) handle.Cancel.Cancel();
        if (open.Count == 0)
            PostNote(roomId, $"Exchange stopped by the owner: {live.Count} running spawn(s) of an earlier exchange stopped.");
        foreach (var x in open) PostNote(roomId, ExchangePolicy.Stop(x, ExchangeStopCause.Owner));
        return Publish(roomId);
    }

    private (ExchangeStopOutcome, ExchangeSnapshot?) OnStopOne(string roomId, long rootMessageId)
    {
        if (_runs.Active(roomId) is not null || _runs.Latest(roomId) is { Status: RunStatus.Parked })
            return (ExchangeStopOutcome.RunOwnsRoom, null);
        var x = ExchangesIn(roomId).LastOrDefault(e => e.RootMessageId == rootMessageId);
        if (x is null) return (ExchangeStopOutcome.NotFound, null);
        var live = _inFlight.Values.Where(h => ReferenceEquals(h.Exchange, x)).ToList();
        var open = x.Status == ExchangeStatus.Open;
        if (!open && live.Count == 0) return (ExchangeStopOutcome.NothingToStop, null);
        foreach (var handle in live) handle.Cancel.Cancel();
        PostNote(roomId, open
            ? ExchangePolicy.Stop(x, ExchangeStopCause.Owner)
            : $"Exchange stopped by the owner: {live.Count} running spawn(s) of an earlier exchange stopped.");
        return (ExchangeStopOutcome.Stopped, Publish(roomId));
    }

    private void ArmWake()
    {
        var now = _clock.GetUtcNow();
        DateTimeOffset? next = null;
        foreach (var x in _rooms.Values.SelectMany(list => list))
        {
            var directory = _store.GetRoom(x.RoomId)?.Directory;
            if (x.ModeLeg is { Preparing: true }) continue;
            var exclusive = directory is not null && x.ModeLeg?.Settings.Mode != "panel";
            var over = UsesWorktrees(x.RoomId, directory) ? x.InFlight : null;
            if (over is not null && _inFlight.Values.Any(h => h.Request.RoomId == x.RoomId && h.Directory is not null && !h.InWorktree)) continue;
            // Skip this exchange's wake while its room's close is running: the close's own
            // WorktreeClosedEvent wakes the loop when it finishes.
            if (exclusive && over is null && _closingRooms.Contains(x.RoomId)) continue;
            if (x.WaitsForClose) continue;
            var wake = _policy.NextWake(x, now, _lastStart, InFlightIn(x.RoomId), exclusive, over);
            if (wake is not null && (next is null || wake < next)) next = wake;
        }

        // Every active run gets two wakes of its own, which the exchange loop above cannot see (a run
        // with nothing open or in flight has no Open exchange in _rooms for _policy.NextWake). The
        // wall-clock wake is unconditional (the hard cap must trip even while the run is busy, and
        // parking stops its open exchange and in-flight spawns). The stall wake arms only while idle,
        // SpawnTimeout after the last instant the run was known busy, never "SpawnTimeout from now":
        // another room's activity reruns this method on every pass and would push a stalled run's
        // deadline forward forever.
        var activeIds = new HashSet<long>();
        foreach (var run in _runs.ListActive())
        {
            activeIds.Add(run.Id);
            var wallClockDeadline = run.StartedAt + TimeSpan.FromSeconds(run.ParkedSeconds) + _runLimits.WallClock;
            if (next is null || wallClockDeadline < next) next = wallClockDeadline;

            var busy = Newest(run.RoomId) is { Status: ExchangeStatus.Open } || InFlightIn(run.RoomId).Count > 0;
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
        // The TimeProvider overload, so a FakeTimeProvider's Advance() fires this without a test
        // waiting out 8 hours.
        _ = Task.Delay(delay, _clock, cts.Token).ContinueWith(t => { if (!t.IsCanceled) _events.Writer.TryWrite(new TickEvent()); }, TaskScheduler.Default);
    }

    private long _seq;

    private ExchangeSnapshot Publish(string roomId)
    {
        MaintainModeResources(roomId);
        CloseIdleWorktrees(roomId);
        // Read once per publish: a run active or parked in the room gates Continuable the same way for
        // every exchange's own view and for the top-level field below (a parked run resumes on any
        // human post, /continue included, so a /continue there would steer the run rather than
        // reopen the exchange).
        var runBlocks = _runs.Active(roomId) is not null || _runs.Latest(roomId) is { Status: RunStatus.Parked };
        // InFlight is the room's live spawns (a superseded exchange's spawn included), not the newest
        // exchange's list; Seq lets a client order a GET against an event.
        var views = ExchangesIn(roomId).Select(x => View(x, runBlocks)).ToList();
        var starts = _inFlight.Values.Where(h => h.Request.RoomId == roomId)
            .ToDictionary(h => h.Participant.Id, h => h.StartedAt, StringComparer.Ordinal);
        var snapshot = (Displayed(roomId) is { } x
            ? new ExchangeSnapshot(roomId, x.Status.ToString().ToLowerInvariant(), x.RootMessageId, x.Budget, x.TurnsStarted, x.TurnsCommitted,
                Math.Max(0, x.Budget - (x.ModeLeg is null ? x.TurnsCommitted : x.TurnsStarted)), InFlightIn(roomId).Order(StringComparer.Ordinal).ToList(), x.Pending.Keys.ToList(),
                StoppedBy: x.StopCause?.ToString().ToLowerInvariant(),
                Continuable: ExchangePolicy.Continuable(x, runBlocks) && x.ModeLeg?.Preparing != true, InFlightStartedAt: starts,
                Mode: x.ModeLeg?.Settings.Mode, ModeParticipants: x.ModeLeg?.Settings.Participants, Preparing: x.ModeLeg?.Preparing == true)
            : Idle(roomId)) with { Seq = ++_seq, Exchanges = views };
        _snapshots[roomId] = snapshot;
        BroadcastAsync(roomId, snapshot);
        return snapshot;
    }

    private ExchangeView View(Exchange x, bool runBlocks) => new(
        x.RootMessageId, x.Status.ToString().ToLowerInvariant(), x.Budget, x.TurnsStarted, x.TurnsCommitted,
        Math.Max(0, x.Budget - (x.ModeLeg is null ? x.TurnsCommitted : x.TurnsStarted)), x.InFlight.Order(StringComparer.Ordinal).ToList(), x.Pending.Keys.ToList(),
        x.StopCause?.ToString().ToLowerInvariant(),
        ExchangePolicy.Continuable(x, runBlocks) && x.ModeLeg?.Preparing != true,
        _inFlight.Values.Where(h => ReferenceEquals(h.Exchange, x))
            .ToDictionary(h => h.Participant.Id, h => h.StartedAt, StringComparer.Ordinal),
        x.ModeLeg?.Settings.Mode, x.ModeLeg?.Settings.Participants, x.ModeLeg?.Preparing == true);

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

    private static ExchangeSnapshot Idle(string roomId) => new(roomId, "idle", null, 0, 0, 0, 0, [], [], Exchanges: []);

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
