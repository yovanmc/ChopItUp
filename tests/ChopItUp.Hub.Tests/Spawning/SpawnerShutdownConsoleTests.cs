using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Skills;
using ChopItUp.Hub.Spawning;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>Swaps the process-wide <see cref="Console.Error"/>, so it runs in the non-parallel collection.</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class SpawnerShutdownConsoleTests : SpawnerServiceTestBase
{
    [Fact]
    public async Task R35_a_worktree_close_that_outlives_shutdown_logs_its_note_to_stderr()
    {
        // A close still running when the hub shuts down finds its event channel already completed: the
        // note it carries must be logged, not silently dropped.
        var delayingRunner = new DelayingMergeRunner();
        var localDir = _dir + "_close_shutdown";
        await using var host = await HubTestHost.StartAsync(localDir, processRunner: _runner, limits: Fast,
            roomGit: dir => new GitTrail(dir, runner: delayingRunner));
        host.AuthorizeAs(ChopDb.OwnerParticipantId);
        var spawner = host.Services.GetRequiredService<SpawnerService>();

        var dir = Path.Combine(host.RoomsRoot, "lab-shutdown-close");
        Assert.True(await new GitTrail(dir).InitAsync());
        host.Services.GetRequiredService<MessageStore>().CreateRoom("lab-shutdown-close", "Lab", dir);

        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"type":"result","result":"done"}"""));
        var post = await host.Client.PostAsJsonAsync("api/rooms/lab-shutdown-close/messages", new { body = "@opus task A" });
        Assert.Equal(System.Net.HttpStatusCode.Created, post.StatusCode);
        await _runner.NextSpecAsync(Wait);   // opus's worktree spawn; its close now blocks on the delayed merge
        await delayingRunner.MergeAttempted.Task.WaitAsync(Wait);

        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        try
        {
            // StopAsync completes the events channel on its very first line, synchronously, before its
            // first await - so by the time this call even returns a Task, that has already happened;
            // releasing the merge only now guarantees the close's own write lands after the channel closes.
            var stopTask = spawner.StopAsync(CancellationToken.None);
            delayingRunner.Hold.SetResult();
            await stopTask.WaitAsync(Wait);
        }
        finally { Console.SetError(original); }

        Assert.Contains("lab-shutdown-close", error.ToString());
    }
}
