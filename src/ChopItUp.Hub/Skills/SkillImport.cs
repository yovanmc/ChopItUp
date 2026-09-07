using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ChopItUp.Core.Storage;

namespace ChopItUp.Hub.Skills;

/// <summary>Why <see cref="SkillImport.Run"/> refused, or that it did not.</summary>
public enum SkillImportOutcome
{
    Ok,
    /// <summary>An invalid name, a frontmatter/directory mismatch, a size or count over its cap, a
    /// reparse point anywhere in the source, or the target already existing without <c>--force</c>.
    /// Maps to exit code 2.</summary>
    BadArgument,
    /// <summary>A rename-swap move failed even after the bounded retry (5c), most likely because a
    /// reader has a handle open on the installed skill, or another unexpected disk failure. Maps to
    /// exit code 3.</summary>
    IoFailure,
    /// <summary>The source directory does not exist, or holds no <c>SKILL.md</c> directly in it.
    /// Maps to exit code 4.</summary>
    SourceMissing,
}

/// <summary>What one <see cref="SkillImport.Run"/> call did. <c>Files</c> and <c>Name</c> are set only
/// on <see cref="SkillImportOutcome.Ok"/>.</summary>
public sealed record SkillImportResult(SkillImportOutcome Outcome, string Message, string? Name = null, int Files = 0);

/// <summary>The write side of the skill store (row 11 task 5): <c>--import-skill &lt;dir&gt;</c>
/// copies a folder holding a valid <c>SKILL.md</c> into <c>&lt;skillsRoot&gt;/&lt;name&gt;/</c> and
/// records a fingerprint of the installed <c>SKILL.md</c> in the <c>skills</c> table (D-i) — never
/// beside the skill itself, which the threat model says a spawn can write.
///
/// This is a RENAME SWAP, never delete-then-write (grill ledger M6): a reader running at the same
/// moment sees either the old skill or the new one, never a half-removed directory. Two invocations of
/// the same name, and a reader observing the swap mid-flight, are both closed by ONE mechanism: the
/// whole procedure — every refusal check and the write itself — runs inside the same named mutex
/// <see cref="SkillStore"/>'s <c>Read</c>/<c>List</c> take (grill ledger M-4), keyed on the skills
/// root directory.
///
/// The verb runs before any hub has ever started against this data directory (m6), so it creates the
/// skills root itself and, via <see cref="SkillHashes"/>, brings the database's <c>skills</c> table
/// into existence the same idempotent way a hub start does (<c>ChopDb.EnsureDatabase</c>) — it does
/// NOT check <c>HubLock</c> and does NOT read or rotate <c>tokens.json</c>: unlike <c>--rotate-token</c>
/// it needs no hub stopped (D-d — the store is read on demand, not cached).</summary>
public static class SkillImport
{
    // Same literal prefix/timeout SkillStore.Read/List use (M-4): the mutex name is a hash of the
    // path, so importing and reading the SAME skillsRoot always contend for the SAME mutex even
    // though the two classes share no field.
    private const string MutexPrefix = "Global\\ChopItUp.Skills.";
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(10);

    private const int MoveRetryAttempts = 5;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>Thrown by <see cref="MoveWithRetry"/> when a rename never stops failing. Caught only
    /// inside <see cref="RunCore"/>, which knows how to roll the swap back and which exit code this
    /// maps to — it never escapes <see cref="Run"/>.</summary>
    private sealed class SkillMoveException(string message) : Exception(message);

    public static SkillImportResult Run(string sourceDir, string skillsRoot, bool force, SkillHashes hashes) =>
        PathMutex.Run(MutexPrefix, skillsRoot, MutexTimeout, () => RunCore(sourceDir, skillsRoot, force, hashes));

    private static SkillImportResult RunCore(string sourceDir, string skillsRoot, bool force, SkillHashes hashes)
    {
        // m6: nothing before this point may assume a hub, or even this data directory, has ever
        // existed.
        Directory.CreateDirectory(skillsRoot);

        // Refusal 1: source exists.
        if (!Directory.Exists(sourceDir))
            return new SkillImportResult(SkillImportOutcome.SourceMissing, $"Source directory '{sourceDir}' does not exist.");

        // Refusal 2: name. Trimming first (m5) is what stops a trailing separator from turning
        // GetFileName into an empty string and refusing a perfectly good path as "invalid name".
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDir));
        if (!SkillStore.NamePattern.IsMatch(name))
            return new SkillImportResult(SkillImportOutcome.BadArgument,
                $"'{name}' is not a valid skill name: lowercase letters, digits and hyphens, starting with a letter or digit, at most 64 characters.");

        var target = Path.Combine(skillsRoot, name);
        var staging = Path.Combine(skillsRoot, name + ".importing");
        var replaced = Path.Combine(skillsRoot, name + ".replaced");

        // A previous run that died between the two moves of the write procedure leaves `.replaced`
        // as the ONLY surviving copy of the installed skill, with `<name>` absent. Restore it before
        // doing anything else — deleting it here (the first draft's bug, grill ledger M-4) would
        // destroy the very backup the swap exists to keep. Scoped to files/dirs whose name pattern
        // excludes a `.`, so `.importing`/`.replaced` are never themselves resolvable as a skill.
        if (Directory.Exists(replaced) && !Directory.Exists(target))
            Directory.Move(replaced, target);

        // Refusal 3: SKILL.md exists directly in the source.
        var sourceSkillMd = Path.Combine(sourceDir, "SKILL.md");
        if (!File.Exists(sourceSkillMd))
            return new SkillImportResult(SkillImportOutcome.SourceMissing, $"No SKILL.md directly in '{sourceDir}'.");

        var sourceBytes = File.ReadAllBytes(sourceSkillMd);
        var sourceText = new UTF8Encoding(false).GetString(sourceBytes);

        // Refusal 4: character cap.
        if (sourceText.Length > SkillStore.MaxSkillChars)
            return new SkillImportResult(SkillImportOutcome.BadArgument,
                $"SKILL.md is {sourceText.Length} characters; the cap is {SkillStore.MaxSkillChars}.");

        // Refusal 5: frontmatter name, when present, must agree with the directory. CRLF-normalised
        // first (claim 19) so a harness-authored CRLF SKILL.md is still read correctly.
        var frontmatterName = FrontmatterName(sourceText);
        if (frontmatterName is not null && !string.Equals(frontmatterName, name, StringComparison.Ordinal))
            return new SkillImportResult(SkillImportOutcome.BadArgument,
                $"SKILL.md's frontmatter name '{frontmatterName}' does not match the directory name '{name}'.");

        // Refusal 6: no reparse point anywhere in the tree. A `.git` directory at the source root is
        // skipped entirely rather than refused or copied — plenty of skill folders are git clones.
        var reparse = FindReparsePoint(sourceDir);
        if (reparse is not null)
            return new SkillImportResult(SkillImportOutcome.BadArgument,
                $"'{reparse}' is a link or junction; refusing to import a tree that contains one.");

        // Refusal 7: file/byte caps.
        var (files, totalBytes) = CountTree(sourceDir);
        if (files > SkillStore.MaxFiles)
            return new SkillImportResult(SkillImportOutcome.BadArgument, $"Source has {files} files; the cap is {SkillStore.MaxFiles}.");
        if (totalBytes > SkillStore.MaxBytes)
            return new SkillImportResult(SkillImportOutcome.BadArgument, $"Source is {totalBytes} bytes; the cap is {SkillStore.MaxBytes}.");

        // Refusal 8: target already exists.
        var targetExisted = Directory.Exists(target);
        if (targetExisted && !force)
            return new SkillImportResult(SkillImportOutcome.BadArgument, $"'{name}' already exists. Re-import with --force to replace it.");

        // Every refusal above returns before this line: nothing has been written yet.

        // Leftover staging can only be this mutex-holder's own debris — nobody else can be mid-import
        // of the same name while this mutex is held.
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

        var replacedTargetMoved = false;
        try
        {
            CopyTree(sourceDir, staging);

            if (targetExisted)
            {
                MoveWithRetry(target, replaced);
                replacedTargetMoved = true;
            }
            MoveWithRetry(staging, target);

            // Hash the bytes AS COPIED INTO THE TARGET, not the source bytes read above: what gets
            // fingerprinted is what a later Read will see. Recording AFTER the move means a crash
            // between them leaves an installed skill with a stale-or-absent hash, which reads back
            // as Tampered and refuses — the safe direction (D-i).
            var installedBytes = File.ReadAllBytes(Path.Combine(target, "SKILL.md"));
            var hash = Convert.ToHexString(SHA256.HashData(installedBytes)).ToLowerInvariant();
            hashes.Record(name, hash, sourceDir);

            if (replacedTargetMoved) Directory.Delete(replaced, recursive: true);

            return new SkillImportResult(SkillImportOutcome.Ok, $"Imported '{name}' ({files} file{(files == 1 ? "" : "s")}).", name, files);
        }
        catch (SkillMoveException e)
        {
            RollBack(target, replaced, staging, replacedTargetMoved);
            return new SkillImportResult(SkillImportOutcome.IoFailure, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            RollBack(target, replaced, staging, replacedTargetMoved);
            return new SkillImportResult(SkillImportOutcome.IoFailure, $"Could not import '{name}': {e.Message}");
        }
    }

    /// <summary>Step 4 of the write procedure: delete the staging directory (never the target — it
    /// may be exactly what a partially-completed rename left behind, and it is a copy, not the
    /// source), and if the FIRST move (target -&gt; replaced) succeeded but the second one did not,
    /// put the previously-installed skill back rather than stranding it under `.replaced` with
    /// nothing at `&lt;name&gt;` (grill ledger M-4).</summary>
    private static void RollBack(string target, string replaced, string staging, bool replacedTargetMoved)
    {
        if (replacedTargetMoved && !Directory.Exists(target) && Directory.Exists(replaced))
            Directory.Move(replaced, target);
        if (Directory.Exists(staging))
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort cleanup */ }
    }

    /// <summary>Both moves of the rename swap fail exactly when a reader is live (measured this
    /// session: <c>Directory.Move</c> throws <see cref="UnauthorizedAccessException"/> even under
    /// <c>FileShare.ReadWrite | FileShare.Delete</c>) — <see cref="SkillStore.Read"/> opens SKILL.md
    /// at every exchange root and <see cref="SkillStore.List"/> opens every skill's on every
    /// <c>GET /api/skills</c>, which the composer fetches on mount. Under a live hub the unretried
    /// swap fails in the ORDINARY case, so this must not report a bare I/O failure: on exhaustion it
    /// names a live reader as the likely cause.</summary>
    private static void MoveWithRetry(string from, string to)
    {
        for (var attempt = 1; attempt <= MoveRetryAttempts; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception e) when ((e is UnauthorizedAccessException or IOException) && attempt < MoveRetryAttempts)
            {
                Thread.Sleep(MoveRetryDelay);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                throw new SkillMoveException(
                    $"Could not move '{from}' to '{to}' after {MoveRetryAttempts} attempts, most likely because a live reader has a file inside it open. " +
                    $"Retry the import, or stop the hub and try again. ({e.Message})");
            }
        }
    }

    /// <summary>Normalises CRLF to LF first (claim 19: the harness skills are CRLF) so a frontmatter
    /// scan comparing a split line to <c>"---"</c> does not see <c>"---\r"</c> and miss it.</summary>
    private static string? FrontmatterName(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return null;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") break;
            var match = Regex.Match(lines[i], "^name:\\s*(.*)$");
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        return null;
    }

    /// <summary>True for the directory itself only when it IS <c>root/.git</c> — a nested
    /// <c>.git</c> deeper in the tree is not special-cased, only the source root's own.</summary>
    private static bool IsRootGitDir(string entryParentDir, string rootFull, string entryName) =>
        string.Equals(entryName, ".git", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.TrimEndingDirectorySeparator(entryParentDir), rootFull, StringComparison.OrdinalIgnoreCase);

    /// <summary>Walks every directory and file under <paramref name="root"/>, at any depth, looking
    /// for <see cref="FileAttributes.ReparsePoint"/> — a symlink or a junction, either one a spawn
    /// with shell access could point somewhere this store never intended to read from or write into.
    /// Stops descending into a reparse-point directory rather than following it. A `.git` directory
    /// at the source root is skipped, not scanned.</summary>
    private static string? FindReparsePoint(string root)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return Scan(rootFull);

        string? Scan(string dir)
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (IsRootGitDir(dir, rootFull, Path.GetFileName(sub))) continue;
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) return sub;
                var found = Scan(sub);
                if (found is not null) return found;
            }
            foreach (var file in Directory.EnumerateFiles(dir))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return file;
            return null;
        }
    }

    /// <summary>File count and total byte size of everything under <paramref name="root"/>, at any
    /// depth, excluding a `.git` directory at the root.</summary>
    private static (int Files, long Bytes) CountTree(string root)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var files = 0;
        long bytes = 0;
        Walk(rootFull);
        return (files, bytes);

        void Walk(string dir)
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (IsRootGitDir(dir, rootFull, Path.GetFileName(sub))) continue;
                Walk(sub);
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                files++;
                bytes += new FileInfo(file).Length;
            }
        }
    }

    /// <summary>Copies <paramref name="sourceRoot"/> into <paramref name="destRoot"/> (which must not
    /// already exist), skipping a `.git` directory at the source root exactly as
    /// <see cref="FindReparsePoint"/> and <see cref="CountTree"/> do.</summary>
    private static void CopyTree(string sourceRoot, string destRoot)
    {
        var sourceFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        Directory.CreateDirectory(destRoot);
        Copy(sourceFull, destRoot);

        void Copy(string srcDir, string destDir)
        {
            foreach (var sub in Directory.EnumerateDirectories(srcDir))
            {
                if (IsRootGitDir(srcDir, sourceFull, Path.GetFileName(sub))) continue;
                var destSub = Path.Combine(destDir, Path.GetFileName(sub));
                Directory.CreateDirectory(destSub);
                Copy(sub, destSub);
            }
            foreach (var file in Directory.EnumerateFiles(srcDir))
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        }
    }
}
