using System.Text;
using ChopItUp.Core.Model;
using ChopItUp.Hub.Git;

namespace ChopItUp.Hub.Spawning;

/// <summary>What a room commit says and whom it credits. The author and committer are the
/// repository's own configured identity - git resolves it and the hub never overrides it
/// (<see cref="Git.GitTrail.ConfiguredIdentityAsync"/>) - so the room's log reads like every other
/// commit made there. The participant is named in the subject, and a turn that changed something is
/// credited with its host's <c>Co-authored-by</c> trailer, the convention that folder's AGENTS.md
/// sets for Codex. The subject is one line the trail dialog shows; the body carries the shell
/// log.</summary>
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

    public const string WebHeader = "Web requests";

    /// <summary>The agent commit's message. <paramref name="web"/> adds a block after the shell log only
    /// when it holds a call, so a spawn that made none reads exactly as before. Each list names its first
    /// <see cref="SpawnOutput.MaxCommands"/> entries and counts the rest in its header.</summary>
    public static string AgentMessage(Participant agent, string roomId, int turn, int budget, IReadOnlyList<ShellCommand> commands, bool headMoved, IReadOnlyList<WebRequest>? web = null)
    {
        var sb = new StringBuilder(AgentSubject(agent, roomId, turn, budget)).Append("\n\n");
        if (commands.Count == 0) sb.Append(ShellHeader).Append(": none.\n");
        else
        {
            sb.Append(ShellHeader).Append(Count(commands.Count)).Append(":\n");
            for (int i = 0; i < Math.Min(commands.Count, SpawnOutput.MaxCommands); i++)
                sb.Append("  ").Append(i + 1).Append(". ").Append(commands[i].Command).Append(commands[i].Denied ? " [denied]" : "").Append('\n');
        }
        if (web is { Count: > 0 })
        {
            sb.Append('\n').Append(WebHeader).Append(Count(web.Count)).Append(":\n");
            for (int i = 0; i < Math.Min(web.Count, SpawnOutput.MaxCommands); i++)
                sb.Append("  ").Append(i + 1).Append(". ").Append(web[i].Tool).Append(": ").Append(web[i].Target).Append(web[i].Denied ? " [denied]" : "").Append('\n');
        }
        if (headMoved) sb.Append("\nHEAD moved during this spawn: the model committed on its own; its commit(s) sit below this one in the log.\n");
        return sb.ToString();
    }

    private static string Count(int total) => total > SpawnOutput.MaxCommands
        ? $" ({SpawnOutput.MaxCommands} of {total}, {total - SpawnOutput.MaxCommands} not listed)"
        : $" ({total})";
}
