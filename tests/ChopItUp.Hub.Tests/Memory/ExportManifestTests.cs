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

    /// <summary>Manifest text that must read as null. A null text means no manifest file at all.</summary>
    public static TheoryData<string, string?> UnreadableManifests => new()
    {
        { "missing file", null },
        { "truncated json", "{ \"Version\": 1, \"SourceRoot\": \"C:" },
        { "empty object", "{}" },
        { "newer version", $"{{\"Version\": {ExportManifest.CurrentVersion + 1}, \"SourceRoot\": \"C:\\\\store\", " +
                           "\"SourceFingerprint\": \"abc\", \"ExportedAt\": \"2026-01-01T00:00:00Z\", \"Files\": {}}" },
        { "missing field (no SourceRoot)", "{\"Version\": 1, \"SourceFingerprint\": \"abc\", \"ExportedAt\": \"2026-01-01T00:00:00Z\", \"Files\": {}}" },
    };

    [Theory]
    [MemberData(nameof(UnreadableManifests))]
    public void T2_TryRead_returns_null_on_an_unreadable_manifest(string @case, string? manifestJson)
    {
        _ = @case;
        var targetDir = NewDir("unreadable");
        if (manifestJson is not null) File.WriteAllText(Path.Combine(targetDir, ExportManifest.FileName), manifestJson);
        Assert.Null(ExportManifest.TryRead(targetDir));
    }

    [Fact]
    public void T2_a_hand_written_v1_manifest_still_reads()
    {
        // Raw JSON string literal, never produced by ExportManifest.Write: the schema-evolution
        // guard. An older-shape record must still READ, not be treated as unreadable.
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

    public static TheoryData<string, Action<MemoryStore>> FingerprintChanges => new()
    {
        { "Original body.", store => store.Supersede("user", "A", "A", "Edited body.", "prov") },   // an edited body
        { "Body A.", store => store.Append("user", "B", "Body B.", "prov") },                       // an added entry
    };

    [Theory]
    [MemberData(nameof(FingerprintChanges))]
    public void T2_Fingerprint_changes_on_an_edited_body_or_an_added_entry(string initialBody, Action<MemoryStore> change)
    {
        var store = NewStore(NewDir("fp-change"));
        store.Append("user", "A", initialBody, "prov");
        var before = ExportManifest.Fingerprint(store);

        change(store);

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

    [Theory]
    [InlineData(false)]   // the directory does not exist
    [InlineData(true)]    // the directory exists and is empty
    public void T2_Verify_is_Absent_for_a_missing_or_empty_directory(bool directoryExists)
    {
        var store = NewStore(NewDir("verify-absent-store"));
        var targetDir = directoryExists
            ? NewDir("verify-absent-target")
            : Path.Combine(_root, "does-not-exist-" + Guid.NewGuid().ToString("N"));

        var verdict = ExportManifest.Verify(null, targetDir, store);

        Assert.Equal(TargetState.Absent, verdict.State);
        Assert.Empty(verdict.Paths);
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

    [Theory]
    [InlineData(false)]   // a directory holding ONLY a corrupt manifest legitimately reports an empty list (carried-over fix from the T2 review): there is nothing else in there to name
    [InlineData(true)]    // the unparseable-manifest refusal must list every affected path by recursive relative path, not an empty Paths list regardless of what else is in the directory
    public void T2_Verify_is_Unreadable_when_the_manifest_file_is_present_but_corrupt_and_names_the_real_files_beside_it(bool withRealFiles)
    {
        var store = NewStore(NewDir("verify-unreadable-store"));
        var targetDir = NewDir("verify-unreadable-target");
        File.WriteAllText(Path.Combine(targetDir, ExportManifest.FileName), "not json at all {{{");
        if (withRealFiles)
        {
            Directory.CreateDirectory(Path.Combine(targetDir, "sub"));
            File.WriteAllText(Path.Combine(targetDir, "real-file.md"), "real content");
            File.WriteAllText(Path.Combine(targetDir, "sub", "nested.md"), "nested content");
        }
        var manifest = ExportManifest.TryRead(targetDir);
        Assert.Null(manifest);   // sanity: TryRead itself already returned null

        var verdict = ExportManifest.Verify(manifest, targetDir, store);

        Assert.Equal(TargetState.Unreadable, verdict.State);
        if (withRealFiles)
        {
            Assert.Contains("real-file.md", verdict.Paths);
            Assert.Contains("sub/nested.md", verdict.Paths);
        }
        else
        {
            Assert.Empty(verdict.Paths);
        }
        Assert.DoesNotContain(ExportManifest.FileName, verdict.Paths);
    }

    [Theory]
    [InlineData("A", "Same body.", false)]   // identical entries at a different root: nothing about the FILES distinguishes the stores, only the manifest's SourceRoot does
    [InlineData("B", "Body B.", true)]       // a different store whose target is also drifted
    public void T2_Verify_returns_DifferentSource_for_a_store_at_a_different_root_and_names_a_drifted_file(string titleB, string bodyB, bool targetDrifted)
    {
        var storeA = NewStore(NewDir("verify-diffsrc-a"));
        storeA.Append("user", "A", "Same body.", "prov");
        var storeB = NewStore(NewDir("verify-diffsrc-b"));
        storeB.Append("user", titleB, bodyB, "prov");

        var targetDir = NewDir("verify-diffsrc-target");
        var manifest = WriteExport(storeA, targetDir);
        // Tamper with the target beyond just the source mismatch.
        if (targetDrifted) File.WriteAllText(Path.Combine(targetDir, "extra-file.md"), "not part of either export");

        var verdict = ExportManifest.Verify(manifest, targetDir, storeB);

        Assert.Equal(TargetState.DifferentSource, verdict.State);
        if (targetDrifted) Assert.Contains("extra-file.md", verdict.Paths);
        else Assert.Empty(verdict.Paths);   // every file hash matched — the attack this guards against
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
