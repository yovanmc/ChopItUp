using ChopItUp.Hub.Security;
using Microsoft.AspNetCore.Http;

namespace ChopItUp.Hub.Tests;

/// <summary>M25 ticket 06 / plan Task 6, D1: the guarded-route matcher itself, isolated from a live
/// host. D1 widens the guard from "/mcp" alone to also cover exactly
/// <c>POST /api/skills/proposals/{id}/approve|reject</c> — matched on exact path segments and method,
/// never a string prefix or suffix, so a route that merely starts with the guarded prefix is neither
/// swept in nor left out by accident (ticket 06's explicit ask for a test of the matching itself).</summary>
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
    public void POST_to_a_decision_route_requires_auth(string path) => Assert.True(Requires("POST", path));

    [Fact]
    public void Mcp_still_requires_auth_unchanged() => Assert.True(Requires("POST", "/mcp"));

    [Fact]
    public void GET_the_listing_does_not_require_auth() => Assert.False(Requires("GET", "/api/skills/proposals"));

    [Fact]
    public void POST_the_bare_listing_path_does_not_require_auth() => Assert.False(Requires("POST", "/api/skills/proposals"));

    [Fact]
    public void GET_on_a_decision_route_does_not_require_auth() => Assert.False(Requires("GET", "/api/skills/proposals/1/approve"));

    [Theory]
    [InlineData("/api/skills/proposals-export/1/approve")]   // sibling that merely starts with the guarded prefix's leading segments
    [InlineData("/api/skills/proposals/1/approve/extra")]    // one trailing segment too many
    [InlineData("/api/skills/proposals/1/approved")]         // a verb that is not exactly "approve"
    [InlineData("/api/skills/proposals/abc/approve")]        // non-numeric id
    [InlineData("/api/memory/proposals/1/approve")]          // a different, deliberately still-unauthenticated /api route
    public void A_route_that_merely_resembles_the_guarded_shape_does_not_require_auth(string path) => Assert.False(Requires("POST", path));
}
