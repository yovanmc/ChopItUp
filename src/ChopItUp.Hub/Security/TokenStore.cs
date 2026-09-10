using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Security;

/// <summary>Row 28: two credential classes, split by <see cref="ExchangePolicy.IsSpawnable"/> (the
/// predicate is total over <c>ChopDb.SeedRoster</c> — verified this task, not inferred). A row the
/// hub can spawn (<c>Kind == "model"</c> with a <c>Model</c> of its own) gets an EPHEMERAL bearer:
/// minted fresh into memory on every <see cref="Load"/> and never written anywhere. Everything else
/// except <c>Kind == "system"</c> (the hub itself, which authenticates nothing) is a HOST-FILE row —
/// a value someone pastes into a Claude/Codex config — and its credential is hashed at rest: the
/// plaintext is shown once, at the moment it is minted (<see cref="MintFor"/>), and never stored.
/// Reading every file under the data dir therefore yields nothing that authenticates as a host-file
/// participant (AC6); it still yields nothing at all for a spawnable one, because nothing was ever
/// written for it in the first place.</summary>
public sealed class TokenStore
{
    public const string FileName = "tokens.json";

    private readonly string _dataDir;
    private readonly string[] _allIds;
    private readonly Dictionary<string, string> _hashed;      // host-file participantId -> sha256 hex, persisted
    private readonly Dictionary<string, string> _ephemeral;   // spawnable participantId -> plaintext, in-memory only
    private readonly Dictionary<string, string> _justMinted;  // host-file participantId -> plaintext, this instance's one-time reveal (Load's own backfill, or a later MintFor)

    /// <summary>How many participants currently hold a credential of either class. Excludes
    /// <c>system</c> rows, which hold none.</summary>
    public int Count => _hashed.Count + _ephemeral.Count;

    private TokenStore(string dataDir, string[] allIds, Dictionary<string, string> hashed, Dictionary<string, string> ephemeral, Dictionary<string, string> justMinted)
    {
        _dataDir = dataDir;
        _allIds = allIds;
        _hashed = hashed;
        _ephemeral = ephemeral;
        _justMinted = justMinted;
    }

    /// <summary>Read-generate-write under a per-path cross-process mutex, with an atomic replace, so
    /// neither a concurrent start nor a crash mid-write can tear the file. A loaded TokenStore is a
    /// startup singleton: editing tokens.json takes effect on the next hub start.
    ///
    /// Per <paramref name="participants"/> row: <c>system</c> (hub) is skipped entirely — it is
    /// minted nothing and refused on <c>/mcp</c>. A spawnable row is minted straight into the
    /// in-memory ephemeral map every call, regardless of what (if anything) the file says about it —
    /// it never has a host file, so there is nothing to keep in sync. Everything else is a host-file
    /// row: an existing entry is read (a plaintext string is HASHED in place — the schema-evolution
    /// path, detected per entry so a hand-edited plaintext value in an otherwise-hashed file is
    /// simply hashed next start — an object is taken as already migrated), and a missing entry is
    /// minted fresh, hashed, and recorded in <see cref="MintFor"/>'s companion one-time reveal so the
    /// caller that just backfilled a database's new participants can still learn the values (which is
    /// how a schema upgrade's new rows get their tokens told to the operator on the next start).
    /// Nothing is ever written back in plaintext.</summary>
    public static TokenStore Load(string dataDir, IReadOnlyList<Participant> participants)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, FileName);
        return PathMutex.Run("Global\\ChopItUp.Tokens.", path, TimeSpan.FromSeconds(10), () =>
        {
            bool existed = File.Exists(path);
            var raw = existed
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? new()
                : new Dictionary<string, JsonElement>();

            var hashed = new Dictionary<string, string>(StringComparer.Ordinal);
            var ephemeral = new Dictionary<string, string>(StringComparer.Ordinal);
            var justMinted = new Dictionary<string, string>(StringComparer.Ordinal);
            bool changed = false;

            foreach (var p in participants)
            {
                if (p.Kind == "system") continue;
                if (ExchangePolicy.IsSpawnable(p))
                {
                    ephemeral[p.Id] = NewToken();
                    continue;
                }
                if (raw.TryGetValue(p.Id, out var entry))
                {
                    if (entry.ValueKind == JsonValueKind.String) changed = true;   // migrating this entry to the hashed shape
                    hashed[p.Id] = HashFromEntry(entry, p.Id, path);
                }
                else
                {
                    var minted = NewToken();
                    hashed[p.Id] = Hash(minted);
                    justMinted[p.Id] = minted;
                    changed = true;
                    if (existed)
                        Console.Error.WriteLine($"tokens.json had no token for '{p.Id}'; minted one. If that participant has a host file, run --print-config and re-paste it.");
                }
            }

            if (changed) WriteHashed(path, hashed);
            return new TokenStore(dataDir, participants.Select(p => p.Id).ToArray(), hashed, ephemeral, justMinted);
        });
    }

    /// <summary>Reads tokens.json as it stands, minting nothing and writing nothing. Used by the
    /// non-serving verbs, which must never create a credential as a side effect of being run against
    /// the wrong directory (pass 2, MINOR-12): <see cref="Load"/> back-fills any missing participant
    /// and rewrites the file, which would silently rotate a hand-edited token from a read-only
    /// command. Narrowed to host-file rows (row 28): a spawnable or system row is legitimately absent
    /// from the file and must never be reported "missing" — only a host-file row without an entry is
    /// an error. The values returned are hashes, not credentials: nothing that reads this file back
    /// can ever recover a host-file row's plaintext (AC6).</summary>
    public static IReadOnlyDictionary<string, string> ReadExisting(string dataDir, IReadOnlyList<Participant> participants)
    {
        var path = Path.Combine(dataDir, FileName);
        if (!File.Exists(path)) throw new FileNotFoundException($"No {FileName} in '{dataDir}'.", path);
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? new();
        var hostFile = participants.Where(p => p.Kind != "system" && !ExchangePolicy.IsSpawnable(p)).ToArray();
        var missing = hostFile.Where(p => !raw.ContainsKey(p.Id)).Select(p => p.Id).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"{FileName} has no token for: {string.Join(", ", missing)}. Start the hub once to mint them.");
        return hostFile.ToDictionary(p => p.Id, p => HashFromEntry(raw[p.Id], p.Id, path), StringComparer.Ordinal);
    }

    /// <summary>The plaintext bearer to hand a spawn (<see cref="ChopItUp.Hub.Spawning.SpawnerService.Launch"/>).
    /// Backed by the ephemeral, never-persisted map; throws for anything not spawnable — there is no
    /// bearer to hand out for a host-file or system row through this path.</summary>
    public string BearerFor(string participantId) =>
        _ephemeral.TryGetValue(participantId, out var t) ? t
        : throw new KeyNotFoundException($"'{participantId}' has no ephemeral bearer: it is not a spawnable participant.");

    /// <summary>Constant-time-ish lookup: the presented value is hashed once and compared against
    /// every stored hash (fixed-length, so this leaks no more than a hash comparison ever does),
    /// then — only if nothing matched — compared byte-for-byte against every ephemeral plaintext.
    /// Neither loop breaks early on a match, matching the discipline the original single-map version
    /// used.</summary>
    public bool TryResolve(string presented, out string participantId)
    {
        participantId = "";
        var presentedHash = Encoding.UTF8.GetBytes(Hash(presented));
        string? match = null;
        foreach (var (id, hash) in _hashed)
        {
            var bytes = Encoding.UTF8.GetBytes(hash);
            if (bytes.Length == presentedHash.Length && CryptographicOperations.FixedTimeEquals(bytes, presentedHash))
                match = id;
        }
        if (match is null)
        {
            var presentedBytes = Encoding.UTF8.GetBytes(presented);
            foreach (var (id, token) in _ephemeral)
            {
                var bytes = Encoding.UTF8.GetBytes(token);
                if (bytes.Length == presentedBytes.Length && CryptographicOperations.FixedTimeEquals(bytes, presentedBytes))
                    match = id;
            }
        }
        if (match is null) return false;
        participantId = match;
        return true;
    }

    /// <summary>The ONE mint entry point (row 28): returns a fresh plaintext once, persisting only its
    /// hash, for a host-file row. Shared by <c>--rotate-token</c> and the test fixture — the only two
    /// callers that ever legitimately need a host-file row's plaintext after the first reveal. Reads
    /// the file fresh under the mutex before writing (like the old <c>Rotate</c> did) so a concurrent
    /// change to a different key is never clobbered; only <paramref name="participantId"/>'s entry
    /// changes.
    ///
    /// A running hub holds its TokenStore for the life of the process, so writing this file while a
    /// hub is up revokes nothing — the leaked token keeps full access to every room until someone
    /// remembers to restart. The caller must therefore refuse to rotate while a hub owns the data dir
    /// (see <c>HostCommands.RotateToken</c>): ordering, not vigilance (pass 2, MAJOR-6).</summary>
    public string MintFor(string participantId)
    {
        if (!_allIds.Contains(participantId, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown participant '{participantId}'. Known: {string.Join(", ", _allIds)}.", nameof(participantId));
        if (!_hashed.ContainsKey(participantId))
            throw new ArgumentException($"'{participantId}' has no host-file token to mint: it is either ephemeral (minted fresh every hub start, never persisted) or a system row (never authenticates). Host-file participants: {string.Join(", ", _hashed.Keys.OrderBy(k => k, StringComparer.Ordinal))}.", nameof(participantId));
        var path = Path.Combine(_dataDir, FileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No {FileName} in '{_dataDir}'. Start the hub once against this data directory first, or check --data.", path);
        return PathMutex.Run("Global\\ChopItUp.Tokens.", path, TimeSpan.FromSeconds(10), () =>
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? new();
            var minted = NewToken();
            var hash = Hash(minted);
            raw[participantId] = JsonSerializer.SerializeToElement(new { sha256 = hash });   // only this key changes
            WriteAtomically(path, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
            _hashed[participantId] = hash;
            _justMinted[participantId] = minted;
            return minted;
        });
    }

    /// <summary>The value from one tokens.json entry, hashed if it is not already: a JSON string is
    /// the pre-row-28 plaintext shape (hashed here, never stored back as-is), a
    /// <c>{"sha256":"..."}</c> object is already migrated and its hash is taken verbatim. Anything
    /// else fails with one clear line naming the file and the entry, never a raw
    /// <see cref="JsonException"/> (row 28 Task 1's requirement).</summary>
    private static string HashFromEntry(JsonElement entry, string participantId, string path) => entry.ValueKind switch
    {
        JsonValueKind.String when entry.GetString() is { Length: > 0 } plaintext => Hash(plaintext),
        JsonValueKind.Object when entry.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String
            && sha.GetString() is { Length: > 0 } hex => hex,
        _ => throw new InvalidOperationException($"{path}: entry '{participantId}' is not a recognised token shape (expected a plaintext string or {{\"sha256\":\"...\"}})."),
    };

    private static string Hash(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();

    private static void WriteHashed(string path, Dictionary<string, string> hashed)
    {
        var obj = hashed.ToDictionary(kv => kv.Key, kv => new { sha256 = kv.Value }, StringComparer.Ordinal);
        WriteAtomically(path, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Sibling temp file + rename: a crash mid-write never leaves a truncated tokens.json.</summary>
    private static void WriteAtomically(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
