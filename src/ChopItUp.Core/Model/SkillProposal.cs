namespace ChopItUp.Core.Model;

/// <summary>One proposed skill import (M25): the same "agent proposes, owner approves" shape
/// <see cref="MemoryProposal"/> already carries, over <c>SkillImport</c>'s write path instead of
/// <c>MemoryStore</c>'s. <see cref="Status"/> is <c>pending</c>, <c>approved</c> or <c>rejected</c>.
/// <see cref="TreeSha256"/> is <c>SkillImport.ManifestDigest(SkillImport.HashSourceTree(SourceDir))</c>
/// at propose time (D5) — the one value the owner's card, the request body and the staged copy must
/// all three agree on before an approval installs anything. <see cref="ReplacesInstalled"/> and
/// <see cref="Force"/> are captured at propose time, not derived at approve time (plan pass 2
/// blocker 2): approve re-checks <see cref="ReplacesInstalled"/> against the store's live state
/// rather than trusting a stale value or hard-coding either extreme. <see cref="InstalledAt"/> stays
/// null between "marked approved" and "the install finished" — what lets a repeat approval detect and
/// complete an install that already ran, instead of re-running one (task 7's Retry arm).</summary>
public sealed record SkillProposal(
    long Id, string RoomId, string AuthorId, string Name, string SourceDir, string TreeSha256,
    bool ReplacesInstalled, bool Force, int Files, long Bytes, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt, DateTimeOffset? InstalledAt);
