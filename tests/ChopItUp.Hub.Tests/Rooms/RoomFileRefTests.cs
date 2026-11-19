using ChopItUp.Hub.Rooms;

namespace ChopItUp.Hub.Tests.Rooms;

public sealed class RoomFileRefTests
{
    [Theory]
    [InlineData("artifact:renders/a.png", RoomFileRoot.Artifacts, "renders/a.png", "image/png")]
    [InlineData("ARTIFACT:a.PNG", RoomFileRoot.Artifacts, "a.PNG", "image/png")]
    [InlineData("artifact:./a.png", RoomFileRoot.Artifacts, "a.png", "image/png")]
    [InlineData("./docs/b.png", RoomFileRoot.Room, "docs/b.png", "image/png")]
    [InlineData(@"docs\b.png", RoomFileRoot.Room, "docs/b.png", "image/png")]
    [InlineData("a.jpg", RoomFileRoot.Room, "a.jpg", "image/jpeg")]
    [InlineData("a.JPEG", RoomFileRoot.Room, "a.JPEG", "image/jpeg")]
    [InlineData("a.gif", RoomFileRoot.Room, "a.gif", "image/gif")]
    [InlineData("a.webp", RoomFileRoot.Room, "a.webp", "image/webp")]
    [InlineData("artifact:clip.mp4", RoomFileRoot.Artifacts, "clip.mp4", "video/mp4")]
    [InlineData("clip.webm", RoomFileRoot.Room, "clip.webm", "video/webm")]
    [InlineData("console.png", RoomFileRoot.Room, "console.png", "image/png")]   // a device name only as a whole stem
    public void A_served_ref_names_its_root_path_and_type(string value, RoomFileRoot root, string relative, string type)
    {
        var parsed = RoomFileRef.Parse(value);
        Assert.Null(parsed.Error);
        Assert.Equal(root, parsed.Root);
        Assert.Equal(relative, parsed.Relative);
        Assert.Equal(type, parsed.ContentType);
        Assert.Equal(type.StartsWith("video/", StringComparison.Ordinal), parsed.IsVideo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("artifact:")]
    [InlineData("../x.png")]
    [InlineData("artifact:../x.png")]
    [InlineData(@"sub\..\..\x.png")]
    [InlineData("/docs/b.png")]
    [InlineData(@"\docs\b.png")]
    [InlineData(@"C:\x.png")]
    [InlineData("x.txt:y.png")]                 // an alternate data stream
    [InlineData("docs/b.png:hidden")]
    [InlineData("CON.png")]
    [InlineData("docs/nul.png")]
    [InlineData("COM1.jpg")]
    [InlineData("CONIN$.png")]
    [InlineData("LPT¹.png")]
    [InlineData("aux .png")]                    // trimmed before the device check
    [InlineData("DOCS~1/b.png")]                // an 8.3 short name
    [InlineData("x.png.")]
    [InlineData("x.png ")]
    [InlineData("a//b.png")]
    [InlineData("a/./b.png")]
    public void A_ref_with_no_single_safe_spelling_is_refused_as_bad_shape(string? value)
    {
        var parsed = RoomFileRef.Parse(value);
        Assert.Equal(400, parsed.Status);
        Assert.NotNull(parsed.Error);
    }

    [Theory]
    [InlineData("x.svg")]
    [InlineData("x.html")]
    [InlineData("x.txt")]
    [InlineData("x")]
    [InlineData("artifact:x.svg")]
    public void A_ref_to_a_type_the_route_does_not_serve_is_refused_as_unsupported(string value)
    {
        var parsed = RoomFileRef.Parse(value);
        Assert.Equal(415, parsed.Status);
        Assert.NotNull(parsed.Error);
    }
}
