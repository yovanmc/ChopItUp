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
             "--allowedTools", "mcp__chopitup__post_message", "--no-session-persistence", "--model", "opus",
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
}
