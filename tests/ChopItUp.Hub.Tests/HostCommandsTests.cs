using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Hosting;
using ChopItUp.Hub.Security;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Hub.Tests;

/// <summary>The non-serving CLI verbs: A6/A6b (--rotate-token) and A7 (--print-config), including
/// the no-lock contract --print-config keeps while a hub is running.</summary>
public sealed class HostCommandsTests : IDisposable
{
    private static readonly string[] Roster = ChopDb.SeedRoster.Select(p => p.Id).ToArray();
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
        TokenStore.Load(dir, Roster);
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
        var before = TokenStore.ReadExisting(dir, Roster);

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.RotateToken, "claude"), output, error);

        Assert.Equal(0, exit);
        var after = TokenStore.Load(dir, Roster).Tokens;
        Assert.NotEqual(before["claude"], after["claude"]);
        Assert.Equal(before["owner"], after["owner"]);
        Assert.Equal(before["codex"], after["codex"]);

        var stdout = output.ToString();
        foreach (var t in before.Values) Assert.DoesNotContain(t, stdout);
        foreach (var t in after.Values) Assert.DoesNotContain(t, stdout);
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

        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.RotateToken, "claude"), new StringWriter(), new StringWriter());
        Assert.Equal(0, exit);

        var newToken = TokenStore.Load(dir, Roster).Tokens["claude"];
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
    public void A7_print_config_writes_all_three_files_with_the_live_port_and_tokens()
    {
        var dir = NewDir();
        StartedOnce(dir);
        var tokens = TokenStore.ReadExisting(dir, Roster);

        var exit = HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), new StringWriter());
        Assert.Equal(0, exit);

        var folder = ConfigFolder(dir);
        // Three files, not four: no separate Claude Code artifact is generated. Claude Code joins
        // as 'claude' by pasting the Claude Desktop entry - owner ruling 2026-09-04, who prefers
        // one Claude identity and accepts that the two hosts share a read cursor. Spawn rows (M8)
        // add no files either: the hub is their client.
        Assert.Equal(
            new[] { "README.md", "claude-desktop.json", "codex-config.toml" },
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
        Assert.Equal("Bearer " + tokens["claude"], server.GetProperty("env").GetProperty("CHOPITUP_TOKEN").GetString());
        var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert.Equal("/c", args[0]);
        Assert.Equal("npx", args[1]);
        Assert.Contains("--allow-http", args);
        Assert.Contains(url, args);
        Assert.Contains("Authorization:${CHOPITUP_TOKEN}", args);   // header value via env: a space in an arg is mangled on Windows
        Assert.DoesNotContain(tokens["codex"], File.ReadAllText(Path.Combine(folder, "claude-desktop.json")));

        var codex = File.ReadAllText(Path.Combine(folder, "codex-config.toml"));
        Assert.Contains("[mcp_servers.chopitup]", codex);
        Assert.Contains($"url = \"{url}\"", codex);
        // Single braces, not the doubled ones the interpolated raw string is written with.
        Assert.Contains($"http_headers = {{ Authorization = \"Bearer {tokens["codex"]}\" }}", codex);
        Assert.Contains("bearer_token_env_var = \"CHOPITUP_CODEX_TOKEN\"", codex);
        // The commented bridge fallback carries the same cmd /c shape as the Claude Desktop entry,
        // for the same reason: there is no npx.exe to spawn directly on Windows.
        Assert.Contains("# command = \"cmd\"", codex);
        Assert.Contains("# args = [\"/c\", \"npx\", \"-y\", \"mcp-remote@", codex);
        Assert.Contains("\"Authorization:${CHOPITUP_TOKEN}\"", codex);
        Assert.DoesNotContain(tokens["claude"], codex);
        Assert.DoesNotContain(tokens["owner"], codex);

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
        Assert.Contains("Claude Code gets no file of its own", readme);
        Assert.Contains("only as private as", readme);
        Assert.Contains("## Restoring a backup", readme);
        // A restore that leaves the WAL behind replays the very writes it was meant to undo.
        Assert.Contains("chopitup.db-wal", readme);
        Assert.Contains("chopitup.db-shm", readme);
        foreach (var t in tokens.Values) Assert.DoesNotContain(t, readme);
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
        var tokens = TokenStore.ReadExisting(dir, Roster);

        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), output, error));

        var printed = output.ToString() + error.ToString();
        Assert.Contains(ConfigFolder(dir), printed);
        foreach (var t in tokens.Values) Assert.DoesNotContain(t, printed);
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
        var tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
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
    }

    [Fact]
    public void M8_A8_print_config_against_a_v2_database_writes_nothing_and_says_start_the_hub()
    {
        var dir = NewDir();
        TokenStore.Load(dir, ["owner", "claude", "codex"]);
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
        TokenStore.Load(dir, Roster);
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
        var tokens = TokenStore.ReadExisting(dir, Roster);

        Assert.Equal(0, HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), new StringWriter()));

        var folder = ConfigFolder(dir);
        var claude = File.ReadAllText(Path.Combine(folder, "claude-desktop.json"));
        var codex = File.ReadAllText(Path.Combine(folder, "codex-config.toml"));
        var readme = File.ReadAllText(Path.Combine(folder, "README.md"));
        Assert.Contains(tokens["claude"], claude);
        Assert.Contains(tokens["codex"], codex);
        foreach (var p in ChopDb.SeedRoster)
        {
            Assert.Contains($"`{p.Id}`", readme);
            Assert.DoesNotContain(tokens[p.Id], readme);   // the README never carries a token
        }
        Assert.Contains("usage credits", readme);
        Assert.Contains("no file", readme);

        // Rotating a spawn row's token changes only that key.
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.RotateToken, "gpt-5.5"), new StringWriter(), new StringWriter());
        Assert.Equal(0, exit);
        var after = TokenStore.ReadExisting(dir, Roster);
        Assert.NotEqual(tokens["gpt-5.5"], after["gpt-5.5"]);
        foreach (var id in Roster.Where(id => id != "gpt-5.5")) Assert.Equal(tokens[id], after[id]);
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
}
