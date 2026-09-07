using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ChopItUp.Core.Storage;

namespace ChopItUp.Hub.Skills;

/// <summary>One gate a skill declares in its (fingerprinted) frontmatter (row 19, task 2d): a name
/// resolved to <c>data/skills/&lt;skill&gt;/scripts/&lt;gate&gt;.ps1</c> and the exact argument tokens
/// <c>run_gate</c> (task 12) passes and no others. The argument string lives inside the same
/// frontmatter bytes <see cref="SkillHashes"/> hashes, so it is exactly as tamper-protected as the
/// gate name itself.</summary>
public sealed record GateDeclaration(string Name, IReadOnlyList<string> Arguments);

/// <summary>What the prompt renders for one skill. No overlay member: D-j keeps OVERLAY.md out of
/// row 11 entirely, because an unpinned file rendered inside the pinned fence defeats the pin.
/// <see cref="IsRun"/> and <see cref="Gates"/> are row 19 (D1/D9): a skill whose frontmatter carries
/// <c>run: true</c> starts a run when invoked, and <c>Gates</c> is what <c>run_gate</c> may execute
/// inside one. Both default so every pre-row-19 construction site still compiles.</summary>
public sealed record ResolvedSkill(string Name, string Title, string Body, bool Truncated,
    bool IsRun = false, IReadOnlyList<GateDeclaration>? Gates = null);

/// <summary>One row of GET /api/skills and of the import verb's output. This is THE shape: tasks 6a,
/// 6's tests, 7a and ticket 06 all quote it verbatim and none of them invents a field. <c>Chars</c>
/// is the body length after frontmatter stripping. <see cref="IsRun"/> is row 19: the skill list the
/// UI shows says which skills start a run.</summary>
public sealed record SkillSummary(string Name, string Title, string Description, int Chars, bool IsRun = false);

/// <summary>What <see cref="SkillStore.Read"/> found.</summary>
public abstract record SkillRead
{
    public sealed record NotFound : SkillRead;
    public sealed record Tampered(string Name) : SkillRead;
    public sealed record Ok(ResolvedSkill Skill) : SkillRead;
}

/// <summary>What <see cref="SkillStore.VerifyTree"/> found (row 19, task 12b): re-hashes every entry
/// the manifest recorded at import against what is on disk NOW, and treats an unrecorded file present
/// on disk as tampering too — a spawn cannot smuggle a helper file past the manifest by adding one
/// the import never saw. <see cref="Missing"/> and <see cref="Tampered"/> both name the one path that
/// failed first (ordinal order), never every path at once — one bad path is proof enough to refuse.</summary>
public abstract record TreeVerification
{
    public sealed record Ok : TreeVerification;
    public sealed record Missing(string Path) : TreeVerification;
    public sealed record Tampered(string Path) : TreeVerification;
}

/// <summary>What <see cref="SkillStore.ReadGate"/> found for one gate name of one skill (row 19, task
/// 12b), checked independently of <see cref="TreeVerification"/> — a gate can be <see cref="Ok"/>
/// while the SKILL surrounding it is <see cref="TreeVerification.Tampered"/> because a NEIGHBOURING
/// file changed, which is exactly the case P5 exists to catch, so <c>run_gate</c> (task 12d) requires
/// both checks, never either alone.</summary>
public abstract record GateRead
{
    public sealed record NotDeclared : GateRead;
    public sealed record Missing : GateRead;
    public sealed record Tampered(string Path) : GateRead;
    public sealed record Ok(GateDeclaration Gate) : GateRead;
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
        using var tx = conn.BeginTransaction();
        using (var files = conn.CreateCommand())
        {
            files.Transaction = tx;
            files.CommandText = "DELETE FROM skill_files WHERE skill_name = $name";
            files.Parameters.AddWithValue("$name", name);
            files.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM skills WHERE name = $name";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Row 19, task 12a (P5): the whole-tree manifest — every file under a skill's installed
    /// directory, relative path (forward-slashed) to its SHA-256, replacing whatever was recorded
    /// before in one transaction so a forced re-import never leaves a stale entry for a file the new
    /// version dropped. Never beside the skill itself (D-i), same rationale as <see cref="Record"/>.</summary>
    public void RecordTree(string name, IReadOnlyDictionary<string, string> filesBySha256)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM skill_files WHERE skill_name = $name";
            del.Parameters.AddWithValue("$name", name);
            del.ExecuteNonQuery();
        }
        foreach (var (path, sha) in filesBySha256)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO skill_files (skill_name, path, sha256) VALUES ($name, $path, $sha)";
            ins.Parameters.AddWithValue("$name", name);
            ins.Parameters.AddWithValue("$path", path);
            ins.Parameters.AddWithValue("$sha", sha);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>The manifest <see cref="RecordTree"/> last wrote for <paramref name="name"/>: relative
    /// path to expected SHA-256. Empty when the skill has none recorded (a skill installed before row
    /// 19, or one never imported at all) — callers treat that as "nothing to verify against", never as
    /// "verified empty".</summary>
    public IReadOnlyDictionary<string, string> ExpectedTree(string name)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, sha256 FROM skill_files WHERE skill_name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = cmd.ExecuteReader();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) map[reader.GetString(0)] = reader.GetString(1);
        return map;
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
                        summaries.Add(new SkillSummary(name, ok.Skill.Title, description ?? "", ok.Skill.Body.Length, ok.Skill.IsRun));
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
        var (body, title, desc, isRun, gates) = StripFrontmatter(text, name);
        description = desc;
        var (cut, truncated) = Cut(body, MaxSkillChars);
        return new SkillRead.Ok(new ResolvedSkill(name, title, cut, truncated, isRun, gates));
    }

    /// <summary>Row 19, task 12b (P5): re-hashes every entry <see cref="SkillHashes.RecordTree"/>
    /// wrote at import against what is on disk right now, then checks for a file present on disk that
    /// the manifest never recorded — closing the hash-then-run window <c>run_gate</c> (task 12d) exists
    /// to close. A skill with no manifest at all (imported before row 19, or never imported) reads as
    /// <see cref="TreeVerification.Missing"/> naming the skill itself: there is nothing to verify
    /// against, and "nothing recorded" must never read as "verified clean".</summary>
    public TreeVerification VerifyTree(string name) =>
        PathMutex.Run(MutexPrefix, Root, MutexTimeout, () => VerifyTreeCore(name));

    private TreeVerification VerifyTreeCore(string name)
    {
        var dir = Path.Combine(Root, name);
        var expected = hashes.ExpectedTree(name);
        if (expected.Count == 0) return new TreeVerification.Missing(name);
        foreach (var path in expected.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            var full = Path.Combine(dir, path);
            if (!File.Exists(full)) return new TreeVerification.Missing(path);
            var actual = Convert.ToHexString(SHA256.HashData(ReadAllBytes(full))).ToLowerInvariant();
            if (!string.Equals(actual, expected[path], StringComparison.Ordinal)) return new TreeVerification.Tampered(path);
        }
        var extra = WalkFiles(dir).Except(expected.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
        return extra is null ? new TreeVerification.Ok() : new TreeVerification.Tampered(extra);
    }

    /// <summary>Row 19, task 12b: is <paramref name="gate"/> one <paramref name="name"/> declares, and
    /// does its script match the manifest? Checked independently of <see cref="VerifyTree"/> — a gate
    /// can read <see cref="GateRead.Ok"/> while the tree overall is tampered by a NEIGHBOURING file,
    /// which is exactly why <c>run_gate</c> requires both, never either alone.</summary>
    public GateRead ReadGate(string name, string gate) =>
        PathMutex.Run(MutexPrefix, Root, MutexTimeout, () => ReadGateCore(name, gate));

    private GateRead ReadGateCore(string name, string gate)
    {
        if (ReadCore(name, out _) is not SkillRead.Ok ok) return new GateRead.Missing();
        var declaration = ok.Skill.Gates?.FirstOrDefault(g => g.Name == gate);
        if (declaration is null) return new GateRead.NotDeclared();

        var relative = $"scripts/{gate}.ps1";
        var expected = hashes.ExpectedTree(name);
        if (!expected.TryGetValue(relative, out var sha)) return new GateRead.Missing();
        var full = Path.Combine(Root, name, "scripts", gate + ".ps1");
        if (!File.Exists(full)) return new GateRead.Missing();
        var actual = Convert.ToHexString(SHA256.HashData(ReadAllBytes(full))).ToLowerInvariant();
        return string.Equals(actual, sha, StringComparison.Ordinal) ? new GateRead.Ok(declaration) : new GateRead.Tampered(relative);
    }

    /// <summary>Every file under <paramref name="root"/>, at any depth, as a forward-slashed path
    /// relative to it — the same shape <see cref="SkillHashes.RecordTree"/> stores, so the two can be
    /// compared directly.</summary>
    private static IEnumerable<string> WalkFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            yield return Path.GetRelativePath(root, file).Replace('\\', '/');
    }

    /// <summary>Row 19: a comma-separated list of gate names, each optionally followed by
    /// <c>(&lt;args&gt;)</c> — e.g. <c>gates: budget(--RoadmapPath ROADMAP.md), count-files</c>. A
    /// malformed entry is dropped, never thrown on (<see cref="ChopItUp.Core.Model.ParticipantClasses.Parse"/>'s
    /// rule): one bad cell in a skill's frontmatter must not make the whole skill unreadable.</summary>
    private static readonly Regex GateEntryPattern = new(
        @"^(?<name>[a-z0-9][a-z0-9-]{0,31})(?:\((?<args>[^()]*)\))?$", RegexOptions.Compiled);

    private static IReadOnlyList<GateDeclaration> ParseGates(string value)
    {
        var gates = new List<GateDeclaration>();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = GateEntryPattern.Match(raw.Trim());
            if (!match.Success) continue;
            var argsText = match.Groups["args"].Success ? match.Groups["args"].Value.Trim() : "";
            IReadOnlyList<string> arguments = argsText.Length == 0
                ? []
                : argsText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            gates.Add(new GateDeclaration(match.Groups["name"].Value, arguments));
        }
        return gates;
    }

    /// <summary>When the normalised first line is exactly `---`, everything through the next line
    /// that is exactly `---` is dropped from the body and `name:`/`description:`/`run:`/`gates:` are
    /// read out of it. Title is frontmatter `name`, else the first `# ` heading, else the directory
    /// name. Description is frontmatter `description` trimmed to 300 characters, else the first
    /// non-blank non-heading line, else empty. <c>run:</c> is true only for the literal (case
    /// insensitive) value `true`; <c>gates:</c> is parsed by <see cref="ParseGates"/>. Internal (row
    /// 19, task 12a): <c>SkillImport</c> calls this on the SOURCE text, before anything is written, to
    /// refuse a skill that declares a gate whose script is not in the import — the same parser Read
    /// uses at every later call, so import-time and read-time can never disagree on what a skill
    /// declares.</summary>
    internal static (string Body, string Title, string Description, bool IsRun, IReadOnlyList<GateDeclaration> Gates) StripFrontmatter(string text, string dirName)
    {
        var lines = text.Split('\n');
        string? frontmatterName = null;
        string? frontmatterDescription = null;
        var isRun = false;
        IReadOnlyList<GateDeclaration> gates = [];
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
                    var runMatch = Regex.Match(lines[i], "^run:\\s*(.*)$");
                    if (runMatch.Success) isRun = string.Equals(runMatch.Groups[1].Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                    var gatesMatch = Regex.Match(lines[i], "^gates:\\s*(.*)$");
                    if (gatesMatch.Success) gates = ParseGates(gatesMatch.Groups[1].Value);
                }
                bodyStart = end + 1;
            }
        }

        var body = string.Join('\n', lines.Skip(bodyStart)).TrimStart('\n');
        var title = frontmatterName ?? FirstHeading(body) ?? dirName;
        var description = frontmatterDescription is { Length: > 300 } d
            ? d[..300]
            : frontmatterDescription ?? FirstNonBlankNonHeadingLine(body) ?? "";
        return (body, title, description, isRun, gates);
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
