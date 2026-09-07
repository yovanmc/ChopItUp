using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Hosting;
using ChopItUp.Hub.Security;
using ChopItUp.Hub.Skills;
using ChopItUp.Hub.Spawning;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ChopItUp.Hub.Mcp;

/// <summary>One gate running per room at a time (row 19, task 12d). Registered as its own DI singleton
/// (<see cref="HubHost.Build"/>) rather than a field on <see cref="RunTools"/>, following
/// <see cref="RoomTools"/>/<see cref="MemoryTools"/>'s rule that a tool class holds no state of its
/// own — every mutable thing a tool touches lives in an explicit singleton, so the tool's own DI
/// lifetime is never load-bearing.</summary>
public sealed class GateLocks
{
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);
    public bool TryEnter(string roomId) => _running.TryAdd(roomId, 0);
    public void Exit(string roomId) => _running.TryRemove(roomId, out _);
}

/// <summary>The single tool a run's spawns get beyond the ordinary three (row 19, task 12; AC10):
/// <c>run_gate</c> runs one of the checks the run's skill declares, in the room's directory, from a
/// hub-verified copy, and returns its exit code and capped output. Every call — a run or a refusal —
/// is recorded in <c>run_gate_runs</c> and announced with a fixed-form note, so a later verification
/// reads what happened from hub state, never from a model's wording (AC9's gate-results list is the
/// same records, rendered into the prompt).</summary>
[McpServerToolType]
public sealed class RunTools(RunStore runs, SkillStore skills, SpawnerService spawner, MessageStore store,
    MessageSignal signal, IProcessRunner runner, CliLocator cliLocator, RunLimits limits, HubOptions options,
    TimeProvider clock, GateLocks locks, IHttpContextAccessor http)
{
    private const int StdoutCapChars = 8_000;
    private const int StderrCapChars = 2_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string Caller =>
        http.HttpContext?.Items[BearerTokenMiddleware.ParticipantKey] as string
        ?? throw new McpException("Unauthenticated request reached a tool; this is a hub bug.");

    [McpServerTool(Name = "run_gate", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Run one of the named checks your run's skill ships, in the room's directory, from a hub-verified copy of the skill. Returns the exit code and both output streams (tail-kept if long). Only an in-flight spawn of an active run may call this, only for a gate its skill declares, only when the skill's files match what was installed, and only one at a time per room - every other case is refused with a reason and nothing is executed.")]
    public async Task<string> RunGate(
        [Description("Room id, e.g. \"lab\".")] string room_id,
        [Description("Gate name your run's skill declares, e.g. \"budget\".")] string gate,
        CancellationToken cancellationToken = default)
    {
        var me = Caller;
        var now = clock.GetUtcNow();
        if (!store.RoomExists(room_id)) throw new McpException($"Unknown room '{room_id}'. Call list_rooms.");

        var run = runs.Active(room_id);
        if (run is null) throw Refuse(null, room_id, gate, me, now, "no-active-run", $"No run is active in room '{room_id}'.");

        if (!spawner.Snapshot(room_id).InFlight.Contains(me))
            throw Refuse(run.Id, room_id, gate, me, now, "not-in-flight", "run_gate may only be called by a spawn currently in flight for this run in this room.");

        if (!locks.TryEnter(room_id))
            throw Refuse(run.Id, room_id, gate, me, now, "gate-running", $"A gate is already running in room '{room_id}'.");
        try
        {
            var read = skills.ReadGate(run.SkillName, gate);
            if (read is GateRead.NotDeclared)
                throw Refuse(run.Id, room_id, gate, me, now, "undeclared-gate", $"Skill '{run.SkillName}' does not declare a gate named '{gate}'.");
            if (read is not GateRead.Ok ok)
                throw Refuse(run.Id, room_id, gate, me, now, "tree-mismatch", $"'{run.SkillName}' does not match what was imported; re-import it with --import-skill.");
            if (skills.VerifyTree(run.SkillName) is not TreeVerification.Ok)
                throw Refuse(run.Id, room_id, gate, me, now, "tree-mismatch", $"'{run.SkillName}' does not match what was imported; re-import it with --import-skill.");

            var room = store.GetRoom(room_id);
            if (room?.Directory is null)
                throw Refuse(run.Id, room_id, gate, me, now, "no-directory", $"Room '{room_id}' has no directory.");

            return await Execute(run.Id, room_id, gate, me, room.Directory, run.SkillName, ok.Gate, now, cancellationToken);
        }
        finally { locks.Exit(room_id); }
    }

    /// <summary>P5: copies the skill's whole VERIFIED tree to a hub-private throwaway folder and runs
    /// the gate from there, closing the hash-then-run window — a rewrite landing between
    /// <see cref="SkillStore.VerifyTree"/> above and this copy is still caught, because the copy reads
    /// the same files that were just verified, and the next call re-verifies from scratch. The copy is
    /// deleted in a `finally` (pass 2's F-15: nobody owned this in the previous draft).
    /// <c>ROADMAP_GATE_BASELINE</c> points a script that reads/writes a sibling baseline file (the
    /// roadmap gate this feature exists to run) at a hub-private DURABLE path instead of the throwaway
    /// copy's own directory — the script's own documented env-var seam (verified this session) — or
    /// moving the script's <c>$PSScriptRoot</c> would silently void its legitimate write.</summary>
    private async Task<string> Execute(long runId, string roomId, string gate, string caller, string roomDirectory,
        string skillName, GateDeclaration declaration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var copyDir = Path.Combine(options.DataDir, "gate-runs", Guid.NewGuid().ToString("N"));
        try
        {
            CopyTree(Path.Combine(skills.Root, skillName), copyDir);
            var scriptPath = Path.Combine(copyDir, "scripts", gate + ".ps1");
            var cli = cliLocator("pwsh");
            var baselinePath = Path.Combine(options.DataDir, "gate-baselines", skillName, "baselines.json");
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);

            var arguments = new List<string> { "-NoProfile", "-NonInteractive", "-File", scriptPath };
            arguments.AddRange(declaration.Arguments);
            var spec = new ProcessSpec(cli.FileName, [.. cli.LeadingArguments, .. arguments],
                new Dictionary<string, string> { ["ROADMAP_GATE_BASELINE"] = baselinePath },
                roomDirectory, "", $"run_gate/{gate}/{caller}");

            var result = await runner.RunAsync(spec, limits.SpawnTimeout, cancellationToken);
            var outcome = result.TimedOut ? "timed out" : $"exit {Describe(result.ExitCode)}";
            runs.RecordGateRun(runId, roomId, gate, caller, result.ExitCode, outcome, now);
            PostNote(roomId, $"run_gate {gate} by @{caller}: {outcome}");

            return JsonSerializer.Serialize(new
            {
                gate,
                exit_code = result.ExitCode,
                timed_out = result.TimedOut,
                stdout = Tail(result.StandardOutput, StdoutCapChars),
                stderr = Tail(result.StandardError, StderrCapChars),
            }, JsonOptions);
        }
        finally
        {
            try { if (Directory.Exists(copyDir)) Directory.Delete(copyDir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"run_gate: copy '{copyDir}' not deleted: {e.Message}"); }
        }
    }

    /// <summary>AC10's other half: "and record the refusal". Written and posted before the throw, so a
    /// caller that never sees the exception's text still left a trail behind — the same fixed form a
    /// run gets, minus the exit code, plus the reason slug in parentheses.</summary>
    private McpException Refuse(long? runId, string roomId, string gate, string caller, DateTimeOffset now, string reasonSlug, string message)
    {
        runs.RecordGateRun(runId, roomId, gate, caller, null, $"refused: {reasonSlug}", now);
        PostNote(roomId, $"run_gate {gate} by @{caller}: refused ({reasonSlug})");
        return new McpException(message);
    }

    private void PostNote(string roomId, string text)
    {
        try
        {
            var message = store.Post(roomId, ChopDb.HubParticipantId, text);
            signal.Publish(roomId, message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"run_gate: note to '{roomId}' not posted ({e.GetType().Name}: {e.Message}): {text.Split('\n')[0]}");
        }
    }

    private static string Describe(int? exitCode) => exitCode?.ToString() ?? "none";

    private static string Tail(string text, int max) => text.Length <= max ? text : "…" + text[^max..];

    private static void CopyTree(string sourceRoot, string destRoot)
    {
        Directory.CreateDirectory(destRoot);
        foreach (var dir in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destRoot, Path.GetRelativePath(sourceRoot, dir)));
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destRoot, Path.GetRelativePath(sourceRoot, file)));
    }
}
