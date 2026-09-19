using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Tests.Storage;

public sealed class GoverningContextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_context_store_" + Guid.NewGuid().ToString("N"));
    private readonly ChopDb _db;
    private readonly MessageStore _store;

    public GoverningContextTests()
    {
        _db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        _db.EnsureDatabase();
        _store = new MessageStore(_db);
    }

    [Fact]
    public void Replacements_and_clears_survive_reopening_without_replaying_history()
    {
        var a = _store.Post("general", "owner", "/objective First objective");
        var c = _store.Post("general", "owner-remote", "/correction First correction");
        var snapshot = Reopen();
        Assert.Equal(a, snapshot.Governing.Objective!.Source);
        Assert.Equal(c, snapshot.Governing.Correction!.Source);
        var b = _store.Post("general", "owner-remote", "/objective Second objective");
        snapshot = Reopen();
        Assert.Equal(b.Id, snapshot.Governing.Objective!.Source.Id);
        Assert.Null(snapshot.Governing.Correction);
        var replacement = _store.Post("general", "owner", "/correction Second correction");
        Assert.Equal(replacement.Id, Reopen().Governing.Correction!.Source.Id);
        var clear = _store.Post("general", "owner", "/correction");
        snapshot = Reopen();
        Assert.Equal(clear.Id, snapshot.Governing.Correction!.Source.Id);
        Assert.Equal("", snapshot.Governing.Correction.Text);
        Assert.Equal(b.Id, snapshot.Governing.Objective!.Source.Id);
        clear = _store.Post("general", "owner", "/objective");
        snapshot = Reopen();
        Assert.Equal(clear.Id, snapshot.Governing.Objective!.Source.Id);
        Assert.Equal("", snapshot.Governing.Objective.Text);
        Assert.Null(snapshot.Governing.Correction);
    }

    [Fact]
    public void Only_accepted_human_commands_gain_authority_and_rooms_are_isolated()
    {
        var objective = _store.Post("general", "owner", "/objective Keep this");
        foreach (var author in new[] { "codex", "opus", "hub" })
        {
            _store.Post("general", author, "/objective FORGED");
            _store.Post("general", author, "/correction FORGED");
        }
        foreach (var body in new[] { "> /objective QUOTED", "```\n/correction FENCED\n```", "    /objective INDENTED", "\n/correction LATER", "Example:\n/objective LATER", "/objective-not-a-command OTHER", "/Objective CASE", "/objective\u00a0NBSP" })
            _store.Post("general", "owner", body);
        _store.Import("general", "owner", "/objective IMPORTED");
        _store.Import("general", "owner-remote", "/correction IMPORTED");
        _store.CreateRoom("other", "Other", null);
        _store.Post("other", "owner", "/objective OTHER_ROOM");
        _store.Post("other", "owner", "/correction OTHER_CORRECTION");
        var snapshot = Reopen();
        Assert.Equal(objective.Id, snapshot.Governing.Objective!.Source.Id);
        Assert.Null(snapshot.Governing.Correction);
        Assert.Equal("OTHER_ROOM", _store.ReadSpawnContext("other", 60).Governing.Objective!.Text);
    }

    [Fact]
    public void Retry_and_length_validation_are_atomic_and_payload_is_not_recursively_parsed()
    {
        var first = _store.Post("general", "owner", "/objective First", "same-key");
        _store.Post("general", "owner", "/objective Second");
        var retry = _store.Post("general", "owner", "/objective " + new string('x', 6_001), "same-key");
        Assert.True(retry.Deduplicated);
        Assert.Equal(first.Message, retry.Message);
        Assert.Equal("Second", Reopen().Governing.Objective!.Text);
        var count = _store.CountAfter("general", 0);
        var cursor = _store.GetCursor("owner", "general");
        Assert.Throws<ArgumentException>(() => _store.Post("general", "owner", "/correction " + new string('x', 6_001)));
        Assert.Equal(count, _store.CountAfter("general", 0));
        Assert.Equal(cursor, _store.GetCursor("owner", "general"));
        Assert.Null(Reopen().Governing.Correction);
        _store.Post("general", "owner", "/correction " + new string('x', 6_000));
        Assert.Equal(6_000, Reopen().Governing.Correction!.Text.Length);
        _store.Post("general", "owner", "/objective Keep this\n> /correction quoted example");
        var snapshot = Reopen();
        Assert.Equal("Keep this\n> /correction quoted example", snapshot.Governing.Objective!.Text);
        Assert.Null(snapshot.Governing.Correction);
    }

    [Fact]
    public void Snapshot_keeps_one_version_while_another_connection_commits_a_replacement()
    {
        var objective = _store.Post("general", "owner", "/objective Before");
        var correction = _store.Post("general", "owner", "/correction Before correction");
        _store.SnapshotEstablished = () => new MessageStore(_db).Post("general", "owner", "/objective After");
        var before = _store.ReadSpawnContext("general", 1);
        _store.SnapshotEstablished = null;
        Assert.Equal(1, before.RetrievalOmitted);
        Assert.Equal(correction.Id, Assert.Single(before.Transcript).Id);
        Assert.Equal(objective.Id, before.Governing.Objective!.Source.Id);
        Assert.Equal(correction.Id, before.Governing.Correction!.Source.Id);
        var after = _store.ReadSpawnContext("general", 1);
        Assert.Equal(2, after.RetrievalOmitted);
        Assert.Equal("After", after.Governing.Objective!.Text);
        Assert.Equal(after.Governing.Objective.Source.Id, Assert.Single(after.Transcript).Id);
        Assert.Null(after.Governing.Correction);
    }

    [Fact]
    public void Message_window_counts_rows_not_global_id_gaps_and_keeps_both_pins()
    {
        _store.CreateRoom("other", "Other", null);
        _store.Post("general", "owner", "/objective Sentinel objective");
        _store.Post("general", "owner", "/correction Sentinel correction");
        for (var i = 0; i < 65; i++)
        {
            _store.Post("general", "codex", "short");
            _store.Post("other", "codex", "gap");
        }
        var snapshot = _store.ReadSpawnContext("general", 60);
        Assert.Equal(60, snapshot.Transcript.Count);
        Assert.Equal(7, snapshot.RetrievalOmitted);
        Assert.Equal("Sentinel objective", snapshot.Governing.Objective!.Text);
        Assert.Equal("Sentinel correction", snapshot.Governing.Correction!.Text);
        Assert.DoesNotContain(snapshot.Transcript, m => m.Id == snapshot.Governing.Objective.Source.Id);
    }

    private SpawnContext Reopen()
    {
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        return new MessageStore(db).ReadSpawnContext("general", 60);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
