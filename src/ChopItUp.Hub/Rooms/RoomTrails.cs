using System.Collections.Concurrent;
using ChopItUp.Hub.Git;

namespace ChopItUp.Hub.Rooms;

/// <summary>One <see cref="GitTrail"/> per room directory, created on first use and kept for the hub's
/// life so the per-tree gate is shared by the spawner and the API. Keyed by the normalised path.</summary>
public sealed class RoomTrails(Func<string, GitTrail> factory)
{
    private readonly ConcurrentDictionary<string, GitTrail> _trails = new(StringComparer.OrdinalIgnoreCase);

    public GitTrail For(string directory) => _trails.GetOrAdd(RoomPaths.Normalize(directory), factory);

    /// <summary>The trail of a linked worktree of <paramref name="roomDirectory"/>, sharing the room
    /// trail's write gate (<see cref="GitTrail.WithRoot"/>).</summary>
    public GitTrail ForWorktree(string roomDirectory, string worktree) =>
        _trails.GetOrAdd(RoomPaths.Normalize(worktree), path => For(roomDirectory).WithRoot(path));

    public void Forget(string directory) => _trails.TryRemove(RoomPaths.Normalize(directory), out _);
}
