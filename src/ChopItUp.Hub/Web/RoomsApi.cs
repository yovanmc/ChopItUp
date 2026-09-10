using ChopItUp.Core.Storage;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Web;

/// <summary>Room lifecycle for the web UI (M9): create, archive, bind a directory, mark read, and the
/// commit trail. Same guard as <see cref="ChatApi"/> since row 28: every non-<c>GET</c> route here
/// needs an owner-class bearer (<c>BearerTokenMiddleware</c>), superseding the old no-auth loopback
/// boundary. Directory work is the hub's alone (D11) — a browser never sends a git command. Room
/// create/bind/archive are refused while a spawn is in flight (plan decision 11).</summary>
public static class RoomsApi
{
    public const string SpawnRunning = "A spawn is in flight; change rooms when the exchange has finished.";
    public const string GeneralStays = "The general room cannot be archived.";
    public const int MaxNameChars = 80;
    public const int TrailLength = 20;

    public static void MapRoomsApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/rooms");
        api.MapPost("", CreateRoom);
        api.MapPost("/{roomId}/archive", Archive);
        api.MapPost("/{roomId}/unarchive", Unarchive);
        api.MapPost("/{roomId}/directory", BindDirectory);
        api.MapPost("/{roomId}/read", MarkRead);
        api.MapGet("/{roomId}/trail", GetTrail);
    }

    private static async Task<IResult> CreateRoom(CreateRoomBody body, MessageStore store, ParticipantStore participants, RoomDirectories directories, SpawnerService spawner, CancellationToken cancellation)
    {
        var name = (body.Name ?? "").Trim();
        if (name.Length is 0 or > MaxNameChars) return Results.BadRequest(new { error = $"name must be 1 to {MaxNameChars} characters." });
        if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
        var id = RoomIds.Unique(name, store.RoomExists);
        string directory;
        try { directory = await directories.PrepareAsync(id, body.Directory, cancellation); }
        catch (RoomDirectoryException e) { return Results.BadRequest(new { error = e.Message }); }
        try { store.CreateRoom(id, name, directory); }
        catch (ArgumentException e) { return Results.Conflict(new { error = e.Message }); }   // lost a race for the id
        return Results.Json(ChatApi.MapRoom(store.GetRoom(id, participants.OwnerId())!), statusCode: StatusCodes.Status201Created);
    }

    private static IResult Archive(string roomId, MessageStore store, ParticipantStore participants, SpawnerService spawner)
    {
        if (store.GetRoom(roomId) is not { } room) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        if (roomId == "general") return Results.BadRequest(new { error = GeneralStays });
        if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
        if (room.ArchivedAt is null) store.SetArchived(roomId, DateTimeOffset.UtcNow);
        return Results.Json(ChatApi.MapRoom(store.GetRoom(roomId, participants.OwnerId())!));
    }

    private static IResult Unarchive(string roomId, MessageStore store, ParticipantStore participants, SpawnerService spawner)
    {
        if (store.GetRoom(roomId) is null) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
        store.SetArchived(roomId, null);
        return Results.Json(ChatApi.MapRoom(store.GetRoom(roomId, participants.OwnerId())!));
    }

    /// <summary>Binds a directory to a legacy (M1–M10) room once. A room created after M9 always has one.</summary>
    private static async Task<IResult> BindDirectory(string roomId, DirectoryBody body, MessageStore store, ParticipantStore participants, RoomDirectories directories, SpawnerService spawner, CancellationToken cancellation)
    {
        if (store.GetRoom(roomId) is not { } room) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        if (room.Directory is not null) return Results.Conflict(new { error = $"Room '{roomId}' already has the directory '{room.Directory}'; a directory is bound once." });
        if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
        string directory;
        try { directory = await directories.PrepareAsync(roomId, body.Directory, cancellation); }
        catch (RoomDirectoryException e) { return Results.BadRequest(new { error = e.Message }); }
        if (!store.BindDirectory(roomId, directory))
            return Results.Conflict(new { error = $"Room '{roomId}' was bound by another request; reload." });
        return Results.Json(ChatApi.MapRoom(store.GetRoom(roomId, participants.OwnerId())!));
    }

    /// <summary>The owner's read cursor moves to the room's last message: the same row an MCP
    /// participant's read_messages advances (plan decision 10).</summary>
    private static IResult MarkRead(string roomId, MessageStore store, ParticipantStore participants)
    {
        if (store.GetRoom(roomId) is not { } room) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        if (room.LastMessageId > 0) store.SetCursor(participants.OwnerId(), roomId, room.LastMessageId);
        return Results.Json(new { roomId, unread = 0L });
    }

    private static async Task<IResult> GetTrail(string roomId, MessageStore store, RoomTrails trails, CancellationToken cancellation)
    {
        if (store.GetRoom(roomId) is not { } room) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        if (room.Directory is null) return Results.Json(new { directory = (string?)null, commits = Array.Empty<object>(), error = (string?)null });
        var trail = trails.For(room.Directory);
        var commits = await trail.LogAsync(TrailLength, cancellation);
        return Results.Json(new
        {
            directory = room.Directory,
            commits = commits.Select(c => new { c.Hash, c.Author, c.At, c.Subject }),
            error = trail.Reason,
        });
    }

    internal sealed record CreateRoomBody(string? Name, string? Directory);
    internal sealed record DirectoryBody(string? Directory);
}
