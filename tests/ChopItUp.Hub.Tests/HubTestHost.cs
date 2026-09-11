using System.Net.Http.Headers;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Hosting;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Security;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ChopItUp.Hub.Tests;

/// <summary>Starts the real hub on 127.0.0.1:0 against a temp data dir. Disposal stops the host and
/// (by default) deletes the directory.</summary>
public sealed class HubTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _dir;
    private readonly bool _deleteOnDispose;
    // Row 28: a host-file (hashed-at-rest) participant's plaintext is only ever known at the moment
    // it is minted. This fixture plays the operator's part (D-28-d's --rotate-token) right after the
    // FIRST start against a fresh data dir - the one moment a plaintext value for a host-file row is
    // available - and caches it here for TokenFor/ClientFor. A SECOND HubTestHost against an EXISTING
    // dir (PersistenceTests) must not repeat this: it would rotate away the very tokens that test is
    // proving survive a restart, so a token captured before the restart has to be presented directly
    // via ClientFor's bearer override instead.
    private readonly Dictionary<string, string> _mintedHostFile = new(StringComparer.Ordinal);

    public Uri BaseAddress { get; }
    public HttpClient Client { get; }
    public TokenStore Tokens { get; }
    public string RoomsRoot { get; }

    private HubTestHost(WebApplication app, string dir, Uri baseAddress, bool deleteOnDispose, string roomsRoot, bool freshDataDir)
    {
        _app = app;
        _dir = dir;
        _deleteOnDispose = deleteOnDispose;
        BaseAddress = baseAddress;
        Client = new HttpClient { BaseAddress = baseAddress };
        // The LIVE singleton the running hub's BearerTokenMiddleware and SpawnerService already use -
        // never a second, independent TokenStore.Load: a spawnable row's bearer is minted fresh on
        // every Load, so a second instance would hold different ephemeral values than the ones the
        // hub itself is actually checking requests against.
        Tokens = app.Services.GetRequiredService<TokenStore>();
        RoomsRoot = roomsRoot;
        if (freshDataDir)
            foreach (var p in ChopDb.SeedRoster.Where(p => p.Kind != "system" && !ExchangePolicy.IsSpawnable(p)))
                _mintedHostFile[p.Id] = Tokens.MintFor(p.Id);
    }

    public static async Task<HubTestHost> StartAsync(string dir, bool deleteOnDispose = true, string? webRoot = null, IProcessRunner? processRunner = null, SpawnLimits? limits = null, CliLocator? cliLocator = null, Func<string, MemoryGit>? memoryGit = null, Func<string, GitTrail>? roomGit = null, string? roomsRoot = null, TimeProvider? clock = null, RunLimits? runLimits = null, IOwnerPeerCheck? ownerPeerCheck = null, bool ownerPeerCheckEnabled = true, int port = 0)
    {
        var freshDataDir = !File.Exists(Path.Combine(dir, TokenStore.FileName));
        // Row 29 Task 4: port 0 (the default) never gets the [::1] listener (HubHost.cs:41, ledger
        // 23) - a caller that needs to prove anything over IPv6 passes a fixed free port instead.
        var options = new HubOptions(dir, Port: port, WebRoot: webRoot, RoomsRoot: roomsRoot ?? dir + "_rooms", OwnerPeerCheck: ownerPeerCheckEnabled);
        var app = HubHost.Build(options, processRunner ?? new RefusingProcessRunner(), limits, cliLocator ?? FakeCli.Locate, memoryGit, roomGit, clock, runLimits, ownerPeerCheck);
        await app.StartAsync();
        // With a fixed port and an IPv6 stack, HubHost binds BOTH 127.0.0.1 and [::1] (HubHost.cs:
        // 38-49), so Addresses can hold two entries here - Single() would throw. BaseAddress stays
        // the IPv4 one, matching every existing test's expectations of this fixture; a caller that
        // needs the IPv6 side (Row 29 Task 4) builds that URI itself from the same port.
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var address = addresses.FirstOrDefault(a => new Uri(a).Host == "127.0.0.1") ?? addresses.Single();
        return new HubTestHost(app, dir, new Uri(address.TrimEnd('/') + "/"), deleteOnDispose, options.RoomsRootPath, freshDataDir);
    }

    public IServiceProvider Services => _app.Services;

    /// <summary>The plaintext bearer for <paramref name="participant"/>. A spawnable row's ephemeral
    /// bearer is always available; a host-file row's plaintext is only known here when THIS fixture
    /// minted it (right after the first start against a fresh data dir - see the constructor). A
    /// second host against an already-populated dir cannot answer for a host-file row: present a
    /// token captured before the restart directly via <see cref="ClientFor(string, string?)"/>'s
    /// bearer override instead.</summary>
    public string TokenFor(string participant) =>
        _mintedHostFile.TryGetValue(participant, out var minted) ? minted : Tokens.BearerFor(participant);

    /// <summary>Row 28 Task 4: sets <see cref="Client"/>'s default <c>Authorization</c> header to
    /// <paramref name="participant"/>'s bearer (via <see cref="TokenFor"/>), so every non-GET
    /// <c>/api</c> call this fixture's own tests already made unauthenticated keeps working now that
    /// <c>BearerTokenMiddleware</c> guards them. Built on <see cref="TokenFor"/> rather than a second
    /// lookup path. A request that builds its own <see cref="HttpRequestMessage"/> and sets its own
    /// <c>Authorization</c> header overrides this default; a test proving the no-credential or a
    /// non-owner path needs a client that never had this called on it (see
    /// <c>SkillsApiAuthTests.Send</c>).</summary>
    public void AuthorizeAs(string participant) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(participant));

    public async Task<McpClient> ClientFor(string participant, string? bearer = null)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(BaseAddress, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + (bearer ?? TokenFor(participant)) },
        });
        return await McpClient.CreateAsync(transport);
    }

    public static JsonElement Json(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        return JsonDocument.Parse(text).RootElement;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (_deleteOnDispose)
        {
            TestDirs.DeleteTree(_dir);
            TestDirs.DeleteTree(RoomsRoot);
        }
    }
}
