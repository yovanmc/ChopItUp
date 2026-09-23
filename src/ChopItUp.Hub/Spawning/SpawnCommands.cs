using System.Text.Json;

namespace ChopItUp.Hub.Spawning;

/// <summary>The two command lines, verbatim from measured runs. Pure: builds a
/// <see cref="ProcessSpec"/>, touches nothing. The token never appears in an argument: Claude reads
/// it from the per-spawn <c>mcp.json</c>, Codex from <see cref="TokenEnvVar"/>; both CLIs take the
/// prompt on stdin (a Windows command line is capped at 32,767 characters and a transcript is
/// longer).</summary>
public static class SpawnCommands
{
    public const string McpServerName = "chopitup";
    public const string TokenEnvVar = "CHOPITUP_TOKEN";
    /// <summary>Comma-separated in one value (`claude --help`: "Comma or space-separated list"). Read
    /// tools stay off the list: the prompt already carries the transcript.</summary>
    public const string ClaudeToolAllowed = "mcp__" + McpServerName + "__post_message,mcp__" + McpServerName + "__recall,mcp__" + McpServerName + "__propose_memory,mcp__" + McpServerName + "__propose_rewrite";

    /// <summary>`--tools ""` drops every built-in and leaves MCP tools directly callable;
    /// `--strict-mcp-config` + `--setting-sources ""` keep the owner's own MCP servers and settings
    /// out of the spawn; `--no-session-persistence` keeps the spawn stateless; `--bare` is never
    /// used: it switches auth to API key only, and this app holds no key. <paramref name="effort"/>
    /// is null outside a run and for an ordinary in-run row (nothing appended); a conductor or a
    /// `judge`-class row gets exactly `--effort high`, never `xhigh` or `max`.</summary>
    public static ProcessSpec Claude(ResolvedCli cli, string model, string mcpConfigPath, string workDir, string prompt, string label, string? effort = null) =>
        new(cli.FileName,
            [.. cli.LeadingArguments,
             "-p", "--tools", "", "--strict-mcp-config", "--mcp-config", mcpConfigPath,
             "--allowedTools", ClaudeToolAllowed, "--no-session-persistence", "--model", model,
             "--output-format", "json", "--disable-slash-commands", "--setting-sources", "",
             .. effort is null ? Array.Empty<string>() : new[] { "--effort", effort }],
            new Dictionary<string, string>(),
            workDir, prompt, label);

    // WriteIndented + a trailing newline: this JSON also lands in
    // host-configs\claude-code-owner-remote.json, which the owner hand-merges like every other file
    // in that folder, and those are all indented with a trailing newline. The per-spawn mcp.json
    // this also produces (SpawnerService) is read by the Claude CLI, which does not care either way.
    //
    // <paramref name="toolTimeoutMs"/>: the installed CLI's own per-server <c>timeout</c> field, in
    // milliseconds, a documented override of <see cref="ClaudeMcpToolTimeoutEnvVar"/> for this one
    // server. Null (every out-of-run caller) omits the field entirely rather than writing an
    // explicit default.
    public static string ClaudeMcpConfigJson(string mcpUrl, string token, int? toolTimeoutMs = null)
    {
        object server = toolTimeoutMs is null
            ? new { type = "http", url = mcpUrl, headers = new { Authorization = "Bearer " + token } }
            : new { type = "http", url = mcpUrl, headers = new { Authorization = "Bearer " + token }, timeout = toolTimeoutMs.Value };
        return JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object> { [McpServerName] = server },
        }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    /// <summary>`--approve-for-me` is the only policy under which a headless Codex may call an MCP
    /// tool; `--ignore-user-config` keeps the owner's config.toml out while auth still comes from
    /// CODEX_HOME (verified); `-c` values are literal strings when they are not TOML, so no quotes
    /// and no cmd.exe quoting hazards; `-` reads the prompt from stdin. <paramref name="effort"/> is
    /// passed as `-c model_reasoning_effort=&lt;value&gt;` (null appends nothing); measured on
    /// codex-cli 0.153.3, the override binds per spawn and `high` is a value the CLI/API accepts.</summary>
    public static ProcessSpec Codex(ResolvedCli cli, string model, string mcpUrl, string token, string workDir, string lastMessagePath, string prompt, string label, string? effort = null) =>
        new(cli.FileName,
            [.. cli.LeadingArguments,
             "exec", "--ephemeral", "--ignore-user-config",
             "-c", $"mcp_servers.{McpServerName}.url={mcpUrl}",
             "-c", $"mcp_servers.{McpServerName}.bearer_token_env_var={TokenEnvVar}",
             "-c", $"mcp_servers.{McpServerName}.startup_timeout_sec=20",
             "-c", $"mcp_servers.{McpServerName}.tool_timeout_sec=60",
             .. effort is null ? Array.Empty<string>() : new[] { "-c", $"model_reasoning_effort={effort}" },
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

    /// <summary>The built-in tools a spawn in a directory room gets, and the allow list that
    /// pre-approves them beside the three MCP tools. `dontAsk` denies anything not on the list.</summary>
    public const string ClaudeBuiltins = "Read,Edit,Write,Glob,Grep,Bash";
    public const string ClaudeDirectoryToolsAllowed = ClaudeBuiltins + "," + ClaudeToolAllowed;

    /// <summary>Profile folders that hold credentials: refused as room directories (RoomPathRules) and
    /// denied to Claude's file tools through the settings deny list. `~/` patterns are the one path
    /// form measured to bind on 2.1.220.</summary>
    public static readonly string[] CredentialFolders = [".claude", ".codex", ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker"];

    /// <summary>Every git subcommand that writes the index, the tree, refs, config or remotes.
    /// `Bash(git -*)` (below) covers any invocation that opens with an option (`-c`, `-C`, `--git-dir`,
    /// `--work-tree`), which is the form that reaches these verbs sideways. Read-only verbs (log, status,
    /// diff, show, blame, grep, ls-files, rev-parse) stay allowed: git is read-only for a spawn, not off.</summary>
    public static readonly string[] GitWriteVerbs =
    [
        "add", "am", "apply", "bisect", "branch", "checkout", "cherry-pick", "clean", "clone", "commit", "config",
        "fast-import", "fetch", "filter-branch", "gc", "init", "merge", "mv", "notes", "prune", "pull", "push",
        "rebase", "reflog", "remote", "repack", "replace", "reset", "restore", "revert", "rm", "stash",
        "submodule", "switch", "symbolic-ref", "tag", "update-index", "update-ref", "worktree", "write-tree",
    ];

    /// <summary>The second Claude allowlist: the ordinary six-builtin-plus-three-MCP list, plus
    /// `run_gate`, for an in-run Claude spawn's directory room only. The built-in tool set
    /// (`--tools`, <see cref="ClaudeBuiltins"/>) is untouched: MCP tools stay directly callable
    /// regardless of the built-in list, so widening only the allowlist is enough, and an out-of-run
    /// spawn never sees this constant at all.</summary>
    public const string ClaudeRunToolsAllowed = ClaudeDirectoryToolsAllowed + ",mcp__" + McpServerName + "__run_gate";

    /// <summary>The deny list of the per-spawn settings file: measured on 2.1.220 to block `git commit`,
    /// `git -c … commit` and `git.exe commit` while `echo`, `git log` and in-room writes ran, and to block
    /// a Write under a denied `~/` folder while a cwd Write succeeded. Deny rules apply in every
    /// permission mode and are prefix rules: an absolute-path `git.exe` is not caught (measured) and
    /// is left to the trail. There is deliberately no allow list here (the command line carries it) and
    /// no read fence (measured ineffective on this version; a rule in the prompt instead).
    ///
    /// <paramref name="dataDir"/>: when given, adds Read/Write/Edit deny rules for the hub's data
    /// directory, forward-slashed and `/**`-suffixed. The skill store lives under this directory and
    /// its text becomes instruction in a later spawn's prompt; the hash pin in the `skills` table is
    /// the actual control, and this is a second lock whose binding is unverified: every deny form
    /// measured on 2.1.220 used the `~/` shape, never an absolute path. Invoke-M11SkillCheck.ps1
    /// probes it live. Codex directory spawns get no deny list at all. Null (the default) omits these
    /// rules entirely.</summary>
    public static IReadOnlyList<string> ClaudeDenyRules(string? dataDir = null)
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
        if (dataDir is not null)
        {
            var forward = dataDir.Replace('\\', '/');
            foreach (var verb in new[] { "Read", "Write", "Edit" })
                rules.Add($"{verb}({forward}/**)");
        }
        return rules;
    }

    public static string ClaudeSettingsJson(string? dataDir = null) =>
        JsonSerializer.Serialize(new { permissions = new { deny = ClaudeDenyRules(dataDir) } });

    /// <summary>The environment variable the installed Claude CLI's own <c>--mcp-config</c> schema
    /// documents as its per-server tool-call timeout ("Per-server tool-call timeout in milliseconds...
    /// Hard wall-clock limit per call; progress notifications do not extend it. Values below 1000ms
    /// are ignored"). Measured: neither this nor the other two timeout knobs stops the runtime cutting
    /// a silent MCP call at 300 s; only bytes on the wire (run_gate's progress notifications) reset
    /// that cut.</summary>
    public const string ClaudeMcpToolTimeoutEnvVar = "MCP_TOOL_TIMEOUT";

    /// <summary>The env var the installed CLI documents for the HTTP MCP transport's idle-abort
    /// timeout, in milliseconds, separate from <see cref="ClaudeMcpToolTimeoutEnvVar"/>'s hard per-call
    /// wall clock; the CLI's own default is 5 minutes when this is unset. Set alongside the other two
    /// knobs (the env var above and the per-server <c>timeout</c> field in
    /// <see cref="ClaudeMcpConfigJson"/>) for an in-run Claude directory spawn only, all three at the
    /// same value.</summary>
    public const string ClaudeMcpIdleTimeoutEnvVar = "CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT";

    /// <summary>A spawn in a directory room: cwd is the room's tree; `dontAsk` plus the allow list
    /// runs the six built-ins and the three MCP tools without a prompt and auto-denies everything
    /// else (protected-path writes included; under `bypassPermissions` they would be auto-approved);
    /// the deny list rides in <paramref name="settingsPath"/>, which sits in the scratch folder
    /// beside <paramref name="mcpConfigPath"/>, never in the room; `stream-json` + `--verbose` is
    /// what carries the Bash calls the trail records; <paramref name="systemRules"/> (`SpawnPrompt.DirectoryRules`)
    /// rides as an appended system prompt so the fence is not only in the transcript channel.
    /// <paramref name="mcpToolTimeoutMs"/> sets both <see cref="ClaudeMcpToolTimeoutEnvVar"/> and
    /// <see cref="ClaudeMcpIdleTimeoutEnvVar"/> in the child's environment, to the same value, when
    /// given (an in-run Claude spawn only). Null (every non-run caller) sets nothing.</summary>
    public static ProcessSpec ClaudeInDirectory(ResolvedCli cli, string model, string mcpConfigPath, string settingsPath, string systemRules, string roomDir, string prompt, string label, string? effort = null, string allowedTools = ClaudeDirectoryToolsAllowed, int? mcpToolTimeoutMs = null) =>
        new(cli.FileName,
            [.. cli.LeadingArguments,
             "-p", "--permission-mode", "dontAsk", "--tools", ClaudeBuiltins, "--strict-mcp-config", "--mcp-config", mcpConfigPath,
             "--allowedTools", allowedTools, "--settings", settingsPath, "--append-system-prompt", systemRules, "--no-session-persistence", "--model", model,
             "--output-format", "stream-json", "--verbose", "--disable-slash-commands", "--setting-sources", "",
             .. effort is null ? Array.Empty<string>() : new[] { "--effort", effort }],
            mcpToolTimeoutMs is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>
                {
                    [ClaudeMcpToolTimeoutEnvVar] = mcpToolTimeoutMs.Value.ToString(),
                    [ClaudeMcpIdleTimeoutEnvVar] = mcpToolTimeoutMs.Value.ToString(),
                },
            roomDir, prompt, label);

    /// <summary>A Codex spawn in a directory room: `-C` is the room (a repository, so the repo check is
    /// not skipped), `--json` carries the command_execution items the trail records, and network is on
    /// inside workspace-write. Measured: every flag accepted, but the sandbox did not stop a
    /// `git commit`; the prompt rule and the trail are the mechanism.
    /// <paramref name="toolTimeoutSeconds"/>: a 60 s MCP tool-call timeout kills a long
    /// <c>run_gate</c> call well before a gate can finish, so an in-run Codex spawn passes
    /// <c>RunLimits.EffectiveGateTimeout</c> in seconds. Every other caller keeps the 60 s default.</summary>
    public static ProcessSpec CodexInDirectory(ResolvedCli cli, string model, string mcpUrl, string token, string roomDir, string lastMessagePath, string prompt, string label, string? effort = null, int toolTimeoutSeconds = 60) =>
        new(cli.FileName,
            [.. cli.LeadingArguments,
             "exec", "--ephemeral", "--ignore-user-config", "--json",
             "-c", $"mcp_servers.{McpServerName}.url={mcpUrl}",
             "-c", $"mcp_servers.{McpServerName}.bearer_token_env_var={TokenEnvVar}",
             "-c", $"mcp_servers.{McpServerName}.startup_timeout_sec=20",
             "-c", $"mcp_servers.{McpServerName}.tool_timeout_sec={toolTimeoutSeconds}",
             "-c", "sandbox_workspace_write.network_access=true",
             .. effort is null ? Array.Empty<string>() : new[] { "-c", $"model_reasoning_effort={effort}" },
             "--approve-for-me", "-C", roomDir, "-m", model,
             "--color", "never", "-o", lastMessagePath, "-"],
            new Dictionary<string, string> { [TokenEnvVar] = token },
            roomDir, prompt, label);
}
