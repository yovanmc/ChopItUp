// See the comment in ShellArgs.cs: this project's implicit usings do not include System.IO.
using System.IO;

namespace ChopItUp.Desktop;

/// <summary>An append-only text log for the shell process itself (`desktop.log`, beside the hub's own
/// log under the same data dir). Never throws: a logging failure must not take the shell down. The
/// hub's own log tail is a separate concern (LogTail).</summary>
public sealed class ShellLog
{
    private static readonly object Gate = new();

    /// <summary>desktop.log follows the same 5 MB rule as hub.log (<see cref="Hub.ProcessHubFactory"/>):
    /// rename to the ".1" file, replacing any previous one, before appending. Kept
    /// internal-with-a-real-default rather than a magic literal in Append so a test can inject a
    /// byte-scale threshold instead of writing 5 MB of log lines.</summary>
    private const long DefaultRotateBytes = 5 * 1024 * 1024;

    private readonly long _rotateBytes;

    public string Path { get; }

    public ShellLog(ShellArgs args) : this(System.IO.Path.Combine(args.LogDir, "desktop.log"))
    {
    }

    public ShellLog(string path) : this(path, DefaultRotateBytes)
    {
    }

    internal ShellLog(string path, long rotateBytes)
    {
        Path = path;
        _rotateBytes = rotateBytes;
    }

    public void Append(string line)
    {
        try
        {
            lock (Gate)
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                RotateIfNeeded();
                File.AppendAllText(Path, $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {line}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // The log must never take the shell down.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: swallow.
        }
    }

    /// <summary>Called with Gate already held. Mirrors ProcessHubFactory's hub.log rotation: past the
    /// threshold, the current file becomes "<name>.1<ext>" (desktop.log -> desktop.1.log), replacing
    /// whatever was there before, and this Append starts a fresh file.</summary>
    private void RotateIfNeeded()
    {
        if (!File.Exists(Path)) return;
        if (new FileInfo(Path).Length <= _rotateBytes) return;

        var dir = System.IO.Path.GetDirectoryName(Path);
        var rotated = System.IO.Path.Combine(
            string.IsNullOrEmpty(dir) ? "" : dir,
            System.IO.Path.GetFileNameWithoutExtension(Path) + ".1" + System.IO.Path.GetExtension(Path));
        File.Delete(rotated);
        File.Move(Path, rotated);
    }
}
