using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ChopItUp.Desktop.Notices;

/// <summary>A run event from the hub's <c>GET /api/runs/events</c> stream. No ProjectReference to the
/// Hub exists, so the shape and the three kinds repeat ChopItUp.Hub.Realtime.RunNotice.</summary>
public sealed record RunNotice(string Kind, string RoomId, string RoomName, long RunId, string Text)
{
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal) { "presented", "parked", "held" };

    /// <summary>The notification title: which run, where, and what happened.</summary>
    public string Title => Kind switch
    {
        "presented" => $"Run #{RunId} in {RoomName} presented",
        "held" => $"Run #{RunId} in {RoomName} held for a usage limit",
        _ => $"Run #{RunId} in {RoomName} parked",
    };
}

/// <summary>Reads the server-sent event stream one line at a time. A blank line ends an event; an
/// <c>event: run</c> whose data is a notice of a known kind yields it, and anything else (comments,
/// other events, malformed JSON, an unknown kind) yields nothing.</summary>
public sealed class RunNoticeReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? _event;
    private readonly StringBuilder _data = new();

    public RunNotice? Feed(string line)
    {
        if (line.Length == 0) return Dispatch();
        if (line[0] == ':') return null;
        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
        if (field == "event") _event = value;
        else if (field == "data") { if (_data.Length > 0) _data.Append('\n'); _data.Append(value); }
        return null;
    }

    private RunNotice? Dispatch()
    {
        var evt = _event;
        var data = _data.ToString();
        _event = null;
        _data.Clear();
        if (evt != "run" || data.Length == 0) return null;
        try
        {
            var notice = JsonSerializer.Deserialize<RunNotice>(data, Json);
            return notice is { RoomId.Length: > 0, RunId: > 0 } && RunNotice.Kinds.Contains(notice.Kind ?? "")
                ? notice with { RoomName = string.IsNullOrEmpty(notice.RoomName) ? notice.RoomId : notice.RoomName, Text = notice.Text ?? "" }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Keeps one connection to the hub's run notice stream for the shell's whole life, window
/// hidden or not. While the hub is not ready it waits; when the stream drops or the hub restarts it
/// reconnects, waiting one second at first and doubling to thirty while connections keep failing.</summary>
public sealed class RunNoticeListener(
    HttpClient http,
    Func<Uri?> origin,
    Action<RunNotice> onNotice,
    Action<string> log,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan LongestRetry = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan NotReadyPoll = TimeSpan.FromMilliseconds(500);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task RunAsync(CancellationToken cancel)
    {
        var retry = FirstRetry;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                if (origin() is not { } at)
                {
                    await _delay(NotReadyPoll, cancel);
                    continue;
                }
                using var response = await http.GetAsync(new Uri(at, "api/runs/events"), HttpCompletionOption.ResponseHeadersRead, cancel);
                response.EnsureSuccessStatusCode();
                log("NOTICES connected");
                retry = FirstRetry;
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel), Encoding.UTF8);
                var parser = new RunNoticeReader();
                while (await reader.ReadLineAsync(cancel) is { } line)
                    if (parser.Feed(line) is { } notice) onNotice(notice);
                log("NOTICES stream ended");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
            {
                log($"NOTICES connect failed ({e.GetType().Name}); retry in {retry.TotalSeconds:0}s");
            }
            try { await _delay(retry, cancel); }
            catch (OperationCanceledException) { return; }
            retry = retry * 2 > LongestRetry ? LongestRetry : retry * 2;
        }
    }
}
