namespace ChopItUp.Hub.Spawning;

/// <summary>D9: the run caps are hard code, not configuration — exactly the shape
/// <see cref="SpawnLimits.Default"/> already sets for exchanges, and just as unreachable from inside a
/// room. <see cref="Spawns"/>, <see cref="WallClock"/> and <see cref="PhaseEntries"/> are the three
/// HARD caps (AC8): a park naming one of them is always <c>capSpent: true</c>.
/// <see cref="SpawnTimeout"/> is the per-spawn wall clock while a run is active, replacing
/// <see cref="SpawnLimits.Timeout"/>'s 5 minutes for the duration of a run (AC8's last clause).</summary>
public sealed record RunLimits(int Spawns, TimeSpan WallClock, TimeSpan SpawnTimeout, int PhaseEntries)
{
    public static readonly RunLimits Default = new(
        Spawns: 80, WallClock: TimeSpan.FromHours(8),
        SpawnTimeout: TimeSpan.FromMinutes(30), PhaseEntries: 3);
}
