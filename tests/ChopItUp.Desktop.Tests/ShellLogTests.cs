using System.IO;
using Xunit;

namespace ChopItUp.Desktop.Tests;

/// <summary>Row 12 review fix (B): desktop.log follows the same 5 MB rotation rule the plan (Task 3)
/// already applies to hub.log — rename to desktop.1.log, replacing any previous one, before appending.
/// The real 5 MB threshold is impractical to hit in a unit test, so the internal ctor takes an
/// injectable byte threshold (InternalsVisibleTo already covers this test project).</summary>
public sealed class ShellLogTests
{
    private static string NewLogPath()
    {
        var dir = TestDirs.New();
        return Path.Combine(dir, "desktop.log");
    }

    [Fact]
    public void Append_under_the_threshold_does_not_rotate()
    {
        var path = NewLogPath();
        var log = new ShellLog(path, rotateBytes: 1024);
        log.Append("short line");

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "desktop.1.log")));
    }

    [Fact]
    public void Append_past_the_threshold_rotates_before_appending()
    {
        var path = NewLogPath();
        var rotatedPath = Path.Combine(Path.GetDirectoryName(path)!, "desktop.1.log");
        var log = new ShellLog(path, rotateBytes: 100);

        // Push the file past the threshold first.
        log.Append(new string('x', 200));
        var sizeBeforeRotatingAppend = new FileInfo(path).Length;
        Assert.True(sizeBeforeRotatingAppend > 100);

        log.Append("this append should trigger rotation");

        Assert.True(File.Exists(rotatedPath));
        Assert.Equal(sizeBeforeRotatingAppend, new FileInfo(rotatedPath).Length);
        // The live file now holds only the line written after rotation.
        Assert.Contains("this append should trigger rotation", File.ReadAllText(path));
        Assert.DoesNotContain(new string('x', 200), File.ReadAllText(path));
    }

    [Fact]
    public void Rotation_replaces_a_previous_desktop_1_log()
    {
        var path = NewLogPath();
        var rotatedPath = Path.Combine(Path.GetDirectoryName(path)!, "desktop.1.log");
        File.WriteAllText(rotatedPath, "stale rotated content");
        var log = new ShellLog(path, rotateBytes: 100);

        log.Append(new string('y', 200));
        log.Append("triggers rotation");

        Assert.DoesNotContain("stale rotated content", File.ReadAllText(rotatedPath));
    }
}
