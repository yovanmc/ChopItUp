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

    [Fact]
    public void A4_same_client_key_twice_stores_one_message_and_returns_the_original()
    {
        var first = _store.Post("general", "claude", "first body", "k1");
        var second = _store.Post("general", "claude", "different body", "k1");

        Assert.False(first.Deduplicated);
        Assert.True(second.Deduplicated);
        Assert.Equal(first.Message.Id, second.Message.Id);
        Assert.Equal("first body", second.Message.Body);
        Assert.Single(_store.Read("general", 0, 50).Messages);
    }

    [Fact]
    public void A4_the_same_key_from_a_different_author_or_room_is_a_different_message()
    {
        var byClaude = _store.Post("general", "claude", "hi", "k1");
        var byCodex = _store.Post("general", "codex", "hi", "k1");
        Assert.NotEqual(byClaude.Message.Id, byCodex.Message.Id);
        Assert.False(byCodex.Deduplicated);

        // A second room, inserted directly.
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "chopitup.db")};Mode=ReadWrite;Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO rooms (id, name, created_at) VALUES ('second', 'Second', $at)";
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        var inOtherRoom = _store.Post("second", "claude", "hi", "k1");
        Assert.NotEqual(byClaude.Message.Id, inOtherRoom.Message.Id);
        Assert.False(inOtherRoom.Deduplicated);
    }

    [Fact]
    public void A5_posts_without_a_key_are_never_deduplicated()
    {
        var r1 = _store.Post("general", "claude", "same body", null);
        var r2 = _store.Post("general", "claude", "same body", null);
        var r3 = _store.Post("general", "claude", "same body", null);
        Assert.False(r1.Deduplicated);
        Assert.False(r2.Deduplicated);
        Assert.False(r3.Deduplicated);
        Assert.Equal(3, new[] { r1.Message.Id, r2.Message.Id, r3.Message.Id }.Distinct().Count());
    }

    [Fact]
    public void Client_key_is_trimmed_and_blank_is_treated_as_absent()
    {
        var blankSpace = _store.Post("general", "claude", "a", "   ");
        var empty = _store.Post("general", "claude", "b", "");
        Assert.False(blankSpace.Deduplicated);
        Assert.False(empty.Deduplicated);

        var withSpaces = _store.Post("general", "claude", "c", " k1 ");
        var bare = _store.Post("general", "claude", "d", "k1");
        Assert.False(withSpaces.Deduplicated);
        Assert.True(bare.Deduplicated);
        Assert.Equal(withSpaces.Message.Id, bare.Message.Id);
    }

    [Fact]
    public void Client_key_over_the_cap_is_rejected()
    {
        var tooLong = new string('x', MessageStore.MaxClientKeyChars + 1);
        Assert.Throws<ArgumentException>(() => _store.Post("general", "claude", "x", tooLong));
    }

    [Fact]
    public void Deduplicated_post_does_not_move_the_author_cursor_backwards()
    {
        _store.Post("general", "claude", "k1 message", "k1");
        _store.Post("general", "claude", "another", null);   // advances the cursor further
        var cursorBefore = _store.GetCursor("claude", "general");

        _store.Post("general", "claude", "k1 message retried", "k1");

        Assert.Equal(cursorBefore, _store.GetCursor("claude", "general"));
    }

    [Fact]
    public void A_keyed_post_to_an_unknown_room_still_surfaces_the_integrity_error()
    {
        var ex = Assert.Throws<SqliteException>(() => _store.Post("nope", "claude", "x", "k1"));
        Assert.Equal(787, ex.SqliteExtendedErrorCode);
    }

    [Fact]
    public async Task A4_two_racing_retries_collapse_to_one_message()
    {
        // The loser of the race can also come back as SQLITE_BUSY (code 5, extended 5 or 261)
        // rather than 2067, because busy_timeout=5000 is the only thing holding it open long
        // enough for the retry to complete; the 2067 filter does not catch that case. If any of
        // the 10 iterations below produces a SqliteException with code 5, that is a STOP-and-report
        // per the dispatch: the fix would be a bounded retry around the insert, and whether that
        // belongs in M2 is the orchestrator's call, not mine.
        for (int i = 0; i < 10; i++)
        {
            var key = $"race-{i}-{Guid.NewGuid():N}";
            using var barrier = new Barrier(2);

            var t1 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return _store.Post("general", "claude", "same body", key);
            });
            var t2 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return _store.Post("general", "claude", "same body", key);
            });

            var results = await Task.WhenAll(t1, t2);

            Assert.Equal(results[0].Message.Id, results[1].Message.Id);
            Assert.Single(results, r => !r.Deduplicated);
            Assert.Equal(1, _store.Read("general", 0, 200).Messages.Count(m => m.Id == results[0].Message.Id));
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
