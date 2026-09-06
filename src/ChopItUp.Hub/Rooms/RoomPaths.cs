using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Rooms;

public sealed record RefusedSubtree(string Path, string Reason);

/// <summary>What a room directory may not be (D12, M9 plan decision 3): the profile folder itself, and
/// anything under the listed subtrees. Built once per hub from the data dir, the install dir, the
/// profile and four environment folders; pure string rules after that.</summary>
public sealed record RoomPathRules(string UserProfile, IReadOnlyList<RefusedSubtree> Subtrees)
{
    public const string SelfApps = @"C:\Self Apps";

    public static RoomPathRules ForHub(string dataDir) =>
        ForHub(dataDir, AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable);

    public static RoomPathRules ForHub(string dataDir, string installDir, string userProfile, Func<string, string?> getEnv)
    {
        var profile = RoomPaths.Normalize(userProfile);
        var subtrees = new List<RefusedSubtree>
        {
            new(RoomPaths.Normalize(dataDir), "the hub's data folder"),
            new(RoomPaths.Normalize(installDir), "the hub's install folder"),
            new(SelfApps, @"C:\Self Apps (installed apps and their data)"),
        };
        foreach (var name in new[] { "SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "ProgramData" })
            if (getEnv(name) is { Length: > 0 } value) subtrees.Add(new(RoomPaths.Normalize(value), $"a Windows system folder ({name})"));
        foreach (var folder in SpawnCommands.CredentialFolders)
            subtrees.Add(new(Path.Combine(profile, folder), $"a credential folder ({folder})"));
        return new(profile, subtrees);
    }
}

/// <summary>Path shape, normalisation and the refusal sentence. Comparisons are case-insensitive on
/// normalised full paths with no trailing separator (a drive root keeps its one backslash).</summary>
public static class RoomPaths
{
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
    }

    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static bool IsUnderOrEqual(string path, string root)
    {
        if (Same(path, root)) return true;
        var prefix = root.EndsWith('\\') ? root : root + '\\';
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Null when the typed path may be a room directory; otherwise the sentence the owner sees.
    /// Checks the shape, then the normalised path against the rules, then the path with every existing
    /// junction or symbolic link on it resolved (the leaf or any ancestor: `Path.GetFullPath` resolves
    /// neither) against the same rules.</summary>
    public static string? Refusal(string? typed, RoomPathRules rules)
    {
        var t = (typed ?? "").Trim();
        if (t.Length == 0) return "Directory is empty.";
        if (t.StartsWith(@"\\", StringComparison.Ordinal) || t.StartsWith("//", StringComparison.Ordinal))
            return @"Network and device paths (\\server\share, \\?\...) cannot be room directories.";
        if (t.Length < 3 || !char.IsAsciiLetter(t[0]) || t[1] != ':' || (t[2] != '\\' && t[2] != '/'))
            return @"The directory must be an absolute local path such as C:\Projects\thing.";
        string full;
        try { full = Normalize(t); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return "The directory is not a valid Windows path.";
        }
        if (RefusalOfNormalized(full, rules) is { } refused) return refused;
        var resolved = ResolveLinks(full);
        if (!Same(resolved, full) && RefusalOfNormalized(resolved, rules) is { } viaLink)
            return $"'{full}' is a link to '{resolved}': {viaLink}";
        return null;
    }

    /// <summary>The path with each existing segment that is a junction or symbolic link replaced by its
    /// final target, walking from the drive root; segments that do not exist yet are appended as typed.
    /// A link that cannot be resolved (a loop, no access) is left as is — the refusal then rests on the
    /// typed path, which is the conservative side only when the target is outside the refused set, so
    /// <see cref="RoomDirectories"/> also refuses a folder whose git top level differs from itself.</summary>
    public static string ResolveLinks(string full)
    {
        var root = Path.GetPathRoot(full) ?? full;
        var segments = full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (int i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            if (!Directory.Exists(current))
                return i + 1 < segments.Length ? Path.Combine(current, Path.Combine(segments[(i + 1)..])) : current;
            if (LinkTarget(current) is { } target) current = Normalize(target);
        }
        return Normalize(current);
    }

    public static string? RefusalOfNormalized(string full, RoomPathRules rules)
    {
        if (Same(full, Path.GetPathRoot(full) ?? full)) return $"'{full}' is a drive root; use a folder on the drive.";
        if (Same(full, rules.UserProfile)) return $"'{full}' is your user profile folder; use a folder inside it.";
        foreach (var s in rules.Subtrees)
            if (IsUnderOrEqual(full, s.Path)) return $"'{full}' is refused: it is {s.Reason}, or inside it.";
        return null;
    }

    /// <summary>The final target when the folder exists and is a junction or symbolic link; null otherwise.</summary>
    public static string? LinkTarget(string full)
    {
        try
        {
            var info = new DirectoryInfo(full);
            if (!info.Exists || info.LinkTarget is null) return null;
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
