using System.Text.Json;

namespace ChopItUp.Hub.Security;

/// <summary>Guards <c>/mcp</c>: a valid bearer token sets <see cref="ParticipantKey"/> in
/// <see cref="HttpContext.Items"/>; anything else is 401 before any MCP code runs.</summary>
public sealed class BearerTokenMiddleware(RequestDelegate next, TokenStore tokens)
{
    public const string ParticipantKey = "chopitup.participant";
    private static readonly byte[] Unauthorized = JsonSerializer.SerializeToUtf8Bytes(new { error = "unauthorized" });

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp"))
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
