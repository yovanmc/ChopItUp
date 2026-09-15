// See the comment in ShellArgs.cs: this project's implicit usings do not include System.IO.
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace ChopItUp.Desktop.Hub;

// Row 12 T3: the plan's Files list for this task does not name this file explicitly, but its shape
// is fully specified in the task body (right after HubProbe) and Task 5's App wiring needs a real
// IHubProcessFactory to exist — so it is built here, alongside the seams it implements. Not covered
// by this task's RED tests (HubChildTests exercises the state machine through a fake factory); it is
// exercised by Task 5's manual smoke test and Task 9's harness.
/// <summary>Row 12 T3 (B10): the real IHubProcessFactory. No ProjectReference to the Hub exists, so
/// "CHOPITUP_SHELL_TOKEN" is repeated here as a literal — it must match
/// ChopItUp.Hub.Hosting.HubOptions.ShellTokenEnvVar exactly (a cross-file invariant the orchestrator's
/// fixed lenses check).</summary>
public sealed class ProcessHubFactory : IHubProcessFactory
{
    private const string ShellTokenEnvVar = "CHOPITUP_SHELL_TOKEN";
    private const long LogRotateBytes = 5 * 1024 * 1024;

    public IHubProcess Start(string exe, string dataDir, int port, string shellToken)
    {
        var logWriter = OpenRotatedLog(dataDir);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--data");
        psi.ArgumentList.Add(dataDir);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        // The literal is repeated here (no ProjectReference to the Hub, B10); see the class doc comment.
        psi.Environment[ShellTokenEnvVar] = shellToken;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var job = JobObjectInterop.CreateKillOnCloseJob();
        try
        {
            process.Start();
        }
        catch
        {
            job.Dispose();
            logWriter.Dispose();
            throw;
        }

        try
        {
            JobObjectInterop.Assign(job, process.Handle);
        }
        catch (Win32Exception) when (process.HasExited)
        {
            // Not an error: a child that exited between Start() and here has nothing left to confine.
            // HubChild's Exited handler reports the real failure reason (mirrors
            // src/ChopItUp.Hub/Spawning/SpawnJobs.cs:57-61; pass 2, finding 1).
        }

        return new RealHubProcess(process, job, logWriter);
    }

    /// <summary>Also writes every line to &lt;LogDir&gt;\hub.log; if it already exceeds 5 MB it is
    /// renamed to hub.1.log (replacing any previous one) before this run appends, so the pair is
    /// bounded at ~10 MB.</summary>
    private static StreamWriter OpenRotatedLog(string dataDir)
    {
        var logDir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "hub.log");
        if (File.Exists(logPath) && new FileInfo(logPath).Length > LogRotateBytes)
        {
            var rotated = Path.Combine(logDir, "hub.1.log");
            File.Delete(rotated);
            File.Move(logPath, rotated);
        }
        return new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }
}

/// <summary>Wraps a real child process: drains stdout/stderr into <see cref="IHubProcess.OutputLine"/>
/// and the hub.log file, kills through the row 29 kill-on-close job as well as Process.Kill. The
/// output pumps (BeginOutputReadLine/BeginErrorReadLine) are what keep the child from blocking on a
/// full pipe — never started until HubChild has subscribed (see <see cref="BeginReading"/>).</summary>
internal sealed class RealHubProcess : IHubProcess
{
    private readonly Process _process;
    private readonly SafeFileHandle _job;
    private readonly StreamWriter _log;
    private readonly object _gate = new();

    public RealHubProcess(Process process, SafeFileHandle job, StreamWriter log)
    {
        _process = process;
        _job = job;
        _log = log;
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) RaiseLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) RaiseLine(e.Data); };
        _process.Exited += (_, _) => Exited?.Invoke();
    }

    public int Pid => _process.Id;
    public bool HasExited => _process.HasExited;
    public event Action<string>? OutputLine;
    public event Action? Exited;

    public void BeginReading()
    {
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>The kill-on-close job (disposed in Dispose) confines the whole tree; this is the
    /// immediate signal to the root, matching B4.</summary>
    public void Kill()
    {
        try { _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
    }

    private void RaiseLine(string line)
    {
        lock (_gate)
        {
            try { _log.WriteLine(line); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
        OutputLine?.Invoke(line);
    }

    public void Dispose()
    {
        _process.Dispose();
        _job.Dispose();   // kill-on-close: whatever is still inside dies here
        lock (_gate)
        {
            try { _log.Dispose(); }
            catch (IOException) { }
        }
    }
}
