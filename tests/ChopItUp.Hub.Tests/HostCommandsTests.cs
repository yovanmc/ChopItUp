using System.Net;
using System.Net.Http.Headers;
using ChopItUp.Hub.Hosting;
using ChopItUp.Hub.Security;

namespace ChopItUp.Hub.Tests;

/// <summary>The non-serving CLI verbs: A6/A6b (--rotate-token) and the A7 no-lock contract that
/// --print-config must keep even before Task 5 fills its body in.</summary>
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
