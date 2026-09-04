using ChopItUp.Hub.Hosting;
using ChopItUp.Hub.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests;

/// <summary>Starts the real hub on 127.0.0.1:0 against a temp data dir. Disposal stops the host and
/// (by default) deletes the directory.</summary>
public sealed class HubTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _dir;
    private readonly bool _deleteOnDispose;

    public Uri BaseAddress { get; }
    public HttpClient Client { get; }
    public TokenStore Tokens { get; }

    private HubTestHost(WebApplication app, string dir, Uri baseAddress, bool deleteOnDispose)
    {
        _app = app;
        _dir = dir;
        _deleteOnDispose = deleteOnDispose;
        BaseAddress = baseAddress;
        Client = new HttpClient { BaseAddress = baseAddress };
        Tokens = TokenStore.Load(dir);
    }

    public static async Task<HubTestHost> StartAsync(string dir, bool deleteOnDispose = true)
    {
        var app = HubHost.Build(new HubOptions(dir, Port: 0));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new HubTestHost(app, dir, new Uri(address.TrimEnd('/') + "/"), deleteOnDispose);
    }

    public string TokenFor(string participant) => Tokens.Tokens[participant];

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (_deleteOnDispose && Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
