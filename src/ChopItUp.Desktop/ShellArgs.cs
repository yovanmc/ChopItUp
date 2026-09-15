// UseWindowsForms's implicit-usings set does not include System.IO (measured: the generated
// GlobalUsings.g.cs for this project has only System, System.Collections.Generic, System.Linq,
// System.Threading, System.Threading.Tasks), unlike a plain net10.0 project such as ChopItUp.Hub.
using System.IO;

namespace ChopItUp.Desktop;

public enum ShellCommand { Run, Show, Quit }

/// <summary>Row 12: the shell's whole command line. Everything resolves to an absolute path so the
/// child hub, the log and the WebView2 profile agree on one data dir regardless of the launcher's cwd.
/// `--hub` exists for development (B10): the deployed layout has the hub beside this exe.</summary>
public sealed record ShellArgs(string DataDir, int Port, string HubExe, ShellCommand Command)
{
    public const int DefaultPort = 8790;
    public const string HubExeName = "ChopItUp.Hub.exe";

    public string LogDir => Path.Combine(DataDir, "logs");
    /// <summary>B11: outside data\. Keyed by the data dir so two data dirs never share a profile.</summary>
    public string WebViewProfileDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChopItUp", "webview2", SingleInstance.Hash16(DataDir));
    /// <summary>The origin implied by --port. Only the START path may use it; after StartOrAttach every consumer reads HubChild.ResolvedOrigin (B3).</summary>
    public Uri RequestedOrigin => new($"http://127.0.0.1:{Port}/");

    public static ShellArgs Parse(string[] args, string baseDir, string? cwd = null)
    {
        cwd ??= Environment.CurrentDirectory;
        string? data = null, port = null, hub = null;
        var command = ShellCommand.Run;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data": data = Next(args, ref i); break;
                case "--port": port = Next(args, ref i); break;
                case "--hub": hub = Next(args, ref i); break;
                case "--show": command = ShellCommand.Show; break;
                case "--quit": command = ShellCommand.Quit; break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }
        var p = port is null ? DefaultPort
            : int.TryParse(port, out var parsed) && parsed is > 0 and <= 65535 ? parsed
            : throw new ArgumentException($"--port needs a number between 1 and 65535, got '{port}'.");
        return new ShellArgs(
            Path.GetFullPath(data is null ? Path.Combine(baseDir, "data") : Path.Combine(cwd, data)),
            p,
            Path.GetFullPath(hub is null ? Path.Combine(baseDir, HubExeName) : Path.Combine(cwd, hub)),
            command);
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} requires a value.");
        return args[++i];
    }
}
