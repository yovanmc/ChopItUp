using System.Runtime.Versioning;

namespace ChopItUp.Hub.Rooms;

/// <summary>Where a room file root is, or why the room file routes may not use it.</summary>
public abstract record RootOutcome
{
    public sealed record Ok(string Path, string Final) : RootOutcome;
    public sealed record Missing : RootOutcome;
    public sealed record Refused(int Status, string Error) : RootOutcome;
}

/// <summary>Resolves a room's directory or artifacts folder for the routes that serve or list room
/// files, so both answer the same for one root.</summary>
public static class RoomFileRoots
{
    [SupportedOSPlatform("windows")]
    public static RootOutcome Resolve(string roomDirectory, RoomFileRoot which, RoomPathRules rules)
    {
        var root = which == RoomFileRoot.Artifacts ? RoomArtifacts.FolderFor(roomDirectory) : RoomPaths.Normalize(roomDirectory);
        if (!Directory.Exists(root)) return new RootOutcome.Missing();
        if (which == RoomFileRoot.Artifacts && new DirectoryInfo(root).LinkTarget is not null)
            return new RootOutcome.Refused(403, "The room's artifacts folder is a link.");
        if (FinalPath.OfDirectory(root) is not { } final) return new RootOutcome.Refused(404, "The room folder is unreachable.");
        if (RoomPaths.RefusalOfNormalized(final, rules) is not null)
            return new RootOutcome.Refused(403, "The room folder leads somewhere the hub does not serve.");
        return new RootOutcome.Ok(root, final);
    }

    /// <summary>Whether a reparse-point file is the file itself (a placeholder, compressed or
    /// deduplicated file) rather than a link, the same test the file route applies.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsPlainFile(FileInfo file)
    {
        using var handle = FinalPath.OpenFile(file.FullName, followLinks: false, out _);
        return handle is not null && FinalPath.ReparseTag(handle) is { } tag && !FinalPath.IsLink(tag);
    }
}
