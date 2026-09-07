using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ChopItUp.Core.Storage;

namespace ChopItUp.Hub.Skills;

/// <summary>What the prompt renders for one skill. No overlay member: D-j keeps OVERLAY.md out of
/// row 11 entirely, because an unpinned file rendered inside the pinned fence defeats the pin.</summary>
public sealed record ResolvedSkill(string Name, string Title, string Body, bool Truncated);

/// <summary>One row of GET /api/skills and of the import verb's output. This is THE shape: tasks 6a,
/// 6's tests, 7a and ticket 06 all quote it verbatim and none of them invents a field. <c>Chars</c>
/// is the body length after frontmatter stripping.</summary>
public sealed record SkillSummary(string Name, string Title, string Description, int Chars);

/// <summary>What <see cref="SkillStore.Read"/> found.</summary>
public abstract record SkillRead
{
    public sealed record NotFound : SkillRead;
    public sealed record Tampered(string Name) : SkillRead;
    public sealed record Ok(ResolvedSkill Skill) : SkillRead;
}

/// <summary>The <c>skills</c> table (schema v7): one row per imported skill, recording the SHA-256 of
/// its <c>SKILL.md</c> at import time. Kept off the surface a spawn can write (grill ledger D-i) — a
/// manifest file sitting beside the skill it guards would be exactly as writable as the skill itself.
/// Same collaborator shape as <c>MemoryProposalStore</c>: a thin wrapper the store composes with.</summary>
public sealed class SkillHashes(ChopDb db)
{
    public string? Expected(string name)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT body_sha256 FROM skills WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() as string;
    }

    public void Record(string name, string sha256, string? source)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO skills (name, body_sha256, imported_at, source) VALUES ($name, $sha, $at, $source)
            ON CONFLICT(name) DO UPDATE SET body_sha256 = excluded.body_sha256, imported_at = excluded.imported_at, source = excluded.source
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$sha", sha256);
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Forget(string name)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM skills WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>The hub's skill store: `<data>/skills/<name>/SKILL.md`, plus whatever references and
/// scripts the skill brought with it (grill ledger D7), which row 11 copies but never reads. Read on
/// demand and never cached: a skill is a document, not a credential, and an import must take effect
/// without restarting the hub. Nothing here writes — the import verb does.
///
/// Row 11 renders SKILL.md into the prompt and nothing else — not OVERLAY.md (D-j: an unpinned file
/// inside the pinned fence defeats the pin), not the references: no spawn is told it may read them,
/// and reaching them is `run_gate`, which is row 19.</summary>
public sealed class SkillStore(string root, SkillHashes hashes)
{
    /// <summary>Sized in the plan (D-f) against the roadmap skill row 20 must carry — 20,161
    /// characters today, on a file the owner edits. The worst-case prompt is this plus the transcript
    /// window (24,000) plus the memory core (6,000).</summary>
    public const int MaxSkillChars = 32_000;

    /// <summary>A FILE-length refusal, checked before a byte is read, and not the same thing as
    /// <see cref="MaxSkillChars"/>: the character cap is enforced by the import verb, but this class
    /// reads a directory that the threat model (D-i) says something else may have written to, on the
    /// spawner's single event-loop thread. Without this, one huge file stalls every room's exchange
    /// handling and the owner's stop button behind a read and a hash.</summary>
    public const long MaxSkillFileBytes = 1L * 1024 * 1024;

    public const int MaxFiles = 200;
    public const long MaxBytes = 2L * 1024 * 1024;

    public static readonly Regex NamePattern = new(@"^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

    private const string MutexPrefix = "Global\\ChopItUp.Skills.";
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Test seam mirroring <c>ChopDb.BackupDestinationFactory</c>: lets a test make the read
    /// itself fail (M-8), and lets the oversized-file test assert the file was never opened.</summary>
    internal Func<string, byte[]> ReadAllBytes { get; set; } = File.ReadAllBytes;

    // TrimEndingDirectorySeparator: Path.GetFullPath preserves a trailing separator when the caller
    // passed one, and ReadCore's containment check compares against `Root + DirectorySeparatorChar`
    // - with an untrimmed Root that becomes a doubled separator a normalised child path never starts
    // with, so every skill would read NotFound. Fail-closed today (no caller passes a trailing
    // separator), but cheap to close outright while this class is being constructed here.
    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public void EnsureLayout() => Directory.CreateDirectory(Root);

    /// <summary>Every directory whose name is a valid slug and that holds a readable SKILL.md, sorted
    /// ordinally. A directory that fails either test is skipped, never thrown on: a stray folder the
    /// owner dropped in must not break every exchange in the hub. Applies
    /// <see cref="MaxSkillFileBytes"/> too — this is what <c>GET /api/skills</c> calls, and the
    /// composer fetches that on mount. A skipped entry is never silently invisible: one line per skip
    /// goes to the operator log naming the reason.</summary>
    public IReadOnlyList<SkillSummary> List() =>
        PathMutex.Run(MutexPrefix, Root, MutexTimeout, () =>
        {
            var summaries = new List<SkillSummary>();
            if (!Directory.Exists(Root)) return summaries;
            foreach (var dir in Directory.EnumerateDirectories(Root).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
                if (!NamePattern.IsMatch(name))
                {
                    Console.Error.WriteLine($"Skipping skill directory '{name}': not a valid skill name.");
                    continue;
                }
                switch (ReadCore(name, out var description))
                {
                    case SkillRead.Ok ok:
                        summaries.Add(new SkillSummary(name, ok.Skill.Title, description ?? "", ok.Skill.Body.Length));
                        break;
                    case SkillRead.NotFound:
                        Console.Error.WriteLine($"Skipping skill '{name}': no readable SKILL.md.");
                        break;
                    case SkillRead.Tampered:
                        Console.Error.WriteLine($"Skipping skill '{name}': does not match the fingerprint recorded at import.");
                        break;
                }
            }
            return summaries;
        });

    /// <summary>Three outcomes, not two: the skill; <c>NotFound</c> when nothing readable is there;
    /// or <c>Tampered</c> when SKILL.md does not match the hash the <c>skills</c> table recorded at
    /// import, has no row there, or exceeds <see cref="MaxSkillFileBytes"/> (D-i). Tampered is NOT
    /// treated as missing — a skill whose text changed under the hub is a different and worse event
    /// than one that was never installed, and the room is told which.</summary>
    public SkillRead Read(string name) =>
        PathMutex.Run(MutexPrefix, Root, MutexTimeout, () => ReadCore(name, out _));

    private SkillRead ReadCore(string name, out string? description)
    {
        description = null;

        // Name first, filesystem second. The containment re-check is belt and braces: NamePattern
        // already forbids a separator or a dot, so a traversal cannot be spelled - but this class is
        // the only thing between a message body and a file path, and it costs one comparison.
        if (!NamePattern.IsMatch(name)) return new SkillRead.NotFound();
        var dir = Path.GetFullPath(Path.Combine(Root, name));
        if (!dir.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return new SkillRead.NotFound();
        var path = Path.Combine(dir, "SKILL.md");
        var file = new FileInfo(path);
        if (!file.Exists) return new SkillRead.NotFound();

        // Length BEFORE content (D-f/M-7): this runs on the spawner's single event loop, and the
        // store is writable by the thing D-i guards against. Oversized is Tampered, not NotFound -
        // the owner imported something that fitted, so what is on disk now is not what they installed.
        if (file.Length > MaxSkillFileBytes) return new SkillRead.Tampered(name);

        var bytes = ReadAllBytes(path);
        // Hash the RAW bytes, before normalisation and before frontmatter stripping. Hashing the
        // parsed text would make the fingerprint depend on the parser and let a line-ending rewrite
        // through; it would also mean a parser change silently invalidates every installed skill.
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var expected = hashes.Expected(name);
        if (expected is null || !string.Equals(actual, expected, StringComparison.Ordinal))
            return new SkillRead.Tampered(name);

        // CRLF (claim 19): the harness skills are CRLF, so a frontmatter scan that compares a split
        // line to "---" sees "---\r", strips nothing, and renders the YAML header as instruction.
        var text = new UTF8Encoding(false).GetString(bytes).Replace("\r\n", "\n");
        var (body, title, desc) = StripFrontmatter(text, name);
        description = desc;
        var (cut, truncated) = Cut(body, MaxSkillChars);
        return new SkillRead.Ok(new ResolvedSkill(name, title, cut, truncated));
    }

    /// <summary>When the normalised first line is exactly `---`, everything through the next line
    /// that is exactly `---` is dropped from the body and `name:`/`description:` are read out of it.
    /// Title is frontmatter `name`, else the first `# ` heading, else the directory name. Description
    /// is frontmatter `description` trimmed to 300 characters, else the first non-blank non-heading
    /// line, else empty.</summary>
    private static (string Body, string Title, string Description) StripFrontmatter(string text, string dirName)
    {
        var lines = text.Split('\n');
        string? frontmatterName = null;
        string? frontmatterDescription = null;
        var bodyStart = 0;

        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var end = -1;
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() != "---") continue;
                end = i;
                break;
            }
            if (end >= 0)
            {
                for (var i = 1; i < end; i++)
                {
                    var nameMatch = Regex.Match(lines[i], "^name:\\s*(.*)$");
                    if (nameMatch.Success) frontmatterName = nameMatch.Groups[1].Value.Trim();
                    var descMatch = Regex.Match(lines[i], "^description:\\s*(.*)$");
                    if (descMatch.Success) frontmatterDescription = descMatch.Groups[1].Value.Trim();
                }
                bodyStart = end + 1;
            }
        }

        var body = string.Join('\n', lines.Skip(bodyStart)).TrimStart('\n');
        var title = frontmatterName ?? FirstHeading(body) ?? dirName;
        var description = frontmatterDescription is { Length: > 300 } d
            ? d[..300]
            : frontmatterDescription ?? FirstNonBlankNonHeadingLine(body) ?? "";
        return (body, title, description);
    }

    private static string? FirstHeading(string body)
    {
        foreach (var line in body.Split('\n'))
            if (line.StartsWith("# ", StringComparison.Ordinal))
                return line[2..].Trim();
        return null;
    }

    private static string? FirstNonBlankNonHeadingLine(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            return trimmed;
        }
        return null;
    }

    /// <summary>Mirrors <c>MemoryStore.Cut</c>: never splits a surrogate pair, returns the flag.</summary>
    private static (string Text, bool Truncated) Cut(string text, int max)
    {
        if (text.Length <= max) return (text, false);
        var cut = max;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return (text[..cut], true);
    }
}
