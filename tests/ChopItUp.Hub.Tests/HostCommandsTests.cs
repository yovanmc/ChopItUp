using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ChopItUp.Hub.Hosting;
using ChopItUp.Hub.Security;

namespace ChopItUp.Hub.Tests;

/// <summary>The non-serving CLI verbs: A6/A6b (--rotate-token) and A7 (--print-config), including
/// the no-lock contract --print-config keeps while a hub is running.</summary>
public sealed class HostCommandsTests : IDisposable
{
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

    [Fact]
    public void A6_rotate_replaces_one_token_and_leaves_the_others_alone()
    {
        var dir = NewDir();
        var before = TokenStore.Load(dir).Tokens.ToDictionary(kv => kv.Key, kv => kv.Value);

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.RotateToken, "claude"), output, error);

        Assert.Equal(0, exit);
        var after = TokenStore.Load(dir).Tokens;
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
        TokenStore.Load(dir);
        var before = File.ReadAllBytes(Path.Combine(dir, TokenStore.FileName));

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.RotateToken, "mallory"), new StringWriter(), error);

        Assert.Equal(2, exit);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(dir, TokenStore.FileName)));
        var message = error.ToString();
        foreach (var p in TokenStore.Participants) Assert.Contains(p, message);
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

        var newToken = TokenStore.Load(dir).Tokens["claude"];
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
        var tokens = TokenStore.Load(dir).Tokens.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var exit = HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), new StringWriter());
        Assert.Equal(0, exit);

        var folder = ConfigFolder(dir);
        // Three files, not four: there is deliberately no Claude Code artifact, because it would
        // have to reuse the 'claude' token and two hosts on one identity race one read cursor.
        Assert.Equal(
            new[] { "README.md", "claude-desktop.json", "codex-config.toml" },
            Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        const string url = "http://127.0.0.1:9123/mcp";

        using var desktop = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "claude-desktop.json")));
        var server = desktop.RootElement.GetProperty("mcpServers").GetProperty("chopitup");
        Assert.Equal("npx", server.GetProperty("command").GetString());
        Assert.Equal("Bearer " + tokens["claude"], server.GetProperty("env").GetProperty("CHOPITUP_TOKEN").GetString());
        var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
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
        Assert.Contains("# args = [\"-y\", \"mcp-remote@", codex);   // the commented bridge fallback (grill note R2)
        Assert.Contains("\"Authorization:${CHOPITUP_TOKEN}\"", codex);
        Assert.DoesNotContain(tokens["claude"], codex);
        Assert.DoesNotContain(tokens["owner"], codex);

        var readme = File.ReadAllText(Path.Combine(folder, "README.md"));
        Assert.Contains(url, readme);
        Assert.Contains("Port 9123", readme);
        Assert.Contains(@"%APPDATA%\Claude\claude_desktop_config.json", readme);
        Assert.Contains(@"%USERPROFILE%\.codex\config.toml", readme);
        Assert.Contains("npm i -g mcp-remote@", readme);
        Assert.Contains("Claude Code is not configured here", readme);
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
        TokenStore.Load(dir);
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
        var tokens = TokenStore.Load(dir).Tokens.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

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
        TokenStore.Load(dir);

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
        TokenStore.Load(dir);
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
        TokenStore.Load(dir);
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
