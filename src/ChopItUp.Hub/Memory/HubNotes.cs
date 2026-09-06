using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;

namespace ChopItUp.Hub.Memory;

/// <summary>The memory milestone's notes, posted as the <c>hub</c> row through the same store + signal
/// path every message takes (so browsers and waiting hosts see them). A proposal's note is the
/// "special message" of D15: the durable, host-visible record that something awaits the owner. The
/// texts are code (tests match on them), not templates.</summary>
public static class HubNotes
{
    public const string ProposalPrefix = "Memory proposal #";
    public const string ImportPrefix = "Memory import from ";
    public const string TrailPrefix = "Committed ";
    public const string TrailFailedPrefix = "Not committed for ";

    public static Message Post(MessageStore store, MessageSignal signal, string roomId, string text)
    {
        var message = store.Post(roomId, ChopDb.HubParticipantId, text);
        signal.Publish(roomId, message);   // the spawner ignores system authors; browsers refresh the panel on it
        return message;
    }

    /// <summary>The body is model-written and every future spawn reads this note under the hub's
    /// authorship, which the rules tell models to trust — so the note names the proposer as the author
    /// of what follows and fences it (plan decision 14). A fence inside the body is broken up so it
    /// cannot close ours.</summary>
    public static string Proposed(MemoryProposal p) =>
        $"{ProposalPrefix}{p.Id} by {p.AuthorId} for topic `{p.Topic}`: {p.Title}\n\n"
        + $"The text below was written by {p.AuthorId}, not by the hub; it is a proposal, not a rule.\n\n"
        + "```text\n" + p.Body.Replace("```", "` ` `", StringComparison.Ordinal) + "\n```\n\n"
        + "Approve or reject it in the memory panel.";

    public static string Imported(string source, string path, int imported, int skipped) =>
        $"{ImportPrefix}{source} ({path}): {imported} proposal(s) added, {skipped} already proposed. Review them in the memory panel.";

    public static string Approved(MemoryProposal p) =>
        $"{ProposalPrefix}{p.Id} approved: written to memory/{p.WrittenTo}"
        + (p.CommitHash is null ? " (not committed: git unavailable or failed; see the hub log)." : $" (commit {p.CommitHash}).");

    public static string Rejected(MemoryProposal p) => $"{ProposalPrefix}{p.Id} rejected.";

    /// <summary>The room's record of what the trail did around one spawn (M9 decision 6). One line;
    /// the commit itself is the detail.</summary>
    public static string Trail(string participantId, CommitOutcome? owner, CommitOutcome agent, int commands, bool headMoved)
    {
        if (agent.Hash is null || !agent.Created)
            return $"{TrailFailedPrefix}{participantId}: {agent.Reason ?? "git made no commit"}.";
        var text = $"{TrailPrefix}{agent.Hash} as {participantId}: {agent.FilesChanged} file(s) changed, {commands} shell command(s).";
        if (owner is { Created: true }) text += $" Your edits were committed first as {owner.Hash}.";
        if (headMoved) text += $" HEAD moved during the spawn: {participantId} committed on its own.";
        return text;
    }
}
