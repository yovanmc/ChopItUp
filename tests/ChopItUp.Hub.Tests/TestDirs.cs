namespace ChopItUp.Hub.Tests;

/// <summary>Deletes a temp tree that may hold a git repository: git writes its objects read-only, and
/// <c>Directory.Delete(recursive: true)</c> throws <see cref="UnauthorizedAccessException"/> on the
/// first one. Attributes are cleared first, then the tree goes.</summary>
public static class TestDirs
{
    public static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }
}
