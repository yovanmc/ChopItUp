using ChopItUp.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>Row 14, task 4: the spawner reads role and persona text fresh from the stores at launch
/// time rather than off the startup-static <c>_roster</c> — this is what makes an owner's edit through
/// the (future, task 5) API take effect on the next spawn with no hub restart (D-c, AC7).</summary>
public sealed partial class SpawnerServiceTests
{
    [Fact]
    public async Task Row14_T4_a_global_role_is_rendered_into_the_spawned_prompt()
    {
        _host.Services.GetRequiredService<ParticipantStore>().SetRole("opus", "Row14 role: the team's skeptic.");

        await PostAsOwner("@opus hi");
        var spec = await _runner.NextSpecAsync(Wait);
        Assert.Contains("You in this room: Row14 role: the team's skeptic.", spec.StandardInput);
    }

    /// <summary>AC7 / D-c: the test that binds the whole design decision behind this row. A
    /// startup-snapshot implementation (reading role text off <c>_roster</c> instead of live from
    /// <see cref="ParticipantStore"/>) passes every other test in this file and fails only this one.</summary>
    [Fact]
    public async Task Row14_T4_AC7_a_role_written_after_the_host_started_is_carried_by_the_very_next_spawn_with_no_restart()
    {
        var participants = _host.Services.GetRequiredService<ParticipantStore>();
        participants.SetRole("opus", "Row14 role A: the optimist.");

        await PostAsOwner("@opus first");
        var first = await _runner.NextSpecAsync(Wait);
        Assert.Contains("Row14 role A: the optimist.", first.StandardInput);
        await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("Exchange concluded"));

        // Written through the store with the host still running - no restart, no new SpawnerService.
        participants.SetRole("opus", "Row14 role B: the skeptic.");

        await PostAsOwner("@opus second");
        var second = await _runner.NextSpecAsync(Wait);
        Assert.Contains("Row14 role B: the skeptic.", second.StandardInput);
        Assert.DoesNotContain("Row14 role A: the optimist.", second.StandardInput);
    }

    [Fact]
    public async Task Row14_T4_a_room_override_is_rendered_instead_of_the_global_role()
    {
        var participants = _host.Services.GetRequiredService<ParticipantStore>();
        participants.SetRole("opus", "Row14 global role text.");
        participants.SetRoomRole("general", "opus", "Row14 room override text.");

        await PostAsOwner("@opus hi");
        var spec = await _runner.NextSpecAsync(Wait);
        Assert.Contains("Row14 room override text.", spec.StandardInput);
        Assert.DoesNotContain("Row14 global role text.", spec.StandardInput);
    }

    /// <summary>AC4: a room persona renders for every participant spawned there, including one with no
    /// role of its own. Without this test, wiring only <c>new StandingText(null, role)</c> would pass
    /// every other test in this file.</summary>
    [Fact]
    public async Task Row14_T4_AC4_a_room_persona_is_rendered_for_a_participant_with_no_role_of_its_own()
    {
        _host.Services.GetRequiredService<MessageStore>().SetPersona("general", "Row14 persona: this is the planning room.");

        await PostAsOwner("@opus hi");
        var spec = await _runner.NextSpecAsync(Wait);
        Assert.Contains("This room: Row14 persona: this is the planning room.", spec.StandardInput);
    }
}
