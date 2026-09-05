namespace ChopItUp.Core.Model;

public sealed record Message(long Id, string RoomId, string AuthorId, string Body, DateTimeOffset CreatedAt);

/// <summary>The outcome of a post. <see cref="Deduplicated"/> is true when the caller's client_key
/// matched a message already stored, so nothing new was written and <see cref="Message"/> is the
/// original.</summary>
public sealed record PostResult(Message Message, bool Deduplicated);

public sealed record Room(string Id, string Name, DateTimeOffset CreatedAt, long LastMessageId, int MessageCount);

/// <summary>A page of messages in ascending id order. <see cref="NextAfterId"/> is the value to pass
/// as <c>afterId</c> to continue; it equals the request's afterId when the page is empty.
/// <see cref="Cursor"/> is where this participant's stored cursor stands after the call — equal to
/// NextAfterId on an implicit read, and the untouched stored value when an explicit afterId was
/// given. It exists so a caller whose previous response was lost can tell that its cursor has run
/// ahead of what it actually processed.</summary>
public sealed record MessagePage(IReadOnlyList<Message> Messages, long NextAfterId, bool HasMore, long Cursor = 0);

/// <summary>One roster row. <see cref="Host"/> is which program speaks for this row: <c>human</c>,
/// <c>claude</c> (Claude Desktop / Claude Code) or <c>codex</c>. <see cref="Model"/> is null for the
/// human and for the app-backed rows (whatever model the app has selected), and the model name the
/// host takes on its command line for a spawn row (M5). <see cref="Note"/> is owner-facing text
/// shown beside the row in the generated README, e.g. the usage-credit warning on <c>fable</c>.</summary>
public sealed record Participant(string Id, string DisplayName, string Kind, string Host, string? Model, string? Note);
