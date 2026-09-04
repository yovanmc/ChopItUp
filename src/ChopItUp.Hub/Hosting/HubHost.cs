using System.Net;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Mcp;
using ChopItUp.Hub.Security;
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
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port));

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

            var app = builder.Build();
            app.Lifetime.ApplicationStopped.Register(hubLock.Dispose);
            app.UseMiddleware<BearerTokenMiddleware>();
            app.MapGet("/health", (ChopDb d, MessageStore s) => Results.Json(new
            {
                ok = true,
                schema = d.GetSchemaVersion(),
                key_usage = s.KeyUsage().Select(r => new { author = r.AuthorId, keyed = r.Keyed, keyless = r.Keyless }),
            }));
            app.MapMcp("/mcp");
            return app;
        }
        catch
        {
            hubLock.Dispose();
            throw;
        }
    }
}
