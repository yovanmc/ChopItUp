using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Security;
using ChopItUp.Hub.Spawning;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests.Security;

/// <summary>Row 29 Task 3 / issues/03-middleware-check.md: the check inside
/// <see cref="BearerTokenMiddleware"/> that refuses an owner-class bearer presented from inside a
/// spawn. <see cref="FakeOwnerPeerCheck"/> answers whatever verdict the test sets, so these tests bind
/// the middleware's own rule (what it does with a verdict) without needing a real child process — that
/// is <c>OwnerPeerCheckEndToEndTests</c>'s job (a later task).</summary>
public sealed class OwnerPeerCheckMiddlewareTests
{
    private sealed class FakeOwnerPeerCheck : IOwnerPeerCheck
    {
        public OwnerPeerVerdict Verdict { get; set; } = new OwnerPeerVerdict.Allowed();
        public OwnerPeerVerdict Check(ConnectionInfo connection) => Verdict;
    }

    private static async Task<(HubTestHost Host, FakeOwnerPeerCheck Fake)> StartAsync(bool ownerPeerCheckEnabled = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_ownerpeer_" + Guid.NewGuid().ToString("N"));
        var fake = new FakeOwnerPeerCheck();
        var host = await HubTestHost.StartAsync(dir, ownerPeerCheck: fake, ownerPeerCheckEnabled: ownerPeerCheckEnabled);
        return (host, fake);
    }

    private static async Task<JsonElement> ErrorBodyOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error");
    }

    private static async Task<List<string?>> MessageBodiesAsync(HubTestHost host) =>
        (await host.Client.GetFromJsonAsync<JsonElement>("api/rooms/general/messages"))
            .GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("body").GetString()).ToList();

    [Fact]
    public async Task Owner_class_from_inside_a_spawn_is_403_on_api_with_the_fixed_error_and_writes_nothing()
    {
        var (host, fake) = await StartAsync();
        await using var _ = host;
        fake.Verdict = new OwnerPeerVerdict.InsideSpawn(4242, new SpawnJobEntry(4242, "opus/x", "general", "opus"), NoteDue: true);
        host.AuthorizeAs("owner");

        var response = await host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "forged" });
        var error = await ErrorBodyOf(response);
        Assert.Equal(BearerTokenMiddleware.InsideSpawnError, error.GetString());

        Assert.DoesNotContain("forged", await MessageBodiesAsync(host));
    }

    [Fact]
    public async Task A_second_refusal_against_the_same_job_posts_no_second_note()
    {
        var (host, fake) = await StartAsync();
        await using var _ = host;
        fake.Verdict = new OwnerPeerVerdict.InsideSpawn(4242, new SpawnJobEntry(4242, "opus/x", "general", "opus"), NoteDue: false);
        host.AuthorizeAs("owner");

        var before = (await MessageBodiesAsync(host)).Count;
        await ErrorBodyOf(await host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "forged" }));
        var after = (await MessageBodiesAsync(host)).Count;
        Assert.Equal(before, after);
    }

    /// <summary>Observed this session (not previously measured — the plan flags this shape as
    /// unverified): <c>McpClient.CreateAsync</c> against a 403 response throws a
    /// <see cref="System.Net.Http.HttpRequestException"/> whose <c>StatusCode</c> is
    /// <see cref="HttpStatusCode.Forbidden"/> and whose <c>Message</c> embeds the response body,
    /// including <see cref="BearerTokenMiddleware.InsideSpawnError"/> — the underlying
    /// <c>HttpClient.EnsureSuccessStatusCode</c> failure from the transport's initialize call, not an
    /// MCP-protocol-level exception type.</summary>
    [Fact]
    public async Task Owner_remote_from_inside_a_spawn_is_403_on_mcp()
    {
        var (host, fake) = await StartAsync();
        await using var _ = host;
        fake.Verdict = new OwnerPeerVerdict.InsideSpawn(4242, new SpawnJobEntry(4242, "opus/x", "general", "opus"), NoteDue: true);

        var ex = await Record.ExceptionAsync(() => host.ClientFor(ChopDb.OwnerRemoteParticipantId));
        var http = Assert.IsType<System.Net.Http.HttpRequestException>(ex);
        Assert.Equal(HttpStatusCode.Forbidden, http.StatusCode);
        Assert.Contains(BearerTokenMiddleware.InsideSpawnError, http.Message);
    }

    [Fact]
    public async Task The_room_note_names_the_spawn_and_pid()
    {
        var (host, fake) = await StartAsync();
        await using var _ = host;
        fake.Verdict = new OwnerPeerVerdict.InsideSpawn(4242, new SpawnJobEntry(4242, "opus/x", "general", "opus"), NoteDue: true);
        host.AuthorizeAs("owner");

        await ErrorBodyOf(await host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "forged" }));

        var messages = (await host.Client.GetFromJsonAsync<JsonElement>("api/rooms/general/messages"))
            .GetProperty("messages").EnumerateArray().ToList();
        var last = messages[^1];
        Assert.Equal(ChopDb.HubParticipantId, last.GetProperty("authorId").GetString());
        Assert.Equal("Refused an owner-class credential presented from inside @opus's spawn (pid 4242).", last.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_spawn_job_with_no_room_writes_no_note()
    {
        var (host, fake) = await StartAsync();
        await using var _ = host;
        fake.Verdict = new OwnerPeerVerdict.InsideSpawn(4242, new SpawnJobEntry(4242, "opus/x", null, null), NoteDue: true);
        host.AuthorizeAs("owner");

        var before = (await MessageBodiesAsync(host)).Count;
        await ErrorBodyOf(await host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "forged" }));
        var after = (await MessageBodiesAsync(host)).Count;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Unresolvable_is_403_with_the_reason()
    {
        var (host, fake) = await StartAsync();
        await using var _ = host;
        fake.Verdict = new OwnerPeerVerdict.Unresolvable("test");
        host.AuthorizeAs("owner");

        var error = (await ErrorBodyOf(await host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "forged" }))).GetString();
        Assert.StartsWith(BearerTokenMiddleware.UnresolvableErrorPrefix, error);
        Assert.Contains("(test)", error);
    }

    [Fact]
    public async Task A_spawnable_row_on_mcp_is_untouched_by_the_verdict()
    {
        var (host, fake) = await StartAsync();
        await using var _ = host;
        fake.Verdict = new OwnerPeerVerdict.InsideSpawn(4242, new SpawnJobEntry(4242, "opus/x", "general", "opus"), NoteDue: true);

        await using var client = await host.ClientFor("opus");
        var result = await client.CallToolAsync("list_rooms", new Dictionary<string, object?>());
        Assert.NotEqual(true, result.IsError);
    }

    [Fact]
    public async Task Allowed_leaves_the_existing_path_unchanged()
    {
        var (host, fake) = await StartAsync();
        await using var _ = host;
        fake.Verdict = new OwnerPeerVerdict.Allowed();
        host.AuthorizeAs("owner");

        var response = await host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "fine" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task With_the_check_off_an_inside_spawn_verdict_is_ignored()
    {
        var (host, fake) = await StartAsync(ownerPeerCheckEnabled: false);
        await using var _ = host;
        fake.Verdict = new OwnerPeerVerdict.InsideSpawn(4242, new SpawnJobEntry(4242, "opus/x", "general", "opus"), NoteDue: true);
        host.AuthorizeAs("owner");

        var response = await host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "fine" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.IsType<DisabledOwnerPeerCheck>(host.Services.GetRequiredService<IOwnerPeerCheck>());
    }
}
