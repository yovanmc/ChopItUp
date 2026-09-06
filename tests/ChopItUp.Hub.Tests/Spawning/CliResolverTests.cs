using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed class CliResolverTests : IDisposable
{
    private readonly string _dirA = Path.Combine(Path.GetTempPath(), "chopitup_cli_" + Guid.NewGuid().ToString("N"));
    private readonly string _dirB = Path.Combine(Path.GetTempPath(), "chopitup_cli_" + Guid.NewGuid().ToString("N"));

    public CliResolverTests()
    {
        Directory.CreateDirectory(_dirA);
        Directory.CreateDirectory(_dirB);
    }

    private string PathOf(params string[] dirs) => string.Join(Path.PathSeparator, dirs);

    [Fact]
    public void A_real_exe_is_run_directly()
    {
        File.WriteAllText(Path.Combine(_dirA, "tool.exe"), "");
        var r = CliResolver.Resolve("tool", PathOf(_dirA, _dirB));
        Assert.Equal(Path.Combine(_dirA, "tool.exe"), r.FileName);
        Assert.Empty(r.LeadingArguments);
    }

    [Fact]
    public void A_cmd_shim_goes_through_cmd_exe_with_d_and_c()
    {
        File.WriteAllText(Path.Combine(_dirB, "tool.cmd"), "@echo off");
        var r = CliResolver.Resolve("tool", PathOf(_dirA, _dirB));
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), r.FileName);
        Assert.Equal(["/d", "/c", Path.Combine(_dirB, "tool.cmd")], r.LeadingArguments);
        Assert.Equal(Path.Combine(_dirB, "tool.cmd"), r.ResolvedPath);
    }

    [Fact]
    public void An_exe_later_on_path_beats_a_shim_earlier_on_path()
    {
        File.WriteAllText(Path.Combine(_dirA, "tool.cmd"), "@echo off");
        File.WriteAllText(Path.Combine(_dirB, "tool.exe"), "");
        var r = CliResolver.Resolve("tool", PathOf(_dirA, _dirB));
        Assert.Equal(Path.Combine(_dirB, "tool.exe"), r.FileName);
    }

    [Fact]
    public void A_missing_tool_names_every_shape_it_looked_for()
    {
        var e = Assert.Throws<FileNotFoundException>(() => CliResolver.Resolve("nosuchtool", PathOf(_dirA)));
        Assert.Contains("nosuchtool.exe", e.Message);
        Assert.Contains("nosuchtool.cmd", e.Message);
    }

    public void Dispose()
    {
        Directory.Delete(_dirA, recursive: true);
        Directory.Delete(_dirB, recursive: true);
    }
}
