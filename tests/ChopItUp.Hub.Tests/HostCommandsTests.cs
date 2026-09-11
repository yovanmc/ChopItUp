using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ChopItUp.Core.Memory;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Hosting;
using ChopItUp.Hub.Security;
using ChopItUp.Hub.Skills;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Hub.Tests;

/// <summary>The non-serving CLI verbs: A6/A6b (--rotate-token) and A7 (--print-config), including
/// the no-lock contract --print-config keeps while a hub is running.</summary>
public sealed class HostCommandsTests : IDisposable
{
    private static readonly string[] Roster = ChopDb.SeedRoster.Select(p => p.Id).ToArray();
    // Row 28: TokenStore.Load/ReadExisting/MintFor classify by Participant, not by bare id - kept
    // separate from Roster (still used everywhere a plain id string is what's under test) rather than
    // retyping every existing string-based assertion in this file.
    private static readonly IReadOnlyList<Participant> Participants = ChopDb.SeedRoster;
    private readonly List<string> _dirs = new();

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_cmd_" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var dir in _dirs)
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>What "start the hub once" leaves behind: a v3 database and a full tokens.json.</summary>
    private static void StartedOnce(string dir)
    {
        new ChopDb(Path.Combine(dir, "chopitup.db")).EnsureDatabase();
        TokenStore.Load(dir, Participants);
    }

    /// <summary>v1 shape plus exactly what ApplyV2 adds (M8 Task 1's fixture, duplicated here with a
    /// parameterised path: a Hub test cannot reach Core's private test helper, and the fixture must
    /// stay raw SQL in both places — LESSONS M2).</summary>
    private static void WriteRawV2(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE participants (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, kind TEXT NOT NULL);
            CREATE TABLE rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
                author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL,
                client_key TEXT);
            CREATE INDEX ix_messages_room_id ON messages(room_id, id);
            CREATE UNIQUE INDEX ux_messages_client_key ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;
            CREATE TABLE read_cursors (participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id TEXT NOT NULL REFERENCES rooms(id), last_read_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id));
            INSERT INTO participants (id, display_name, kind) VALUES ('owner','Owner','human'),('claude','Claude','model'),('codex','Codex','model');
            INSERT INTO rooms (id, name, created_at) VALUES ('general','General','2026-09-01T10:00:00.000+00:00');
            INSERT INTO messages (id, room_id, author_id, body, created_at, client_key) VALUES
                (1,'general','owner','first v2 message','2026-09-01T10:01:00.000+00:00',NULL),
                (2,'general','codex','second v2 message','2026-09-01T10:02:00.000+00:00','k-1');
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ('claude','general',2);
            PRAGMA user_version = 2;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void A6_rotate_replaces_one_token_and_leaves_the_others_alone()
    {
        var dir = NewDir();
        StartedOnce(dir);
        var before = TokenStore.ReadExisting(dir, Participants);

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.RotateToken, "claude"), output, error);

        Assert.Equal(0, exit);
        var after = TokenStore.ReadExisting(dir, Participants);
        Assert.NotEqual(before["claude"], after["claude"]);
        Assert.Equal(before["owner"], after["owner"]);
        Assert.Equal(before["codex"], after["codex"]);

        // These are hashes (row 28), never the plaintext rotate now prints - so this stays a
        // meaningful check that the hash never leaks, distinct from the new plaintext assertion below.
        var stdout = output.ToString();
        foreach (var t in before.Values) Assert.DoesNotContain(t, stdout);
        foreach (var t in after.Values) Assert.DoesNotContain(t, stdout);
    }

    /// <summary>D-28-d: this reverses critique pass 1's "never print" ruling. --print-config no
    /// longer embeds a live value (it writes a {{TOKEN}} placeholder - see the print-config tests
    /// below), so a rotated token has no other way to reach the operator. The bounding clause
    /// (rotate is owner-typed only, never agent-run) lives in docs/verification.md, not in test
    /// assertions - this test only proves the mechanism: printed once, resolves to the right
    /// participant, and lands in no file.</summary>
    [Fact]
    public void A6_rotate_prints_the_new_token_once_and_writes_it_to_no_file()
    {
        var dir = NewDir();
        StartedOnce(dir);

        var output = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.RotateToken, "claude"), output, new StringWriter());

        Assert.Equal(0, exit);
        var stdout = output.ToString();
        var printed = TokenScan.Candidates(stdout).Distinct().ToList();
        Assert.Single(printed);

        var store = TokenStore.Load(dir, Participants);
        Assert.True(store.TryResolve(printed[0], out var resolvedId));
        Assert.Equal("claude", resolvedId);

        Assert.Contains("will not be shown again", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(printed[0], File.ReadAllText(Path.Combine(dir, TokenStore.FileName)));
    }

    [Fact]
    public void A6_rotate_with_an_unknown_participant_changes_nothing_and_exits_nonzero()
    {
        var dir = NewDir();
        StartedOnce(dir);
        var before = File.ReadAllBytes(Path.Combine(dir, TokenStore.FileName));

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.RotateToken, "mallory"), new StringWriter(), error);

        Assert.Equal(2, exit);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(dir, TokenStore.FileName)));
        var message = error.ToString();
        foreach (var p in Roster) Assert.Contains(p, message);
    }

    [Fact]
    public void A6b_rotate_against_a_directory_with_no_tokens_file_creates_nothing()
    {
        var dir = NewDir();
        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.RotateToken, "claude"), new StringWriter(), error);

        Assert.Equal(4, exit);
        Assert.Contains(TokenStore.FileName, error.ToString());
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task A6b_rotate_is_refused_while_a_hub_owns_the_data_dir()
    {
        var dir = NewDir();
        await using var host = await HubTestHost.StartAsync(dir, deleteOnDispose: false);
        var before = File.ReadAllBytes(Path.Combine(dir, TokenStore.FileName));

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.RotateToken, "claude"), new StringWriter(), error);

        Assert.Equal(5, exit);
        Assert.Contains(dir, error.ToString());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(dir, TokenStore.FileName)));
    }

    [Fact]
    public async Task A6_a_rotated_token_is_dead_at_the_next_hub_start()
    {
        var dir = NewDir();
        string old, ownerToken;
        await using (var host1 = await HubTestHost.StartAsync(dir, deleteOnDispose: false))
        {
            old = host1.TokenFor("claude");
            ownerToken = host1.TokenFor("owner");
        }

        // Row 28, D-28-d: --rotate-token does not print the new value yet (that reversal is a later
        // task), so the plaintext this test needs to present has to come from the same mint entry
        // point the CLI verb itself calls - MintFor - rather than from stdout. A6_rotate_replaces_one_
        // token_and_leaves_the_others_alone already covers the CLI verb's own exit code and isolation.
        var newToken = TokenStore.Load(dir, Participants).MintFor("claude");
        Assert.NotEqual(old, newToken);

        await using var host2 = await HubTestHost.StartAsync(dir, deleteOnDispose: true);

        async Task<HttpStatusCode> Try(string token)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", new MediaTypeHeaderValue("application/json")) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res = await host2.Client.SendAsync(req);
            return res.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await Try(old));
        Assert.NotEqual(HttpStatusCode.Unauthorized, await Try(newToken));
        Assert.NotEqual(HttpStatusCode.Unauthorized, await Try(ownerToken));
    }

    [Fact]
    public async Task A7_print_config_still_works_while_a_hub_is_running()
    {
        var dir = NewDir();
        await using var host = await HubTestHost.StartAsync(dir, deleteOnDispose: false);

        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.PrintConfig), new StringWriter(), new StringWriter());

        Assert.Equal(0, exit);
    }

    // The folder and file names below are written as literals rather than through HostConfigs'
    // constants on purpose: they are the on-disk contract the owner pastes from, so a rename must
    // break a test rather than silently follow the code.
    private static string ConfigFolder(string dataDir) => Path.Combine(dataDir, "host-configs");

    private static string[] Snapshot(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void A7_print_config_writes_all_four_files_with_the_live_port_and_a_token_placeholder()
    {
        var dir = NewDir();
        StartedOnce(dir);
        var tokens = TokenStore.ReadExisting(dir, Participants);   // hashes only (row 28); never a credential

        var exit = HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), new StringWriter());
        Assert.Equal(0, exit);

        var folder = ConfigFolder(dir);
        // Four files: no separate Claude DESKTOP artifact for Claude Code — it still joins as
        // 'claude' by pasting the Claude Desktop entry (owner ruling 2026-09-04, one Claude identity,
        // a shared read cursor accepted). But row 11 adds a fourth: 'owner-remote' is a second,
        // distinct human-kind row, and it gets its own config (task 6b). Spawn rows (M8) still add
        // no files: the hub is their client.
        Assert.Equal(
            new[] { "README.md", "claude-code-owner-remote.json", "claude-desktop.json", "codex-config.toml" },
            Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        const string url = "http://127.0.0.1:9123/mcp";

        using var desktop = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "claude-desktop.json")));
        var server = desktop.RootElement.GetProperty("mcpServers").GetProperty("chopitup");
        // Windows ships no npx.exe - only npx, npx.cmd and npx.ps1 - and Claude Desktop spawns a
        // stdio server with a direct process create rather than through a shell, so "command":
        // "npx" resolves to nothing and the bridge dies before mcp-remote loads. Observed on a
        // stock Node install 2026-09-04: zero /mcp traffic until the entry was rewritten to this
        // form, then the bridge came up on the next launch. Windows is the only platform this app
        // targets, so the shell form is the default, not a documented fallback.
        Assert.Equal("cmd", server.GetProperty("command").GetString());
        // Row 28 ticket 3: generation never embeds a real value - a {{TOKEN}} placeholder stands in
        // for it, and --rotate-token <id> is how the operator gets a real one to paste over it.
        Assert.Equal("Bearer " + HostConfigs.TokenPlaceholder, server.GetProperty("env").GetProperty("CHOPITUP_TOKEN").GetString());
        var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert.Equal("/c", args[0]);
        Assert.Equal("npx", args[1]);
        Assert.Contains("--allow-http", args);
        Assert.Contains(url, args);
        Assert.Contains("Authorization:${CHOPITUP_TOKEN}", args);   // header value via env: a space in an arg is mangled on Windows

        var codex = File.ReadAllText(Path.Combine(folder, "codex-config.toml"));
        Assert.Contains("[mcp_servers.chopitup]", codex);
        Assert.Contains($"url = \"{url}\"", codex);
        // Single braces, not the doubled ones the interpolated raw string is written with.
        Assert.Contains($"http_headers = {{ Authorization = \"Bearer {HostConfigs.TokenPlaceholder}\" }}", codex);
        Assert.Contains("bearer_token_env_var = \"CHOPITUP_CODEX_TOKEN\"", codex);
        // The commented bridge fallback carries the same cmd /c shape as the Claude Desktop entry,
        // for the same reason: there is no npx.exe to spawn directly on Windows.
        Assert.Contains("# command = \"cmd\"", codex);
        Assert.Contains("# args = [\"/c\", \"npx\", \"-y\", \"mcp-remote@", codex);
        Assert.Contains("\"Authorization:${CHOPITUP_TOKEN}\"", codex);

        // claude-code-owner-remote.json (task 6b): the direct type:"http" + Authorization: Bearer
        // shape every hub-spawned Claude has used since M5 — NOT the mcp-remote bridge above, which
        // is Claude Desktop's workaround for a problem Claude Code does not have.
        using var proxyDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "claude-code-owner-remote.json")));
        var proxyServer = proxyDoc.RootElement.GetProperty("mcpServers").GetProperty("chopitup");
        Assert.Equal("http", proxyServer.GetProperty("type").GetString());
        Assert.Equal(url, proxyServer.GetProperty("url").GetString());
        Assert.Equal("Bearer " + HostConfigs.TokenPlaceholder, proxyServer.GetProperty("headers").GetProperty("Authorization").GetString());
        var proxyText = File.ReadAllText(Path.Combine(folder, "claude-code-owner-remote.json"));
        Assert.EndsWith(Environment.NewLine, proxyText);   // indented + trailing newline, like its neighbours (m-12)

        // No generated file ever carries a real hash or a real token - the only credential-shaped
        // text anywhere in the folder is the placeholder itself (AC6, extended to generation).
        foreach (var file in Directory.GetFiles(folder))
        {
            var text = File.ReadAllText(file);
            foreach (var t in tokens.Values) Assert.DoesNotContain(t, text);
        }

        var readme = File.ReadAllText(Path.Combine(folder, "README.md"));
        Assert.Contains(url, readme);
        Assert.Contains("Port 9123", readme);
        Assert.Contains(@"%APPDATA%\Claude\claude_desktop_config.json", readme);
        Assert.Contains(@"%USERPROFILE%\.codex\config.toml", readme);
        Assert.Contains("npm i -g mcp-remote@", readme);
        // The README must say WHY the command is cmd /c, or the next person "simplifies" it
        // back to a bare npx and the bridge dies silently again.
        Assert.Contains("cmd /c", readme);
        Assert.Contains("npx.exe", readme);
        // "Claude Code gets no file of its own" became false at 6b (m-12) — the README must now say
        // what is true: Claude Code still joins as 'claude' when the owner wants that, and
        // owner-remote is a separate credential.
        Assert.Contains("Claude Code still joins as `claude`", readme);
        Assert.DoesNotContain("gets no file of its own", readme);
        Assert.Contains("claude-code-owner-remote.json", readme);
        Assert.Contains("## The remote hand", readme);
        Assert.Contains("--rotate-token owner-remote", readme);
        // Ticket 06: the readme must state which alternative connection form is untested (the
        // mcp-remote bridge, for this identity).
        Assert.Contains("untested", readme);
        // Row 29: the readme must say that an owner-class credential presented from inside a spawn
        // is refused, and must name the switch that turns that off - a refusal the owner meets with
        // no explanation anywhere is the failure mode this assertion exists to prevent.
        Assert.Contains("inside a spawn", readme);
        Assert.Contains("--owner-peer-check off", readme);
        Assert.Contains("## Roster classes", readme);
        Assert.Contains("| Classes |", readme);
        Assert.Contains("only as private as", readme);
        Assert.Contains("## Restoring a backup", readme);
        // A restore that leaves the WAL behind replays the very writes it was meant to undo.
        Assert.Contains("chopitup.db-wal", readme);
        Assert.Contains("chopitup.db-shm", readme);
        foreach (var t in tokens.Values) Assert.DoesNotContain(t, readme);
        // Row 28 ticket 3: generation must name how to get a real value.
        Assert.Contains(HostConfigs.TokenPlaceholder, readme);
        Assert.Contains("--rotate-token", readme);
    }

    [Fact]
    public void A7_print_config_prefers_the_port_the_hub_actually_bound()
    {
        var dir = NewDir();
        StartedOnce(dir);
        File.WriteAllText(Path.Combine(dir, "hub.port"), "9000");

        var output = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 8790, HubCommand.PrintConfig), output, new StringWriter());
        Assert.Equal(0, exit);

        foreach (var file in Directory.GetFiles(ConfigFolder(dir)))
        {
            var text = File.ReadAllText(file);
            Assert.Contains("http://127.0.0.1:9000/mcp", text);
            Assert.DoesNotContain("8790", text);
        }

        var stdout = output.ToString();
        Assert.Contains("9000", stdout);
        Assert.Contains("8790", stdout);
    }

    [Fact]
    public void A7_print_config_prints_the_folder_but_never_a_token()
    {
        var dir = NewDir();
        StartedOnce(dir);
        var tokens = TokenStore.ReadExisting(dir, Participants);

        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), output, error));

        var printed = output.ToString() + error.ToString();
        Assert.Contains(ConfigFolder(dir), printed);
        foreach (var t in tokens.Values) Assert.DoesNotContain(t, printed);
        // Row 28 ticket 3: the command must name the placeholder and how to fill it in.
        Assert.Contains(HostConfigs.TokenPlaceholder, printed);
        Assert.Contains("--rotate-token", printed);
    }

    [Fact]
    public void A7_print_config_is_rerunnable()
    {
        var dir = NewDir();
        StartedOnce(dir);

        Assert.Equal(0, HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), new StringWriter()));
        var first = Directory.GetFiles(ConfigFolder(dir)).ToDictionary(f => f, File.ReadAllBytes, StringComparer.Ordinal);

        Assert.Equal(0, HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), new StringWriter()));
        var second = Directory.GetFiles(ConfigFolder(dir)).ToDictionary(f => f, File.ReadAllBytes, StringComparer.Ordinal);

        Assert.Equal(first.Keys.OrderBy(k => k, StringComparer.Ordinal), second.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (path, bytes) in first) Assert.Equal(bytes, second[path]);   // regenerated, never appended to
    }

    [Fact]
    public void A7_print_config_against_a_directory_with_no_tokens_file_creates_nothing()
    {
        var dir = NewDir();
        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), error);

        Assert.Equal(4, exit);
        Assert.Contains(TokenStore.FileName, error.ToString());
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void A7_print_config_does_not_mint_a_missing_token()
    {
        var dir = NewDir();
        StartedOnce(dir);
        var path = Path.Combine(dir, TokenStore.FileName);
        // Row 28: entries are now { "sha256": "..." } objects, not raw strings.
        var tokens = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))!;
        tokens.Remove("codex");
        File.WriteAllText(path, JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true }));
        var before = File.ReadAllBytes(path);

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), error);

        Assert.NotEqual(0, exit);
        Assert.Contains("codex", error.ToString());
        Assert.Equal(before, File.ReadAllBytes(path));      // reading must never rotate a credential
        Assert.False(Directory.Exists(ConfigFolder(dir)));
    }

    [Fact]
    public void A7_print_config_writes_nothing_outside_the_data_directory()
    {
        var parent = NewDir();
        var dir = Path.Combine(parent, "data");
        StartedOnce(dir);
        var before = Snapshot(parent);

        Assert.Equal(0, HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), new StringWriter()));

        var appeared = Snapshot(parent).Except(before, StringComparer.Ordinal).ToArray();
        var inside = Path.Combine("data", "host-configs");
        Assert.All(appeared, p => Assert.StartsWith(inside, p, StringComparison.Ordinal));
        Assert.Contains(Path.Combine(inside, "README.md"), appeared);
        Assert.Contains(Path.Combine(inside, "claude-desktop.json"), appeared);
        Assert.Contains(Path.Combine(inside, "codex-config.toml"), appeared);
        Assert.Contains(Path.Combine(inside, "claude-code-owner-remote.json"), appeared);
    }

    [Fact]
    public void M8_A8_print_config_against_a_v2_database_writes_nothing_and_says_start_the_hub()
    {
        var dir = NewDir();
        TokenStore.Load(dir, Participants.Where(p => p.Id is "owner" or "claude" or "codex").ToArray());
        WriteRawV2(Path.Combine(dir, "chopitup.db"));   // what the previous build left behind
        var names = Snapshot(dir);
        var dbBytes = File.ReadAllBytes(Path.Combine(dir, "chopitup.db"));   // names alone cannot see a header rewrite

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), error);

        Assert.Equal(4, exit);
        Assert.Contains("start the hub once", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(names, Snapshot(dir));
        Assert.Equal(dbBytes, File.ReadAllBytes(Path.Combine(dir, "chopitup.db")));
    }

    [Fact]
    public void M8_A8_print_config_against_a_newer_database_exits_4_without_templating_it()
    {
        var dir = NewDir();
        StartedOnce(dir);
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "chopitup.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), error);

        Assert.Equal(4, exit);
        Assert.Contains("newer build", error.ToString());
        Assert.False(Directory.Exists(ConfigFolder(dir)));
    }

    [Fact]
    public void M8_A8_rotate_against_a_missing_database_writes_nothing_and_exits_4()
    {
        var dir = NewDir();
        TokenStore.Load(dir, Participants);
        var before = File.ReadAllBytes(Path.Combine(dir, TokenStore.FileName));

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.RotateToken, "opus"), new StringWriter(), error);

        Assert.Equal(4, exit);
        Assert.Contains("start the hub once", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(dir, TokenStore.FileName)));
    }

    [Fact]
    public void M8_A3_A5_print_config_uses_the_app_backed_rows_and_lists_the_whole_roster()
    {
        var dir = NewDir();
        StartedOnce(dir);
        var tokens = TokenStore.ReadExisting(dir, Participants);

        Assert.Equal(0, HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), new StringWriter()));

        var folder = ConfigFolder(dir);
        var claude = File.ReadAllText(Path.Combine(folder, "claude-desktop.json"));
        var codex = File.ReadAllText(Path.Combine(folder, "codex-config.toml"));
        var readme = File.ReadAllText(Path.Combine(folder, "README.md"));
        // Row 28 ticket 3: every app-backed row's file carries the placeholder, never its real hash.
        Assert.Contains(HostConfigs.TokenPlaceholder, claude);
        Assert.Contains(HostConfigs.TokenPlaceholder, codex);
        foreach (var p in ChopDb.SeedRoster)
        {
            Assert.Contains($"`{p.Id}`", readme);
            // Only a host-file row has an entry to check for at all (row 28); a spawnable/system row
            // never appears in `tokens`, so there is nothing here to leak into the README.
            if (tokens.TryGetValue(p.Id, out var t)) Assert.DoesNotContain(t, readme);
        }
        Assert.Contains("usage credits", readme);
        Assert.Contains("no file", readme);

        // Rotating one host-file row's token changes only that key. Rotating a hub-launched model's
        // token (e.g. "gpt-5.5") no longer applies post-row-28: it is ephemeral and has no entry in
        // the file to rotate at all - MintFor_replaces_one_host_file_token_and_refuses_a_spawnable_or_
        // system_id (TokenStoreTests.cs) covers that refusal directly.
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.RotateToken, "owner-remote"), new StringWriter(), new StringWriter());
        Assert.Equal(0, exit);
        var after = TokenStore.ReadExisting(dir, Participants);
        Assert.NotEqual(tokens["owner-remote"], after["owner-remote"]);
        foreach (var id in tokens.Keys.Where(id => id != "owner-remote")) Assert.Equal(tokens[id], after[id]);
    }

    [Fact]
    public void Options_parse_recognises_the_command_verbs()
    {
        var rotate = HubOptions.Parse(["--rotate-token", "codex"], _ => null);
        Assert.Equal(HubCommand.RotateToken, rotate.Command);
        Assert.Equal("codex", rotate.RotateParticipant);

        var print = HubOptions.Parse(["--print-config"], _ => null);
        Assert.Equal(HubCommand.PrintConfig, print.Command);

        var bare = HubOptions.Parse([], _ => null);
        Assert.Equal(HubCommand.Serve, bare.Command);

        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--rotate-token"], _ => null));
    }

    // --- Row 11 task 5: --import-skill -----------------------------------------------------

    [Fact]
    public void Options_parse_recognises_import_skill_and_force()
    {
        var import = HubOptions.Parse(["--import-skill", "C:\\somewhere\\demo", "--force"], _ => null);
        Assert.Equal(HubCommand.ImportSkill, import.Command);
        Assert.Equal(Path.GetFullPath("C:\\somewhere\\demo"), import.ImportSkillPath);
        Assert.True(import.Force);

        var withoutForce = HubOptions.Parse(["--import-skill", "C:\\somewhere\\demo"], _ => null);
        Assert.False(withoutForce.Force);

        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--import-skill"], _ => null));
    }

    // --- Row 20 task 1: --overlay -----------------------------------------------------------

    [Fact]
    public void Options_parse_recognises_overlay_alongside_import_skill()
    {
        var withOverlay = HubOptions.Parse(["--import-skill", "C:\\somewhere\\demo", "--overlay", "C:\\somewhere\\odir"], _ => null);
        Assert.Equal(HubCommand.ImportSkill, withOverlay.Command);
        Assert.Equal(Path.GetFullPath("C:\\somewhere\\odir"), withOverlay.OverlayPath);

        var without = HubOptions.Parse(["--import-skill", "C:\\somewhere\\demo"], _ => null);
        Assert.Null(without.OverlayPath);

        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--import-skill", "C:\\somewhere\\demo", "--overlay"], _ => null));
    }

    [Fact]
    public void Overlay_without_import_skill_is_refused()
    {
        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--overlay", "C:\\somewhere\\odir"], _ => null));
    }

    /// <summary>A synthetic (never third-party, D-g) skill source directory outside the data dir.</summary>
    private string NewSkillSource(string name, string skillMd)
    {
        var dir = Path.Combine(NewDir(), name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), skillMd);
        return dir;
    }

    private const string DemoSkillBody = "---\nname: demo\ndescription: A demo skill for tests.\n---\n# Demo Skill\n\nBody text here.\n";

    [Fact]
    public void A9_import_skill_works_before_any_hub_has_ever_started_and_records_the_hash()
    {
        var dir = NewDir();   // no chopitup.db, no tokens.json - nothing has touched this dir yet
        var source = NewSkillSource("demo", DemoSkillBody);

        var output = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.ImportSkill, ImportSkillPath: source), output, new StringWriter());

        Assert.Equal(0, exit);
        var installed = Path.Combine(dir, "skills", "demo", "SKILL.md");
        Assert.True(File.Exists(installed));
        Assert.Contains("demo", output.ToString());

        var db = new ChopDb(Path.Combine(dir, "chopitup.db"));
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(installed))).ToLowerInvariant();
        Assert.Equal(hash, new SkillHashes(db).Expected("demo"));
    }

    [Fact]
    public void A9_import_skill_missing_source_exits_4_and_writes_nothing()
    {
        var dir = NewDir();
        var missing = Path.Combine(dir, "nope-does-not-exist");

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.ImportSkill, ImportSkillPath: missing), new StringWriter(), error);

        Assert.Equal(4, exit);
        Assert.Contains(missing, error.ToString());
        Assert.False(Directory.Exists(Path.Combine(dir, "skills")) && Directory.EnumerateFileSystemEntries(Path.Combine(dir, "skills")).Any());
    }

    [Fact]
    public void A9_import_skill_an_invalid_name_exits_2()
    {
        var dir = NewDir();
        var source = NewSkillSource("Invalid_Name", DemoSkillBody);

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.ImportSkill, ImportSkillPath: source), new StringWriter(), error);

        Assert.Equal(2, exit);
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public async Task A9_import_skill_does_not_need_the_hub_stopped()
    {
        var dir = NewDir();
        await using var host = await HubTestHost.StartAsync(dir, deleteOnDispose: false);
        var source = NewSkillSource("demo", DemoSkillBody);

        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.ImportSkill, ImportSkillPath: source), new StringWriter(), new StringWriter());

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(dir, "skills", "demo", "SKILL.md")));
    }

    // --- Row 20 task 2: --set-classes ---------------------------------------------------------

    [Fact]
    public void Options_parse_recognises_set_classes()
    {
        var setClasses = HubOptions.Parse(["--set-classes", "gpt-5.4-mini=plumbing"], _ => null);
        Assert.Equal(HubCommand.SetClasses, setClasses.Command);
        Assert.Equal("gpt-5.4-mini=plumbing", setClasses.SetClassesSpec);

        var clear = HubOptions.Parse(["--set-classes", "sonnet="], _ => null);
        Assert.Equal("sonnet=", clear.SetClassesSpec);

        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--set-classes", "no-equals-sign"], _ => null));
        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--set-classes"], _ => null));
    }

    [Fact]
    public void Options_parse_rejects_an_empty_participant_id()
    {
        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--set-classes", "=judge"], _ => null));
    }

    [Fact]
    public void A8_set_classes_updates_a_codex_row_and_prints_the_normalized_set()
    {
        var dir = NewDir();
        StartedOnce(dir);

        var output = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.SetClasses, SetClassesSpec: "gpt-5.4-mini=Judge,plumbing,judge"), output, new StringWriter());

        Assert.Equal(0, exit);
        Assert.Contains("gpt-5.4-mini: classes = plumbing,judge", output.ToString());

        var db = new ChopDb(Path.Combine(dir, "chopitup.db"));
        var updated = new ParticipantStore(db).List().Single(p => p.Id == "gpt-5.4-mini");
        Assert.Equal("plumbing,judge", updated.Classes);
    }

    [Fact]
    public async Task A8_set_classes_is_refused_while_a_hub_owns_the_data_dir()
    {
        var dir = NewDir();
        await using var host = await HubTestHost.StartAsync(dir, deleteOnDispose: false);

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.SetClasses, SetClassesSpec: "sonnet=judge"), new StringWriter(), error);

        Assert.Equal(5, exit);
        Assert.Contains(dir, error.ToString());
    }

    [Fact]
    public void A8_set_classes_rejects_an_unknown_class_and_changes_nothing()
    {
        var dir = NewDir();
        StartedOnce(dir);
        var db = new ChopDb(Path.Combine(dir, "chopitup.db"));
        var before = new ParticipantStore(db).List().Single(p => p.Id == "sonnet").Classes;

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.SetClasses, SetClassesSpec: "sonnet=bogus"), new StringWriter(), error);

        Assert.Equal(2, exit);
        Assert.Contains("bogus", error.ToString());
        var after = new ParticipantStore(db).List().Single(p => p.Id == "sonnet").Classes;
        Assert.Equal(before, after);
    }

    [Fact]
    public void A8_set_classes_with_an_unknown_participant_exits_4()
    {
        var dir = NewDir();
        StartedOnce(dir);

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.SetClasses, SetClassesSpec: "mallory=judge"), new StringWriter(), error);

        Assert.Equal(4, exit);
        Assert.Contains("mallory", error.ToString());
    }

    [Fact]
    public void A8_set_classes_works_before_the_first_hub_start()
    {
        var dir = NewDir();
        // What --import-skill leaves behind before any hub has ever started: a v9 database with
        // the roster seeded, no tokens.json.
        new ChopDb(Path.Combine(dir, "chopitup.db")).EnsureDatabase();

        var output = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.SetClasses, SetClassesSpec: "gpt-5.4-mini=plumbing"), output, new StringWriter());

        Assert.Equal(0, exit);
        Assert.Contains("gpt-5.4-mini: classes = plumbing", output.ToString());
        Assert.False(File.Exists(Path.Combine(dir, TokenStore.FileName)));
    }

    // --- Row 11 task 6: the owner-remote host config and acceptance 6's unit half -----------

    [Fact]
    public void A7_print_config_writes_the_owner_remote_config_with_only_its_own_token()
    {
        var dir = NewDir();
        StartedOnce(dir);
        var tokens = TokenStore.ReadExisting(dir, Participants);

        var exit = HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), new StringWriter());
        Assert.Equal(0, exit);

        var path = Path.Combine(ConfigFolder(dir), "claude-code-owner-remote.json");
        Assert.True(File.Exists(path));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("chopitup");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:9123/mcp", server.GetProperty("url").GetString());
        Assert.Equal("Bearer " + HostConfigs.TokenPlaceholder, server.GetProperty("headers").GetProperty("Authorization").GetString());

        var text = File.ReadAllText(path);
        foreach (var token in tokens.Values) Assert.DoesNotContain(token, text);

        var readme = File.ReadAllText(Path.Combine(ConfigFolder(dir), "README.md"));
        Assert.Contains("claude-code-owner-remote.json", readme);
        Assert.Contains("| Classes |", readme);
    }

    /// <summary>Unit half of acceptance 6 (critique pass 2, m-10); the live half is M11 check 7.
    /// A post authenticated as <c>owner-remote</c> through the real MCP surface is stamped
    /// <c>owner-remote</c> and opens an exchange exactly as an <c>owner</c> post does.</summary>
    private static readonly SpawnLimits Fast = new(Budget: 4, Debounce: TimeSpan.FromMilliseconds(150), MinSpacing: TimeSpan.Zero, Timeout: TimeSpan.FromSeconds(30), TranscriptMessages: 60, TranscriptChars: 24_000);

    [Fact]
    public async Task A6_a_post_authenticated_as_owner_remote_is_stamped_owner_remote_and_opens_an_exchange()
    {
        var dir = NewDir();
        var runner = new FakeProcessRunner { Handler = (_, timeout, ct) => FakeProcessRunner.HangUntilKilled(timeout, ct) };
        await using var host = await HubTestHost.StartAsync(dir, processRunner: runner, limits: Fast);
        host.AuthorizeAs(ChopDb.OwnerParticipantId);   // row 28: the cleanup stop below is a write and now needs a credential too
        await using var proxy = await host.ClientFor("owner-remote");

        var posted = HubTestHost.Json(await proxy.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "@sonnet hello from the phone" }));
        Assert.Equal("owner-remote", posted.GetProperty("author_id").GetString());

        var spec = await runner.NextSpecAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(spec));

        using var doc = JsonDocument.Parse(await host.Client.GetStringAsync("api/rooms/general/exchange"));
        Assert.Equal("open", doc.RootElement.GetProperty("status").GetString());

        await host.Client.PostAsync("api/rooms/general/exchange/stop", null);   // clean up the hanging spawn before dispose
    }

    // --- Row 24 task 4: --export-memory -----------------------------------------------------

    private static void SeedOneLiveMemory(string dataDir) =>
        new MemoryStore(Path.Combine(dataDir, "memory")).Append("user", "A", "Body A.", "prov");

    [Fact]
    public void Options_parse_recognises_export_memory_roots_a_relative_path_and_trims_a_trailing_separator()
    {
        var relative = HubOptions.Parse(["--export-memory", "somedir"], _ => null);
        Assert.Equal(HubCommand.ExportMemory, relative.Command);
        Assert.Equal(Path.GetFullPath("somedir"), relative.ExportMemoryPath);
        Assert.False(relative.AcceptNewSource);

        var trimmed = HubOptions.Parse(["--export-memory", "C:\\x\\"], _ => null);
        Assert.Equal("C:\\x", trimmed.ExportMemoryPath);

        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--export-memory"], _ => null));
    }

    [Fact]
    public void Options_parse_refuses_a_drive_root_for_export_memory()
    {
        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--export-memory", "C:\\"], _ => null));
    }

    [Fact]
    public void Options_parse_recognises_accept_new_source_alongside_export_memory()
    {
        var withAccept = HubOptions.Parse(["--export-memory", "C:\\somewhere\\odir", "--accept-new-source"], _ => null);
        Assert.Equal(HubCommand.ExportMemory, withAccept.Command);
        Assert.True(withAccept.AcceptNewSource);
    }

    [Fact]
    public void Accept_new_source_or_overlay_used_with_the_wrong_verb_is_refused()
    {
        // --accept-new-source belongs to --export-memory only (D9), the same idiom --overlay
        // already follows for --import-skill (claim 15).
        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--accept-new-source"], _ => null));
        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--import-skill", "C:\\somewhere\\demo", "--accept-new-source"], _ => null));
        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--export-memory", "C:\\somewhere\\odir", "--overlay", "C:\\somewhere\\odir2"], _ => null));
    }

    [Fact]
    public async Task A24_export_memory_is_refused_while_a_hub_owns_the_data_dir()
    {
        var dir = NewDir();
        SeedOneLiveMemory(dir);
        var target = Path.Combine(NewDir(), "export-target");
        await using var host = await HubTestHost.StartAsync(dir, deleteOnDispose: false);

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.ExportMemory, ExportMemoryPath: target), new StringWriter(), error);

        Assert.Equal(5, exit);
        Assert.Contains(dir, error.ToString());
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void A24_export_memory_against_a_data_dir_with_no_memory_directory_exits_4_and_creates_nothing()
    {
        var dir = NewDir();   // no memory\ subdirectory
        var target = Path.Combine(NewDir(), "export-target");

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.ExportMemory, ExportMemoryPath: target), new StringWriter(), error);

        Assert.Equal(4, exit);
        Assert.Contains("memory directory", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(dir, "memory")));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void A24_export_memory_with_zero_live_entries_refuses_without_force_and_succeeds_with_it()
    {
        var dir = NewDir();
        Directory.CreateDirectory(Path.Combine(dir, "memory"));   // memory\ exists but holds no entries
        var target = Path.Combine(NewDir(), "export-target");

        var error = new StringWriter();
        var refused = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.ExportMemory, ExportMemoryPath: target), new StringWriter(), error);
        Assert.NotEqual(0, refused);
        Assert.False(Directory.Exists(target));

        var forced = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.ExportMemory, ExportMemoryPath: target, Force: true), new StringWriter(), new StringWriter());
        Assert.Equal(0, forced);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void A24_export_memory_over_cap_store_exits_6_not_3()
    {
        var dir = NewDir();
        var store = new MemoryStore(Path.Combine(dir, "memory"));
        for (var i = 0; i < 200; i++)
            store.Append("bulk", $"Title {i}", $"Body {i}.", "prov");
        var target = Path.Combine(NewDir(), "export-target");

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.ExportMemory, ExportMemoryPath: target), new StringWriter(), error);

        Assert.Equal(6, exit);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void A24_export_memory_a_clean_run_against_scratch_dirs_exits_0()
    {
        var dir = NewDir();
        SeedOneLiveMemory(dir);
        var target = Path.Combine(NewDir(), "export-target");

        var output = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.ExportMemory, ExportMemoryPath: target), output, new StringWriter());

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(target, "MEMORY.md")));
        Assert.Contains("EXPORT_RESULT:", output.ToString());
    }
}
