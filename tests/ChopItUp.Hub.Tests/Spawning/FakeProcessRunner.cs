using System.Threading.Channels;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>Stands in for the two CLIs. A test sets <see cref="Handler"/> to play the model: read the
/// spec (the prompt is <c>StandardInput</c>), post into the hub through a real MCP client with the
/// participant's token, return a result. Every spec is recorded and also queued, so a test can await
/// "the next spawn" instead of sleeping.</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Channel<ProcessSpec> _seen = Channel.CreateUnbounded<ProcessSpec>();
    private readonly List<(ProcessSpec Spec, DateTimeOffset At)> _runs = new();
    public Func<ProcessSpec, TimeSpan, CancellationToken, Task<ProcessResult>> Handler { get; set; } = (_, _, _) => Task.FromResult(Ok("""{"result":"done"}"""));

    /// <summary>Every launch so far with its wall-clock start, copied under the lock.</summary>
    public IReadOnlyList<(ProcessSpec Spec, DateTimeOffset At)> Runs { get { lock (_runs) return _runs.ToList(); } }
    public int Count { get { lock (_runs) return _runs.Count; } }

    private readonly Dictionary<ProcessSpec, string> _mcpJson = new(ReferenceEqualityComparer.Instance);

    /// <summary>The text of the spec's Claude <c>mcp.json</c> as it was when the launch happened, or null
    /// for a Codex spec. Tests read this, never the file: by the time a test looks, the spawn may have
    /// finished and the service may have deleted its work directory (critique pass 2, M3).</summary>
    public string? McpJsonOf(ProcessSpec spec) { lock (_runs) return _mcpJson.TryGetValue(spec, out var t) ? t : null; }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation)
    {
        var args = spec.Arguments;
        var i = -1;
        for (var k = 0; k < args.Count; k++) { if (args[k] == "--mcp-config") { i = k; break; } }
        var mcp = i >= 0 && i + 1 < args.Count && File.Exists(args[i + 1]) ? File.ReadAllText(args[i + 1]) : null;
        lock (_runs)
        {
            _runs.Add((spec, DateTimeOffset.UtcNow));
            if (mcp is not null) _mcpJson[spec] = mcp;
        }
        _seen.Writer.TryWrite(spec);
        return await Handler(spec, timeout, cancellation);
    }

    public async Task<ProcessSpec> NextSpecAsync(TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        return await _seen.Reader.ReadAsync(cts.Token);
    }

    public async Task<bool> NoSpecWithin(TimeSpan wait)
    {
        try { await NextSpecAsync(wait); return false; }
        catch (OperationCanceledException) { return true; }
    }

    public static ProcessResult Ok(string stdout) => new(0, false, false, stdout, "", TimeSpan.FromMilliseconds(1));
    public static string ParticipantOf(ProcessSpec spec) => spec.Label.Split('/')[0];

    /// <summary>What the real runner does with a child that never exits: TimedOut on the timeout,
    /// Cancelled on the token.</summary>
    public static async Task<ProcessResult> HangUntilKilled(TimeSpan timeout, CancellationToken cancellation)
    {
        try { await Task.Delay(timeout, cancellation); return new ProcessResult(null, true, false, "", "", timeout); }
        catch (OperationCanceledException) { return new ProcessResult(null, false, true, "", "", TimeSpan.Zero); }
    }
}

/// <summary>The default for every hub a test boots: spawning is opt-in. A launch here is a test bug.</summary>
public sealed class RefusingProcessRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation) =>
        throw new InvalidOperationException($"This test did not opt into spawning, but the hub tried to launch '{spec.Label}'. Pass a FakeProcessRunner to HubTestHost.StartAsync.");
}
