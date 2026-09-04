using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Tests.Storage;

public sealed class MessageStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_test_" + Guid.NewGuid().ToString("N"));
    private readonly MessageStore _store;

    public MessageStoreTests()
    {
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        _store = new MessageStore(db);
    }

    [Fact]
    public void Post_returns_monotonic_ids_and_server_timestamp()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var m1 = _store.Post("general", "claude", "first");
        var m2 = _store.Post("general", "codex", "second");
        Assert.True(m2.Id > m1.Id);
        Assert.Equal("claude", m1.AuthorId);
        Assert.InRange(m1.CreatedAt, before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(TimeSpan.Zero, m1.CreatedAt.Offset);
        // Posting advances the author's own cursor past the new message, and only the author's.
        Assert.Equal(m1.Id, _store.GetCursor("claude", "general"));
        Assert.Equal(m2.Id, _store.GetCursor("codex", "general"));
        Assert.Equal(0L, _store.GetCursor("owner", "general"));
    }

    [Fact]
    public void Post_rejects_empty_body_and_unknown_room()
    {
        Assert.Throws<ArgumentException>(() => { _store.Post("general", "claude", "   "); });
        Assert.Throws<SqliteException>(() => { _store.Post("nope", "claude", "x"); });
    }

    [Fact]
    public void Read_pages_in_id_order_with_has_more()
    {
        for (int i = 1; i <= 5; i++) _store.Post("general", "owner", $"m{i}");
        var page1 = _store.Read("general", afterId: 0, limit: 2);
        Assert.Equal(new[] { "m1", "m2" }, page1.Messages.Select(m => m.Body));
        Assert.True(page1.HasMore);
        var page2 = _store.Read("general", page1.NextAfterId, limit: 10);
        Assert.Equal(new[] { "m3", "m4", "m5" }, page2.Messages.Select(m => m.Body));
        Assert.False(page2.HasMore);
        Assert.Equal(page2.Messages[^1].Id, page2.NextAfterId);
        var empty = _store.Read("general", page2.NextAfterId, limit: 10);
        Assert.Empty(empty.Messages);
        Assert.Equal(page2.NextAfterId, empty.NextAfterId);
    }

    [Fact]
    public void Read_clamps_limit_to_max()
    {
        for (int i = 0; i < MessageStore.MaxLimit + 5; i++) _store.Post("general", "owner", "x");
        var page = _store.Read("general", 0, limit: 10_000);
        Assert.Equal(MessageStore.MaxLimit, page.Messages.Count);
        Assert.True(page.HasMore);
        Assert.Equal(MessageStore.MaxLimit + 5L, _store.CountAfter("general", 0));   // COUNT(*) is never capped
        Assert.Equal(5L, _store.CountAfter("general", page.NextAfterId));
    }

    [Fact]
    public void Cursor_defaults_to_zero_and_only_moves_forward()
    {
        Assert.Equal(0L, _store.GetCursor("claude", "general"));
        _store.SetCursor("claude", "general", 7);
        _store.SetCursor("claude", "general", 3);
        Assert.Equal(7L, _store.GetCursor("claude", "general"));
    }

    [Fact]
    public void ListRooms_reports_counts_and_last_id()
    {
        var m = _store.Post("general", "owner", "hello");
        var rooms = _store.ListRooms();
        var general = Assert.Single(rooms);
        Assert.Equal("general", general.Id);
        Assert.Equal(1, general.MessageCount);
        Assert.Equal(m.Id, general.LastMessageId);
        Assert.True(_store.RoomExists("general"));
        Assert.False(_store.RoomExists("nope"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
