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
    public static readonly string[] Participants = ["owner", "claude", "codex"];

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
    /// tokens.json takes effect on the next hub start.</summary>
    public static TokenStore Load(string dataDir)
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
            foreach (var p in Participants)
            {
                if (!tokens.TryGetValue(p, out var t) || string.IsNullOrWhiteSpace(t))
                {
                    tokens[p] = NewToken();
                    changed = true;
                    if (existed)
                        Console.Error.WriteLine($"tokens.json had no token for '{p}'; minted a new one. Paste it into that host's config.");
                }
            }
            if (changed)
                WriteAtomically(path, JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true }));
            return new TokenStore(tokens);
        });
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

    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
