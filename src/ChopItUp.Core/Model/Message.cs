namespace ChopItUp.Core.Model;

public sealed record Message(long Id, string RoomId, string AuthorId, string Body, DateTimeOffset CreatedAt);

public sealed record Room(string Id, string Name, DateTimeOffset CreatedAt, long LastMessageId, int MessageCount);

/// <summary>A page of messages in ascending id order. <see cref="NextAfterId"/> is the value to pass
/// as <c>afterId</c> to continue; it equals the request's afterId when the page is empty.</summary>
public sealed record MessagePage(IReadOnlyList<Message> Messages, long NextAfterId, bool HasMore);
