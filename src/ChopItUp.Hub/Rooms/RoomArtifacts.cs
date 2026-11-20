using ChopItUp.Core.Storage;

namespace ChopItUp.Hub.Rooms;

/// <summary>One file as a scan saw it. Two scans that agree on both values saw the same content.</summary>
public sealed record ArtifactFile(long Length, DateTime WrittenUtc);

/// <summary>What one walk of an artifacts folder saw. Paths are relative with forward slashes.
/// <see cref="Unreadable"/> names folders whose content is unknown, and <see cref="Faulted"/> says the
/// folder itself could not be read.</summary>
public sealed record ArtifactScan(IReadOnlyDictionary<string, ArtifactFile> Files, IReadOnlyList<string> Unreadable, bool Faulted);

/// <summary>The folder beside a room's directory where spawns put renders and other generated files.
/// It sits outside every repository, so the hub's after-spawn commit never takes them, and outside the
/// exchange worktrees, so they outlast an exchange.</summary>
public sealed class RoomArtifacts(MessageStore store, RoomPathRules rules)
{
    /// <summary>How long a launch waits on the folder check. A room on a sleeping or disconnected
    /// drive must not hold its spawn for longer.</summary>
    public static readonly TimeSpan CheckLimit = TimeSpan.FromSeconds(5);

    /// <summary>Background job output lives here and outlasts the spawn that started it, so a before
    /// and after scan cannot name its author.</summary>
    public const string JobsFolder = "jobs";

    private static readonly EnumerationOptions OneLevel = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };

    /// <summary><see cref="Path"/> is the usable folder. Otherwise <see cref="Refusal"/> says why the
    /// room has none for this launch.</summary>
    public sealed record Lease(string? Path, string? Refusal);

    public static string FolderFor(string roomDirectory) => RoomPaths.Normalize(roomDirectory) + ".artifacts";

    /// <summary>Creates or confirms the room's folder. Never throws: a room whose folder cannot be
    /// used still runs its spawns, so every fault becomes a refusal.</summary>
    public Lease Ensure(string roomDirectory)
    {
        try { return EnsureCore(FolderFor(roomDirectory)); }
        catch (Exception e) when (e is not OperationCanceledException) { return new(null, e.Message); }
    }

    private Lease EnsureCore(string path)
    {
        if (RoomPaths.Refusal(path, rules) is { } refused) return new(null, refused);
        foreach (var other in store.ListRooms(includeArchived: true))
        {
            if (other.Directory is null) continue;
            var theirs = RoomPaths.Normalize(other.Directory);
            if (RoomPaths.IsUnderOrEqual(path, theirs) || RoomPaths.IsUnderOrEqual(theirs, path))
                return new(null, $"'{path}' overlaps room '{other.Id}'");
            if (RoomPaths.IsUnderOrEqual(path, ExchangeWorktrees.FolderFor(theirs)))
                return new(null, $"'{path}' is inside room '{other.Id}''s exchange worktrees");
        }
        if (File.Exists(path)) return new(null, $"'{path}' is a file");
        if (new DirectoryInfo(path).LinkTarget is not null) return new(null, $"'{path}' is a link");
        Directory.CreateDirectory(path);
        return new(path, null);
    }

    /// <summary>Runs <paramref name="ensure"/> off the caller's thread and gives up after
    /// <paramref name="limit"/>.</summary>
    public static async Task<Lease> WithinAsync(Func<Lease> ensure, TimeSpan limit)
    {
        var check = Task.Run(ensure);
        if (await Task.WhenAny(check, Task.Delay(limit)) == check) return await check;
        return new(null, $"the folder did not answer within {limit.TotalSeconds:0} s");
    }

    /// <summary>Walks <paramref name="folder"/> without following links and, with
    /// <paramref name="skipJobs"/>, without entering the top level <see cref="JobsFolder"/> (a job's own
    /// folder is walked whole). <paramref name="list"/> replaces the directory listing in tests.
    /// <paramref name="keepReparse"/> keeps a reparse-point file it accepts; without it every reparse
    /// point is skipped.</summary>
    public static ArtifactScan Scan(string folder, Func<string, IEnumerable<FileSystemInfo>>? list = null, Func<FileInfo, bool>? keepReparse = null, bool skipJobs = true)
    {
        list ??= directory => new DirectoryInfo(directory).EnumerateFileSystemInfos("*", OneLevel);
        var files = new Dictionary<string, ArtifactFile>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();
        var pending = new Stack<(string Full, string Relative)>();
        pending.Push((folder, ""));
        while (pending.TryPop(out var current))
        {
            List<FileSystemInfo> entries;
            try { entries = list(current.Full).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (current.Relative.Length == 0) return new(files, unreadable, Faulted: true);
                unreadable.Add(current.Relative);
                continue;
            }
            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && !(entry is FileInfo kept && keepReparse?.Invoke(kept) == true)) continue;
                var relative = current.Relative.Length == 0 ? entry.Name : current.Relative + "/" + entry.Name;
                if (entry is FileInfo file) files[relative] = new ArtifactFile(file.Length, file.LastWriteTimeUtc);
                else if (!skipJobs || !relative.Equals(JobsFolder, StringComparison.OrdinalIgnoreCase)) pending.Push((entry.FullName, relative));
            }
        }
        return new(files, unreadable, Faulted: false);
    }

    /// <summary>Files new or different in <paramref name="after"/>. Recording an author overwrites the
    /// previous one for good, so a scan that could not see a file claims nothing about it.</summary>
    public static IReadOnlyList<string> Changed(ArtifactScan before, ArtifactScan after)
    {
        if (before.Faulted || after.Faulted) return [];
        var unknown = before.Unreadable.Concat(after.Unreadable).Select(d => d + "/").ToList();
        return after.Files
            .Where(f => !before.Files.TryGetValue(f.Key, out var old) || old != f.Value)
            .Select(f => f.Key)
            .Where(path => !unknown.Any(d => path.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Whether <paramref name="relative"/> is a real file reached through no link. A scan
    /// never records a file behind a link, so such a file has no author the critique rule could check.</summary>
    public static bool ReferenceExists(string folder, string relative)
    {
        try
        {
            var current = folder;
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
            foreach (var segment in relative.Split('/'))
            {
                current = Path.Combine(current, segment);
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
            }
            return File.Exists(current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
}
