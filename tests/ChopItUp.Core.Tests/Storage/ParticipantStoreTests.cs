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

    private ParticipantStore MakeStore(out ChopDb db)
    {
        db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        return new ParticipantStore(db);
    }

    private MessageStore MakeRooms(ChopDb db)
    {
        var rooms = new MessageStore(db);
        rooms.CreateRoom("lab", "Lab", null);
        return rooms;
    }

    [Fact]
    public void EffectiveRole_returns_global_when_no_override_exists()
    {
        var store = MakeStore(out var db);
        MakeRooms(db);
        Assert.True(store.SetRole("opus", "Reviewer"));

        Assert.Equal("Reviewer", store.EffectiveRole("lab", "opus"));
    }

    [Fact]
    public void EffectiveRole_returns_the_override_and_not_the_global_when_one_exists()
    {
        var store = MakeStore(out var db);
        MakeRooms(db);
        Assert.True(store.SetRole("opus", "Global reviewer"));
        Assert.True(store.SetRoomRole("lab", "opus", "Lab-only architect"));

        var effective = store.EffectiveRole("lab", "opus");
        Assert.Equal("Lab-only architect", effective);
        Assert.NotEqual("Global reviewer", effective);
    }

    [Fact]
    public void EffectiveRole_is_null_when_neither_global_nor_override_exists()
    {
        var store = MakeStore(out var db);
        MakeRooms(db);

        Assert.Null(store.EffectiveRole("lab", "opus"));
    }

    [Fact]
    public void SetRole_on_owner_returns_false_and_stores_nothing()
    {
        var store = MakeStore(out _);

        Assert.False(store.SetRole("owner", "Should not stick"));
        Assert.Null(store.GlobalRole("owner"));
    }

    [Fact]
    public void SetRole_on_claude_returns_false_and_stores_nothing_because_it_is_kind_model_with_a_null_model()
    {
        // Row 14's named trap: claude and codex are kind 'model' with model = NULL — app-backed
        // windows the hub never spawns. `kind = 'model'` alone would wrongly accept this id.
        var store = MakeStore(out _);

        Assert.False(store.SetRole("claude", "Should not stick"));
        Assert.Null(store.GlobalRole("claude"));
    }

    [Fact]
    public void SetRole_with_empty_string_clears_an_existing_role_to_null()
    {
        var store = MakeStore(out _);
        Assert.True(store.SetRole("opus", "Reviewer"));

        Assert.True(store.SetRole("opus", ""));

        Assert.Null(store.GlobalRole("opus"));
    }

    [Fact]
    public void SetRole_over_the_cap_throws_and_stores_nothing()
    {
        var store = MakeStore(out _);
        var tooLong = new string('x', ParticipantStore.MaxRoleChars + 1);

        Assert.Throws<ArgumentException>(() => store.SetRole("opus", tooLong));
        Assert.Null(store.GlobalRole("opus"));
    }

    [Fact]
    public void ClearRoomRole_succeeds_even_when_no_override_existed_and_falls_back_to_global()
    {
        var store = MakeStore(out var db);
        MakeRooms(db);
        Assert.True(store.SetRole("opus", "Global reviewer"));

        Assert.True(store.ClearRoomRole("lab", "opus"));

        Assert.Equal("Global reviewer", store.EffectiveRole("lab", "opus"));
    }

    [Fact]
    public void SetRoomRole_with_empty_string_stores_the_suppress_sentinel_not_the_global()
    {
        // D-b's fourth state: a room can say "no role here" without deleting the override row.
        // A SetRoomRole that turns "" into a DELETE is the defect this test binds.
        var store = MakeStore(out var db);
        MakeRooms(db);
        Assert.True(store.SetRole("opus", "Global reviewer"));

        Assert.True(store.SetRoomRole("lab", "opus", ""));

        Assert.Equal("", store.EffectiveRole("lab", "opus"));
    }

    [Fact]
    public void SetRoomRole_twice_upserts_rather_than_throwing()
    {
        var store = MakeStore(out var db);
        MakeRooms(db);

        Assert.True(store.SetRoomRole("lab", "opus", "First"));
        Assert.True(store.SetRoomRole("lab", "opus", "Second"));

        Assert.Equal("Second", store.RoomRole("lab", "opus"));
    }

    [Fact]
    public void List_round_trips_a_stored_role()
    {
        var store = MakeStore(out _);
        Assert.True(store.SetRole("opus", "Reviewer"));

        Assert.Equal("Reviewer", store.List().Single(p => p.Id == "opus").Role);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
