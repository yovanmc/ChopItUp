using System.Text.Json;
using System.Threading.Channels;

namespace ChopItUp.Hub.Realtime;

/// <summary>A run event the owner hears about outside the web UI: the run presented (its conductor
/// pinged), parked, or was held for a usage limit. The desktop shell turns each one into a tray
/// notification.</summary>
public sealed record RunNotice(string Kind, string RoomId, string RoomName, long RunId, string Text)
{
    public const string Presented = "presented";
    public const string Parked = "parked";
    public const string Held = "held";
}

/// <summary>Fans run notices out to every open <c>GET /api/runs/events</c> stream. Each subscriber has
/// its own small buffer that drops its oldest notice when full, so a stalled reader never holds up
/// the spawner or another reader. Nothing is replayed: a reader hears what happens while it is
/// connected.</summary>
public sealed class RunNoticeBus
{
    private const int Buffer = 64;
    private readonly object _gate = new();
    private readonly List<Channel<RunNotice>> _subscribers = [];

    /// <summary>How long an idle stream waits before it writes a keepalive comment.</summary>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(15);

    public int Subscribers { get { lock (_gate) return _subscribers.Count; } }

    public void Publish(RunNotice notice)
    {
        lock (_gate)
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(notice);
    }

    /// <summary>Opens a subscription. Disposing <paramref name="lease"/> ends it.</summary>
    public ChannelReader<RunNotice> Subscribe(out IDisposable lease)
    {
        var channel = Channel.CreateBounded<RunNotice>(new BoundedChannelOptions(Buffer) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_gate) _subscribers.Add(channel);
        lease = new Lease(() =>
        {
            lock (_gate) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        });
        return channel.Reader;
    }

    private sealed class Lease(Action end) : IDisposable
    {
        private int _ended;
        public void Dispose() { if (Interlocked.Exchange(ref _ended, 1) == 0) end(); }
    }
}

/// <summary><c>GET /api/runs/events</c>: a server-sent event stream of <see cref="RunNotice"/>s, one
/// <c>event: run</c> per notice with the notice as JSON, and a comment line while idle. Unauthenticated
/// like the hub's other GETs: the bearer gate guards only non-GET <c>/api</c> calls, and the hub binds
/// loopback only.</summary>
public static class RunNoticeStream
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapRunNoticeStream(this WebApplication app) =>
        app.MapGroup("/api").MapGet("/runs/events", StreamAsync);

    private static async Task StreamAsync(HttpContext context, RunNoticeBus bus)
    {
        var aborted = context.RequestAborted;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        var reader = bus.Subscribe(out var lease);
        using (lease)
        {
            try
            {
                await WriteAsync(context, ": connected\n\n", aborted);
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                    idle.CancelAfter(bus.KeepAlive);
                    try
                    {
                        if (!await reader.WaitToReadAsync(idle.Token)) return;
                    }
                    catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
                    {
                        await WriteAsync(context, ": keepalive\n\n", aborted);
                        continue;
                    }
                    while (reader.TryRead(out var notice))
                        await WriteAsync(context, $"event: run\ndata: {JsonSerializer.Serialize(notice, Json)}\n\n", aborted);
                }
            }
            catch (Exception e) when (aborted.IsCancellationRequested && e is OperationCanceledException or IOException)
            {
                // The reader went away.
            }
        }
    }

    private static async Task WriteAsync(HttpContext context, string text, CancellationToken cancel)
    {
        await context.Response.WriteAsync(text, cancel);
        await context.Response.Body.FlushAsync(cancel);
    }
}
