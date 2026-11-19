using ChopItUp.Core.Model;

namespace ChopItUp.Hub.Rooms;

public enum RoomFileRoot { Room, Artifacts }

/// <summary>A file the room file route may serve, or why it may not: <see cref="Status"/> is 400 for
/// a ref with no single safe spelling and 415 for a type the route does not serve.</summary>
public sealed record RoomFileRef(RoomFileRoot Root, string Relative, string ContentType, bool IsVideo, int Status, string? Error)
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
    };

    private static readonly HashSet<string> Devices = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
         .. Enumerable.Range(0, 10).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })],
        StringComparer.OrdinalIgnoreCase);

    /// <summary><c>artifact:&lt;path&gt;</c> names the room's artifacts folder, anything else the room
    /// directory. One leading <c>./</c> is dropped, as phase lines drop it.</summary>
    public static RoomFileRef Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return Refused(400, "ref is required: artifact:<path> or a path inside the room directory");
        var root = ArtifactRef.IsReference(value) ? RoomFileRoot.Artifacts : RoomFileRoot.Room;
        var path = (root == RoomFileRoot.Artifacts ? value[ArtifactRef.Prefix.Length..] : value).Replace('\\', '/');
        if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        if (!ArtifactRef.IsCanonicalRelative(path) || path.Split('/').Any(IsDevice))
            return Refused(400, $"ref '{value}' is not a plain relative path: no '..', ':', wildcard, device name, trailing dot or space, and no '~' before a digit");
        var extension = Path.GetExtension(path);
        if (!Types.TryGetValue(extension, out var type))
            return Refused(415, $"ref '{value}' is not a type the room file route serves (png, jpg, jpeg, gif, webp, mp4, webm)");
        return new(root, path, type, type.StartsWith("video/", StringComparison.Ordinal), 200, null);
    }

    private static bool IsDevice(string segment)
    {
        var dot = segment.IndexOf('.');
        return Devices.Contains((dot < 0 ? segment : segment[..dot]).Trim());
    }

    private static RoomFileRef Refused(int status, string error) => new(RoomFileRoot.Room, "", "", false, status, error);
}
