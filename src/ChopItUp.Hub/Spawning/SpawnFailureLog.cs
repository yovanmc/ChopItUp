using System.Globalization;
using System.Text;

namespace ChopItUp.Hub.Spawning;

/// <summary>The raw output of spawns that ended without posting, kept so a real usage-limit line
/// (or any other failure) can be read after the fact. One file per spawn under the hub's data
/// folder, which already holds the hub's other private state; each stream is scrubbed whole and then
/// cut to its last <see cref="StreamChars"/> characters, and only the newest <see cref="Keep"/> files
/// stay.</summary>
public static class SpawnFailureLog
{
    public const string Folder = "spawn-failures";
    public const int Keep = 200;
    public const int StreamChars = 64 * 1024;

    /// <summary>Writes the record and returns its path relative to <paramref name="dataDir"/>, or null
    /// when it could not be written (logged; a failed spawn's note never depends on it).</summary>
    public static string? Write(string dataDir, string spawnId, string participantId, string host, int? exitCode,
        string stdout, string stderr, Func<string, string> scrub, DateTimeOffset now)
    {
        try
        {
            var folder = Path.Combine(dataDir, Folder);
            Directory.CreateDirectory(folder);
            var name = $"{now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{spawnId}.txt";
            var text = new StringBuilder()
                .Append("spawn ").Append(spawnId).Append(" @").Append(participantId).Append(" (").Append(host).Append(") exit ")
                .Append(exitCode?.ToString(CultureInfo.InvariantCulture) ?? "none").Append(" at ").Append(now.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)).Append('\n')
                .Append("== stdout ==\n").Append(Tail(scrub(stdout))).Append('\n')
                .Append("== stderr ==\n").Append(Tail(scrub(stderr))).Append('\n')
                .ToString();
            File.WriteAllText(Path.Combine(folder, name), text, new UTF8Encoding(false));
            foreach (var old in Directory.GetFiles(folder, "*.txt").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(Keep))
                File.Delete(old);
            return Path.Combine(Folder, name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"spawn {spawnId}: keeping its raw output failed: {e.Message}");
            return null;
        }
    }

    private static string Tail(string text) => text.Length <= StreamChars ? text : text[^StreamChars..];
}
