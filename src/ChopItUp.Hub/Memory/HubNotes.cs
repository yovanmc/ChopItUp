using System.Text.RegularExpressions;
using ChopItUp.Core.Memory;
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
    /// cannot close ours; a memory-fence-shaped line (row 18, critique P1-4) is broken the same way so a
    /// proposal note can never carry one into a later spawn's memory injection.</summary>
    public static string Proposed(MemoryProposal p)
    {
        var quoted = p.Body.Replace("```", "` ` `", StringComparison.Ordinal);
        quoted = Regex.Replace(quoted, @"(?m)^(\s*)--- (begin|end) memory", "$1- - - $2 memory");
        return $"{ProposalPrefix}{p.Id} by {p.AuthorId} for topic `{p.Topic}`: {p.Title}\n\n"
            + $"The text below was written by {p.AuthorId}, not by the hub; it is a proposal, not a rule.\n\n"
            + "```text\n" + quoted + "\n```\n\n"
            + "Approve or reject it in the memory panel.";
    }

    public static string Imported(string source, string path, int imported, int skipped) =>
        $"{ImportPrefix}{source} ({path}): {imported} proposal(s) added, {skipped} already proposed. Review them in the memory panel.";

    /// <summary>Row 23 (item 4): a rewrite is a whole-file replacement, not an append or a supersede, so
    /// "written to" / "replaced" are both wrong for it; the note names the topic and the entries the
    /// consolidation removed instead.</summary>
    public static string Approved(MemoryProposal p, IReadOnlyList<string>? removedTitles = null)
    {
        var commit = p.CommitHash is null ? " (not committed: git unavailable or failed; see the hub log)." : $" (commit {p.CommitHash}).";
        if (p.Kind == MemoryProposalStore.KindRewrite)
        {
            var removed = removedTitles is { Count: > 0 }
                ? $", removing {string.Join(", ", removedTitles.Select(t => $"'{t}'"))}"
                : "";
            return $"{ProposalPrefix}{p.Id} approved: consolidated memory/{p.WrittenTo}{removed}{commit}";
        }
        return $"{ProposalPrefix}{p.Id} approved: " + (p.Replaces is null ? $"written to memory/{p.WrittenTo}" : $"replaced '{p.Replaces}' in memory/{p.WrittenTo}") + commit;
    }

    public static string Rejected(MemoryProposal p) => $"{ProposalPrefix}{p.Id} rejected.";

    /// <summary>Pass 2 P2-13: a core that is ALREADY over the cap (the L2 defect M10 shipped, or hand-written
    /// prose with no entries) cannot be shrunk by any approval, so the message says which door opens. Row 23
    /// (item 4): a rewrite's refusal names the topic and its real cap instead — "fold it into a topic" makes
    /// no sense for a rewrite, which already targets a whole topic (or the core) by design.</summary>
    public static string Refused(MemoryProposal p, int chars, int current)
    {
        if (p.Kind == MemoryProposalStore.KindRewrite)
        {
            var cap = p.Topic == MemoryStore.CoreTopic ? MemoryStore.CoreChars : MemoryStore.TopicChars;
            var where = p.Topic == MemoryStore.CoreTopic ? "the core" : $"topic '{p.Topic}'";
            return $"{ProposalPrefix}{p.Id} refused: the rewrite of {where} would be {chars} characters, over the {cap} cap. Trim it and propose the rewrite again.";
        }
        return $"{ProposalPrefix}{p.Id} refused: the core would be {chars} characters, over the {MemoryStore.CoreChars} cap. "
            + (current > MemoryStore.CoreChars
                ? $"The core is already {current} characters; edit MEMORY.md by hand before approving anything to it."
                : "Fold it into a topic, or propose it with replaces to update an entry the core already holds.");
    }

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
