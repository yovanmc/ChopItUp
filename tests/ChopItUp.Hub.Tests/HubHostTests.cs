using System.Net;
using System.Net.Http.Headers;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Hosting;
using ChopItUp.Hub.Security;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>Row 19 task 2b: the clock seam. Nothing yet reads it off a run path (that starts at
    /// task 4), so this proves the seam itself — the injected fake reaches the DI container the
    /// hub was built with, rather than every consumer silently falling back to the real wall clock.</summary>
    [Fact]
    public async Task A_HubTestHost_built_with_a_fake_TimeProvider_reports_the_fakes_time()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_clock_" + Guid.NewGuid().ToString("N"));
        var fake = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        await using var host = await HubTestHost.StartAsync(dir, clock: fake);

        Assert.Same(fake, host.Services.GetRequiredService<TimeProvider>());
        Assert.Equal(fake.GetUtcNow(), host.Services.GetRequiredService<TimeProvider>().GetUtcNow());

        fake.Advance(TimeSpan.FromHours(3));
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T03:00:00Z"), host.Services.GetRequiredService<TimeProvider>().GetUtcNow());
    }

    /// <summary>A host built with no clock argument falls back to the real wall clock (the production
    /// default), not to some fixed or null value.</summary>
    [Fact]
    public void With_no_clock_argument_HubHost_registers_the_real_TimeProvider()
    {
        Assert.Same(TimeProvider.System, _host.Services.GetRequiredService<TimeProvider>());
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

    [Fact]
    public void M9_A2_rooms_root_comes_from_the_flag_then_the_environment_then_the_profile()
    {
        var fromArgs = HubOptions.Parse(["--rooms-root", @"C:\Rooms\flag"], _ => null);
        Assert.Equal(@"C:\Rooms\flag", fromArgs.RoomsRootPath);

        var fromEnv = HubOptions.Parse([], name => name == "CHOPITUP_ROOMS" ? @"C:\Rooms\env" : null);
        Assert.Equal(@"C:\Rooms\env", fromEnv.RoomsRootPath);

        var both = HubOptions.Parse(["--rooms-root", @"C:\Rooms\flag"], name => name == "CHOPITUP_ROOMS" ? @"C:\Rooms\env" : null);
        Assert.Equal(@"C:\Rooms\flag", both.RoomsRootPath);

        var defaults = HubOptions.Parse([], _ => null);
        Assert.Null(defaults.RoomsRoot);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ChopItUp", "rooms"), defaults.RoomsRootPath);

        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--rooms-root"], _ => null));
    }

    [Fact]
    public void A_relative_data_dir_is_made_absolute_so_spawns_get_rooted_paths()
    {
        var options = HubOptions.Parse(["--data", ".data"], _ => null);
        Assert.True(Path.IsPathRooted(options.DataDir), $"expected a rooted path, got '{options.DataDir}'");
        Assert.EndsWith(".data", options.DataDir);
    }

    [Fact]
    public async Task M5_a_stale_spawn_directory_is_swept_at_start()
    {
        // One hub per data dir: this test's own directory, not the fixture's _dir which already has one running.
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_hub_sweep_" + Guid.NewGuid().ToString("N"));
        var stale = Path.Combine(dir, "spawns", "general-1-1-deadbeef");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "mcp.json"), "{}");
        await using var host = await HubTestHost.StartAsync(dir);
        Assert.False(Directory.Exists(Path.Combine(dir, "spawns")));
    }
}
