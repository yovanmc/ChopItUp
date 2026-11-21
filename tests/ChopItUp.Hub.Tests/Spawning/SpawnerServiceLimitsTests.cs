using System.Net.Http.Json;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Hosting;
using ChopItUp.Hub.Spawning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static ChopItUp.Hub.Tests.RunHostFixture;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>Usage limits end to end, on a fake clock in a zone six hours behind UTC: a held run
/// resumes two minutes past the reset, a limit with no reset waits for the owner, a hold survives a
/// restart, a plain exchange asks the participant again, and every failed spawn's raw output is
/// kept.</summary>
public sealed class SpawnerServiceLimitsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly SpawnLimits Instant = new(Budget: 4, Debounce: TimeSpan.Zero, MinSpacing: TimeSpan.Zero, Timeout: TimeSpan.FromSeconds(30), TranscriptMessages: 60, TranscriptChars: 24_000);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("hub-local", TimeSpan.FromHours(-6), "hub-local", "hub-local");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-20T16:00:00Z");   // 10:00 hub time
    private static readonly DateTimeOffset ResetAt3pm = DateTimeOffset.Parse("2026-11-20T21:00:00Z");
    private const string ClaudeLimit = "You've hit your 5-hour limit · resets 3pm (America/Chicago)";
    private const string Working = """{"result":"working"}""";

    private static FakeTimeProvider Clock()
    {
        var clock = new FakeTimeProvider(Start);
        clock.SetLocalTimeZone(Zone);
        return clock;
    }

    private static ProcessResult ClaudeFailed(string text) =>
        new(1, false, false, """{"type":"result","is_error":true,"result":""" + System.Text.Json.JsonSerializer.Serialize(text) + "}", "", TimeSpan.FromMilliseconds(1));

    private static Task Post(HubTestHost host, string room, string body) =>
        host.Client.PostAsJsonAsync($"api/rooms/{room}/messages", new { body });

    private static async Task<long> NoteIdStartingWith(HubTestHost host, string room, string prefix)
    {
        var note = await WaitForNoteContaining(host, room, prefix);
        return host.Services.GetRequiredService<MessageStore>().ReadLast(room, 100).Last(m => m.Body == note.Body).Id;
    }

    [Fact]
    public async Task A_run_whose_conductor_hits_a_limit_is_held_refunded_and_resumes_two_minutes_after_the_reset()
    {
        var clock = Clock();
        var (host, runner, room) = await StartRunHostAsync(Instant, RunLimits.Default, clock);
        await using var _ = host;
        var launches = 0;
        runner.Handler = (_, _, _) => Task.FromResult(Interlocked.Increment(ref launches) == 1 ? ClaudeFailed(ClaudeLimit) : FakeProcessRunner.Ok(Working));

        await Post(host, room, "/build-thing @sonnet begin");
        await runner.NextSpecAsync(Wait);
        var held = await WaitForRun(host, room, r => r.Status == RunStatus.Parked);

        Assert.Equal((ResetAt3pm + SpawnerService.ResumeGrace, 0, false), (held.ResumeAt!.Value, held.SpawnsUsed, held.CapSpent));
        await WaitForNoteContaining(host, room, $"Run #{held.Id} held: @sonnet hit a usage limit (\"{ClaudeLimit}\"). It resumes at 2026-11-20 15:02 hub time");
        clock.Advance(ResetAt3pm + SpawnerService.ResumeGrace - Start - TimeSpan.FromSeconds(1));
        Assert.True(await runner.NoSpecWithin(TimeSpan.FromMilliseconds(400)));

        clock.Advance(TimeSpan.FromSeconds(1));
        var conductor = await runner.NextSpecAsync(Wait);
        var noteId = await NoteIdStartingWith(host, room, $"Run #{held.Id}: the usage-limit reset passed.");
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(conductor));
        Assert.Contains($"#{noteId}", WhyHere(conductor.StandardInput));
        var resumed = await WaitForRun(host, room, r => r.Status == RunStatus.Active);
        Assert.Null(resumed.ResumeAt);
    }

    [Fact]
    public async Task A_limit_with_no_reset_time_parks_the_run_until_the_owner_posts()
    {
        var clock = Clock();
        var (host, runner, room) = await StartRunHostAsync(Instant, RunLimits.Default, clock);
        await using var _ = host;
        var launches = 0;
        runner.Handler = (_, _, _) => Task.FromResult(Interlocked.Increment(ref launches) == 1 ? ClaudeFailed("You've hit your monthly spend limit.") : FakeProcessRunner.Ok(Working));

        await Post(host, room, "/build-thing @sonnet begin");
        await runner.NextSpecAsync(Wait);
        var parked = await WaitForRun(host, room, r => r.Status == RunStatus.Parked);
        Assert.Null(parked.ResumeAt);
        await WaitForNoteContaining(host, room, $"Run #{parked.Id} parked: @sonnet hit a usage limit (\"You've hit your monthly spend limit.\") with no reset time given. Post to resume.");

        clock.Advance(TimeSpan.FromDays(2));
        Assert.True(await runner.NoSpecWithin(TimeSpan.FromMilliseconds(400)));
        await Post(host, room, "carry on");
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(await runner.NextSpecAsync(Wait)));
    }

    [Fact]
    public async Task A_hold_survives_a_restart_and_resumes_at_the_same_instant()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_limitrestart_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (host, runner, room) = await StartRunHostAsync(Instant, RunLimits.Default, Clock(), dir: dir, deleteOnDispose: false);
            long runId;
            await using (host)
            {
                runner.Handler = (_, _, _) => Task.FromResult(ClaudeFailed(ClaudeLimit));
                await Post(host, room, "/build-thing @sonnet begin");
                await runner.NextSpecAsync(Wait);
                runId = (await WaitForRun(host, room, r => r.Status == RunStatus.Parked)).Id;
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var clock = Clock();
            var (again, againRunner, _) = await StartRunHostAsync(Instant, RunLimits.Default, clock, dir: dir, deleteOnDispose: false);
            await using (again)
            {
                againRunner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok(Working));
                Assert.Equal(ResetAt3pm + SpawnerService.ResumeGrace, again.Services.GetRequiredService<RunStore>().ById(runId)!.ResumeAt);
                // The restarted hub arms its wake on a pool thread, maybe after this first advance: a
                // wake armed late is fired by the small steps that follow.
                clock.Advance(ResetAt3pm + SpawnerService.ResumeGrace - Start);
                ProcessSpec? conductor = null;
                for (var step = 0; step < 20 && conductor is null; step++)
                {
                    try { conductor = await againRunner.NextSpecAsync(TimeSpan.FromMilliseconds(250)); }
                    catch (OperationCanceledException) { clock.Advance(TimeSpan.FromSeconds(1)); }
                }
                Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(conductor ?? throw new TimeoutException("the held run never resumed")));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TestDirs.DeleteTree(dir);
            TestDirs.DeleteTree(dir + "_rooms");
        }
    }

    /// <summary>Outside a run the participant goes back into its exchange, refunded, and is asked
    /// again two minutes past the reset; another participant's spawn in the same exchange keeps running
    /// and its post lands. The room has no directory, so both run at once.</summary>
    [Fact]
    public async Task A_plain_exchange_asks_the_limited_participant_again_and_leaves_the_other_running()
    {
        var clock = Clock();
        var (host, runner, _) = await StartRunHostAsync(Instant, RunLimits.Default, clock);
        await using var _ = host;
        host.Services.GetRequiredService<MessageStore>().CreateRoom("plain", "PLAIN", null);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sonnetLaunches = 0;
        runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "opus")
            {
                await release.Task;
                await PostAsInHost(host, "opus", "plain", "opus's answer");
                return FakeProcessRunner.Ok(Working);
            }
            return Interlocked.Increment(ref sonnetLaunches) == 1 ? ClaudeFailed(ClaudeLimit) : FakeProcessRunner.Ok(Working);
        };

        await Post(host, "plain", "@sonnet @opus compare notes");
        await runner.NextSpecAsync(Wait);
        await runner.NextSpecAsync(Wait);
        await WaitForNoteContaining(host, "plain", $"@sonnet hit a usage limit (\"{ClaudeLimit}\"). Asking it again at 2026-11-20 15:02 hub time");
        var spawner = host.Services.GetRequiredService<SpawnerService>();
        var snapshotDeadline = DateTime.UtcNow + Wait;
        while (!spawner.Snapshot("plain").Pending.Contains("sonnet"))
        {
            Assert.True(DateTime.UtcNow < snapshotDeadline, "sonnet never went back to pending");
            await Task.Delay(20);
        }
        var snapshot = spawner.Snapshot("plain");
        Assert.Equal(("open", 1), (snapshot.Status, snapshot.TurnsUsed));   // sonnet's turn refunded, opus's still counted

        release.SetResult();
        var deadline = DateTime.UtcNow + Wait;
        while (!(await AllMessagesFrom(host, "plain")).Any(m => m.Author == "opus" && m.Body == "opus's answer"))
        {
            Assert.True(DateTime.UtcNow < deadline, "opus's post never landed");
            await Task.Delay(50);
        }
        Assert.True(await runner.NoSpecWithin(TimeSpan.FromMilliseconds(400)));
        clock.Advance(ResetAt3pm + SpawnerService.ResumeGrace - Start);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(await runner.NextSpecAsync(Wait)));
    }

    /// <summary>The unattended ask after a reset happens once per participant per exchange: a second
    /// limit is reported and not retried, so a CLI that keeps naming a reset never loops.</summary>
    [Fact]
    public async Task A_plain_exchange_asks_again_after_a_limit_only_once()
    {
        var clock = Clock();
        var (host, runner, _) = await StartRunHostAsync(Instant, RunLimits.Default, clock);
        await using var _ = host;
        host.Services.GetRequiredService<MessageStore>().CreateRoom("plain", "PLAIN", null);
        runner.Handler = (_, _, _) => Task.FromResult(ClaudeFailed(ClaudeLimit));

        await Post(host, "plain", "@sonnet hello");
        await runner.NextSpecAsync(Wait);
        await WaitForNoteContaining(host, "plain", "Asking it again at 2026-11-20 15:02 hub time");
        clock.Advance(ResetAt3pm + SpawnerService.ResumeGrace - Start);
        await runner.NextSpecAsync(Wait);
        await WaitForNoteContaining(host, "plain", $"@sonnet hit a usage limit again (\"{ClaudeLimit}\"), so it is not asked again.");

        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(await runner.NoSpecWithin(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task A_failed_spawn_with_no_limit_keeps_its_raw_output_in_a_file_its_note_names()
    {
        var (host, runner, _) = await StartRunHostAsync(Instant, RunLimits.Default, Clock());
        await using var _ = host;
        host.Services.GetRequiredService<MessageStore>().CreateRoom("plain", "PLAIN", null);
        runner.Handler = (_, _, _) => Task.FromResult(new ProcessResult(2, false, false, "partial stdout é", "stack trace here", TimeSpan.FromMilliseconds(1)));

        await Post(host, "plain", "@sonnet hello");
        await runner.NextSpecAsync(Wait);
        var note = await WaitForNoteContaining(host, "plain", "@sonnet exited with code 2 without replying. Raw output: spawn-failures\\");

        var relative = note.Body[(note.Body.IndexOf("spawn-failures\\", StringComparison.Ordinal))..].Split(' ')[0];
        var file = Path.Combine(host.Services.GetRequiredService<HubOptions>().DataDir, relative);
        var text = File.ReadAllText(file);
        Assert.Contains("== stdout ==\npartial stdout é", text);
        Assert.Contains("== stderr ==\nstack trace here", text);
    }
}
