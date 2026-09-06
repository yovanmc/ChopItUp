using System.Text;
using ChopItUp.Core.Model;
using ChopItUp.Hub.Git;

namespace ChopItUp.Hub.Spawning;

/// <summary>Who a room commit is by and what it says (M9 decisions 6, 7). The author is the participant
/// row — display name and <c>&lt;id&gt;@chopitup.local</c> — so `git log` in the room reads as the chat
/// does; the committer is always the hub (<see cref="GitTrail.Hub"/>). The subject is one line the
/// trail dialog shows; the body carries the shell log.</summary>
public static class RoomCommits
{
    public const string Domain = "chopitup.local";
    public const string ShellHeader = "Shell commands run";

    public static GitIdentity IdentityOf(Participant p) => new(p.DisplayName, p.Id + "@" + Domain);

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
