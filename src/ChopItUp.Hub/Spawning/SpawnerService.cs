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
    private readonly Channel<Event> _events = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, Exchange> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Room, string Participant), SpawnHandle> _inFlight = new();
    private readonly Dictionary<string, DateTimeOffset> _lastStart = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExchangeSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedCli> _clis = new(StringComparer.Ordinal);
    private CancellationTokenSource? _wake;

    public SpawnerService(MessageStore store, IReadOnlyList<Participant> roster, MessageSignal signal, TokenStore tokens,
        IProcessRunner runner, ChopItUp.Hub.Hosting.HubOptions options, SpawnLimits limits, IServer server, IHubContext<RoomHub> hub,
        CliLocator cliLocator, MemoryStore memory, RoomTrails trails, ParticipantStore participants, SkillStore skills, TimeProvider clock)
    {
        _store = store; _roster = roster; _signal = signal; _tokens = tokens; _runner = runner;
        _options = options; _limits = limits; _server = server; _hub = hub; _locate = cliLocator; _memory = memory;
        _trails = trails; _owner = roster.First(p => p.Id == participants.OwnerId());
        _policy = new ExchangePolicy(roster, limits);
        _skills = skills;
        _clock = clock;
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
            case TickEvent: break;
        }
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
        var (next, notes) = _policy.OnMessage(current, m, _clock.GetUtcNow(), acceptMentions, ResolveSkill(m));
        if (next is null) _rooms.Remove(m.RoomId); else _rooms[m.RoomId] = next;
        foreach (var note in notes) PostNote(m.RoomId, note);
        if (next is not null || current is not null) Publish(m.RoomId);
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
        var now = DateTimeOffset.UtcNow;
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
        try
        {
            var token = _tokens.Tokens[participant.Id];
            Directory.CreateDirectory(workDir);
            var core = _memory.ReadCore();
            var room = _store.GetRoom(request.RoomId);
            var directory = room?.Directory;
            var prompt = SpawnPrompt.Render(new SpawnPromptInput(
                participant, request.RoomId, room?.Name ?? request.RoomId, _store.ReadLast(request.RoomId, _limits.TranscriptMessages),
                request.TriggerIds, request.RootMessageId, request.TurnNumber, x.Budget, request.RemainingAfter, spawnId, _roster,
                core.Text, core.Truncated, _memory.ListTopics().Select(t => t.Slug).ToList(), Directory: directory, Skill: x.Skill), _limits);
            var label = $"{participant.Id}/{spawnId}";
            ProcessSpec spec;
            switch (participant.Host)
            {
                case "claude":
                {
                    var mcpPath = Path.Combine(workDir, "mcp.json");
                    File.WriteAllText(mcpPath, SpawnCommands.ClaudeMcpConfigJson(McpUrl(), token));
                    if (directory is null)
                        spec = SpawnCommands.Claude(Cli("claude"), participant.Model!, mcpPath, workDir, prompt, label);
                    else
                    {
                        var settingsPath = Path.Combine(workDir, "settings.json");     // scratch, never the room (decision 8)
                        File.WriteAllText(settingsPath, SpawnCommands.ClaudeSettingsJson(_options.DataDir));
                        spec = SpawnCommands.ClaudeInDirectory(Cli("claude"), participant.Model!, mcpPath, settingsPath, SpawnPrompt.DirectoryRules(directory), directory, prompt, label);
                    }
                    break;
                }
                case "codex":
                    spec = directory is null
                        ? SpawnCommands.Codex(Cli("codex"), participant.Model!, McpUrl(), token, workDir, Path.Combine(workDir, "last.txt"), prompt, label)
                        : SpawnCommands.CodexInDirectory(Cli("codex"), participant.Model!, McpUrl(), token, directory, Path.Combine(workDir, "last.txt"), prompt, label);
                    break;
                default:
                    throw new InvalidOperationException($"Participant '{participant.Id}' has host '{participant.Host}', which the spawner does not know how to start.");
            }
            var handle = new SpawnHandle
            {
                Request = request, Exchange = x, Participant = participant, SpawnId = spawnId, WorkDir = workDir, Token = token,
                Cancel = new CancellationTokenSource(), Directory = directory,
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
                    try { result = await _runner.RunAsync(spec, _limits.Timeout, handle.Cancel.Token); }
                    catch (Exception e) { result = new ProcessResult(null, false, false, "", "launch failed: " + e.Message, TimeSpan.Zero); }
                    if (git is not null)
                    {
                        // After: always a commit, empty or not, timed out or not — the trail says the spawn happened.
                        var commands = (host == "codex" ? SpawnOutput.CodexShellCommands(result.StandardOutput) : SpawnOutput.ClaudeShellCommands(result.StandardOutput))
                            .Select(c => c with { Command = Scrub(StripAnsi(c.Command), token) }).ToList();
                        var headMoved = await git.HeadAsync(CancellationToken.None) != headBefore;
                        var agent = await git.CommitAllAsync(RoomCommits.AgentMessage(participant, roomId, turn, budget, commands, headMoved), RoomCommits.IdentityOf(participant), allowEmpty: true, CancellationToken.None);
                        trail = new TrailReport(owner, agent, commands.Count, headMoved);
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
                ? $"@{id} posted but did not exit within {Describe(_limits.Timeout)}; its process was stopped."
                : $"@{id} did not reply within {Describe(_limits.Timeout)} and was stopped.");
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
        if (note is not null) PostNote(room, note);
        Publish(room);
    }

    /// <summary>Room-scoped, not exchange-scoped (critique pass 2, M1): a superseded exchange's spawn
    /// is still a live CLI in this room, and an owner message with no mention leaves the room with no
    /// open exchange while one runs. Stop kills every in-flight spawn of the room, closes the open
    /// exchange if there is one, and answers null only when there is nothing at all to stop.</summary>
    private ExchangeSnapshot? OnStop(string roomId)
    {
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
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? next = null;
        foreach (var x in _rooms.Values)
        {
            var exclusive = _store.GetRoom(x.RoomId)?.Directory is not null;
            var wake = _policy.NextWake(x, now, _lastStart, InFlightIn(x.RoomId), exclusive);
            if (wake is not null && (next is null || wake < next)) next = wake;
        }
        _wake?.Cancel();
        _wake?.Dispose();
        _wake = null;
        if (next is null) return;
        var delay = next.Value - now;
        if (delay < TimeSpan.FromMilliseconds(10)) delay = TimeSpan.FromMilliseconds(10);
        var cts = new CancellationTokenSource();
        _wake = cts;
        _ = Task.Delay(delay, cts.Token).ContinueWith(t => { if (!t.IsCanceled) _events.Writer.TryWrite(new TickEvent()); }, TaskScheduler.Default);
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
