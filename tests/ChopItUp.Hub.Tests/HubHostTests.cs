using System.Net;
using System.Net.Http.Headers;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Hosting;
using ChopItUp.Hub.Security;

namespace ChopItUp.Hub.Tests;

public sealed class HubHostTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_hub_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;

    public async Task InitializeAsync() => _host = await HubTestHost.StartAsync(_dir);
    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public void Startup_creates_db_and_tokens_and_binds_loopback()
    {
        Assert.True(File.Exists(Path.Combine(_dir, "chopitup.db")));
        Assert.True(File.Exists(Path.Combine(_dir, "tokens.json")));
        Assert.Equal("127.0.0.1", _host.BaseAddress.Host);
        var tokens = TokenStore.Load(_dir, ChopDb.SeedRoster.Select(p => p.Id).ToArray());
        Assert.Equal(ChopDb.SeedRoster.Count, tokens.Count);
        Assert.Equal(ChopDb.SeedRoster.Count, tokens.Tokens.Values.Distinct().Count());
        Assert.All(tokens.Tokens.Values, t => Assert.True(t.Length >= 32));
    }

    [Fact]
    public async Task Health_is_open_and_reports_schema()
    {
        var res = await _host.Client.GetStringAsync("/health");
        Assert.Contains($"\"schema\":{ChopItUp.Core.Storage.ChopDb.LatestSchemaVersion}", res);
        // A9: retry-key adoption is part of the shape from M2 on — empty on a hub nobody has posted to.
        var health = System.Text.Json.JsonDocument.Parse(res).RootElement;
        Assert.True(health.GetProperty("ok").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Array, health.GetProperty("key_usage").ValueKind);
        Assert.Empty(health.GetProperty("key_usage").EnumerateArray());
    }

    [Fact]
    public async Task Mcp_without_token_is_401_and_with_wrong_token_is_401()
    {
        using var noAuth = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", new MediaTypeHeaderValue("application/json")) };
        var r1 = await _host.Client.SendAsync(noAuth);
        Assert.Equal(HttpStatusCode.Unauthorized, r1.StatusCode);
        Assert.Contains("Bearer", r1.Headers.WwwAuthenticate.ToString());

        using var badAuth = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", new MediaTypeHeaderValue("application/json")) };
        badAuth.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        var r2 = await _host.Client.SendAsync(badAuth);
        Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);
    }

    [Fact]
    public void Second_hub_on_the_same_data_dir_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => HubHost.Build(new HubOptions(_dir, Port: 0)));
        Assert.Contains("one hub per data directory", ex.Message);
    }

    [Fact]
    public void Options_parse_args_then_env_then_defaults()
    {
        var argsData = Path.Combine(Path.GetTempPath(), "from-args");
        var envData = Path.Combine(Path.GetTempPath(), "from-env");

        var fromArgs = HubOptions.Parse(["--data", argsData, "--port", "1234"], _ => null);
        Assert.Equal(argsData, fromArgs.DataDir);
        Assert.Equal(1234, fromArgs.Port);

        var fromEnv = HubOptions.Parse([], name => name switch { "CHOPITUP_DATA" => envData, "CHOPITUP_PORT" => "4321", _ => null });
        Assert.Equal(envData, fromEnv.DataDir);
        Assert.Equal(4321, fromEnv.Port);

        var defaults = HubOptions.Parse([], _ => null);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "data"), defaults.DataDir);
        Assert.Equal(HubOptions.DefaultPort, defaults.Port);
    }

    [Fact]
    public void A_trailing_data_or_port_flag_with_no_value_throws_instead_of_silently_defaulting()
    {
        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--data"], _ => null));
        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--port"], _ => null));
    }
}
