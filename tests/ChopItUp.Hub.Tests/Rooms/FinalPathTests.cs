using ChopItUp.Hub.Rooms;

namespace ChopItUp.Hub.Tests.Rooms;

public sealed class FinalPathTests
{
    [Theory]
    [InlineData(0xA000000Cu, true)]     // symbolic link
    [InlineData(0xA0000003u, true)]     // mount point (junction)
    [InlineData(0x9000001Au, false)]    // OneDrive Files On-Demand placeholder
    [InlineData(0x80000017u, false)]    // WOF compressed file
    [InlineData(0x80000013u, false)]    // deduplicated file
    public void Only_a_tag_that_stands_for_another_name_counts_as_a_link(uint tag, bool isLink) =>
        Assert.Equal(isLink, FinalPath.IsLink(tag));
}
