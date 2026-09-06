using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Tests.Storage;

public sealed class MemoryProposalStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_proposals_" + Guid.NewGuid().ToString("N"));
    private readonly ChopDb _db;
    private readonly MemoryProposalStore _store;

    public MemoryProposalStoreTests()
    {
        _db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        _db.EnsureDatabase();
        _store = new MemoryProposalStore(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A4_Create_stores_a_pending_row_trimmed_and_List_returns_it_in_order()
    {
        var a = _store.Create("general", "opus", "user", "  Likes tests ", " Yes. ", null);
        var b = _store.Create("general", "gpt-6-astra", "project", "Second", "Body", "codex:C:\\x");
        Assert.Equal((1L, "pending", "Likes tests", "Yes.", (string?)null), (a.Id, a.Status, a.Title, a.Body, a.Source));
        Assert.Equal("codex:C:\\x", b.Source);
        Assert.Equal(new[] { 1L, 2L }, _store.List("general").Select(p => p.Id));
        Assert.Equal(new[] { 1L, 2L }, _store.List(null, null).Select(p => p.Id));
        Assert.Empty(_store.List("other"));
        Assert.Equal(a with { }, _store.Get(1)!);
        Assert.Null(_store.Get(99));
    }

    [Fact]
    public void A5_Decide_moves_a_pending_row_once_and_only_once()
    {
        _store.Create("general", "opus", "user", "T", "B", null);
        var approved = _store.Decide(1, MemoryProposalStore.Approved, "topics/user.md", "abc1234");
        Assert.NotNull(approved);
        Assert.Equal(("approved", "topics/user.md", "abc1234"), (approved!.Status, approved.WrittenTo, approved.CommitHash));
        Assert.NotNull(approved.DecidedAt);
        Assert.Null(_store.Decide(1, MemoryProposalStore.Rejected, null, null));   // no longer pending
        Assert.Null(_store.Decide(2, MemoryProposalStore.Approved, null, null));   // unknown
        Assert.Empty(_store.List("general"));
        Assert.Single(_store.List("general", MemoryProposalStore.Approved));
        Assert.Throws<ArgumentException>(() => _store.Decide(1, "pending", null, null));
    }

    [Fact]
    public void A6_Exists_is_keyed_on_author_topic_and_title_and_ignores_rejected_rows()
    {
        _store.Create("general", "claude", "user", "Dup", "B", "claude:C:\\m");
        Assert.True(_store.Exists("claude", "user", "Dup"));
        Assert.True(_store.Exists("claude", "user", " Dup "));
        Assert.False(_store.Exists("codex", "user", "Dup"));
        Assert.False(_store.Exists("claude", "project", "Dup"));
        _store.Decide(1, MemoryProposalStore.Approved, "topics/user.md", null);
        Assert.True(_store.Exists("claude", "user", "Dup"));
        _store.Create("general", "claude", "user", "Gone", "B", "claude:C:\\m");
        _store.Decide(2, MemoryProposalStore.Rejected, null, null);
        Assert.False(_store.Exists("claude", "user", "Gone"));
    }

    [Fact]
    public void A5_RecordWrite_only_touches_an_approved_row_and_DeletePending_only_pending_rows_of_one_source()
    {
        _store.Create("general", "claude", "user", "A", "a", "claude:C:\\m");
        _store.Create("general", "claude", "user", "B", "b", "claude:C:\\m");
        _store.Create("general", "codex", "user", "C", "c", "codex:C:\\n");
        _store.Create("general", "opus", "user", "D", "d", null);
        Assert.Null(_store.RecordWrite(1, "topics/user.md", "abc1234"));            // still pending
        Assert.NotNull(_store.Decide(1, MemoryProposalStore.Approved, null, null));
        Assert.Null(_store.Get(1)!.WrittenTo);                                        // approved, unwritten = replayable
        var written = _store.RecordWrite(1, "topics/user.md", "abc1234")!;
        Assert.Equal(("approved", "topics/user.md", "abc1234"), (written.Status, written.WrittenTo, written.CommitHash));
        Assert.Equal(1, _store.DeletePending("claude:C:\\m"));                     // B only: A is approved
        Assert.Equal(0, _store.DeletePending("claude:C:\\m"));
        Assert.Equal(new[] { 1L, 3L, 4L }, _store.List(null, null).Select(p => p.Id));
    }

    [Fact]
    public void A5_Undecided_lists_pending_rows_and_approved_rows_that_were_never_written()
    {
        _store.Create("general", "opus", "user", "A", "a", null);
        _store.Create("general", "opus", "user", "B", "b", null);
        _store.Create("general", "opus", "user", "C", "c", null);
        _store.Decide(1, MemoryProposalStore.Approved, null, null);                    // crashed before the write
        _store.Decide(2, MemoryProposalStore.Approved, null, null);
        _store.RecordWrite(2, "topics/user.md", "abc1234");                          // finished
        Assert.Equal(new[] { 1L, 3L }, _store.List("general", MemoryProposalStore.Undecided).Select(p => p.Id));
        Assert.Equal(new[] { 3L }, _store.List("general").Select(p => p.Id));
        Assert.Equal(new[] { 1L, 2L }, _store.List("general", MemoryProposalStore.Approved).Select(p => p.Id));
    }

    [Fact]
    public void A4_shape_is_validated_and_an_unknown_room_or_author_is_an_integrity_error()
    {
        Assert.Throws<ArgumentException>(() => _store.Create("general", "opus", "Bad Topic", "T", "B", null));
        Assert.Throws<ArgumentException>(() => _store.Create("general", "opus", "user", "", "B", null));
        Assert.Throws<ArgumentException>(() => _store.Create("general", "opus", "user", "T", new string('b', 4_001), null));
        Assert.Throws<SqliteException>(() => _store.Create("nope", "opus", "user", "T", "B", null));
        Assert.Throws<SqliteException>(() => _store.Create("general", "nobody", "user", "T", "B", null));
        Assert.Empty(_store.List(null, null));
    }
}
