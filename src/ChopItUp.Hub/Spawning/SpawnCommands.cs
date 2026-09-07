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
    /// used — it switches auth to API key only, and this app holds no key. <paramref name="effort"/>
    /// is row 19's AC7/D10: null outside a run and for an ordinary in-run row (nothing appended); a
    /// conductor or a `judge`-class row gets exactly `--effort high` — never `xhigh` or `max`.</summary>
    public static ProcessSpec Claude(ResolvedCli cli, string model, string mcpConfigPath, string workDir, string prompt, string label, string? effort = null) =>
        new(cli.FileName,
            [.. cli.LeadingArguments,
             "-p", "--tools", "", "--strict-mcp-config", "--mcp-config", mcpConfigPath,
             "--allowedTools", ClaudeToolAllowed, "--no-session-persistence", "--model", model,
             "--output-format", "json", "--disable-slash-commands", "--setting-sources", "",
             .. effort is null ? Array.Empty<string>() : new[] { "--effort", effort }],
            new Dictionary<string, string>(),
            workDir, prompt, label);

    // WriteIndented + a trailing newline (critique pass 2, m-12): this JSON also lands in
    // host-configs\claude-code-owner-remote.json, which the owner hand-merges like every other file
    // in that folder, and those are all indented with a trailing newline. The per-spawn mcp.json
    // this also produces (SpawnerService) is read by the Claude CLI, which does not care either way.
    public static string ClaudeMcpConfigJson(string mcpUrl, string token) =>
        JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                [McpServerName] = new { type = "http", url = mcpUrl, headers = new { Authorization = "Bearer " + token } },
            },
        }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

    /// <summary>`--approve-for-me` is the only policy under which a headless Codex may call an MCP
    /// tool (LESSONS, M5 approvals); `--ignore-user-config` keeps the owner's config.toml out while
    /// auth still comes from CODEX_HOME (verified); `-c` values are literal strings when they are
    /// not TOML, so no quotes and no cmd.exe quoting hazards; `-` reads the prompt from stdin.
    /// <paramref name="effort"/> is row 19's AC7/D10, as `-c model_reasoning_effort=<value>` — null
    /// appends nothing.</summary>
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

    /// <summary>Row 19, task 12e: the second Claude allowlist — the ordinary six-builtin-plus-three-MCP
    /// list, plus `run_gate`, for an in-run Claude spawn's directory room only. The built-in TOOL set
    /// (`--tools`, <see cref="ClaudeBuiltins"/>) is untouched (LESSONS, M5 claude-code-headless-tool-surface):
    /// MCP tools stay directly callable regardless of the built-in list, so widening only the allowlist
    /// is enough, and an out-of-run or non-conductor spawn never sees this constant at all.</summary>
    public const string ClaudeRunToolsAllowed = ClaudeDirectoryToolsAllowed + ",mcp__" + McpServerName + "__run_gate";

    /// <summary>The deny list of the per-spawn settings file: measured on 2.1.220 to block `git commit`,
    /// `git -c … commit` and `git.exe commit` while `echo`, `git log` and in-room writes ran, and to block
    /// a Write under a denied `~/` folder while a cwd Write succeeded (claims 23, 35). Deny rules apply in
    /// every permission mode and are prefix rules: an absolute-path `git.exe` is NOT caught (measured) and
    /// is left to the trail. There is deliberately no allow list here (the command line carries it) and
    /// no read fence (measured ineffective on this version — a rule in the prompt instead, decision 8).
    ///
    /// <paramref name="dataDir"/> (row 11, D-i measure (a)): when given, adds Read/Write/Edit deny rules
    /// for the hub's data directory, forward-slashed and `/**`-suffixed. The skill store lives under
    /// this directory and its text becomes instruction in a later spawn's prompt (D-i); the hash pin in
    /// the `skills` table is the actual control, and this is a second lock whose binding is UNVERIFIED -
    /// every deny form ever measured on 2.1.220 used the `~/` shape (claim 23), never an absolute path.
    /// The M11 check probes it live. The Codex asymmetry is NOT closed here: Codex directory spawns get
    /// no deny list at all, and row 13 ("symmetric confinement") owns both. Null (the default) omits
    /// these rules entirely, so a caller with no data directory in scope gets the pre-row-11 list.</summary>
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

    /// <summary>Row 19, orchestrator addition to task 12f: the environment variable the installed
    /// Claude CLI's own <c>--mcp-config</c> schema documents as its per-server tool-call timeout
    /// ("Per-server tool-call timeout in milliseconds... Hard wall-clock limit per call; progress
    /// notifications do not extend it. Values below 1000ms are ignored"). Task 12f measured that this
    /// is read from the environment, not that raising it actually extends a live call that would
    /// otherwise time out — the CLI's default value could not be extracted either. Inferred from the
    /// binary's own embedded schema text this session, not from a round-trip that timed out and was
    /// then rescued by this variable; treat it as unverified until such a round-trip is observed.</summary>
    public const string ClaudeMcpToolTimeoutEnvVar = "MCP_TOOL_TIMEOUT";

    /// <summary>A spawn in a directory room (M9 decision 9): cwd is the room's tree; `dontAsk` plus the
    /// allow list runs the six built-ins and the three MCP tools without a prompt and auto-denies
    /// everything else (protected-path writes included — under `bypassPermissions` they would be
    /// auto-approved); the deny list rides in <paramref name="settingsPath"/>, which sits in the scratch
    /// folder beside <paramref name="mcpConfigPath"/>, never in the room; `stream-json` + `--verbose` is
    /// what carries the Bash calls the trail records; <paramref name="systemRules"/> (`SpawnPrompt.DirectoryRules`)
    /// rides as an appended system prompt (F10) so the fence is not only in the transcript channel.
    /// <paramref name="mcpToolTimeoutMs"/> (orchestrator addition to task 12f) sets
    /// <see cref="ClaudeMcpToolTimeoutEnvVar"/> in the child's environment when given — an in-run
    /// Claude spawn only, at <c>RunLimits.SpawnTimeout</c> in milliseconds, so the CLI's own hard MCP
    /// tool-call wall clock cannot kill a long <c>run_gate</c> call well before the hub's own 30-minute
    /// per-spawn timeout does. Null (the default, every non-run caller) sets nothing, exactly the
    /// pre-existing empty environment.</summary>
    public static ProcessSpec ClaudeInDirectory(ResolvedCli cli, string model, string mcpConfigPath, string settingsPath, string systemRules, string roomDir, string prompt, string label, string? effort = null, string allowedTools = ClaudeDirectoryToolsAllowed, int? mcpToolTimeoutMs = null) =>
        new(cli.FileName,
            [.. cli.LeadingArguments,
             "-p", "--permission-mode", "dontAsk", "--tools", ClaudeBuiltins, "--strict-mcp-config", "--mcp-config", mcpConfigPath,
             "--allowedTools", allowedTools, "--settings", settingsPath, "--append-system-prompt", systemRules, "--no-session-persistence", "--model", model,
             "--output-format", "stream-json", "--verbose", "--disable-slash-commands", "--setting-sources", "",
             .. effort is null ? Array.Empty<string>() : new[] { "--effort", effort }],
            mcpToolTimeoutMs is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { [ClaudeMcpToolTimeoutEnvVar] = mcpToolTimeoutMs.Value.ToString() },
            roomDir, prompt, label);

    /// <summary>A Codex spawn in a directory room: `-C` is the room (a repository, so the repo check is
    /// not skipped), `--json` carries the command_execution items the trail records, and network is on
    /// inside workspace-write (D10). Measured 2026-09-06 (claim 24): every flag accepted; note the
    /// sandbox did NOT stop a `git commit` — the prompt rule and the trail are the mechanism (decision 7).
    /// <paramref name="toolTimeoutSeconds"/> is row 19's task 12f (pass 1's M7): the hard-coded 60 s
    /// this used to always carry kills an MCP tool call — <c>run_gate</c> included — well before a
    /// 30-minute gate can finish; an in-run Codex spawn passes <c>RunLimits.SpawnTimeout</c> in
    /// seconds here instead. Every other caller keeps the 60 s default.</summary>
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
