using System.Net;
using System.Net.Sockets;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Mcp;
using ChopItUp.Hub.Realtime;
using ChopItUp.Hub.Security;
using ChopItUp.Hub.Web;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.AspNetCore;

namespace ChopItUp.Hub.Hosting;

public static class HubHost
{
    public static WebApplication Build(HubOptions options)
    {
        var hubLock = HubLock.Acquire(options.DataDir);   // first: fail fast if another hub owns this dir
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
            // AllowedHosts already permits [::1], but nothing was listening there — and on Windows
            // `localhost` resolves to ::1 first, so a host configured with a localhost URL never
            // reached us (pass 2, MINOR-17). Guarded on a non-zero port: with port 0 the two
            // families get different ephemeral ports and the single-address assumption breaks.
            // An absent or disabled IPv6 stack is not a reason to fail to start; 127.0.0.1 is the
            // contract and ::1 is the convenience.
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(IPAddress.Loopback, options.Port);
                if (options.Port != 0 && Socket.OSSupportsIPv6)
                {
                    try { k.Listen(IPAddress.IPv6Loopback, options.Port); }
                    catch (Exception e) when (e is SocketException or InvalidOperationException or NotSupportedException)
                    {
                        Console.Error.WriteLine($"Not listening on [::1]:{options.Port} ({e.Message}); 127.0.0.1 only.");
                    }
                }
            });

            var db = new ChopDb(Path.Combine(options.DataDir, "chopitup.db"));
            db.EnsureDatabase();
            var tokens = TokenStore.Load(options.DataDir);

            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton(new MessageStore(db));
            builder.Services.AddSingleton<MessageSignal>();
            builder.Services.AddSingleton(tokens);
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddMcpServer(o => o.ServerInstructions = Participation.Instructions)
                .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
                .WithTools<RoomTools>();
            builder.Services.AddSignalR();

            var app = builder.Build();
            // The one place a post announces itself outward (see MessageSignal.Posted's doc comment):
            // every path that calls the message-carrying Publish overload — post_message now, the M3
            // web API's post/import — reaches every browser subscribed to that room's SignalR group,
            // regardless of which path stored the message.
            var roomHubContext = app.Services.GetRequiredService<IHubContext<RoomHub>>();
            app.Services.GetRequiredService<MessageSignal>().Posted += message => BroadcastAsync(roomHubContext, message);
            app.Lifetime.ApplicationStopped.Register(hubLock.Dispose);
            // Record the port actually bound, so --print-config emits URLs that match reality. With
            // Port: 0 the real port is only known after the server starts, so this reads the bound
            // address rather than options.Port.
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                    ?.Addresses.Select(a => new Uri(a).Port).FirstOrDefault();
                if (bound is > 0) HubPortFile.Write(options.DataDir, bound.Value);
            });
            app.UseMiddleware<BearerTokenMiddleware>();
            app.UseSpaClient(SpaFiles.ResolveWebRoot(options.WebRoot));
            app.MapGet("/health", (ChopDb d, MessageStore s) => Results.Json(new
            {
                ok = true,
                schema = d.GetSchemaVersion(),
                key_usage = s.KeyUsage().Select(r => new { author = r.AuthorId, keyed = r.Keyed, keyless = r.Keyless }),
            }));
            app.MapMcp("/mcp");
            app.MapHub<RoomHub>("/hub/rooms");
            app.MapChatApi();
            return app;
        }
        catch
        {
            hubLock.Dispose();
            throw;
        }
    }

    /// <summary>Fire-and-forget by design: a stalled or disconnected browser client must never slow
    /// down or fail the post that triggered it. Exceptions are swallowed after logging — nothing here
    /// is retried, and a missed broadcast is recoverable by the client re-reading the room.</summary>
    private static async void BroadcastAsync(IHubContext<RoomHub> hubContext, ChopItUp.Core.Model.Message message)
    {
        try
        {
            await hubContext.Clients.Group(message.RoomId).SendAsync("MessagePosted", new
            {
                message.Id,
                message.RoomId,
                message.AuthorId,
                message.Body,
                message.CreatedAt,
            });
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"SignalR broadcast to room '{message.RoomId}' failed: {e.Message}");
        }
    }
}
