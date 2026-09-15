using ChopItUp.Desktop.Hub;

namespace ChopItUp.Desktop.Tests;

public class LogTailTests
{
    [Fact]
    public void Snapshot_is_empty_for_a_fresh_tail()
    {
        var tail = new LogTail(3);
        Assert.Empty(tail.Snapshot());
    }

    [Fact]
    public void Keeps_only_the_last_N_lines_in_order()
    {
        var tail = new LogTail(3);
        tail.Add("a");
        tail.Add("b");
        tail.Add("c");
        tail.Add("d");
        Assert.Equal(new[] { "b", "c", "d" }, tail.Snapshot());
    }
}
