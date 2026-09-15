// See the comment in ShellArgs.cs: this project's implicit usings do not include System.IO.
using System.IO;

namespace ChopItUp.Desktop;

/// <summary>Row 12: an append-only text log for the shell process itself (`desktop.log`, beside the
/// hub's own log under the same data dir). Never throws: a logging failure must not take the shell
/// down. The hub's own log tail is a separate concern (Task 3, LogTail).</summary>
public sealed class ShellLog
{
    private static readonly object Gate = new();

    public string Path { get; }

    public ShellLog(ShellArgs args) : this(System.IO.Path.Combine(args.LogDir, "desktop.log"))
    {
    }

    public ShellLog(string path)
    {
        Path = path;
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
}
