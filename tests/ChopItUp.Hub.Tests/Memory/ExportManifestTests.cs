using ChopItUp.Core.Memory;
using ChopItUp.Hub.Memory;

namespace ChopItUp.Hub.Tests.Memory;

public sealed class ExportManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chopitup_manifest_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _cleanup = [];

    private string NewDir(string suffix)
    {
        var dir = Path.Combine(_root, suffix);
        Directory.CreateDirectory(dir);
        _cleanup.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _cleanup) TestDirs.DeleteTree(dir);
        TestDirs.DeleteTree(_root);
    }

    /// <summary>Renders <paramref name="store"/>, writes every file into <paramref name="targetDir"/>
    /// and writes a manifest describing exactly what was written — the same shape T3's real writer
    /// will produce, kept small here because T2 tests only <see cref="ExportManifest"/> itself.</summary>
    private static ExportManifest WriteExport(MemoryStore store, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var plan = MemoryExport.Render(store);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in plan.Files)
        {
            var path = Path.Combine(targetDir, f.FileName);
            File.WriteAllText(path, f.Text);
            files[f.FileName] = ExportManifest.HashFile(path);
        }
        var manifest = new ExportManifest(1, store.Root, ExportManifest.Fingerprint(store), DateTime.UtcNow.ToString("O"), files);
        ExportManifest.Write(targetDir, manifest);
        return manifest;
    }

    private static MemoryStore NewStore(string dir)
    {
        Directory.CreateDirectory(dir);
        return new MemoryStore(dir);
    }

    // ---- round trip -------------------------------------------------------

    [Fact]
    public void T2_a_manifest_round_trips()
    {
        var storeDir = NewDir("store1");
        var targetDir = NewDir("target1");
        var store = NewStore(storeDir);
        store.Append("user", "A", "Body A.", "prov");

        var written = WriteExport(store, targetDir);
        var read = ExportManifest.TryRead(targetDir);

        Assert.NotNull(read);
        Assert.Equal(written.Version, read!.Version);
        Assert.Equal(written.SourceRoot, read.SourceRoot);
        Assert.Equal(written.SourceFingerprint, read.SourceFingerprint);
        Assert.Equal(written.ExportedAt, read.ExportedAt);
        Assert.Equal(written.Files, read.Files);
    }

    // ---- TryRead never throws ---------------------------------------------

    [Fact]
    public void T2_TryRead_returns_null_on_a_missing_file()
    {
        var targetDir = NewDir("missing");
        Assert.Null(ExportManifest.TryRead(targetDir));
    }

    [Fact]
    public void T2_TryRead_returns_null_on_truncated_json()
    {
        var targetDir = NewDir("truncated");
        File.WriteAllText(Path.Combine(targetDir, ExportManifest.FileName), "{ \"Version\": 1, \"SourceRoot\": \"C:");
        Assert.Null(ExportManifest.TryRead(targetDir));
    }

    [Fact]
    public void T2_TryRead_returns_null_on_an_empty_object()
    {
        var targetDir = NewDir("empty-object");
        File.WriteAllText(Path.Combine(targetDir, ExportManifest.FileName), "{}");
        Assert.Null(ExportManifest.TryRead(targetDir));
    }

    [Fact]
    public void T2_TryRead_returns_null_on_a_newer_version()
    {
        var targetDir = NewDir("newer-version");
        var json = $"{{\"Version\": {ExportManifest.CurrentVersion + 1}, \"SourceRoot\": \"C:\\\\store\", " +
                   "\"SourceFingerprint\": \"abc\", \"ExportedAt\": \"2026-01-01T00:00:00Z\", \"Files\": {}}";
        File.WriteAllText(Path.Combine(targetDir, ExportManifest.FileName), json);
        Assert.Null(ExportManifest.TryRead(targetDir));
    }

    [Fact]
    public void T2_TryRead_returns_null_on_a_missing_field()
    {
        var targetDir = NewDir("missing-field");
        // No SourceRoot.
        var json = "{\"Version\": 1, \"SourceFingerprint\": \"abc\", \"ExportedAt\": \"2026-01-01T00:00:00Z\", \"Files\": {}}";
        File.WriteAllText(Path.Combine(targetDir, ExportManifest.FileName), json);
        Assert.Null(ExportManifest.TryRead(targetDir));
    }

    [Fact]
    public void T2_a_hand_written_v1_manifest_still_reads()
    {
        // Raw JSON string literal, never produced by ExportManifest.Write — the schema-evolution
        // guard (pass 2 M8): an older-shape record must still READ, not be treated as unreadable.
        var targetDir = NewDir("v1-fixture");
        const string v1Json = """
            {
              "Version": 1,
              "SourceRoot": "C:\\owner\\memory",
              "SourceFingerprint": "deadbeef",
              "ExportedAt": "2026-01-01T00:00:00Z",
              "Files": {
                "user-a.md": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd"
              }
            }
            """;
        File.WriteAllText(Path.Combine(targetDir, ExportManifest.FileName), v1Json);

        var read = ExportManifest.TryRead(targetDir);

        Assert.NotNull(read);
        Assert.Equal(1, read!.Version);
        Assert.Equal("C:\\owner\\memory", read.SourceRoot);
        Assert.Equal("deadbeef", read.SourceFingerprint);
        Assert.Equal("2026-01-01T00:00:00Z", read.ExportedAt);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd", read.Files["user-a.md"]);
    }

    // ---- Fingerprint: report-only, never a refusal predicate ---------------

    [Fact]
    public void T2_Fingerprint_is_stable_across_calls()
    {
        var store = NewStore(NewDir("fp-stable"));
        store.Append("user", "A", "Body A.", "prov");

        Assert.Equal(ExportManifest.Fingerprint(store), ExportManifest.Fingerprint(store));
    }

    [Fact]
    public void T2_Fingerprint_changes_on_an_edited_body()
    {
        var store = NewStore(NewDir("fp-edit"));
        store.Append("user", "A", "Original body.", "prov");
        var before = ExportManifest.Fingerprint(store);

        store.Supersede("user", "A", "A", "Edited body.", "prov");

        Assert.NotEqual(before, ExportManifest.Fingerprint(store));
    }

    [Fact]
    public void T2_Fingerprint_changes_on_an_added_entry()
    {
        var store = NewStore(NewDir("fp-add"));
        store.Append("user", "A", "Body A.", "prov");
        var before = ExportManifest.Fingerprint(store);

        store.Append("user", "B", "Body B.", "prov");

        Assert.NotEqual(before, ExportManifest.Fingerprint(store));
    }

    [Fact]
    public void T2_Fingerprint_does_not_change_when_only_a_provenance_comment_differs()
    {
        var storeA = NewStore(NewDir("fp-prov-a"));
        storeA.Append("user", "A", "Body A.", "prov: room one, proposal 1");

        var storeB = NewStore(NewDir("fp-prov-b"));
        storeB.Append("user", "A", "Body A.", "prov: room two, proposal 99");

        Assert.Equal(ExportManifest.Fingerprint(storeA), ExportManifest.Fingerprint(storeB));
    }

    // ---- Verify: the six states ---------------------------------------------

    [Fact]
    public void T2_Verify_is_Absent_for_a_missing_directory()
    {
        var store = NewStore(NewDir("verify-absent-store"));
        var targetDir = Path.Combine(_root, "does-not-exist-" + Guid.NewGuid().ToString("N"));

        var verdict = ExportManifest.Verify(null, targetDir, store);

        Assert.Equal(TargetState.Absent, verdict.State);
        Assert.Empty(verdict.Paths);
    }

    [Fact]
    public void T2_Verify_is_Absent_for_an_empty_directory()
    {
        var store = NewStore(NewDir("verify-absent-store2"));
        var targetDir = NewDir("verify-absent-target2");

        var verdict = ExportManifest.Verify(null, targetDir, store);

        Assert.Equal(TargetState.Absent, verdict.State);
    }

    [Fact]
    public void T2_Verify_is_Foreign_for_a_populated_directory_with_no_manifest()
    {
        var store = NewStore(NewDir("verify-foreign-store"));
        var targetDir = NewDir("verify-foreign-target");
        File.WriteAllText(Path.Combine(targetDir, "someone-elses-file.md"), "not ours");

        var verdict = ExportManifest.Verify(null, targetDir, store);

        Assert.Equal(TargetState.Foreign, verdict.State);
        Assert.Contains("someone-elses-file.md", verdict.Paths);
    }

    [Fact]
    public void T2_Verify_is_Unreadable_when_the_manifest_file_is_present_but_corrupt()
    {
        var store = NewStore(NewDir("verify-unreadable-store"));
        var targetDir = NewDir("verify-unreadable-target");
        File.WriteAllText(Path.Combine(targetDir, ExportManifest.FileName), "not json at all {{{");
        var manifest = ExportManifest.TryRead(targetDir);
        Assert.Null(manifest);   // sanity: TryRead itself already returned null

        var verdict = ExportManifest.Verify(manifest, targetDir, store);

        Assert.Equal(TargetState.Unreadable, verdict.State);
    }

    [Fact]
    public void T2_Verify_returns_DifferentSource_for_two_stores_with_identical_entries_at_different_roots_and_every_hash_matched()
    {
        var storeA = NewStore(NewDir("verify-diffsrc-a"));
        storeA.Append("user", "A", "Same body.", "prov");
        var storeB = NewStore(NewDir("verify-diffsrc-b"));
        storeB.Append("user", "A", "Same body.", "prov");

        var targetDir = NewDir("verify-diffsrc-target");
        var manifest = WriteExport(storeA, targetDir);

        // storeB's entries are byte-identical to storeA's, so nothing about the FILES distinguishes
        // them (D4) — only the manifest's SourceRoot, bound to storeA, does.
        var verdict = ExportManifest.Verify(manifest, targetDir, storeB);

        Assert.Equal(TargetState.DifferentSource, verdict.State);
        Assert.Empty(verdict.Paths);   // every file hash matched — the attack this guards against
    }

    [Fact]
    public void T2_Verify_returns_Clean_when_the_root_matches_and_the_store_changed_since_export()
    {
        var store = NewStore(NewDir("verify-clean-changed-store"));
        store.Append("user", "A", "Body A.", "prov");
        var targetDir = NewDir("verify-clean-changed-target");
        var manifest = WriteExport(store, targetDir);

        // The store legitimately changed after the export — a content mismatch must never refuse.
        store.Append("user", "B", "Body B.", "prov");

        var verdict = ExportManifest.Verify(manifest, targetDir, store);

        Assert.Equal(TargetState.Clean, verdict.State);
        Assert.Empty(verdict.Paths);
    }

    [Fact]
    public void T2_DifferentSource_carries_a_non_empty_drift_list_when_the_target_is_also_drifted()
    {
        var storeA = NewStore(NewDir("verify-diffsrc-drift-a"));
        storeA.Append("user", "A", "Body A.", "prov");
        var storeB = NewStore(NewDir("verify-diffsrc-drift-b"));
        storeB.Append("user", "B", "Body B.", "prov");

        var targetDir = NewDir("verify-diffsrc-drift-target");
        var manifest = WriteExport(storeA, targetDir);
        // Tamper with the target beyond just the source mismatch.
        File.WriteAllText(Path.Combine(targetDir, "extra-file.md"), "not part of either export");

        var verdict = ExportManifest.Verify(manifest, targetDir, storeB);

        Assert.Equal(TargetState.DifferentSource, verdict.State);
        Assert.Contains("extra-file.md", verdict.Paths);
    }

    [Fact]
    public void T2_Verify_is_Drifted_naming_an_added_a_deleted_an_edited_and_a_file_in_a_subdirectory()
    {
        var store = NewStore(NewDir("verify-drift-store"));
        var targetDir = NewDir("verify-drift-target");
        Directory.CreateDirectory(Path.Combine(targetDir, "sub"));

        File.WriteAllText(Path.Combine(targetDir, "keep.md"), "keep me");
        File.WriteAllText(Path.Combine(targetDir, "delete-me.md"), "will be deleted");
        File.WriteAllText(Path.Combine(targetDir, "sub", "edit-me.md"), "original content");

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["keep.md"] = ExportManifest.HashFile(Path.Combine(targetDir, "keep.md")),
            ["delete-me.md"] = ExportManifest.HashFile(Path.Combine(targetDir, "delete-me.md")),
            ["sub/edit-me.md"] = ExportManifest.HashFile(Path.Combine(targetDir, "sub", "edit-me.md")),
        };
        var manifest = new ExportManifest(1, store.Root, "fp", DateTime.UtcNow.ToString("O"), files);
        ExportManifest.Write(targetDir, manifest);

        // Now drift the target: delete one file, edit the nested one, add an unlisted one.
        File.Delete(Path.Combine(targetDir, "delete-me.md"));
        File.WriteAllText(Path.Combine(targetDir, "sub", "edit-me.md"), "edited content");
        File.WriteAllText(Path.Combine(targetDir, "added.md"), "brand new");

        var verdict = ExportManifest.Verify(manifest, targetDir, store);

        Assert.Equal(TargetState.Drifted, verdict.State);
        Assert.Contains("delete-me.md", verdict.Paths);
        Assert.Contains("sub/edit-me.md", verdict.Paths);
        Assert.Contains("added.md", verdict.Paths);
        Assert.DoesNotContain("keep.md", verdict.Paths);
    }

    [Fact]
    public void T2_Verify_is_Clean_for_an_untouched_export_including_one_whose_directory_holds_the_manifest_itself()
    {
        var store = NewStore(NewDir("verify-clean-store"));
        store.Append("user", "A", "Body A.", "prov");
        store.Append("feedback", "B", "Body B.", "prov");
        var targetDir = NewDir("verify-clean-target");
        var manifest = WriteExport(store, targetDir);

        // Sanity: the manifest file really is sitting inside targetDir alongside the exported files.
        Assert.True(File.Exists(Path.Combine(targetDir, ExportManifest.FileName)));

        var verdict = ExportManifest.Verify(manifest, targetDir, store);

        Assert.Equal(TargetState.Clean, verdict.State);
        Assert.Empty(verdict.Paths);
    }
}
