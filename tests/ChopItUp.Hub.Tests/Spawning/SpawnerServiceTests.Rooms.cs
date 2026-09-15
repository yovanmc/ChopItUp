using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Spawning;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed partial class SpawnerServiceTests
{
    private const string ClaudeStreamWithTwoCommands = """
        {"type":"system","subtype":"init"}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"echo hello"}}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"git commit -m nope"}}]}}
        {"type":"result","subtype":"success","is_error":false,"result":"done","permission_denials":[{"tool_name":"Bash","tool_use_id":"t2","tool_input":{"command":"git commit -m nope"}}]}
        """;

    private async Task<string> MakeRoom(string id)
    {
        var dir = Path.Combine(_host.RoomsRoot, id);
        Assert.True(await new GitTrail(dir).InitAsync());
        _host.Services.GetRequiredService<MessageStore>().CreateRoom(id, id.ToUpperInvariant(), dir);
        return dir;
    }

    private async Task PostAsOwnerIn(string room, string body)
    {
        var r = await _host.Client.PostAsJsonAsync($"api/rooms/{room}/messages", new { body });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
    }

    private async Task<List<(string Author, string Body)>> MessagesIn(string room)
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync($"api/rooms/{room}/messages?afterId=0&limit=200"));
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("authorId").GetString()!, m.GetProperty("body").GetString()!)).ToList();
    }

    private async Task<(string Author, string Body)> WaitForMessageIn(string room, Func<(string Author, string Body), bool> match)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var hit = (await MessagesIn(room)).FirstOrDefault(match);
            if (hit != default) return hit;
            await Task.Delay(100);
        }
        throw new TimeoutException($"No matching message in '{room}' within {Wait}");
    }

    // Row 35: gitRef reads a branch other than the one checked out at dir - a directory room's own
    // exchange branches (see ExchangeWorktrees.Branch) share dir's repository, so its refs are visible
    // from dir even though the commits on them were made from a linked worktree.
    private static async Task<string> GitLog(string dir, string format, int n = 5, string? gitRef = null)
    {
        var args = new List<string> { "log", $"--format={format}", "-n", n.ToString() };
        if (gitRef is not null) args.Add(gitRef);
        var r = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, args, new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        return r.StandardOutput.Trim();
    }

    [Fact]
    public async Task M9_A6_A8_A9_A10_a_directory_room_spawn_runs_in_the_room_with_settings_in_scratch_and_the_hub_commits_owner_then_agent()
    {
        var dir = await MakeRoom("lab");
        File.WriteAllText(Path.Combine(dir, "notes.md"), "owner wrote this before the spawn\n");
        string? settingsAtLaunch = null;
        _runner.Handler = async (spec, _, _) =>
        {
            var args = spec.Arguments.ToList();
            settingsAtLaunch = File.ReadAllText(args[args.IndexOf("--settings") + 1]);
            File.WriteAllText(Path.Combine(spec.WorkingDirectory, "hello.txt"), "made by the model\n");
            await PostAsIn("sonnet", "lab", "Wrote hello.txt.");
            return FakeProcessRunner.Ok(ClaudeStreamWithTwoCommands);
        };

        await PostAsOwnerIn("lab", "@sonnet create hello.txt");
        var spec = await _runner.NextSpecAsync(Wait);
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;
        var tree = ExchangeWorktrees.PathFor(dir, root);                                      // row 35: works in its own worktree
        Assert.Equal(tree, spec.WorkingDirectory);
        Assert.Contains("dontAsk", spec.Arguments);
        Assert.Equal("stream-json", spec.Arguments[spec.Arguments.ToList().IndexOf("--output-format") + 1]);
        var settingsPath = spec.Arguments[spec.Arguments.ToList().IndexOf("--settings") + 1];
        Assert.StartsWith(Path.Combine(_dir, "spawns"), settingsPath);                         // scratch, not the room
        Assert.StartsWith(Path.Combine(_dir, "spawns"), spec.Arguments[spec.Arguments.ToList().IndexOf("--mcp-config") + 1]);
        Assert.Contains(@"Files: this room's directory is " + tree, spec.StandardInput);
        Assert.Equal(SpawnPrompt.DirectoryRules(tree, dir), spec.Arguments[spec.Arguments.ToList().IndexOf("--append-system-prompt") + 1]);   // the fence in the system channel

        var note = await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith(HubNotes.TrailPrefix));
        Assert.Contains("as sonnet: 1 file(s) changed, 2 shell command(s).", note.Body);
        Assert.Contains("Your edits were committed first as", note.Body);
        Assert.Contains("\"Bash(git commit *)\"", settingsAtLaunch);

        // The owner's pre-spawn edit lands on the room directory itself; the agent's commit lands on
        // the exchange's own branch inside its worktree - row 35's Task 5 (not this row) merges it back.
        var roomLog = (await GitLog(dir, "%an <%ae>|%cn|%s")).Split('\n');
        Assert.Equal(["Owner <owner@chopitup.local>|ChopItUp hub|owner: edits before the next spawn in room lab"], roomLog);
        var branchLog = (await GitLog(dir, "%an <%ae>|%cn|%s", gitRef: ExchangeWorktrees.Branch(root))).Split('\n');
        Assert.Equal(2, branchLog.Length);
        Assert.Equal("Sonnet <sonnet@chopitup.local>|ChopItUp hub|sonnet: turn 1/4 in room lab", branchLog[0]);
        Assert.Equal("Owner <owner@chopitup.local>|ChopItUp hub|owner: edits before the next spawn in room lab", branchLog[1]);
        var body = await GitLog(dir, "%B", 1, ExchangeWorktrees.Branch(root));
        Assert.Contains("Shell commands run (2):\n  1. echo hello\n  2. git commit -m nope [denied]", body.Replace("\r\n", "\n"));
        Assert.True(File.Exists(Path.Combine(tree, "hello.txt")));                            // the worktree survives the spawn
        Assert.True(File.Exists(Path.Combine(dir, "notes.md")));
        Assert.True(File.Exists(Path.Combine(tree, "notes.md")));                             // forked from the room's HEAD, which already has it
        Assert.False(Directory.Exists(Path.GetDirectoryName(settingsPath)!));                 // the scratch folder does not
        Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
        Assert.False(File.Exists(Path.Combine(dir, "mcp.json")));
    }

    [Fact]
    public async Task M9_A10_a_directory_room_runs_one_spawn_at_a_time_while_general_still_runs_them_side_by_side()
    {
        await MakeRoom("lab");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "opus") await release.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"type":"result","result":"done"}""");
        };

        await PostAsOwnerIn("lab", "@opus @gpt-6-astra both of you");
        var first = await _runner.NextSpecAsync(Wait);
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(first));
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromSeconds(1)));                    // the second waits
        release.SetResult();
        var second = await _runner.NextSpecAsync(Wait);
        Assert.Equal("gpt-6-astra", FakeProcessRunner.ParticipantOf(second));
        Assert.Contains("Turn 2 of 4", second.StandardInput);

        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"done"}"""));
        await PostAsOwner("@opus @gpt-6-astra both of you");                                  // general: NULL directory
        var a = await _runner.NextSpecAsync(Wait);
        var b = await _runner.NextSpecAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new HashSet<string> { "opus", "gpt-6-astra" }, new HashSet<string> { FakeProcessRunner.ParticipantOf(a), FakeProcessRunner.ParticipantOf(b) });
    }

    [Fact]
    public async Task R35_a_directory_room_runs_two_exchanges_at_once_and_one_spawn_at_a_time_within_each()
    {
        await MakeRoom("lab");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "opus") await release.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"type":"result","result":"done"}""");
        };

        await PostAsOwnerIn("lab", "@opus @sonnet task A");                                  // one exchange, two participants queued
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromSeconds(1)));                    // sonnet waits for its sibling opus, same exchange

        await PostAsOwnerIn("lab", "@gpt-6-astra task B");                                   // a second, distinct exchange
        Assert.Equal("gpt-6-astra", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));   // runs alongside exchange A
        Assert.Equal(["open", "open"], Spawner.Snapshot("lab").Exchanges!.Select(e => e.Status));

        release.SetResult();
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));   // opus ended: its own sibling may launch now
    }

    [Fact]
    public async Task R35_a_directory_room_spawn_works_in_its_exchange_worktree_and_commits_on_its_branch()
    {
        var dir = await MakeRoom("lab");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet") await release.Task.WaitAsync(ct);
            return FakeProcessRunner.Ok("""{"type":"result","result":"done"}""");
        };

        await PostAsOwnerIn("lab", "@opus @sonnet both of you");                              // one exchange, two participants queued
        var first = await _runner.NextSpecAsync(Wait);
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(first));
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;
        var tree = ExchangeWorktrees.PathFor(dir, root);
        Assert.Equal(tree, first.WorkingDirectory);

        // Opus returned at once, freeing the exchange's own exclusivity for sonnet - held here so the
        // assertions below observe opus's already-landed commit while sonnet is still running.
        var second = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(second));
        Assert.Equal(tree, second.WorkingDirectory);                                          // same exchange, same worktree
        var headAfterLaunch = await new GitTrail(dir).HeadAsync();
        Assert.Equal("Opus", (await GitLog(dir, "%an", 1, ExchangeWorktrees.Branch(root))).Trim());   // opus's own commit already landed on the branch
        Assert.Equal(headAfterLaunch, await new GitTrail(dir).HeadAsync());                   // the room directory's own HEAD never moved

        release.SetResult();
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith(HubNotes.TrailPrefix) && m.Body.Contains("sonnet"));
    }

    [Fact]
    public async Task R35_owner_edits_are_committed_in_the_room_before_the_worktree_branches()
    {
        var dir = await MakeRoom("lab");
        File.WriteAllText(Path.Combine(dir, "owner.txt"), "owner wrote this before the spawn\n");
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"type":"result","result":"done"}"""));

        await PostAsOwnerIn("lab", "@opus go");
        var spec = await _runner.NextSpecAsync(Wait);
        Assert.True(File.Exists(Path.Combine(spec.WorkingDirectory, "owner.txt")));           // forked from the room's HEAD, which already has it committed
        Assert.Equal("owner: edits before the next spawn in room lab", await GitLog(dir, "%s", 1));
    }

    [Fact]
    public async Task R35_a_refused_worktree_starts_no_cli_and_says_why()
    {
        var dir = await MakeRoom("lab");
        File.WriteAllText(ExchangeWorktrees.FolderFor(dir), "not a folder");                  // deterministic refusal

        await PostAsOwnerIn("lab", "@opus go");
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromSeconds(1)));
        var note = await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith("@opus was not started:"));
        Assert.Contains("could not be created", note.Body);
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;
        Assert.False(await new GitTrail(dir).BranchExistsAsync(ExchangeWorktrees.Branch(root)));
    }

    [Fact]
    public async Task M9_A6_a_codex_spawn_in_a_directory_room_gets_the_room_as_its_workspace_and_json_output()
    {
        var dir = await MakeRoom("lab");
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok(""));
        await PostAsOwnerIn("lab", "@gpt-6-astra look around");
        var spec = await _runner.NextSpecAsync(Wait);
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;
        var tree = ExchangeWorktrees.PathFor(dir, root);                                      // row 35: works in its own worktree
        var args = spec.Arguments.ToList();
        Assert.Equal(tree, spec.WorkingDirectory);
        Assert.Equal(tree, args[args.IndexOf("-C") + 1]);
        Assert.Contains("--json", args);
        Assert.Contains("sandbox_workspace_write.network_access=true", args);
        Assert.DoesNotContain("--skip-git-repo-check", args);
        Assert.StartsWith(Path.Combine(_dir, "spawns"), args[args.IndexOf("-o") + 1]);
        var note = await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith(HubNotes.TrailPrefix));
        Assert.Contains("as gpt-6-astra: 0 file(s) changed, 0 shell command(s).", note.Body);   // empty commit, empty log
        // The commit lands on the exchange's own branch, not the room's own log (unmerged until Task 5).
        Assert.Equal("GPT-6 Astra <gpt-6-astra@chopitup.local>", await GitLog(dir, "%an <%ae>", 1, ExchangeWorktrees.Branch(root)));
    }

    [Fact]
    public async Task M9_A9_a_model_that_commits_on_its_own_is_called_out_in_the_note_and_the_hub_still_commits_after_it()
    {
        var dir = await MakeRoom("lab");
        _runner.Handler = async (spec, _, _) =>
        {
            File.WriteAllText(Path.Combine(spec.WorkingDirectory, "rogue.txt"), "x");
            await new GitTrail(spec.WorkingDirectory).CommitAllAsync("rogue", new GitIdentity("Rogue", "rogue@example.test"), allowEmpty: false);
            return FakeProcessRunner.Ok("""{"type":"result","result":"done"}""");
        };
        await PostAsOwnerIn("lab", "@sonnet go");
        var note = await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith(HubNotes.TrailPrefix));
        Assert.Contains("HEAD moved during the spawn: sonnet committed on its own.", note.Body);
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;
        // Both commits land on the exchange's own branch (row 35); the room itself was unborn, so
        // ExchangeWorktrees made one empty start commit for the branch to fork from.
        var log = (await GitLog(dir, "%an|%s", gitRef: ExchangeWorktrees.Branch(root))).Split('\n');
        Assert.Equal(3, log.Length);
        Assert.Equal("Sonnet|sonnet: turn 1/4 in room lab", log[0]);
        Assert.Equal("Rogue|rogue", log[1]);
        Assert.Equal("ChopItUp hub|Room trail start", log[2]);
    }

    [Fact]
    public async Task M9_A6_a_null_directory_room_keeps_the_m5_command_line_byte_for_byte()
    {
        await PostAsOwner("@opus hi");
        var spec = await _runner.NextSpecAsync(Wait);
        Assert.Equal(["-p", "--tools", "", "--strict-mcp-config", "--mcp-config", spec.Arguments[5], "--allowedTools", SpawnCommands.ClaudeToolAllowed,
                      "--no-session-persistence", "--model", "opus", "--output-format", "json", "--disable-slash-commands", "--setting-sources", ""],
            spec.Arguments);
        Assert.StartsWith(Path.Combine(_dir, "spawns"), spec.WorkingDirectory);
        Assert.DoesNotContain("Files: this room's directory", spec.StandardInput);
    }

    private async Task PostAsIn(string participant, string room, string body)
    {
        await using var client = await _host.ClientFor(participant);
        HubTestHost.Json(await client.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = room, ["body"] = body, ["client_key"] = Guid.NewGuid().ToString() }));
    }
}
