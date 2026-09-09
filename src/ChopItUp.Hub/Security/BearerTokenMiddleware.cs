using System.Text.Json;

namespace ChopItUp.Hub.Security;

/// <summary>Guards <c>/mcp</c>, plus — as of D1 (M25 task 6) — exactly
/// <c>POST /api/skills/proposals/{id}/approve|reject</c>: a valid bearer token sets
/// <see cref="ParticipantKey"/> in <see cref="HttpContext.Items"/>; anything else is 401 before any
/// MCP or decision-endpoint code runs. <c>GET /api/skills/proposals</c> and every other <c>/api</c>
/// route stay unauthenticated — widening auth to the whole surface is roadmap row 13's work, not
/// this milestone's. Authorization (owner-or-owner-remote, 403 otherwise) is the decision handlers'
/// own job in <c>SkillsApi</c>; this middleware only answers "is there a credential and does it
/// resolve".</summary>
public sealed class BearerTokenMiddleware(RequestDelegate next, TokenStore tokens)
{
    public const string ParticipantKey = "chopitup.participant";
    private static readonly byte[] Unauthorized = JsonSerializer.SerializeToUtf8Bytes(new { error = "unauthorized" });

    /// <summary>Whether <paramref name="request"/> falls on the guarded surface. Matched on the
    /// request's own path segments and method, never a string prefix or suffix: <c>/mcp</c> keeps its
    /// existing prefix match (an MCP session may legitimately request any sub-path under it), while
    /// the two decision routes require an exact five-segment shape — <c>api / skills / proposals /
    /// &lt;numeric id&gt; / approve-or-reject</c> — on a POST. That means a sibling path that merely
    /// starts with "/api/skills/proposals" (extra segments, a different verb, a non-numeric id, or a
    /// same-prefixed neighbour directory) is never swept in, and <c>GET /api/skills/proposals</c> — the
    /// unauthenticated listing — is excluded by the method check alone.</summary>
    internal static bool RequiresAuth(HttpRequest request)
    {
        if (request.Path.StartsWithSegments("/mcp")) return true;
        if (!HttpMethods.IsPost(request.Method)) return false;

        var segments = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is not { Length: 5 }) return false;
        return string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[1], "skills", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[2], "proposals", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(segments[3], out _)
            && (string.Equals(segments[4], "approve", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segments[4], "reject", StringComparison.OrdinalIgnoreCase));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!RequiresAuth(context.Request))
        {
            await next(context);
            return;
        }

        string? header = context.Request.Headers.Authorization;
        const string prefix = "Bearer ";
        if (header is not null && header.StartsWith(prefix, StringComparison.Ordinal)
            && tokens.TryResolve(header[prefix.Length..].Trim(), out var participant))
        {
            context.Items[ParticipantKey] = participant;
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer realm=\"chopitup\"";
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(Unauthorized, context.RequestAborted);
    }
}
