using System.Net;

namespace ChopItUp.Hub.Tests;

/// <summary>The hub serves the built web client (brief D3: one process, no separate front-end
/// deploy). A fabricated wwwroot stands in for the Vite output so these tests do not depend on npm
/// having run. The fallback is the risky half: it must answer client routes with the shell without
/// swallowing <c>/api</c>, <c>/hub</c>, <c>/health</c> or <c>/mcp</c>.</summary>
public sealed class SpaServingTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_spa_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;

    public async Task InitializeAsync()
    {
        var webRoot = Path.Combine(_dir, "wwwroot");
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"),
            "<!doctype html><title>Chop It Up</title><div id=\"root\"></div>");
        await File.WriteAllTextAsync(Path.Combine(webRoot, "assets", "index-abc123.js"), "console.log('chat');");
        _host = await HubTestHost.StartAsync(_dir, webRoot: webRoot);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Root_url_serves_the_chat_shell()
    {
        var response = await _host.Client.GetAsync("");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("id=\"root\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Hashed_assets_are_served_from_the_web_root()
    {
        var response = await _host.Client.GetAsync("assets/index-abc123.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("console.log", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_client_route_falls_back_to_the_shell()
    {
        var response = await _host.Client.GetAsync("rooms/general");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"root\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_routes_still_answer_and_unknown_api_paths_are_404_not_the_shell()
    {
        var rooms = await _host.Client.GetAsync("api/rooms");
        Assert.Equal(HttpStatusCode.OK, rooms.StatusCode);
        Assert.Equal("application/json", rooms.Content.Headers.ContentType?.MediaType);

        var unknown = await _host.Client.GetAsync("api/nope");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.DoesNotContain("id=\"root\"", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_and_mcp_are_not_shadowed_by_the_fallback()
    {
        var health = await _host.Client.GetAsync("health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains("\"ok\":true", await health.Content.ReadAsStringAsync());

        // Still the bearer middleware's 401, not an HTML shell.
        var mcp = await _host.Client.GetAsync("mcp/anything");
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);

        var hub = await _host.Client.GetAsync("hub/rooms/anything");
        Assert.DoesNotContain("id=\"root\"", await hub.Content.ReadAsStringAsync());
    }
}

/// <summary>A build on a machine without Node produces no wwwroot. That must degrade to "no UI",
/// never to a startup failure — the MCP surface is the part that has to keep working.</summary>
public sealed class SpaMissingClientTests
{
    [Fact]
    public async Task Without_a_built_client_the_hub_still_serves_the_api_and_404s_the_root()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_nospa_" + Guid.NewGuid().ToString("N"));
        await using var host = await HubTestHost.StartAsync(dir, webRoot: Path.Combine(dir, "absent"));

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("api/rooms")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("health")).StatusCode);
    }
}
