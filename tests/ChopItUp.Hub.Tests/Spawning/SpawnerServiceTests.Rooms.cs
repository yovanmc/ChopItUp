using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Skills;
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
        await GitConfig(dir, "user.name", "Room Owner");
        await GitConfig(dir, "user.email", "room-owner@example.test");
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

    private async Task<ExchangeView> WaitForExchangeStatusIn(string room, long root, string status)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var exchange = Spawner.Snapshot(room).Exchanges?.SingleOrDefault(e => e.RootMessageId == root);
            if (exchange?.Status == status) return exchange;
            await Task.Delay(50);
        }
        var last = Spawner.Snapshot(room).Exchanges?.SingleOrDefault(e => e.RootMessageId == root)?.Status ?? "missing";
        throw new TimeoutException($"Exchange #{root} in '{room}' never reached '{status}'; last was '{last}'.");
    }

    /// <summary>A room's git trail whose `git merge` call (and only that call - never `merge-base`,
    /// `worktree add`, a commit, and so on) blocks until <see cref="Hold"/> is released, so a test can
    /// deterministically catch a worktree close mid-merge. <see cref="MergeAttempted"/> completes the
    /// instant the merge call actually starts, so a test can wait for the close to have genuinely
    /// reached it before acting, rather than racing it.</summary>
    private sealed class DelayingMergeRunner : IProcessRunner
    {
        private readonly IProcessRunner _inner = new ProcessRunner();
        public readonly TaskCompletionSource Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource MergeAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation)
        {
            if (spec.Arguments.Contains("merge"))
            {
                MergeAttempted.TrySetResult();
                await Hold.Task;
            }
            return await _inner.RunAsync(spec, timeout, cancellation);
        }
    }

    // Row 35: gitRef reads a branch other than the one checked out at dir - a directory room's own
    // exchange branches (see ExchangeWorktrees.Branch) share dir's repository, so its refs are visible
    // from dir even though the commits on them were made from a linked worktree. skip is row 35's own
    // addition: once a close merges an exchange's branch into dir, its commits are still reachable from
    // dir's own HEAD, just not at the top (the merge commit is) - skip walks past the newer entries
    // without needing the branch name (deleted by then) or the merge commit's own hash.
    private static async Task<string> GitLog(string dir, string format, int n = 5, string? gitRef = null, int skip = 0)
    {
        // --date-order: without it, git's default log order only sorts by commit timestamp, which is
        // only per-second - two commits made in the same test, a second apart in wall clock but the
        // same git second, can otherwise print a merge's own ancestor (e.g. "Room trail start") ahead
        // of its descendants. --date-order still sorts by date but never a parent before its child.
        var args = new List<string> { "log", "--date-order", $"--format={format}", "-n", n.ToString() };
        if (skip > 0) args.Add($"--skip={skip}");
        if (gitRef is not null) args.Add(gitRef);
        var r = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, args, new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        return r.StandardOutput.Trim();
    }

    private static async Task GitConfig(string dir, string key, string value)
    {
        var r = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, ["config", key, value], new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
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
        Assert.Contains("for sonnet: 1 file(s) changed, 2 shell command(s).", note.Body);
        Assert.Contains("Your edits were committed first as", note.Body);
        Assert.Contains("\"Bash(git commit *)\"", settingsAtLaunch);

        // Row 35: the exchange concludes and its worktree is merged into the room directory -
        // wait for that note, then read only dir (the worktree is gone once the close finishes).
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} merged into"));
        var roomLog = (await GitLog(dir, "%an <%ae>|%cn|%s", 3)).Split('\n');
        Assert.Equal([
            $"Room Owner <room-owner@example.test>|Room Owner|Merge exchange #{root} (lab)",
            "Room Owner <room-owner@example.test>|Room Owner|sonnet: turn 1/4 in room lab",
            "Room Owner <room-owner@example.test>|Room Owner|owner: edits before the next spawn in room lab",
        ], roomLog);
        var body = await GitLog(dir, "%B", 1, skip: 1);                                       // the agent's own commit, one below the merge
        Assert.Contains("Shell commands run (2):\n  1. echo hello\n  2. git commit -m nope [denied]", body.Replace("\r\n", "\n"));
        Assert.EndsWith("\n\nCo-authored-by: Claude <noreply@anthropic.com>", body.Replace("\r\n", "\n").TrimEnd('\n'));   // sonnet is a claude host and changed a file
        Assert.Equal("Claude <noreply@anthropic.com>", (await GitLog(dir, "%(trailers:key=Co-authored-by,valueonly)", 1)).Trim());   // the merge carries it
        Assert.DoesNotContain("Co-authored-by", await GitLog(dir, "%B", 1, skip: 2));                             // the owner's own commit credits no model
        Assert.True(File.Exists(Path.Combine(dir, "hello.txt")));                             // merged into the room directory
        Assert.True(File.Exists(Path.Combine(dir, "notes.md")));
        Assert.False(Directory.Exists(tree));                                                 // the worktree is gone after the merge
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
        // A real HEAD to compare against, captured before anything else can move it (not the unborn
        // one MakeRoom leaves behind, which EnsureAsync would otherwise advance with its own "Room
        // trail start" commit the moment the exchange below launches).
        Assert.True((await new GitTrail(dir).CommitAllAsync("seed", GitTrail.Hub, allowEmpty: true)).Created);
        var headBefore = await new GitTrail(dir).HeadAsync();
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
        Assert.Equal("Room Owner", (await GitLog(dir, "%an", 1, ExchangeWorktrees.Branch(root))).Trim());   // opus's own commit already landed on the branch
        Assert.Equal(headBefore, await new GitTrail(dir).HeadAsync());                        // the room directory's own HEAD never moved

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
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;

        // Reading dir's log right after the spec races the close's own merge commit landing there too;
        // wait for the close note, then read the merged history instead: the merge commit, the agent's
        // own commit on the branch, then the owner commit it forked from.
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} merged into"));
        Assert.Equal("owner: edits before the next spawn in room lab", await GitLog(dir, "%s", 1, skip: 2));
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

    // --- A closed exchange's worktree is merged or kept, and the note is posted -------------

    [Fact]
    public async Task R35_two_exchanges_edit_at_once_and_both_merge_into_the_room_on_conclusion()
    {
        var dir = await MakeRoom("lab");
        _runner.Handler = (spec, _, _) =>
        {
            File.WriteAllText(Path.Combine(spec.WorkingDirectory, FakeProcessRunner.ParticipantOf(spec) == "opus" ? "a.txt" : "b.txt"), "x\n");
            return Task.FromResult(FakeProcessRunner.Ok("""{"type":"result","result":"done"}"""));
        };

        await PostAsOwnerIn("lab", "@opus task A");
        await _runner.NextSpecAsync(Wait);
        await PostAsOwnerIn("lab", "@sonnet task B");
        await _runner.NextSpecAsync(Wait);
        var roots = Spawner.Snapshot("lab").Exchanges!.Select(e => e.RootMessageId).ToList();
        Assert.Equal(2, roots.Count);

        foreach (var root in roots)
            await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} merged into"));

        Assert.True(File.Exists(Path.Combine(dir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(dir, "b.txt")));
        foreach (var root in roots)
            Assert.False(await new GitTrail(dir).BranchExistsAsync(ExchangeWorktrees.Branch(root)));
        var worktrees = ExchangeWorktrees.FolderFor(dir);
        Assert.True(!Directory.Exists(worktrees) || Directory.GetDirectories(worktrees).Length == 0);
    }

    [Fact]
    public async Task R35_a_conflict_keeps_the_branch_and_names_it()
    {
        var dir = await MakeRoom("lab");
        var releaseSonnet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            var who = FakeProcessRunner.ParticipantOf(spec);
            if (who == "sonnet") await releaseSonnet.Task.WaitAsync(ct);
            File.WriteAllText(Path.Combine(spec.WorkingDirectory, "c.txt"), who + "\n");
            return FakeProcessRunner.Ok("""{"type":"result","result":"done"}""");
        };

        await PostAsOwnerIn("lab", "@opus task A");
        await _runner.NextSpecAsync(Wait);
        await PostAsOwnerIn("lab", "@sonnet task B");
        await _runner.NextSpecAsync(Wait);

        var merged = await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.Contains(" merged into "));   // opus's exchange lands cleanly first
        releaseSonnet.SetResult();
        var kept = await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.Contains("was not merged") && m.Body.Contains("conflicts in c.txt"));

        Assert.Contains("chopitup/x", kept.Body);
        Assert.False(await new GitTrail(dir).IsDirtyAsync());
        Assert.Equal("opus", File.ReadAllText(Path.Combine(dir, "c.txt")).Trim());
        Assert.Single(await MessagesIn("lab"), m => m.Body.Contains(" merged into "));
        Assert.Single(await MessagesIn("lab"), m => m.Body.Contains("was not merged"));
    }

    [Fact]
    public async Task R35_a_stopped_exchange_keeps_its_branch()
    {
        var dir = await MakeRoom("lab");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            try { await hold.Task.WaitAsync(ct); } catch (OperationCanceledException) { }
            return new ProcessResult(null, false, true, "", "", TimeSpan.Zero);
        };

        await PostAsOwnerIn("lab", "@opus task A");
        await _runner.NextSpecAsync(Wait);
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;

        var r = await _host.Client.PostAsync($"api/rooms/lab/exchanges/{root}/stop", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);

        var note = await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} was not merged: it was stopped."));
        Assert.Contains($"stay on branch {ExchangeWorktrees.Branch(root)}", note.Body);
        Assert.True(await new GitTrail(dir).BranchExistsAsync(ExchangeWorktrees.Branch(root)));
    }

    [Fact]
    public async Task R35_a_superseded_exchange_merges_when_its_last_spawn_ends()
    {
        var dir = await MakeRoom("lab");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            await hold.Task.WaitAsync(ct);
            File.WriteAllText(Path.Combine(spec.WorkingDirectory, "a.txt"), "x\n");
            return FakeProcessRunner.Ok("""{"type":"result","result":"done"}""");
        };

        await PostAsOwnerIn("lab", "@opus task A");
        await _runner.NextSpecAsync(Wait);
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;

        await PostAsOwnerIn("lab", "/nope @opus");                                            // supersedes; opus is still running
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.Contains("No skill named"));
        // The note is persisted before the spawner publishes its refreshed snapshot.
        Assert.Equal("superseded", (await WaitForExchangeStatusIn("lab", root, "superseded")).Status);

        hold.SetResult();
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} merged into"));
        Assert.True(File.Exists(Path.Combine(dir, "a.txt")));
        Assert.False(await new GitTrail(dir).BranchExistsAsync(ExchangeWorktrees.Branch(root)));
    }

    [Fact]
    public async Task R35_a_stopped_or_timed_out_spawn_keeps_the_branch_of_a_concluded_or_superseded_exchange()
    {
        var dir = await MakeRoom("lab");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, ct) =>
        {
            try { await hold.Task.WaitAsync(ct); } catch (OperationCanceledException) { }
            return new ProcessResult(null, false, true, "", "", TimeSpan.Zero);
        };

        await PostAsOwnerIn("lab", "@opus task A");
        await _runner.NextSpecAsync(Wait);
        var rootA = Spawner.Snapshot("lab").RootMessageId!.Value;
        await PostAsOwnerIn("lab", "/nope @opus");                                            // supersedes; opus is still running
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.Contains("No skill named"));

        var (outcome, _) = await Spawner.StopExchangeAsync("lab", rootA);
        Assert.Equal(ExchangeStopOutcome.Stopped, outcome);
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{rootA} was not merged: a spawn of it was stopped or timed out"));
        Assert.True(await new GitTrail(dir).BranchExistsAsync(ExchangeWorktrees.Branch(rootA)));

        _runner.Handler = (_, timeout, ct) => FakeProcessRunner.HangUntilKilled(timeout, ct);
        await PostAsOwnerIn("lab", "@sonnet take forever");
        await _runner.NextSpecAsync(Wait);
        var rootB = Spawner.Snapshot("lab").Exchanges!.Single().RootMessageId;
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{rootB} was not merged: a spawn of it was stopped or timed out"));
        Assert.True(await new GitTrail(dir).BranchExistsAsync(ExchangeWorktrees.Branch(rootB)));
    }

    [Fact]
    public async Task R35_a_worktree_launch_and_a_close_wait_for_a_dying_room_directory_spawn()
    {
        WriteSkill("build-thing", ChopItUp.Hub.Tests.RunHostFixture.RunSkillMd);
        var dir = await MakeRoom("lab-run-dying");
        var holdConductor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, _) =>
        {
            // Ignores the cancellation token on purpose (AC9): a run's cancelled conductor
            // is not really gone until its own process ends, and this reproduces exactly that window.
            if (FakeProcessRunner.ParticipantOf(spec) == "sonnet") await holdConductor.Task;
            return FakeProcessRunner.Ok("""{"result":"working"}""");
        };

        await PostAsOwnerIn("lab-run-dying", "/build-thing @sonnet begin");
        await _runner.NextSpecAsync(Wait);
        Assert.NotNull(Runs.Active("lab-run-dying"));

        Assert.NotNull(await Spawner.StopAsync("lab-run-dying"));
        await WaitForMessageIn("lab-run-dying", m => m.Author == "hub" && m.Body.StartsWith("Run #") && m.Body.Contains("ended"));

        await PostAsOwnerIn("lab-run-dying", "@opus a worker, outside the run now");
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromSeconds(1)));                     // waits for the dying conductor's own spawn

        holdConductor.SetResult();
        var worker = await _runner.NextSpecAsync(Wait);
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(worker));
        var root = Spawner.Snapshot("lab-run-dying").RootMessageId!.Value;
        Assert.Equal(ExchangeWorktrees.PathFor(dir, root), worker.WorkingDirectory);
    }

    [Fact]
    public async Task R35_AC9_a_room_directory_launch_waits_for_a_running_close()
    {
        // The direction AC9 had no test for at all: a worktree close's merge is genuinely running (the
        // repository's write gate is held) when a run's conductor - a room-directory launch - would
        // otherwise be due; it must get no spec until the close finishes.
        var delayingRunner = new DelayingMergeRunner();
        var localDir = _dir + "_close_wait";
        await using var host = await HubTestHost.StartAsync(localDir, processRunner: _runner, limits: Fast,
            roomGit: dir => new GitTrail(dir, runner: delayingRunner));
        host.AuthorizeAs(ChopDb.OwnerParticipantId);
        var runs = host.Services.GetRequiredService<RunStore>();

        var skillDir = Path.Combine(localDir, "skills", "build-thing");
        Directory.CreateDirectory(skillDir);
        var bytes = new UTF8Encoding(false).GetBytes(RunHostFixture.RunSkillMd);
        File.WriteAllBytes(Path.Combine(skillDir, "SKILL.md"), bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        new SkillHashes(host.Services.GetRequiredService<ChopDb>()).Record("build-thing", hash, "test-fixture");

        var dir = Path.Combine(host.RoomsRoot, "lab-close-wait");
        Assert.True(await new GitTrail(dir).InitAsync());
        host.Services.GetRequiredService<MessageStore>().CreateRoom("lab-close-wait", "Lab", dir);

        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"type":"result","result":"done"}"""));
        var opusPost = await host.Client.PostAsJsonAsync("api/rooms/lab-close-wait/messages", new { body = "@opus task A" });
        Assert.Equal(System.Net.HttpStatusCode.Created, opusPost.StatusCode);
        await _runner.NextSpecAsync(Wait);   // opus's worktree spawn; concludes right after, its close now blocks on the delayed merge
        await delayingRunner.MergeAttempted.Task.WaitAsync(Wait);

        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"result":"working"}"""));
        var runPost = await host.Client.PostAsJsonAsync("api/rooms/lab-close-wait/messages", new { body = "/build-thing @sonnet begin" });
        Assert.Equal(System.Net.HttpStatusCode.Created, runPost.StatusCode);

        var deadline = DateTime.UtcNow + Wait;
        Run? run = null;
        while (DateTime.UtcNow < deadline && (run = runs.Active("lab-close-wait")) is null) await Task.Delay(50);
        Assert.NotNull(run);                                                        // the conductor exchange opened
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromSeconds(1)));           // its spawn waits for the close to finish

        delayingRunner.Hold.SetResult();
        var conductorSpec = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(conductorSpec));
        Assert.Equal(dir, conductorSpec.WorkingDirectory);
    }

    [Fact]
    public async Task R35_a_worktree_close_that_outlives_shutdown_logs_its_note_to_stderr()
    {
        // A close still running when the hub shuts down finds its event channel already completed: the
        // note it carries must be logged, not silently dropped.
        var delayingRunner = new DelayingMergeRunner();
        var localDir = _dir + "_close_shutdown";
        await using var host = await HubTestHost.StartAsync(localDir, processRunner: _runner, limits: Fast,
            roomGit: dir => new GitTrail(dir, runner: delayingRunner));
        host.AuthorizeAs(ChopDb.OwnerParticipantId);
        var spawner = host.Services.GetRequiredService<SpawnerService>();

        var dir = Path.Combine(host.RoomsRoot, "lab-shutdown-close");
        Assert.True(await new GitTrail(dir).InitAsync());
        host.Services.GetRequiredService<MessageStore>().CreateRoom("lab-shutdown-close", "Lab", dir);

        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"type":"result","result":"done"}"""));
        var post = await host.Client.PostAsJsonAsync("api/rooms/lab-shutdown-close/messages", new { body = "@opus task A" });
        Assert.Equal(System.Net.HttpStatusCode.Created, post.StatusCode);
        await _runner.NextSpecAsync(Wait);   // opus's worktree spawn; its close now blocks on the delayed merge
        await delayingRunner.MergeAttempted.Task.WaitAsync(Wait);

        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        try
        {
            // StopAsync completes the events channel on its very first line, synchronously, before its
            // first await - so by the time this call even returns a Task, that has already happened;
            // releasing the merge only now guarantees the close's own write lands after the channel closes.
            var stopTask = spawner.StopAsync(CancellationToken.None);
            delayingRunner.Hold.SetResult();
            await stopTask.WaitAsync(Wait);
        }
        finally { Console.SetError(original); }

        Assert.Contains("lab-shutdown-close", error.ToString());
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
        Assert.Contains("for gpt-6-astra: 0 file(s) changed, 0 shell command(s).", note.Body);   // empty commit, empty log
        // Row 35: wait for the close's merge note, then read the merged history in dir itself -
        // the branch is deleted once the merge lands.
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} merged into"));
        var log = (await GitLog(dir, "%an <%ae>", 3)).Split('\n');
        Assert.Equal([
            "Room Owner <room-owner@example.test>",
            "Room Owner <room-owner@example.test>",
            "Room Owner <room-owner@example.test>",
        ], log);
        Assert.DoesNotContain("Co-authored-by", await GitLog(dir, "%B", 1, skip: 1));   // an empty turn credits nobody
        Assert.DoesNotContain("Co-authored-by", await GitLog(dir, "%B", 1));            // so the merge carries nothing either
        Assert.False(await new GitTrail(dir).BranchExistsAsync(ExchangeWorktrees.Branch(root)));
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
        // Row 35: wait for the close's merge note, then read the merged history in dir - the
        // room itself was unborn, so ExchangeWorktrees made one empty start commit for the branch to
        // fork from, and that start commit is what the merge commit's other parent already is.
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} merged into"));
        var log = (await GitLog(dir, "%an|%s", 4)).Split('\n');
        Assert.Equal(4, log.Length);
        Assert.Equal($"Room Owner|Merge exchange #{root} (lab)", log[0]);
        Assert.Equal("Room Owner|sonnet: turn 1/4 in room lab", log[1]);
        Assert.Equal("Rogue|rogue", log[2]);
        Assert.Equal("Room Owner|Room trail start", log[3]);
    }

    [Fact]
    public async Task Row46_R11_a_spawn_that_never_launched_credits_no_host_even_when_the_tree_changed()
    {
        var dir = await MakeRoom("lab");
        _runner.Handler = (spec, _, _) =>
        {
            File.WriteAllText(Path.Combine(spec.WorkingDirectory, "during.txt"), "owner edit while nothing ran");
            throw new InvalidOperationException("launch failed on purpose");
        };
        await PostAsOwnerIn("lab", "@sonnet hello");
        await _runner.NextSpecAsync(Wait);
        var note = await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith(HubNotes.TrailPrefix));
        Assert.Contains("for sonnet: 1 file(s) changed", note.Body);
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} merged into"));
        Assert.DoesNotContain("Co-authored-by", await GitLog(dir, "%B", 1, skip: 1));   // the turn commit
        Assert.DoesNotContain("Co-authored-by", await GitLog(dir, "%B", 1));            // the merge
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

    [Fact]
    public async Task R36_a_reopened_exchange_in_a_directory_room_leases_its_worktree_again_after_the_merge()
    {
        var dir = await MakeRoom("lab");
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"type":"result","result":"done"}"""));

        await PostAsOwnerIn("lab", "@opus go");
        var first = await _runner.NextSpecAsync(Wait);
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;
        Assert.Equal(ExchangeWorktrees.PathFor(dir, root), first.WorkingDirectory);
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} merged into"));   // every worktree spawn makes an agent commit, so HEAD moves

        var r = await _host.Client.PostAsJsonAsync("api/rooms/lab/messages", new { body = "@sonnet continue", replyToId = root });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
        var second = await _runner.NextSpecAsync(Wait);

        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(second));
        Assert.Equal(ExchangeWorktrees.PathFor(dir, root), second.WorkingDirectory);
        Assert.Contains("Turn 2 of 4;", second.StandardInput);
    }

    /// <summary>Row 36: like <see cref="DelayingMergeRunner"/>, but holds `git worktree remove`. The close
    /// has committed and the exchange's worktree is still registered and on disk, which is exactly the window
    /// a reopened exchange must not lease into (EnsureAsync would hand back the path without taking the gate).</summary>
    private sealed class DelayingRemoveRunner : IProcessRunner
    {
        private readonly IProcessRunner _inner = new ProcessRunner();
        public readonly TaskCompletionSource Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource RemoveAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation)
        {
            if (spec.Arguments.Contains("worktree") && spec.Arguments.Contains("remove"))
            {
                RemoveAttempted.TrySetResult();
                await Hold.Task;
            }
            return await _inner.RunAsync(spec, timeout, cancellation);
        }
    }

    [Fact]
    public async Task R36_a_reopened_exchange_waits_for_a_close_still_running_in_its_room()
    {
        var delayingRunner = new DelayingRemoveRunner();
        var localDir = _dir + "_reply_close_wait";
        await using var host = await HubTestHost.StartAsync(localDir, processRunner: _runner, limits: Fast,
            roomGit: dir => new GitTrail(dir, runner: delayingRunner));
        host.AuthorizeAs(ChopDb.OwnerParticipantId);
        var spawner = host.Services.GetRequiredService<SpawnerService>();
        var dir = Path.Combine(host.RoomsRoot, "lab-reply-wait");
        Assert.True(await new GitTrail(dir).InitAsync());
        host.Services.GetRequiredService<MessageStore>().CreateRoom("lab-reply-wait", "Lab", dir);
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"type":"result","result":"done"}"""));

        var post = await host.Client.PostAsJsonAsync("api/rooms/lab-reply-wait/messages", new { body = "@opus task A" });
        Assert.Equal(System.Net.HttpStatusCode.Created, post.StatusCode);
        await _runner.NextSpecAsync(Wait);
        await delayingRunner.RemoveAttempted.Task.WaitAsync(Wait);                                   // the close holds the worktree mid-removal
        var root = spawner.Snapshot("lab-reply-wait").RootMessageId!.Value;

        var reply = await host.Client.PostAsJsonAsync("api/rooms/lab-reply-wait/messages", new { body = "@sonnet continue", replyToId = root });
        Assert.Equal(System.Net.HttpStatusCode.Created, reply.StatusCode);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromSeconds(1)));                            // without the wait it would lease the dying worktree

        delayingRunner.Hold.TrySetResult();
        var second = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(second));
        Assert.Equal(ExchangeWorktrees.PathFor(dir, root), second.WorkingDirectory);
        using var doc = JsonDocument.Parse(await host.Client.GetStringAsync("api/rooms/lab-reply-wait/messages?afterId=0&limit=200"));
        var closeNote = doc.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.GetProperty("body").GetString()!)
            .First(b => b.StartsWith($"Exchange #{root} merged into"));
        Assert.DoesNotContain("was not", closeNote);
    }

    [Fact]
    public async Task R36_a_reply_to_a_stopped_worktree_exchange_continues_its_kept_branch()
    {
        var dir = await MakeRoom("lab");
        _runner.Handler = async (spec, _, ct) =>
        {
            File.WriteAllText(Path.Combine(spec.WorkingDirectory, "half.txt"), "half done\n");
            return await FakeProcessRunner.HangUntilKilled(TimeSpan.FromSeconds(30), ct);
        };
        await PostAsOwnerIn("lab", "@opus start");
        await _runner.NextSpecAsync(Wait);
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;
        var (outcome, _) = await Spawner.StopExchangeAsync("lab", root);
        Assert.Equal(ExchangeStopOutcome.Stopped, outcome);
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} was not merged"));

        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"type":"result","result":"done"}"""));
        var r = await _host.Client.PostAsJsonAsync("api/rooms/lab/messages", new { body = "@opus finish it", replyToId = root });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
        var spec = await _runner.NextSpecAsync(Wait);

        Assert.Equal(ExchangeWorktrees.PathFor(dir, root), spec.WorkingDirectory);
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} merged into"));
        Assert.True(File.Exists(Path.Combine(dir, "half.txt")));                                  // only the kept branch had it
        Assert.DoesNotContain(await MessagesIn("lab"), m => m.Body.StartsWith("@opus was not started"));
    }

    [Fact]
    public async Task R36_a_reply_never_adopts_a_branch_its_exchange_did_not_lease()
    {
        var dir = await MakeRoom("lab");
        Assert.True((await new GitTrail(dir).CommitAllAsync("seed", GitTrail.Hub, allowEmpty: true)).Created);
        // Reserve a history id without dispatch: M49 ordinary owner HTTP posts now start on-call work.
        var probe = _host.Services.GetRequiredService<MessageStore>().Post("lab", "claude", "branch collision id probe", null);
        var root = probe.Message.Id + 1;
        var made = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, ["branch", ExchangeWorktrees.Branch(root)], new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(0, made.ExitCode);
        var foreignHead = await GitLog(dir, "%H", 1, gitRef: ExchangeWorktrees.Branch(root));

        await PostAsOwnerIn("lab", "@opus go");                                                      // takes id root; its first lease is refused
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith("Exchange concluded"));

        var r = await _host.Client.PostAsJsonAsync("api/rooms/lab/messages", new { body = "@opus try again", replyToId = root });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);

        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromSeconds(1)));
        var deadline = DateTime.UtcNow + Wait;
        while ((await MessagesIn("lab")).Count(m => m.Body.StartsWith("@opus was not started:")) < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "the reopened lease was not refused");
            await Task.Delay(100);
        }
        Assert.Equal(foreignHead, await GitLog(dir, "%H", 1, gitRef: ExchangeWorktrees.Branch(root)));
    }

    // --- Start-up recovery ------------------------------------------------------------------

    private static async Task<string> GitShow(string dir, string gitRef, string path)
    {
        var r = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, ["show", $"{gitRef}:{path}"], new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        return r.StandardOutput;
    }

    [Fact]
    public async Task R35_hub_start_recovers_leftover_worktrees()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_worktree_recover_" + Guid.NewGuid().ToString("N"));
        var roomDir = Path.Combine(Path.GetTempPath(), "chopitup_worktree_recover_room_" + Guid.NewGuid().ToString("N"));
        Assert.True(await new GitTrail(roomDir).InitAsync());
        File.WriteAllText(Path.Combine(roomDir, "seed.txt"), "seed\n");
        Assert.NotNull((await new GitTrail(roomDir).CommitAllAsync("seed", GitTrail.Hub, allowEmpty: false)).Hash);
        const long root = 42;
        var branch = ExchangeWorktrees.Branch(root);

        try
        {
            string tree;
            string ownerToken;
            await using (var first = await HubTestHost.StartAsync(dir, deleteOnDispose: false))
            {
                first.Services.GetRequiredService<MessageStore>().CreateRoom("lab-recover", "Lab", roomDir);
                // Captured while the minting host is alive (row 28): the plaintext bearer itself outlives
                // it, since it is the same secret whose hash is persisted to tokens.json - a later host
                // over the SAME data dir can be handed it directly even though it cannot look it back up.
                ownerToken = first.TokenFor(ChopDb.OwnerParticipantId);
                // A worktree left registered with no close ever having run - exactly what a hub killed
                // mid-exchange leaves behind (no spawn needed to reproduce it: EnsureAsync's own
                // book-keeping is the whole of what a crash interrupts).
                var lease = await first.Services.GetRequiredService<ExchangeWorktrees>().EnsureAsync(roomDir, root, CancellationToken.None);
                Assert.Null(lease.Refusal);
                tree = lease.Path!;
                File.WriteAllText(Path.Combine(tree, "leftover.txt"), "left dirty by a crashed hub\n");
            }

            var second = await HubTestHost.StartAsync(dir, deleteOnDispose: false);
            try
            {
                // GET /api/rooms/.../messages needs no credential (BearerTokenMiddleware only guards
                // non-GET routes) - and "owner" is a host-file participant whose plaintext this second,
                // non-fresh host cannot look back up anyway (HubTestHost.TokenFor's own doc comment).
                var deadline = DateTime.UtcNow + Wait;
                (string Author, string Body) note = default;
                while (DateTime.UtcNow < deadline)
                {
                    using var doc = JsonDocument.Parse(await second.Client.GetStringAsync("api/rooms/lab-recover/messages?afterId=0&limit=200"));
                    note = doc.RootElement.GetProperty("messages").EnumerateArray()
                        .Select(m => (m.GetProperty("authorId").GetString()!, m.GetProperty("body").GetString()!))
                        .FirstOrDefault(m => m.Item1 == "hub" && m.Item2.Contains("restarted while exchange worktrees were open"));
                    if (note != default) break;
                    await Task.Delay(100);
                }
                Assert.NotEqual(default, note);
                Assert.Contains(branch, note.Body);
                Assert.False(Directory.Exists(tree));
                Assert.Equal("left dirty by a crashed hub\n", (await GitShow(roomDir, branch, "leftover.txt")).Replace("\r\n", "\n"));
            }
            finally { await second.DisposeAsync(); }

            // A second restart (nothing left to recover) is quiet - "a start never aborts a merge the
            // hub did not make" also covers "never repeats a note for what is already gone": the room's
            // history still holds the ONE note from the first recovery (messages are durable), but a
            // second, later restart must not add a second one.
            await using var third = await HubTestHost.StartAsync(dir, deleteOnDispose: true);
            // A deterministic proof that recovery has already run, instead of a bare delay: the
            // spawner's startup loop recovers every directory room (lab-recover included) entirely
            // before its event loop ever reads a posted message, so once THIS probe gets its own hub
            // note, every room's recovery has already happened.
            var probe = new HttpRequestMessage(HttpMethod.Post, "api/rooms/lab-recover/messages") { Content = JsonContent.Create(new { body = "/nope" }) };
            probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            Assert.Equal(System.Net.HttpStatusCode.Created, (await third.Client.SendAsync(probe)).StatusCode);
            var probeDeadline = DateTime.UtcNow + Wait;
            var probeAnswered = false;
            while (DateTime.UtcNow < probeDeadline)
            {
                using var probeDoc = JsonDocument.Parse(await third.Client.GetStringAsync("api/rooms/lab-recover/messages?afterId=0&limit=200"));
                probeAnswered = probeDoc.RootElement.GetProperty("messages").EnumerateArray()
                    .Any(m => m.GetProperty("authorId").GetString() == "hub" && m.GetProperty("body").GetString()!.Contains("No skill named"));
                if (probeAnswered) break;
                await Task.Delay(100);
            }
            Assert.True(probeAnswered);
            using var doc3 = JsonDocument.Parse(await third.Client.GetStringAsync("api/rooms/lab-recover/messages?afterId=0&limit=200"));
            Assert.Equal(1, doc3.RootElement.GetProperty("messages").EnumerateArray()
                .Count(m => m.GetProperty("body").GetString()!.Contains("restarted while exchange worktrees were open")));
        }
        finally
        {
            TestDirs.DeleteTree(roomDir);
        }
    }
}
