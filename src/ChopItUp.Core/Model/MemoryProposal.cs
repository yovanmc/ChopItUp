namespace ChopItUp.Core.Model;

/// <summary>One proposed memory entry (M10). <see cref="Status"/> is <c>pending</c>, <c>approved</c>
/// or <c>rejected</c>. <see cref="Source"/> is null for a proposal made in the room and
/// <c>&lt;vendor&gt;:&lt;path&gt;</c> for an import. <see cref="WrittenTo"/> (path relative to the memory
/// root) and <see cref="CommitHash"/> (null when git was unavailable) are set on approval.
/// Row 18: <see cref="Kind"/> is <c>append</c> or <c>supersede</c> (row 23 adds <c>rewrite</c>);
/// <see cref="Replaces"/> is the title a supersede retires; <see cref="Flags"/> are review hints
/// computed once at creation, comma-joined (see <c>ChopItUp.Core.Memory.ProposalFlags</c>).</summary>
public sealed record MemoryProposal(
    long Id, string RoomId, string AuthorId, string Topic, string Title, string Body, string Status, string? Source,
    DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt, string? WrittenTo, string? CommitHash,
    string Kind = "append", string? Replaces = null, string? Flags = null);
