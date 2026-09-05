using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChopItUp.Core.Storage;

namespace ChopItUp.Hub.Security;

/// <summary>One bearer token per participant, generated on first run into <c>tokens.json</c> in the
/// data dir. Tokens are the only credential the hub knows; they never leave the machine.</summary>
public sealed class TokenStore
{
    public const string FileName = "tokens.json";

    private readonly Dictionary<string, string> _byToken;

    public IReadOnlyDictionary<string, string> Tokens { get; }
    public int Count => Tokens.Count;

    private TokenStore(Dictionary<string, string> byParticipant)
    {
        Tokens = byParticipant;
        _byToken = byParticipant.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
    }

    /// <summary>Read-generate-write under a per-path cross-process mutex, with an atomic replace, so
    /// neither a concurrent start nor a crash mid-write can tear or silently re-mint the file the
    /// hosts' configs were pasted from. A loaded TokenStore is a startup singleton: editing
    /// tokens.json takes effect on the next hub start. The ids come from the roster the hub read at
    /// start (<c>ParticipantStore.List</c>): a roster row with no token gets one minted here, which is
    /// how a database upgraded to v3 gets its nine new tokens on the next start.</summary>
    public static TokenStore Load(string dataDir, IReadOnlyList<string> participantIds)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, FileName);
        return PathMutex.Run("Global\\ChopItUp.Tokens.", path, TimeSpan.FromSeconds(10), () =>
        {
            bool existed = File.Exists(path);
            Dictionary<string, string> tokens = existed
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
                : new();
            bool changed = false;
            foreach (var p in participantIds)
            {
                if (!tokens.TryGetValue(p, out var t) || string.IsNullOrWhiteSpace(t))
                {
                    tokens[p] = NewToken();
                    changed = true;
                    if (existed)
                        Console.Error.WriteLine($"tokens.json had no token for '{p}'; minted one. If that participant has a host file, run --print-config and re-paste it.");
                }
            }
            if (changed)
                WriteAtomically(path, JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true }));
            return new TokenStore(tokens);
        });
    }

    /// <summary>Reads tokens.json as it stands, minting nothing and writing nothing. Used by the
    /// non-serving verbs, which must never create a credential as a side effect of being run against
    /// the wrong directory (pass 2, MINOR-12): <see cref="Load"/> back-fills any missing participant
    /// and rewrites the file, which would silently rotate a hand-edited token from a read-only
    /// command.</summary>
    public static IReadOnlyDictionary<string, string> ReadExisting(string dataDir, IReadOnlyList<string> participantIds)
    {
        var path = Path.Combine(dataDir, FileName);
        if (!File.Exists(path)) throw new FileNotFoundException($"No {FileName} in '{dataDir}'.", path);
        var tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        var missing = participantIds.Where(p => !tokens.TryGetValue(p, out var t) || string.IsNullOrWhiteSpace(t)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"{FileName} has no token for: {string.Join(", ", missing)}. Start the hub once to mint them.");
        return tokens;
    }

    /// <summary>Sibling temp file + rename: a crash mid-write never leaves a truncated tokens.json.</summary>
    private static void WriteAtomically(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Constant-time lookup: compares the presented token against every stored token so
    /// timing does not reveal which prefix matched.</summary>
    public bool TryResolve(string presented, out string participantId)
    {
        participantId = "";
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        string? match = null;
        foreach (var (token, participant) in _byToken)
        {
            var bytes = Encoding.UTF8.GetBytes(token);
            if (bytes.Length == presentedBytes.Length && CryptographicOperations.FixedTimeEquals(bytes, presentedBytes))
                match = participant;
        }
        if (match is null) return false;
        participantId = match;
        return true;
    }

    /// <summary>Mints a fresh token for one participant, leaving the others byte-identical. Same
    /// mutex and same atomic replace as <see cref="Load"/>.
    ///
    /// A running hub holds its TokenStore for the life of the process, so writing this file while a
    /// hub is up revokes nothing — the leaked token keeps full access to every room until someone
    /// remembers to restart. The caller must therefore refuse to rotate while a hub owns the data
    /// dir (see <c>HostCommands.RotateToken</c>): ordering, not vigilance (pass 2, MAJOR-6).</summary>
    public static string Rotate(string dataDir, IReadOnlyList<string> participantIds, string participant)
    {
        if (!participantIds.Contains(participant, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown participant '{participant}'. Known: {string.Join(", ", participantIds)}.", nameof(participant));
        var path = Path.Combine(dataDir, FileName);
        // Deliberately NOT CreateDirectory: rotating against a mistyped --data would otherwise mint a
        // fresh token set in a directory no hub uses and print a token that authenticates nothing
        // (critique pass 1, F8). A rotation only makes sense where tokens already live.
        if (!File.Exists(path))
            throw new FileNotFoundException($"No {FileName} in '{dataDir}'. Start the hub once against this data directory first, or check --data.", path);
        return PathMutex.Run("Global\\ChopItUp.Tokens.", path, TimeSpan.FromSeconds(10), () =>
        {
            var tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
            var minted = NewToken();
            tokens[participant] = minted;                                    // only this one changes
            WriteAtomically(path, JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true }));
            return minted;
        });
    }

    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
