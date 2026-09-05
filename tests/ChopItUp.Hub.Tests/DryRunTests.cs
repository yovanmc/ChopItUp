using ChopItUp.Corpus;
using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Hub.Tests;

/// <summary>The composition <c>tools/Invoke-M2DryRun.ps1</c> proves at full scale (10,000 messages,
/// 500 left in the write-ahead log) against the real, built hub — covered here at a size
/// <c>dotnet test</c> can afford, through the SAME <c>ChopItUp.Corpus</c> builder so the script and
/// the ordinary test run cannot drift (pass 2, MAJOR-9 part 2).</summary>
public sealed class DryRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_dryruntest_" + Guid.NewGuid().ToString("N"));
    private CorpusHandle? _handle;

    [Fact]
    public void Migrating_a_synthetic_v1_corpus_at_reduced_scale_preserves_the_fingerprint()
    {
        _handle = CorpusBuilder.Build(_dir, messages: 1000, rooms: 3, leaveInWal: 200);
        var before = _handle.Fingerprint;
        Assert.Equal(1000, before.Count);
        Assert.NotEmpty(before.Cursors);

        // The writer connection inside `_handle` is still open here on purpose — closing it would
        // checkpoint the WAL-only tail into the main file before EnsureDatabase ever sees it as
        // "still in the WAL", exactly like SchemaMigrationTests' own WAL fixture test.
        var db = new ChopDb(Path.Combine(_dir, CorpusBuilder.DatabaseFileName));
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.True(File.Exists(db.LastBackupPath!));

        var backupFingerprint = CorpusBuilder.FingerprintOf(db.LastBackupPath!);
        Assert.Equal(before.ToCanonicalJson(), backupFingerprint.ToCanonicalJson());

        var migratedFingerprint = CorpusBuilder.FingerprintOf(db.DatabasePath);
        Assert.Equal(before.ToCanonicalJson(), migratedFingerprint.ToCanonicalJson());
    }

    public void Dispose()
    {
        _handle?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
