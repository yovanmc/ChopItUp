using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Tests.Storage;

public sealed class ChopDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_test_" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "chopitup.db");

    [Fact]
    public void EnsureDatabase_creates_the_current_schema_with_seed_rows()
    {
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.True(File.Exists(DbPath));
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.Equal(
            ChopDb.SeedRoster.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            Scalar<string>("SELECT group_concat(id, ',') FROM (SELECT id FROM participants ORDER BY id)").Split(','));
        Assert.Equal("general", Scalar<string>("SELECT id FROM rooms"));
        Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM messages"));
        Assert.EndsWith("+00:00", Scalar<string>("SELECT created_at FROM rooms"));   // one timestamp writer (Timestamps.Stamp), never strftime
    }

    [Fact]
    public void EnsureDatabase_is_idempotent()
    {
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();
        db.EnsureDatabase();
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.Equal((long)ChopDb.SeedRoster.Count, Scalar<long>("SELECT COUNT(*) FROM participants"));
    }

    [Fact]
    public void EnsureDatabase_repairs_tables_without_a_version_stamp_instead_of_crashing()
    {
        // Simulates a DB whose create-step was interrupted between the DDL and the stamp (or written by
        // a pre-fix build): tables exist, user_version is 0. Must not throw "table already exists".
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();
        using (var conn = db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 0;";
            cmd.ExecuteNonQuery();
        }
        db.EnsureDatabase();
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.Equal((long)ChopDb.SeedRoster.Count, Scalar<long>("SELECT COUNT(*) FROM participants"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM rooms"));
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
    }

    [Fact]
    public void Foreign_keys_are_enforced()
    {
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO messages (room_id, author_id, body, created_at) VALUES ('nope', 'claude', 'x', '2026-01-01T00:00:00.000Z')";
        Assert.Throws<SqliteException>(() => { cmd.ExecuteNonQuery(); });
    }

    private T Scalar<T>(string sql)
    {
        var db = new ChopDb(DbPath);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
