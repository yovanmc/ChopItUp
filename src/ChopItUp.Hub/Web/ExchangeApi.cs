using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Web;

/// <summary>The exchange state for the web UI. <c>GET</c> stays unauthenticated like the rest of
/// <c>/api</c>; <c>POST .../stop</c> needs an owner-class bearer (<c>BearerTokenMiddleware</c>).
/// The stop is the hub owner's "step in and end it": the hub kills the in-flight spawns and closes the
/// exchange; the hub owner's next message opens a fresh one.
/// <c>POST .../exchanges/{rootMessageId}/stop</c> stops one exchange and leaves the rest of the room
/// running.</summary>
public static class ExchangeApi
{
    public static void MapExchangeApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/rooms/{roomId}/exchange", GetExchange);
        api.MapPost("/rooms/{roomId}/exchange/stop", StopExchange);
        api.MapPost("/rooms/{roomId}/exchanges/{rootMessageId:long}/stop", StopOneExchange);
    }

    private static IResult GetExchange(string roomId, MessageStore store, SpawnerService spawner)
    {
        if (!store.RoomExists(roomId)) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        return Results.Json(spawner.Snapshot(roomId));
    }

    private static async Task<IResult> StopExchange(string roomId, MessageStore store, SpawnerService spawner)
    {
        if (!store.RoomExists(roomId)) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        var snapshot = await spawner.StopAsync(roomId);
        return snapshot is null
            ? Results.Conflict(new { error = "Nothing to stop in this room: no open exchange and no running spawn." })
            : Results.Json(snapshot);
    }

    private static async Task<IResult> StopOneExchange(string roomId, long rootMessageId, MessageStore store, SpawnerService spawner)
    {
        if (!store.RoomExists(roomId)) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        var (outcome, snapshot) = await spawner.StopExchangeAsync(roomId, rootMessageId);
        return outcome switch
        {
            ExchangeStopOutcome.Stopped => Results.Json(snapshot),
            ExchangeStopOutcome.NotFound => Results.NotFound(new { error = $"No exchange rooted at #{rootMessageId} in this room." }),
            ExchangeStopOutcome.RunOwnsRoom => Results.Conflict(new { error = "A run owns this room; stop the run instead." }),
            ExchangeStopOutcome.NothingToStop => Results.Conflict(new { error = "Nothing to stop in that exchange: it is closed and has no running spawn." }),
            var other => throw new InvalidOperationException($"Unhandled ExchangeStopOutcome {other}."),
        };
    }
}
