using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Web;

/// <summary>The exchange state for the web UI (row 16 renders it; the tests are its first client).
/// No auth here, like the rest of <c>/api</c>: loopback is the boundary. The stop is the owner's "step in
/// and end it" (D17): the hub kills the in-flight spawns and closes the exchange; the owner's next
/// message opens a fresh one.</summary>
public static class ExchangeApi
{
    public static void MapExchangeApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/rooms/{roomId}/exchange", GetExchange);
        api.MapPost("/rooms/{roomId}/exchange/stop", StopExchange);
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
}
