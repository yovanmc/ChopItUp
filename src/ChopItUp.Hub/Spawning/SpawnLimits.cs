namespace ChopItUp.Hub.Spawning;

/// <summary>D7: the caps are hard code, not configuration. <see cref="Default"/> is the only
/// instance the hub ever constructs; tests build smaller ones through the DI seam in
/// <c>HubHost.Build</c>. Budget = model turns per exchange (D5, row 44: 8; a turns: token in the
/// leading run overrides it up to ExchangeCommands.MaxTurns); Debounce = how long after the
/// last triggering message the hub waits before launching, so one burst is one spawn (D8);
/// MinSpacing = gap between two launches of the same participant, across rooms (D7);
/// Timeout = wall clock per plain-room spawn, then the tree is killed (D7); OutsideDirectoryTimeout
/// extends only directory-room spawns outside a run (M57). Test limits that omit it keep their short
/// Timeout in both room shapes. Transcript* = the prompt window (decision 6).</summary>
public sealed record SpawnLimits(int Budget, TimeSpan Debounce, TimeSpan MinSpacing, TimeSpan Timeout, int TranscriptMessages, int TranscriptChars, TimeSpan? OutsideDirectoryTimeout = null)
{
    public TimeSpan EffectiveOutsideDirectoryTimeout => OutsideDirectoryTimeout ?? Timeout;

    public static readonly SpawnLimits Default = new(
        Budget: 8,
        Debounce: TimeSpan.FromSeconds(2),
        MinSpacing: TimeSpan.FromSeconds(10),
        Timeout: TimeSpan.FromMinutes(5),
        TranscriptMessages: 60,
        TranscriptChars: 24_000,
        OutsideDirectoryTimeout: TimeSpan.FromMinutes(30));
}
