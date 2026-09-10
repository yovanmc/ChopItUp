using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using static ChopItUp.Hub.Tests.RunHostFixture;

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

    /// <summary>Row 27, task 2: the cause on the wire, run arm. Same hard-cap-park setup as
    /// SpawnerServiceTests.Runs.cs's Run19_M27_a_hard_cap_park_... test (task 1): a run whose spawn
    /// cap is spent while its conductor's own exchange is still open, so the park's exchange-stop note
    /// carries the run cause.</summary>
    [Fact]
    public async Task A7_row27_a_run_driven_stop_marks_stoppedBy_run_on_the_wire()
    {
        var runLimits = new RunLimits(Spawns: 1, WallClock: TimeSpan.FromHours(1), SpawnTimeout: TimeSpan.FromMinutes(30), PhaseEntries: 100);
        var (host, runner, room) = await StartRunHostAsync(Fast, runLimits);
        await using var _ = host;

        runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet")
                await PostAsInHost(host, "sonnet", room, "phase: build @opus - the spawn cap is already spent by the launch");
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await host.Client.PostAsJsonAsync($"api/rooms/{room}/messages", new { body = "/build-thing @sonnet begin" });
        await runner.NextSpecAsync(TimeSpan.FromSeconds(15));

        await WaitForNoteContaining(host, room, "parked");

        using var snapDoc = JsonDocument.Parse(await host.Client.GetStringAsync($"api/rooms/{room}/exchange"));
        Assert.Equal("run", snapDoc.RootElement.GetProperty("stoppedBy").GetString());
    }
}
