using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>Row 19, task 4: starting a run, and every refusal at the start (ticket 04). End-to-end
/// through the real HTTP + MCP surface, same fixture shape as the Skill_04 tests above.</summary>
public sealed partial class SpawnerServiceTests
{
    private const string RunSkillMd = "---\nname: build-thing\nrun: true\n---\n\n# Build Thing\n\nBuild the thing.\n";

    private RunStore Runs => _host.Services.GetRequiredService<RunStore>();

    [Fact]
    public async Task Run04_A1_a_valid_invocation_in_a_directory_room_starts_a_run_and_asks_the_conductor()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-start");
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"working"}"""));

        await PostAsOwnerIn("lab-run-start", "/build-thing @sonnet begin");
        var spec = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(spec));

        var note = await WaitForMessageIn("lab-run-start", m => m.Author == ChopDb.HubParticipantId && m.Body.StartsWith("Run #"));
        Assert.StartsWith("Run #", note.Body);
        Assert.Contains(" started", note.Body);
        Assert.Contains("@sonnet conducts", note.Body);

        var run = Runs.Active("lab-run-start");
        Assert.NotNull(run);
        Assert.Equal("sonnet", run!.ConductorId);
        Assert.Equal("build-thing", run.SkillName);
        Assert.Equal(RunStatus.Active, run.Status);
        Assert.Equal(1, run.Exchanges);
    }

    [Fact]
    public async Task Run04_A2_a_room_with_no_directory_refuses_and_starts_nothing()
    {
        WriteSkill("build-thing", RunSkillMd);
        await PostAsOwner("/build-thing @sonnet begin");   // "general" has no directory
        var note = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("needs a room bound to a directory"));
        Assert.Equal("/build-thing starts a run, which needs a room bound to a directory; this room has none.", note.Body);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));
        Assert.Null(Runs.Active("general"));
    }

    [Fact]
    public async Task Run04_A2_zero_or_many_conductors_refuses_naming_the_count()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-count");

        await PostAsOwnerIn("lab-run-count", "/build-thing begin, no one mentioned");
        var zero = await WaitForMessageIn("lab-run-count", m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("none was"));
        Assert.Equal("/build-thing starts a run and needs exactly one conductor mentioned; none was.", zero.Body);

        await PostAsOwnerIn("lab-run-count", "/build-thing @opus @sonnet both of you");
        var many = await WaitForMessageIn("lab-run-count", m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("were:"));
        Assert.Equal("/build-thing starts a run and needs exactly one conductor mentioned; 2 were: @opus, @sonnet.", many.Body);

        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));
        Assert.Null(Runs.Active("lab-run-count"));
    }

    [Fact]
    public async Task Run04_A2_a_second_run_start_invocation_while_one_is_active_refuses_without_disturbing_it()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-twice");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet") await release.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwnerIn("lab-run-twice", "/build-thing @sonnet begin");
        await _runner.NextSpecAsync(Wait);
        var firstRun = Runs.Active("lab-run-twice");
        Assert.NotNull(firstRun);

        await PostAsOwnerIn("lab-run-twice", "/build-thing @opus begin again");
        var note = await WaitForMessageIn("lab-run-twice", m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("already active"));
        Assert.Equal($"A run is already active in this room (#{firstRun!.Id}); /stop it first.", note.Body);

        var stillActive = Runs.Active("lab-run-twice");
        Assert.Equal(firstRun.Id, stillActive!.Id);
        Assert.Equal("sonnet", stillActive.ConductorId);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));   // opus never spawned
        release.SetResult();
    }

    [Fact]
    public async Task Run04_AC5_a_human_message_mentioning_a_participant_during_a_run_creates_no_work()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-steer");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet") await release.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwnerIn("lab-run-steer", "/build-thing @sonnet begin");
        await _runner.NextSpecAsync(Wait);

        await PostAsOwnerIn("lab-run-steer", "@opus actually you take it");
        await Task.Delay(300);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(300)));   // opus never spawned
        var run = Runs.Active("lab-run-steer");
        Assert.NotNull(run);
        Assert.Equal("sonnet", run!.ConductorId);                                 // untouched
        release.SetResult();
    }

    [Fact]
    public async Task Run04_owner_remote_starts_a_run_exactly_as_the_owner_does()
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom("lab-run-remote");
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"working"}"""));

        await PostAsIn(ChopDb.OwnerRemoteParticipantId, "lab-run-remote", "/build-thing @sonnet begin");
        var spec = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(spec));
        Assert.NotNull(Runs.Active("lab-run-remote"));
    }
}
