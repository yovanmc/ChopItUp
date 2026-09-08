using ChopItUp.Core.Storage;

namespace ChopItUp.Core.Tests.Storage;

public sealed class ParticipantStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_roster_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void List_returns_the_seed_roster_in_seed_order_and_OwnerId_is_owner()
    {
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        var store = new ParticipantStore(db);

        Assert.Equal(ChopDb.SeedRoster, store.List());
        Assert.Equal("owner", store.OwnerId());
    }

    [Fact]
    public void OwnerId_is_owner_even_though_the_roster_has_two_humans()
    {
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO participants (id, display_name, kind, host) VALUES ('guest','Guest','human','human')";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal("owner", new ParticipantStore(db).OwnerId());
    }

    [Fact]
    public void OwnerId_throws_when_the_owner_row_is_absent()
    {
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM participants WHERE id = 'owner'";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new ParticipantStore(db).OwnerId());
    }

    [Fact]
    public void HumanIds_lists_owner_and_owner_remote()
    {
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();

        Assert.Equal(new[] { "owner", "owner-remote" }, new ParticipantStore(db).HumanIds());
    }

    [Fact]
    public void SetClasses_normalizes_and_persists()
    {
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        var store = new ParticipantStore(db);

        Assert.True(store.SetClasses("gpt-5.4-mini", "Judge, plumbing, judge, bogus"));
        Assert.Equal("plumbing,judge", store.List().Single(p => p.Id == "gpt-5.4-mini").Classes);

        Assert.True(store.SetClasses("gpt-5.4-mini", ""));
        Assert.Null(store.List().Single(p => p.Id == "gpt-5.4-mini").Classes);

        Assert.False(store.SetClasses("mallory", "judge"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
