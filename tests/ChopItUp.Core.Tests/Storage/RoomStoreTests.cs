using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Tests.Storage;

public sealed class RoomStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_rooms_" + Guid.NewGuid().ToString("N"));
    private readonly ChopDb _db;
    private readonly MessageStore _store;

    public RoomStoreTests()
    {
        _db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        _db.EnsureDatabase();
        _store = new MessageStore(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void M9_A2_A3_create_stores_the_directory_and_rooms_list_newest_activity_first()
    {
        Thread.Sleep(2);
        var lab = _store.CreateRoom("lab", "  Lab  ", @"C:\Projects\lab");
        Thread.Sleep(2);
        var notes = _store.CreateRoom("notes", "Notes", null);

        Assert.Equal(("lab", "Lab", @"C:\Projects\lab", 0, 0L), (lab.Id, lab.Name, lab.Directory, lab.MessageCount, lab.Unread));
        Assert.Null(lab.ArchivedAt);
        Assert.Equal(lab.CreatedAt, lab.LastActivityAt);
        Assert.Null(notes.Directory);

        Assert.Equal(["notes", "lab", "general"], _store.ListRooms().Select(r => r.Id));   // nothing posted: creation order, newest first

        Thread.Sleep(2);
        var posted = _store.Post("lab", "owner", "hello");
        var rooms = _store.ListRooms();
        Assert.Equal(["lab", "notes", "general"], rooms.Select(r => r.Id));
        Assert.Equal(posted.CreatedAt, rooms[0].LastActivityAt);
        Assert.Equal(1, rooms[0].MessageCount);
        Assert.Equal(posted.Id, rooms[0].LastMessageId);
    }

    [Fact]
    public void M9_A3_A4_archived_rooms_are_hidden_unless_asked_for_and_unarchive_restores()
    {
        _store.CreateRoom("lab", "Lab", @"C:\Projects\lab");
        var at = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        Assert.True(_store.SetArchived("lab", at));
        Assert.Equal(["general"], _store.ListRooms().Select(r => r.Id));
        var all = _store.ListRooms(includeArchived: true);
        Assert.Contains(all, r => r.Id == "lab" && r.ArchivedAt == at);
        Assert.Equal(at, _store.GetRoom("lab")!.ArchivedAt);    // GetRoom never filters
        Assert.True(_store.RoomExists("lab"));                    // archive hides, never blocks

        Assert.True(_store.SetArchived("lab", null));
        Assert.Null(_store.GetRoom("lab")!.ArchivedAt);
        Assert.Equal(2, _store.ListRooms().Count);
        Assert.False(_store.SetArchived("nope", at));
    }

    [Fact]
    public void M9_A5_unread_counts_past_the_askers_cursor_and_the_askers_own_post_clears_it()
    {
        _store.Post("general", "owner", "@opus hi");
        var first = _store.Post("general", "opus", "one");
        _store.Post("general", "opus", "two");

        Assert.Equal(2, _store.ListRooms(unreadFor: "owner").Single().Unread);
        Assert.Equal(0, _store.ListRooms().Single().Unread);                    // nobody asked
        Assert.Equal(2, _store.GetRoom("general", "owner")!.Unread);

        _store.SetCursor("owner", "general", first.Id);
        Assert.Equal(1, _store.ListRooms(unreadFor: "owner").Single().Unread);

        _store.Post("general", "owner", "seen");                                  // Post advances the author's own cursor (claim 5)
        Assert.Equal(0, _store.ListRooms(unreadFor: "owner").Single().Unread);
    }

    [Fact]
    public void M9_A2_A5_duplicate_id_is_refused_and_bind_only_fills_a_null()
    {
        _store.CreateRoom("lab", "Lab", @"C:\Projects\lab");
        var e = Assert.Throws<ArgumentException>(() => _store.CreateRoom("lab", "Lab again", null));
        Assert.Contains("already exists", e.Message);

        Assert.Null(_store.GetRoom("general")!.Directory);
        Assert.True(_store.BindDirectory("general", @"C:\Projects\general"));
        Assert.Equal(@"C:\Projects\general", _store.GetRoom("general")!.Directory);
        Assert.False(_store.BindDirectory("general", @"C:\Elsewhere"));          // already bound
        Assert.Equal(@"C:\Projects\general", _store.GetRoom("general")!.Directory);
        Assert.False(_store.BindDirectory("nope", @"C:\Projects\x"));
        Assert.Throws<ArgumentException>(() => _store.BindDirectory("lab", " "));
    }

    [Theory]
    [InlineData("General", "general")]
    [InlineData("  Résumé Review!  ", "r-sum-review")]
    [InlineData("CON", "con-room")]
    [InlineData("lpt1", "lpt1-room")]
    [InlineData("", "room")]
    [InlineData("--x--", "x")]
    [InlineData("aaaaaaaaaabbbbbbbbbbccccccccccddddddddddeeeeeeeeee", "aaaaaaaaaabbbbbbbbbbccccccccccdddddddddd")]
    public void M9_A2_slugs_are_lowercase_ascii_capped_and_never_a_reserved_device_name(string name, string expected) =>
        Assert.Equal(expected, RoomIds.Slug(name));

    [Fact]
    public void M9_A2_unique_appends_a_counter_until_the_id_is_free()
    {
        var taken = new HashSet<string> { "general", "general-2" };
        Assert.Equal("general-3", RoomIds.Unique("General", taken.Contains));
        Assert.Equal("lab", RoomIds.Unique("Lab", taken.Contains));
    }
}
