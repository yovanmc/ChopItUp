using System.Net;
using System.Text.Json;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChopItUp.Hub.Tests;

/// <summary>Row 19, task 14: <c>GET /api/rooms/{roomId}/run</c>, the strip's only source. The rows
/// here are written straight through <see cref="RunStore"/> rather than driven through a spawn: the
/// endpoint is a projection of the store, and the three states it has to answer for include two
/// (<c>parked</c>, <c>ended</c>) that a real hub cannot be walked into inside a builder task — the
/// caps are hard code, so parking one for real costs 8 hours or 80 spawns (pass 2's F-20).</summary>
public sealed class RunsApiTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-01T09:00:00Z");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_runs_api_" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(T0);
    private HubTestHost _host = null!;

    public async Task InitializeAsync() => _host = await HubTestHost.StartAsync(_dir, clock: _clock);
    public async Task DisposeAsync() => await _host.DisposeAsync();

    private RunStore Runs => _host.Services.GetRequiredService<RunStore>();

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetRun(string roomId = "general")
    {
        var response = await _host.Client.GetAsync($"api/rooms/{roomId}/run");
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text.Length == 0 ? "null" : text);
        return (response.StatusCode, doc.RootElement.Clone());
    }

    private static string[] Names(JsonElement o) => o.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public async Task Run14_a_room_that_never_had_a_run_answers_204_with_no_body_and_an_unknown_room_is_still_404()
    {
        var (status, body) = await GetRun();
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Equal(JsonValueKind.Null, body.ValueKind);

        var missing = await GetRun("no-such-room");
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Contains("no-such-room", missing.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Run14_an_active_run_answers_its_phase_counters_caps_artifacts_and_gate_runs()
    {
        var run = Runs.Start("general", "sonnet", "build-thing", "begin", rootMessageId: 41, T0);
        Runs.EnterPhase(run.Id, "build/api", T0);
        Runs.EnterPhase(run.Id, "critique", T0);
        Runs.EnterPhase(run.Id, "build/api", T0);       // second entry into the tag the run now sits in
        Runs.CountSpawn(run.Id);
        Runs.CountSpawn(run.Id);
        Runs.CountExchange(run.Id);
        Runs.RecordArtifact(run.Id, "./Docs/Plan.md", "opus", T0);
        Runs.RecordGateRun(run.Id, "general", "roadmap-budget", "sonnet", 0, "ran", T0);
        _clock.Advance(TimeSpan.FromMinutes(45));

        var (status, body) = await GetRun();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(run.Id, body.GetProperty("id").GetInt64());
        Assert.Equal("general", body.GetProperty("roomId").GetString());
        Assert.Equal("sonnet", body.GetProperty("conductorId").GetString());
        Assert.Equal("build-thing", body.GetProperty("skillName").GetString());
        Assert.Equal(RunStatus.Active, body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("reason").ValueKind);
        Assert.False(body.GetProperty("capSpent").GetBoolean());
        Assert.Equal("build/api", body.GetProperty("phase").GetString());
        Assert.Equal(2, body.GetProperty("phaseEntries").GetInt32());          // this tag only, never the total
        Assert.Equal(3, body.GetProperty("phaseEntryCap").GetInt32());
        Assert.Equal(1, body.GetProperty("exchanges").GetInt32());
        Assert.Equal(2, body.GetProperty("spawnsUsed").GetInt32());
        Assert.Equal(80, body.GetProperty("spawnCap").GetInt32());
        Assert.Equal(45, body.GetProperty("elapsedMinutes").GetInt32());
        Assert.Equal(480, body.GetProperty("wallClockCapMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("endedAt").ValueKind);
        Assert.Equal(T0, body.GetProperty("startedAt").GetDateTimeOffset());

        var artifact = Assert.Single(body.GetProperty("artifacts").EnumerateArray());
        Assert.Equal("docs/plan.md", artifact.GetProperty("path").GetString());
        Assert.Equal("opus", artifact.GetProperty("authorId").GetString());
        var gate = Assert.Single(body.GetProperty("gateRuns").EnumerateArray());
        Assert.Equal("roadmap-budget", gate.GetProperty("gate").GetString());
        Assert.Equal(0, gate.GetProperty("exitCode").GetInt32());
        Assert.Equal("ran", gate.GetProperty("outcome").GetString());

        // The strip is a total render of this shape: a field added to the snapshot and not to the
        // client's `RunSnapshot` is a silent blank label, so the field list is pinned here.
        Assert.Equal(
            new[]
            {
                "id", "roomId", "conductorId", "skillName", "status", "reason", "capSpent", "phase",
                "phaseEntries", "phaseEntryCap", "exchanges", "spawnsUsed", "spawnCap", "startedAt",
                "endedAt", "elapsedMinutes", "wallClockCapMinutes", "artifacts", "gateRuns",
            },
            Names(body));
    }

    [Fact]
    public async Task Run14_a_parked_run_answers_the_reason_and_its_elapsed_stays_frozen_while_parked()
    {
        var run = Runs.Start("general", "sonnet", "build-thing", "", rootMessageId: 7, T0);
        Runs.EnterPhase(run.Id, "build", T0);
        Runs.Park(run.Id, "the conductor broke the phase rules twice in a row", capSpent: false, T0.AddMinutes(30));
        _clock.Advance(TimeSpan.FromHours(2));   // the owner was away; parked time is not run time

        var (status, body) = await GetRun();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(RunStatus.Parked, body.GetProperty("status").GetString());
        Assert.Equal("the conductor broke the phase rules twice in a row", body.GetProperty("reason").GetString());
        Assert.False(body.GetProperty("capSpent").GetBoolean());
        Assert.Equal(30, body.GetProperty("elapsedMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("endedAt").ValueKind);
    }

    [Fact]
    public async Task Run14_the_room_answers_its_most_recent_run_once_that_run_has_ended()
    {
        var run = Runs.Start("general", "sonnet", "build-thing", "", rootMessageId: 9, T0);
        Runs.EnterPhase(run.Id, "ping", T0.AddMinutes(10));
        Runs.End(run.Id, "the conductor posted phase: ping", T0.AddMinutes(12));
        _clock.Advance(TimeSpan.FromMinutes(90));

        var (status, body) = await GetRun();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(run.Id, body.GetProperty("id").GetInt64());
        Assert.Equal(RunStatus.Ended, body.GetProperty("status").GetString());
        Assert.Equal("the conductor posted phase: ping", body.GetProperty("reason").GetString());
        Assert.Equal("ping", body.GetProperty("phase").GetString());
        Assert.Equal(T0.AddMinutes(12), body.GetProperty("endedAt").GetDateTimeOffset());
        // An ended run's clock stopped when it ended: the strip must not keep counting it up.
        Assert.Equal(12, body.GetProperty("elapsedMinutes").GetInt32());
    }
}
