using Xunit;

namespace ChopItUp.Desktop.Tests;

/// <summary>Row 12 T5 (B6): the named mutex + two named events a second launch signals. Every test uses
/// its own key (a fresh Guid) so tests never collide with each other or with a real shell running on
/// this machine; the assembly disables parallelization (AssemblyInfo.cs) so within-process races are
/// the only ones that matter.</summary>
public sealed class SingleInstanceTests
{
    private static string NewKey() => "ChopItUp.Desktop.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void Second_TryBecomePrimary_on_a_key_is_false()
    {
        var key = NewKey();
        using var first = SingleInstance.TryBecomePrimary(key);
        Assert.NotNull(first);

        var second = SingleInstance.TryBecomePrimary(key);
        Assert.Null(second);
    }

    [Fact]
    public void Signal_without_a_primary_is_false()
    {
        var key = NewKey();
        Assert.False(SingleInstance.Signal(key, ShellCommand.Show));
    }

    [Fact]
    public void A_primary_observes_show()
    {
        var key = NewKey();
        using var primary = SingleInstance.TryBecomePrimary(key);
        Assert.NotNull(primary);

        using var showSeen = new ManualResetEventSlim(false);
        primary!.Listen(onShow: () => showSeen.Set(), onQuit: () => { });

        Assert.True(SingleInstance.Signal(key, ShellCommand.Show));
        Assert.True(showSeen.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Dispose_releases_the_mutex_so_a_later_TryBecomePrimary_succeeds()
    {
        var key = NewKey();
        var first = SingleInstance.TryBecomePrimary(key);
        Assert.NotNull(first);
        first!.Dispose();

        using var second = SingleInstance.TryBecomePrimary(key);
        Assert.NotNull(second);
    }
}
