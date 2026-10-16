using System.IO;
using ChopItUp.Desktop.Hub;

namespace ChopItUp.Desktop.Tests;

public class HubProbeTests
{
    [Fact]
    public void LockIsHeld_is_false_when_the_lock_file_is_absent()
    {
        var dir = TestDirs.New();
        Assert.False(HubProbe.LockIsHeld(dir));
    }

    [Fact]
    public void LockIsHeld_is_true_while_another_handle_holds_it_exclusively()
    {
        var dir = TestDirs.New();
        var path = Path.Combine(dir, "hub.lock");
        using var held = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.True(HubProbe.LockIsHeld(dir));
    }

    [Fact]
    public void LockIsHeld_is_false_once_released()
    {
        var dir = TestDirs.New();
        var path = Path.Combine(dir, "hub.lock");
        using (new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.False(HubProbe.LockIsHeld(dir));
    }

    [Theory]
    [InlineData("8795", 8795)]
    [InlineData("x", null)]
    public void ReadPort_parses_a_valid_port_and_is_null_for_unparsable_content(string content, int? expected)
    {
        var dir = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, "hub.port"), content);
        Assert.Equal(expected, HubProbe.ReadPort(dir));
    }

    [Fact]
    public void ReadPort_is_null_when_the_file_is_missing()
    {
        var dir = TestDirs.New();
        Assert.Null(HubProbe.ReadPort(dir));
    }
}
