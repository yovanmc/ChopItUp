using System.Globalization;

namespace ChopItUp.Hub.Hosting;

/// <summary>The port the running (or last-running) hub bound, written beside the data. --print-config
/// reads it so the emitted URLs match reality rather than whatever port that invocation resolved.</summary>
public static class HubPortFile
{
    public const string FileName = "hub.port";

    public static void Write(string dataDir, int port) =>
        File.WriteAllText(Path.Combine(dataDir, FileName), port.ToString(CultureInfo.InvariantCulture));

    public static int? Read(string dataDir)
    {
        var path = Path.Combine(dataDir, FileName);
        if (!File.Exists(path)) return null;
        return int.TryParse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535
            ? p : null;
    }
}
