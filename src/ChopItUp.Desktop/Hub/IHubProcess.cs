namespace ChopItUp.Desktop.Hub;

/// <summary>What HubChild needs from a process: start it, know when it exits, kill it. Seamed so the
/// state machine is tested without a real hub.</summary>
public interface IHubProcess : IDisposable
{
    int Pid { get; }
    bool HasExited { get; }
    event Action<string> OutputLine;     // stdout and stderr, merged, one line at a time
    event Action Exited;
    /// <summary>Starts the output pumps. Called by HubChild AFTER it has subscribed OutputLine and
    /// Exited, so a hub that dies in its first milliseconds (port in use is the common one) still
    /// lands its lines in the tail.</summary>
    void BeginReading();
    void Kill();
}

public interface IHubProcessFactory
{
    IHubProcess Start(string exe, string dataDir, int port, string shellToken);
}

public interface IHealthProbe
{
    /// <summary>True when GET /health answered 200 with ok:true.</summary>
    Task<bool> IsHealthyAsync(Uri origin, CancellationToken ct);
}

public enum HubState { Starting, Ready, Failed, Attached, Stopped }

/// <summary>Port is the one the hub is actually on: --port when the shell started it, the value of
/// hub.port when it attached. Every consumer (navigate, trusted source, tray, chip) reads it from here.</summary>
public sealed record HubStatus(HubState State, int? Pid, int Port, string? Reason);
