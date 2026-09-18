using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Storage;
using static ChopItUp.Hub.Tests.RunHostFixture;   // RunSkillMd, exactly as SpawnerServiceTests.Runs.cs:10 imports it

namespace ChopItUp.Hub.Tests.Spawning;

public sealed partial class SpawnerServiceTests
{
    private const string ImportedHistory =
        "Owner: @opus what do you think of the plan?\n" +
        "Opus: I think so. @gpt-6-astra, a second opinion?\n" +
        "Owner: /build-thing @sonnet begin\n" +
        "Owner: /stop\n" +
        "Owner: @sonnet build the thing now";

    private async Task<JsonElement> ImportInto(string room, string text)
    {
        var r = await _host.Client.PostAsJsonAsync($"api/rooms/{room}/import", new { text });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> HubNotesIn(string room) => (await MessagesIn(room)).Count(m => m.Author == ChopDb.HubParticipantId);

    [Fact]
    public async Task Row42_AC1_a_labelled_import_with_mentions_a_run_skill_stop_and_build_requests_spawns_and_notes_nothing()
    {
        WriteSkill("build-thing", RunSkillMd);
        const string room = "lab-import";
        await MakeRoom(room);                                                    // a directory room: a run-start here would otherwise succeed
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (_, _, ct) => { await hold.Task.WaitAsync(ct); return FakeProcessRunner.Ok("""{"result":"done"}"""); };

        var created = await ImportInto(room, ImportedHistory);
        Assert.Equal(5, created.GetProperty("messages").GetArrayLength());

        // Barrier + control (M24): a live post AFTER the import. Its spec arriving proves the FIFO loop
        // has processed all five imported events; that it spawns proves the instrument binds.
        await PostAsOwnerIn(room, "@opus control: what do you think?");
        var spec = await _runner.NextSpecAsync(Wait);
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(spec));
        Assert.Contains("control: what do you think?", spec.StandardInput);

        Assert.Equal(1, _runner.Count);                                          // the control is the ONLY launch
        Assert.Null(Runs.Active(room));
        Assert.Null(Runs.Latest(room));
        Assert.Equal(0, await HubNotesIn(room));                                  // no unknown-skill, run, stop or steer note
        Assert.Equal(6, (await MessagesIn(room)).Count);
        var snap = Spawner.Snapshot(room);
        Assert.Equal("open", snap.Status);                                        // the control's exchange, rooted at the live post
        Assert.Equal(created.GetProperty("messages")[4].GetProperty("id").GetInt64() + 1, snap.RootMessageId);
        hold.SetResult();
    }

    [Fact]
    public async Task Row42_AC1_labelless_paste_starting_with_a_run_skill_starts_no_run()
    {
        WriteSkill("build-thing", RunSkillMd);
        const string room = "lab-import-labelless";
        await MakeRoom(room);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (_, _, ct) => { await hold.Task.WaitAsync(ct); return FakeProcessRunner.Ok("""{"result":"done"}"""); };

        var created = await ImportInto(room, "/build-thing @sonnet begin\nand then @opus review it");
        Assert.Equal(1, created.GetProperty("messages").GetArrayLength());      // no speaker label: one message, body starts with "/"

        await PostAsOwnerIn(room, "@opus control");                             // barrier + control
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));

        Assert.Equal(1, _runner.Count);
        Assert.Null(Runs.Active(room));
        Assert.Null(Runs.Latest(room));
        Assert.Equal(0, await HubNotesIn(room));
        hold.SetResult();
    }

    [Fact]
    public async Task Row42_AC2_a_labelled_import_during_an_active_run_neither_stops_nor_steers_it()
    {
        await AssertImportDuringRunIsInert("lab-import-run", "Owner: /stop\nOwner: @opus actually you take it", expectedMessages: 2);
    }

    [Fact]
    public async Task Row42_AC2_labelless_paste_starting_with_stop_does_not_end_the_run()
    {
        await AssertImportDuringRunIsInert("lab-import-run-stop", "/stop\n@sonnet build the thing now", expectedMessages: 1);
    }

    private async Task AssertImportDuringRunIsInert(string room, string paste, int expectedMessages)
    {
        WriteSkill("build-thing", RunSkillMd);
        await MakeRoom(room);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet") await release.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };
        await PostAsOwnerIn(room, "/build-thing @sonnet begin");
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        var notesBefore = await HubNotesIn(room);

        var created = await ImportInto(room, paste);
        Assert.Equal(expectedMessages, created.GetProperty("messages").GetArrayLength());

        // Barrier: a live owner post during a run is a steer and posts "Steer noted" from inside the loop,
        // so seeing that note proves the imported events ahead of it were processed.
        await PostAsOwnerIn(room, "marker steer");
        await WaitForMessageIn(room, m => m.Author == ChopDb.HubParticipantId && m.Body.StartsWith("Steer noted", StringComparison.Ordinal));

        var run = Runs.Active(room);
        Assert.NotNull(run);                                                      // not stopped by the imported /stop
        Assert.Equal("sonnet", run!.ConductorId);
        Assert.Equal(1, _runner.Count);                                           // opus never spawned
        Assert.Equal(notesBefore + 1, await HubNotesIn(room));                    // exactly the marker's steer note, nothing from the import
        release.SetResult();
    }
}
