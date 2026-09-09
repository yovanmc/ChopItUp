using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ChopItUp.Core.Skills;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Rooms;

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

/// <summary>What one <see cref="SkillImport.Run"/> or <see cref="SkillImport.Validate"/> call found.
/// <c>Name</c>, <c>Files</c>, <c>Bytes</c> and <c>ReplacesInstalled</c> are set only on
/// <see cref="SkillImportOutcome.Ok"/> (task 1: <c>Bytes</c> and <c>ReplacesInstalled</c> are new —
/// task 5 records all three into a skill proposal).</summary>
public sealed record SkillImportResult(SkillImportOutcome Outcome, string Message, string? Name = null, int Files = 0, long Bytes = 0, bool ReplacesInstalled = false);

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

    public static SkillImportResult Run(string sourceDir, string skillsRoot, bool force, SkillHashes hashes, string? overlayDir = null) =>
        PathMutex.Run(MutexPrefix, skillsRoot, MutexTimeout, () => RunCore(sourceDir, skillsRoot, force, hashes, overlayDir));

    /// <summary>Task 1: the whole refusal battery (refusals 1-9), with no side effect — nothing under
    /// <paramref name="skillsRoot"/> changes and no database row is written, whether or not the skills
    /// root has ever existed. Takes the SAME named mutex <see cref="Run"/> does, with the same 10 s
    /// timeout (the locking contract, pass 2), so a caller asking while another import is mid-swap
    /// never gets a half-true answer; on contention it returns <see cref="SkillImportOutcome.IoFailure"/>
    /// rather than throwing, with a message the MCP caller can act on. Unlike <see cref="Run"/>, this
    /// never restores a torn `.replaced` — it judges the store exactly as it finds it, treating a
    /// present `&lt;name&gt;.replaced` with an absent `&lt;name&gt;` as installed (pass 1 finding), so
    /// <see cref="Validate"/> and <see cref="Run"/> can never disagree about whether a skill is
    /// installed.</summary>
    public static SkillImportResult Validate(string sourceDir, string skillsRoot, bool force, string? overlayDir = null)
    {
        try
        {
            return PathMutex.Run(MutexPrefix, skillsRoot, MutexTimeout, () => ValidateCore(sourceDir, skillsRoot, force, overlayDir, enforceReviewAllowlist: true));
        }
        catch (TimeoutException)
        {
            return new SkillImportResult(SkillImportOutcome.IoFailure, "the skill store is busy; try again");
        }
    }

    private static SkillImportResult RunCore(string sourceDir, string skillsRoot, bool force, SkillHashes hashes, string? overlayDir)
    {
        // m6: nothing before this point may assume a hub, or even this data directory, has ever
        // existed.
        Directory.CreateDirectory(skillsRoot);

        // A previous run that died between the two moves of the write procedure leaves `.replaced`
        // as the ONLY surviving copy of the installed skill, with `<name>` absent. Restore it before
        // Validate looks at the store, so Validate and the write path never disagree about whether
        // `<name>` is installed (pass 1 finding) — deleting it here (the first draft's bug, grill
        // ledger M-4) would destroy the very backup the swap exists to keep. A garbage
        // (refusal-2-failing) name simply has no `.replaced` to restore, so this is harmless to run
        // unconditionally, before the name is even validated.
        if (Directory.Exists(sourceDir))
        {
            var candidateName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDir));
            var candidateTarget = Path.Combine(skillsRoot, candidateName);
            var candidateReplaced = Path.Combine(skillsRoot, candidateName + ".replaced");
            if (Directory.Exists(candidateReplaced) && !Directory.Exists(candidateTarget))
                Directory.Move(candidateReplaced, candidateTarget);
        }

        // D7's reviewable-extension allowlist (refusal 6b) is a propose-time refusal, not an
        // import-time one (plan lines 172-183): "Skills needing a binary stay on the CLI path, where
        // the owner is already at the keyboard." RunCore is the CLI path, so it asks ValidateCore for
        // everything else but leaves that one refusal off; Validate (the propose path, task 5) turns
        // it on. The root/ancestor link check (refusal 1b) is a security refusal and is NOT gated -
        // it always runs, inside ValidateCore, regardless of this flag.
        var validation = ValidateCore(sourceDir, skillsRoot, force, overlayDir, enforceReviewAllowlist: false);
        if (validation.Outcome != SkillImportOutcome.Ok)
            return validation;

        var name = validation.Name!;
        var target = Path.Combine(skillsRoot, name);
        var staging = Path.Combine(skillsRoot, name + ".importing");
        var replaced = Path.Combine(skillsRoot, name + ".replaced");
        var targetExisted = validation.ReplacesInstalled;
        var files = validation.Files;
        var totalBytes = validation.Bytes;
        // Recomputed, not re-validated: Validate already confirmed the overlay (when present) is
        // well formed, so these are just the paths the write step composes from.
        var overlayMdPath = overlayDir is not null ? Path.Combine(overlayDir, SkillStore.OverlayFileName) : null;
        var overlayScriptsDir = overlayDir is not null ? Path.Combine(overlayDir, SkillStore.ScriptsDirName) : null;

        // Leftover staging can only be this mutex-holder's own debris — nobody else can be mid-import
        // of the same name while this mutex is held.
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

        var replacedTargetMoved = false;
        try
        {
            CopyTree(sourceDir, staging);

            if (overlayMdPath is not null)
            {
                File.Copy(overlayMdPath, Path.Combine(staging, SkillStore.OverlayFileName));
                if (overlayScriptsDir is not null && Directory.Exists(overlayScriptsDir))
                {
                    var stagingScripts = Path.Combine(staging, SkillStore.ScriptsDirName);
                    Directory.CreateDirectory(stagingScripts);
                    foreach (var file in Directory.EnumerateFiles(overlayScriptsDir))
                        File.Copy(file, Path.Combine(stagingScripts, Path.GetFileName(file)));
                }
            }

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

            // Row 19, task 12a (P5): the WHOLE tree, hashed as installed (same "after the move"
            // rule as the SKILL.md hash above) — a per-script hash alone would let a spawn rewrite a
            // sibling data file a gate reads (the roadmap gate's baselines.json is exactly this) and
            // make the gate pass a violating result. Replaces whatever was recorded before in one
            // transaction (RecordTree), so a forced re-import never leaves a stale entry for a file
            // the new version dropped.
            hashes.RecordTree(name, HashTree(target));

            if (replacedTargetMoved) Directory.Delete(replaced, recursive: true);

            return new SkillImportResult(SkillImportOutcome.Ok,
                $"Imported '{name}' ({files} file{(files == 1 ? "" : "s")}, overlay: {(overlayDir is null ? "no" : "yes")}).", name, files);
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

    /// <summary>Task 1: refusals 1-9 (plus, when <paramref name="enforceReviewAllowlist"/> is set,
    /// D7's extension allowlist and per-file character cap — and, unconditionally, the root/ancestor
    /// link check), with no side effect. Assumes nothing about <paramref name="skillsRoot"/> having
    /// ever existed, and never restores a torn `.replaced` — <paramref name="skillsRoot"/> may not
    /// even exist on disk. Callers hold the store mutex already (<see cref="Validate"/> takes it
    /// itself; <see cref="RunCore"/> is already inside <see cref="Run"/>'s).
    ///
    /// <paramref name="enforceReviewAllowlist"/>: D7 is a propose-time refusal, not an import-time
    /// one (plan lines 172-183) — a skill needing a binary stays on the CLI path, where the owner is
    /// already at the keyboard. <see cref="Validate"/> (the propose path, task 5) passes true;
    /// <see cref="RunCore"/> (the CLI path) passes false. The root/ancestor link check (refusal 1b) is
    /// a security refusal, not a disclosure one, and is NOT gated by this flag — it runs for both
    /// paths.</summary>
    private static SkillImportResult ValidateCore(string sourceDir, string skillsRoot, bool force, string? overlayDir, bool enforceReviewAllowlist)
    {
        // Refusal 1: source exists.
        if (!Directory.Exists(sourceDir))
            return new SkillImportResult(SkillImportOutcome.SourceMissing, $"Source directory '{sourceDir}' does not exist.");

        // Refusal 1b (task 1, pass 1 finding 3): the source itself, or any existing ancestor segment
        // of its path, resolves through a link or junction. FindReparsePoint below only walks the
        // CHILDREN of the source, so a junction NAMED <slug> would otherwise pass unnoticed. Reuses
        // RoomPaths' own per-segment resolver (RoomPaths.cs:84-118) rather than a second
        // implementation.
        var sourceFull = RoomPaths.Normalize(sourceDir);
        var resolvedSource = RoomPaths.ResolveLinks(sourceFull);
        if (!RoomPaths.Same(resolvedSource, sourceFull))
            return new SkillImportResult(SkillImportOutcome.BadArgument,
                $"'{sourceFull}' is a link or junction; refusing to import a tree that contains one.");

        // Refusal 2: name. Trimming first (m5) is what stops a trailing separator from turning
        // GetFileName into an empty string and refusing a perfectly good path as "invalid name".
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDir));
        if (!SkillStore.NamePattern.IsMatch(name))
            return new SkillImportResult(SkillImportOutcome.BadArgument,
                $"'{name}' is not a valid skill name: lowercase letters, digits and hyphens, starting with a letter or digit, at most 64 characters.");

        // Refusal 2b (row 19, task 13): the reserved `/stop` command cannot be shadowed by an
        // installed skill (ticket 13). Checked on the name alone, before anything about the
        // frontmatter is even read, so a directory named "stop" is refused for THIS reason
        // regardless of what its SKILL.md claims.
        if (string.Equals(name, RunCommands.StopName, StringComparison.Ordinal))
            return new SkillImportResult(SkillImportOutcome.BadArgument,
                $"'{name}' is a reserved name (the run stop command) and cannot be installed as a skill.");

        var target = Path.Combine(skillsRoot, name);
        var replaced = Path.Combine(skillsRoot, name + ".replaced");

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

        // Refusal 6b (task 1, D7): every file in the source tree must be reviewable text a card can
        // show in full — an extension outside the allowlist (matched case-insensitively; a file with
        // no extension counts as outside it) is refused by name, and so is a file over
        // SkillStore.MaxSkillChars, so an oversized file is refused HERE rather than minting a
        // proposal the owner's card could never show in full (pass 2 finding 4). Propose-time only
        // (enforceReviewAllowlist) — the CLI path (RunCore) leaves binaries alone; D7 disclosure is
        // for the room card, not the keyboard the owner is already at (plan lines 172-183).
        if (enforceReviewAllowlist)
        {
            var reviewIssue = FindReviewIssue(sourceDir);
            if (reviewIssue is not null)
                return new SkillImportResult(SkillImportOutcome.BadArgument, reviewIssue);
        }

        // Refusal 7b (task 1): an overlay is hub-side and travels only through --overlay; a source
        // that carries its own OVERLAY.md would let a third-party skill claim overlay standing for
        // itself.
        if (File.Exists(Path.Combine(sourceDir, SkillStore.OverlayFileName)))
            return new SkillImportResult(SkillImportOutcome.BadArgument,
                $"'{name}' source carries OVERLAY.md; an overlay is hub-side and travels only through --overlay.");

        // Refusal 7c (task 1): the overlay directory itself, validated before anything is staged —
        // it must exist, hold OVERLAY.md under the character cap, and contain nothing else besides
        // scripts/*.ps1 (no other files, no subdirectories beyond scripts, no reparse points, and no
        // script name that collides with one the source already ships).
        string? overlayMdPath = null;
        string? overlayScriptsDir = null;
        if (overlayDir is not null)
        {
            if (!Directory.Exists(overlayDir))
                return new SkillImportResult(SkillImportOutcome.BadArgument, $"Overlay directory '{overlayDir}' does not exist.");

            var overlayReparse = FindReparsePoint(overlayDir);
            if (overlayReparse is not null)
                return new SkillImportResult(SkillImportOutcome.BadArgument,
                    $"'{overlayReparse}' is a link or junction; refusing to import a tree that contains one.");

            overlayMdPath = Path.Combine(overlayDir, SkillStore.OverlayFileName);
            if (!File.Exists(overlayMdPath))
                return new SkillImportResult(SkillImportOutcome.BadArgument, $"Overlay directory '{overlayDir}' has no OVERLAY.md.");
            var overlayMdChars = new UTF8Encoding(false).GetString(File.ReadAllBytes(overlayMdPath)).Length;
            if (overlayMdChars > SkillStore.MaxOverlayChars)
                return new SkillImportResult(SkillImportOutcome.BadArgument,
                    $"OVERLAY.md is {overlayMdChars} characters; the cap is {SkillStore.MaxOverlayChars}.");

            overlayScriptsDir = Path.Combine(overlayDir, SkillStore.ScriptsDirName);
            foreach (var entry in Directory.EnumerateFileSystemEntries(overlayDir))
            {
                var entryName = Path.GetFileName(entry);
                if (string.Equals(entryName, SkillStore.OverlayFileName, StringComparison.Ordinal)) continue;
                if (string.Equals(entryName, SkillStore.ScriptsDirName, StringComparison.Ordinal) && Directory.Exists(entry)) continue;
                return new SkillImportResult(SkillImportOutcome.BadArgument,
                    $"'{entry}' is not allowed in the overlay directory; it may hold only OVERLAY.md and scripts/*.ps1.");
            }
            if (Directory.Exists(overlayScriptsDir))
            {
                foreach (var sub in Directory.EnumerateDirectories(overlayScriptsDir))
                    return new SkillImportResult(SkillImportOutcome.BadArgument,
                        $"'{sub}' is not allowed in the overlay's scripts directory; only *.ps1 files are.");
                foreach (var file in Directory.EnumerateFiles(overlayScriptsDir))
                {
                    if (!file.EndsWith(".ps1", StringComparison.Ordinal))
                        return new SkillImportResult(SkillImportOutcome.BadArgument,
                            $"'{file}' is not allowed in the overlay's scripts directory; only *.ps1 files are.");
                    var scriptBase = Path.GetFileNameWithoutExtension(file);
                    if (File.Exists(Path.Combine(sourceDir, "scripts", scriptBase + ".ps1")))
                        return new SkillImportResult(SkillImportOutcome.BadArgument,
                            $"'{scriptBase}.ps1' exists in both the source and the overlay.");
                }
            }
        }

        // Refusal 7 (moved here so it runs after 7c validates the overlay): file/byte caps over the
        // COMPOSED tree, not just the source. The overlay's OVERLAY.md and scripts/*.ps1 are copied
        // into the installed skill alongside the source (task 1's composition step) and pinned as one
        // manifest, so they count toward the same caps the source alone used to be checked against.
        var (sourceTreeFiles, sourceTreeBytes) = CountTree(sourceDir);
        var (overlayFileCount, overlayByteCount) = overlayDir is not null ? CountTree(overlayDir) : (0, 0L);
        var files = sourceTreeFiles + overlayFileCount;
        var totalBytes = sourceTreeBytes + overlayByteCount;
        if (files > SkillStore.MaxFiles)
            return new SkillImportResult(SkillImportOutcome.BadArgument, $"Source has {files} files; the cap is {SkillStore.MaxFiles}.");
        if (totalBytes > SkillStore.MaxBytes)
            return new SkillImportResult(SkillImportOutcome.BadArgument, $"Source is {totalBytes} bytes; the cap is {SkillStore.MaxBytes}.");

        // Refusal 8: target already exists — read-only (never restores `.replaced`), so a present
        // `<name>.replaced` with an absent `<name>` counts as installed exactly as it will once
        // RunCore's restore runs (pass 1 finding): the two can never disagree.
        var existingDir = Directory.Exists(target) ? target : (Directory.Exists(replaced) ? replaced : null);
        var installedExists = existingDir is not null;
        if (installedExists && !force)
            return new SkillImportResult(SkillImportOutcome.BadArgument, $"'{name}' already exists. Re-import with --force to replace it.");

        // Refusal 8b (task 1): a forced re-import that would silently drop or rewrite an installed
        // overlay is refused — the import never touches an overlay on its own; the caller must repeat
        // it with --overlay to keep it.
        if (installedExists && force && overlayDir is null && existingDir is not null && File.Exists(Path.Combine(existingDir, SkillStore.OverlayFileName)))
            return new SkillImportResult(SkillImportOutcome.BadArgument,
                $"'{name}' carries an overlay; re-import with --overlay <dir> to keep it. The import never rewrites or drops an overlay on its own.");

        // Refusal 9 (row 19, task 12a; extended task 1): a declared gate whose script the import does
        // not ship is a hard failure, checked against the SOURCE (nothing has moved yet) with the SAME
        // frontmatter parser SkillStore.Read uses at every later call (SkillStore.StripFrontmatter,
        // made internal for exactly this) — import-time and read-time can never disagree on what a
        // skill declares. The check now runs over the UNION of SKILL.md's gates and OVERLAY.md's gates
        // against the union of the source's scripts/ and the overlay's scripts/; a name declared by
        // both frontmatters is refused before either script is even looked for.
        var (_, _, _, _, sourceGates) = SkillStore.StripFrontmatter(sourceText.Replace("\r\n", "\n"), name);
        IReadOnlyList<GateDeclaration> overlayGates = [];
        if (overlayMdPath is not null)
        {
            var overlayText = new UTF8Encoding(false).GetString(File.ReadAllBytes(overlayMdPath)).Replace("\r\n", "\n");
            (_, _, _, _, overlayGates) = SkillStore.StripFrontmatter(overlayText, name);
        }
        var duplicateGate = sourceGates.Select(g => g.Name)
            .Intersect(overlayGates.Select(g => g.Name), StringComparer.Ordinal)
            .FirstOrDefault();
        if (duplicateGate is not null)
            return new SkillImportResult(SkillImportOutcome.BadArgument,
                $"gate '{duplicateGate}' is declared twice (SKILL.md and OVERLAY.md).");
        var missingGate = sourceGates.Concat(overlayGates).FirstOrDefault(g =>
            !File.Exists(Path.Combine(sourceDir, "scripts", g.Name + ".ps1")) &&
            !(overlayScriptsDir is not null && File.Exists(Path.Combine(overlayScriptsDir, g.Name + ".ps1"))));
        if (missingGate is not null)
            return new SkillImportResult(SkillImportOutcome.BadArgument,
                $"Skill declares gate '{missingGate.Name}' but 'scripts/{missingGate.Name}.ps1' is not in the import.");

        // Every refusal above returns before this line: nothing has been written, and nothing below
        // writes anything either — Validate's Ok case reports what Run would do, not what it did.
        return new SkillImportResult(SkillImportOutcome.Ok,
            $"'{name}' would be imported ({files} file{(files == 1 ? "" : "s")}, overlay: {(overlayDir is null ? "no" : "yes")}).",
            name, files, totalBytes, installedExists);
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

    /// <summary>D7's reviewable-text allowlist, matched case-insensitively — an extension outside it
    /// (including having none at all) is what the owner's approval card could never show as anything
    /// but a raw download, so <see cref="FindReviewIssue"/> refuses it before it ever becomes a
    /// proposal.</summary>
    private static readonly string[] ReviewableExtensions = [".md", ".ps1", ".psm1", ".psd1", ".txt", ".json", ".yml", ".yaml"];

    /// <summary>Task 1 / D7: the first file under <paramref name="root"/> that is not reviewable text —
    /// an extension outside <see cref="ReviewableExtensions"/> (case-insensitive; no extension counts
    /// as outside it), or one within it whose UTF-8 character count exceeds
    /// <see cref="SkillStore.MaxSkillChars"/> — naming the offending file; null when every file
    /// qualifies. A `.git` directory at the root is skipped exactly as <see cref="FindReparsePoint"/>,
    /// <see cref="CountTree"/> and <see cref="CopyTree"/> skip it.</summary>
    private static string? FindReviewIssue(string root)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return Scan(rootFull);

        string? Scan(string dir)
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (IsRootGitDir(dir, rootFull, Path.GetFileName(sub))) continue;
                var found = Scan(sub);
                if (found is not null) return found;
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file);
                if (ext.Length == 0 || !ReviewableExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    return $"'{file}' is not reviewable text; only {string.Join(' ', ReviewableExtensions)} files are accepted.";
                var chars = new UTF8Encoding(false).GetString(File.ReadAllBytes(file)).Length;
                if (chars > SkillStore.MaxSkillChars)
                    return $"'{file}' is {chars} characters; the cap is {SkillStore.MaxSkillChars}.";
            }
            return null;
        }
    }

    /// <summary>Row 19, task 12a: every file under <paramref name="root"/>, forward-slashed relative
    /// path to SHA-256 — the exact shape <see cref="SkillHashes.RecordTree"/> stores and
    /// <see cref="SkillStore.VerifyTree"/> later re-checks against. <paramref name="skipRootGit"/> is
    /// false for the INSTALLED target (the default, unchanged from before task 1): <see cref="CopyTree"/>
    /// already excluded `.git` there, so it can never contain one. <see cref="HashSourceTree"/> passes
    /// true, because a SOURCE tree — unlike an installed one — may still hold a `.git` directory
    /// CopyTree would itself skip; without the same skip here the two hashes of the same tree would
    /// disagree (task 1, D5's "the source tree's hash matches what installing it would record").</summary>
    private static Dictionary<string, string> HashTree(string root, bool skipRootGit = false)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(rootFull);
        return map;

        void Walk(string dir)
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (skipRootGit && IsRootGitDir(dir, rootFull, Path.GetFileName(sub))) continue;
                Walk(sub);
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var relative = Path.GetRelativePath(rootFull, file).Replace('\\', '/');
                map[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            }
        }
    }

    /// <summary>Task 1: the SOURCE tree's own manifest — the same shape <see cref="HashTree"/> computes
    /// for an installed target, generalised to skip a root `.git` the way <see cref="CopyTree"/> does,
    /// so a source tree's hash and the tree CopyTree would install from it are the same value (D5's
    /// staged-copy pin, task 2).</summary>
    public static Dictionary<string, string> HashSourceTree(string root) => HashTree(root, skipRootGit: true);

    /// <summary>Task 1: one value standing for a whole tree manifest — an ordinal sort of the
    /// manifest's own `path\n&lt;sha&gt;\n` lines, folded to one SHA-256 — so the `tree_sha256` column
    /// and a request body's hash have exactly one defined meaning (D5).</summary>
    public static string ManifestDigest(IReadOnlyDictionary<string, string> manifest)
    {
        var sb = new StringBuilder();
        foreach (var path in manifest.Keys.OrderBy(k => k, StringComparer.Ordinal))
            sb.Append(path).Append('\n').Append(manifest[path]).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
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
