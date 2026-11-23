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

public sealed class SpawnerServiceRoomsTests : SpawnerServiceTestBase
{
    private const string ClaudeStreamWithTwoCommands = """
        {"type":"system","subtype":"init"}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"echo hello"}}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"git commit -m nope"}}]}}
        {"type":"result","subtype":"success","is_error":false,"result":"done","permission_denials":[{"tool_name":"Bash","tool_use_id":"t2","tool_input":{"command":"git commit -m nope"}}]}
        """;

    // gitRef reads a branch other than the one checked out at dir: a directory room's own exchange
    // branches (see ExchangeWorktrees.Branch) share dir's repository, so its refs are visible from dir
    // even though the commits on them were made from a linked worktree. Once a close merges an
    // exchange's branch into dir, its commits are still reachable from dir's own HEAD, just not at the
    // top (the merge commit is); skip walks past the newer entries without needing the branch name
    // (deleted by then) or the merge commit's own hash.
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

    private static async Task<string> GitOutput(string dir, params string[] gitArgs)
    {
        var r = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, gitArgs, new Dictionary<string, string>(), dir, "", "test-git"),
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
        var tree = ExchangeWorktrees.PathFor(dir, root);                                      // works in its own worktree
        Assert.Equal(tree, spec.WorkingDirectory);
        Assert.Contains("dontAsk", spec.Arguments);
        Assert.Equal("stream-json", spec.Arguments[spec.Arguments.ToList().IndexOf("--output-format") + 1]);
        var settingsPath = spec.Arguments[spec.Arguments.ToList().IndexOf("--settings") + 1];
        Assert.StartsWith(Path.Combine(_dir, "spawns"), settingsPath);                         // scratch, not the room
        Assert.StartsWith(Path.Combine(_dir, "spawns"), spec.Arguments[spec.Arguments.ToList().IndexOf("--mcp-config") + 1]);
        Assert.Contains(@"Files: this room's directory is " + tree, spec.StandardInput);
        Assert.Equal(SpawnPrompt.DirectoryRules(tree, dir, RoomArtifacts.FolderFor(dir)) + " " + SpawnPrompt.WebRule, spec.Arguments[spec.Arguments.ToList().IndexOf("--append-system-prompt") + 1]);   // the fence in the system channel
        Assert.Equal(SpawnCommands.ClaudeBuiltins, spec.Arguments[spec.Arguments.ToList().IndexOf("--tools") + 1]);              // web tools out of a run too
        Assert.Equal(SpawnCommands.ClaudeDirectoryToolsAllowed, spec.Arguments[spec.Arguments.ToList().IndexOf("--allowedTools") + 1]);

        var note = await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith(HubNotes.TrailPrefix));
        Assert.Contains("for sonnet: 1 file(s) changed, 2 shell command(s).", note.Body);
        Assert.Contains("Your edits were committed first as", note.Body);
        Assert.Contains("\"Bash(git commit *)\"", settingsAtLaunch);

        // The exchange concludes and its worktree is merged into the room directory: wait for that
        // note, then read only dir (the worktree is gone once the close finishes).
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

    /// <summary>The spawn's own token is scrubbed from the whole stream before either list is cut, so a
    /// token the 400-character cap would split cannot reach git in part.</summary>
    [Fact]
    public async Task Web_requests_reach_the_room_commit_stripped_and_scrubbed_and_the_note_counts_them()
    {
        var dir = await MakeRoom("lab-web");
        string token = "";
        _runner.Handler = async (spec, _, _) =>
        {
            var args = spec.Arguments.ToList();
            using (var doc = JsonDocument.Parse(File.ReadAllText(args[args.IndexOf("--mcp-config") + 1])))
                token = doc.RootElement.GetProperty("mcpServers").GetProperty("chopitup").GetProperty("headers").GetProperty("Authorization").GetString()!["Bearer ".Length..];
            await PostAsIn("sonnet", "lab-web", "Looked it up.");
            var pad = new string('x', 380);
            return FakeProcessRunner.Ok($$$$$"""
                {"type":"assistant","message":{"content":[{"type":"tool_use","id":"b1","name":"Bash","input":{"command":"echo {{{{{pad}}}}}{{{{{token}}}}}"}}]}}
                {"type":"assistant","message":{"content":[{"type":"tool_use","id":"w1","name":"WebSearch","input":{"query":"\u001b[31mred\u001b[0m query"}}]}}
                {"type":"assistant","message":{"content":[{"type":"tool_use","id":"w2","name":"WebFetch","input":{"url":"https://example.com/?k={{{{{token}}}}}"}}]}}
                {"type":"assistant","message":{"content":[{"type":"tool_use","id":"w3","name":"WebFetch","input":{"url":"http://127.0.0.1:8790/api/rooms"}}]}}
                {"type":"result","subtype":"success","is_error":false,"result":"done","permission_denials":[{"tool_name":"WebFetch","tool_use_id":"w3"}]}
                """);
        };

        await PostAsOwnerIn("lab-web", "@sonnet look it up");
        await _runner.NextSpecAsync(Wait);
        var root = Spawner.Snapshot("lab-web").RootMessageId!.Value;
        var note = await WaitForMessageIn("lab-web", m => m.Author == "hub" && m.Body.StartsWith(HubNotes.TrailPrefix));
        Assert.EndsWith("1 shell command(s), 3 web request(s).", note.Body);

        await WaitForMessageIn("lab-web", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} merged into"));
        var body = (await GitLog(dir, "%B", 1, skip: 1)).Replace("\r\n", "\n");
        Assert.Contains("\nWeb requests (3):\n  1. WebSearch: red query\n  2. WebFetch: https://example.com/?k=<token>\n  3. WebFetch: http://127.0.0.1:8790/api/rooms [denied]", body);
        Assert.Contains("echo " + new string('x', 380) + "<token>", body);
        Assert.DoesNotContain(token[..12], body);
        Assert.DoesNotContain(await MessagesIn("lab-web"), m => m.Author == "hub" && m.Body.StartsWith("Web and shell calls by"));   // the commit is the record here
    }

    private static string TokenOf(ProcessSpec spec)
    {
        var args = spec.Arguments.ToList();
        using var doc = JsonDocument.Parse(File.ReadAllText(args[args.IndexOf("--mcp-config") + 1]));
        return doc.RootElement.GetProperty("mcpServers").GetProperty("chopitup").GetProperty("headers").GetProperty("Authorization").GetString()!["Bearer ".Length..];
    }

    /// <summary>A plain room has no commit trail, so its spawn's web calls go to one fenced room note,
    /// stripped and scrubbed like the commit's lines; the launch carries the loopback deny list.</summary>
    [Fact]
    public async Task A_plain_room_claude_spawns_web_calls_reach_one_fenced_note_stripped_and_scrubbed()
    {
        string token = "", settings = "";
        _runner.Handler = async (spec, _, _) =>
        {
            token = TokenOf(spec);
            var args = spec.Arguments.ToList();
            settings = File.ReadAllText(args[args.IndexOf("--settings") + 1]);
            await PostAs("sonnet", "Looked it up.");
            return FakeProcessRunner.Ok($$$$$"""
                {"type":"assistant","message":{"content":[{"type":"tool_use","id":"w1","name":"WebSearch","input":{"query":"\u001b[31mred\u001b[0m query"}}]}}
                {"type":"assistant","message":{"content":[{"type":"tool_use","id":"w2","name":"WebFetch","input":{"url":"https://example.com/?k={{{{{token}}}}}"}}]}}
                {"type":"assistant","message":{"content":[{"type":"tool_use","id":"w3","name":"WebFetch","input":{"url":"http://127.0.0.1:8790/api/rooms"}}]}}
                {"type":"result","subtype":"success","is_error":false,"result":"done","permission_denials":[{"tool_name":"WebFetch","tool_use_id":"w3"}]}
                """);
        };

        await PostAsOwner("@sonnet look it up");
        await _runner.NextSpecAsync(Wait);
        var note = await WaitForMessage(m => m.Author == "hub" && m.Body.StartsWith("Web and shell calls by sonnet (3)."));
        Assert.Contains("```text\n1. WebSearch \"red query\"\n2. WebFetch \"https://example.com/?k=<token>\"\n3. WebFetch \"http://127.0.0.1:8790/api/rooms\" [denied]\n```", note.Body);
        Assert.DoesNotContain(token[..12], note.Body);
        Assert.Contains("WebFetch(domain:[::1])", settings);
        Assert.Contains("WebFetch(domain:127.0.0.1)", settings);
    }

    [Fact]
    public async Task A_plain_room_codex_spawns_searches_and_commands_reach_one_note_in_order()
    {
        _runner.Handler = async (_, _, _) =>
        {
            await PostAs("gpt-6-astra", "Searched.");
            return FakeProcessRunner.Ok(
                "{\"type\":\"item.completed\",\"item\":{\"id\":\"w1\",\"type\":\"web_search\",\"query\":\"q\",\"action\":{\"type\":\"search\",\"query\":\"dotnet 10\"}}}\n"
                + "{\"type\":\"item.completed\",\"item\":{\"id\":\"c1\",\"type\":\"command_execution\",\"command\":\"curl https://example.com\"}}\n"
                + "{\"type\":\"item.completed\",\"item\":{\"id\":\"m1\",\"type\":\"mcp_tool_call\",\"server\":\"chopitup\",\"tool\":\"post_message\",\"arguments\":{\"body\":\"Searched.\"}}}\n"
                + "{\"type\":\"item.completed\",\"item\":{\"id\":\"m2\",\"type\":\"mcp_tool_call\",\"server\":\"codex_apps\",\"tool\":\"search_service.web_run\",\"arguments\":{\"system2_search_query\":[{\"q\":\"mcp spec\"}]}}}\n"
                + "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":5,\"output_tokens\":1}}\n");
        };

        await PostAsOwner("@gpt-6-astra search it");
        await _runner.NextSpecAsync(Wait);
        var note = await WaitForMessage(m => m.Author == "hub" && m.Body.StartsWith("Web and shell calls by gpt-6-astra (3)."));
        Assert.Contains("```text\n1. WebSearch \"dotnet 10\"\n2. Shell \"curl https://example.com\"\n3. WebSearch \"mcp spec\"\n```", note.Body);
    }

    /// <summary>A Codex call to the hub's own MCP server is not a web or shell call.</summary>
    [Theory]
    [InlineData("sonnet", FakeProcessRunner.StreamDone)]
    [InlineData("gpt-6-astra", "{\"type\":\"item.completed\",\"item\":{\"id\":\"m1\",\"type\":\"mcp_tool_call\",\"server\":\"chopitup\",\"tool\":\"post_message\",\"arguments\":{\"body\":\"No lookups.\"}}}\n{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":5,\"output_tokens\":1}}\n")]
    public async Task A_plain_room_spawn_with_no_web_call_posts_no_calls_note(string participant, string stdout)
    {
        _runner.Handler = async (_, _, _) => { await PostAs(participant, "No lookups."); return FakeProcessRunner.Ok(stdout); };
        await PostAsOwner($"@{participant} just answer");
        await _runner.NextSpecAsync(Wait);
        await WaitForStatus("concluded");
        await Spawner.InLoopAsync(() => true);
        Assert.DoesNotContain(await Messages(), m => m.Author == "hub" && m.Body.StartsWith("Web and shell calls by"));
    }

    private const string OneSearch = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"w1\",\"name\":\"WebSearch\",\"input\":{\"query\":\"late\"}}]}}\n";

    /// <summary>The note is posted on every end path, not only a clean finish.</summary>
    [Fact]
    public async Task A_timed_out_plain_spawn_still_posts_its_calls_note()
    {
        _runner.Handler = (_, timeout, _) => Task.FromResult(new ProcessResult(null, true, false, OneSearch, "", timeout));
        await PostAsOwner("@sonnet look slowly");
        await _runner.NextSpecAsync(Wait);
        await WaitForMessage(m => m.Author == "hub" && m.Body.StartsWith("Web and shell calls by sonnet (1).") && m.Body.Contains("WebSearch \"late\""));
    }

    /// <summary>A relay leg finishes through the mode branch and still posts its calls note.</summary>
    [Fact]
    public async Task A_plain_room_relay_leg_posts_its_calls_note()
    {
        Mode("relay");
        _runner.Handler = (spec, _, _) => Task.FromResult(FakeProcessRunner.Ok(FakeProcessRunner.ParticipantOf(spec) == "sonnet"
            ? OneSearch.Replace("late", "relayed") + "{\"type\":\"result\",\"result\":\"relay first\"}\n" : FakeProcessRunner.StreamDone));
        await PostAsOwner("Relay with a lookup.");
        await WaitForMessage(m => m.Author == "hub" && m.Body.StartsWith("Web and shell calls by sonnet (1).") && m.Body.Contains("WebSearch \"relayed\""));
    }

    [Theory]
    [InlineData("sonnet")]
    [InlineData("gpt-6-astra")]
    public async Task A_spawn_writes_to_the_artifacts_folder_and_the_file_outlasts_the_exchange_outside_git(string participant)
    {
        var dir = await MakeRoom("lab-art");
        var folder = dir + ".artifacts";
        _runner.Handler = async (spec, _, _) =>
        {
            File.WriteAllText(Path.Combine(spec.Environment[SpawnCommands.ArtifactsEnvVar], "render.png"), "pixels");
            await PostAsIn(participant, "lab-art", "Rendered.");
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwnerIn("lab-art", $"@{participant} render it");
        var spec = await _runner.NextSpecAsync(Wait);
        var args = spec.Arguments.ToList();
        Assert.Equal(folder, args[args.IndexOf("--add-dir") + 1]);
        Assert.Contains($"One folder outside your working directory is also yours: {folder}.", spec.StandardInput);

        var root = Spawner.Snapshot("lab-art").RootMessageId!.Value;
        await WaitForMessageIn("lab-art", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} merged into"));
        Assert.False(Directory.Exists(ExchangeWorktrees.PathFor(dir, root)));
        Assert.Equal("pixels", File.ReadAllText(Path.Combine(folder, "render.png")));
        Assert.DoesNotContain("render.png", await GitLog(dir, "%H", 50) + await GitOutput(dir, "log", "--all", "--name-only", "--format="));
        Assert.Equal("", await GitOutput(dir, "status", "--porcelain"));
    }

    [Fact]
    public async Task A_room_whose_artifacts_path_cannot_be_used_still_runs_its_spawns_and_says_so_once()
    {
        var dir = await MakeRoom("lab-noart");
        File.WriteAllText(dir + ".artifacts", "the owner's file");
        _runner.Handler = async (_, _, _) =>
        {
            await PostAsIn("sonnet", "lab-noart", "Done.");
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwnerIn("lab-noart", "@sonnet one");
        var first = await _runner.NextSpecAsync(Wait);
        await WaitForMessageIn("lab-noart", m => m.Author == "hub" && m.Body.Contains("merged into"));
        await PostAsOwnerIn("lab-noart", "@sonnet two");
        var second = await _runner.NextSpecAsync(Wait);

        foreach (var spec in new[] { first, second })
        {
            Assert.False(spec.Environment.ContainsKey(SpawnCommands.ArtifactsEnvVar));
            Assert.DoesNotContain("--add-dir", spec.Arguments);
            Assert.DoesNotContain("One folder outside your working directory", spec.StandardInput);
        }
        Assert.Equal("the owner's file", File.ReadAllText(dir + ".artifacts"));
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/lab-noart/messages?afterId=0&limit=200"));
        Assert.Single(doc.RootElement.GetProperty("messages").EnumerateArray(), m => m.GetProperty("body").GetString()!.StartsWith("This room has no artifacts folder"));
    }

    [Fact]
    public async Task A_folder_replaced_by_a_file_after_one_spawn_is_left_out_of_the_next_spawn()
    {
        var dir = await MakeRoom("lab-swap");
        var folder = dir + ".artifacts";
        _runner.Handler = async (_, _, _) =>
        {
            await PostAsIn("sonnet", "lab-swap", "Done.");
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwnerIn("lab-swap", "@sonnet one");
        var first = await _runner.NextSpecAsync(Wait);
        Assert.Equal(folder, first.Environment[SpawnCommands.ArtifactsEnvVar]);
        await WaitForMessageIn("lab-swap", m => m.Author == "hub" && m.Body.Contains("merged into"));

        Directory.Delete(folder, recursive: true);
        File.WriteAllText(folder, "the owner's file");
        await PostAsOwnerIn("lab-swap", "@sonnet two");
        var second = await _runner.NextSpecAsync(Wait);

        Assert.False(second.Environment.ContainsKey(SpawnCommands.ArtifactsEnvVar));
        Assert.DoesNotContain("--add-dir", second.Arguments);
        Assert.DoesNotContain("One folder outside your working directory", second.StandardInput);
        Assert.Equal("the owner's file", File.ReadAllText(folder));
    }

    [Fact]
    public async Task A_note_names_each_file_over_5_mb_the_spawns_commits_added_and_no_other_file()
    {
        const int limit = (int)HubNotes.LargeFileBytes;
        var dir = await MakeRoom("lab-large");
        File.WriteAllBytes(Path.Combine(dir, "tracked.bin"), new byte[limit + 10]);
        File.WriteAllBytes(Path.Combine(dir, "doomed.bin"), new byte[limit + 20]);
        File.WriteAllBytes(Path.Combine(dir, "moved.bin"), new byte[limit + 30]);
        _runner.Handler = async (spec, _, _) =>
        {
            var tree = spec.WorkingDirectory;
            File.WriteAllBytes(Path.Combine(tree, "passing.bin"), new byte[limit + 3]);
            File.WriteAllBytes(Path.Combine(tree, "shrunk.bin"), new byte[limit + 5]);
            await new GitTrail(tree).CommitAllAsync("mid-spawn", new GitIdentity("Model", "model@example.test"), allowEmpty: false);
            File.Delete(Path.Combine(tree, "passing.bin"));                                  // gone from the tree, still in history
            File.Delete(Path.Combine(tree, "shrunk.bin"));
            await new GitTrail(tree).CommitAllAsync("mid-spawn removal", new GitIdentity("Model", "model@example.test"), allowEmpty: false);
            File.WriteAllBytes(Path.Combine(tree, "shrunk.bin"), new byte[10]);              // added again, and the large version stays in history
            File.WriteAllBytes(Path.Combine(tree, "over.bin"), new byte[limit + 1]);
            File.WriteAllBytes(Path.Combine(tree, "größe.bin"), new byte[limit + 2]);
            File.WriteAllBytes(Path.Combine(tree, "exact.bin"), new byte[limit]);
            File.WriteAllBytes(Path.Combine(tree, "tracked.bin"), new byte[limit + 11]);     // modified, not added
            File.Delete(Path.Combine(tree, "doomed.bin"));
            File.Move(Path.Combine(tree, "moved.bin"), Path.Combine(tree, "renamed.bin"));   // no new content for git to keep
            File.WriteAllBytes(Path.Combine(tree, "copy-one.bin"), new byte[limit + 4]);
            File.WriteAllBytes(Path.Combine(tree, "copy-two.bin"), new byte[limit + 4]);     // one blob under two names
            await PostAsIn("sonnet", "lab-large", "Done.");
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwnerIn("lab-large", "@sonnet make files");

        var note = await WaitForMessageIn("lab-large", m => m.Author == "hub" && m.Body.Contains("over 5 MB to git history"));
        foreach (var name in new[] { "over.bin", "größe.bin", "passing.bin", "shrunk.bin", "copy-one.bin", "copy-two.bin" }) Assert.Contains(name + " (", note.Body);
        foreach (var name in new[] { "exact.bin", "tracked.bin", "doomed.bin", "moved.bin", "renamed.bin" }) Assert.DoesNotContain(name, note.Body);
        Assert.Contains("6 file(s)", note.Body);
        Assert.Contains(dir + ".artifacts", note.Body);
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
            // Ignores the cancellation token on purpose: a run's cancelled conductor is not really
            // gone until its own process ends, and this reproduces exactly that window.
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
        // A worktree close's merge is genuinely running (the repository's write gate is held) when a
        // run's conductor (a room-directory launch) would otherwise be due; it must get no spec until
        // the close finishes.
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
    public async Task M9_A6_a_codex_spawn_in_a_directory_room_gets_the_room_as_its_workspace_and_json_output()
    {
        var dir = await MakeRoom("lab");
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok(""));
        await PostAsOwnerIn("lab", "@gpt-6-astra look around");
        var spec = await _runner.NextSpecAsync(Wait);
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;
        var tree = ExchangeWorktrees.PathFor(dir, root);                                      // works in its own worktree
        var args = spec.Arguments.ToList();
        Assert.Equal(tree, spec.WorkingDirectory);
        Assert.Equal(tree, args[args.IndexOf("-C") + 1]);
        Assert.Contains("--json", args);
        Assert.Contains("sandbox_workspace_write.network_access=true", args);
        Assert.DoesNotContain("--skip-git-repo-check", args);
        Assert.StartsWith(Path.Combine(_dir, "spawns"), args[args.IndexOf("-o") + 1]);
        var note = await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith(HubNotes.TrailPrefix));
        Assert.Contains("for gpt-6-astra: 0 file(s) changed, 0 shell command(s).", note.Body);   // empty commit, empty log
        // Wait for the close's merge note, then read the merged history in dir itself: the branch is
        // deleted once the merge lands.
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
        // Wait for the close's merge note, then read the merged history in dir. The room itself was
        // unborn, so ExchangeWorktrees made one empty start commit for the branch to fork from, and
        // that start commit is what the merge commit's other parent already is.
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
    public async Task A_null_directory_room_launches_claude_with_only_the_web_tools_and_its_settings_in_the_scratch_folder()
    {
        await PostAsOwner("@opus hi");
        var spec = await _runner.NextSpecAsync(Wait);
        var mcp = Path.Combine(spec.WorkingDirectory, "mcp.json");
        var settings = Path.Combine(spec.WorkingDirectory, "settings.json");
        Assert.Equal(["-p", "--permission-mode", "dontAsk", "--tools", "WebSearch,WebFetch", "--strict-mcp-config", "--mcp-config", mcp,
                      "--allowedTools", "WebSearch,WebFetch," + SpawnCommands.ClaudeToolAllowed, "--settings", settings, "--append-system-prompt", SpawnPrompt.PlainRoomTools("claude"),
                      "--no-session-persistence", "--model", "opus", "--output-format", "stream-json", "--verbose", "--disable-slash-commands", "--setting-sources", ""],
            spec.Arguments);
        Assert.StartsWith(Path.Combine(_dir, "spawns"), spec.WorkingDirectory);
        Assert.DoesNotContain("Files: this room's directory", spec.StandardInput);
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

    /// <summary>Like <see cref="DelayingMergeRunner"/>, but holds `git worktree remove`. The close
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
        // Reserve a history id without dispatch: ordinary owner HTTP posts start on-call work.
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
                // Captured while the minting host is alive: the plaintext bearer itself outlives it,
                // since it is the same secret whose hash is persisted to tokens.json, so a later host
                // over the same data dir can be handed it directly even though it cannot look it back up.
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
