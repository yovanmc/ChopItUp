using System.Text.Json;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Storage;

namespace ChopItUp.Hub.Security;

/// <summary>Guards <c>/mcp</c> (any resolvable participant) and, as of row 28 Task 4, every
/// <c>/api</c> request whose method is not GET/HEAD/OPTIONS: a valid bearer token sets
/// <see cref="ParticipantKey"/> in <see cref="HttpContext.Items"/>; anything else is 401 before any
/// MCP or handler code runs. This supersedes M25 task 6's narrower rule (exactly
/// <c>POST /api/skills/proposals/{id}/approve|reject</c>, everything else on <c>/api</c> left open) —
/// that was itself the intended scope for THAT milestone, not a permanent boundary; row 28 widens it
/// because an owner-attributed write must not be forgeable by anything that can merely reach the
/// loopback address. <c>GET</c>/<c>HEAD</c>/<c>OPTIONS</c> on <c>/api</c> stay unauthenticated (AC2).
/// Authorization is split by surface: on <c>/mcp</c> any resolved participant may proceed (a model
/// participant authenticates its own tool calls); on a guarded <c>/api</c> write, only a participant
/// resolving to <see cref="ChopDb.OwnerParticipantId"/> or <see cref="ChopDb.OwnerRemoteParticipantId"/>
/// is let through — anything else that resolves is 403, not 401 (acceptance 1's "own hand" clause).</summary>
public sealed class BearerTokenMiddleware(RequestDelegate next, TokenStore tokens, IOwnerPeerCheck peers, MessageStore store, MessageSignal signal)
{
    public const string ParticipantKey = "chopitup.participant";
    private static readonly byte[] Unauthorized = JsonSerializer.SerializeToUtf8Bytes(new { error = "unauthorized" });

    /// <summary>Row 29 AC1's fixed body.</summary>
    public const string InsideSpawnError = "owner credential refused: presented from inside a spawn";

    /// <summary>Row 29 AC5's fixed prefix; the reason is appended in parentheses.</summary>
    public const string UnresolvableErrorPrefix = "owner credential refused: peer process unresolvable";

    /// <summary>D2: the pair the pre-row-29 owner-only branch already named, factored out so the new
    /// check and that branch agree on what "owner-class" means.</summary>
    internal static bool IsOwnerClass(string participant) =>
        participant is ChopDb.OwnerParticipantId or ChopDb.OwnerRemoteParticipantId;

    /// <summary>Whether <paramref name="request"/> falls on the guarded surface. <c>/mcp</c> keeps its
    /// existing prefix match (an MCP session may legitimately request any sub-path under it); every
    /// other <c>/api</c> request needs a credential unless its method is GET, HEAD or OPTIONS — a
    /// blanket rule by method, not a route list, so a new write endpoint is guarded by construction
    /// rather than by someone remembering to add it here (row 28 D-28-a).</summary>
    internal static bool RequiresAuth(HttpRequest request)
    {
        if (request.Path.StartsWithSegments("/mcp")) return true;
        if (!request.Path.StartsWithSegments("/api")) return false;
        return !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);
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
            // Row 29, D4: the owner-peer check runs right after a bearer resolves and before the
            // existing /api-only branch below, on /mcp as well as /api — the phone session's
            // owner-remote bearer posts through /mcp, so the check must run there too.
            if (IsOwnerClass(participant))
            {
                switch (peers.Check(context.Connection))
                {
                    case OwnerPeerVerdict.InsideSpawn inside:
                        Console.Error.WriteLine($"auth: refused owner-class '{participant}' from pid {inside.Pid} inside spawn '{inside.Entry.Label}'");
                        if (inside.NoteDue && inside.Entry.RoomId is { } room)
                            PostNote(room, $"Refused an owner-class credential presented from inside @{inside.Entry.ParticipantId ?? "?"}'s spawn (pid {inside.Pid}).");
                        await Refuse(context, InsideSpawnError);
                        return;
                    case OwnerPeerVerdict.Unresolvable u:
                        Console.Error.WriteLine($"auth: refused owner-class '{participant}': peer unresolvable ({u.Reason})");
                        await Refuse(context, $"{UnresolvableErrorPrefix} ({u.Reason})");
                        return;
                }
            }

            // /mcp lets any resolved participant through (a model authenticates its own tool calls);
            // every guarded /api write needs the owner's own hand, from either device.
            bool isOwnerOnly = !context.Request.Path.StartsWithSegments("/mcp");
            if (isOwnerOnly && !IsOwnerClass(participant))
            {
                await Refuse(context, "forbidden");
                return;
            }

            context.Items[ParticipantKey] = participant;
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer realm=\"chopitup\"";
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(Unauthorized, context.RequestAborted);
    }

    private static Task Refuse(HttpContext context, string error)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        return context.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { error }), context.RequestAborted).AsTask();
    }

    /// <summary>D6: hub-owned text, posted through the same <c>MessageStore.Post</c> +
    /// <c>MessageSignal.Publish</c> pair <c>RunTools.PostNote</c> uses, as <see cref="ChopDb.HubParticipantId"/>.
    /// A posting failure never blocks the refusal itself — it is logged and swallowed, the same rule
    /// every other hub note in this codebase follows.</summary>
    private void PostNote(string roomId, string text)
    {
        try
        {
            var message = store.Post(roomId, ChopDb.HubParticipantId, text);
            signal.Publish(roomId, message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"auth: refusal note to '{roomId}' not posted ({e.GetType().Name}: {e.Message}): {text.Split('\n')[0]}");
        }
    }
}
