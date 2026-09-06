using ChopItUp.Core.Storage;

namespace ChopItUp.Hub.Rooms;

/// <summary>A refusal the owner reads verbatim; never a hub bug.</summary>
public sealed class RoomDirectoryException(string message) : Exception(message);

/// <summary>Turns what the owner typed into a room directory (M9 decisions 2–5): blank means a
/// hub-created folder under the rooms root; the path must pass <see cref="RoomPaths.Refusal"/>; it
/// may not sit inside, or contain, another room's directory; it is created when its parent exists;
/// and it ends up as the root of a git repository (initialised here when it is not one; refused when
/// it is inside someone else's repository, so the hub never commits into a tree it does not own).</summary>
public sealed class RoomDirectories(MessageStore store, RoomTrails trails, RoomPathRules rules, string roomsRoot)
{
    public string RoomsRoot { get; } = RoomPaths.Normalize(roomsRoot);

    public async Task<string> PrepareAsync(string roomId, string? typed, CancellationToken cancellation)
    {
        var hubCreated = string.IsNullOrWhiteSpace(typed);
        var target = hubCreated ? Path.Combine(RoomsRoot, roomId) : typed!.Trim();
        if (RoomPaths.Refusal(target, rules) is { } refused) throw new RoomDirectoryException(refused);
        var full = RoomPaths.Normalize(target);
        var trail = trails.For(full);
        if (!trail.IsAvailable()) throw new RoomDirectoryException("git was not found on PATH; a room directory needs git for its commit trail.");   // before anything touches disk

        foreach (var other in store.ListRooms(includeArchived: true))
        {
            if (other.Id == roomId || other.Directory is null) continue;
            var theirs = RoomPaths.Normalize(other.Directory);
            if (RoomPaths.IsUnderOrEqual(full, theirs) || RoomPaths.IsUnderOrEqual(theirs, full))
                throw new RoomDirectoryException($"'{full}' overlaps room '{other.Id}' ({theirs}); rooms cannot share or nest directories.");
        }

        if (!Directory.Exists(full))
        {
            if (hubCreated) Directory.CreateDirectory(RoomsRoot);
            var parent = Path.GetDirectoryName(full);
            if (parent is null || !Directory.Exists(parent))
                throw new RoomDirectoryException($"'{full}' does not exist and neither does its parent folder; create the parent first.");
            try { Directory.CreateDirectory(full); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new RoomDirectoryException($"'{full}' could not be created: {e.Message}");
            }
        }

        var top = await trail.TopLevelAsync(cancellation);
        if (top is not null && !RoomPaths.Same(RoomPaths.Normalize(top), full))
            throw new RoomDirectoryException($"'{full}' is inside the repository at '{top}'; a room directory must be a repository root.");
        if (top is null && !await trail.InitAsync(cancellation))
            throw new RoomDirectoryException($"git init failed in '{full}': {trail.Reason}");
        return full;
    }
}
