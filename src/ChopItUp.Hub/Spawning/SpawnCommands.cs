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
    /// <summary>Comma-separated in one value (`claude --help`: "Comma or space-separated list"). Read
    /// tools stay off the list: the prompt already carries the transcript (plan decision 11).</summary>
    public const string ClaudeToolAllowed = "mcp__" + McpServerName + "__post_message,mcp__" + McpServerName + "__recall,mcp__" + McpServerName + "__propose_memory";

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

    /// <summary>The model's final text from a `--output-format json` or `stream-json` run (see
    /// <see cref="SpawnOutput.ClaudeFinalText"/>); null when stdout is neither.</summary>
    public static string? ClaudeFinalText(string stdout) => SpawnOutput.ClaudeFinalText(stdout);

    /// <summary>The model's final text from `-o <file>`, or null when the file is absent or blank.</summary>
    public static string? CodexFinalText(string lastMessagePath)
    {
        if (!File.Exists(lastMessagePath)) return null;
        var text = File.ReadAllText(lastMessagePath).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>M9 decision 9: the built-in tools a spawn in a directory room gets, and the allow list
    /// that pre-approves them beside the three MCP tools. `dontAsk` denies anything not on the list.</summary>
    public const string ClaudeBuiltins = "Read,Edit,Write,Glob,Grep,Bash";
    public const string ClaudeDirectoryToolsAllowed = ClaudeBuiltins + "," + ClaudeToolAllowed;

    /// <summary>Profile folders that hold credentials — refused as room directories (RoomPathRules) and
    /// denied to Claude's file tools through the settings deny list (M9 decisions 3, 8). `~/` patterns
    /// are the one path form measured to bind on 2.1.220 (claim 23).</summary>
    public static readonly string[] CredentialFolders = [".claude", ".codex", ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker"];

    /// <summary>Every git subcommand that writes the index, the tree, refs, config or remotes.
    /// `Bash(git -*)` (below) covers any invocation that opens with an option (`-c`, `-C`, `--git-dir`,
    /// `--work-tree`), which is the form that reaches these verbs sideways. Read-only verbs (log, status,
    /// diff, show, blame, grep, ls-files, rev-parse) stay allowed: D11 says read-only, not off.</summary>
    public static readonly string[] GitWriteVerbs =
    [
        "add", "am", "apply", "bisect", "branch", "checkout", "cherry-pick", "clean", "clone", "commit", "config",
        "fast-import", "fetch", "filter-branch", "gc", "init", "merge", "mv", "notes", "prune", "pull", "push",
        "rebase", "reflog", "remote", "repack", "replace", "reset", "restore", "revert", "rm", "stash",
        "submodule", "switch", "symbolic-ref", "tag", "update-index", "update-ref", "worktree", "write-tree",
    ];

    /// <summary>The deny list of the per-spawn settings file: measured on 2.1.220 to block `git commit`,
    /// `git -c … commit` and `git.exe commit` while `echo`, `git log` and in-room writes ran, and to block
    /// a Write under a denied `~/` folder while a cwd Write succeeded (claims 23, 35). Deny rules apply in
    /// every permission mode and are prefix rules: an absolute-path `git.exe` is NOT caught (measured) and
    /// is left to the trail. There is deliberately no allow list here (the command line carries it) and
    /// no read fence (measured ineffective on this version — a rule in the prompt instead, decision 8).</summary>
    public static IReadOnlyList<string> ClaudeDenyRules()
    {
        var rules = new List<string>();
        foreach (var verb in GitWriteVerbs)
        {
            rules.Add($"Bash(git {verb} *)");
            rules.Add($"Bash(git {verb})");
        }
        rules.Add("Bash(git -*)");
        rules.Add("Bash(git.exe *)");
        rules.Add("Bash(git.exe)");
        rules.Add("Edit(.git/**)");
        rules.Add("Write(.git/**)");
        foreach (var folder in CredentialFolders)
        {
            rules.Add($"Read(~/{folder}/**)");
            rules.Add($"Write(~/{folder}/**)");
            rules.Add($"Edit(~/{folder}/**)");
        }
        return rules;
    }

    public static string ClaudeSettingsJson() =>
        JsonSerializer.Serialize(new { permissions = new { deny = ClaudeDenyRules() } });

    /// <summary>A spawn in a directory room (M9 decision 9): cwd is the room's tree; `dontAsk` plus the
    /// allow list runs the six built-ins and the three MCP tools without a prompt and auto-denies
    /// everything else (protected-path writes included — under `bypassPermissions` they would be
    /// auto-approved); the deny list rides in <paramref name="settingsPath"/>, which sits in the scratch
    /// folder beside <paramref name="mcpConfigPath"/>, never in the room; `stream-json` + `--verbose` is
    /// what carries the Bash calls the trail records; <paramref name="systemRules"/> (`SpawnPrompt.DirectoryRules`)
    /// rides as an appended system prompt (F10) so the fence is not only in the transcript channel.</summary>
    public static ProcessSpec ClaudeInDirectory(ResolvedCli cli, string model, string mcpConfigPath, string settingsPath, string systemRules, string roomDir, string prompt, string label) =>
        new(cli.FileName,
            [.. cli.LeadingArguments,
             "-p", "--permission-mode", "dontAsk", "--tools", ClaudeBuiltins, "--strict-mcp-config", "--mcp-config", mcpConfigPath,
             "--allowedTools", ClaudeDirectoryToolsAllowed, "--settings", settingsPath, "--append-system-prompt", systemRules, "--no-session-persistence", "--model", model,
             "--output-format", "stream-json", "--verbose", "--disable-slash-commands", "--setting-sources", ""],
            new Dictionary<string, string>(),
            roomDir, prompt, label);

    /// <summary>A Codex spawn in a directory room: `-C` is the room (a repository, so the repo check is
    /// not skipped), `--json` carries the command_execution items the trail records, and network is on
    /// inside workspace-write (D10). Measured 2026-09-06 (claim 24): every flag accepted; note the
    /// sandbox did NOT stop a `git commit` — the prompt rule and the trail are the mechanism (decision 7).</summary>
    public static ProcessSpec CodexInDirectory(ResolvedCli cli, string model, string mcpUrl, string token, string roomDir, string lastMessagePath, string prompt, string label) =>
        new(cli.FileName,
            [.. cli.LeadingArguments,
             "exec", "--ephemeral", "--ignore-user-config", "--json",
             "-c", $"mcp_servers.{McpServerName}.url={mcpUrl}",
             "-c", $"mcp_servers.{McpServerName}.bearer_token_env_var={TokenEnvVar}",
             "-c", $"mcp_servers.{McpServerName}.startup_timeout_sec=20",
             "-c", $"mcp_servers.{McpServerName}.tool_timeout_sec=60",
             "-c", "sandbox_workspace_write.network_access=true",
             "--approve-for-me", "-C", roomDir, "-m", model,
             "--color", "never", "-o", lastMessagePath, "-"],
            new Dictionary<string, string> { [TokenEnvVar] = token },
            roomDir, prompt, label);
}
