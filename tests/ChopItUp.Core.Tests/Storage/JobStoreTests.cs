using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Tests.Storage;

public sealed class JobStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_jobs_" + Guid.NewGuid().ToString("N"));
    private readonly JobStore _store;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-20T10:00:00Z");

    public JobStoreTests()
    {
        TemplateDatabase.SeedInto(_dir);
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        _store = new JobStore(db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private Job StartOne(string? cancel = "stop.ps1") =>
        _store.Start("general", 7, "sonnet", "render.ps1", cancel, gpu: true, 120, id => $@"C:\a.artifacts\jobs\j{id}-x", T0);

    [Fact]
    public void Start_writes_a_running_row_whose_folder_carries_its_id()
    {
        var job = StartOne();

        Assert.Equal(("general", 7L, "sonnet", "render.ps1", "stop.ps1", true, 120), (job.RoomId, job.RunId, job.AuthorId, job.Command, job.CancelCommand, job.Gpu, job.TimeoutMinutes));
        Assert.Equal($@"C:\a.artifacts\jobs\j{job.Id}-x", job.Folder);
        Assert.Equal((JobStatus.Running, (int?)null, (int?)null, T0), (job.Status, job.ExitCode, job.Pid, job.StartedAt));
        Assert.Equal("j" + job.Id, job.Name);
    }

    [Fact]
    public void End_happens_once_and_a_second_end_changes_nothing()
    {
        var job = StartOne();
        _store.MarkProcessStarted(job.Id, 4242, T0.AddSeconds(1));

        Assert.True(_store.End(job.Id, JobStatus.Exited, 0, T0.AddMinutes(5)));
        Assert.False(_store.End(job.Id, JobStatus.Lost, null, T0.AddMinutes(6)));

        var ended = _store.Get(job.Id)!;
        Assert.Equal((JobStatus.Exited, (int?)0, (int?)4242), (ended.Status, ended.ExitCode, ended.Pid));
        Assert.Equal((T0.AddSeconds(1), T0.AddMinutes(5)), (ended.ProcessStartedAt!.Value, ended.EndedAt!.Value));
    }

    [Fact]
    public void Unreported_holds_running_jobs_and_ended_jobs_with_no_note_until_noted()
    {
        var running = StartOne(cancel: null);
        var endedUnnoted = StartOne();
        var noted = StartOne();
        _store.End(endedUnnoted.Id, JobStatus.Lost, null, T0);
        _store.End(noted.Id, JobStatus.Exited, 1, T0);
        _store.MarkNoted(noted.Id, T0);

        Assert.Equal([running.Id, endedUnnoted.Id], _store.Unreported().Select(j => j.Id));
        _store.MarkNoted(running.Id, T0);
        Assert.Equal([running.Id, endedUnnoted.Id], _store.Unreported().Select(j => j.Id));
    }

    [Fact]
    public void EndedAuthorOfFolder_names_the_starter_only_once_its_job_ended()
    {
        var running = _store.Start("general", 7, "sonnet", "a", null, false, 120, id => $@"C:\r.artifacts\jobs\j{id}-aaaa1111", T0);
        var ended = _store.Start("general", 7, "opus", "b", null, false, 120, id => $"C:/r.artifacts/jobs/j{id}-bbbb2222", T0);
        _store.End(ended.Id, JobStatus.Exited, 0, T0);

        Assert.Null(_store.EndedAuthorOfFolder($"j{running.Id}-aaaa1111"));
        Assert.Equal("opus", _store.EndedAuthorOfFolder($"j{ended.Id}-bbbb2222"));
        Assert.Null(_store.EndedAuthorOfFolder("bbbb2222"));   // a name is matched whole, never as a suffix
        Assert.Null(_store.EndedAuthorOfFolder("j9-none"));
    }

    [Theory]
    [InlineData("j12", 12L)]
    [InlineData("j0", null)]
    [InlineData("12", null)]
    [InlineData("j-3", null)]
    [InlineData("j", null)]
    [InlineData("j1x", null)]
    public void IdFrom_reads_only_a_job_name(string name, long? expected) => Assert.Equal(expected, Job.IdFrom(name));
}
