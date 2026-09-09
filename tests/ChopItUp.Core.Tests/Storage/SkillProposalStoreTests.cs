using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Tests.Storage;

/// <summary>Ticket 04 / plan Task 4: <c>skill_proposals</c> row lifecycle - add, fetch, list by
/// status, the undecided dedup lookup, and the three decision stamps. Mirrors
/// <see cref="MemoryProposalStoreTests"/>'s shape, including memory's definition of
/// <see cref="MemoryProposalStore.Undecided"/> as pending union approved-with-nothing-written, which
/// here is pending union approved-with-<c>installed_at</c>-NULL.</summary>
public sealed class SkillProposalStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_skillproposals_" + Guid.NewGuid().ToString("N"));
    private readonly ChopDb _db;
    private readonly SkillProposalStore _store;

    public SkillProposalStoreTests()
    {
        _db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        _db.EnsureDatabase();
        _store = new SkillProposalStore(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Add_stores_a_pending_row_and_List_returns_undecided_rows_in_stable_order()
    {
        var a = _store.Add("general", "opus", "demo", @"C:\rooms\general\demo", "aaa...", replacesInstalled: false, force: false, files: 2, bytes: 100);
        var b = _store.Add("general", "codex", "other", @"C:\rooms\general\other", "bbb...", replacesInstalled: true, force: true, files: 1, bytes: 40);

        Assert.Equal((1L, "general", "opus", "demo", "pending", false, false, 2, 100L),
            (a.Id, a.RoomId, a.AuthorId, a.Name, a.Status, a.ReplacesInstalled, a.Force, a.Files, a.Bytes));
        Assert.True(b.ReplacesInstalled);
        Assert.True(b.Force);
        Assert.Equal(new[] { 1L, 2L }, _store.List("general", SkillProposalStore.Undecided).Select(p => p.Id));
        Assert.Equal(a with { }, _store.Get(1)!);
        Assert.Null(_store.Get(99));
    }

    [Fact]
    public void List_can_filter_to_approved_rejected_or_all_and_is_scoped_to_the_room()
    {
        var a = _store.Add("general", "opus", "a", @"C:\rooms\general\a", "aaa", false, false, 1, 10);
        var b = _store.Add("general", "opus", "b", @"C:\rooms\general\b", "bbb", false, false, 1, 10);
        var c = _store.Add("general", "opus", "c", @"C:\rooms\general\c", "ccc", false, false, 1, 10);
        Assert.NotNull(_store.MarkApproved(a.Id));
        Assert.NotNull(_store.MarkRejected(b.Id));

        Assert.Equal(new[] { a.Id }, _store.List("general", SkillProposalStore.Approved).Select(p => p.Id));
        Assert.Equal(new[] { b.Id }, _store.List("general", SkillProposalStore.Rejected).Select(p => p.Id));
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, _store.List("general", null).Select(p => p.Id));
        Assert.Empty(_store.List("other-room", null));
    }

    [Fact]
    public void FindPending_locates_an_undecided_proposal_for_the_same_name_and_tree_without_scanning()
    {
        var a = _store.Add("general", "opus", "demo", @"C:\rooms\general\demo", "sha-1", false, false, 1, 10);

        Assert.Equal(a.Id, _store.FindPending("demo", "sha-1")!.Id);
        Assert.Null(_store.FindPending("demo", "sha-2"));    // different tree
        Assert.Null(_store.FindPending("other", "sha-1"));   // different name

        _store.MarkApproved(a.Id);    // approved, not yet installed - still undecided
        Assert.Equal(a.Id, _store.FindPending("demo", "sha-1")!.Id);

        _store.MarkInstalled(a.Id);   // finished - no longer undecided
        Assert.Null(_store.FindPending("demo", "sha-1"));
    }

    [Fact]
    public void FindPending_ignores_a_rejected_row_and_finds_a_later_proposal_of_the_same_name_and_tree()
    {
        var a = _store.Add("general", "opus", "demo", @"C:\rooms\general\demo", "sha-1", false, false, 1, 10);
        _store.MarkRejected(a.Id);
        Assert.Null(_store.FindPending("demo", "sha-1"));

        var b = _store.Add("general", "codex", "demo", @"C:\rooms\general\demo", "sha-1", false, false, 1, 10);
        Assert.Equal(b.Id, _store.FindPending("demo", "sha-1")!.Id);
    }

    [Fact]
    public void MarkApproved_moves_a_pending_row_once_and_stamps_DecidedAt_and_a_repeat_call_is_a_no_op()
    {
        var a = _store.Add("general", "opus", "demo", @"C:\rooms\general\demo", "sha-1", false, false, 1, 10);

        var approved = _store.MarkApproved(a.Id);

        Assert.NotNull(approved);
        Assert.Equal("approved", approved!.Status);
        Assert.NotNull(approved.DecidedAt);
        Assert.Null(approved.InstalledAt);

        // Safe to re-apply (ticket 04): a second call is a no-op, not a corrupting overwrite.
        Assert.Null(_store.MarkApproved(a.Id));
        Assert.Equal(approved.DecidedAt, _store.Get(a.Id)!.DecidedAt);
    }

    [Fact]
    public void MarkRejected_moves_a_pending_row_once_and_MarkApproved_can_no_longer_touch_it()
    {
        var a = _store.Add("general", "opus", "demo", @"C:\rooms\general\demo", "sha-1", false, false, 1, 10);

        var rejected = _store.MarkRejected(a.Id);

        Assert.NotNull(rejected);
        Assert.Equal("rejected", rejected!.Status);
        Assert.NotNull(rejected.DecidedAt);
        Assert.Null(_store.MarkRejected(a.Id));
        Assert.Null(_store.MarkApproved(a.Id));
    }

    [Fact]
    public void MarkInstalled_only_touches_an_approved_row_and_a_repeat_call_does_not_overwrite_the_stamp()
    {
        var a = _store.Add("general", "opus", "demo", @"C:\rooms\general\demo", "sha-1", false, false, 1, 10);

        Assert.Null(_store.MarkInstalled(a.Id));   // still pending

        _store.MarkApproved(a.Id);
        var installed = _store.MarkInstalled(a.Id);

        Assert.NotNull(installed);
        Assert.NotNull(installed!.InstalledAt);

        // Safe to re-apply (ticket 04, and what task 7's Retry arm depends on): a repeat call is a
        // no-op rather than stamping a new time.
        Assert.Null(_store.MarkInstalled(a.Id));
        Assert.Equal(installed.InstalledAt, _store.Get(a.Id)!.InstalledAt);
    }

    [Fact]
    public void The_status_vocabulary_matches_memory_proposals()
    {
        Assert.Equal(MemoryProposalStore.Pending, SkillProposalStore.Pending);
        Assert.Equal(MemoryProposalStore.Approved, SkillProposalStore.Approved);
        Assert.Equal(MemoryProposalStore.Rejected, SkillProposalStore.Rejected);
        Assert.Equal(MemoryProposalStore.Undecided, SkillProposalStore.Undecided);
    }

    [Fact]
    public void An_unknown_room_or_author_is_an_integrity_error()
    {
        Assert.Throws<SqliteException>(() => _store.Add("nope", "opus", "demo", @"C:\x", "sha-1", false, false, 1, 10));
        Assert.Throws<SqliteException>(() => _store.Add("general", "nobody", "demo", @"C:\x", "sha-1", false, false, 1, 10));
    }
}
