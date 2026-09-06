namespace ChopItUp.Hub.Hosting;

public enum HubCommand { Serve, RotateToken, PrintConfig }

/// <summary>Resolved startup options. Precedence: CLI args, then environment, then defaults.
/// Default data dir is <c>data\</c> beside the executable (release layout); dev and tests pass
/// an explicit directory. Port 0 = ephemeral (tests). A non-Serve command runs against the data
/// dir and exits: it binds no port and takes no hub lock, so it works while a hub is running.
/// <paramref name="WebRoot"/> is the built web client (M3 D3): null means <c>wwwroot\</c> beside the
/// executable, which is where the csproj's npm step lands it. It is not a CLI flag — the only reason
/// it is settable is so tests can point at a fabricated client without writing into the test output.
/// <paramref name="RoomsRoot"/> is where hub-created room directories go (M9).</summary>
public sealed record HubOptions(string DataDir, int Port, HubCommand Command = HubCommand.Serve, string? RotateParticipant = null, string? WebRoot = null, string? RoomsRoot = null)
{
    public const int DefaultPort = 8790;

    /// <summary>Where hub-created room directories go (M9 decision 2): `--rooms-root`, then
    /// `CHOPITUP_ROOMS`, then `%USERPROFILE%\ChopItUp\rooms` — a folder inside the profile, which D12
    /// allows, because the install dir is under C:\Self Apps and the data dir holds the tokens.</summary>
    public string RoomsRootPath => Path.GetFullPath(string.IsNullOrWhiteSpace(RoomsRoot) ? DefaultRoomsRoot() : RoomsRoot);

    public static string DefaultRoomsRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ChopItUp", "rooms");

    public static HubOptions Parse(string[] args, Func<string, string?> getEnv)
    {
        string? data = null;
        string? port = null;
        var command = HubCommand.Serve;
        string? rotate = null;
        string? rooms = null;
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
            else if (args[i] == "--rooms-root")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--rooms-root requires a value.");
                rooms = args[++i];
            }
        }
        data ??= getEnv("CHOPITUP_DATA");
        port ??= getEnv("CHOPITUP_PORT");
        rooms ??= getEnv("CHOPITUP_ROOMS");
        return new HubOptions(
            Path.GetFullPath(string.IsNullOrWhiteSpace(data) ? Path.Combine(AppContext.BaseDirectory, "data") : data),
            int.TryParse(port, out var p) ? p : DefaultPort,
            command,
            rotate,
            RoomsRoot: string.IsNullOrWhiteSpace(rooms) ? null : rooms);
    }
}
