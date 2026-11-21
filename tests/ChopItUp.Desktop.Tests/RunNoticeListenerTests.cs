using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ChopItUp.Desktop.Notices;

namespace ChopItUp.Desktop.Tests;

public sealed class RunNoticeReaderTests
{
    private const string Held = """{"kind":"held","roomId":"lab","roomName":"LAB","runId":3,"text":"@sonnet hit a usage limit."}""";

    private static List<RunNotice> Read(params string[] lines)
    {
        var reader = new RunNoticeReader();
        return lines.Select(reader.Feed).OfType<RunNotice>().ToList();
    }

    [Fact]
    public void A_run_event_yields_its_notice_when_the_blank_line_ends_it()
    {
        var notices = Read(": connected", "", "event: run", "data: " + Held, "", ": keepalive", "");

        Assert.Equal([new RunNotice("held", "lab", "LAB", 3, "@sonnet hit a usage limit.")], notices);
        Assert.Equal("Run #3 in LAB held for a usage limit", notices[0].Title);
    }

    [Theory]
    [InlineData("event: other", "data: " + Held)]                                                              // not a run event
    [InlineData("event: run", "data: {not json")]                                                               // malformed
    [InlineData("event: run", """data: {"kind":"finished","roomId":"lab","roomName":"LAB","runId":3,"text":""}""")] // unknown kind
    [InlineData("event: run", """data: {"kind":"parked","roomId":"","roomName":"LAB","runId":3,"text":""}""")]     // no room
    [InlineData("event: run", "")]                                                                              // no data
    public void Anything_but_a_well_formed_run_notice_yields_nothing_and_the_next_one_still_reads(string first, string second)
    {
        var notices = Read(first, second, "", "event: run", "data: " + Held, "");

        Assert.Single(notices);
    }

    [Theory]
    [InlineData("presented", "Run #3 in LAB presented")]
    [InlineData("parked", "Run #3 in LAB parked")]
    public void Each_kind_has_its_own_title(string kind, string title) =>
        Assert.Equal(title, new RunNotice(kind, "lab", "LAB", 3, "").Title);
}

public sealed class RunNoticeListenerTests
{
    private static readonly Uri Origin = new("http://127.0.0.1:8790/");
    private const string Event = "event: run\ndata: {\"kind\":\"parked\",\"roomId\":\"lab\",\"roomName\":\"LAB\",\"runId\":RUN,\"text\":\"t\"}\n\n";

    /// <summary>Answers each GET with the next scripted reply: a stream body, or an exception.</summary>
    private sealed class Scripted(params Func<HttpResponseMessage>[] replies) : HttpMessageHandler
    {
        public int Requests;
        public readonly List<Uri> Uris = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Uris.Add(request.RequestUri!);
            var i = Interlocked.Increment(ref Requests) - 1;
            return i < replies.Length ? Task.FromResult(replies[i]()) : Task.FromException<HttpResponseMessage>(new HttpRequestException("hub down"));
        }
    }

    private static HttpResponseMessage Stream(string body) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))) };

    [Fact]
    public async Task It_waits_for_the_hub_then_reconnects_after_each_drop_and_failure_and_delivers_every_notice()
    {
        var handler = new Scripted(
            () => Stream(": connected\n\n" + Event.Replace("RUN", "1")),
            () => throw new HttpRequestException("hub restarting"),
            () => Stream(": connected\n\n" + Event.Replace("RUN", "2") + "event: run\ndata: garbage\n\n"));
        var notices = new List<long>();
        var delays = new List<TimeSpan>();
        using var stop = new CancellationTokenSource();
        var originReads = 0;
        var listener = new RunNoticeListener(new HttpClient(handler),
            origin: () => Interlocked.Increment(ref originReads) == 1 ? null : Origin,
            onNotice: n => notices.Add(n.RunId),
            log: _ => { },
            delay: (d, _) =>
            {
                delays.Add(d);
                if (handler.Requests >= 4) stop.Cancel();
                return Task.CompletedTask;
            });

        await listener.RunAsync(stop.Token);

        Assert.Equal([1L, 2L], notices);
        Assert.All(handler.Uris, u => Assert.Equal(new Uri(Origin, "api/runs/events"), u));
        // Not ready, then: stream 1 ended (1 s), connect failed (2 s), stream 2 ended (back to 1 s after
        // a connect), then the hub is down (2 s).
        Assert.Equal([RunNoticeListener.NotReadyPoll, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task Failed_connects_back_off_to_thirty_seconds()
    {
        var handler = new Scripted();
        var delays = new List<TimeSpan>();
        using var stop = new CancellationTokenSource();
        var listener = new RunNoticeListener(new HttpClient(handler), () => Origin, _ => { }, _ => { },
            (d, _) => { delays.Add(d); if (delays.Count == 7) stop.Cancel(); return Task.CompletedTask; });

        await listener.RunAsync(stop.Token);

        Assert.Equal(new[] { 1, 2, 4, 8, 16, 30, 30 }.Select(s => TimeSpan.FromSeconds(s)), delays);
    }

    [Fact]
    public void A_notification_click_opens_the_room_by_its_escaped_id() =>
        Assert.Equal("http://127.0.0.1:8790/?room=goal%20runs", MainWindow.RoomUri(Origin, "goal runs").AbsoluteUri);
}
