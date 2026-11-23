using System.Text.RegularExpressions;
using ChopItUp.Core.Memory;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Memory;

/// <summary>The memory notes, posted as the <c>hub</c> row through the same store + signal path every
/// message takes (so browsers and waiting hosts see them). A proposal's note is the durable,
/// host-visible record that something awaits the hub owner. The texts are code (tests match on them),
/// not templates.</summary>
public static class HubNotes
{
    public const string ProposalPrefix = "Memory proposal #";
    public const string ImportPrefix = "Memory import from ";
    public const string TrailPrefix = "Committed ";
    public const string TrailFailedPrefix = "Not committed for ";
    public const long LargeFileBytes = 5L * 1024 * 1024;

    public static Message Post(MessageStore store, MessageSignal signal, string roomId, string text)
    {
        var message = store.Post(roomId, ChopDb.HubParticipantId, text);
        signal.Publish(roomId, message);   // the spawner ignores system authors; browsers refresh the panel on it
        return message;
    }

    /// <summary>The body is model-written and every future spawn reads this note under the hub's
    /// authorship, which the rules tell models to trust, so the note names the proposer as the author
    /// of what follows and fences it. A fence inside the body is broken up so it cannot close ours; a
    /// memory-fence-shaped line is broken the same way so a proposal note can never carry one into a
    /// later spawn's memory injection.</summary>
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

    /// <summary>A rewrite is a whole-file replacement, not an append or a supersede, so "written to" /
    /// "replaced" are both wrong for it; the note names the topic and the entries the
    /// consolidation removed instead.</summary>
    public static string Approved(MemoryProposal p, IReadOnlyList<string>? removedTitles = null)
    {
        var commit = p.CommitHash is null ? " (not committed: git unavailable or failed; see the hub log)." : $" (commit {p.CommitHash}).";
        if (p.Kind == MemoryProposalStore.KindRewrite)
        {
            var removed = removedTitles is { Count: > 0 }
                ? $", removing {string.Join(", ", removedTitles.Select(t => $"'{t}'"))}"
                : "";
            var verb = p.Source == MemoryProposalStore.SourceEditor ? "edited" : "consolidated";
            return $"{ProposalPrefix}{p.Id} approved: {verb} memory/{p.WrittenTo}{removed}{commit}";
        }
        return $"{ProposalPrefix}{p.Id} approved: " + (p.Replaces is null ? $"written to memory/{p.WrittenTo}" : $"replaced '{p.Replaces}' in memory/{p.WrittenTo}") + commit;
    }

    public static string Rejected(MemoryProposal p) => $"{ProposalPrefix}{p.Id} rejected.";

    /// <summary>A core that is ALREADY over the cap (legacy content, or hand-written prose with no
    /// entries) cannot be shrunk by any approval, so the message says which door opens. A rewrite's
    /// refusal names the topic and its real cap instead: "fold it into a topic" makes no sense for a
    /// rewrite, which already targets a whole topic (or the core) by design.</summary>
    public static string Refused(MemoryProposal p, int chars, int current)
    {
        if (p.Kind == MemoryProposalStore.KindRewrite)
        {
            var cap = p.Topic == MemoryStore.CoreTopic ? MemoryStore.CoreChars : MemoryStore.TopicChars;
            var where = p.Topic == MemoryStore.CoreTopic ? "the core" : $"topic '{p.Topic}'";
            // An editor row is never pending for anyone, so the Retry path's fix-and-try-again
            // sentence must say "save", not "propose" (the editor has no separate propose step).
            var tryAgain = p.Source == MemoryProposalStore.SourceEditor ? "Trim it and save again." : "Trim it and propose the rewrite again.";
            return $"{ProposalPrefix}{p.Id} refused: the rewrite of {where} would be {chars} characters, over the {cap} cap. {tryAgain}";
        }
        return $"{ProposalPrefix}{p.Id} refused: the core would be {chars} characters, over the {MemoryStore.CoreChars} cap. "
            + (current > MemoryStore.CoreChars
                ? $"The core is already {current} characters; edit MEMORY.md by hand before approving anything to it."
                : "Fold it into a topic, or propose it with replaces to update an entry the core already holds.");
    }

    /// <summary>Git keeps every version of a file for good, so a render that lands in a commit costs
    /// the room's repository its size permanently.</summary>
    public static string LargeFiles(string participantId, IReadOnlyList<AddedFile> files, string? artifactsFolder)
    {
        var names = string.Join(", ", files.Select(f => $"{f.Path} ({f.Size / 1024d / 1024d:0.0} MB)"));
        return $"@{participantId}'s commit added {files.Count} file(s) over 5 MB to git history: {names}. Git keeps them for good."
            + (artifactsFolder is null ? "" : $" Write large generated files to {artifactsFolder} instead.");
    }

    public const int CallsNoteLines = 10;
    public const int CallsNoteChars = 160;

    /// <summary>A plain-room spawn's web and shell calls, its only record (no commit trail there). The
    /// targets are model-chosen text that every later spawn reads under the hub's authorship, so, as in
    /// <see cref="Proposed"/>, the note names the participant as their author and fences them; each
    /// target is JSON-encoded (quotes and control characters escaped) and every backtick run long
    /// enough to fence is broken up. A tool name that is not a plain word is encoded with its target
    /// under the label "Call". At most <see cref="CallsNoteLines"/> lines, each target cut at
    /// <see cref="CallsNoteChars"/> characters before encoding, then a count of the rest; the header gives the total, "at least" when a parser hit its cap. Null when
    /// there is no call.</summary>
    public static string? Calls(string participantId, IReadOnlyList<WebRequest> calls, bool atLeast)
    {
        if (calls.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        sb.Append("Web and shell calls by ").Append(participantId).Append(" (").Append(atLeast ? "at least " : "").Append(calls.Count)
          .Append("). The text below was written by ").Append(participantId).Append("'s calls, not by the hub.\n\n```text\n");
        for (var i = 0; i < Math.Min(calls.Count, CallsNoteLines); i++)
        {
            // A tool name comes off the stream too: one that is not a plain word moves into the
            // encoded text under a fixed label.
            var plainTool = PlainToolName.IsMatch(calls[i].Tool);
            var target = plainTool ? calls[i].Target : calls[i].Tool + " " + calls[i].Target;
            if (target.Length > CallsNoteChars)
                target = target[..(char.IsHighSurrogate(target[CallsNoteChars - 1]) ? CallsNoteChars - 1 : CallsNoteChars)] + "…";
            var encoded = System.Text.Json.JsonSerializer.Serialize(target, CallsJson);
            while (encoded.Contains("```", StringComparison.Ordinal)) encoded = encoded.Replace("```", "` ` `", StringComparison.Ordinal);
            sb.Append(i + 1).Append(". ").Append(plainTool ? calls[i].Tool : "Call").Append(' ').Append(encoded).Append(calls[i].Denied ? " [denied]" : "").Append('\n');
        }
        if (calls.Count > CallsNoteLines) sb.Append("and ").Append(calls.Count - CallsNoteLines).Append(" more\n");
        return sb.Append("```").ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex PlainToolName = new("^[A-Za-z_]{1,32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.Json.JsonSerializerOptions CallsJson =new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The room's record of what the trail did around one spawn. One line; the commit itself
    /// is the detail.</summary>
    public static string Trail(string participantId, CommitOutcome? owner, CommitOutcome agent, int commands, bool headMoved, int webRequests = 0)
    {
        if (agent.Hash is null || !agent.Created)
            return $"{TrailFailedPrefix}{participantId}: {agent.Reason ?? "git made no commit"}.";
        var web = webRequests > 0 ? $", {webRequests} web request(s)" : "";
        var text = $"{TrailPrefix}{agent.Hash} for {participantId}: {agent.FilesChanged} file(s) changed, {commands} shell command(s){web}.";
        if (owner is { Created: true }) text += $" Your edits were committed first as {owner.Hash}.";
        if (headMoved) text += $" HEAD moved during the spawn: {participantId} committed on its own.";
        return text;
    }
}
