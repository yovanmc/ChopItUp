using ChopItUp.Core.Model;
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

    public const int MaxListed = 500;

    /// <summary>How long a listing waits on the folder walk. A room on a sleeping drive must not hold
    /// a gate permit for longer.</summary>
    public static readonly TimeSpan ListLimit = TimeSpan.FromSeconds(5);

    public static void MapRoomFilesApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/rooms/{roomId}/files", Get);
        api.MapGet("/rooms/{roomId}/artifacts", List);
    }

    /// <summary>On every answer, refusals included, so a page on another origin learns nothing from
    /// which answer it got.</summary>
    private static void Guard(HttpContext context)
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    }

    private static IResult Get(string roomId, [FromQuery(Name = "ref")] string? reference, HttpContext context,
        MessageStore store, RoomPathRules rules, RoomFileGate gate)
    {
        Guard(context);
        if (!gate.Slots.Wait(0)) return Busy(context, "too many room file requests at once; retry");
        try { return Serve(roomId, reference, context, store, rules); }
        finally { gate.Slots.Release(); }
    }

    public sealed record ListedFile(string Ref, string Name, long Bytes, DateTime Modified);

    /// <summary><c>GET /api/rooms/{roomId}/artifacts</c>: every file the file route would serve from
    /// the room's artifacts folder, newest first, for the room gallery.</summary>
    private static async Task<IResult> List(string roomId, HttpContext context, MessageStore store, RoomPathRules rules, RoomFileGate gate)
    {
        Guard(context);
        if (!gate.Slots.Wait(0)) return Busy(context, "too many room file requests at once; retry");
        try
        {
            if (!OperatingSystem.IsWindows()) return Refuse(404, "Room files are served on Windows only.");
            if (store.GetRoom(roomId) is not { } room) return Refuse(404, $"Unknown room '{roomId}'.");
            if (room.Directory is null) return Refuse(404, $"Room '{roomId}' has no directory.");
            string root;
            switch (RoomFileRoots.Resolve(room.Directory, RoomFileRoot.Artifacts, rules))
            {
                case RootOutcome.Ok ok: root = ok.Path; break;
                case RootOutcome.Refused refused: return Refuse(refused.Status, refused.Error);
                default: return Results.Json(new { files = Array.Empty<ListedFile>(), omitted = 0, unreadable = 0 });
            }

            var walk = Task.Run(() => RoomArtifacts.Scan(root, keepReparse: f => OperatingSystem.IsWindows() && RoomFileRoots.IsPlainFile(f)));
            if (await Task.WhenAny(walk, Task.Delay(ListLimit)) != walk)
                return Busy(context, $"the artifacts folder did not answer within {ListLimit.TotalSeconds:0} s; retry");
            var scan = await walk;
            if (scan.Faulted) return Busy(context, "the artifacts folder could not be read; retry");

            var served = scan.Files
                .Where(f => RoomFileRef.Parse(ArtifactRef.Prefix + f.Key).Error is null)
                .OrderByDescending(f => f.Value.WrittenUtc).ThenBy(f => f.Key, StringComparer.Ordinal)
                .ToList();
            var files = served.Take(MaxListed)
                .Select(f => new ListedFile(ArtifactRef.Prefix + f.Key, f.Key[(f.Key.LastIndexOf('/') + 1)..], f.Value.Length, f.Value.WrittenUtc))
                .ToList();
            return Results.Json(new { files, omitted = served.Count - files.Count, unreadable = scan.Unreadable.Count });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Busy(context, "the artifacts folder could not be read; retry");
        }
        finally { gate.Slots.Release(); }
    }

    /// <summary>A file a lock, a dropped drive or a denied read stops midway still gets an answer with
    /// the guard headers, never a bare 500.</summary>
    private static IResult Serve(string roomId, string? reference, HttpContext context, MessageStore store, RoomPathRules rules)
    {
        try { return ServeCore(roomId, reference, context, store, rules); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (e.HResult & 0xFFFF) is FinalPath.SharingViolation or FinalPath.LockViolation
                ? Busy(context, "the file is open in another program; retry")
                : Refuse(404, "Not a readable file.");
        }
    }

    private static IResult ServeCore(string roomId, string? reference, HttpContext context, MessageStore store, RoomPathRules rules)
    {
        // The fence reads the final path from a Win32 handle, so there is no fence anywhere else.
        if (!OperatingSystem.IsWindows()) return Refuse(404, "Room files are served on Windows only.");
        if (store.GetRoom(roomId) is not { } room) return Refuse(404, $"Unknown room '{roomId}'.");
        if (room.Directory is null) return Refuse(404, $"Room '{roomId}' has no directory.");
        var parsed = RoomFileRef.Parse(reference);
        if (parsed.Error is not null) return Refuse(parsed.Status, parsed.Error);

        string root, rootFinal;
        switch (RoomFileRoots.Resolve(room.Directory, parsed.Root, rules))
        {
            case RootOutcome.Ok ok: (root, rootFinal) = (ok.Path, ok.Final); break;
            case RootOutcome.Refused refused: return Refuse(refused.Status, refused.Error);
            default: return Refuse(404, "The room has no such folder.");
        }

        var path = Path.Combine(root, parsed.Relative.Replace('/', '\\'));
        var handle = FinalPath.OpenFile(path, followLinks: false, out var error);
        if (handle is not null && FinalPath.ReparseTag(handle) is var reparse and not 0)
        {
            handle.Dispose();
            if (reparse is null) return Refuse(404, "Not a readable file.");
            if (FinalPath.IsLink(reparse.Value)) return Refuse(403, "The file is a link.");
            // A placeholder, compressed or deduplicated file is still the file itself, and only a
            // normal open lets its file system filter hand over the content.
            handle = FinalPath.OpenFile(path, followLinks: true, out error);
        }
        if (handle is null)
            return error == FinalPath.SharingViolation ? Busy(context, "the file is open in another program; retry")
                : Refuse(404, error is FinalPath.FileNotFound or FinalPath.PathNotFound ? "No such file." : "Not a readable file.");
        SafeFileHandle? owned = handle;
        try
        {
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
