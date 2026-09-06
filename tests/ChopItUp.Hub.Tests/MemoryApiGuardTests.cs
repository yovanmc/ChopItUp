using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Memory;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using ChopItUp.Hub.Web;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests;

public sealed class MemoryApiGuardTests
{
    private static readonly SpawnLimits Fast = new(Budget: 4, Debounce: TimeSpan.FromMilliseconds(150), MinSpacing: TimeSpan.Zero, Timeout: TimeSpan.FromSeconds(30), TranscriptMessages: 60, TranscriptChars: 24_000);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task A5_without_git_an_approval_still_writes_and_marks_and_says_it_was_not_committed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_memnogit_" + Guid.NewGuid().ToString("N"));
        await using var host = await HubTestHost.StartAsync(dir, memoryGit: root => new MemoryGit(root, () => throw new FileNotFoundException("'git' was not found on PATH")));
        host.Services.GetRequiredService<MemoryProposalStore>().Create("general", "opus", "user", "Likes tests", "Yes.", null);

        var r = await host.Client.PostAsync("api/memory/proposals/1/approve", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var approved = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(("approved", "topics/user.md", JsonValueKind.Null), (approved.GetProperty("status").GetString(), approved.GetProperty("writtenTo").GetString(), approved.GetProperty("commitHash").ValueKind));
        var memory = host.Services.GetRequiredService<MemoryStore>();
        Assert.Contains("## Likes tests", File.ReadAllText(Path.Combine(memory.TopicsDir, "user.md")));
        Assert.False(Directory.Exists(Path.Combine(memory.Root, ".git")));
        using var doc = JsonDocument.Parse(await host.Client.GetStringAsync("api/rooms/general/messages?afterId=0&limit=200"));
        Assert.Equal("Memory proposal #1 approved: written to memory/topics/user.md (not committed: git unavailable or failed; see the hub log).", doc.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("body").GetString());
    }

    [Fact]
    public async Task A5_decisions_are_refused_while_a_spawn_is_in_flight_and_allowed_after_it_ends()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_memguard_" + Guid.NewGuid().ToString("N"));
        var runner = new FakeProcessRunner();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Handler = async (_, _, ct) => { await release.Task.WaitAsync(ct); return FakeProcessRunner.Ok("""{"result":"done"}"""); };
        await using var host = await HubTestHost.StartAsync(dir, processRunner: runner, limits: Fast);
        var proposals = host.Services.GetRequiredService<MemoryProposalStore>();
        proposals.Create("general", "opus", "user", "A", "a", null);
        proposals.Create("general", "opus", "user", "B", "b", null);

        Assert.Equal(HttpStatusCode.Created, (await host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "@opus hi" })).StatusCode);
        await runner.NextSpecAsync(Wait);                                                 // in flight now
        Assert.True(host.Services.GetRequiredService<SpawnerService>().AnySpawnInFlight);

        var refused = await host.Client.PostAsync("api/memory/proposals/1/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(MemoryApi.SpawnRunning, await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync("api/memory/proposals/2/reject", null)).StatusCode);
        var planted = Path.Combine(dir, "planted"); Directory.CreateDirectory(planted);
        File.WriteAllText(Path.Combine(planted, "x.md"), "---\nname: x\ndescription: Planted\nmetadata:\n  type: user\n---\nBy a spawn.\n");
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsJsonAsync("api/memory/import", new { source = "claude", path = planted, roomId = "general" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.DeleteAsync($"api/memory/proposals?source=claude&path={Uri.EscapeDataString(planted)}")).StatusCode);
        Assert.Equal("pending", proposals.Get(1)!.Status);
        Assert.Equal(2, proposals.List(null, null).Count);

        release.SetResult();
        var spawner = host.Services.GetRequiredService<SpawnerService>();
        var deadline = DateTime.UtcNow + Wait;
        while (spawner.AnySpawnInFlight && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.False(spawner.AnySpawnInFlight);

        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsync("api/memory/proposals/1/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsync("api/memory/proposals/2/reject", null)).StatusCode);
    }
}
