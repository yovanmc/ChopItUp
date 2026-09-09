using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ChopItUp.Core.Skills;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Skills;

namespace ChopItUp.Hub.Tests.Skills;

/// <summary>Task 5 (--import-skill): the rename-swap write procedure, its refusals (all checked
/// before anything is written), the mutex it shares with <see cref="SkillStore.Read"/>/
/// <see cref="SkillStore.List"/> (grill ledger M-4), and the fingerprint recorded in the `skills`
/// table rather than beside the skill (D-i). Row 11 fixtures only - no third-party skill text (D-g).</summary>
public sealed class SkillImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chopitup_import_" + Guid.NewGuid().ToString("N"));
    private readonly string _skillsRoot;
    private readonly ChopDb _db;
    private readonly SkillHashes _hashes;

    public SkillImportTests()
    {
        Directory.CreateDirectory(_root);
        _skillsRoot = Path.Combine(_root, "skills");
        _db = new ChopDb(Path.Combine(_root, "chopitup.db"));
        _db.EnsureDatabase();
        _hashes = new SkillHashes(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string ValidSkillBody =
        "---\nname: demo\ndescription: A demo skill for tests.\n---\n# Demo Skill\n\nBody text here.\n";

    /// <summary>A synthetic (never third-party, D-g) source directory outside the store, named
    /// <paramref name="name"/>, holding SKILL.md and (optionally) a references/ file.</summary>
    private string NewSourceDir(string name, string skillMd, bool withReference = false)
    {
        var dir = Path.Combine(_root, "sources", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), skillMd);
        if (withReference)
        {
            var refs = Path.Combine(dir, "references");
            Directory.CreateDirectory(refs);
            File.WriteAllText(Path.Combine(refs, "notes.md"), "reference material");
        }
        return dir;
    }

    private void AssertTargetAbsent(string name) =>
        Assert.False(Directory.Exists(Path.Combine(_skillsRoot, name)));

    /// <summary>A synthetic hub-side overlay directory (row 20 task 1): <c>OVERLAY.md</c> plus,
    /// optionally, <c>scripts/*.ps1</c> named by <paramref name="scripts"/> (file name including the
    /// <c>.ps1</c> extension to content).</summary>
    private string NewOverlayDir(string overlayMd, IReadOnlyDictionary<string, string>? scripts = null)
    {
        var dir = Path.Combine(_root, "overlays", "overlay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "OVERLAY.md"), overlayMd);
        if (scripts is { Count: > 0 })
        {
            var scriptsDir = Path.Combine(dir, "scripts");
            Directory.CreateDirectory(scriptsDir);
            foreach (var (fileName, content) in scripts)
                File.WriteAllText(Path.Combine(scriptsDir, fileName), content);
        }
        return dir;
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException($"mklink /J failed: {p.StandardError.ReadToEnd()}");
    }

    [Fact]
    public void Refuses_when_the_source_directory_does_not_exist()
    {
        var source = Path.Combine(_root, "sources", "nope");

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.SourceMissing, result.Outcome);
        AssertTargetAbsent("nope");
    }

    [Fact]
    public void Refuses_an_invalid_directory_name()
    {
        var source = NewSourceDir("Invalid_Name", ValidSkillBody);

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        AssertTargetAbsent("Invalid_Name");
    }

    // Row 19, task 13: the reserved `/stop` command cannot be shadowed by an installed skill. Checked
    // purely on the directory name, before the frontmatter is even read - ValidSkillBody's own
    // `name: demo` would otherwise mismatch the "stop" directory and refuse for a DIFFERENT reason
    // (Refusal 5), which would prove nothing about the reserved-name rule itself.
    [Fact]
    public void Refuses_to_import_a_skill_named_stop_the_reserved_run_command()
    {
        var source = NewSourceDir(RunCommands.StopName, ValidSkillBody);

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        Assert.Contains("reserved", result.Message);
        AssertTargetAbsent(RunCommands.StopName);
    }

    [Fact]
    public void Refuses_a_source_with_no_SKILL_md()
    {
        var dir = Path.Combine(_root, "sources", "empty");
        Directory.CreateDirectory(dir);

        var result = SkillImport.Run(dir, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.SourceMissing, result.Outcome);
        AssertTargetAbsent("empty");
    }

    [Fact]
    public void Refuses_a_SKILL_md_over_the_character_cap()
    {
        var huge = "---\nname: huge\ndescription: big.\n---\n" + new string('A', SkillStore.MaxSkillChars + 1);
        var source = NewSourceDir("huge", huge);

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        Assert.Contains(SkillStore.MaxSkillChars.ToString(), result.Message);
        AssertTargetAbsent("huge");
    }

    [Fact]
    public void Refuses_when_frontmatter_name_disagrees_with_the_directory()
    {
        var mismatched = "---\nname: other-name\ndescription: x.\n---\n# X\n";
        var source = NewSourceDir("mydir", mismatched);

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        AssertTargetAbsent("mydir");
    }

    [Fact]
    public void The_frontmatter_name_check_engages_on_CRLF_line_endings()
    {
        var crlf = "---\r\nname: other-name\r\ndescription: x.\r\n---\r\n# X\r\n";
        var source = NewSourceDir("mydir2", crlf);

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        AssertTargetAbsent("mydir2");
    }

    [Fact]
    public void A_junction_anywhere_in_the_source_is_refused()
    {
        var source = NewSourceDir("withjunction", ValidSkillBody);
        var junctionTargetDir = Path.Combine(_root, "junction-target");
        Directory.CreateDirectory(junctionTargetDir);
        var junctionPath = Path.Combine(source, "linked");
        CreateJunction(junctionPath, junctionTargetDir);
        try
        {
            var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

            Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
            AssertTargetAbsent("withjunction");
        }
        finally
        {
            // A non-recursive delete removes just the junction point, not its target's contents.
            // Deleting it here, before Dispose's recursive delete of the whole temp root, sidesteps
            // a measured .NET quirk where Directory.Delete(recursive: true) can throw
            // UnauthorizedAccessException on a tree containing a junction.
            Directory.Delete(junctionPath, recursive: false);
        }
    }

    [Fact]
    public void A_symlink_anywhere_in_the_source_is_refused_when_this_machine_can_create_one()
    {
        var source = NewSourceDir("withsymlink", ValidSkillBody);
        var targetFile = Path.Combine(_root, "symlink-target.txt");
        File.WriteAllText(targetFile, "x");
        try
        {
            File.CreateSymbolicLink(Path.Combine(source, "linked.txt"), targetFile);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Symlink creation needs developer mode or elevation, not guaranteed on the build/test
            // machine (LESSONS, "Could not verify" note on this plan). The junction test above
            // exercises the same refusal without that dependency. Early return, not Assert.Skip -
            // this stack is xunit 2.9.3 (claim 18), which has no such API.
            return;
        }

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
    }

    [Fact]
    public void Refuses_a_source_with_more_than_MaxFiles_files()
    {
        var source = NewSourceDir("manyfiles", ValidSkillBody);
        for (var i = 0; i < SkillStore.MaxFiles; i++)   // + SKILL.md itself = MaxFiles + 1 total
            File.WriteAllText(Path.Combine(source, $"f{i}.txt"), "x");

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        AssertTargetAbsent("manyfiles");
    }

    [Fact]
    public void Refuses_a_source_over_MaxBytes_total()
    {
        var source = NewSourceDir("bigfiles", ValidSkillBody);
        using (var fs = new FileStream(Path.Combine(source, "blob.bin"), FileMode.Create))
            fs.SetLength(SkillStore.MaxBytes + 1);

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        AssertTargetAbsent("bigfiles");
    }

    [Fact]
    public void A_source_exactly_at_MaxFiles_plus_a_one_file_overlay_is_refused_with_the_cap_message()
    {
        // Source alone lands EXACTLY at the cap (MaxFiles total, including SKILL.md) - not over it on
        // its own. The composed tree is what gets pinned (task 1), so the overlay's own OVERLAY.md
        // must count too: one more file tips it over. Directory name must match ValidSkillBody's own
        // frontmatter name ("demo") so refusal 5 (frontmatter/directory mismatch) does not fire first.
        var source = NewSourceDir("demo", ValidSkillBody);
        for (var i = 0; i < SkillStore.MaxFiles - 1; i++)   // + SKILL.md itself = MaxFiles total
            File.WriteAllText(Path.Combine(source, $"f{i}.txt"), "x");
        var overlay = NewOverlayDir("Overlay body.\n");

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes, overlay);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        Assert.Contains("the cap is", result.Message);
        AssertTargetAbsent("demo");
    }

    [Fact]
    public void Refuses_to_replace_an_existing_skill_without_force()
    {
        var source = NewSourceDir("demo", ValidSkillBody);
        Assert.Equal(SkillImportOutcome.Ok, SkillImport.Run(source, _skillsRoot, force: false, _hashes).Outcome);
        var before = File.ReadAllBytes(Path.Combine(_skillsRoot, "demo", "SKILL.md"));

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_skillsRoot, "demo", "SKILL.md")));
    }

    [Fact]
    public void A_valid_source_is_copied_and_its_hash_recorded()
    {
        var source = NewSourceDir("demo", ValidSkillBody, withReference: true);

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.Ok, result.Outcome);
        Assert.Equal("demo", result.Name);
        Assert.Equal(2, result.Files);   // SKILL.md + references/notes.md
        Assert.True(File.Exists(Path.Combine(_skillsRoot, "demo", "references", "notes.md")));
        var installed = File.ReadAllBytes(Path.Combine(_skillsRoot, "demo", "SKILL.md"));
        var expectedHash = Convert.ToHexString(SHA256.HashData(installed)).ToLowerInvariant();
        Assert.Equal(expectedHash, _hashes.Expected("demo"));
        Assert.False(Directory.Exists(Path.Combine(_skillsRoot, "demo.importing")));
    }

    [Fact]
    public void A_forced_reimport_replaces_SKILL_md_and_re_hashes_it()
    {
        var source = NewSourceDir("demo", ValidSkillBody);
        Assert.Equal(SkillImportOutcome.Ok, SkillImport.Run(source, _skillsRoot, force: false, _hashes).Outcome);
        const string replacement = "---\nname: demo\ndescription: v2.\n---\n# Demo v2\n";
        File.WriteAllText(Path.Combine(source, "SKILL.md"), replacement);

        var result = SkillImport.Run(source, _skillsRoot, force: true, _hashes);

        Assert.Equal(SkillImportOutcome.Ok, result.Outcome);
        Assert.Equal(replacement, File.ReadAllText(Path.Combine(_skillsRoot, "demo", "SKILL.md")));
        var bytes = File.ReadAllBytes(Path.Combine(_skillsRoot, "demo", "SKILL.md"));
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(expectedHash, _hashes.Expected("demo"));
        Assert.False(Directory.Exists(Path.Combine(_skillsRoot, "demo.replaced")));
        Assert.False(Directory.Exists(Path.Combine(_skillsRoot, "demo.importing")));
    }

    [Fact]
    public void A_trailing_directory_separator_on_the_source_path_does_not_yield_an_empty_name()
    {
        var source = NewSourceDir("trailer", "# Trailer\n\nNo frontmatter, so no name to disagree with the directory.\n");
        var withSlash = source + Path.DirectorySeparatorChar;

        var result = SkillImport.Run(withSlash, _skillsRoot, force: false, _hashes);

        Assert.True(result.Outcome == SkillImportOutcome.Ok, result.Message);
        Assert.Equal("trailer", result.Name);
        Assert.True(Directory.Exists(Path.Combine(_skillsRoot, "trailer")));
    }

    [Fact]
    public async Task A_swap_retries_past_a_reader_that_releases_its_handle_in_time()
    {
        var source = NewSourceDir("demo", ValidSkillBody);
        Assert.Equal(SkillImportOutcome.Ok, SkillImport.Run(source, _skillsRoot, force: false, _hashes).Outcome);
        const string replacement = "---\nname: demo\ndescription: v2.\n---\n# Demo v2\n";
        File.WriteAllText(Path.Combine(source, "SKILL.md"), replacement);

        var handle = new FileStream(Path.Combine(_skillsRoot, "demo", "SKILL.md"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var release = Task.Run(async () => { await Task.Delay(300); handle.Dispose(); });

        var result = SkillImport.Run(source, _skillsRoot, force: true, _hashes);
        await release;

        Assert.Equal(SkillImportOutcome.Ok, result.Outcome);
        Assert.Equal(replacement, File.ReadAllText(Path.Combine(_skillsRoot, "demo", "SKILL.md")));
    }

    [Fact]
    public void A_swap_fails_and_leaves_the_installed_skill_intact_when_a_reader_never_releases()
    {
        var source = NewSourceDir("demo", ValidSkillBody);
        Assert.Equal(SkillImportOutcome.Ok, SkillImport.Run(source, _skillsRoot, force: false, _hashes).Outcome);
        var before = File.ReadAllBytes(Path.Combine(_skillsRoot, "demo", "SKILL.md"));
        var beforeHash = _hashes.Expected("demo");
        File.WriteAllText(Path.Combine(source, "SKILL.md"), "---\nname: demo\ndescription: v2.\n---\n# v2\n");

        using var handle = new FileStream(Path.Combine(_skillsRoot, "demo", "SKILL.md"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var result = SkillImport.Run(source, _skillsRoot, force: true, _hashes);

        Assert.Equal(SkillImportOutcome.IoFailure, result.Outcome);
        Assert.Contains("reader", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_skillsRoot, "demo", "SKILL.md")));
        Assert.Equal(beforeHash, _hashes.Expected("demo"));
        Assert.False(Directory.Exists(Path.Combine(_skillsRoot, "demo.replaced")));
        Assert.False(Directory.Exists(Path.Combine(_skillsRoot, "demo.importing")));
    }

    // --- Row 19 task 12a: the whole-tree manifest -------------------------------------------------

    [Fact]
    public void A_valid_import_records_a_manifest_of_every_installed_file_hashed_after_the_copy()
    {
        var source = NewSourceDir("demo", ValidSkillBody, withReference: true);

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.Ok, result.Outcome);
        var manifest = _hashes.ExpectedTree("demo");
        Assert.Equal(2, manifest.Count);
        foreach (var relative in new[] { "SKILL.md", "references/notes.md" })
        {
            var installed = File.ReadAllBytes(Path.Combine(_skillsRoot, "demo", relative));
            var expectedHash = Convert.ToHexString(SHA256.HashData(installed)).ToLowerInvariant();
            Assert.Equal(expectedHash, manifest[relative]);
        }
    }

    [Fact]
    public void A_forced_reimport_replaces_the_manifest_rather_than_accumulating_stale_entries()
    {
        var source = NewSourceDir("demo", ValidSkillBody, withReference: true);
        Assert.Equal(SkillImportOutcome.Ok, SkillImport.Run(source, _skillsRoot, force: false, _hashes).Outcome);
        Assert.Equal(2, _hashes.ExpectedTree("demo").Count);
        Directory.Delete(Path.Combine(source, "references"), recursive: true);   // the re-import drops the reference file
        File.WriteAllText(Path.Combine(source, "SKILL.md"), "---\nname: demo\ndescription: v2.\n---\n# v2\n");

        var result = SkillImport.Run(source, _skillsRoot, force: true, _hashes);

        Assert.Equal(SkillImportOutcome.Ok, result.Outcome);
        var manifest = _hashes.ExpectedTree("demo");
        Assert.Equal(["SKILL.md"], manifest.Keys);
    }

    [Fact]
    public void Refuses_when_a_declared_gate_script_is_not_in_the_import_and_writes_nothing()
    {
        var gatedBody = "---\nname: gated\ndescription: d.\nrun: true\ngates: check-it\n---\n# Gated\n";
        var source = NewSourceDir("gated", gatedBody);   // no scripts/check-it.ps1

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        Assert.Contains("check-it", result.Message);
        Assert.Contains("scripts/check-it.ps1", result.Message);
        AssertTargetAbsent("gated");
        Assert.Null(_hashes.Expected("gated"));
    }

    [Fact]
    public void A_declared_gate_whose_script_is_present_installs_and_is_hashed_in_the_manifest()
    {
        var gatedBody = "---\nname: gated-ok\ndescription: d.\nrun: true\ngates: check-it(--Foo bar)\n---\n# Gated\n";
        var source = NewSourceDir("gated-ok", gatedBody);
        var scripts = Path.Combine(source, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "check-it.ps1"), "exit 0\n");

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.Ok, result.Outcome);
        Assert.True(_hashes.ExpectedTree("gated-ok").ContainsKey("scripts/check-it.ps1"));
    }

    [Fact]
    public void A_dangling_replaced_directory_from_a_crashed_swap_is_restored_not_deleted()
    {
        var replacedDir = Path.Combine(_skillsRoot, "demo.replaced");
        Directory.CreateDirectory(replacedDir);
        var oldBytes = new UTF8Encoding(false).GetBytes(ValidSkillBody);
        File.WriteAllBytes(Path.Combine(replacedDir, "SKILL.md"), oldBytes);
        var oldHash = Convert.ToHexString(SHA256.HashData(oldBytes)).ToLowerInvariant();
        _hashes.Record("demo", oldHash, "test");

        var newSource = NewSourceDir("demo", "---\nname: demo\ndescription: v2.\n---\n# v2\n");

        // Without --force this call sees the RESTORED target and refuses - proof the restore ran,
        // rather than the dangling backup being silently discarded (grill ledger M-4).
        var refused = SkillImport.Run(newSource, _skillsRoot, force: false, _hashes);
        Assert.Equal(SkillImportOutcome.BadArgument, refused.Outcome);
        Assert.False(Directory.Exists(replacedDir));
        Assert.Equal(oldBytes, File.ReadAllBytes(Path.Combine(_skillsRoot, "demo", "SKILL.md")));

        // A forced reimport proceeds normally on top of the restored skill.
        var forced = SkillImport.Run(newSource, _skillsRoot, force: true, _hashes);
        Assert.Equal(SkillImportOutcome.Ok, forced.Outcome);
        Assert.Contains("v2", File.ReadAllText(Path.Combine(_skillsRoot, "demo", "SKILL.md")));
    }

    // --- Row 20 task 1: overlay composition and pinned rendering ------------------------------------

    [Fact]
    public void An_overlay_dir_is_composed_into_the_skill_and_every_file_is_pinned()
    {
        var source = NewSourceDir("demo", ValidSkillBody);
        var overlay = NewOverlayDir("Overlay prose.\n", new Dictionary<string, string> { ["x.ps1"] = "exit 0\n" });

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes, overlay);

        Assert.Equal(SkillImportOutcome.Ok, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_skillsRoot, "demo", "OVERLAY.md")));
        Assert.True(File.Exists(Path.Combine(_skillsRoot, "demo", "scripts", "x.ps1")));
        var manifest = _hashes.ExpectedTree("demo");
        Assert.True(manifest.ContainsKey("OVERLAY.md"));
        Assert.True(manifest.ContainsKey("scripts/x.ps1"));
        foreach (var relative in new[] { "SKILL.md", "OVERLAY.md", "scripts/x.ps1" })
        {
            var installed = File.ReadAllBytes(Path.Combine(_skillsRoot, "demo", relative));
            var expectedHash = Convert.ToHexString(SHA256.HashData(installed)).ToLowerInvariant();
            Assert.Equal(expectedHash, manifest[relative]);
        }
    }

    [Fact]
    public void A_source_carrying_OVERLAY_md_is_refused()
    {
        var source = NewSourceDir("demo", ValidSkillBody);
        File.WriteAllText(Path.Combine(source, "OVERLAY.md"), "sneaky\n");

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        Assert.Contains("OVERLAY.md", result.Message);
        AssertTargetAbsent("demo");
    }

    [Fact]
    public void A_forced_reimport_without_overlay_is_refused_when_the_skill_has_one()
    {
        var source = NewSourceDir("demo", ValidSkillBody);
        var overlay = NewOverlayDir("Overlay prose.\n");
        Assert.Equal(SkillImportOutcome.Ok, SkillImport.Run(source, _skillsRoot, force: false, _hashes, overlay).Outcome);
        var beforeSkillMd = File.ReadAllBytes(Path.Combine(_skillsRoot, "demo", "SKILL.md"));
        var beforeOverlayMd = File.ReadAllBytes(Path.Combine(_skillsRoot, "demo", "OVERLAY.md"));

        var result = SkillImport.Run(source, _skillsRoot, force: true, _hashes);   // no --overlay

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        Assert.Contains("overlay", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeSkillMd, File.ReadAllBytes(Path.Combine(_skillsRoot, "demo", "SKILL.md")));
        Assert.Equal(beforeOverlayMd, File.ReadAllBytes(Path.Combine(_skillsRoot, "demo", "OVERLAY.md")));
    }

    [Fact]
    public void Overlay_gates_join_the_skill_gates_and_each_needs_a_script()
    {
        var gatedBody = "---\nname: gated-overlay\ndescription: d.\nrun: true\ngates: check-it\n---\n# Gated\n";
        var source = NewSourceDir("gated-overlay", gatedBody);
        var scripts = Path.Combine(source, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "check-it.ps1"), "exit 0\n");
        var overlayMd = "---\nrun: true\ngates: overlay-gate\n---\nOverlay prose.\n";
        var overlayWithoutScript = NewOverlayDir(overlayMd);

        var missing = SkillImport.Run(source, _skillsRoot, force: false, _hashes, overlayWithoutScript);
        Assert.Equal(SkillImportOutcome.BadArgument, missing.Outcome);
        Assert.Contains("overlay-gate", missing.Message);
        AssertTargetAbsent("gated-overlay");

        var overlayWithScript = NewOverlayDir(overlayMd, new Dictionary<string, string> { ["overlay-gate.ps1"] = "exit 0\n" });
        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes, overlayWithScript);

        Assert.Equal(SkillImportOutcome.Ok, result.Outcome);
        var manifest = _hashes.ExpectedTree("gated-overlay");
        Assert.True(manifest.ContainsKey("scripts/check-it.ps1"));
        Assert.True(manifest.ContainsKey("scripts/overlay-gate.ps1"));
    }

    [Fact]
    public void A_gate_declared_in_both_SKILL_md_and_OVERLAY_md_is_refused()
    {
        var gatedBody = "---\nname: dup-gate\ndescription: d.\nrun: true\ngates: check-it\n---\n# Gated\n";
        var source = NewSourceDir("dup-gate", gatedBody);
        var scripts = Path.Combine(source, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "check-it.ps1"), "exit 0\n");
        // No overlay script named check-it.ps1 here on purpose: shipping one would collide with the
        // source's own scripts/check-it.ps1 and trip refusal 7c's cross-directory collision check
        // before refusal 9's gate-union duplicate check ever runs.
        var overlay = NewOverlayDir("---\nrun: true\ngates: check-it\n---\nOverlay prose.\n");

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes, overlay);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        Assert.Contains("check-it", result.Message);
        Assert.Contains("twice", result.Message);
        AssertTargetAbsent("dup-gate");
    }

    [Fact]
    public void An_overlay_dir_with_a_stray_file_is_refused()
    {
        var source = NewSourceDir("demo", ValidSkillBody);
        var overlay = NewOverlayDir("Overlay prose.\n");
        File.WriteAllText(Path.Combine(overlay, "stray.txt"), "not allowed\n");

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes, overlay);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        AssertTargetAbsent("demo");
    }

    [Fact]
    public void An_overlay_over_MaxOverlayChars_is_refused()
    {
        var source = NewSourceDir("demo", ValidSkillBody);
        var huge = new string('A', SkillStore.MaxOverlayChars + 1);
        var overlay = NewOverlayDir(huge);

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes, overlay);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        Assert.Contains(SkillStore.MaxOverlayChars.ToString(), result.Message);
        AssertTargetAbsent("demo");
    }

    // --- M25 task 1: SkillImport.Validate - the refusal battery with no side effects -------------

    [Fact]
    public void Validate_refuses_when_the_target_already_exists_and_writes_nothing_even_when_the_skills_root_never_existed()
    {
        // The skills root's own directory never gets created by this test - only the `.replaced`
        // fixture's own nested-directory creation brings it into being, never Validate itself. A
        // torn store left holding ONLY `demo.replaced` (no `demo`) must still be judged "installed"
        // by Validate, without Validate restoring it (task 1, pass 1 finding).
        var freshRoot = Path.Combine(_root, "fresh-skills");
        var replacedDir = Path.Combine(freshRoot, "demo.replaced");
        Directory.CreateDirectory(replacedDir);
        File.WriteAllBytes(Path.Combine(replacedDir, "SKILL.md"), new UTF8Encoding(false).GetBytes(ValidSkillBody));
        var source = NewSourceDir("demo", "---\nname: demo\ndescription: v2.\n---\n# v2\n");

        var result = SkillImport.Validate(source, freshRoot, force: false);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        Assert.Contains("already exists", result.Message);
        Assert.False(Directory.Exists(Path.Combine(freshRoot, "demo.importing")));
        Assert.False(Directory.Exists(Path.Combine(freshRoot, "demo")));   // Validate never restores .replaced
        Assert.True(Directory.Exists(replacedDir));                       // .replaced left exactly as found
        Assert.Null(_hashes.Expected("demo"));                            // no database row written
    }

    [Fact]
    public void Validate_of_a_good_source_writes_nothing_under_a_skills_root_that_has_never_existed_and_reports_files_bytes_and_replaces_installed()
    {
        var freshRoot = Path.Combine(_root, "another-fresh-skills");
        Assert.False(Directory.Exists(freshRoot));
        var source = NewSourceDir("demo", ValidSkillBody, withReference: true);

        var result = SkillImport.Validate(source, freshRoot, force: false);

        Assert.Equal(SkillImportOutcome.Ok, result.Outcome);
        Assert.Equal("demo", result.Name);
        Assert.Equal(2, result.Files);
        Assert.True(result.Bytes > 0);
        Assert.False(result.ReplacesInstalled);
        Assert.False(Directory.Exists(freshRoot));   // Validate never creates the skills root
        Assert.Null(_hashes.Expected("demo"));       // no database row written
    }

    [Fact]
    public void Validate_reports_ReplacesInstalled_true_for_a_torn_replaced_only_store_and_allows_it_with_force_without_restoring()
    {
        var freshRoot = Path.Combine(_root, "torn-skills");
        var replacedDir = Path.Combine(freshRoot, "demo.replaced");
        Directory.CreateDirectory(replacedDir);
        File.WriteAllBytes(Path.Combine(replacedDir, "SKILL.md"), new UTF8Encoding(false).GetBytes(ValidSkillBody));
        var source = NewSourceDir("demo", "---\nname: demo\ndescription: v2.\n---\n# v2\n");

        var result = SkillImport.Validate(source, freshRoot, force: true);

        Assert.Equal(SkillImportOutcome.Ok, result.Outcome);
        Assert.True(result.ReplacesInstalled);
        Assert.False(Directory.Exists(Path.Combine(freshRoot, "demo")));   // Validate never restores .replaced
    }

    [Fact]
    public void Validate_returns_the_same_refusal_message_Run_would_for_an_invalid_name()
    {
        var source = NewSourceDir("Invalid_Name", ValidSkillBody);

        var validated = SkillImport.Validate(source, _skillsRoot, force: false);
        var run = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(run.Outcome, validated.Outcome);
        Assert.Equal(run.Message, validated.Message);
        AssertTargetAbsent("Invalid_Name");
    }

    [Fact]
    public void Validate_returns_the_same_refusal_message_Run_would_for_a_missing_SKILL_md()
    {
        var dir = Path.Combine(_root, "sources", "empty");
        Directory.CreateDirectory(dir);

        var validated = SkillImport.Validate(dir, _skillsRoot, force: false);
        var run = SkillImport.Run(dir, _skillsRoot, force: false, _hashes);

        Assert.Equal(run.Outcome, validated.Outcome);
        Assert.Equal(run.Message, validated.Message);
    }

    [Fact]
    public void A_source_directory_that_is_itself_a_junction_is_refused_by_Validate()
    {
        var realDir = Path.Combine(_root, "real-demo");
        Directory.CreateDirectory(realDir);
        File.WriteAllText(Path.Combine(realDir, "SKILL.md"), ValidSkillBody);
        Directory.CreateDirectory(Path.Combine(_root, "sources"));
        var linkedSource = Path.Combine(_root, "sources", "demo");
        CreateJunction(linkedSource, realDir);
        try
        {
            var result = SkillImport.Validate(linkedSource, _skillsRoot, force: false);

            Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
            Assert.Contains("link", result.Message, StringComparison.OrdinalIgnoreCase);
            AssertTargetAbsent("demo");
        }
        finally
        {
            Directory.Delete(linkedSource, recursive: false);
        }
    }

    [Fact]
    public void A_file_with_an_extension_outside_the_reviewable_allowlist_is_refused_by_name()
    {
        var source = NewSourceDir("demo", ValidSkillBody);
        File.WriteAllBytes(Path.Combine(source, "helper.exe"), [0x4D, 0x5A]);

        var result = SkillImport.Validate(source, _skillsRoot, force: false);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        Assert.Contains("helper.exe", result.Message);
        AssertTargetAbsent("demo");
    }

    [Fact]
    public void Run_does_not_enforce_D7s_reviewable_allowlist_the_CLI_path_stays_open_to_binaries()
    {
        // D7 (plan lines 172-183) is a propose-time refusal: "Skills needing a binary stay on the
        // CLI path, where the owner is already at the keyboard." Same fixture as
        // A_file_with_an_extension_outside_the_reviewable_allowlist_is_refused_by_name above, which
        // proves Validate still refuses it - this proves Run (the CLI path) does not.
        var source = NewSourceDir("demo", ValidSkillBody);
        File.WriteAllBytes(Path.Combine(source, "helper.exe"), [0x4D, 0x5A]);

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.Ok, result.Outcome);
    }

    [Fact]
    public void The_reviewable_extension_check_is_case_insensitive()
    {
        var source = NewSourceDir("demo", ValidSkillBody);
        File.WriteAllText(Path.Combine(source, "notes.TXT"), "fine");
        File.WriteAllText(Path.Combine(source, "readme.YML"), "fine: too");

        var result = SkillImport.Validate(source, _skillsRoot, force: false);

        Assert.True(result.Outcome == SkillImportOutcome.Ok, result.Message);
    }

    [Fact]
    public void A_file_with_no_extension_is_refused()
    {
        var source = NewSourceDir("demo", ValidSkillBody);
        File.WriteAllText(Path.Combine(source, "LICENSE"), "MIT");

        var result = SkillImport.Validate(source, _skillsRoot, force: false);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        Assert.Contains("LICENSE", result.Message);
        AssertTargetAbsent("demo");
    }

    [Fact]
    public void A_non_SKILL_md_file_over_MaxSkillChars_is_refused_at_validate_time()
    {
        var source = NewSourceDir("demo", ValidSkillBody, withReference: true);
        File.WriteAllText(Path.Combine(source, "references", "notes.md"), new string('A', SkillStore.MaxSkillChars + 1));

        var result = SkillImport.Validate(source, _skillsRoot, force: false);

        Assert.Equal(SkillImportOutcome.BadArgument, result.Outcome);
        Assert.Contains(SkillStore.MaxSkillChars.ToString(), result.Message);
        AssertTargetAbsent("demo");
    }

    [Fact]
    public void Validate_refuses_with_IoFailure_when_the_store_mutex_is_held_by_another_import()
    {
        var source = NewSourceDir("demo", ValidSkillBody);
        // Same literal prefix SkillImport and SkillStore both key their mutex on (M-4) - duplicated
        // here on purpose, the way SkillStore.cs already duplicates it rather than exposing it.
        var mutexName = PathMutex.Name("Global\\ChopItUp.Skills.", _skillsRoot);
        // A named Mutex is reentrant for the THREAD that owns it (measured this session: calling
        // Validate on the SAME thread that holds `external` sails straight through, no contention at
        // all), so the holder must be a genuinely different OS thread - a plain Thread, not
        // Task.Run/await, which can also resume a continuation on a different pool thread than the
        // one that started it and make ReleaseMutex throw "unsynchronized block of code" (also
        // measured this session).
        using var external = new Mutex(initiallyOwned: true, mutexName);
        SkillImportResult? result = null;
        Exception? workerException = null;
        var sw = Stopwatch.StartNew();
        var worker = new Thread(() =>
        {
            try { result = SkillImport.Validate(source, _skillsRoot, force: false); }
            catch (Exception e) { workerException = e; }
        });
        worker.Start();
        worker.Join();
        sw.Stop();
        external.ReleaseMutex();

        Assert.Null(workerException);
        Assert.NotNull(result);
        Assert.Equal(SkillImportOutcome.IoFailure, result!.Outcome);
        Assert.Contains("busy", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(9), $"Elapsed {sw.Elapsed} - Validate should have waited out the same 10s mutex timeout Run uses.");
    }

    [Fact]
    public void HashSourceTree_matches_the_manifest_Run_records_and_folds_to_the_same_ManifestDigest()
    {
        var source = NewSourceDir("demo", ValidSkillBody, withReference: true);
        var sourceManifest = SkillImport.HashSourceTree(source);

        var result = SkillImport.Run(source, _skillsRoot, force: false, _hashes);

        Assert.Equal(SkillImportOutcome.Ok, result.Outcome);
        var installedManifest = _hashes.ExpectedTree("demo");
        Assert.Equal(installedManifest.Keys.OrderBy(k => k), sourceManifest.Keys.OrderBy(k => k));
        foreach (var key in sourceManifest.Keys)
            Assert.Equal(installedManifest[key], sourceManifest[key]);
        Assert.Equal(SkillImport.ManifestDigest(installedManifest), SkillImport.ManifestDigest(sourceManifest));
    }
}
