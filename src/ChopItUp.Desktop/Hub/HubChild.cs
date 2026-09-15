// See the comment in ShellArgs.cs: this project's implicit usings do not include System.IO.
using System.IO;
using System.Security.Cryptography;

namespace ChopItUp.Desktop.Hub;

/// <summary>Row 12 T3: starts (or attaches to, B3) the hub and tracks its lifecycle for the rest of
/// the shell's run. The process and the HTTP probe are seams (IHubProcess/IHubProcessFactory/
/// IHealthProbe); HubProbe's lock/port checks are real disk reads, not seamed (see its own doc
/// comment). Lifetime (pass 1, finding 21): HubChild owns the factory, which owns the kill-on-close
/// job handle; whoever owns HubChild for the process's lifetime keeps that job alive.</summary>
public sealed class HubChild : IDisposable
{
    public static readonly TimeSpan ReadyBudget = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ShellArgs _args;
    private readonly IHubProcessFactory _factory;
    private readonly IHealthProbe _probe;
    private readonly TimeProvider _clock;
    private readonly Action<string> _log;
    private IHubProcess? _proc;

    public LogTail Tail { get; } = new(40);
    public string? ShellToken { get; private set; }
    public HubStatus Status { get; private set; }

    /// <summary>Where the hub really is. Valid once Status is Ready or Attached; before that it is the
    /// requested origin (the boot page needs none).</summary>
    public Uri ResolvedOrigin => new($"http://127.0.0.1:{Status.Port}/");

    public event Action<HubStatus>? StatusChanged;   // raised on whatever thread set the status; App marshals (Task 5)

    public HubChild(ShellArgs args, IHubProcessFactory factory, IHealthProbe probe, TimeProvider clock, Action<string> log)
    {
        _args = args;
        _factory = factory;
        _probe = probe;
        _clock = clock;
        _log = log;
        Status = new HubStatus(HubState.Starting, null, args.Port, null);
    }

    /// <summary>B3: a hub already owns the data dir → attach. Otherwise start ours and wait for
    /// /health. Never throws: the whole body is guarded and every exception becomes Failed.</summary>
    public async Task StartOrAttachAsync(CancellationToken ct)
    {
        try { await StartOrAttachCoreAsync(ct); }
        catch (Exception ex) { Set(new HubStatus(HubState.Failed, _proc?.Pid, Status.Port, $"{ex.GetType().Name}: {ex.Message}")); }
    }

    private async Task StartOrAttachCoreAsync(CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + ReadyBudget;
        if (HubProbe.LockIsHeld(_args.DataDir))
        {
            // A hand-started hub takes the lock before it binds; hub.port appears after (Task 1 makes
            // the hub delete it right after locking). Poll, never probe once.
            int? port = null;
            while (_clock.GetUtcNow() < deadline)
            {
                port = HubProbe.ReadPort(_args.DataDir);
                if (port is int p && await _probe.IsHealthyAsync(new Uri($"http://127.0.0.1:{p}/"), ct))
                {
                    Set(new HubStatus(HubState.Attached, null, p, $"attached to :{p}"));
                    return;
                }
                await Task.Delay(PollInterval, _clock, ct);
            }
            Set(new HubStatus(HubState.Failed, null, port ?? _args.Port,
                $"another hub holds {_args.DataDir} but {(port is null ? "wrote no hub.port" : $":{port}/health does not answer")} within {ReadyBudget.TotalSeconds:0} s"));
            return;
        }

        if (!File.Exists(_args.HubExe))
        {
            Set(new HubStatus(HubState.Failed, null, _args.Port, $"hub exe not found: {_args.HubExe}"));
            return;
        }

        ShellToken = NewToken();
        _proc = _factory.Start(_args.HubExe, _args.DataDir, _args.Port, ShellToken);
        _proc.OutputLine += line => { Tail.Add(line); _log("HUB " + line); };
        // Any non-terminal state at exit time is a failure (Starting, or Ready if the probe answered
        // and the hub died right after; pass 2, finding 13).
        var proc = _proc;
        _proc.Exited += () => { if (Status.State is HubState.Starting or HubState.Ready) Set(new HubStatus(HubState.Failed, proc.Pid, _args.Port, @"the hub exited (see data\logs\hub.log)")); };
        _proc.BeginReading();
        _log($"HUB START pid={_proc.Pid}");

        while (_clock.GetUtcNow() < deadline)
        {
            if (_proc.HasExited)
            {
                Set(new HubStatus(HubState.Failed, _proc.Pid, _args.Port, "the hub exited before it was ready"));
                return;
            }
            if (await _probe.IsHealthyAsync(_args.RequestedOrigin, ct))
            {
                Set(new HubStatus(HubState.Ready, _proc.Pid, _args.Port, null));
                if (_proc.HasExited)
                    Set(new HubStatus(HubState.Failed, _proc.Pid, _args.Port, @"the hub exited (see data\logs\hub.log)"));
                return;
            }
            await Task.Delay(PollInterval, _clock, ct);
        }
        Set(new HubStatus(HubState.Failed, _proc.Pid, _args.Port, $"/health did not answer within {ReadyBudget.TotalSeconds:0} s"));
    }

    /// <summary>B4. Idempotent. Attached → nothing to stop.</summary>
    public void Stop()
    {
        if (_proc is null || _proc.HasExited)
        {
            if (Status.State != HubState.Attached) Set(Status with { State = HubState.Stopped });
            return;
        }
        _log($"HUB KILL pid={_proc.Pid}");
        _proc.Kill();
        Set(new HubStatus(HubState.Stopped, _proc.Pid, Status.Port, null));
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');   // 43 chars, URL-safe

    private void Set(HubStatus s)
    {
        Status = s;
        _log($"HUB STATE {s.State} {s.Reason}");
        StatusChanged?.Invoke(s);
    }

    public void Dispose()
    {
        Stop();
        _proc?.Dispose();
    }
}
