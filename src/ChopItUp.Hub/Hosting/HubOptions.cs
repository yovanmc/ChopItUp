namespace ChopItUp.Hub.Hosting;

public enum HubCommand { Serve, RotateToken, PrintConfig }

/// <summary>Resolved startup options. Precedence: CLI args, then environment, then defaults.
/// Default data dir is <c>data\</c> beside the executable (release layout); dev and tests pass
/// an explicit directory. Port 0 = ephemeral (tests). A non-Serve command runs against the data
/// dir and exits: it binds no port and takes no hub lock, so it works while a hub is running.</summary>
public sealed record HubOptions(string DataDir, int Port, HubCommand Command = HubCommand.Serve, string? RotateParticipant = null)
{
    public const int DefaultPort = 8790;

    public static HubOptions Parse(string[] args, Func<string, string?> getEnv)
    {
        string? data = null;
        string? port = null;
        var command = HubCommand.Serve;
        string? rotate = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--data requires a value.");
                data = args[++i];
            }
            else if (args[i] == "--port")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--port requires a value.");
                port = args[++i];
            }
            else if (args[i] == "--rotate-token")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--rotate-token requires a participant name.");
                command = HubCommand.RotateToken;
                rotate = args[++i];
            }
            else if (args[i] == "--print-config")
            {
                command = HubCommand.PrintConfig;
            }
        }
        data ??= getEnv("CHOPITUP_DATA");
        port ??= getEnv("CHOPITUP_PORT");
        return new HubOptions(
            string.IsNullOrWhiteSpace(data) ? Path.Combine(AppContext.BaseDirectory, "data") : data,
            int.TryParse(port, out var p) ? p : DefaultPort,
            command,
            rotate);
    }
}
