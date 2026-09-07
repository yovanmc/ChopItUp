using System.Text.Json;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Hosting;

/// <summary>Emits ready-to-paste MCP client configurations carrying this hub's port and each
/// app-backed row's real token. Claude Desktop cannot dial a plain-http loopback remote connector,
/// so it goes through the mcp-remote stdio bridge; Codex reads the same config.toml from the
/// ChatGPT desktop app, the CLI and the IDE extension, and accepts an http://127.0.0.1 URL directly.
///
/// Everything lands under the gitignored data directory. These files hold live tokens, so they are
/// never written anywhere else, and <b>never</b> into <c>%APPDATA%\Claude\claude_desktop_config.json</c>
/// or <c>~/.codex/config.toml</c>: those are the owner's files and the owner pastes into them.</summary>
public static class HostConfigs
{
    public const string FolderName = "host-configs";
    public const string McpRemoteVersion = "0.8.3";

    public static string Write(string dataDir, int port, IReadOnlyDictionary<string, string> tokens, IReadOnlyList<Participant> roster)
    {
        var folder = Path.Combine(dataDir, FolderName);
        Directory.CreateDirectory(folder);
        var url = $"http://127.0.0.1:{port}/mcp";
        // One file per app-backed row: a model row with no model of its own is a window some
        // program opens on the room (Claude Desktop / Claude Code, the Codex app). Spawn rows
        // (model set) get no file; the hub itself is their client (M5). At most one app-backed
        // row per host, by construction of the seed; a second would overwrite the first here.
        foreach (var row in roster.Where(p => p.Kind == "model" && p.Model is null))
        {
            switch (row.Host)
            {
                case "claude": File.WriteAllText(Path.Combine(folder, "claude-desktop.json"), ClaudeDesktop(url, tokens[row.Id])); break;
                case "codex":  File.WriteAllText(Path.Combine(folder, "codex-config.toml"), Codex(url, tokens[row.Id])); break;
                default: throw new InvalidOperationException($"Participant '{row.Id}' has host '{row.Host}', which has no config template.");
            }
        }
        // The owner's remote hand (grill ledger D3) is the one human row that needs a client config:
        // it is a credential a Claude Code session on this machine is configured with. Claude CODE
        // dials loopback directly - that is SpawnCommands.ClaudeMcpConfigJson, the shape every
        // hub-spawned Claude has used since M5 and that the M5/M9/M10 live checks exercise. The
        // mcp-remote bridge above is Claude DESKTOP's workaround, needed only because Desktop's
        // remote connectors are dialled from Anthropic's cloud - using it here would add an npx
        // registry fetch to every session start for nothing.
        var proxy = roster.FirstOrDefault(p => p.Id == ChopDb.OwnerRemoteParticipantId);
        if (proxy is not null && tokens.TryGetValue(proxy.Id, out var proxyToken))
            File.WriteAllText(Path.Combine(folder, "claude-code-owner-remote.json"),
                SpawnCommands.ClaudeMcpConfigJson(url, proxyToken));
        File.WriteAllText(Path.Combine(folder, "README.md"), Readme(url, port, roster));
        return folder;
    }

    /// <summary>Owner-facing roster. Never a token: this file is the one in the folder that is safe
    /// to read aloud.</summary>
    private static string RosterTable(IReadOnlyList<Participant> roster)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Roster");
        sb.AppendLine();
        sb.AppendLine("Participants are rows in `chopitup.db` (table `participants`), read once at hub start. A token");
        sb.AppendLine("exists for every row in `tokens.json`; only app-backed rows get a config file here. Rows with a");
        sb.AppendLine("model are spawned by the hub itself once spawning ships, so they have no file to paste.");
        sb.AppendLine();
        sb.AppendLine("| Id | Host | Model | Classes | File | Note |");
        sb.AppendLine("|----|------|-------|---------|------|------|");
        foreach (var p in roster)
        {
            var file = p.Id == ChopDb.OwnerRemoteParticipantId ? "`claude-code-owner-remote.json`"
                : p.Kind == "human" ? "none (the web UI)"
                : p.Kind == "system" ? "none (the hub itself)"
                : p.Model is not null ? "no file (hub-spawned)"
                : p.Host switch { "claude" => "`claude-desktop.json`", "codex" => "`codex-config.toml`", _ => "no template for this host" };
            var classes = ParticipantClasses.Parse(p.Classes) is { Count: > 0 } set ? string.Join(", ", set) : "—";
            sb.AppendLine($"| `{p.Id}` | {p.Host} | {p.Model ?? "—"} | {classes} | {file} | {p.Note ?? ""} |");
        }
        sb.AppendLine();
        sb.AppendLine("To rotate any row's token: `ChopItUp.Hub --rotate-token <id>` with the hub stopped, then");
        sb.AppendLine("`--print-config` again. A row added by hand shows up in the web UI and in list_rooms at once,");
        sb.AppendLine("but gets its token and its line in the participation prompt at the next hub start.");
        return sb.ToString();
    }

    /// <summary>Claude Desktop cannot reach http://localhost as a remote connector, so it spawns
    /// mcp-remote as a local stdio server that proxies to the hub. The header value lives in env
    /// rather than inline: an arg containing a space is mangled on Windows (mcp-remote README).
    /// The command is <c>cmd /c npx</c>, not <c>npx</c>: Windows ships no <c>npx.exe</c> (only
    /// <c>npx</c>, <c>npx.cmd</c> and <c>npx.ps1</c>) and the host spawns a stdio server with a
    /// direct process create rather than through a shell, so a bare <c>npx</c> resolves to nothing
    /// and the bridge dies before mcp-remote loads - silently, with no /mcp traffic to show for
    /// it. Windows is this app's only target, so the shell form is the default.</summary>
    private static string ClaudeDesktop(string url, string token) => JsonSerializer.Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            ["chopitup"] = new
            {
                command = "cmd",
                args = new[]
                {
                    "/c", "npx",
                    "-y", $"mcp-remote@{McpRemoteVersion}", url,
                    "--allow-http", "--transport", "http-only",
                    "--header", "Authorization:${CHOPITUP_TOKEN}",
                },
                env = new Dictionary<string, string> { ["CHOPITUP_TOKEN"] = "Bearer " + token },
            },
        },
    }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

    // $$""" (two dollars) so a single brace is literal TOML/shell text and {{expr}} is the
    // interpolation. The emitted file must carry single braces: `{ Authorization = ... }` is TOML
    // inline-table syntax and `${CHOPITUP_TOKEN}` is mcp-remote's env-substitution form.
    private static string Codex(string url, string token) => $$"""
        # Paste into %USERPROFILE%\.codex\config.toml (the ChatGPT desktop Codex surface, the Codex
        # CLI and the IDE extension all read this one file). Restart Codex afterwards.
        [mcp_servers.chopitup]
        url = "{{url}}"
        http_headers = { Authorization = "Bearer {{token}}" }
        startup_timeout_sec = 20
        tool_timeout_sec = 60

        # Alternative if a literal token in this file is unwelcome: set the machine environment
        # variable CHOPITUP_CODEX_TOKEN to the value above (including the word Bearer is NOT needed
        # here) and replace the http_headers line with:
        #   bearer_token_env_var = "CHOPITUP_CODEX_TOKEN"

        # Fallback (grill note R2) if Codex refuses a plain-http URL: run the same mcp-remote bridge
        # Claude Desktop uses, as a stdio server, and delete the url/http_headers block above.
        # [mcp_servers.chopitup]
        # command = "cmd"
        # args = ["/c", "npx", "-y", "mcp-remote@{{McpRemoteVersion}}", "{{url}}", "--allow-http", "--transport", "http-only", "--header", "Authorization:${CHOPITUP_TOKEN}"]
        # cmd /c, not a bare npx: Windows has no npx.exe. See README.md in this folder.
        # [mcp_servers.chopitup.env]
        # CHOPITUP_TOKEN = "Bearer {{token}}"

        """;

    // No Claude Code artifact in M2, deliberately. It would have to reuse the 'claude' token, and
    // read_cursors is keyed (participant_id, room_id) with a stateless transport — two hosts on one
    // identity would share and race one cursor while the participation prompt promises a private
    // one (pass 2, MAJOR-2). Claude Code as a host is M5's job and needs its own participant row,
    // which is a schema change, not a config file.

    private static string Readme(string url, int port, IReadOnlyList<Participant> roster) => $"""
        # Host configs for Chop It Up

        Generated by `ChopItUp.Hub --print-config`. Every file here contains a live token: this
        folder lives under the gitignored data directory and must never be copied into the repo,
        a chat, or a screenshot.

        Hub endpoint: {url} (loopback only — nothing outside this machine can reach it).

        | File | Where it goes |
        |------|---------------|
        | `claude-desktop.json` | Merge the `mcpServers` entry into `%APPDATA%\Claude\claude_desktop_config.json`, then fully quit and reopen Claude Desktop. |
        | `codex-config.toml` | Append to `%USERPROFILE%\.codex\config.toml`, then restart Codex. |
        | `claude-code-owner-remote.json` | Merge the `mcpServers` entry into the MCP settings of the Claude Code session you drive the hub from — a `.mcp.json` in that session's directory, or the user-level MCP settings. Restart the session. |

        {RosterTable(roster)}

        Claude Desktop goes through the `mcp-remote` bridge because its remote connectors are dialled
        from Anthropic's cloud and cannot reach a loopback address; that needs Node on PATH (`npx`).
        The entry runs `cmd /c npx`, not `npx`, and that is load-bearing: Windows ships no
        `npx.exe` - only `npx`, `npx.cmd` and `npx.ps1` - and the host spawns a stdio server with a
        direct process create rather than through a shell, so a bare `"command": "npx"` finds
        nothing to execute and the bridge dies before mcp-remote loads. It fails silently: the
        server simply never appears and the hub logs no `/mcp` traffic at all. `npx` also resolves
        against the npm registry on every launch, so if you would rather this app not depend on the
        network to start: `npm i -g mcp-remote@{McpRemoteVersion}` once, then replace `"npx", "-y",
        "mcp-remote@{McpRemoteVersion}"` with just `"mcp-remote"`. Keep the `cmd /c` in front - the
        global install is a `.cmd` shim with the same missing `.exe`.

        Claude Code still joins as `claude` when the owner wants one Claude identity across both
        hosts (the 2026-09-04 ruling, unchanged): paste the `claude-desktop.json` entry above into
        that session's MCP settings instead of Claude Desktop's config file. The cost is a shared
        read cursor, so whichever host calls `read_messages` without an `after_id` first consumes
        the other's unread. Pass an explicit `after_id` to read without moving it. `owner-remote`
        below is a different thing entirely: a separate, human-kind credential for driving the hub
        from a session on another machine, not for participating in it the way `claude` does.

        ## The remote hand

        `owner-remote` is a second row of kind `human`. Posts made with its token start and steer
        exchanges exactly as `owner`'s do; the hub stamps the author, so the transcript and the
        commit trail show which hand typed. It exists so the owner can drive the hub from a session
        on another device. Revoking it is `--rotate-token owner-remote` with the hub stopped, then a
        restart: the old token dies and nothing re-mints it into any session you have not re-pasted.

        The entry dials the hub directly over `http://127.0.0.1` with an `Authorization` header,
        which is the same connection every hub-spawned Claude has used since M5. It does **not** go
        through the `mcp-remote` bridge — that exists only because Claude Desktop's remote connectors
        are dialled from Anthropic's cloud and cannot reach loopback, which is not Claude Code's
        problem. That bridge is an alternative connection form for this identity too, in principle,
        but it is untested here: only the direct connection above has ever been exercised against
        `owner-remote`.

        It is a second identity, not a second person: it reads with its own cursor, so messages you
        post from the phone still count as unread in the web UI until you open the room, and
        messages you read there are still unread for the phone. That is the same trade the `claude`
        row makes across Desktop and Code, inverted.

        What "shows which hand typed" does and does not cover: the message's stored author is
        `owner-remote`, and the room shows it under its own name and badge. The **git trail** still
        records file commits as `Owner` — those are edits made on this machine before a spawn ran,
        not something the phone did, so attributing them to the remote row would be a worse lie than
        the one it fixes.

        ## Roster classes

        `classes` is a set drawn from `plumbing`, `visible` and `judge`, stored comma-separated. A
        row can hold more than one — `opus` ships as `visible,judge`, because it is both the model
        you want on anything you will look at and one of the two you want judging. The hub reads the
        set and validates it but does not yet act on it; that is the runs milestone. Set one by hand
        with the hub stopped: `UPDATE participants SET classes='visible,judge' WHERE id='gpt-6-astra';`
        - it takes effect at the next start, and anything outside the vocabulary is dropped with a
        warning in the hub's log at startup.

        This folder is only as private as the directory it sits in — two live bearer tokens with
        no expiry. If other accounts or unattended processes can read this machine's files, they can
        read these.

        Tokens: `ChopItUp.Hub --rotate-token <id>` mints a new one for any roster id and invalidates
        the old at the next hub start. It does not print the token — re-run `--print-config` and
        re-paste that host's file.

        Port {port} is the configured port; if you start the hub with `--port`, regenerate this
        folder so the URLs match.

        ## Restoring a backup

        Every schema migration writes a verified snapshot beside the database first, named
        `chopitup.db.v<old version>.<timestamp>.bak`. To go back to one:

        1. Stop the hub. Confirm no ChopItUp process is running — a live WAL is what makes this
           dangerous.
        2. Delete `chopitup.db`, `chopitup.db-wal` and `chopitup.db-shm`. **All three.** Leaving a
           stale `-wal` beside a restored database lets SQLite replay post-migration writes onto it.
        3. Copy the `.bak` to `chopitup.db` (copy, do not move — keep the snapshot).
        4. Start the hub. It will migrate the restored database forward again, taking a fresh
           snapshot as it goes.

        """;
}
