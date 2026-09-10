using System.Text;
using System.Text.RegularExpressions;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Security;

namespace ChopItUp.Hub.Web;

/// <summary>JSON endpoints under <c>/api</c> for the web UI. Brief decision D2's "no auth, loopback is
/// the boundary" is superseded by row 28: <c>BearerTokenMiddleware</c> now guards every non-GET
/// request here too, and a write is authored from whichever participant the bearer resolved to
/// (<see cref="BearerTokenMiddleware.ParticipantKey"/> in <see cref="HttpContext.Items"/>), falling
/// back to the owner id only when it is unset (a defensive default; the middleware never lets a
/// guarded write through without setting it). Every write still goes through
/// <see cref="MessageStore.Post(string,string,string)"/>, the same path the MCP tools use, so the
/// cursor and broadcast rules cannot drift.</summary>
public static class ChatApi
{
    /// <summary>A line that opens a new speaker's turn during transcript import: a short label
    /// followed by a colon, e.g. "Claude:", "CODEX:   ", "random Name:". Deliberately permissive —
    /// this only decides where to SPLIT the paste into messages; it never decides who a message is
    /// authored by (that is always the roster's human row, per D1).</summary>
    private static readonly Regex SpeakerHeader = new(@"^[A-Za-z][\w .'-]{0,39}:", RegexOptions.Compiled);

    public static void MapChatApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/rooms", GetRooms);
        api.MapGet("/rooms/{roomId}/messages", GetMessages);
        api.MapPost("/rooms/{roomId}/messages", PostMessage);
        api.MapPost("/rooms/{roomId}/import", PostImport);
        api.MapGet("/rooms/{roomId}/export", GetExport);
        api.MapGet("/participants", GetParticipants);
    }

    private static IResult GetRooms(MessageStore store, ParticipantStore participants, bool archived = false) =>
        Results.Json(store.ListRooms(includeArchived: archived, unreadFor: participants.OwnerId()).Select(MapRoom));

    private static IResult GetParticipants(ParticipantStore participants) =>
        Results.Json(participants.List().Select(p => new { p.Id, p.DisplayName, p.Kind, p.Host, p.Model, Classes = ParticipantClasses.Parse(p.Classes) }));

    private static IResult GetMessages(string roomId, MessageStore store, long afterId = 0, int limit = MessageStore.DefaultLimit)
    {
        if (!store.RoomExists(roomId)) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        var page = store.Read(roomId, afterId, limit);
        return Results.Json(new
        {
            messages = page.Messages.Select(MapMessage),
            nextAfterId = page.NextAfterId,
            hasMore = page.HasMore,
        });
    }

    /// <summary>B3, row 28 pass 2 finding 13: authored as whichever participant's bearer resolved the
    /// request (the owner or owner-remote — <see cref="BearerTokenMiddleware"/> refuses anything
    /// else), falling back to the owner id only when the key is unset, and stored through the same
    /// <c>MessageStore.Post</c> the MCP tools use, so the cursor and broadcast rules cannot drift. No
    /// client_key on this surface — a browser POST has no story for "was this delivered", unlike an
    /// MCP tool call.</summary>
    private static IResult PostMessage(string roomId, PostBody body, HttpContext httpContext, MessageStore store, MessageSignal signal, ParticipantStore participants)
    {
        if (!store.RoomExists(roomId)) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        if (string.IsNullOrWhiteSpace(body.Body)) return Results.BadRequest(new { error = "body is empty." });
        var authorId = AuthorId(httpContext, participants);
        var message = store.Post(roomId, authorId, body.Body);   // 3-arg overload: no client_key, always inserts
        signal.Publish(roomId, message);
        return Results.Json(MapMessage(message), statusCode: StatusCodes.Status201Created);
    }

    /// <summary>The participant <see cref="BearerTokenMiddleware"/> resolved the caller's bearer to,
    /// or the owner id when the key is unset — a defensive fallback only, since the middleware never
    /// lets a guarded write reach here without setting it.</summary>
    private static string AuthorId(HttpContext httpContext, ParticipantStore participants) =>
        httpContext.Items.TryGetValue(BearerTokenMiddleware.ParticipantKey, out var raw) && raw is string participantId
            ? participantId
            : participants.OwnerId();

    /// <summary>D1, binding: every imported message is authored as the roster's one human row; the
    /// original speaker label (if any) stays as plain text inside the body. A line matching
    /// <see cref="SpeakerHeader"/> starts a new message; everything before the first such line, or the
    /// whole paste when no line matches, becomes one message.</summary>
    private static IResult PostImport(string roomId, ImportBody body, MessageStore store, MessageSignal signal, ParticipantStore participants)
    {
        if (!store.RoomExists(roomId)) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        if (string.IsNullOrWhiteSpace(body.Text)) return Results.BadRequest(new { error = "text is empty." });
        var turns = SplitIntoTurns(body.Text);
        if (turns.Count == 0) return Results.BadRequest(new { error = "nothing to import." });

        var humanId = participants.OwnerId();
        var posted = new List<Message>(turns.Count);
        foreach (var turn in turns)
        {
            var message = store.Post(roomId, humanId, turn);   // D1: always the human row, never the label in the text
            signal.Publish(roomId, message);
            posted.Add(message);
        }
        return Results.Json(new { messages = posted.Select(MapMessage) }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult GetExport(string roomId, MessageStore store)
    {
        if (!store.RoomExists(roomId)) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        return Results.Text(ExportMarkdown(roomId, store), "text/markdown");
    }

    internal static List<string> SplitIntoTurns(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        if (!lines.Any(l => SpeakerHeader.IsMatch(l)))
        {
            var whole = text.Trim();
            return whole.Length == 0 ? [] : [whole];
        }

        var turns = new List<string>();
        var current = new List<string>();
        foreach (var line in lines)
        {
            if (SpeakerHeader.IsMatch(line))
            {
                FlushTurn(turns, current);
                current = [];
            }
            current.Add(line);
        }
        FlushTurn(turns, current);
        return turns;
    }

    private static void FlushTurn(List<string> turns, List<string> current)
    {
        if (current.Count == 0) return;
        var body = string.Join('\n', current).Trim();
        if (body.Length > 0) turns.Add(body);
    }

    private static string ExportMarkdown(string roomId, MessageStore store)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(roomId).Append("\n\n");
        long afterId = 0;
        while (true)
        {
            var page = store.Read(roomId, afterId, MessageStore.MaxLimit);
            foreach (var m in page.Messages)
            {
                sb.Append("## ").Append(m.AuthorId).Append(" — ").Append(Timestamps.Stamp(m.CreatedAt)).Append("\n\n");
                sb.Append(m.Body).Append("\n\n---\n\n");
            }
            if (!page.HasMore) break;
            afterId = page.NextAfterId;
        }
        return sb.ToString();
    }

    private static object MapMessage(Message m) => new { m.Id, m.RoomId, m.AuthorId, m.Body, m.CreatedAt };
    internal static object MapRoom(Room r) => new { r.Id, r.Name, r.CreatedAt, r.MessageCount, r.LastMessageId, r.Directory, r.ArchivedAt, r.LastActivityAt, r.Unread };

    internal sealed record PostBody(string? Body);
    internal sealed record ImportBody(string? Text);
}
