using System.Collections.Concurrent;
using ChopItUp.Hub.Git;

namespace ChopItUp.Hub.Rooms;

/// <summary>One <see cref="GitTrail"/> per room directory, created on first use and kept for the hub's
/// life so the per-tree gate is shared by the spawner and the API. Keyed by the normalised path.</summary>
public sealed class RoomTrails(Func<string, GitTrail> factory)
{
    private readonly ConcurrentDictionary<string, GitTrail> _trails = new(StringComparer.OrdinalIgnoreCase);

    public GitTrail For(string directory) => _trails.GetOrAdd(RoomPaths.Normalize(directory), factory);
}
