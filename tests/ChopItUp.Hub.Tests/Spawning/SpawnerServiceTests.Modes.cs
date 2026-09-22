using System.Net;
using System.Net.Http.Json;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Core.Messaging;
using ChopItUp.Hub.Spawning;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed class SpawnerServiceModesTests : SpawnerServiceTestBase
{
    [Fact]
    public async Task M49_direct_directory_panel_identifies_the_same_commit_in_all_three_prompts()
    {
        var directory = await MakeRoom("panel-hash");
        await new ChopItUp.Hub.Git.GitTrail(directory).CommitAllAsync("Synthetic panel snapshot", null, allowEmpty: true);
        var commit = await PanelInputs.ReadyCommitAsync(directory, CancellationToken.None);
        await Spawner.SetModeAsync("panel-hash", "panel", "sonnet", "opus");
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"independent answer"}"""));
        var posted = _host.Services.GetRequiredService<MessageStore>().Post("panel-hash", "owner", "Inspect the same frozen commit", null);
        _host.Services.GetRequiredService<MessageSignal>().Publish("panel-hash", posted.Message);
        for (var stage = 0; stage < 3; stage++)
            Assert.Contains("Snapshot commit: " + commit, (await _runner.NextSpecAsync(Wait)).StandardInput);
        await WaitForExchangeStatusIn("panel-hash", posted.Message.Id, "concluded");
        Assert.Equal(commit, await PanelInputs.ReadyCommitAsync(directory, CancellationToken.None));
        Assert.Equal(3, _runner.Count);
    }

    [Fact]
    public async Task M49_off_handle_model_mentions_cannot_expand_a_primary_exchange()
    {
        Mode("primary");
        var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = (_, _, _) => release.Task;
        await PostAsOwner("Fixed one-turn budget");
        await _runner.NextSpecAsync(Wait);
        await PostAs("opus", "@fable please add an unplanned turn");
        await Spawner.InLoopAsync(() => true);
        Assert.Empty(Spawner.Snapshot("general").Pending);
        release.SetResult(FakeProcessRunner.Ok("""{"result":"one answer"}"""));
        await WaitForStatus("concluded");
        Assert.Equal(1, _runner.Count);
    }

    [Fact]
    public async Task M49_owner_retry_key_cannot_be_reused_in_another_room()
    {
        Mode("primary");
        var store = _host.Services.GetRequiredService<MessageStore>();
        store.CreateRoom("other", "Other", null);
        await Spawner.AdmitOwnerAsync("general", "owner", "retry once", "author-wide-key", null);
        await _runner.NextSpecAsync(Wait);
        await WaitForStatus("concluded");
        await Assert.ThrowsAsync<StaleDispatchException>(() => Spawner.AdmitOwnerAsync("other", "owner", "retry once", "author-wide-key", null));
        Assert.Empty(store.Read("other", 0, 100).Messages);
        Assert.Equal(1, _runner.Count);
    }

    [Fact]
    public async Task M49_slow_panel_preflight_does_not_block_stop_or_another_room()
    {
        Mode("primary");
        var store = _host.Services.GetRequiredService<MessageStore>();
        store.CreateRoom("panel", "Panel", _dir); // The injected readiness seam intentionally holds before real Git.
        await Spawner.SetModeAsync("panel", "panel", "sonnet", "opus");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Spawner.PanelReadiness = (_, cancel) => { entered.TrySetResult(); return release.Task.WaitAsync(cancel); };
        _runner.Handler = (_, _, cancel) => FakeProcessRunner.HangUntilKilled(TimeSpan.FromSeconds(30), cancel);
        await PostAsOwner("A running primary");
        await _runner.NextSpecAsync(Wait);
        var preview = Spawner.PreviewAsync("panel", "owner", "Slow repository", null);
        await entered.Task.WaitAsync(Wait);
        var admission = Spawner.AdmitOwnerAsync("panel", "owner", "Slow send", "slow-send", null);
        await Spawner.InLoopAsync(() => true);
        await Spawner.StopAsync("general").WaitAsync(TimeSpan.FromSeconds(2));
        await Spawner.AdmitOwnerAsync("general", "owner", "/objective control loop remains responsive", null, null).WaitAsync(TimeSpan.FromSeconds(2));
        await Spawner.SetModeAsync("panel", "primary", "sonnet", null);
        release.SetResult(new string('a', 40));
        Assert.Contains("changed", (await preview).Error);
        await Assert.ThrowsAsync<StaleDispatchException>(() => admission);
        Assert.Empty(store.Read("panel", 0, 100).Messages);
    }

    [Fact]
    public async Task M49_superseded_panel_publishes_a_collected_answer_when_second_is_still_pending()
    {
        await _host.DisposeAsync();
        _host = await HubTestHost.StartAsync(_dir, processRunner: _runner, limits: Fast with { MinSpacing = TimeSpan.FromSeconds(30) });
        _host.AuthorizeAs(ChopDb.OwnerParticipantId);
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"retained independent result"}"""));
        await PostAsOwner("@opus warmup spacing");
        await _runner.NextSpecAsync(Wait);
        await WaitForStatus("concluded");
        Mode("panel");
        await PostAsOwner("Panel with a spaced second pass");
        await _runner.NextSpecAsync(Wait);
        // The fake runner can expose the launch before LaunchDue publishes its updated snapshot.
        // Drain that loop pass and distinguish completed sonnet from the pre-launch pending state.
        await Spawner.InLoopAsync(() => true);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = Spawner.Snapshot("general");
            if (state.Pending.Contains("opus") && !state.Pending.Contains("sonnet") && state.InFlight.Count == 0) break;
            await Task.Delay(10);
        }
        Assert.Contains("opus", Spawner.Snapshot("general").Pending);
        Assert.Empty(Spawner.Snapshot("general").InFlight);
        Assert.DoesNotContain(await Messages(), m => m.Author == "sonnet" && m.Body == "retained independent result");
        await Spawner.SetModeAsync("general", "primary", "sonnet", null);
        await PostAsOwner("Replacement owner prompt");
        await WaitForMessage(m => m.Author == "sonnet" && m.Body == "retained independent result");
        Assert.Equal(2, _runner.Count); // Warmup + one first pass; no synthesis or second pass.
        await Spawner.StopAsync("general");
    }

    [Fact]
    public async Task M49_duplicate_signals_and_explicit_continuation_do_not_repeat_the_mode()
    {
        Mode("primary");
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"successful answer"}"""));
        var posted = await Spawner.AdmitOwnerAsync("general", "owner", "one primary turn", "once", null);
        await _runner.NextSpecAsync(Wait);
        await WaitForStatus("concluded");
        var signal = _host.Services.GetRequiredService<MessageSignal>();
        signal.Publish("general", posted.Message);
        signal.Publish("general", posted.Message);
        await Spawner.InLoopAsync(() => true);
        Assert.Equal(1, _runner.Count);
        await PostAsOwner("/continue @opus turns: 1");
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        var snapshot = await WaitForStatus("concluded");
        Assert.Null(snapshot.Mode);
        Assert.Equal(2, _runner.Count);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("{\"result\":\"partial answer\"}", 1)]
    public async Task M49_failed_or_empty_first_relay_answer_does_not_launch_second(string output, int exit)
    {
        Mode("relay");
        _runner.Handler = (_, _, _) => Task.FromResult(new ProcessResult(exit, false, false, output, "", TimeSpan.Zero));
        await PostAsOwner("Do not continue an unavailable answer.");
        await _runner.NextSpecAsync(Wait);
        await WaitForStatus("concluded");
        Assert.Equal(1, _runner.Count);
    }

    [Fact]
    public async Task M49_panel_one_failure_synthesizes_labelled_unavailable_then_continue_costs_three()
    {
        Mode("panel");
        _runner.Handler = (spec, _, _) => Task.FromResult(FakeProcessRunner.ParticipantOf(spec) == "opus"
            ? new ProcessResult(1, false, false, "", "synthetic failure", TimeSpan.Zero)
            : FakeProcessRunner.Ok("""{"result":"available answer"}"""));
        await PostAsOwner("Panel partial outcome");
        await _runner.NextSpecAsync(Wait);
        await _runner.NextSpecAsync(Wait);
        var synthesis = await _runner.NextSpecAsync(Wait);
        Assert.Contains("\"opus\":null", synthesis.StandardInput);
        await WaitForStatus("concluded");
        await PostAsOwner("/continue");
        await _runner.NextSpecAsync(Wait);
        await _runner.NextSpecAsync(Wait);
        await _runner.NextSpecAsync(Wait);
        var snapshot = await WaitForStatus("concluded");
        Assert.Equal(6, snapshot.TurnsUsed);
        Assert.Equal(6, _runner.Count);
    }

    [Fact]
    public async Task M49_underfunded_quote_and_changed_body_store_nothing()
    {
        Mode("panel");
        var quote = await Spawner.PreviewAsync("general", "owner", "turns: 2 panel", null);
        Assert.Contains("requires 3", quote.Error);
        await Assert.ThrowsAsync<ArgumentException>(() => Spawner.AdmitOwnerAsync("general", "owner", "turns: 2 panel", "low", null));
        await Assert.ThrowsAsync<StaleDispatchException>(() => Spawner.AdmitOwnerAsync("general", "owner", "changed", "new", null, quote.Quote));
        Assert.Empty(await Messages());
        Assert.Equal(0, _runner.Count);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    public async Task M49_posted_answers_are_canonical_only_after_successful_process_completion(int exit, int launches)
    {
        Mode("relay");
        var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = (spec, _, _) => FakeProcessRunner.ParticipantOf(spec) == "sonnet" ? release.Task : Task.FromResult(FakeProcessRunner.Ok("""{"result":"second"}"""));
        await PostAsOwner("Posted answer relay");
        await _runner.NextSpecAsync(Wait);
        await PostAs("sonnet", "posted first @fable");
        await PostAs("sonnet", "posted second");
        await Spawner.InLoopAsync(() => true);
        Assert.Equal(1, _runner.Count);
        release.SetResult(new ProcessResult(exit, false, false, """{"result":"DUPLICATE_FINAL_SHOULD_NOT_JOIN"}""", "", TimeSpan.Zero));
        if (exit == 0)
        {
            var second = await _runner.NextSpecAsync(Wait);
            Assert.Contains("posted first", second.StandardInput);
            Assert.Contains("posted second", second.StandardInput);
            Assert.DoesNotContain("DUPLICATE_FINAL_SHOULD_NOT_JOIN", second.StandardInput);
        }
        await WaitForStatus("concluded");
        Assert.Equal(launches, _runner.Count);
    }

    [Fact]
    public async Task M49_stop_panel_prevents_synthesis_even_when_a_success_races_cancellation()
    {
        Mode("panel");
        var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = (_, _, _) => release.Task;
        await PostAsOwner("Stopped panel");
        try { await _runner.NextSpecAsync(Wait); }
        catch (OperationCanceledException e)
        {
            throw new InvalidOperationException("Panel did not launch: " + System.Text.Json.JsonSerializer.Serialize(Spawner.Snapshot("general"))
                + "; messages=" + System.Text.Json.JsonSerializer.Serialize(await Messages()), e);
        }
        await _runner.NextSpecAsync(Wait);
        await Spawner.StopAsync("general");
        release.SetResult(FakeProcessRunner.Ok("""{"result":"racing success"}"""));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (Spawner.Snapshot("general").InFlight.Count != 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal("stopped", Spawner.Snapshot("general").Status);
        Assert.Empty(Spawner.Snapshot("general").InFlight);
        Assert.Equal(2, _runner.Count);
    }

    [Fact]
    public async Task M49_relay_waits_for_success_and_includes_the_first_answer_once()
    {
        Mode("relay");
        var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = (spec, _, _) => FakeProcessRunner.ParticipantOf(spec) == "sonnet" ? release.Task : Task.FromResult(FakeProcessRunner.Ok("""{"result":"second answer"}"""));
        await PostAsOwner("Relay this question.");
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        Assert.Equal(1, _runner.Count);
        release.SetResult(FakeProcessRunner.Ok("""{"result":"first completed answer @fable"}"""));
        var second = await _runner.NextSpecAsync(Wait);
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(second));
        Assert.Contains("first completed answer @fable", second.StandardInput);
        Assert.Equal(2, (await WaitForStatus("concluded")).TurnsUsed);
        Assert.Equal(2, _runner.Count);
    }

    [Fact]
    public async Task M49_panel_withholds_first_passes_and_synthesizes_the_frozen_context()
    {
        Mode("panel");
        var releases = new Dictionary<string, TaskCompletionSource<ProcessResult>>
        {
            ["sonnet"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["opus"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        _runner.Handler = (spec, _, _) => spec.WorkingDirectory.EndsWith("synthesis")
            ? Task.FromResult(FakeProcessRunner.Ok("""{"result":"combined answer"}""")) : releases[FakeProcessRunner.ParticipantOf(spec)].Task;
        await PostAsOwner("Frozen original question.");
        var first = await _runner.NextSpecAsync(Wait);
        var second = await _runner.NextSpecAsync(Wait);
        Assert.Equal(2, _runner.Count);
        await Spawner.InLoopAsync(() => true);
        Assert.Equal(2, Spawner.Snapshot("general").InFlight.Count);
        releases[FakeProcessRunner.ParticipantOf(first)].SetResult(FakeProcessRunner.Ok("""{"result":"PRIVATE_FIRST_RESULT @fable"}"""));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (Spawner.Snapshot("general").InFlight.Count != 1 && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.Single(Spawner.Snapshot("general").InFlight);
        Assert.DoesNotContain(await Messages(), m => m.Body.Contains("PRIVATE_FIRST_RESULT"));
        Assert.DoesNotContain("PRIVATE_FIRST_RESULT", second.StandardInput);
        await PostAsOwner("/objective changed after both first passes started");
        releases[FakeProcessRunner.ParticipantOf(second)].SetResult(FakeProcessRunner.Ok("""{"result":"PRIVATE_SECOND_RESULT"}"""));
        var synthesis = await _runner.NextSpecAsync(Wait);
        Assert.Contains("PRIVATE_FIRST_RESULT", synthesis.StandardInput);
        Assert.Contains("PRIVATE_SECOND_RESULT", synthesis.StandardInput);
        Assert.DoesNotContain("changed after both", synthesis.StandardInput);
        Assert.Equal(3, (await WaitForStatus("concluded")).TurnsUsed);
        Assert.Equal(3, _runner.Count);
    }

    [Fact]
    public async Task M49_stale_settings_quote_refuses_before_storage_and_an_identical_retry_spends_once()
    {
        Mode("primary", "sonnet", null);
        var preview = await Spawner.PreviewAsync("general", "owner", "quoted question", null);
        await Spawner.SetModeAsync("general", "primary", "opus", null);
        var response = await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "quoted question", quote = preview.Quote, clientKey = "m49-key" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(await Messages(), m => m.Body == "quoted question");
        Assert.Equal(0, _runner.Count);
        var fresh = await Spawner.PreviewAsync("general", "owner", "quoted question", null);
        var first = await Spawner.AdmitOwnerAsync("general", "owner", "quoted question", "m49-key", null, fresh.Quote);
        var duplicate = await Spawner.AdmitOwnerAsync("general", "owner", "quoted question", "m49-key", null, fresh.Quote);
        Assert.Equal(first.Message.Id, duplicate.Message.Id);
        Assert.True(duplicate.Deduplicated);
        await Assert.ThrowsAsync<StaleDispatchException>(() => Spawner.AdmitOwnerAsync("general", "owner", "changed", "m49-key", null, fresh.Quote));
        await _runner.NextSpecAsync(Wait);
        await WaitForStatus("concluded");
        Assert.Equal(1, _runner.Count);
    }

    [Fact]
    public async Task M49_stop_during_relay_prevents_the_second_turn()
    {
        Mode("relay");
        _runner.Handler = (_, timeout, cancel) => FakeProcessRunner.HangUntilKilled(TimeSpan.FromSeconds(30), cancel);
        await PostAsOwner("This relay will be stopped.");
        await _runner.NextSpecAsync(Wait);
        await Spawner.StopAsync("general");
        await WaitForStatus("stopped");
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(350)));
        Assert.Equal(1, _runner.Count);
    }

    [Fact]
    public async Task M49_primary_unaddressed_owner_message_launches_once_and_publishes_final_answer()
    {
        var store = _host.Services.GetRequiredService<MessageStore>();
        Assert.True(store.SetMode("general", new RoomModeSettings("primary", "sonnet", null), ChopDb.SeedRoster));
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"Synthetic primary answer"}"""));
        await PostAsOwner("Please answer this ordinary unaddressed question.");
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        await WaitForMessage(m => m.Author == "sonnet" && m.Body.Contains("Synthetic primary answer"));
        Assert.Equal(1, (await WaitForStatus("concluded")).TurnsUsed);
        Assert.Equal(1, _runner.Count);
    }
}
