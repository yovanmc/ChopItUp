using System.Net;
using System.Net.Sockets;
using ChopItUp.Core.Memory;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Mcp;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Realtime;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Security;
using ChopItUp.Hub.Skills;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Web;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.AspNetCore;

namespace ChopItUp.Hub.Hosting;

public static class HubHost
{
    public static WebApplication Build(HubOptions options, IProcessRunner? processRunner = null, SpawnLimits? limits = null, CliLocator? cliLocator = null, Func<string, MemoryGit>? memoryGit = null, Func<string, GitTrail>? roomGit = null, TimeProvider? clock = null, RunLimits? runLimits = null)
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
            var participants = new ParticipantStore(db);
            // Startup-static, like the tokens: the roster is read once here, and every consumer
            // below (tokens, instructions, tools) sees the same list. Editing rows takes effect at
            // the next hub start.
            var roster = participants.List();
            var tokens = TokenStore.Load(options.DataDir, roster.Select(p => p.Id).ToArray());
            // M10: the memory store lives beside the database; the seed core is written once, the git
            // trail is created lazily by the first approval (plan decisions 1, 5).
            var memory = new MemoryStore(Path.Combine(options.DataDir, "memory"));
            memory.EnsureLayout();
            // Row 11: read on demand, never cached (D-d) - a skill is a document, not a credential,
            // and --import-skill must take effect without a restart.
            var skills = new SkillStore(Path.Combine(options.DataDir, "skills"), new SkillHashes(db));
            skills.EnsureLayout();

            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton(new MessageStore(db));
            builder.Services.AddSingleton(participants);
            builder.Services.AddSingleton<MessageSignal>();
            builder.Services.AddSingleton(tokens);
            builder.Services.AddSingleton<IReadOnlyList<Participant>>(roster);
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(limits ?? SpawnLimits.Default);
            // Row 19's clock seam (pass 2's F-13): .NET's own TimeProvider, not a hand-rolled
            // interface, so a run's whole timeline can be driven by a fake clock in tests without
            // waiting on a wall clock (D9's 8-hour cap).
            var effectiveClock = clock ?? TimeProvider.System;
            builder.Services.AddSingleton(effectiveClock);
            // Row 19: the runs table and its satellites (schema v8), and D9's hard-coded caps -
            // exactly as unreachable from inside a room as SpawnLimits.Default above. runLimits is an
            // override for the same reason `limits` (SpawnLimits) is one: task 9's tests need a small
            // ceiling and a controllable clock to prove a cap parks a run without actually waiting
            // out 80 spawns or 8 hours.
            var runs = new RunStore(db);
            builder.Services.AddSingleton(runs);
            builder.Services.AddSingleton(runLimits ?? RunLimits.Default);
            // Row 19, task 9e (AC12): a stored 'active' run means the hub died, was killed, or was
            // force-stopped while something was driving it - nothing IS driving it anymore in this
            // fresh process, so it must never be left 'active' across a restart (the plan's
            // termination obligation). Parked before anything is served: MessageSignal and
            // SpawnerService do not exist yet at this point in Build, so there is nowhere to post a
            // note to - AC12 asks for none.
            foreach (var stale in runs.ListActive())
                runs.Park(stale.Id, "the hub restarted while this run was active", capSpent: false, effectiveClock.GetUtcNow());
            builder.Services.AddSingleton<IProcessRunner>(processRunner ?? new ProcessRunner());
            builder.Services.AddSingleton<CliLocator>(cliLocator ?? (name => CliResolver.Resolve(name)));
            // Row 19, task 12d: one gate per room at a time. A singleton so the lock survives
            // regardless of RunTools' own DI lifetime (RunTools, like RoomTools/MemoryTools, holds no
            // state of its own).
            builder.Services.AddSingleton<GateLocks>();
            builder.Services.AddSingleton<SpawnerService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SpawnerService>());
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton(memory);
            builder.Services.AddSingleton(skills);
            builder.Services.AddSingleton(new MemoryProposalStore(db));
            builder.Services.AddSingleton((memoryGit ?? (root => new MemoryGit(root)))(memory.Root));
            builder.Services.AddSingleton(new RoomTrails(roomGit ?? (dir => new GitTrail(dir))));
            builder.Services.AddSingleton(sp => new RoomDirectories(
                sp.GetRequiredService<MessageStore>(), sp.GetRequiredService<RoomTrails>(), RoomPathRules.ForHub(options.DataDir), options.RoomsRootPath));
            builder.Services.AddMcpServer(o => o.ServerInstructions = Participation.Instructions(roster))
                .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
                .WithTools<RoomTools>().WithTools<MemoryTools>().WithTools<RunTools>();
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
            app.MapRoomsApi();
            app.MapExchangeApi();
            app.MapRunsApi();
            app.MapMemoryApi();
            app.MapSkillsApi();
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
