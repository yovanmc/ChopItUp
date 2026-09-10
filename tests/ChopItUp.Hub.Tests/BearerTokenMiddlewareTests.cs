using ChopItUp.Hub.Security;
using Microsoft.AspNetCore.Http;

namespace ChopItUp.Hub.Tests;

/// <summary>Row 28 Task 4, D-28-a: the guarded-route matcher itself, isolated from a live host.
/// Supersedes M25 ticket 06's narrower rule (exactly <c>POST /api/skills/proposals/{id}/approve|reject</c>,
/// matched on exact path segments) — that route list is gone; the rule is now "authenticate by
/// method, not by route list": every <c>/api</c> request needs a credential unless its method is GET,
/// HEAD or OPTIONS, so a new write endpoint is guarded by construction rather than by someone
/// remembering to add it to a list here.</summary>
public sealed class BearerTokenMiddlewareTests
{
    private static bool Requires(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return BearerTokenMiddleware.RequiresAuth(context.Request);
    }

    [Theory]
    [InlineData("/api/skills/proposals/1/approve")]
    [InlineData("/api/skills/proposals/42/reject")]
    [InlineData("/api/rooms/general/messages")]
    [InlineData("/api/memory/proposals/1/approve")]     // row 28: this used to be the deliberately-unauthenticated example; now it is guarded like every other write
    [InlineData("/api/rooms")]
    [InlineData("/api/anything-not-yet-invented")]       // D-28-a: a new write route is guarded by construction, not by being added to a list
    public void POST_to_any_api_route_requires_auth(string path) => Assert.True(Requires("POST", path));

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void A_non_get_method_other_than_post_also_requires_auth(string method) => Assert.True(Requires(method, "/api/memory/proposals"));

    [Fact]
    public void Mcp_still_requires_auth_unchanged() => Assert.True(Requires("POST", "/mcp"));

    [Theory]
    [InlineData("/api/skills/proposals")]
    [InlineData("/api/skills/proposals/1/approve")]
    [InlineData("/api/rooms/general/messages")]
    [InlineData("/api/memory/proposals")]
    public void GET_on_any_api_route_does_not_require_auth(string path) => Assert.False(Requires("GET", path));

    [Theory]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void HEAD_and_OPTIONS_on_an_api_route_do_not_require_auth(string method) => Assert.False(Requires(method, "/api/rooms/general/messages"));

    [Fact]
    public void A_path_outside_api_and_mcp_never_requires_auth() => Assert.False(Requires("POST", "/health"));
}
