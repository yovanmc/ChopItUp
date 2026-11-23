using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Tests.Storage;

public sealed class TaskStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_test_" + Guid.NewGuid().ToString("N"));
    private readonly TaskStore _store;
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    public TaskStoreTests()
    {
        TemplateDatabase.SeedInto(_dir);
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        _store = new TaskStore(db);
    }

    private static TaskStart Start(string id, DateTimeOffset at, string room = "general") =>
        new(id, room, "opus", "claude", "opus", "high", 7, 2, 3, Retry: true, at);

    [Fact]
    public void A_started_task_is_open_until_finished_once_and_a_second_finish_or_start_changes_nothing()
    {
        Assert.True(_store.Start(Start("t1", T0)));
        Assert.False(_store.Start(Start("t1", T0.AddHours(1)) with { ParticipantId = "sonnet" }));
        var open = Assert.Single(_store.ForRoom("general", 10));
        Assert.Equal(Start("t1", T0), open.Start);
        Assert.Null(open.End);

        var end = new TaskEnd(T0.AddSeconds(9), 8_500, 0, "completed", null, 1, """{"host":"claude"}""");
        Assert.True(_store.Finish("t1", end));
        Assert.False(_store.Finish("t1", end with { Termination = "failed" }));
        Assert.False(_store.Finish("missing", end));

        var done = Assert.Single(_store.ForRoom("general", 10));
        Assert.Equal(end, done.End);
    }

    [Fact]
    public void A_room_lists_newest_first_with_ties_in_launch_order_reversed_and_respects_the_limit()
    {
        _store.Start(Start("a", T0));
        _store.Start(Start("b", T0.AddMinutes(1)));
        _store.Start(Start("c", T0.AddMinutes(1)));   // same instant as b: one launch pass
        _store.Start(Start("x", T0.AddMinutes(2), room: "other"));

        Assert.Equal(["c", "b", "a"], _store.ForRoom("general", 10).Select(t => t.Start.SpawnId));
        Assert.Equal(["c", "b"], _store.ForRoom("general", 2).Select(t => t.Start.SpawnId));
        Assert.Empty(_store.ForRoom("nowhere", 10));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
