namespace ChopItUp.Hub.Hosting;

/// <summary>Resolved startup options. Precedence: CLI args, then environment, then defaults.
/// Default data dir is <c>data\</c> beside the executable (release layout); dev and tests pass
/// an explicit directory. Port 0 = ephemeral (tests).</summary>
public sealed record HubOptions(string DataDir, int Port)
{
    public const int DefaultPort = 8790;

    public static HubOptions Parse(string[] args, Func<string, string?> getEnv)
    {
        string? data = null;
        string? port = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--data") data = args[++i];
            else if (args[i] == "--port") port = args[++i];
        }
        data ??= getEnv("CHOPITUP_DATA");
        port ??= getEnv("CHOPITUP_PORT");
        return new HubOptions(
            string.IsNullOrWhiteSpace(data) ? Path.Combine(AppContext.BaseDirectory, "data") : data,
            int.TryParse(port, out var p) ? p : DefaultPort);
    }
}
