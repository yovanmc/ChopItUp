using System.Text;
using ChopItUp.Core.Model;
using ChopItUp.Hub.Git;

namespace ChopItUp.Hub.Spawning;

/// <summary>What a room commit says and whom it credits (M9 decisions 6, 7; identity rule changed by
/// row 46). The author and committer are the repository's own configured identity - git resolves it
/// and the hub never overrides it (<see cref="Git.GitTrail.ConfiguredIdentityAsync"/>) - so the room's
/// log reads like every other commit made there. The participant is named in the subject, and a turn
/// that changed something is credited with its host's <c>Co-authored-by</c> trailer, the convention
/// that folder's AGENTS.md sets for Codex. The subject is one line the trail dialog shows; the body
/// carries the shell log.</summary>
public static class RoomCommits
{
    public const string ShellHeader = "Shell commands run";
    public const string CodexTrailer = GitTrail.CoAuthorKey + ": " + GitTrail.CodexCoAuthor;
    public const string ClaudeTrailer = GitTrail.CoAuthorKey + ": " + GitTrail.ClaudeCoAuthor;

    /// <summary>The trailer that credits a spawn's host, or null for a host the spawner cannot start:
    /// nothing is credited rather than something invented.</summary>
    public static string? CoAuthorTrailer(string host) => host switch
    {
        "codex" => CodexTrailer,
        "claude" => ClaudeTrailer,
        _ => null,
    };

    public static string OwnerMessage(Participant owner, string roomId) =>
        $"{owner.Id}: edits before the next spawn in room {roomId}\n";

    public static string AgentSubject(Participant agent, string roomId, int turn, int budget) =>
        $"{agent.Id}: turn {turn}/{budget} in room {roomId}";

    public static string AgentMessage(Participant agent, string roomId, int turn, int budget, IReadOnlyList<ShellCommand> commands, bool headMoved)
    {
        var sb = new StringBuilder(AgentSubject(agent, roomId, turn, budget)).Append("\n\n");
        if (commands.Count == 0) sb.Append(ShellHeader).Append(": none.\n");
        else
        {
            sb.Append(ShellHeader).Append(" (").Append(commands.Count).Append("):\n");
            for (int i = 0; i < commands.Count; i++)
                sb.Append("  ").Append(i + 1).Append(". ").Append(commands[i].Command).Append(commands[i].Denied ? " [denied]" : "").Append('\n');
        }
        if (headMoved) sb.Append("\nHEAD moved during this spawn: the model committed on its own; its commit(s) sit below this one in the log.\n");
        return sb.ToString();
    }
}
