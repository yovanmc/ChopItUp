using System.Diagnostics;
using ChopItUp.Hub.Rooms;

namespace ChopItUp.Hub.Tests.Rooms;

public sealed class RoomPathsTests
{
    // A fabricated machine: nothing here reads the real profile or environment.
    private static readonly RoomPathRules Rules = RoomPathRules.ForHub(
        dataDir: @"C:\hub\data", installDir: @"C:\hub\app", userProfile: @"C:\Users\me",
        getEnv: name => name switch
        {
            "SystemRoot" => @"C:\Windows",
            "ProgramFiles" => @"C:\Program Files",
            "ProgramFiles(x86)" => @"C:\Program Files (x86)",
            "ProgramData" => @"C:\ProgramData",
            _ => null,
        });

    [Theory]
    [InlineData(@"C:\", "drive root")]
    [InlineData(@"C:\Users\me\..\..", "drive root")]
    [InlineData(@"D:", "absolute local path")]
    [InlineData(@"relative\path", "absolute local path")]
    [InlineData(@"\\server\share\x", "Network and device paths")]
    [InlineData(@"\\?\C:\x", "Network and device paths")]
    [InlineData("", "Directory is empty")]
    [InlineData(@"C:\Users\me", "user profile folder")]
    [InlineData(@"c:\users\ME\", "user profile folder")]
    [InlineData(@"C:\Self Apps\ChopItUp\data", @"C:\Self Apps")]
    [InlineData(@"C:\Self Apps", @"C:\Self Apps")]
    [InlineData(@"C:\hub\data\rooms\x", "the hub's data folder")]
    [InlineData(@"C:\hub\data", "the hub's data folder")]
    [InlineData(@"C:\hub\app\wwwroot", "the hub's install folder")]
    [InlineData(@"C:\Users\me\.ssh", "a credential folder (.ssh)")]
    [InlineData(@"C:\Users\me\.codex\memories", "a credential folder (.codex)")]
    [InlineData(@"C:\Users\me\.claude\projects\x", "a credential folder (.claude)")]
    [InlineData(@"C:\Windows\Temp", "a Windows system folder (SystemRoot)")]
    [InlineData(@"C:\Program Files\x", "a Windows system folder (ProgramFiles)")]
    [InlineData(@"C:\Program Files (x86)\x", "a Windows system folder (ProgramFiles(x86))")]
    [InlineData(@"C:\ProgramData\x", "a Windows system folder (ProgramData)")]
    public void M9_A2_refused_paths_name_the_reason(string typed, string reasonFragment)
    {
        var refusal = RoomPaths.Refusal(typed, Rules);
        Assert.NotNull(refusal);
        Assert.Contains(reasonFragment, refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Users\me\ChopItUp\rooms\lab")]
    [InlineData(@"C:\Users\me\Documents\resume")]
    [InlineData(@"C:\Agent Projects\thing")]
    [InlineData(@"C:\hub\data2")]                 // a sibling of the data folder, not inside it
    [InlineData(@"C:\hub\application")]           // shares a prefix with C:\hub\app, is not under it
    [InlineData("C:/Users/me/proj/")]             // forward slashes and a trailing slash normalise
    [InlineData(@"C:\Self Apps2\x")]              // prefix, not subtree
    public void M9_A2_accepted_paths_return_null(string typed) =>
        Assert.Null(RoomPaths.Refusal(typed, Rules));

    [Theory]
    [InlineData(@"C:\foo\bar\", @"C:\foo\bar")]
    [InlineData(@"C:\foo\bar", @"C:\foo\bar")]
    [InlineData("C:/foo/bar/", @"C:\foo\bar")]
    [InlineData(@"C:\foo\..\bar", @"C:\bar")]
    [InlineData(@"C:\", @"C:\")]
    public void M9_A2_normalize_drops_trailing_separators_except_on_a_drive_root(string input, string expected) =>
        Assert.Equal(expected, RoomPaths.Normalize(input));

    [Fact]
    public void M9_A2_is_under_or_equal_is_case_insensitive_and_boundary_aware()
    {
        Assert.True(RoomPaths.IsUnderOrEqual(@"C:\HUB\Data\x", @"C:\hub\data"));
        Assert.True(RoomPaths.IsUnderOrEqual(@"C:\hub\data", @"C:\hub\data"));
        Assert.False(RoomPaths.IsUnderOrEqual(@"C:\hub\data2", @"C:\hub\data"));
        Assert.True(RoomPaths.IsUnderOrEqual(@"C:\x", @"C:\"));
    }

    [Fact]
    public void M9_A2_a_junction_into_a_refused_place_is_refused_by_its_target_whether_it_is_the_leaf_or_an_ancestor()
    {
        Assert.True(OperatingSystem.IsWindows());
        var root = Path.Combine(Path.GetTempPath(), "chopitup_paths_" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var inner = Path.Combine(data, "inner");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(inner);
        try
        {
            using var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{inner}\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            mk.WaitForExit();
            Assert.Equal(0, mk.ExitCode);

            var rules = RoomPathRules.ForHub(data, Path.Combine(root, "app"), Path.Combine(root, "profile"), _ => null);
            var refusal = RoomPaths.Refusal(link, rules);                            // the leaf is the link
            Assert.NotNull(refusal);
            Assert.Contains("is a link to", refusal);
            Assert.Contains("the hub's data folder", refusal);
            var below = RoomPaths.Refusal(Path.Combine(link, "sub", "deeper"), rules);   // an ancestor is the link; the leaf does not exist yet
            Assert.NotNull(below);
            Assert.Contains("is a link to", below);
            Assert.Contains("the hub's data folder", below);
            Assert.Equal(Path.Combine(inner, "sub", "deeper"), RoomPaths.ResolveLinks(Path.Combine(link, "sub", "deeper")));
            Assert.Null(RoomPaths.Refusal(Path.Combine(root, "plain"), rules));   // a sibling that is not a link, existing or not
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);   // removes the junction, not its target
            Directory.Delete(root, recursive: true);
        }
    }
}
