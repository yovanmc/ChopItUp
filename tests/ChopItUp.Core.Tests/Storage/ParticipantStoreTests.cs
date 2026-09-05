using ChopItUp.Core.Storage;

namespace ChopItUp.Core.Tests.Storage;

public sealed class ParticipantStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_roster_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void List_returns_the_seed_roster_in_seed_order_and_HumanId_is_owner()
    {
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        var store = new ParticipantStore(db);

        Assert.Equal(ChopDb.SeedRoster, store.List());
        Assert.Equal("owner", store.HumanId());
    }

    [Fact]
    public void HumanId_throws_when_the_roster_has_two_humans()
    {
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO participants (id, display_name, kind, host) VALUES ('guest','Guest','human','human')";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new ParticipantStore(db).HumanId());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
