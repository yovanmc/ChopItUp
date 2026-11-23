using System.Net.Http.Json;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>Each message a spawn posts carries that spawn's id, and the usage block it
/// reported lands on exactly one of its rows; "not reported" is a stamped row with no block. Each
/// launch also writes one task row before its process starts, completed once at its finish.</summary>
public sealed class SpawnerServiceUsageTests : SpawnerServiceTestBase
{
    private MessageStore Store => _host.Services.GetRequiredService<MessageStore>();
    private TaskStore Tasks => _host.Services.GetRequiredService<TaskStore>();

    private async Task<TaskRecord> WaitForTask(string room, string spawnId)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (Tasks.ForRoom(room, 200).SingleOrDefault(t => t.Start.SpawnId == spawnId) is { End: not null } done) return done;
            await Task.Delay(25);
        }
        throw new TimeoutException($"task {spawnId} never finished");
    }

    private static string SpawnIdOf(ProcessSpec spec) => spec.Label.Split('/')[1];

    private static string Result(string text, string model, int tokens) =>
        """{"type":"result","subtype":"success","session_id":"s","result":""" + "\"" + text + "\"," + UsageFields(model, tokens) + "}";

    private static string Block(string model, int tokens) => """{"host":"claude",""" + UsageFields(model, tokens) + "}";

    private static string UsageFields(string model, int tokens) =>
        "\"usage\":{\"output_tokens\":" + tokens + "},\"modelUsage\":{\"" + model + "\":{\"outputTokens\":" + tokens + "}}";

    private Message Row(string room, string body)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            if (Store.ReadLast(room, 100).SingleOrDefault(m => m.Body == body) is { } found) return found;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"no message \"{body}\" in {room}");
            Thread.Sleep(25);
        }
    }

    private async Task<SpawnUsage> WaitForUsage(long id)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (Store.GetSpawnUsage(id) is { Usage: not null } u) return u;
            await Task.Delay(25);
        }
        throw new TimeoutException($"message #{id} never got a usage block");
    }

    private async Task<SpawnUsage> WaitForStamp(long id)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (Store.GetSpawnUsage(id) is { SpawnId: not null } u) return u;
            await Task.Delay(25);
        }
        throw new TimeoutException($"message #{id} was never stamped");
    }

    [Fact]
    public async Task A_spawn_that_posts_twice_stamps_both_rows_and_keeps_its_usage_on_the_last_only()
    {
        var openBeforeStart = false;
        _runner.Handler = async (spec, _, _) =>
        {
            openBeforeStart = Tasks.ForRoom("general", 10).Any(t => t.Start.SpawnId == SpawnIdOf(spec) && t.End is null);
            await PostAs("opus", "usage first");
            await PostAs("opus", "usage last");
            return FakeProcessRunner.Ok(Result("done", "claude-opus-5-5", 7));
        };
        await PostAsOwner("@opus two parts please");
        var spec = await _runner.NextSpecAsync(Wait);
        await WaitForStatus("concluded");

        var last = await WaitForUsage(Row("general", "usage last").Id);
        Assert.Equal(new SpawnUsage(SpawnIdOf(spec), Block("claude-opus-5-5", 7)), last);
        Assert.Equal(new SpawnUsage(SpawnIdOf(spec), null), Store.GetSpawnUsage(Row("general", "usage first").Id));
        Assert.Equal(new SpawnUsage(null, null), Store.GetSpawnUsage(Row("general", "@opus two parts please").Id));

        var task = await WaitForTask("general", SpawnIdOf(spec));
        Assert.True(openBeforeStart);
        var root = Row("general", "@opus two parts please").Id;
        Assert.Equal(new TaskStart(SpawnIdOf(spec), "general", "opus", "claude", "opus", null, root, 1, null, false, task.Start.StartedAt), task.Start);
        Assert.Equal(new TaskEnd(task.End!.EndedAt, 1, 0, TaskTermination.Completed, null, 2, Block("claude-opus-5-5", 7)), task.End);
        Assert.True(task.End.EndedAt >= task.Start.StartedAt);
    }

    [Fact]
    public async Task A_runner_that_throws_finishes_its_task_as_launch_failed()
    {
        _runner.Handler = (_, _, _) => throw new InvalidOperationException("no such file");
        await PostAsOwner("@opus never starts");
        var spec = await _runner.NextSpecAsync(Wait);

        var task = await WaitForTask("general", SpawnIdOf(spec));
        Assert.Equal((TaskTermination.LaunchFailed, (int?)null, (int?)null, 0, (string?)null),
            (task.End!.Termination, task.End.ExitCode, task.End.ToolCalls, task.End.Posts, task.End.Usage));
    }

    [Fact]
    public async Task A_spawn_that_could_not_be_started_writes_no_task()
    {
        File.WriteAllText(Path.Combine(_dir, "spawns"), "a file where the spawns folder goes");
        await PostAsOwner("@opus no folder for you");
        await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("could not be started"));
        await Spawner.InLoopAsync(() => true);

        Assert.Empty(Tasks.ForRoom("general", 10));
    }

    [Fact]
    public async Task A_directory_spawn_with_hostile_stream_lines_still_finishes_its_task()
    {
        await MakeRoom("lab-hostile");
        _runner.Handler = async (_, _, _) =>
        {
            await PostAsIn("opus", "lab-hostile", "hostile reply");
            return FakeProcessRunner.Ok("{\"type\":\"assistant\",\"message\":{\"content\":[1,\"x\",{\"type\":\"tool_use\"}]}}\n{\"type\":\"assistant\",\"message\":[1]}\nnot json\n"
                + Result("done", "claude-opus-5-5", 1) + "\n");
        };
        await PostAsOwnerIn("lab-hostile", "@opus odd output");
        var spec = await _runner.NextSpecAsync(Wait);

        var task = await WaitForTask("lab-hostile", SpawnIdOf(spec));
        Assert.Equal((TaskTermination.Completed, (int?)1, 1), (task.End!.Termination, task.End.ToolCalls, task.End.Posts));
    }

    [Fact]
    public async Task A_panel_first_pass_over_the_output_limit_finishes_as_output_limit_with_no_tool_count()
    {
        Mode("panel");
        _runner.Handler = (spec, _, _) => Task.FromResult(!spec.WorkingDirectory.EndsWith("synthesis") && FakeProcessRunner.ParticipantOf(spec) == "opus"
            ? new ProcessResult(null, false, false, "", "", TimeSpan.FromMilliseconds(5), OutputLimitExceeded: true)
            : FakeProcessRunner.Ok(Result("panel ok", "claude-sonnet-5-5", 1)));
        await PostAsOwner("Panel with an overflow.");
        var specs = new[] { await _runner.NextSpecAsync(Wait), await _runner.NextSpecAsync(Wait) };
        var opus = specs.Single(s => FakeProcessRunner.ParticipantOf(s) == "opus");

        var task = await WaitForTask("general", SpawnIdOf(opus));
        Assert.Equal((TaskTermination.OutputLimit, (int?)null, (int?)null, 5L), (task.End!.Termination, task.End.ExitCode, task.End.ToolCalls, task.End.DurationMs));
    }

    [Fact]
    public async Task A_timed_out_spawn_still_stores_a_result_line_it_printed_before_the_kill()
    {
        _runner.Handler = async (_, timeout, _) =>
        {
            await PostAs("opus", "posted then hung");
            return new ProcessResult(null, true, false, Result("done", "claude-opus-5-5", 3), "", timeout);
        };
        await PostAsOwner("@opus answer then hang");
        var spec = await _runner.NextSpecAsync(Wait);

        Assert.Equal(new SpawnUsage(SpawnIdOf(spec), Block("claude-opus-5-5", 3)), await WaitForUsage(Row("general", "posted then hung").Id));
        var task = await WaitForTask("general", SpawnIdOf(spec));
        Assert.Equal((TaskTermination.TimedOut, (int?)null, 1, (long)Fast.Timeout.TotalMilliseconds), (task.End!.Termination, task.End.ExitCode, task.End.Posts, task.End.DurationMs));
    }

    [Fact]
    public async Task A_killed_spawn_with_no_result_line_is_stamped_and_reads_as_not_reported()
    {
        _runner.Handler = async (_, timeout, cancellation) =>
        {
            await PostAs("opus", "posted then killed");
            return await FakeProcessRunner.HangUntilKilled(timeout, cancellation);
        };
        await PostAsOwner("@opus answer then stall");
        var spec = await _runner.NextSpecAsync(Wait);
        await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("did not exit within"));
        await Spawner.InLoopAsync(() => true);

        Assert.Equal(new SpawnUsage(SpawnIdOf(spec), null), Store.GetSpawnUsage(Row("general", "posted then killed").Id));
    }

    [Fact]
    public async Task A_plain_codex_reply_quoting_an_event_line_is_stamped_and_reads_as_not_reported()
    {
        _runner.Handler = async (_, _, _) =>
        {
            await PostAs("gpt-6-astra", "codex reply");
            return FakeProcessRunner.Ok("codex reply\n{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1}}\n");
        };
        await PostAsOwner("@gpt-6-astra show me an event");
        var spec = await _runner.NextSpecAsync(Wait);
        await WaitForStatus("concluded");
        await Spawner.InLoopAsync(() => true);

        Assert.Equal(new SpawnUsage(SpawnIdOf(spec), null), Store.GetSpawnUsage(Row("general", "codex reply").Id));
        var task = await WaitForTask("general", SpawnIdOf(spec));
        Assert.Equal(("codex", "gpt-6-astra", TaskTermination.Completed, (int?)null, (string?)null),
            (task.Start.Host, task.Start.RequestedModel, task.End!.Termination, task.End.ToolCalls, task.End.Usage));
    }

    [Fact]
    public async Task A_spawn_that_posts_nothing_writes_nothing()
    {
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok(Result("only stdout", "claude-opus-5-5", 2)));
        await PostAsOwner("@opus stay quiet");
        await _runner.NextSpecAsync(Wait);
        await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("replied without posting"));
        await Spawner.InLoopAsync(() => true);

        Assert.All(Store.ReadLast("general", 100), m => Assert.Equal(new SpawnUsage(null, null), Store.GetSpawnUsage(m.Id)));
    }

    [Fact]
    public async Task A_directory_room_stream_json_spawn_stores_its_usage()
    {
        await MakeRoom("lab");
        _runner.Handler = async (_, _, _) =>
        {
            await PostAsIn("opus", "lab", "lab reply");
            return FakeProcessRunner.Ok("{\"type\":\"system\",\"subtype\":\"init\"}\n"
                + "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Bash\"},{\"type\":\"tool_use\",\"id\":\"t2\",\"name\":\"mcp__chopitup__post_message\"}]}}\n"
                + Result("done", "claude-opus-5-5", 5) + "\n");
        };
        await PostAsOwnerIn("lab", "@opus work here");
        var spec = await _runner.NextSpecAsync(Wait);

        Assert.Equal(new SpawnUsage(SpawnIdOf(spec), Block("claude-opus-5-5", 5)), await WaitForUsage(Row("lab", "lab reply").Id));
        var task = await WaitForTask("lab", SpawnIdOf(spec));
        Assert.Equal((TaskTermination.Completed, (int?)2, 1), (task.End!.Termination, task.End.ToolCalls, task.End.Posts));
    }

    [Fact]
    public async Task A_relay_answer_the_hub_posts_carries_each_spawns_id_and_usage()
    {
        Mode("relay");
        _runner.Handler = (spec, _, _) => Task.FromResult(FakeProcessRunner.Ok(FakeProcessRunner.ParticipantOf(spec) == "sonnet"
            ? Result("relay first", "claude-sonnet-5-5", 11) : Result("relay second", "claude-opus-5-5", 12)));
        await PostAsOwner("Relay this.");
        var first = await _runner.NextSpecAsync(Wait);
        var second = await _runner.NextSpecAsync(Wait);
        await WaitForStatus("concluded");

        Assert.Equal(new SpawnUsage(SpawnIdOf(first), Block("claude-sonnet-5-5", 11)), await WaitForUsage(Row("general", "relay first").Id));
        Assert.Equal(new SpawnUsage(SpawnIdOf(second), Block("claude-opus-5-5", 12)), await WaitForUsage(Row("general", "relay second").Id));
    }

    [Fact]
    public async Task A_relay_leg_that_posts_itself_keeps_its_usage_on_its_own_row_and_no_answer_row_is_added()
    {
        Mode("relay");
        _runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) != "sonnet") return FakeProcessRunner.Ok(Result("relay closing", "claude-opus-5-5", 14));
            await PostAs("sonnet", "relay self post");
            return FakeProcessRunner.Ok(Result("relay self post", "claude-sonnet-5-5", 13));
        };
        await PostAsOwner("Relay with a post.");
        var first = await _runner.NextSpecAsync(Wait);
        await WaitForStatus("concluded");

        Assert.Equal(new SpawnUsage(SpawnIdOf(first), Block("claude-sonnet-5-5", 13)), await WaitForUsage(Row("general", "relay self post").Id));
        Assert.Single(Store.ReadLast("general", 100), m => m.AuthorId == "sonnet");
    }

    [Fact]
    public async Task A_cancelled_spawn_still_stores_a_result_line_it_printed_before_the_stop()
    {
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (_, _, cancellation) =>
        {
            await PostAs("opus", "posted then stopped");
            posted.SetResult();
            try { await Task.Delay(Timeout.Infinite, cancellation); } catch (OperationCanceledException) { }
            return new ProcessResult(null, false, true, Result("done", "claude-opus-5-5", 4), "", TimeSpan.Zero);
        };
        await PostAsOwner("@opus answer then wait for the stop");
        var spec = await _runner.NextSpecAsync(Wait);
        await posted.Task.WaitAsync(Wait);
        await Spawner.StopAsync("general");

        Assert.Equal(new SpawnUsage(SpawnIdOf(spec), Block("claude-opus-5-5", 4)), await WaitForUsage(Row("general", "posted then stopped").Id));
        Assert.Equal(TaskTermination.Cancelled, (await WaitForTask("general", SpawnIdOf(spec))).End!.Termination);
    }

    [Fact]
    public async Task A_codex_directory_spawn_stores_the_usage_of_its_last_turn()
    {
        await MakeRoom("lab-codex");
        _runner.Handler = async (_, _, _) =>
        {
            await PostAsIn("gpt-6-astra", "lab-codex", "codex lab reply");
            return FakeProcessRunner.Ok("{\"type\":\"turn.started\"}\n"
                + "{\"type\":\"item.started\",\"item\":{\"id\":\"i1\",\"type\":\"command_execution\"}}\n{\"type\":\"item.completed\",\"item\":{\"id\":\"i1\",\"type\":\"command_execution\"}}\n"
                + "{\"type\":\"item.completed\",\"item\":{\"id\":\"i2\",\"type\":\"agent_message\"}}\n"
                + "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":15,\"output_tokens\":6}}\n");
        };
        await PostAsOwnerIn("lab-codex", "@gpt-6-astra work here");
        var spec = await _runner.NextSpecAsync(Wait);

        Assert.Equal(new SpawnUsage(SpawnIdOf(spec), """{"host":"codex","usage":{"input_tokens":15,"output_tokens":6}}"""), await WaitForUsage(Row("lab-codex", "codex lab reply").Id));
        Assert.Equal((int?)1, (await WaitForTask("lab-codex", SpawnIdOf(spec))).End!.ToolCalls);
    }

    [Fact]
    public async Task Panel_first_passes_and_the_synthesis_each_carry_their_own_spawns_id_and_usage()
    {
        Mode("panel");
        _runner.Handler = (spec, _, _) => Task.FromResult(FakeProcessRunner.Ok(spec.WorkingDirectory.EndsWith("synthesis")
            ? Result("panel combined", "claude-sonnet-5-5", 30)
            : FakeProcessRunner.ParticipantOf(spec) == "sonnet" ? Result("panel sonnet", "claude-sonnet-5-5", 21) : Result("panel opus", "claude-opus-5-5", 22)));
        await PostAsOwner("Panel this.");
        var specs = new[] { await _runner.NextSpecAsync(Wait), await _runner.NextSpecAsync(Wait), await _runner.NextSpecAsync(Wait) };
        await WaitForStatus("concluded");

        var synthesis = specs.Single(s => s.WorkingDirectory.EndsWith("synthesis"));
        var sonnet = specs.Single(s => s != synthesis && FakeProcessRunner.ParticipantOf(s) == "sonnet");
        var opus = specs.Single(s => FakeProcessRunner.ParticipantOf(s) == "opus");
        Assert.Equal(new SpawnUsage(SpawnIdOf(sonnet), Block("claude-sonnet-5-5", 21)), await WaitForUsage(Row("general", "panel sonnet").Id));
        Assert.Equal(new SpawnUsage(SpawnIdOf(opus), Block("claude-opus-5-5", 22)), await WaitForUsage(Row("general", "panel opus").Id));
        Assert.Equal(new SpawnUsage(SpawnIdOf(synthesis), Block("claude-sonnet-5-5", 30)), await WaitForUsage(Row("general", "panel combined").Id));
        foreach (var leg in specs)
        {
            var task = await WaitForTask("general", SpawnIdOf(leg));
            Assert.Equal((FakeProcessRunner.ParticipantOf(leg), (string?)null, TaskTermination.Completed, (int?)null, 0),
                (task.Start.ParticipantId, task.Start.Effort, task.End!.Termination, task.End.ToolCalls, task.End.Posts));
        }
    }

    [Fact]
    public async Task A_hub_stopped_while_a_spawn_that_posted_still_runs_keeps_the_stamp_without_usage()
    {
        await using var host = await HubTestHost.StartAsync(_dir + "_stop", processRunner: _runner, limits: Fast with { Timeout = TimeSpan.FromMinutes(5) });
        host.AuthorizeAs(ChopDb.OwnerParticipantId);
        var store = host.Services.GetRequiredService<MessageStore>();
        _runner.Handler = async (_, timeout, cancellation) =>
        {
            await using var client = await host.ClientFor("opus");
            HubTestHost.Json(await client.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "posted before stop" }));
            return await FakeProcessRunner.HangUntilKilled(timeout, cancellation);
        };
        var post = await host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "@opus then the hub stops" });
        Assert.Equal(System.Net.HttpStatusCode.Created, post.StatusCode);
        var spec = await _runner.NextSpecAsync(Wait);

        var deadline = DateTime.UtcNow + Wait;
        Message? row = null;
        while (DateTime.UtcNow < deadline && (row = store.ReadLast("general", 100).SingleOrDefault(m => m.Body == "posted before stop")) is null) await Task.Delay(25);
        Assert.NotNull(row);
        while (DateTime.UtcNow < deadline && store.GetSpawnUsage(row!.Id)!.SpawnId is null) await Task.Delay(25);
        await host.Services.GetRequiredService<SpawnerService>().StopAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(new SpawnUsage(SpawnIdOf(spec), null), store.GetSpawnUsage(row!.Id));
        var open = Assert.Single(host.Services.GetRequiredService<TaskStore>().ForRoom("general", 10));
        Assert.Equal((SpawnIdOf(spec), (TaskEnd?)null), (open.Start.SpawnId, open.End));
    }
}
