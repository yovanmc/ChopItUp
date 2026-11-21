using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Realtime;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static ChopItUp.Hub.Tests.RunHostFixture;

namespace ChopItUp.Hub.Tests.Realtime;

/// <summary>GET /api/runs/events end to end: a run that presents, parks or is held for a usage limit
/// sends one notice to every open stream, and nothing else does.</summary>
public sealed class RunNoticeStreamTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly SpawnLimits Instant = new(Budget: 4, Debounce: TimeSpan.Zero, MinSpacing: TimeSpan.Zero, Timeout: TimeSpan.FromSeconds(30), TranscriptMessages: 60, TranscriptChars: 24_000);
    private const string Working = """{"result":"working"}""";

    private static ProcessResult ClaudeFailed(string text) =>
        new(1, false, false, """{"type":"result","is_error":true,"result":""" + JsonSerializer.Serialize(text) + "}", "", TimeSpan.FromMilliseconds(1));

    private static Task Post(HubTestHost host, string room, string body) =>
        host.Client.PostAsJsonAsync($"api/rooms/{room}/messages", new { body });

    /// <summary>An open stream, read line by line.</summary>
    private sealed class Stream(HttpResponseMessage response, StreamReader reader) : IAsyncDisposable
    {
        public static async Task<Stream> OpenAsync(HubTestHost host, int subscribersAfter = 1)
        {
            var response = await host.Client.GetAsync("api/runs/events", HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            var stream = new Stream(response, new StreamReader(await response.Content.ReadAsStreamAsync()));
            Assert.Equal(": connected", await stream.LineAsync(Wait));
            var bus = host.Services.GetRequiredService<RunNoticeBus>();
            var deadline = DateTime.UtcNow + Wait;
            while (bus.Subscribers < subscribersAfter) { Assert.True(DateTime.UtcNow < deadline, "the stream never subscribed"); await Task.Delay(10); }
            return stream;
        }

        public async Task<string?> LineAsync(TimeSpan within)
        {
            using var cts = new CancellationTokenSource(within);
            return await reader.ReadLineAsync(cts.Token);
        }

        /// <summary>The next notice, skipping comments and blank lines; null when none comes in time.</summary>
        public async Task<JsonElement?> NoticeAsync(TimeSpan within)
        {
            var deadline = DateTime.UtcNow + within;
            string? evt = null;
            while (DateTime.UtcNow < deadline)
            {
                string? line;
                try { line = await LineAsync(deadline - DateTime.UtcNow); }
                catch (OperationCanceledException) { return null; }
                if (line is null) return null;
                if (line.StartsWith("event: ", StringComparison.Ordinal)) evt = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal) && evt == "run") return JsonDocument.Parse(line[6..]).RootElement.Clone();
            }
            return null;
        }

        public async ValueTask DisposeAsync() { reader.Dispose(); response.Dispose(); await Task.CompletedTask; }
    }

    private static void AssertNotice(JsonElement? notice, string kind, string room, long runId, string textPart)
    {
        Assert.NotNull(notice);
        var n = notice.Value;
        Assert.Equal((kind, room, "LAB", runId), (n.GetProperty("kind").GetString(), n.GetProperty("roomId").GetString(), n.GetProperty("roomName").GetString(), n.GetProperty("runId").GetInt64()));
        Assert.Contains(textPart, n.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_run_whose_conductor_pings_sends_one_presented_notice()
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, RunLimits.Default);
        await using var _ = host;
        await using var stream = await Stream.OpenAsync(host);
        runner.Handler = async (spec, _, _) =>
        {
            await PostAsInHost(host, "sonnet", room, "phase: ping the result is in the room");
            return FakeProcessRunner.Ok(Working);
        };

        await Post(host, room, "/build-thing @sonnet begin");
        await runner.NextSpecAsync(Wait);

        var runId = host.Services.GetRequiredService<RunStore>().Latest(room)!.Id;
        AssertNotice(await stream.NoticeAsync(Wait), RunNotice.Presented, room, runId, "presented");
        Assert.Null(await stream.NoticeAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task A_usage_limit_hold_and_a_park_each_send_one_notice()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-20T16:00:00Z"));
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("hub-local", TimeSpan.FromHours(-6), "hub-local", "hub-local"));
        var (host, runner, room) = await StartRunHostAsync(Instant, RunLimits.Default, clock);
        await using var _ = host;
        await using var stream = await Stream.OpenAsync(host);
        var launches = 0;
        runner.Handler = (_, _, _) => Task.FromResult(Interlocked.Increment(ref launches) == 1
            ? ClaudeFailed("You've hit your 5-hour limit · resets 3pm (America/Chicago)")
            : ClaudeFailed("You've hit your monthly spend limit."));

        await Post(host, room, "/build-thing @sonnet begin");
        await runner.NextSpecAsync(Wait);
        var runId = (await WaitForRun(host, room, r => r.Status == RunStatus.Parked)).Id;
        AssertNotice(await stream.NoticeAsync(Wait), RunNotice.Held, room, runId, "resumes at 2026-11-20 15:02 hub time");

        await Post(host, room, "carry on");
        await runner.NextSpecAsync(Wait);
        AssertNotice(await stream.NoticeAsync(Wait), RunNotice.Parked, room, runId, "with no reset time given");
        Assert.Null(await stream.NoticeAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task A_run_stopped_by_the_owner_sends_no_notice()
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, RunLimits.Default);
        await using var _ = host;
        await using var stream = await Stream.OpenAsync(host);
        runner.Handler = (_, _, cancel) => FakeProcessRunner.HangUntilKilled(TimeSpan.FromSeconds(30), cancel);

        await Post(host, room, "/build-thing @sonnet begin");
        await runner.NextSpecAsync(Wait);
        await Post(host, room, "/stop");
        await WaitForRun(host, room, r => r.Status == RunStatus.Ended);

        Assert.Null(await stream.NoticeAsync(TimeSpan.FromMilliseconds(800)));
    }

    [Fact]
    public async Task Every_open_stream_gets_the_notice_and_a_closed_one_is_dropped()
    {
        var (host, runner, room) = await StartRunHostAsync(Instant, RunLimits.Default);
        await using var _ = host;
        var bus = host.Services.GetRequiredService<RunNoticeBus>();
        var first = await Stream.OpenAsync(host, 1);
        await using var second = await Stream.OpenAsync(host, 2);
        await first.DisposeAsync();
        var deadline = DateTime.UtcNow + Wait;
        while (bus.Subscribers != 1) { Assert.True(DateTime.UtcNow < deadline, "the closed stream was never dropped"); await Task.Delay(10); }

        bus.Publish(new RunNotice(RunNotice.Parked, room, "LAB", 7, "Fabricated."));

        AssertNotice(await second.NoticeAsync(Wait), RunNotice.Parked, room, 7, "Fabricated.");
    }

    [Fact]
    public async Task An_idle_stream_sends_a_keepalive_comment()
    {
        var (host, _, _) = await StartRunHostAsync(Instant, RunLimits.Default);
        await using var _ = host;
        host.Services.GetRequiredService<RunNoticeBus>().KeepAlive = TimeSpan.FromMilliseconds(100);
        await using var stream = await Stream.OpenAsync(host);

        string? line;
        do line = await stream.LineAsync(Wait); while (line == "");
        Assert.Equal(": keepalive", line);
    }
}
