using System.IO;

namespace ChopItUp.Desktop.Tests;

/// <summary>Row 12 T3: a fresh temp dir per test, optionally seeded with an empty `ChopItUp.Hub.exe`
/// (the start path's `File.Exists` check). Never shared across tests: this assembly disables
/// parallelization (AssemblyInfo.cs).</summary>
internal static class TestDirs
{
    public static string New()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A fresh temp dir plus an empty file named ChopItUp.Hub.exe inside it.</summary>
    public static (string DataDir, string HubExe) NewWithHubExe()
    {
        var dir = New();
        var hubExe = Path.Combine(dir, "ChopItUp.Hub.exe");
        File.WriteAllBytes(hubExe, []);
        return (dir, hubExe);
    }
}
