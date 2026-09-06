using System.Text.Json;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed class SpawnCommandsTests
{
    private static readonly ResolvedCli ClaudeExe = new(@"C:\tools\claude.exe", [], @"C:\tools\claude.exe");
    private static readonly ResolvedCli CodexShim = new(@"C:\Windows\System32\cmd.exe", ["/d", "/c", @"C:\tools\codex.cmd"], @"C:\tools\codex.cmd");

    [Fact]
    public void Claude_command_line_is_the_verified_one_and_carries_no_token()
    {
        var spec = SpawnCommands.Claude(ClaudeExe, "opus", @"C:\data\spawns\s1\mcp.json", @"C:\data\spawns\s1", "PROMPT", "opus/s1");
        Assert.Equal(@"C:\tools\claude.exe", spec.FileName);
        Assert.Equal(
            ["-p", "--tools", "", "--strict-mcp-config", "--mcp-config", @"C:\data\spawns\s1\mcp.json",
             "--allowedTools", "mcp__chopitup__post_message,mcp__chopitup__recall,mcp__chopitup__propose_memory", "--no-session-persistence", "--model", "opus",
             "--output-format", "json", "--disable-slash-commands", "--setting-sources", ""],
            spec.Arguments);
        Assert.Empty(spec.Environment);
        Assert.Equal("PROMPT", spec.StandardInput);
        Assert.Equal(@"C:\data\spawns\s1", spec.WorkingDirectory);
        Assert.DoesNotContain("--bare", spec.Arguments);   // --bare = API-key-only auth (claude --help)
    }

    [Fact]
    public void Claude_mcp_config_file_names_the_hub_server_with_a_bearer_header()
    {
        var json = SpawnCommands.ClaudeMcpConfigJson("http://127.0.0.1:8790/mcp", "tok123");
        using var doc = JsonDocument.Parse(json);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("chopitup");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:8790/mcp", server.GetProperty("url").GetString());
        Assert.Equal("Bearer tok123", server.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    [Fact]
    public void Codex_command_line_is_the_verified_one_with_the_token_in_the_environment_only()
    {
        var spec = SpawnCommands.Codex(CodexShim, "gpt-6-astra", "http://127.0.0.1:8790/mcp", "tok456", @"C:\data\spawns\s2", @"C:\data\spawns\s2\last.txt", "PROMPT", "gpt-6-astra/s2");
        Assert.Equal(@"C:\Windows\System32\cmd.exe", spec.FileName);
        Assert.Equal(
            ["/d", "/c", @"C:\tools\codex.cmd", "exec", "--ephemeral", "--ignore-user-config",
             "-c", "mcp_servers.chopitup.url=http://127.0.0.1:8790/mcp",
             "-c", "mcp_servers.chopitup.bearer_token_env_var=CHOPITUP_TOKEN",
             "-c", "mcp_servers.chopitup.startup_timeout_sec=20",
             "-c", "mcp_servers.chopitup.tool_timeout_sec=60",
             "--approve-for-me", "-C", @"C:\data\spawns\s2", "--skip-git-repo-check", "-m", "gpt-6-astra",
             "--color", "never", "-o", @"C:\data\spawns\s2\last.txt", "-"],
            spec.Arguments);
        Assert.Equal("tok456", spec.Environment["CHOPITUP_TOKEN"]);
        Assert.DoesNotContain(spec.Arguments, a => a.Contains("tok456"));
        Assert.Equal("PROMPT", spec.StandardInput);
    }

    [Fact]
    public void Final_text_comes_from_claude_json_result_or_codex_last_message_file()
    {
        Assert.Equal("hello", SpawnCommands.ClaudeFinalText("""{"type":"result","subtype":"success","is_error":false,"result":"hello"}"""));
        Assert.Null(SpawnCommands.ClaudeFinalText("not json"));
        Assert.Null(SpawnCommands.ClaudeFinalText(""));
        var file = Path.Combine(Path.GetTempPath(), "chopitup_last_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            Assert.Null(SpawnCommands.CodexFinalText(file));
            File.WriteAllText(file, "  world \n");
            Assert.Equal("world", SpawnCommands.CodexFinalText(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void M9_A6_claude_directory_command_line_runs_in_the_room_under_dontAsk_with_six_builtins_and_the_settings_file()
    {
        var spec = SpawnCommands.ClaudeInDirectory(ClaudeExe, "opus", @"C:\data\spawns\s1\mcp.json", @"C:\data\spawns\s1\settings.json", "RULES", @"C:\Rooms\lab", "PROMPT", "opus/s1");
        Assert.Equal(@"C:\tools\claude.exe", spec.FileName);
        Assert.Equal(
            ["-p", "--permission-mode", "dontAsk", "--tools", "Read,Edit,Write,Glob,Grep,Bash", "--strict-mcp-config", "--mcp-config", @"C:\data\spawns\s1\mcp.json",
             "--allowedTools", "Read,Edit,Write,Glob,Grep,Bash,mcp__chopitup__post_message,mcp__chopitup__recall,mcp__chopitup__propose_memory",
             "--settings", @"C:\data\spawns\s1\settings.json", "--append-system-prompt", "RULES", "--no-session-persistence", "--model", "opus",
             "--output-format", "stream-json", "--verbose", "--disable-slash-commands", "--setting-sources", ""],
            spec.Arguments);
        Assert.Equal(@"C:\Rooms\lab", spec.WorkingDirectory);
        Assert.Empty(spec.Environment);
        Assert.Equal("PROMPT", spec.StandardInput);
        Assert.DoesNotContain("bypassPermissions", spec.Arguments);
        Assert.DoesNotContain("--bare", spec.Arguments);
    }

    [Fact]
    public void M9_A6_claude_settings_deny_every_git_write_verb_any_option_form_git_exe_dot_git_and_the_credential_folders_for_read_write_edit_and_allow_nothing()
    {
        using var doc = JsonDocument.Parse(SpawnCommands.ClaudeSettingsJson());
        var permissions = doc.RootElement.GetProperty("permissions");
        var deny = permissions.GetProperty("deny").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.False(permissions.TryGetProperty("allow", out _));
        Assert.False(permissions.TryGetProperty("blockReadsOutsideWorkingDirectories", out _));   // measured ineffective on 2.1.220 (claim 23)
        foreach (var expected in new[]
                 {
                     "Bash(git commit *)", "Bash(git commit)", "Bash(git push *)", "Bash(git add *)", "Bash(git reset)", "Bash(git -*)", "Bash(git.exe *)", "Bash(git.exe)",
                     "Edit(.git/**)", "Write(.git/**)", "Read(~/.ssh/**)", "Read(~/.claude/**)", "Read(~/.codex/**)", "Write(~/.claude/**)", "Edit(~/.codex/**)", "Write(~/.ssh/**)",
                 })
            Assert.Contains(expected, deny);
        Assert.DoesNotContain("Bash(git log *)", deny);
        Assert.DoesNotContain("Bash(git status *)", deny);
        Assert.DoesNotContain("Bash(git *)", deny);
        Assert.DoesNotContain("Bash(*git.exe *)", deny);   // leading wildcards do not match on 2.1.220 (claim 35); a rule that looks like a fence and is not
        Assert.Equal(SpawnCommands.GitWriteVerbs.Length * 2 + 5 + SpawnCommands.CredentialFolders.Length * 3, deny.Count);
    }

    [Fact]
    public void M9_A7_directory_rules_name_the_directory_the_fence_the_git_rule_and_the_api_rule()
    {
        var rules = SpawnPrompt.DirectoryRules(@"C:\Rooms\lab");
        Assert.StartsWith(@"Stay inside your working directory, C:\Rooms\lab:", rules);
        Assert.Contains("do not read, list, create or change anything outside this directory", rules);
        Assert.Contains("Do not run git commands that write", rules);
        Assert.Contains("git log, git status and git diff are fine.", rules);
        Assert.Contains("Do not call the hub's HTTP API", rules);
        Assert.DoesNotContain("\n", rules);
    }

    [Fact]
    public void M9_A6_codex_directory_command_line_runs_in_the_room_with_json_and_network_and_without_the_repo_check_skip()
    {
        var spec = SpawnCommands.CodexInDirectory(CodexShim, "gpt-6-astra", "http://127.0.0.1:8790/mcp", "tok456", @"C:\Rooms\lab", @"C:\data\spawns\s2\last.txt", "PROMPT", "gpt-6-astra/s2");
        Assert.Equal(@"C:\Windows\System32\cmd.exe", spec.FileName);
        Assert.Equal(
            ["/d", "/c", @"C:\tools\codex.cmd", "exec", "--ephemeral", "--ignore-user-config", "--json",
             "-c", "mcp_servers.chopitup.url=http://127.0.0.1:8790/mcp",
             "-c", "mcp_servers.chopitup.bearer_token_env_var=CHOPITUP_TOKEN",
             "-c", "mcp_servers.chopitup.startup_timeout_sec=20",
             "-c", "mcp_servers.chopitup.tool_timeout_sec=60",
             "-c", "sandbox_workspace_write.network_access=true",
             "--approve-for-me", "-C", @"C:\Rooms\lab", "-m", "gpt-6-astra",
             "--color", "never", "-o", @"C:\data\spawns\s2\last.txt", "-"],
            spec.Arguments);
        Assert.Equal(@"C:\Rooms\lab", spec.WorkingDirectory);
        Assert.Equal("tok456", spec.Environment["CHOPITUP_TOKEN"]);
        Assert.DoesNotContain(spec.Arguments, a => a.Contains("tok456"));
        Assert.DoesNotContain("--skip-git-repo-check", spec.Arguments);
    }
}
