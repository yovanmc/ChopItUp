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
}
