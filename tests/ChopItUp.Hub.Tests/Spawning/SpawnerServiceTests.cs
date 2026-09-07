using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Skills;
using ChopItUp.Hub.Spawning;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed partial class SpawnerServiceTests : IAsyncLifetime
{
    private static readonly SpawnLimits Fast = new(Budget: 4, Debounce: TimeSpan.FromMilliseconds(150), MinSpacing: TimeSpan.Zero, Timeout: TimeSpan.FromSeconds(1), TranscriptMessages: 60, TranscriptChars: 24_000);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_spawner_" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new();
    private HubTestHost _host = null!;

    public async Task InitializeAsync() => _host = await HubTestHost.StartAsync(_dir, processRunner: _runner, limits: Fast);
    public async Task DisposeAsync() => await _host.DisposeAsync();

    private SpawnerService Spawner => _host.Services.GetRequiredService<SpawnerService>();

    private async Task PostAsOwner(string body)
    {
        var r = await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
    }

    private async Task PostAs(string participant, string body, string? clientKey = null)
    {
        await using var client = await _host.ClientFor(participant);
        var args = new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = body };
        if (clientKey is not null) args["client_key"] = clientKey;
        HubTestHost.Json(await client.CallToolAsync("post_message", args));
    }

    private async Task<List<(string Author, string Body)>> Messages()
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/general/messages?afterId=0&limit=200"));
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("authorId").GetString()!, m.GetProperty("body").GetString()!)).ToList();
    }

    private async Task<(string Author, string Body)> WaitForMessage(Func<(string Author, string Body), bool> match)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var hit = (await Messages()).FirstOrDefault(match);
            if (hit != default) return hit;
            await Task.Delay(100);
        }
        throw new TimeoutException("No matching message within " + Wait);
    }

    private async Task<ExchangeSnapshot> WaitForStatus(string status)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var s = Spawner.Snapshot("general");
            if (s.Status == status) return s;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Exchange never reached '{status}'; last was '{Spawner.Snapshot("general").Status}'.");
    }

    private string ClaudeTokenIn(ProcessSpec spec)
    {
        // From the fake's launch-time snapshot, never the file: the work dir may already be gone.
        using var doc = JsonDocument.Parse(_runner.McpJsonOf(spec) ?? throw new InvalidOperationException("the fake captured no mcp.json for this spec"));
        return doc.RootElement.GetProperty("mcpServers").GetProperty("chopitup").GetProperty("headers").GetProperty("Authorization").GetString()!["Bearer ".Length..];
    }

    /// <summary>Writes a fixture skill straight into the store's directory and records its fingerprint
    /// in the same `chopitup.db` the running hub uses (via the DI-registered <see cref="ChopDb"/>),
    /// exactly the shape Task 5's --import-skill produces. Row 11 fixtures only - no third-party
    /// skill text (D-g).</summary>
    private void WriteSkill(string name, string body)
    {
        var dir = Path.Combine(_dir, "skills", name);
        Directory.CreateDirectory(dir);
        var bytes = new UTF8Encoding(false).GetBytes(body);
        File.WriteAllBytes(Path.Combine(dir, "SKILL.md"), bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        new SkillHashes(_host.Services.GetRequiredService<ChopDb>()).Record(name, hash, "test-fixture");
    }

    [Fact]
    public async Task A1_A3_A7_an_owner_mention_spawns_the_row_with_the_right_command_prompt_and_token_then_the_chain_concludes()
    {
        _runner.Handler = async (spec, _, _) =>
        {
            switch (FakeProcessRunner.ParticipantOf(spec))
            {
                case "opus": await PostAs("opus", "I think so. @gpt-6-astra, a second opinion?"); break;
                case "gpt-6-astra": await PostAs("gpt-6-astra", "Agreed, nothing to add."); break;
            }
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwner("@opus what do you think of the plan?");

        var opus = await _runner.NextSpecAsync(Wait);
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(opus));
        Assert.Contains("--model", opus.Arguments); Assert.Contains("opus", opus.Arguments);
        Assert.Contains("--allowedTools", opus.Arguments);
        Assert.Empty(opus.Environment);
        Assert.Equal(_host.TokenFor("opus"), ClaudeTokenIn(opus));
        Assert.DoesNotContain(opus.Arguments, a => a.Contains(_host.TokenFor("opus")));
        Assert.StartsWith(Path.Combine(_dir, "spawns"), opus.WorkingDirectory);
        Assert.True(Path.IsPathRooted(opus.WorkingDirectory), $"expected a rooted work dir, got '{opus.WorkingDirectory}'");
        var mcpConfigArg = opus.Arguments[opus.Arguments.ToList().IndexOf("--mcp-config") + 1];
        Assert.True(Path.IsPathRooted(mcpConfigArg), $"expected a rooted --mcp-config path, got '{mcpConfigArg}'");
        Assert.Contains("what do you think of the plan?", opus.StandardInput);
        Assert.Contains("Turn 1 of 4; 3 turn(s) remain after yours.", opus.StandardInput);
        Assert.Contains("#1 owner", opus.StandardInput);

        var astra = await _runner.NextSpecAsync(Wait);
        Assert.Equal("gpt-6-astra", FakeProcessRunner.ParticipantOf(astra));
        Assert.Contains("-m", astra.Arguments); Assert.Contains("gpt-6-astra", astra.Arguments);
        Assert.Equal(_host.TokenFor("gpt-6-astra"), astra.Environment["CHOPITUP_TOKEN"]);
        Assert.DoesNotContain(astra.Arguments, a => a.Contains(_host.TokenFor("gpt-6-astra")));
        Assert.Contains("a second opinion?", astra.StandardInput);
        Assert.Contains("Turn 2 of 4; 2 turn(s) remain after yours.", astra.StandardInput);
        Assert.Contains("message(s) #2 mentioned you", astra.StandardInput);

        var note = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("Exchange concluded"));
        Assert.Equal("Exchange concluded: 2 of 4 turns used.", note.Body);
        var snap = await WaitForStatus("concluded");
        Assert.Equal(2, snap.TurnsUsed);
        Assert.Equal(1, snap.RootMessageId);
        Assert.Empty(Directory.Exists(Path.Combine(_dir, "spawns")) ? Directory.GetDirectories(Path.Combine(_dir, "spawns")) : Array.Empty<string>());
        Assert.Equal(["owner", "opus", "gpt-6-astra", ChopDb.HubParticipantId], (await Messages()).Select(m => m.Author));
    }

    [Fact]
    public async Task A2_the_budget_stops_the_chain_and_the_last_turn_is_told_so()
    {
        _runner.Handler = async (spec, _, _) =>
        {
            var me = FakeProcessRunner.ParticipantOf(spec);
            var other = me == "opus" ? "sonnet" : "opus";
            await PostAs(me, $"@{other} your move");
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwner("@opus play ping-pong with sonnet");
        var prompts = new List<string>();
        for (int i = 0; i < 4; i++) prompts.Add((await _runner.NextSpecAsync(Wait)).StandardInput);
        Assert.Contains("Turn 4 of 4; 0 turn(s) remain after yours.", prompts[3]);
        Assert.Contains("This is the last turn", prompts[3]);
        Assert.DoesNotContain("This is the last turn", prompts[2]);

        var refused = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("not spawning"));
        Assert.Contains("Budget of 4 turns is used up", refused.Body);
        var note = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("Exchange concluded"));
        Assert.Equal("Exchange concluded: 4 of 4 turns used.", note.Body);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(4, _runner.Count);
    }

    [Fact]
    public async Task A1_A2_windows_the_owner_the_hub_and_a_model_outside_an_exchange_spawn_nothing()
    {
        await PostAsOwner("@claude @codex @owner @hub hello windows");
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));
        Assert.Equal("idle", Spawner.Snapshot("general").Status);

        await PostAs("codex", "@opus please weigh in");   // a window's mention with no open exchange (D2)
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));
        Assert.Equal("idle", Spawner.Snapshot("general").Status);
    }

    [Fact]
    public async Task A4_a_spawn_past_the_timeout_is_killed_and_noted()
    {
        _runner.Handler = (_, timeout, ct) => FakeProcessRunner.HangUntilKilled(timeout, ct);
        await PostAsOwner("@sonnet take forever");
        await _runner.NextSpecAsync(Wait);
        var note = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("did not reply"));
        Assert.Equal("@sonnet did not reply within 1 second(s) and was stopped.", note.Body);
        Assert.Equal("Exchange concluded: 1 of 4 turns used.", (await WaitForMessage(m => m.Body.Contains("Exchange concluded"))).Body);
    }

    [Fact]
    public async Task A4_a_spawn_that_exits_without_posting_has_its_reply_posted_for_it_with_the_token_scrubbed()
    {
        _runner.Handler = (spec, _, _) => Task.FromResult(FakeProcessRunner.Ok(
            JsonSerializer.Serialize(new { type = "result", result = "Here is my answer, and by the way the token is " + ClaudeTokenIn(spec) })));
        await PostAsOwner("@opus answer without the tool");
        var note = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("replied without posting"));
        Assert.StartsWith("@opus replied without posting to the room (exit code 0). Its reply:", note.Body);
        Assert.Contains("Here is my answer", note.Body);
        Assert.DoesNotContain(_host.TokenFor("opus"), note.Body);
        Assert.Contains("<token>", note.Body);

        _runner.Handler = (_, _, _) => Task.FromResult(new ProcessResult(2, false, false, "", "boom: something failed\n", TimeSpan.Zero));
        await PostAsOwner("@sonnet crash");
        var crash = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("exited with code 2"));
        Assert.Contains("boom: something failed", crash.Body);
    }

    [Fact]
    public async Task A4_b_ansi_escape_codes_in_stderr_are_stripped_before_posting()
    {
        var esc = ((char)0x1b).ToString();
        _runner.Handler = (_, _, _) => Task.FromResult(new ProcessResult(1, false, false, "", esc + "[31mError: boom" + esc + "[0m", TimeSpan.Zero));
        await PostAsOwner("@sonnet crash");
        var note = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("exited with code 1"));
        Assert.Contains("Error: boom", note.Body);
        Assert.DoesNotContain(esc, note.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A6_an_owner_message_mid_exchange_lets_the_running_spawn_finish_ignores_its_mentions_and_re_roots()
    {
        var releaseOpus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGpt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            switch (FakeProcessRunner.ParticipantOf(spec))
            {
                case "opus":
                    await releaseOpus.Task.WaitAsync(ct);
                    await PostAs("opus", "late: @sonnet @fable please");
                    break;
                case "gpt-5.5":
                    await releaseGpt.Task.WaitAsync(ct);   // keeps the NEW exchange open while opus posts late
                    await PostAs("gpt-5.5", "taken, done");
                    break;
            }
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwner("@opus think slowly");
        await _runner.NextSpecAsync(Wait);
        await PostAsOwner("@gpt-5.5 actually, you take it");
        var gpt = await _runner.NextSpecAsync(Wait);
        Assert.Equal("gpt-5.5", FakeProcessRunner.ParticipantOf(gpt));
        var snap = Spawner.Snapshot("general");
        Assert.Equal("open", snap.Status);
        Assert.Equal(2, snap.RootMessageId);
        Assert.Equal(1, snap.TurnsCommitted);

        releaseOpus.SetResult();
        await WaitForMessage(m => m.Author == "opus");
        await Task.Delay(300);                                                        // let the loop process the post
        var after = Spawner.Snapshot("general");
        Assert.Equal("open", after.Status);                                           // gpt-5.5 still in flight
        Assert.Equal(1, after.TurnsCommitted);                                        // opus's @sonnet @fable bought nothing (B2)
        Assert.Empty(after.Pending);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));

        releaseGpt.SetResult();
        var final = await WaitForStatus("concluded");
        Assert.Equal(2, final.RootMessageId);
        Assert.Equal(1, final.TurnsUsed);
        Assert.Single(await Messages(), m => m.Body.StartsWith("Exchange concluded"));
    }


    [Fact]
    public async Task A5_a_participant_is_never_in_flight_twice_in_one_room()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "opus") await release.Task.WaitAsync(ct);
            else await PostAs("sonnet", "@opus back to you");
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };
        await PostAsOwner("@opus @sonnet both");
        var first = await _runner.NextSpecAsync(Wait);
        var second = await _runner.NextSpecAsync(Wait);
        Assert.Equal(new[] { "opus", "sonnet" }.Order(), new[] { FakeProcessRunner.ParticipantOf(first), FakeProcessRunner.ParticipantOf(second) }.Order());
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));   // sonnet's @opus waits: opus is in flight
        Assert.Contains("opus", Spawner.Snapshot("general").Pending);
        release.SetResult();
        var third = await _runner.NextSpecAsync(Wait);                              // now it runs
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(third));
        Assert.Contains("Turn 3 of 4", third.StandardInput);
    }

    // --- Task 4: the exchange carries the skill; the prompt renders it ----------------------------

    [Fact]
    public async Task Skill_04_an_owner_invocation_renders_the_skill_into_every_spawn_of_the_exchange_it_roots()
    {
        WriteSkill("demo", "# Demo Skill\n\nDo the demo thing.\n");
        _runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet") await PostAs("sonnet", "@opus your turn");
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwner("/demo @sonnet please begin");
        var first = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(first));
        Assert.Contains("Skill in force for this exchange: demo.", first.StandardInput);
        Assert.Contains("Do the demo thing.", first.StandardInput);

        var second = await _runner.NextSpecAsync(Wait);                            // a later turn of the SAME exchange
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(second));
        Assert.Contains("Skill in force for this exchange: demo.", second.StandardInput);

        var note = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("is in force for this exchange"));
        Assert.Equal("Skill /demo is in force for this exchange; every turn of it is rendered the same instruction.", note.Body);
    }

    [Fact]
    public async Task Skill_04_an_unknown_skill_name_launches_nothing_and_names_what_is_installed()
    {
        WriteSkill("demo", "Demo body.\n");
        await PostAsOwner("/nope @sonnet do something");
        var note = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("No skill named"));
        Assert.Equal("No skill named '/nope'. Installed: /demo.", note.Body);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));
        Assert.Equal("idle", Spawner.Snapshot("general").Status);
    }

    [Fact]
    public async Task Skill_04_a_model_typed_slash_line_is_ordinary_text_and_carries_no_skill()
    {
        WriteSkill("demo", "Demo body.\n");
        _runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "opus") await PostAs("opus", "/demo @sonnet handing off");
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwner("@opus start (no skill invoked)");
        var first = await _runner.NextSpecAsync(Wait);
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(first));
        Assert.DoesNotContain("Skill in force", first.StandardInput);

        var second = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(second));
        Assert.DoesNotContain("Skill in force", second.StandardInput);              // opus's /demo is prose, not an invocation
        Assert.Contains("/demo @sonnet handing off", second.StandardInput);         // carried verbatim, as any other message body
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));
    }

    /// <summary>M-8: nothing tested the row's own control before this. Mutating the fixture after its
    /// hash was recorded reproduces exactly what a directory-room spawn with shell access could do
    /// to the store (D-i's threat model) — the read must refuse rather than render the new bytes.</summary>
    [Fact]
    public async Task Skill_04_M8_a_skill_edited_after_import_is_tampered_and_launches_nothing()
    {
        WriteSkill("demo", "Original body.\n");
        File.WriteAllText(Path.Combine(_dir, "skills", "demo", "SKILL.md"), "Mutated body!\n");   // hash now stale

        await PostAsOwner("/demo @sonnet please begin");
        var note = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("does not match what was imported"));
        Assert.Equal("Skill /demo does not match what was imported; nothing was spawned. Re-import it with --import-skill before using it.", note.Body);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(0, _runner.Count);
    }

    /// <summary>M-8: the <c>ReadAllBytes</c> seam (mirroring <c>ChopDb.BackupDestinationFactory</c>) is
    /// the only way to reach <c>Unavailable</c> from outside the store — a real disk failure is not
    /// reproducible in a test.</summary>
    [Fact]
    public async Task Skill_04_M8_a_store_read_failure_is_unavailable_not_unknown_and_launches_nothing()
    {
        WriteSkill("demo", "Body.\n");
        _host.Services.GetRequiredService<SkillStore>().ReadAllBytes = _ => throw new IOException("disk went away");

        await PostAsOwner("/demo @sonnet please begin");
        var note = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("Could not read skill"));
        Assert.Equal("Could not read skill /demo: disk went away. Nothing was spawned.", note.Body);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(0, _runner.Count);
    }

    /// <summary>4f / D-i measure (a), tested here rather than in SpawnCommandsTests: that file is
    /// outside this dispatch's Files list, and <see cref="SpawnCommands.ClaudeSettingsJson"/> keeps a
    /// backward-compatible no-arg overload precisely so that file needed no edit. The directory-room
    /// spawn path (SpawnerService.Launch) is what actually threads the data directory through, so
    /// this is where the resulting deny rules are observable end to end. Whether the rule BINDS on
    /// 2.1.220 is unverified (claim 23 only ever measured the `~/` shape) - the M11 live check probes
    /// that; this only pins that the rule is written.</summary>
    [Fact]
    public async Task Skill_04_4f_a_directory_room_spawns_settings_deny_read_write_and_edit_of_the_data_directory()
    {
        var dir = await MakeRoom("lab-skill-deny");
        string? settingsAtLaunch = null;
        _runner.Handler = (spec, _, _) =>
        {
            var args = spec.Arguments.ToList();
            settingsAtLaunch = File.ReadAllText(args[args.IndexOf("--settings") + 1]);
            return Task.FromResult(FakeProcessRunner.Ok("""{"type":"result","result":"done"}"""));
        };

        await PostAsOwnerIn("lab-skill-deny", "@sonnet look around");
        await _runner.NextSpecAsync(Wait);
        // The fake's channel-write races the handler body that sets settingsAtLaunch (FakeProcessRunner
        // queues the spec before awaiting Handler); wait for the trail note, which only posts after
        // OnFinished, to be sure the handler actually ran before reading the captured settings.json.
        await WaitForMessageIn("lab-skill-deny", m => m.Author == "hub" && m.Body.StartsWith(HubNotes.TrailPrefix));

        var forward = _dir.Replace('\\', '/');
        Assert.Contains($"\"Read({forward}/**)\"", settingsAtLaunch);
        Assert.Contains($"\"Write({forward}/**)\"", settingsAtLaunch);
        Assert.Contains($"\"Edit({forward}/**)\"", settingsAtLaunch);
    }
}

public sealed partial class SpawnerServiceTests
{
    [Fact]
    public async Task A6_stop_reaches_a_spawn_whose_exchange_was_superseded_and_the_room_shows_it_until_then()
    {
        // Owner: "@opus …" (opus runs long) then "never mind" (no mention): the exchange is superseded,
        // nothing new opens, opus is still a live process. GET must show it; stop must kill it (M1).
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) != "opus") return FakeProcessRunner.Ok("""{"result":"done"}""");
            seen = ct;
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return new ProcessResult(null, false, true, "", "", TimeSpan.Zero);
        };

        await PostAsOwner("@opus think slowly");
        await _runner.NextSpecAsync(Wait);
        await started.Task.WaitAsync(Wait);
        await PostAsOwner("never mind");
        await Task.Delay(300);
        var snap = Spawner.Snapshot("general");
        Assert.Equal("superseded", snap.Status);
        Assert.Equal(["opus"], snap.InFlight);

        var stopped = await Spawner.StopAsync("general");
        Assert.NotNull(stopped);
        Assert.True(seen.IsCancellationRequested);
        await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.StartsWith("Exchange stopped by the owner"));
        foreach (var _ in Enumerable.Range(0, 100))
        {
            if (Spawner.Snapshot("general").InFlight.Count == 0) break;
            await Task.Delay(50);
        }
        Assert.Empty(Spawner.Snapshot("general").InFlight);
        Assert.Null(await Spawner.StopAsync("general"));                                  // nothing left: 409 at the API
        Assert.DoesNotContain(await Messages(), m => m.Body.StartsWith("Exchange concluded"));
    }

    /// <summary>The CI runner has neither CLI on PATH; every other test in this file relies on
    /// HubTestHost's default locator never touching PATH. This test proves the seam directly: a hub
    /// booted with an explicit, recording locator never calls CliResolver.Resolve, and the spawn still
    /// reaches the fake runner.</summary>
    [Fact]
    public async Task Spawn_tests_never_consult_PATH_for_the_cli()
    {
        var recorded = new List<string>();
        CliLocator locator = name => { lock (recorded) recorded.Add(name); return new ResolvedCli($"fake-{name}.exe", [], $"fake-{name}.exe"); };
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_spawner_locator_" + Guid.NewGuid().ToString("N"));
        var runner = new FakeProcessRunner();
        await using var host = await HubTestHost.StartAsync(dir, processRunner: runner, limits: Fast, cliLocator: locator);

        var r = await host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "@opus what do you think?" });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);

        var spec = await runner.NextSpecAsync(Wait);
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(spec));
        lock (recorded) Assert.Contains("claude", recorded);
    }
}

/// <summary>The two timing rules that need room to be deterministic: a 2-second debounce (two HTTP
/// posts land well inside it on any machine) and a 1-second per-participant spacing across two
/// rooms (the second room is inserted raw; room creation is M9).</summary>
public sealed class SpawnerTimingTests : IAsyncLifetime
{
    private static readonly SpawnLimits Timed = new(Budget: 4, Debounce: TimeSpan.FromSeconds(2), MinSpacing: TimeSpan.FromSeconds(1), Timeout: TimeSpan.FromSeconds(30), TranscriptMessages: 60, TranscriptChars: 24_000);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_spawntime_" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new();
    private HubTestHost _host = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO rooms (id, name, created_at) VALUES ('second', 'Second', '2026-09-05T20:00:00.000+00:00')";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _host = await HubTestHost.StartAsync(_dir, processRunner: _runner, limits: Timed);
    }
    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task PostAsOwner(string room, string body)
    {
        var r = await _host.Client.PostAsJsonAsync($"api/rooms/{room}/messages", new { body });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
    }

    [Fact]
    public async Task A5_two_owner_messages_inside_the_debounce_window_start_one_spawn_citing_the_second()
    {
        await PostAsOwner("general", "@opus first");
        await PostAsOwner("general", "@opus and second");
        var spec = await _runner.NextSpecAsync(Wait);
        Assert.Contains("message(s) #2 mentioned you", spec.StandardInput);
        Assert.Contains("@opus first", spec.StandardInput);                          // still in the transcript
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task A5_one_participant_is_never_started_twice_within_the_spacing_across_rooms()
    {
        await PostAsOwner("general", "@sonnet here");
        await PostAsOwner("second", "@sonnet and here");
        var first = await _runner.NextSpecAsync(Wait);
        var second = await _runner.NextSpecAsync(Wait);
        Assert.All(new[] { first, second }, s => Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(s)));
        var runs = _runner.Runs;
        Assert.Equal(2, runs.Count);
        Assert.True(runs[1].At - runs[0].At >= TimeSpan.FromMilliseconds(900), $"gap was {runs[1].At - runs[0].At}");
        Assert.NotEqual(first.WorkingDirectory, second.WorkingDirectory);
    }
}
