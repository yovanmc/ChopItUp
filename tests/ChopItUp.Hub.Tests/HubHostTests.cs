using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Hosting;
using ChopItUp.Hub.Security;
using ChopItUp.Hub.Spawning;
using Microsoft.AspNetCore.Builder;
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

        // Row 28: 'hub' (system) holds no credential at all; a host-file row's persisted value is a
        // sha256 hex hash, never a plaintext; a spawnable row's plaintext lives only in memory.
        var hostFile = TokenStore.ReadExisting(_dir, ChopDb.SeedRoster);
        var expectedHostFile = ChopDb.SeedRoster.Count(p => p.Kind != "system" && !ExchangePolicy.IsSpawnable(p));
        Assert.Equal(expectedHostFile, hostFile.Count);
        Assert.All(hostFile.Values, hash => Assert.Equal(64, hash.Length));
        Assert.Equal(hostFile.Count, hostFile.Values.Distinct().Count());

        var ephemeral = ChopDb.SeedRoster.Where(ExchangePolicy.IsSpawnable).Select(p => _host.TokenFor(p.Id)).ToList();
        Assert.Equal(ephemeral.Count, ephemeral.Distinct().Count());
        Assert.All(ephemeral, t => Assert.True(t.Length >= 32));
    }

    /// <summary>AC7: a minted spawn credential authenticates <c>/mcp</c>, stops authenticating at the
    /// next hub start, and appears in no persisted store — never as a tautology (the value it mints
    /// is only ever compared to itself); a REAL restart and a REAL 401 are both exercised.</summary>
    [Fact]
    public async Task AC7_a_minted_spawn_credential_authenticates_mcp_then_dies_at_restart_and_is_never_persisted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_ac7_" + Guid.NewGuid().ToString("N"));
        string bearer;
        await using (var host1 = await HubTestHost.StartAsync(dir, deleteOnDispose: false))
        {
            bearer = host1.TokenFor("opus");
            await using var client = await host1.ClientFor("opus");
            var r = await client.CallToolAsync("list_rooms", new Dictionary<string, object?>());
            Assert.NotEqual(true, r.IsError);
        }

        Assert.DoesNotContain(bearer, File.ReadAllText(Path.Combine(dir, "tokens.json")));

        await using var host2 = await HubTestHost.StartAsync(dir, deleteOnDispose: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(host2.BaseAddress, "mcp"))
        { Content = new StringContent("{}", new MediaTypeHeaderValue("application/json")) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var res = await host2.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.DoesNotContain(bearer, File.ReadAllText(Path.Combine(dir, "tokens.json")));
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

    // --- Row 28 ticket 3: no live token left under data\host-configs\ at hub start -------------

    /// <summary>What a pre-row-28 (or hand-edited) `--print-config` run left behind: a host-config
    /// file with a REAL live token embedded, in the exact shape <see cref="HostConfigs.Write"/>
    /// produces. Reused here rather than hand-typed so the fixture matches production, the same
    /// discipline <c>TokenScanTests</c> follows for <see cref="TokenScan.Candidates"/> itself.</summary>
    [Fact]
    public async Task Row28_a_live_token_left_in_a_host_config_file_is_placeholdered_at_start_and_the_path_is_reported()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_hub_sweep_tok_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        new ChopDb(Path.Combine(dir, "chopitup.db")).EnsureDatabase();
        var roster = ChopDb.SeedRoster;
        // What `--rotate-token claude` would have printed once, pre-row-28-Task-3 shape: a real
        // plaintext embedded straight into the host file (never through the new placeholder path).
        var plaintext = TokenStore.Load(dir, roster).MintFor("claude");
        var claudeOnly = roster.Where(p => p.Id == "claude").ToList();
        HostConfigs.Write(dir, 9999, new Dictionary<string, string> { ["claude"] = plaintext }, claudeOnly);
        var path = Path.Combine(dir, HostConfigs.FolderName, "claude-desktop.json");
        Assert.Contains(plaintext, File.ReadAllText(path));

        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        HubTestHost host;
        try
        {
            host = await HubTestHost.StartAsync(dir, deleteOnDispose: true);
        }
        finally
        {
            Console.SetError(originalError);
        }
        await using (host)
        {
            var rewritten = File.ReadAllText(path);
            Assert.DoesNotContain(plaintext, rewritten);
            Assert.Contains(HostConfigs.TokenPlaceholder, rewritten);
            Assert.Contains(path, captured.ToString());
        }
    }

    /// <summary>AC4's second half: a file the sweep cannot rewrite must not stop the hub. Locked with
    /// <c>FileShare.None</c> for the whole start (Task 8's own ACL/lock choice, applied here at unit
    /// scope) so even the read half of the sweep fails, not only the write — the hub must still come
    /// up, the stderr line must still name the file, and one hub note must land in `general`.</summary>
    [Fact]
    public async Task Row28_a_host_config_file_that_cannot_be_rewritten_does_not_stop_the_hub_and_posts_a_room_note()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_hub_sweep_locked_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        new ChopDb(Path.Combine(dir, "chopitup.db")).EnsureDatabase();
        var roster = ChopDb.SeedRoster;
        var plaintext = TokenStore.Load(dir, roster).MintFor("claude");
        var claudeOnly = roster.Where(p => p.Id == "claude").ToList();
        HostConfigs.Write(dir, 9999, new Dictionary<string, string> { ["claude"] = plaintext }, claudeOnly);
        var path = Path.Combine(dir, HostConfigs.FolderName, "claude-desktop.json");
        var before = File.ReadAllBytes(path);

        // FileShare.None blocks EVERY later open of this path, read included, from this process or
        // any other (measured: a same-process File.ReadAllBytes against a FileShare.None handle
        // throws IOException here) — so it must be released before this test reads the file back or
        // lets HubTestHost delete the tree; both would otherwise throw the same sharing violation.
        var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        HubTestHost host;
        try
        {
            host = await HubTestHost.StartAsync(dir, deleteOnDispose: true);
        }
        finally
        {
            Console.SetError(originalError);
        }

        var health = await host.Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains(path, captured.ToString());

        using (var doc = JsonDocument.Parse(await host.Client.GetStringAsync("/api/rooms/general/messages")))
        {
            var notes = doc.RootElement.GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("authorId").GetString() == ChopDb.HubParticipantId)
                .Select(m => m.GetProperty("body").GetString())
                .ToList();
            Assert.Contains(notes, n => n!.Contains(path, StringComparison.Ordinal) && n.Contains("--rotate-token", StringComparison.Ordinal));
        }

        locked.Dispose();
        Assert.Equal(before, File.ReadAllBytes(path));   // the sweep's write never landed while it was locked
        await host.DisposeAsync();
    }

    // --- Row 29: --owner-peer-check / CHOPITUP_OWNER_PEER_CHECK -------------------------------

    [Fact]
    public void Owner_peer_check_parses_from_the_flag_then_the_environment_defaulting_on()
    {
        Assert.False(HubOptions.Parse(["--owner-peer-check", "off"], _ => null).OwnerPeerCheck);
        Assert.True(HubOptions.Parse(["--owner-peer-check", "on"], _ => null).OwnerPeerCheck);
        Assert.False(HubOptions.Parse([], name => name == "CHOPITUP_OWNER_PEER_CHECK" ? "off" : null).OwnerPeerCheck);
        // The flag wins over the environment.
        Assert.True(HubOptions.Parse(["--owner-peer-check", "on"], name => name == "CHOPITUP_OWNER_PEER_CHECK" ? "off" : null).OwnerPeerCheck);
        Assert.True(HubOptions.Parse([], _ => null).OwnerPeerCheck);
        Assert.Throws<ArgumentException>(() => HubOptions.Parse(["--owner-peer-check"], _ => null));
    }

    /// <summary>D3: the switch's whole reason to exist is that it prints where the owner can see it —
    /// a silent bypass would be a second escalation on top of the first.</summary>
    [Fact]
    public async Task The_switch_prints_its_warning_at_build()
    {
        var offDir = Path.Combine(Path.GetTempPath(), "chopitup_ownerpeer_warn_off_" + Guid.NewGuid().ToString("N"));
        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        WebApplication offApp;
        try
        {
            offApp = HubHost.Build(new HubOptions(offDir, Port: 0, OwnerPeerCheck: false));
        }
        finally
        {
            Console.SetError(originalError);
        }
        Assert.Contains("--owner-peer-check off", captured.ToString());
        try { await offApp.DisposeAsync(); } catch { /* best-effort cleanup */ }

        var onDir = Path.Combine(Path.GetTempPath(), "chopitup_ownerpeer_warn_on_" + Guid.NewGuid().ToString("N"));
        captured = new StringWriter();
        Console.SetError(captured);
        WebApplication onApp;
        try
        {
            onApp = HubHost.Build(new HubOptions(onDir, Port: 0, OwnerPeerCheck: true));
        }
        finally
        {
            Console.SetError(originalError);
        }
        Assert.DoesNotContain("--owner-peer-check off", captured.ToString());
        try { await onApp.DisposeAsync(); } catch { /* best-effort cleanup */ }
    }

    // --- Row 29 commit 1: the Host-header gate accepts loopback under any spelling ------------

    /// <summary>Ticket 04's finding: Windows PowerShell 5.1's Invoke-WebRequest (.NET Framework's
    /// HttpWebRequest) sends the IPv6 loopback address fully expanded, never RFC 5952-canonical
    /// [::1] - the same address, spelled differently, and a real child hit a real 400 for it.</summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("[::1]")]
    [InlineData("[0000:0000:0000:0000:0000:0000:0000:0001]")]
    public async Task Health_accepts_every_loopback_spelling_of_the_Host_header(string host)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/health") { Headers = { Host = $"{host}:{_host.BaseAddress.Port}" } };
        var res = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    /// <summary>The reason the filter exists: accepting every spelling of loopback is not the same as
    /// accepting everything. A Host header naming an address or name that is not loopback still gets
    /// refused, at 400, before auth or any endpoint runs.</summary>
    [Theory]
    [InlineData("evil.example.com")]
    [InlineData("10.0.0.5")]
    [InlineData("[::2]")]
    public async Task Health_refuses_a_Host_header_that_does_not_name_loopback(string host)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/health") { Headers = { Host = $"{host}:{_host.BaseAddress.Port}" } };
        var res = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
