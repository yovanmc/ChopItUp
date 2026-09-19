using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed class RoomCommitsTests
{
    private static readonly ChopItUp.Core.Model.Participant Sonnet = ChopDb.SeedRoster.Single(p => p.Id == "sonnet");
    private static readonly ChopItUp.Core.Model.Participant Owner = ChopDb.SeedRoster.Single(p => p.Id == "owner");

    [Fact]
    public void Row46_A3_the_co_author_trailer_follows_the_host_and_names_nobody_for_an_unknown_one()
    {
        Assert.Equal("Co-authored-by: Codex <noreply@openai.com>", RoomCommits.CoAuthorTrailer("codex"));
        Assert.Equal("Co-authored-by: Claude <noreply@anthropic.com>", RoomCommits.CoAuthorTrailer("claude"));
        Assert.Null(RoomCommits.CoAuthorTrailer("hub"));
        Assert.Null(RoomCommits.CoAuthorTrailer(""));
    }

    [Fact]
    public void M9_A9_the_agent_message_lists_commands_numbered_marks_denials_and_says_none_when_empty()
    {
        Assert.Equal("sonnet: turn 2/4 in room lab\n\nShell commands run: none.\n", RoomCommits.AgentMessage(Sonnet, "lab", 2, 4, [], false));
        var m = RoomCommits.AgentMessage(Sonnet, "lab", 1, 4, [new("echo hi", false), new("git commit -m x", true)], true);
        Assert.StartsWith("sonnet: turn 1/4 in room lab\n\nShell commands run (2):\n  1. echo hi\n  2. git commit -m x [denied]\n", m);
        Assert.Contains("HEAD moved during this spawn", m);
        Assert.Equal("owner: edits before the next spawn in room lab\n", RoomCommits.OwnerMessage(Owner, "lab"));
    }

    [Fact]
    public void M9_A9_the_trail_note_reports_the_commit_the_owner_precommit_and_failures()
    {
        var agent = new CommitOutcome("abc1234", true, 2, null);
        Assert.Equal("Committed abc1234 for sonnet: 2 file(s) changed, 3 shell command(s).", HubNotes.Trail("sonnet", null, agent, 3, false));
        Assert.Equal("Committed abc1234 for sonnet: 2 file(s) changed, 3 shell command(s). Your edits were committed first as 9999999.",
            HubNotes.Trail("sonnet", new CommitOutcome("9999999", true, 1, null), agent, 3, false));
        Assert.Equal("Committed abc1234 for sonnet: 0 file(s) changed, 0 shell command(s). HEAD moved during the spawn: sonnet committed on its own.",
            HubNotes.Trail("sonnet", new CommitOutcome("9999999", false, 0, null), agent with { FilesChanged = 0 }, 0, true));
        Assert.Equal("Not committed for sonnet: git commit exited 128: boom.", HubNotes.Trail("sonnet", null, new CommitOutcome(null, false, 0, "git commit exited 128: boom"), 0, false));
    }
}
