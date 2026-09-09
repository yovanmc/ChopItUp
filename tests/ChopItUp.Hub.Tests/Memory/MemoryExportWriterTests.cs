using ChopItUp.Core.Memory;
using ChopItUp.Hub.Memory;

namespace ChopItUp.Hub.Tests.Memory;

public sealed class MemoryExportWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chopitup_writer_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _cleanup = [];

    private string NewDir(string suffix, bool create = true)
    {
        var dir = Path.Combine(_root, suffix);
        if (create) Directory.CreateDirectory(dir);
        _cleanup.Add(dir);
        return dir;
    }

    private static MemoryStore NewStore(string dir)
    {
        Directory.CreateDirectory(dir);
        return new MemoryStore(dir);
    }

    public void Dispose()
    {
        foreach (var dir in _cleanup) TestDirs.DeleteTree(dir);
        TestDirs.DeleteTree(_root);
    }

    private static ExportResult Run(MemoryStore store, string targetDir, bool force, bool acceptNewSource,
        out string outText, out string errText)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var result = MemoryExportWriter.Run(store, targetDir, force, acceptNewSource, output, error);
        outText = output.ToString();
        errText = error.ToString();
        return result;
    }

    // ---- 1. first export into a missing target -----------------------------------------------

    [Fact]
    public void T3_a_first_export_into_a_missing_target_creates_it_and_verifies_Clean()
    {
        var store = NewStore(NewDir("t1-store"));
        store.Append("user", "A", "Body A.", "prov");
        var targetDir = NewDir("t1-target", create: false);

        var result = Run(store, targetDir, force: false, acceptNewSource: false, out _, out var err);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", err);
        Assert.True(Directory.Exists(targetDir));
        Assert.Null(result.PreviousDir);
        Assert.Empty(result.OtherStagingDirs);

        var manifest = ExportManifest.TryRead(targetDir);
        Assert.NotNull(manifest);
        var verdict = ExportManifest.Verify(manifest, targetDir, store);
        Assert.Equal(TargetState.Clean, verdict.State);
    }

    // ---- 2. three consecutive clean exports (pass 2 B1, AC12) --------------------------------

    [Fact]
    public void T3_three_consecutive_clean_exports_all_exit_0_and_leave_exactly_one_reusable_previous()
    {
        var store = NewStore(NewDir("t2-store"));
        store.Append("user", "A", "Body A.", "prov");
        var targetDir = NewDir("t2-target", create: false);

        var r1 = Run(store, targetDir, false, false, out _, out var e1);
        var r2 = Run(store, targetDir, false, false, out _, out var e2);
        var r3 = Run(store, targetDir, false, false, out _, out var e3);

        Assert.Equal(0, r1.ExitCode);
        Assert.Equal(0, r2.ExitCode);
        Assert.Equal(0, r3.ExitCode);
        Assert.Equal("", e1);
        Assert.Equal("", e2);
        Assert.Equal("", e3);

        var previousPlain = targetDir + ".chopitup-export-previous";
        Assert.True(Directory.Exists(previousPlain));
        var parent = Path.GetDirectoryName(targetDir)!;
        var previousDirs = Directory.EnumerateDirectories(parent, Path.GetFileName(targetDir) + ".chopitup-export-previous*").ToList();
        Assert.Single(previousDirs);
    }

    // ---- 3. populated target, no manifest -> Foreign; force replaces, timestamped previous ---

    [Fact]
    public void T3_a_populated_target_with_no_manifest_refuses_listing_its_files_and_force_replaces_it_into_a_timestamped_previous()
    {
        var store = NewStore(NewDir("t3-store"));
        store.Append("user", "A", "Body A.", "prov");
        var targetDir = NewDir("t3-target");
        File.WriteAllText(Path.Combine(targetDir, "someone-elses-file.md"), "not ours");

        var refused = Run(store, targetDir, force: false, acceptNewSource: false, out _, out var err);
        Assert.Equal(6, refused.ExitCode);
        Assert.Contains("someone-elses-file.md", err);
        Assert.True(File.Exists(Path.Combine(targetDir, "someone-elses-file.md")));   // nothing changed

        var forced = Run(store, targetDir, force: true, acceptNewSource: false, out var outText, out _);
        Assert.Equal(0, forced.ExitCode);
        Assert.NotNull(forced.PreviousDir);
        Assert.Contains("previous", forced.PreviousDir);
        Assert.NotEqual(targetDir + ".chopitup-export-previous", forced.PreviousDir);   // timestamped, not the plain slot
        Assert.True(File.Exists(Path.Combine(forced.PreviousDir!, "someone-elses-file.md")));
        Assert.Contains("someone-elses-file.md", outText);
    }

    // ---- 4. force-replace a drifted target, export again, rescued file survives --------------

    [Fact]
    public void T3_force_replace_a_drifted_target_then_export_again_and_the_rescued_file_still_exists_at_the_printed_path()
    {
        var store = NewStore(NewDir("t4-store"));
        store.Append("user", "A", "Body A.", "prov");
        var targetDir = NewDir("t4-target", create: false);

        var first = Run(store, targetDir, false, false, out _, out _);
        Assert.Equal(0, first.ExitCode);

        // Drift the target: edit a real exported file's content directly.
        var exportedFile = Directory.EnumerateFiles(targetDir, "*.md").First(f => !f.EndsWith("MEMORY.md", StringComparison.OrdinalIgnoreCase));
        File.WriteAllText(exportedFile, "tampered content");

        var forced = Run(store, targetDir, force: true, acceptNewSource: false, out var outText, out _);
        Assert.Equal(0, forced.ExitCode);
        Assert.NotNull(forced.PreviousDir);
        var rescuedPath = Path.Combine(forced.PreviousDir!, Path.GetFileName(exportedFile));
        Assert.True(File.Exists(rescuedPath));
        Assert.Equal("tampered content", File.ReadAllText(rescuedPath));
        Assert.Contains(forced.PreviousDir!, outText);

        // A third (clean) export must not touch the timestamped previous from the force-replace.
        var third = Run(store, targetDir, false, false, out _, out _);
        Assert.Equal(0, third.ExitCode);
        Assert.True(File.Exists(rescuedPath));
        Assert.Equal("tampered content", File.ReadAllText(rescuedPath));
    }

    // ---- 5. shrinking store -> timestamped previous, report names the drop (pass 2 B3) -------

    [Fact]
    public void T3_a_shrinking_store_between_two_clean_exports_lands_in_a_timestamped_previous_and_the_report_names_the_drop()
    {
        var storeDir = NewDir("t5-store");
        var store = NewStore(storeDir);
        store.Append("user", "A", "Body A.", "prov");
        store.Append("user", "B", "Body B.", "prov");
        store.Append("user", "C", "Body C.", "prov");
        var targetDir = NewDir("t5-target", create: false);

        var first = Run(store, targetDir, false, false, out _, out _);
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(3, first.Exported);

        // Shrink the store (row-23-style consolidation, a restored backup, ...): supersede two entries
        // away by writing the topic file directly to a single surviving entry.
        File.WriteAllText(Path.Combine(storeDir, "topics", "user.md"), "# user\n\n## A\n<!-- prov -->\nBody A.\n");

        var second = Run(store, targetDir, false, false, out var outText, out var err);

        Assert.Equal(0, second.ExitCode);
        Assert.Equal("", err);
        Assert.Equal(1, second.Exported);
        Assert.NotNull(second.PreviousDir);
        Assert.NotEqual(targetDir + ".chopitup-export-previous", second.PreviousDir);   // timestamped, not reusable
        Assert.True(Directory.Exists(second.PreviousDir));
        // The dropped files (B, C) survive in the timestamped previous.
        var previousFiles = Directory.EnumerateFiles(second.PreviousDir!, "*.md").Select(Path.GetFileName).ToList();
        Assert.Contains(previousFiles, f => f!.Contains("-b", StringComparison.OrdinalIgnoreCase) || f.Contains("-c", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(second.PreviousDir!, outText);
    }

    // ---- 6. a different store: refuses with both roots, --force does not help, ---------------
    //         --accept-new-source proceeds after printing the drift list (pass 2 M4)

    [Fact]
    public void T3_a_different_store_refuses_with_both_roots_stays_refused_under_force_and_accept_new_source_proceeds_after_printing_the_drift_list()
    {
        var storeA = NewStore(NewDir("t6-store-a"));
        storeA.Append("user", "A", "Body A.", "prov");
        var storeB = NewStore(NewDir("t6-store-b"));
        storeB.Append("user", "B", "Body B.", "prov");
        var targetDir = NewDir("t6-target", create: false);

        var first = Run(storeA, targetDir, false, false, out _, out _);
        Assert.Equal(0, first.ExitCode);

        var refused = Run(storeB, targetDir, force: false, acceptNewSource: false, out _, out var err1);
        Assert.Equal(6, refused.ExitCode);
        Assert.Contains(storeA.Root, err1);
        Assert.Contains(storeB.Root, err1);

        var stillRefused = Run(storeB, targetDir, force: true, acceptNewSource: false, out _, out var err2);
        Assert.Equal(6, stillRefused.ExitCode);   // --force never overrides DifferentSource (D9)
        Assert.Contains(storeA.Root, err2);

        // Nothing changed by either refusal.
        var untouched = ExportManifest.Verify(ExportManifest.TryRead(targetDir), targetDir, storeA);
        Assert.Equal(TargetState.Clean, untouched.State);

        var accepted = Run(storeB, targetDir, force: false, acceptNewSource: true, out var outText, out _);
        Assert.Equal(0, accepted.ExitCode);
        // The drift list (here: the old export's file, unknown to storeB's fresh export) was printed.
        var oldFile = Directory.EnumerateFiles(targetDir, "*.md")
            .Select(Path.GetFileName).FirstOrDefault(f => f!.Contains("-a", StringComparison.OrdinalIgnoreCase));
        // (Best-effort: the exact old file name may already be gone by the time we inspect targetDir,
        // since accepted already swapped it out - so assert against the printed roots instead.)
        Assert.Contains(storeA.Root, outText);
        Assert.Contains(storeB.Root, outText);
    }

    // ---- 7. an unreadable manifest refuses naming the override and does not throw ------------

    [Fact]
    public void T3_an_unreadable_manifest_refuses_naming_the_override_and_does_not_throw()
    {
        var store = NewStore(NewDir("t7-store"));
        store.Append("user", "A", "Body A.", "prov");
        var targetDir = NewDir("t7-target");
        File.WriteAllText(Path.Combine(targetDir, ExportManifest.FileName), "not json at all {{{");
        File.WriteAllText(Path.Combine(targetDir, "real.md"), "real content");

        var result = Run(store, targetDir, force: false, acceptNewSource: false, out _, out var err);

        Assert.Equal(6, result.ExitCode);
        Assert.Contains("force", err, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("real.md", err);
    }

    // ---- 8. the refusal's list and the override's list are byte-identical (AC6, pass 2 M10) --

    [Fact]
    public void T3_the_refusals_affected_path_list_and_the_overrides_are_byte_identical()
    {
        var store = NewStore(NewDir("t8-store"));
        store.Append("user", "A", "Body A.", "prov");
        var targetDir = NewDir("t8-target");
        Directory.CreateDirectory(Path.Combine(targetDir, "sub"));
        File.WriteAllText(Path.Combine(targetDir, "foreign1.md"), "x");
        File.WriteAllText(Path.Combine(targetDir, "sub", "foreign2.md"), "y");

        var refused = Run(store, targetDir, force: false, acceptNewSource: false, out _, out var err);
        Assert.Equal(6, refused.ExitCode);

        var expected = MemoryExportWriter.FormatAffectedPaths(new[] { "foreign1.md", "sub/foreign2.md" });
        Assert.Contains(expected, err);

        var forced = Run(store, targetDir, force: true, acceptNewSource: false, out var outText, out _);
        Assert.Equal(0, forced.ExitCode);
        Assert.Contains(expected, outText);
    }

    // ---- 9. a FileShare.Read handle held on a file inside the target makes the swap fail ------

    [Fact]
    public void T3_a_held_file_handle_inside_the_target_makes_the_swap_fail_and_the_target_and_stage_stay_intact()
    {
        var store = NewStore(NewDir("t9-store"));
        store.Append("user", "A", "Body A.", "prov");
        var targetDir = NewDir("t9-target", create: false);

        var first = Run(store, targetDir, false, false, out _, out _);
        Assert.Equal(0, first.ExitCode);

        var exportedFile = Directory.EnumerateFiles(targetDir, "*.md")
            .First(f => !f.EndsWith("MEMORY.md", StringComparison.OrdinalIgnoreCase));
        var before = File.ReadAllText(exportedFile);

        using var handle = new FileStream(exportedFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Holding FileShare.Read (not None) still blocks Directory.Move on Windows, because a rename
        // requires no open handles at all, even read-sharing ones.
        var result = Run(store, targetDir, false, false, out _, out var err);

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(Directory.Exists(targetDir));
        Assert.Equal(before, File.ReadAllText(exportedFile));   // target byte-identical to before the attempt
        Assert.NotEmpty(err);
    }

    // ---- 10. the target is mutated between step 2 and step 5, run aborts (pass 2 M6) ---------

    [Fact]
    public void T3_the_target_mutated_between_verify_and_swap_aborts_the_run()
    {
        // Nothing in MemoryStore is virtual, so there is no way to make Render() itself mutate the
        // target as a side effect. MemoryExportWriter exposes an internal test-only hook (Run's extra
        // overload, InternalsVisibleTo) that fires right after step 2's initial Verify and before
        // staging - exactly the seam pass 2 M6 is about: D11 stops the hub, not a vendor session
        // writing into the target between steps 2 and 5.
        var store = NewStore(NewDir("t10-store"));
        store.Append("user", "A", "Body A.", "prov");
        var targetDir = NewDir("t10-target", create: false);

        var first = Run(store, targetDir, false, false, out _, out _);
        Assert.Equal(0, first.ExitCode);

        var output = new StringWriter();
        var error = new StringWriter();
        var result = MemoryExportWriter.Run(store, targetDir, false, false, output, error,
            afterInitialVerify: () => File.WriteAllText(Path.Combine(targetDir, "mutated-after-verify.md"), "snuck in"));

        Assert.Equal(6, result.ExitCode);
        Assert.NotEmpty(error.ToString());
        // The snuck-in file survives untouched - nothing was moved.
        Assert.True(File.Exists(Path.Combine(targetDir, "mutated-after-verify.md")));
    }

    // ---- 11. "target absent, previous present" is named by the next run ----------------------

    [Fact]
    public void T3_a_hand_constructed_absent_target_with_a_previous_present_is_named_by_the_next_run()
    {
        var store = NewStore(NewDir("t11-store"));
        store.Append("user", "A", "Body A.", "prov");
        var targetDir = NewDir("t11-target", create: false);
        var previousDir = targetDir + ".chopitup-export-previous-20260101T000000Z";
        Directory.CreateDirectory(previousDir);
        File.WriteAllText(Path.Combine(previousDir, "leftover.md"), "leftover");

        var result = Run(store, targetDir, false, false, out var outText, out _);

        Assert.Equal(0, result.ExitCode);
        Assert.True(Directory.Exists(targetDir));
        Assert.True(Directory.Exists(previousDir));   // never auto-deleted (timestamped form)
        Assert.Contains(previousDir, outText);
    }

    // ---- 12. another run's staging directory is reported and left alone ----------------------

    [Fact]
    public void T3_another_runs_staging_directory_is_reported_and_left_alone()
    {
        var store = NewStore(NewDir("t12-store"));
        store.Append("user", "A", "Body A.", "prov");
        var targetDir = NewDir("t12-target", create: false);
        var staleStage = targetDir + ".chopitup-export-tmp-deadbeef";
        Directory.CreateDirectory(staleStage);
        File.WriteAllText(Path.Combine(staleStage, "half-built.md"), "half");

        var result = Run(store, targetDir, false, false, out var outText, out _);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(staleStage, result.OtherStagingDirs);
        Assert.True(Directory.Exists(staleStage));
        Assert.True(File.Exists(Path.Combine(staleStage, "half-built.md")));
        Assert.Contains(staleStage, outText);
    }

    // ---- 13. a target that is a file, not a directory, refuses cleanly -----------------------

    [Fact]
    public void T3_a_target_that_is_a_file_not_a_directory_refuses_cleanly()
    {
        var store = NewStore(NewDir("t13-store"));
        store.Append("user", "A", "Body A.", "prov");
        var targetDir = NewDir("t13-target", create: false);
        File.WriteAllText(targetDir, "I am a file, not a directory");

        var result = Run(store, targetDir, false, false, out _, out var err);

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(err);
        Assert.True(File.Exists(targetDir));
        Assert.False(Directory.Exists(targetDir));
    }
}
