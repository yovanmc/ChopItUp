namespace ChopItUp.Core.Model;

public sealed record Message(long Id, string RoomId, string AuthorId, string Body, DateTimeOffset CreatedAt);

/// <summary>The outcome of a post. <see cref="Deduplicated"/> is true when the caller's client_key
/// matched a message already stored, so nothing new was written and <see cref="Message"/> is the
/// original.</summary>
public sealed record PostResult(Message Message, bool Deduplicated);

public sealed record Room(string Id, string Name, DateTimeOffset CreatedAt, long LastMessageId, int MessageCount);

/// <summary>A page of messages in ascending id order. <see cref="NextAfterId"/> is the value to pass
/// as <c>afterId</c> to continue; it equals the request's afterId when the page is empty.</summary>
public sealed record MessagePage(IReadOnlyList<Message> Messages, long NextAfterId, bool HasMore);
