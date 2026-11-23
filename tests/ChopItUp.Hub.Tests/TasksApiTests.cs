using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests;

/// <summary><c>GET /api/rooms/{roomId}/tasks</c>: the room's task rows newest first, with token
/// numbers read from each stored usage block. An open row whose spawn is not in flight reads as
/// interrupted, with its posts counted from the messages it stamped.</summary>
public sealed class TasksApiTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-22T09:00:00Z");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_tasks_api_" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new();
    private HubTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await HubTestHost.StartAsync(_dir, processRunner: _runner);
        _host.AuthorizeAs(ChopDb.OwnerParticipantId);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private TaskStore Tasks => _host.Services.GetRequiredService<TaskStore>();

    private async Task<(HttpStatusCode Status, JsonElement Body)> Get(string roomId = "general", string query = "")
    {
        var response = await _host.Client.GetAsync($"api/rooms/{roomId}/tasks{query}");
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text.Length == 0 ? "null" : text);
        return (response.StatusCode, doc.RootElement.Clone());
    }

    private static TaskStart Start(string id, DateTimeOffset at, string host = "claude") =>
        new(id, "general", host == "claude" ? "opus" : "gpt-6-astra", host, host == "claude" ? "opus" : "gpt-6-astra", null, 1, 1, null, false, at);

    [Fact]
    public async Task Tasks_list_newest_first_with_normalized_token_numbers_and_reported_models()
    {
        Tasks.Start(Start("claude-1", T0));
        Tasks.Finish("claude-1", new TaskEnd(T0.AddSeconds(5), 4_200, 0, TaskTermination.Completed, 3, 1,
            """{"host":"claude","usage":{"input_tokens":1},"modelUsage":{"claude-sonnet-5-5":{"inputTokens":10,"outputTokens":20,"cacheReadInputTokens":30,"cacheCreationInputTokens":40},"claude-haiku-5-5":{"inputTokens":1,"outputTokens":2}}}"""));
        Tasks.Start(Start("codex-1", T0.AddMinutes(1), host: "codex"));
        Tasks.Finish("codex-1", new TaskEnd(T0.AddMinutes(2), 9_000, 1, TaskTermination.Failed, null, 0,
            """{"host":"codex","usage":{"input_tokens":100,"cached_input_tokens":60,"cache_write_input_tokens":0,"output_tokens":7}}"""));

        var (status, body) = await Get();

        Assert.Equal(HttpStatusCode.OK, status);
        var rows = body.EnumerateArray().ToList();
        Assert.Equal(["codex-1", "claude-1"], rows.Select(r => r.GetProperty("spawnId").GetString()));
        var codex = rows[0];
        Assert.Equal((40L, 7L, 60L, 0L, 0), (codex.GetProperty("inputTokens").GetInt64(), codex.GetProperty("outputTokens").GetInt64(),
            codex.GetProperty("cacheReadTokens").GetInt64(), codex.GetProperty("cacheWriteTokens").GetInt64(), codex.GetProperty("reportedModels").GetArrayLength()));
        Assert.Equal((TaskTermination.Failed, 1, JsonValueKind.Null), (codex.GetProperty("termination").GetString(), codex.GetProperty("exitCode").GetInt32(), codex.GetProperty("toolCalls").ValueKind));
        var claude = rows[1];
        Assert.Equal((11L, 22L, 30L, 40L), (claude.GetProperty("inputTokens").GetInt64(), claude.GetProperty("outputTokens").GetInt64(),
            claude.GetProperty("cacheReadTokens").GetInt64(), claude.GetProperty("cacheWriteTokens").GetInt64()));
        Assert.Equal(["claude-sonnet-5-5", "claude-haiku-5-5"], claude.GetProperty("reportedModels").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(("opus", "opus", 4_200L, 3, 1), (claude.GetProperty("participantId").GetString(), claude.GetProperty("requestedModel").GetString(),
            claude.GetProperty("durationMs").GetInt64(), claude.GetProperty("toolCalls").GetInt32(), claude.GetProperty("posts").GetInt32()));

        var (_, limited) = await Get(query: "?limit=1");
        Assert.Equal(["codex-1"], limited.EnumerateArray().Select(r => r.GetProperty("spawnId").GetString()));
    }

    [Fact]
    public async Task An_open_row_reads_as_running_while_its_spawn_is_in_flight_and_as_interrupted_with_stamped_posts_otherwise()
    {
        var store = _host.Services.GetRequiredService<MessageStore>();
        Tasks.Start(Start("orphan-1", T0.AddMinutes(-5)));
        var stamped = store.Post("general", "opus", "posted before the hub stopped").Id;
        store.StampSpawn(stamped, "orphan-1");
        var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = (_, _, _) => release.Task;
        await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "@opus hold on" });
        var spec = await _runner.NextSpecAsync(Wait);
        var live = spec.Label.Split('/')[1];

        var (_, body) = await Get();
        var rows = body.EnumerateArray().ToDictionary(r => r.GetProperty("spawnId").GetString()!);
        Assert.Equal(JsonValueKind.Null, rows[live].GetProperty("termination").ValueKind);
        Assert.Equal((TaskTermination.Interrupted, 1, JsonValueKind.Null), (rows["orphan-1"].GetProperty("termination").GetString(),
            rows["orphan-1"].GetProperty("posts").GetInt32(), rows["orphan-1"].GetProperty("endedAt").ValueKind));
        Assert.Null(Tasks.ForRoom("general", 10).Single(t => t.Start.SpawnId == "orphan-1").End);   // the reading never rewrites the row

        release.SetResult(FakeProcessRunner.Ok("""{"result":"done"}"""));
    }

    [Fact]
    public async Task An_unknown_room_is_404_and_a_stopping_hub_is_503()
    {
        var missing = await Get("no-such-room");
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Contains("no-such-room", missing.Body.GetProperty("error").GetString());

        await _host.Services.GetRequiredService<SpawnerService>().StopAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Get()).Status);
    }
}
