using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChopItUp.Core.Memory;

namespace ChopItUp.Hub.Memory;

/// <summary>The states <see cref="ExportManifest.Verify"/> distinguishes (ticket 02's acceptance):
/// nothing there, something there with no record at all, a record that cannot be parsed, a record
/// bound to a different store, a record bound to this store whose files no longer match it, and a
/// clean match. <see cref="Foreign"/> and <see cref="Absent"/> are never the same thing — the whole
/// point of D5 is that a populated directory with no manifest must refuse, not proceed as if empty.</summary>
public enum TargetState { Absent, Foreign, Unreadable, DifferentSource, Drifted, Clean }

/// <summary><see cref="Paths"/> is the affected-paths list D2 requires every refusal and every
/// override to share verbatim; <see cref="ManifestRoot"/> is the manifest's own <c>SourceRoot</c>,
/// carried so a <see cref="TargetState.DifferentSource"/> report can print both roots without
/// re-reading the manifest.</summary>
public sealed record ManifestVerdict(TargetState State, IReadOnlyList<string> Paths, string? ManifestRoot);

/// <summary>The export's receipt: binds a target directory to the store it came from, by the store's
/// ROOT PATH and only that (D4) — never by content, because two stores holding the same entries are
/// indistinguishable by content, which is precisely finding 1's attack, and a store's content changes
/// on every legitimate re-export. <see cref="SourceFingerprint"/> is carried purely for the report to
/// say whether anything changed since last time; it is never a refusal predicate, and must never be
/// promoted into one. <see cref="Files"/> maps a recursive path (relative to the target directory,
/// forward-slash separated) to that file's SHA-256 hex hash; the manifest's own file
/// (<see cref="FileName"/>) is deliberately excluded from this map and from every enumeration
/// <see cref="Verify"/> performs (pass 2 M15a) — it cannot hash itself, and a naive recursive diff
/// would flag it as drift on every second run.</summary>
public sealed record ExportManifest(
    int Version, string SourceRoot, string SourceFingerprint, string ExportedAt,
    IReadOnlyDictionary<string, string> Files)
{
    /// <summary>Not a <c>*.md</c> file, so <see cref="MemoryImport"/> never reads it as a memory.</summary>
    public const string FileName = ".chopitup-export.json";

    /// <summary>The schema version this build writes. <see cref="TryRead"/> refuses only a version
    /// GREATER than this — an older (or equal) manifest, even one hand-written in a shape this code
    /// never produced, must still read (pass 2 M8); that permissiveness, not per-version branching,
    /// is the schema-evolution guard.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Writes the manifest into <paramref name="targetDir"/> as <see cref="FileName"/>.</summary>
    public static void Write(string targetDir, ExportManifest manifest) =>
        File.WriteAllText(Path.Combine(targetDir, FileName), JsonSerializer.Serialize(manifest, WriteOptions));

    /// <summary>Reads the manifest out of <paramref name="targetDir"/>. Returns <c>null</c> for every
    /// failure — missing file, truncated JSON, an empty object, a missing field, or a
    /// <see cref="Version"/> newer than <see cref="CurrentVersion"/> — and never throws (ticket 02:
    /// "reading the record must never throw"). The caller decides what an unreadable record means.</summary>
    public static ExportManifest? TryRead(string targetDir)
    {
        try
        {
            var path = Path.Combine(targetDir, FileName);
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!TryGetInt(root, "Version", out var version)) return null;
            if (version > CurrentVersion) return null;   // a version newer than this code understands

            if (!TryGetString(root, "SourceRoot", out var sourceRoot)) return null;
            if (!TryGetString(root, "SourceFingerprint", out var fingerprint)) return null;
            if (!TryGetString(root, "ExportedAt", out var exportedAt)) return null;
            if (!root.TryGetProperty("Files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Object) return null;

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in filesEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) return null;
                files[prop.Name] = prop.Value.GetString() ?? "";
            }

            return new ExportManifest(version, sourceRoot, fingerprint, exportedAt, files);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value);
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString() ?? "";
        return true;
    }

    /// <summary>SHA-256 hex (lowercase) over a file's bytes. Shared by the writer that builds
    /// <see cref="Files"/> and by <see cref="Verify"/>, which recomputes the same hash to detect
    /// drift — the two must never diverge.</summary>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Report-only (D4): SHA-256 hex over <c>"topic\ntitle\nbody\n"</c> per live entry, in
    /// the same render order <see cref="MemoryExport.Render"/> uses (core first, then
    /// <c>ListTopics()</c>'s order; superseded entries excluded). This answers "has the store changed
    /// since this export" for the report to print — it can NEVER be a refusal predicate, because a
    /// scratch store holding the same entries as the real one produces an identical fingerprint by
    /// construction (that is finding 1's attack), and an ordinary store's fingerprint changes on
    /// every legitimate re-export. Only <see cref="SourceRoot"/> distinguishes stores.</summary>
    public static string Fingerprint(MemoryStore store)
    {
        var topics = MemoryExport.LiveTopics(store);

        var sb = new StringBuilder();
        foreach (var topic in topics)
            foreach (var entry in store.Entries(topic))
                if (!entry.Superseded)
                    sb.Append(topic).Append('\n').Append(entry.Title).Append('\n').Append(entry.Body).Append('\n');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>Compares <paramref name="manifest"/> (the result of a prior <see cref="TryRead"/>,
    /// which may be <c>null</c>) against what is actually in <paramref name="targetDir"/> right now.
    /// Order matters (ticket 02): a source mismatch is decided BEFORE any file comparison, so a
    /// matching set of file hashes can never satisfy the guard on its own — <paramref name="store"/>
    /// is consulted only for its <see cref="MemoryStore.Root"/>, never re-rendered to compare content.</summary>
    public static ManifestVerdict Verify(ExportManifest? manifest, string targetDir, MemoryStore store)
    {
        if (!Directory.Exists(targetDir))
            return new ManifestVerdict(TargetState.Absent, Array.Empty<string>(), null);

        // "Empty" is judged over EVERY file including the manifest — a directory holding only a
        // corrupt manifest is "something there", not "nothing there" (Unreadable, not Absent).
        var manifestFileExists = File.Exists(Path.Combine(targetDir, FileName));
        var otherPaths = RelativePaths(targetDir);   // already excludes the manifest itself
        if (!manifestFileExists && otherPaths.Count == 0)
            return new ManifestVerdict(TargetState.Absent, Array.Empty<string>(), null);

        if (manifest is null)
        {
            // Both Unreadable and Foreign carry the same recursive-relative-path list of whatever else
            // is in the directory (carried-over fix from the T2 review, acceptance criterion 6): a
            // corrupt manifest sitting beside real files must name those files, not report an empty
            // list. A directory holding only the corrupt manifest legitimately has nothing else to
            // name, so otherPaths is empty there too.
            return manifestFileExists
                ? new ManifestVerdict(TargetState.Unreadable, otherPaths, null)
                : new ManifestVerdict(TargetState.Foreign, otherPaths, null);   // D5: populated + no manifest refuses
        }

        // Computed regardless of the source check below (pass 2 M4): DifferentSource must still be
        // able to print a drift list, not just both roots.
        var driftPaths = ComputeDrift(manifest, targetDir);

        var sourceMatches = string.Equals(NormalizeRoot(manifest.SourceRoot), NormalizeRoot(store.Root), StringComparison.OrdinalIgnoreCase);
        if (!sourceMatches)
            return new ManifestVerdict(TargetState.DifferentSource, driftPaths, manifest.SourceRoot);

        if (driftPaths.Count > 0)
            return new ManifestVerdict(TargetState.Drifted, driftPaths, manifest.SourceRoot);

        return new ManifestVerdict(TargetState.Clean, Array.Empty<string>(), manifest.SourceRoot);
    }

    /// <summary>D4's normalisation: <c>OrdinalIgnoreCase</c> over the trimmed, fully-qualified path.</summary>
    private static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>Every file under <paramref name="dir"/>, recursively, as a forward-slash path relative
    /// to it — excluding the manifest's own file (pass 2 M15a).</summary>
    private static List<string> RelativePaths(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .Where(p => !string.Equals(p, FileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static List<string> ComputeDrift(ExportManifest manifest, string targetDir)
    {
        var diffs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actual = RelativePaths(targetDir);

        foreach (var (path, hash) in manifest.Files)
        {
            var full = Path.Combine(targetDir, path);
            if (!File.Exists(full)) { diffs.Add(path); continue; }   // missing
            if (!string.Equals(HashFile(full), hash, StringComparison.OrdinalIgnoreCase)) diffs.Add(path);   // edited
        }
        foreach (var path in actual)
            if (!manifest.Files.ContainsKey(path)) diffs.Add(path);   // added

        return diffs.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }
}
