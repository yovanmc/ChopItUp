using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>Row 44 (D-d): /continue posted while its target exchange is still open, or while the room
/// has nothing to continue at all. Its own fixture (pass 2 m5): the one test here holds a spawn open
/// past SpawnerServiceTests' ordinary short Fast.Timeout, so it needs a longer one that cannot be swept
/// by another test's assertions racing it.</summary>
public sealed class ContinueWhileOpenTests : IAsyncLifetime
{
    private static readonly SpawnLimits Held = new(Budget: 4, Debounce: TimeSpan.FromMilliseconds(150), MinSpacing: TimeSpan.Zero, Timeout: TimeSpan.FromSeconds(30), TranscriptMessages: 60, TranscriptChars: 24_000);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_continue_" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new();
    private HubTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await HubTestHost.StartAsync(_dir, processRunner: _runner, limits: Held);
        _host.AuthorizeAs(ChopDb.OwnerParticipantId);   // row 28: every non-GET /api call here now needs a credential
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private SpawnerService Spawner => _host.Services.GetRequiredService<SpawnerService>();

    private async Task PostAsOwner(string body)
    {
        var r = await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
    }

    private async Task<List<(string Author, string Body)>> Messages()
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/general/messages?afterId=0&limit=200"));
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("authorId").GetString()!, m.GetProperty("body").GetString()!)).ToList();
    }

    private async Task<(string Author, string Body)> WaitForMessage(Func<(string Author, string Body), bool> match)
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

    [Fact]
    public async Task R44_continue_with_nothing_or_on_an_open_exchange_posts_the_refusal_note()
    {
        await PostAsOwner("/continue");                                                          // #1
        var nothing = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("Nothing to continue"));
        Assert.Equal("Nothing to continue in this room: no exchange this hub remembers.", nothing.Body);   // #2

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (_, _, ct) => { await held.Task.WaitAsync(ct); return FakeProcessRunner.Ok("""{"result":"done"}"""); };

        await PostAsOwner("@opus hi");                                                           // #3
        await _runner.NextSpecAsync(Wait);

        await PostAsOwner("/continue");
        var stillOpen = await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.Contains("still open"));
        Assert.Equal("Exchange started at #3 is still open with 3 turn(s) left; /continue once it has concluded.", stillOpen.Body);
        Assert.False(Spawner.Snapshot("general").Continuable);

        held.SetResult();
        var deadline = DateTime.UtcNow + Wait;
        while (Spawner.Snapshot("general").Status != "concluded")
        {
            Assert.True(DateTime.UtcNow < deadline, "the exchange never concluded");
            await Task.Delay(50);
        }
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromMilliseconds(500)));   // no synthesis: opus never posted, so it is not retried
    }
}
