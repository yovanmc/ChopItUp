using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Skills;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests;

/// <summary>Shared run-host fixture for the row 19/20/27 run-loop tests in
/// SpawnerServiceTests.Runs.cs and the wire-level assertions in ExchangeApiTests.cs: spins up a
/// directory-bound "lab" room with the build-thing skill already imported and ready for a run to
/// start against it, plus the hub-note polling helpers both files wait on.</summary>
internal static class RunHostFixture
{
    internal const string RunSkillMd = "---\nname: build-thing\nrun: true\n---\n\n# Build Thing\n\nBuild the thing.\n";

    /// <summary><paramref name="seedClasses"/> (row 20, task 3) runs BEFORE the hub starts, against a
    /// freshly-migrated database - the hub reads the roster once at startup (HubHost.Build) and never
    /// again, so a class needed inside a run (a Codex judge, in particular) has to be set through
    /// <see cref="ParticipantStore.SetClasses"/> here, the same host-command write path task 2 built,
    /// not through the API once the hub is already running.</summary>
    internal static async Task<(HubTestHost Host, FakeProcessRunner Runner, string Room)> StartRunHostAsync(
        SpawnLimits spawnLimits, RunLimits runLimits, TimeProvider? clock = null, Action<ParticipantStore>? seedClasses = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_run_" + Guid.NewGuid().ToString("N"));
        var roomsRoot = dir + "_rooms";
        var runner = new FakeProcessRunner();
        if (seedClasses is not null)
        {
            var seedDb = new ChopDb(Path.Combine(dir, "chopitup.db"));
            seedDb.EnsureDatabase();
            seedClasses(new ParticipantStore(seedDb));
        }
        var host = await HubTestHost.StartAsync(dir, processRunner: runner, limits: spawnLimits, roomsRoot: roomsRoot, clock: clock, runLimits: runLimits);
        host.AuthorizeAs(ChopDb.OwnerParticipantId);   // row 28: every non-GET /api call this fixture's callers make now needs a credential
        const string room = "lab";
        var roomDir = Path.Combine(roomsRoot, room);
        Assert.True(await new GitTrail(roomDir).InitAsync());
        host.Services.GetRequiredService<MessageStore>().CreateRoom(room, "LAB", roomDir);

        var skillDir = Path.Combine(dir, "skills", "build-thing");
        Directory.CreateDirectory(skillDir);
        var bytes = new System.Text.UTF8Encoding(false).GetBytes(RunSkillMd);
        File.WriteAllBytes(Path.Combine(skillDir, "SKILL.md"), bytes);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        new SkillHashes(host.Services.GetRequiredService<ChopDb>()).Record("build-thing", hash, "test-fixture");
        return (host, runner, room);
    }

    internal static async Task PostAsInHost(HubTestHost host, string participant, string room, string body)
    {
        await using var client = await host.ClientFor(participant);
        HubTestHost.Json(await client.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = room, ["body"] = body, ["client_key"] = Guid.NewGuid().ToString() }));
    }

    internal static async Task<List<(string Author, string Body)>> AllMessagesFrom(HubTestHost host, string room)
    {
        using var doc = JsonDocument.Parse(await host.Client.GetStringAsync($"api/rooms/{room}/messages?afterId=0&limit=200"));
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("authorId").GetString()!, m.GetProperty("body").GetString()!)).ToList();
    }

    internal static async Task<(string Author, string Body)> WaitForNoteContaining(HubTestHost host, string room, string text, int waitSeconds = 15)
    {
        var wait = TimeSpan.FromSeconds(waitSeconds);
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            var hit = (await AllMessagesFrom(host, room)).FirstOrDefault(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains(text));
            if (hit != default) return hit;
            await Task.Delay(100);
        }
        throw new TimeoutException($"No hub note containing '{text}' in '{room}' within {wait}");
    }
}
