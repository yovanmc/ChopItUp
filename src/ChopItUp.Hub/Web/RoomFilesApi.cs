using ChopItUp.Core.Storage;
using ChopItUp.Hub.Rooms;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;

namespace ChopItUp.Hub.Web;

/// <summary>Caps how many room file requests open files at once. A room on a sleeping drive can hang
/// an open for a long time, and every hung open holds a thread.</summary>
public sealed class RoomFileGate
{
    public const int Permits = 16;
    public SemaphoreSlim Slots { get; } = new(Permits, Permits);
}

/// <summary><c>GET /api/rooms/{roomId}/files?ref=</c>: an image or video from the room's directory or
/// its artifacts folder, for the chat to show. Unauthenticated like every other GET, behind the
/// loopback host filter. The file is opened first and its real location read from the open handle,
/// so no link on the way can lead the route outside the room.</summary>
public static class RoomFilesApi
{
    public const long MaxImageBytes = 32L * 1024 * 1024;

    public static void MapRoomFilesApi(this WebApplication app) =>
        app.MapGroup("/api").MapGet("/rooms/{roomId}/files", Get);

    private static IResult Get(string roomId, [FromQuery(Name = "ref")] string? reference, HttpContext context,
        MessageStore store, RoomPathRules rules, RoomFileGate gate)
    {
        // On every answer, refusals included, so a page on another origin learns nothing from which
        // answer it got.
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        if (!gate.Slots.Wait(0)) return Busy(context, "too many room file requests at once; retry");
        try { return Serve(roomId, reference, context, store, rules); }
        finally { gate.Slots.Release(); }
    }

    private static IResult Serve(string roomId, string? reference, HttpContext context, MessageStore store, RoomPathRules rules)
    {
        if (store.GetRoom(roomId) is not { } room) return Refuse(404, $"Unknown room '{roomId}'.");
        if (room.Directory is null) return Refuse(404, $"Room '{roomId}' has no directory.");
        var parsed = RoomFileRef.Parse(reference);
        if (parsed.Error is not null) return Refuse(parsed.Status, parsed.Error);

        var root = parsed.Root == RoomFileRoot.Artifacts ? RoomArtifacts.FolderFor(room.Directory) : RoomPaths.Normalize(room.Directory);
        if (!Directory.Exists(root)) return Refuse(404, "The room has no such folder.");
        if (parsed.Root == RoomFileRoot.Artifacts && new DirectoryInfo(root).LinkTarget is not null)
            return Refuse(403, "The room's artifacts folder is a link.");
        string? rootFinal;
        using (var rootHandle = FinalPath.OpenDirectory(root, out _)) rootFinal = rootHandle is null ? null : FinalPath.Of(rootHandle);
        if (rootFinal is null) return Refuse(404, "The room folder is unreachable.");
        if (RoomPaths.RefusalOfNormalized(rootFinal, rules) is not null) return Refuse(403, "The room folder leads somewhere the hub does not serve.");

        var handle = FinalPath.OpenFile(Path.Combine(root, parsed.Relative.Replace('/', '\\')), out var error);
        if (handle is null)
            return error == FinalPath.SharingViolation ? Busy(context, "the file is open in another program; retry")
                : Refuse(404, error is FinalPath.FileNotFound or FinalPath.PathNotFound ? "No such file." : "Not a readable file.");
        SafeFileHandle? owned = handle;
        try
        {
            if (File.GetAttributes(handle).HasFlag(FileAttributes.ReparsePoint)) return Refuse(403, "The file is a link.");
            if (FinalPath.Of(handle) is not { } final || RoomPaths.Same(final, rootFinal) || !RoomPaths.IsUnderOrEqual(final, rootFinal))
                return Refuse(403, "The file is not inside the room.");

            var length = RandomAccess.GetLength(handle);
            var written = File.GetLastWriteTimeUtc(handle);
            var tag = new EntityTagHeaderValue($"\"{length:x}-{written.Ticks:x}\"");
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";

            if (parsed.IsVideo)
            {
                // The stream owns the handle from here and closes it when the response ends.
                owned = null;
                return Results.File(new FileStream(handle, FileAccess.Read), parsed.ContentType, lastModified: written, entityTag: tag, enableRangeProcessing: true);
            }
            if (length > MaxImageBytes) return Refuse(413, $"The image is over {MaxImageBytes / (1024 * 1024)} MB.");
            // Read whole and closed before the response, so a spawn can replace the file by rename
            // while the browser is still receiving it.
            var bytes = new byte[length];
            var read = 0;
            while (read < bytes.Length && RandomAccess.Read(handle, bytes.AsSpan(read), read) is var n and > 0) read += n;
            return Results.Bytes(bytes.AsMemory(0, read), parsed.ContentType, lastModified: written, entityTag: tag, enableRangeProcessing: true);
        }
        finally { owned?.Dispose(); }
    }

    private static IResult Busy(HttpContext context, string error)
    {
        context.Response.Headers.RetryAfter = "1";
        return Refuse(503, error);
    }

    private static IResult Refuse(int status, string error) => Results.Json(new { error }, statusCode: status);
}
