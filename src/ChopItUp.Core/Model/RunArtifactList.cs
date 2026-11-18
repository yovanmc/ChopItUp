namespace ChopItUp.Core.Model;

/// <summary>Keeps a run's artifact list readable: a frame sequence can record thousands of files, and
/// the list goes into every in-run prompt and every run snapshot.</summary>
public static class RunArtifactList
{
    public const int MaxShown = 60;

    /// <summary>Everything, in its given order, at <see cref="MaxShown"/> or fewer. Above that, the
    /// newest entries in path order and how many were left out.</summary>
    public static (IReadOnlyList<RunArtifact> Shown, int Omitted) Newest(IReadOnlyList<RunArtifact> all)
    {
        if (all.Count <= MaxShown) return (all, 0);
        var shown = all.OrderByDescending(a => a.At).ThenBy(a => a.Path, StringComparer.Ordinal).Take(MaxShown)
            .OrderBy(a => a.Path, StringComparer.Ordinal).ToList();
        return (shown, all.Count - MaxShown);
    }
}
