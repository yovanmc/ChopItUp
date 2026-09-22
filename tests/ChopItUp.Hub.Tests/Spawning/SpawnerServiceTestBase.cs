using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using ChopItUp.Core.Memory;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Skills;
using ChopItUp.Hub.Spawning;
using Microsoft.Extensions.DependencyInjection;
using static ChopItUp.Hub.Tests.RunHostFixture;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>The hub, fake runner and helpers every SpawnerService test class shares. Each area of the
/// spawner has its own sealed class over this base, so xUnit can run those classes as separate
/// collections.</summary>
public abstract class SpawnerServiceTestBase : IAsyncLifetime
{
    protected static readonly SpawnLimits Fast = new(Budget: 4, Debounce: TimeSpan.FromMilliseconds(150), MinSpacing: TimeSpan.Zero, Timeout: TimeSpan.FromSeconds(1), TranscriptMessages: 60, TranscriptChars: 24_000);

    protected static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    protected readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_spawner_" + Guid.NewGuid().ToString("N"));

    protected readonly FakeProcessRunner _runner = new();

    protected HubTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await HubTestHost.StartAsync(_dir, processRunner: _runner, limits: Fast);
        _host.AuthorizeAs(ChopDb.OwnerParticipantId);   // row 28: every non-GET /api call here now needs a credential
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    protected SpawnerService Spawner => _host.Services.GetRequiredService<SpawnerService>();

    protected async Task PostAsOwner(string body)
    {
        var r = await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
    }

    protected async Task PostAs(string participant, string body, string? clientKey = null)
    {
        await using var client = await _host.ClientFor(participant);
        var args = new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = body };
        if (clientKey is not null) args["client_key"] = clientKey;
        HubTestHost.Json(await client.CallToolAsync("post_message", args));
    }

    protected async Task<List<(string Author, string Body)>> Messages()
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/general/messages?afterId=0&limit=200"));
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("authorId").GetString()!, m.GetProperty("body").GetString()!)).ToList();
    }

    protected async Task<(string Author, string Body)> WaitForMessage(Func<(string Author, string Body), bool> match)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var hit = (await Messages()).FirstOrDefault(match);
            if (hit != default) return hit;
            await Task.Delay(100);
        }
        throw new TimeoutException("No matching message within " + Wait);
    }

    protected async Task<ExchangeSnapshot> WaitForStatus(string status)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var s = Spawner.Snapshot("general");
            if (s.Status == status) return s;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Exchange never reached '{status}'; last was '{Spawner.Snapshot("general").Status}'.");
    }

    /// <summary>Writes a fixture skill straight into the store's directory and records its fingerprint
    /// in the same `chopitup.db` the running hub uses (via the DI-registered <see cref="ChopDb"/>),
    /// exactly the shape Task 5's --import-skill produces. Row 11 fixtures only - no third-party
    /// skill text (D-g).</summary>
    protected void WriteSkill(string name, string body)
    {
        var dir = Path.Combine(_dir, "skills", name);
        Directory.CreateDirectory(dir);
        var bytes = new UTF8Encoding(false).GetBytes(body);
        File.WriteAllBytes(Path.Combine(dir, "SKILL.md"), bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        new SkillHashes(_host.Services.GetRequiredService<ChopDb>()).Record(name, hash, "test-fixture");
    }

    protected async Task<string> MakeRoom(string id)
    {
        var dir = Path.Combine(_host.RoomsRoot, id);
        Assert.True(await new GitTrail(dir).InitAsync());
        await GitConfig(dir, "user.name", "Room Owner");
        await GitConfig(dir, "user.email", "room-owner@example.test");
        _host.Services.GetRequiredService<MessageStore>().CreateRoom(id, id.ToUpperInvariant(), dir);
        return dir;
    }

    protected async Task PostAsOwnerIn(string room, string body)
    {
        var r = await _host.Client.PostAsJsonAsync($"api/rooms/{room}/messages", new { body });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
    }

    protected async Task<List<(string Author, string Body)>> MessagesIn(string room)
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync($"api/rooms/{room}/messages?afterId=0&limit=200"));
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("authorId").GetString()!, m.GetProperty("body").GetString()!)).ToList();
    }

    protected async Task<(string Author, string Body)> WaitForMessageIn(string room, Func<(string Author, string Body), bool> match)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var hit = (await MessagesIn(room)).FirstOrDefault(match);
            if (hit != default) return hit;
            await Task.Delay(100);
        }
        throw new TimeoutException($"No matching message in '{room}' within {Wait}");
    }

    protected async Task<ExchangeView> WaitForExchangeStatusIn(string room, long root, string status)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var exchange = Spawner.Snapshot(room).Exchanges?.SingleOrDefault(e => e.RootMessageId == root);
            if (exchange?.Status == status) return exchange;
            await Task.Delay(50);
        }
        var last = Spawner.Snapshot(room).Exchanges?.SingleOrDefault(e => e.RootMessageId == root)?.Status ?? "missing";
        throw new TimeoutException($"Exchange #{root} in '{room}' never reached '{status}'; last was '{last}'.");
    }

    /// <summary>A room's git trail whose `git merge` call (and only that call - never `merge-base`,
    /// `worktree add`, a commit, and so on) blocks until <see cref="Hold"/> is released, so a test can
    /// deterministically catch a worktree close mid-merge. <see cref="MergeAttempted"/> completes the
    /// instant the merge call actually starts, so a test can wait for the close to have genuinely
    /// reached it before acting, rather than racing it.</summary>
    protected sealed class DelayingMergeRunner : IProcessRunner
    {
        private readonly IProcessRunner _inner = new ProcessRunner();
        public readonly TaskCompletionSource Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource MergeAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation)
        {
            if (spec.Arguments.Contains("merge"))
            {
                MergeAttempted.TrySetResult();
                await Hold.Task;
            }
            return await _inner.RunAsync(spec, timeout, cancellation);
        }
    }

    protected static async Task GitConfig(string dir, string key, string value)
    {
        var r = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, ["config", key, value], new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
    }

    protected async Task PostAsIn(string participant, string room, string body)
    {
        await using var client = await _host.ClientFor(participant);
        HubTestHost.Json(await client.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = room, ["body"] = body, ["client_key"] = Guid.NewGuid().ToString() }));
    }

    protected RunStore Runs => _host.Services.GetRequiredService<RunStore>();

    protected void Mode(string mode, string first = "sonnet", string? second = "opus")
    {
        var store = _host.Services.GetRequiredService<MessageStore>();
        Assert.True(store.SetMode("general", new RoomModeSettings(mode, first, second, store.GetRoom("general")!.EffectiveMode.Revision), ChopDb.SeedRoster));
    }
}
