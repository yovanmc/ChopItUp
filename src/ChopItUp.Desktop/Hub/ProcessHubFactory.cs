// See the comment in ShellArgs.cs: this project's implicit usings do not include System.IO.
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace ChopItUp.Desktop.Hub;

/// <summary>The real IHubProcessFactory. No ProjectReference to the Hub exists, so
/// "CHOPITUP_SHELL_TOKEN" is repeated here as a literal: it must match
/// ChopItUp.Hub.Hosting.HubOptions.ShellTokenEnvVar exactly.</summary>
public sealed class ProcessHubFactory : IHubProcessFactory
{
    // Internal so ProcessHubFactoryEnvVarTests can pin this literal against
    // ChopItUp.Hub.Hosting.HubOptions.ShellTokenEnvVar.
    internal const string ShellTokenEnvVar = "CHOPITUP_SHELL_TOKEN";
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
            // HubChild's Exited handler reports the real failure reason.
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
/// and the hub.log file, kills through the kill-on-close job as well as Process.Kill. The
/// output pumps (BeginOutputReadLine/BeginErrorReadLine) are what keep the child from blocking on a
/// full pipe, never started until HubChild has subscribed (see <see cref="BeginReading"/>).</summary>
internal sealed class RealHubProcess : IHubProcess
{
    private readonly Process _process;
    private readonly SafeFileHandle _job;
    private readonly StreamWriter _log;
    private readonly object _gate = new();
    private bool _beganReading;

    public RealHubProcess(Process process, SafeFileHandle job, StreamWriter log)
    {
        _process = process;
        _job = job;
        _log = log;
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) RaiseLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) RaiseLine(e.Data); };
        // Process.Exited can fire before the async output readers have delivered the child's last
        // lines (typically the diagnosis, e.g. "address already in use"). The parameterless
        // WaitForExit() also waits for the redirected streams, so running it here drains them before
        // Exited is raised. Bounded to 2s because a grandchild that inherited the pipe handle would
        // otherwise block it forever. BeginReading may never have been called, so guard that case.
        _process.Exited += (_, _) =>
        {
            if (_beganReading)
            {
                try { Task.Run(() => _process.WaitForExit()).Wait(TimeSpan.FromSeconds(2)); }
                catch { /* best-effort drain; the Exited event still fires either way */ }
            }
            Exited?.Invoke();
        };
    }

    public int Pid => _process.Id;
    public bool HasExited => _process.HasExited;
    public event Action<string>? OutputLine;
    public event Action? Exited;

    public void BeginReading()
    {
        _beganReading = true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>The kill-on-close job (disposed in Dispose) confines the whole tree; this is the
    /// immediate signal to the root.</summary>
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
