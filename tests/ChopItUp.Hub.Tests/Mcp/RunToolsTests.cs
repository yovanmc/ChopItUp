using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Skills;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ChopItUp.Hub.Tests.Mcp;

/// <summary>Row 19, task 12 (ticket 12): <c>run_gate</c> - a participant in a run can run the checks
/// its skill ships, and nothing else. Installs its fixture skills through the REAL write path
/// (<see cref="SkillImport"/>), never a hand-written hash, so the whole-tree manifest (task 12a)
/// exists and every refusal here is the one AC10 actually names.
///
/// Every test sets <see cref="FakeProcessRunner.Handler"/> BEFORE posting the message that starts a
/// run: the handler runs INSIDE the real spawn's own task, so calling <c>run_gate</c> from within it
/// (authenticated as the spawned participant, via a real MCP client) is what makes that participant
/// genuinely "in flight" for <see cref="SpawnerService.Snapshot"/> to see - unlike calling it from
/// outside any spawn, which is exactly what the "not in flight" test does on purpose.</summary>
public sealed class RunToolsTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private const string GatedSkillMd = "---\nname: gated\ndescription: d.\nrun: true\ngates: check-it\n---\n\n# Gated\n";
    private const string GatedWithArgsSkillMd = "---\nname: gated-args\ndescription: d.\nrun: true\ngates: check-it(--Foo bar)\n---\n\n# Gated\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_rungate_" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new();
    private HubTestHost _host = null!;
    private string _roomDir = null!;

    /// <summary>Row 20, task 3b: GateProgressInterval is cut from Default's 30 s to 40 ms so the two
    /// progress tests below see several ticks without a slow test. Every other RunLimits field stays at
    /// Default's value - <see cref="run_gate_runs_a_script_under_GateTimeout"/> still asserts against
    /// <see cref="RunLimits.Default"/>'s own EffectiveGateTimeout, which this leaves untouched.</summary>
    public async Task InitializeAsync()
    {
        var runLimits = new RunLimits(RunLimits.Default.Spawns, RunLimits.Default.WallClock, RunLimits.Default.SpawnTimeout,
            RunLimits.Default.PhaseEntries, GateProgressInterval: TimeSpan.FromMilliseconds(40));
        _host = await HubTestHost.StartAsync(_dir, processRunner: _runner, runLimits: runLimits);
        _roomDir = Path.Combine(_host.RoomsRoot, "lab");
        Assert.True(await new GitTrail(_roomDir).InitAsync());
        _host.Services.GetRequiredService<MessageStore>().CreateRoom("lab", "LAB", _roomDir);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private RunStore Runs => _host.Services.GetRequiredService<RunStore>();

    /// <summary>Installs a fixture skill through the real write path so task 12a's whole-tree manifest
    /// exists to verify against (a hand-written <see cref="SkillHashes.Record"/> only ever covers
    /// SKILL.md). <paramref name="overlayMd"/>/<paramref name="overlayScripts"/> (row 20 task 1) compose
    /// a hub-side overlay in through <c>--overlay</c> the same way a real import would.</summary>
    private void ImportSkill(string name, string skillMd, IReadOnlyDictionary<string, string>? extraFiles = null,
        string? overlayMd = null, IReadOnlyDictionary<string, string>? overlayScripts = null)
    {
        var source = Path.Combine(_dir, "sources", name);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "SKILL.md"), skillMd);
        foreach (var (relative, content) in extraFiles ?? new Dictionary<string, string>())
        {
            var full = Path.Combine(source, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        string? overlayDir = null;
        if (overlayMd is not null)
        {
            overlayDir = Path.Combine(_dir, "overlays", name);
            Directory.CreateDirectory(overlayDir);
            File.WriteAllText(Path.Combine(overlayDir, "OVERLAY.md"), overlayMd);
            foreach (var (fileName, content) in overlayScripts ?? new Dictionary<string, string>())
            {
                var scriptsDir = Path.Combine(overlayDir, "scripts");
                Directory.CreateDirectory(scriptsDir);
                File.WriteAllText(Path.Combine(scriptsDir, fileName), content);
            }
        }
        var hashes = new SkillHashes(_host.Services.GetRequiredService<ChopDb>());
        var result = SkillImport.Run(source, Path.Combine(_dir, "skills"), force: false, hashes, overlayDir);
        Assert.True(result.Outcome == SkillImportOutcome.Ok, result.Message);
    }

    /// <summary>Posts the owner's slash invocation that starts a run. Callers set
    /// <see cref="_runner"/>.Handler BEFORE calling this - the real launch happens asynchronously on
    /// the hub's own loop, so the handler must already be in place when it does.</summary>
    private async Task PostRunStart(string skillName, string conductor = "sonnet")
    {
        var r = await _host.Client.PostAsJsonAsync("api/rooms/lab/messages", new { body = $"/{skillName} @{conductor} begin" });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
    }

    private static async Task<CallToolResult> CallRunGate(McpClient client, string room, string gate) =>
        await client.CallToolAsync("run_gate", new Dictionary<string, object?> { ["room_id"] = room, ["gate"] = gate });

    private static string ErrorText(CallToolResult r)
    {
        Assert.True(r.IsError, "expected a tool error");
        return string.Join("", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
    }

    /// <summary>Raw read, direct from the database: <see cref="RunStore.GateRuns"/> is keyed by a
    /// non-null run id, but AC10 requires recording a refusal that has none (there is no run here) -
    /// this is the only way a test can see that row.</summary>
    private List<(long? RunId, string Gate, string CallerId, int? ExitCode, string Outcome)> AllGateRunRows()
    {
        using var conn = _host.Services.GetRequiredService<ChopDb>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT run_id, gate, caller_id, exit_code, outcome FROM run_gate_runs ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<(long?, string, string, int?, string)>();
        while (reader.Read())
            rows.Add((reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetString(4)));
        return rows;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Wait);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(30);
        }
        throw new TimeoutException("condition not met within " + (timeout ?? Wait));
    }

    private async Task<string> WaitForNoteBody(Func<string, bool> match)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/lab/messages?afterId=0&limit=200"));
            foreach (var m in doc.RootElement.GetProperty("messages").EnumerateArray())
                if (m.GetProperty("authorId").GetString() == ChopDb.HubParticipantId && match(m.GetProperty("body").GetString()!))
                    return m.GetProperty("body").GetString()!;
            await Task.Delay(50);
        }
        throw new TimeoutException("No matching hub note within " + Wait);
    }

    [Fact]
    public async Task A_shipped_gate_runs_in_the_room_directory_with_its_exit_code_and_both_streams_intact()
    {
        ImportSkill("gated", GatedSkillMd, new Dictionary<string, string> { ["scripts/check-it.ps1"] = "exit 3\n" });
        CallToolResult? gateResult = null;
        _runner.Handler = async (spec, _, _) =>
        {
            if (spec.Label.StartsWith("run_gate/"))
                return new ProcessResult(3, false, false, "the check ran\n", "a warning\n", TimeSpan.FromMilliseconds(5));
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
            {
                await using var client = await _host.ClientFor("sonnet");
                gateResult = await CallRunGate(client, "lab", "check-it");
            }
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostRunStart("gated");
        await WaitUntil(() => gateResult is not null);

        var json = HubTestHost.Json(gateResult!);
        Assert.Equal("check-it", json.GetProperty("gate").GetString());
        Assert.Equal(3, json.GetProperty("exit_code").GetInt32());
        Assert.False(json.GetProperty("timed_out").GetBoolean());
        Assert.Equal("the check ran\n", json.GetProperty("stdout").GetString());
        Assert.Equal("a warning\n", json.GetProperty("stderr").GetString());

        Assert.Equal("run_gate check-it by @sonnet: exit 3", await WaitForNoteBody(b => b.StartsWith("run_gate check-it by @sonnet:")));

        var run = Runs.Latest("lab")!;
        var row = Assert.Single(AllGateRunRows(), r => r.Gate == "check-it");
        Assert.Equal(run.Id, row.RunId);
        Assert.Equal("sonnet", row.CallerId);
        Assert.Equal(3, row.ExitCode);
        Assert.Equal("exit 3", row.Outcome);
    }

    // --- Row 20 task 1: run_gate on an overlay-declared gate ---------------------------------------

    [Fact]
    public async Task run_gate_executes_an_overlay_declared_gate()
    {
        ImportSkill("overlay-gated", "---\nname: overlay-gated\ndescription: d.\nrun: true\n---\n# Overlay Gated\n",
            overlayMd: "---\ngates: overlay-check\n---\nOverlay prose.\n",
            overlayScripts: new Dictionary<string, string> { ["overlay-check.ps1"] = "exit 0\n" });
        CallToolResult? gateResult = null;
        _runner.Handler = async (spec, _, _) =>
        {
            if (spec.Label.StartsWith("run_gate/"))
                return new ProcessResult(0, false, false, "overlay gate ran\n", "", TimeSpan.FromMilliseconds(5));
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
            {
                await using var client = await _host.ClientFor("sonnet");
                gateResult = await CallRunGate(client, "lab", "overlay-check");
            }
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostRunStart("overlay-gated");
        await WaitUntil(() => gateResult is not null);

        var json = HubTestHost.Json(gateResult!);
        Assert.Equal("overlay-check", json.GetProperty("gate").GetString());
        Assert.Equal(0, json.GetProperty("exit_code").GetInt32());
        Assert.Equal("overlay gate ran\n", json.GetProperty("stdout").GetString());
    }

    [Fact]
    public async Task A_declared_gate_with_arguments_passes_exactly_those_arguments_after_the_script_path()
    {
        ImportSkill("gated-args", GatedWithArgsSkillMd, new Dictionary<string, string> { ["scripts/check-it.ps1"] = "param($Foo)\nexit 0\n" });
        ProcessSpec? gateSpec = null;
        _runner.Handler = async (spec, _, _) =>
        {
            if (spec.Label.StartsWith("run_gate/")) { gateSpec = spec; return FakeProcessRunner.Ok("ok"); }
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
            {
                await using var client = await _host.ClientFor("sonnet");
                await CallRunGate(client, "lab", "check-it");
            }
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostRunStart("gated-args");
        await WaitUntil(() => gateSpec is not null);

        var args = gateSpec!.Arguments.ToList();
        var fileIndex = args.IndexOf("-File");
        Assert.True(fileIndex >= 0);
        Assert.EndsWith("check-it.ps1", args[fileIndex + 1]);
        Assert.Equal(["--Foo", "bar"], args.Skip(fileIndex + 2));
        Assert.Equal(_roomDir, gateSpec.WorkingDirectory);
    }

    [Fact]
    public async Task Refuses_when_no_run_is_active_and_records_the_refusal_with_a_null_run_id()
    {
        await using var client = await _host.ClientFor("claude");

        var result = await CallRunGate(client, "lab", "check-it");

        Assert.Contains("No run is active", ErrorText(result));
        var row = Assert.Single(AllGateRunRows());
        Assert.Null(row.RunId);
        Assert.Equal("claude", row.CallerId);
        Assert.Null(row.ExitCode);
        Assert.Equal("refused: no-active-run", row.Outcome);
        Assert.Equal("run_gate check-it by @claude: refused (no-active-run)", await WaitForNoteBody(b => b.StartsWith("run_gate")));
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(300)));   // nothing executed
    }

    [Fact]
    public async Task Refuses_when_the_caller_is_not_in_flight_for_the_run()
    {
        ImportSkill("gated", GatedSkillMd, new Dictionary<string, string> { ["scripts/check-it.ps1"] = "exit 0\n" });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet") await release.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostRunStart("gated");
        await WaitUntil(() => Runs.Active("lab") is not null);

        await using var opus = await _host.ClientFor("opus");
        var result = await CallRunGate(opus, "lab", "check-it");

        Assert.Contains("currently in flight", ErrorText(result));
        var row = Assert.Single(AllGateRunRows());
        Assert.Equal("refused: not-in-flight", row.Outcome);
        release.SetResult();
    }

    [Fact]
    public async Task Refuses_when_the_gate_is_not_declared()
    {
        ImportSkill("gated", GatedSkillMd, new Dictionary<string, string> { ["scripts/check-it.ps1"] = "exit 0\n" });
        CallToolResult? gateResult = null;
        _runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
            {
                await using var client = await _host.ClientFor("sonnet");
                gateResult = await CallRunGate(client, "lab", "no-such-gate");
            }
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostRunStart("gated");
        await WaitUntil(() => gateResult is not null);

        Assert.Contains("does not declare a gate", ErrorText(gateResult!));
        Assert.Equal("refused: undeclared-gate", Assert.Single(AllGateRunRows()).Outcome);
    }

    [Fact]
    public async Task A_tampered_neighbouring_file_refuses_the_gate_proven_by_the_absence_of_the_side_effect()
    {
        ImportSkill("gated", GatedSkillMd, new Dictionary<string, string>
        {
            ["scripts/check-it.ps1"] = "exit 0\n",
            ["scripts/baselines.json"] = "{}",
        });
        // Tamper a file the gate's OWN manifest entry never touches - proves task 12b's whole-tree
        // check, not merely a per-script hash.
        File.WriteAllText(Path.Combine(_dir, "skills", "gated", "scripts", "baselines.json"), "{\"tampered\":true}");
        CallToolResult? gateResult = null;
        _runner.Handler = async (spec, _, _) =>
        {
            if (spec.Label.StartsWith("run_gate/")) throw new InvalidOperationException("the gate must never actually run");
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
            {
                await using var client = await _host.ClientFor("sonnet");
                gateResult = await CallRunGate(client, "lab", "check-it");
            }
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostRunStart("gated");
        await WaitUntil(() => gateResult is not null);

        Assert.Contains("does not match what was imported", ErrorText(gateResult!));
        Assert.Equal("refused: tree-mismatch", Assert.Single(AllGateRunRows()).Outcome);
        // The tampered content is untouched - nothing ran on top of it, and no copy was left behind.
        Assert.Equal("{\"tampered\":true}", File.ReadAllText(Path.Combine(_dir, "skills", "gated", "scripts", "baselines.json")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "gate-runs")) && Directory.EnumerateFileSystemEntries(Path.Combine(_dir, "gate-runs")).Any());
    }

    // --- Row 20 task 3: run_gate's own process timeout is EffectiveGateTimeout, not SpawnTimeout ----

    [Fact]
    public async Task run_gate_runs_a_script_under_GateTimeout()
    {
        ImportSkill("gated", GatedSkillMd, new Dictionary<string, string> { ["scripts/check-it.ps1"] = "exit 0\n" });
        TimeSpan? gateTimeout = null;
        _runner.Handler = async (spec, timeout, _) =>
        {
            if (spec.Label.StartsWith("run_gate/")) { gateTimeout = timeout; return FakeProcessRunner.Ok("ok"); }
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
            {
                await using var client = await _host.ClientFor("sonnet");
                await CallRunGate(client, "lab", "check-it");
            }
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostRunStart("gated");
        await WaitUntil(() => gateTimeout is not null);

        Assert.Equal(RunLimits.Default.EffectiveGateTimeout, gateTimeout);
        Assert.Equal(TimeSpan.FromMinutes(25), gateTimeout);
        Assert.NotEqual(RunLimits.Default.SpawnTimeout, gateTimeout);
    }

    [Fact]
    public async Task A_second_concurrent_call_refuses_while_the_first_is_still_running()
    {
        ImportSkill("gated", GatedSkillMd, new Dictionary<string, string> { ["scripts/check-it.ps1"] = "exit 0\n" });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Signals the instant the first call's gate process actually starts - which, per RunTools.RunGate,
        // only happens after that call has taken GateLocks' per-room lock (locks.TryEnter precedes the
        // Execute() that spawns this process; see RunTools.cs). Awaiting this instead of a fixed sleep is
        // what makes the second call's race against the lock deterministic rather than load-dependent.
        var firstEnteredGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CallToolResult? second = null;
        _runner.Handler = async (spec, _, ct) =>
        {
            if (spec.Label.StartsWith("run_gate/"))
            {
                firstEnteredGate.TrySetResult();
                await release.Task.WaitAsync(ct);
                return FakeProcessRunner.Ok("ok");
            }
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
            {
                await using var clientA = await _host.ClientFor("sonnet");
                await using var clientB = await _host.ClientFor("sonnet");
                var first = CallRunGate(clientA, "lab", "check-it");
                await firstEnteredGate.Task.WaitAsync(Wait, ct);   // first call now holds the per-room lock
                second = await CallRunGate(clientB, "lab", "check-it");
                release.SetResult();
                await first;
            }
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostRunStart("gated");
        // second is not null once the refusal round-trips; the first call's own "exit 0" row lands later
        // still, only after release.SetResult() unblocks it and its Execute() records the row - so the
        // wait has to cover both, not just the refusal, or the assert below can beat that tail write.
        await WaitUntil(() => second is not null && AllGateRunRows().Count == 2, TimeSpan.FromSeconds(20));

        Assert.Contains("already running", ErrorText(second!));
        var rows = AllGateRunRows();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Outcome == "exit 0");
        Assert.Contains(rows, r => r.Outcome == "refused: gate-running");
    }

    // --- Row 20 task 3b: run_gate reports progress while the script runs ----------------------------

    /// <summary>Records every <see cref="ProgressNotificationValue"/> the MCP client hands back for a
    /// <c>run_gate</c> call made with a progress sink attached.</summary>
    private sealed class RecordingProgress : IProgress<ProgressNotificationValue>
    {
        private readonly List<ProgressNotificationValue> _reports = new();
        public IReadOnlyList<ProgressNotificationValue> Reports { get { lock (_reports) return _reports.ToList(); } }
        public void Report(ProgressNotificationValue value) { lock (_reports) _reports.Add(value); }
    }

    [Fact]
    public async Task run_gate_reports_progress_every_interval_while_the_script_runs()
    {
        ImportSkill("gated", GatedSkillMd, new Dictionary<string, string> { ["scripts/check-it.ps1"] = "exit 0\n" });
        var progress = new RecordingProgress();
        CallToolResult? gateResult = null;
        _runner.Handler = async (spec, _, ct) =>
        {
            // 40 ms GateProgressInterval (InitializeAsync) * 5 intervals = 200 ms of gate wall time.
            if (spec.Label.StartsWith("run_gate/")) { await Task.Delay(TimeSpan.FromMilliseconds(200), ct); return FakeProcessRunner.Ok("ok"); }
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
            {
                await using var client = await _host.ClientFor("sonnet");
                gateResult = await client.CallToolAsync("run_gate",
                    new Dictionary<string, object?> { ["room_id"] = "lab", ["gate"] = "check-it" }, progress: progress);
            }
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostRunStart("gated");
        await WaitUntil(() => gateResult is not null, TimeSpan.FromSeconds(20));

        var json = HubTestHost.Json(gateResult!);
        Assert.Equal("check-it", json.GetProperty("gate").GetString());
        var reports = progress.Reports;
        Assert.True(reports.Count >= 3, $"expected at least 3 progress reports, saw {reports.Count}");
        Assert.All(reports, r => Assert.Contains("check-it", r.Message));
    }

    [Fact]
    public async Task run_gate_with_no_progress_sink_still_records_the_outcome()
    {
        ImportSkill("gated", GatedSkillMd, new Dictionary<string, string> { ["scripts/check-it.ps1"] = "exit 0\n" });
        CallToolResult? gateResult = null;
        _runner.Handler = async (spec, _, ct) =>
        {
            if (spec.Label.StartsWith("run_gate/")) { await Task.Delay(TimeSpan.FromMilliseconds(200), ct); return FakeProcessRunner.Ok("no progress sink here\n"); }
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
            {
                await using var client = await _host.ClientFor("sonnet");
                gateResult = await CallRunGate(client, "lab", "check-it");
            }
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostRunStart("gated");
        await WaitUntil(() => gateResult is not null, TimeSpan.FromSeconds(20));

        var json = HubTestHost.Json(gateResult!);
        Assert.Equal("check-it", json.GetProperty("gate").GetString());
        Assert.Equal(0, json.GetProperty("exit_code").GetInt32());
        Assert.False(json.GetProperty("timed_out").GetBoolean());
        Assert.Equal("no progress sink here\n", json.GetProperty("stdout").GetString());

        var row = Assert.Single(AllGateRunRows(), r => r.Gate == "check-it");
        Assert.Equal("exit 0", row.Outcome);
    }
}
