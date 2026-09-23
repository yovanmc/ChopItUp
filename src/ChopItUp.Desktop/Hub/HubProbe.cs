// See the comment in ShellArgs.cs: this project's implicit usings do not include System.IO.
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ChopItUp.Desktop.Hub;

/// <summary>The real-disk half of attach detection. Deliberately not seamed behind an interface:
/// these are two cheap, synchronous file checks that mirror the hub's own shape exactly
/// (HubLock.IsHeld, HubPortFile.Read) rather than a dependency worth faking.</summary>
public static class HubProbe
{
    private const string LockFileName = "hub.lock";
    private const string PortFileName = "hub.port";

    /// <summary>Mirrors HubLock.IsHeld (src/ChopItUp.Hub/Hosting/HubLock.cs): opens the lock file
    /// exclusively and releases it at once. False when it opens (or the file does not exist), true on
    /// the IOException a held lock throws. An UnauthorizedAccessException is not caught here — it
    /// propagates to HubChild's outer guard, which turns it into a Failed status naming the path.</summary>
    public static bool LockIsHeld(string dataDir)
    {
        var path = Path.Combine(dataDir, LockFileName);
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>The port a hub last wrote to hub.port, or null when the file is absent or unparsable.</summary>
    public static int? ReadPort(string dataDir)
    {
        var path = Path.Combine(dataDir, PortFileName);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path).Trim();
        return int.TryParse(text, out var port) && port is > 0 and <= 65535 ? port : null;
    }
}

/// <summary>The one real IHealthProbe. A single shared client with a short timeout so a hub that
/// never answers does not stall the ready/attach poll loop.</summary>
public sealed class HttpHealthProbe : IHealthProbe
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(2) };

    public async Task<bool> IsHealthyAsync(Uri origin, CancellationToken ct)
    {
        try
        {
            using var response = await Client.GetAsync(new Uri(origin, "health"), ct);
            if (response.StatusCode != HttpStatusCode.OK) return false;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch
        {
            // Any failure (connection refused, timeout, malformed JSON) means "not healthy yet".
            // A cancelled ct still stops the caller: Task.Delay(PollInterval, _clock, ct) observes
            // the same token right after this returns and throws there instead.
            return false;
        }
    }
}
