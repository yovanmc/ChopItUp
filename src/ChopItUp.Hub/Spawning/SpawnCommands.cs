using System.Text.Json;

namespace ChopItUp.Hub.Spawning;

/// <summary>The two command lines, verbatim from the runs measured on 2026-09-05 (plan header).
/// Pure: builds a <see cref="ProcessSpec"/>, touches nothing. The token never appears in an
/// argument — Claude reads it from the per-spawn <c>mcp.json</c>, Codex from
/// <see cref="TokenEnvVar"/>; both CLIs take the prompt on stdin (a Windows command line is capped
/// at 32,767 characters and a transcript is longer).</summary>
public static class SpawnCommands
{
    public const string McpServerName = "chopitup";
    public const string TokenEnvVar = "CHOPITUP_TOKEN";
    public const string ClaudeToolAllowed = "mcp__" + McpServerName + "__post_message";

    /// <summary>`--tools ""` drops every built-in and leaves MCP tools directly callable (LESSONS,
    /// M5 tool-surface); `--strict-mcp-config` + `--setting-sources ""` keep the owner's own MCP
    /// servers and settings out of the spawn; `--no-session-persistence` is D9; `--bare` is NEVER
    /// used — it switches auth to API key only, and this app holds no key.</summary>
    public static ProcessSpec Claude(ResolvedCli cli, string model, string mcpConfigPath, string workDir, string prompt, string label) =>
        new(cli.FileName,
            [.. cli.LeadingArguments,
             "-p", "--tools", "", "--strict-mcp-config", "--mcp-config", mcpConfigPath,
             "--allowedTools", ClaudeToolAllowed, "--no-session-persistence", "--model", model,
             "--output-format", "json", "--disable-slash-commands", "--setting-sources", ""],
            new Dictionary<string, string>(),
            workDir, prompt, label);

    public static string ClaudeMcpConfigJson(string mcpUrl, string token) =>
        JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                [McpServerName] = new { type = "http", url = mcpUrl, headers = new { Authorization = "Bearer " + token } },
            },
        });

    /// <summary>`--approve-for-me` is the only policy under which a headless Codex may call an MCP
    /// tool (LESSONS, M5 approvals); `--ignore-user-config` keeps the owner's config.toml out while
    /// auth still comes from CODEX_HOME (verified); `-c` values are literal strings when they are
    /// not TOML, so no quotes and no cmd.exe quoting hazards; `-` reads the prompt from stdin.</summary>
    public static ProcessSpec Codex(ResolvedCli cli, string model, string mcpUrl, string token, string workDir, string lastMessagePath, string prompt, string label) =>
        new(cli.FileName,
            [.. cli.LeadingArguments,
             "exec", "--ephemeral", "--ignore-user-config",
             "-c", $"mcp_servers.{McpServerName}.url={mcpUrl}",
             "-c", $"mcp_servers.{McpServerName}.bearer_token_env_var={TokenEnvVar}",
             "-c", $"mcp_servers.{McpServerName}.startup_timeout_sec=20",
             "-c", $"mcp_servers.{McpServerName}.tool_timeout_sec=60",
             "--approve-for-me", "-C", workDir, "--skip-git-repo-check", "-m", model,
             "--color", "never", "-o", lastMessagePath, "-"],
            new Dictionary<string, string> { [TokenEnvVar] = token },
            workDir, prompt, label);

    /// <summary>The model's final text from a `--output-format json` run: the `result` string, or
    /// null when stdout is not that JSON.</summary>
    public static string? ClaudeFinalText(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()?.Trim() is { Length: > 0 } s ? s : null
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The model's final text from `-o <file>`, or null when the file is absent or blank.</summary>
    public static string? CodexFinalText(string lastMessagePath)
    {
        if (!File.Exists(lastMessagePath)) return null;
        var text = File.ReadAllText(lastMessagePath).Trim();
        return text.Length == 0 ? null : text;
    }
}
