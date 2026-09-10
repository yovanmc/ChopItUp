using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Skills;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests;

public sealed class ExchangeApiTests : IAsyncLifetime
{
    private static readonly SpawnLimits Fast = new(Budget: 4, Debounce: TimeSpan.FromMilliseconds(150), MinSpacing: TimeSpan.Zero, Timeout: TimeSpan.FromSeconds(30), TranscriptMessages: 60, TranscriptChars: 24_000);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_exapi_" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new();
    private HubTestHost _host = null!;

    public async Task InitializeAsync() => _host = await HubTestHost.StartAsync(_dir, processRunner: _runner, limits: Fast);
    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<JsonElement> Get(string roomId)
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync($"api/rooms/{roomId}/exchange"));
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task A7_a_room_with_no_exchange_reports_idle_and_an_unknown_room_is_404()
    {
        var idle = await Get("general");
        Assert.Equal("idle", idle.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, idle.GetProperty("rootMessageId").ValueKind);
        Assert.Equal(0, idle.GetProperty("remaining").GetInt32());
        Assert.Equal(JsonValueKind.Null, idle.GetProperty("stoppedBy").ValueKind);   // row 27, task 2: Idle sends null
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.GetAsync("api/rooms/nope/exchange")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.PostAsync("api/rooms/nope/exchange/stop", null)).StatusCode);
    }

    [Fact]
    public async Task A6_stop_on_an_idle_room_is_409()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await _host.Client.PostAsync("api/rooms/general/exchange/stop", null)).StatusCode);
    }

    [Fact]
    public async Task A6_A7_stop_kills_the_running_spawn_notes_it_and_every_change_reaches_signalr()
    {
        _runner.Handler = (_, timeout, ct) => FakeProcessRunner.HangUntilKilled(timeout, ct);

        await using var connection = new HubConnectionBuilder().WithUrl(new Uri(_host.BaseAddress, "hub/rooms")).Build();
        await connection.StartAsync();
        await connection.InvokeAsync("JoinRoom", "general");
        var changes = new List<JsonElement>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(SpawnerService.ChangedEvent, snap =>
        {
            lock (changes) changes.Add(snap.Clone());
            if (snap.GetProperty("status").GetString() == "stopped") stopped.TrySetResult();
        });

        var post = await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "@fable think for a long time" });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var spec = await _runner.NextSpecAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("fable", FakeProcessRunner.ParticipantOf(spec));

        var open = await Get("general");
        Assert.Equal("open", open.GetProperty("status").GetString());
        Assert.Equal(["fable"], open.GetProperty("inFlight").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(3, open.GetProperty("remaining").GetInt32());

        var stop = await _host.Client.PostAsync("api/rooms/general/exchange/stop", null);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        using var stopDoc = JsonDocument.Parse(await stop.Content.ReadAsStringAsync());
        Assert.Equal("stopped", stopDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, stopDoc.RootElement.GetProperty("turnsUsed").GetInt32());

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (changes)
        {
            Assert.Contains(changes, c => c.GetProperty("status").GetString() == "open" && c.GetProperty("inFlight").GetArrayLength() == 1);
            Assert.Contains(changes, c => c.GetProperty("status").GetString() == "stopped");
        }

        using var msgs = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/general/messages?afterId=0&limit=50"));
        var bodies = msgs.RootElement.GetProperty("messages").EnumerateArray().Select(m => (m.GetProperty("authorId").GetString(), m.GetProperty("body").GetString())).ToList();
        Assert.Contains((ChopDb.HubParticipantId, "Exchange stopped by the owner: 1 of 4 turns used."), bodies);
        Assert.DoesNotContain(bodies, b => b.Item2!.Contains("did not reply"));      // cancelled, not timed out
        Assert.DoesNotContain(bodies, b => b.Item2!.Contains("Exchange concluded"));

        Assert.Equal(HttpStatusCode.Conflict, (await _host.Client.PostAsync("api/rooms/general/exchange/stop", null)).StatusCode);   // nothing open now
        Assert.Equal("stopped", (await Get("general")).GetProperty("status").GetString());
    }

    /// <summary>Row 27, task 2: the cause on the wire, owner arm. Same fixture as A6/A7 above.</summary>
    [Fact]
    public async Task A7_row27_an_owner_stop_marks_stoppedBy_owner_on_the_wire()
    {
        _runner.Handler = (_, timeout, ct) => FakeProcessRunner.HangUntilKilled(timeout, ct);

        var post = await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "@fable think for a long time" });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        await _runner.NextSpecAsync(TimeSpan.FromSeconds(15));

        var stop = await _host.Client.PostAsync("api/rooms/general/exchange/stop", null);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        using var stopDoc = JsonDocument.Parse(await stop.Content.ReadAsStringAsync());
        Assert.Equal("owner", stopDoc.RootElement.GetProperty("stoppedBy").GetString());

        Assert.Equal("owner", (await Get("general")).GetProperty("stoppedBy").GetString());
    }

    private const string RunSkillMd = "---\nname: build-thing\nrun: true\n---\n\n# Build Thing\n\nBuild the thing.\n";

    /// <summary>Same shape as SpawnerServiceTests.Runs.cs's StartRunHostAsync (row 19, task 9) -
    /// duplicated here rather than shared because that helper is private to its own partial class.</summary>
    private static async Task<(HubTestHost Host, FakeProcessRunner Runner, string Room)> StartRunHostAsync(RunLimits runLimits)
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_exapi_run_" + Guid.NewGuid().ToString("N"));
        var roomsRoot = dir + "_rooms";
        var runner = new FakeProcessRunner();
        var host = await HubTestHost.StartAsync(dir, processRunner: runner, limits: Fast, roomsRoot: roomsRoot, runLimits: runLimits);
        const string room = "lab";
        var roomDir = Path.Combine(roomsRoot, room);
        Assert.True(await new GitTrail(roomDir).InitAsync());
        host.Services.GetRequiredService<MessageStore>().CreateRoom(room, "LAB", roomDir);

        var skillDir = Path.Combine(dir, "skills", "build-thing");
        Directory.CreateDirectory(skillDir);
        var bytes = new System.Text.UTF8Encoding(false).GetBytes(RunSkillMd);
        File.WriteAllBytes(Path.Combine(skillDir, "SKILL.md"), bytes);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        new SkillHashes(host.Services.GetRequiredService<ChopDb>()).Record("build-thing", hash, "test-fixture");
        return (host, runner, room);
    }

    private static async Task PostAsInHost(HubTestHost host, string participant, string room, string body)
    {
        await using var client = await host.ClientFor(participant);
        HubTestHost.Json(await client.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = room, ["body"] = body, ["client_key"] = Guid.NewGuid().ToString() }));
    }

    /// <summary>Row 27, task 2: the cause on the wire, run arm. Same hard-cap-park setup as
    /// SpawnerServiceTests.Runs.cs's Run19_M27_a_hard_cap_park_... test (task 1) - reused here rather
    /// than invented, per the brief.</summary>
    [Fact]
    public async Task A7_row27_a_run_driven_stop_marks_stoppedBy_run_on_the_wire()
    {
        var runLimits = new RunLimits(Spawns: 1, WallClock: TimeSpan.FromHours(1), SpawnTimeout: TimeSpan.FromMinutes(30), PhaseEntries: 100);
        var (host, runner, room) = await StartRunHostAsync(runLimits);
        await using var _ = host;

        runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
                await PostAsInHost(host, "sonnet", room, "phase: build @opus - the spawn cap is already spent by the launch");
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await host.Client.PostAsJsonAsync($"api/rooms/{room}/messages", new { body = "/build-thing @sonnet begin" });
        await runner.NextSpecAsync(TimeSpan.FromSeconds(15));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            using var msgs = JsonDocument.Parse(await host.Client.GetStringAsync($"api/rooms/{room}/messages?afterId=0&limit=200"));
            if (msgs.RootElement.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("body").GetString()!.Contains("parked"))) break;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"run in '{room}' did not park within 15s");
            await Task.Delay(100);
        }

        using var snapDoc = JsonDocument.Parse(await host.Client.GetStringAsync($"api/rooms/{room}/exchange"));
        Assert.Equal("run", snapDoc.RootElement.GetProperty("stoppedBy").GetString());
    }
}
