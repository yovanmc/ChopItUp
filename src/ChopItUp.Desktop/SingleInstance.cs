using System.Security.Cryptography;
using System.Text;

namespace ChopItUp.Desktop;

/// <summary>One shell per data dir. <see cref="TryBecomePrimary"/> claims a named mutex for
/// the data dir's key and, only on success, creates the two named events a later launch signals
/// (<see cref="Signal"/>) — so a bare second launch or a scripted <c>--show</c>/<c>--quit</c> always
/// finds an event to open when, and only when, a primary actually exists. <see cref="Listen"/> starts
/// the background thread that waits on both events and invokes the caller's callbacks directly (App is
/// the one that knows those callbacks need a <c>Dispatcher.BeginInvoke</c> hop onto the UI thread).</summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>First 16 hex chars of SHA-256 over the lower-invariant full path.</summary>
    public static string Hash16(string path)
    {
        var normalized = path.ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>The name every process for one data dir derives its mutex and events from.</summary>
    public static string Key(string dataDir) => "ChopItUp.Desktop." + Hash16(dataDir);

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly EventWaitHandle _quitEvent;
    private Thread? _listener;
    private volatile bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent, EventWaitHandle quitEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _quitEvent = quitEvent;
    }

    /// <summary>Claims the mutex for <paramref name="key"/>. Null when another process already holds
    /// it: the caller (App) then signals that primary to show itself instead of starting a second
    /// shell.</summary>
    public static SingleInstance? TryBecomePrimary(string key)
    {
        var mutex = new Mutex(initiallyOwned: true, name: "Local\\" + key, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + ShowEventName(key));
        var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + QuitEventName(key));
        return new SingleInstance(mutex, showEvent, quitEvent);
    }

    /// <summary>A second launch's <c>--show</c>/<c>--quit</c> (or a bare second launch, which signals
    /// Show). False when no primary holds this key's events — nothing to signal, which the caller
    /// reports rather than starting a second shell.</summary>
    public static bool Signal(string key, ShellCommand command)
    {
        var name = "Local\\" + command switch
        {
            ShellCommand.Show => ShowEventName(key),
            ShellCommand.Quit => QuitEventName(key),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Signal only takes Show or Quit."),
        };
        if (!EventWaitHandle.TryOpenExisting(name, out var handle)) return false;
        using (handle) { handle.Set(); }
        return true;
    }

    /// <summary>Starts the background thread that waits on both events for as long as this instance is
    /// primary. Raw invocation of <paramref name="onShow"/>/<paramref name="onQuit"/> on that thread —
    /// the caller marshals to whatever thread it needs.</summary>
    public void Listen(Action onShow, Action onQuit)
    {
        _listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { _showEvent, _quitEvent };
            while (!_disposed)
            {
                int signaled;
                try
                {
                    signaled = WaitHandle.WaitAny(handles);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                // Dispose() also sets _showEvent to wake this thread out of WaitAny; this check keeps
                // that wakeup from being mistaken for a real Show signal.
                if (_disposed) return;

                if (signaled == 0) onShow();
                else if (signaled == 1) onQuit();
            }
        })
        { IsBackground = true, Name = "ChopItUp.SingleInstance" };
        _listener.Start();
    }

    private static string ShowEventName(string key) => key + ".show";
    private static string QuitEventName(string key) => key + ".quit";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Wake the listener thread (if any) out of WaitAny before the handles it is waiting on are
        // disposed out from under it.
        _showEvent.Set();
        _listener?.Join(TimeSpan.FromSeconds(2));

        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _showEvent.Dispose();
        _quitEvent.Dispose();
    }
}
