using ChopItUp.Desktop;

namespace ChopItUp.Desktop.Tests;

public class ShellArgsTests
{
    [Fact]
    public void Defaults_put_data_and_hub_beside_the_exe()
    {
        var a = ShellArgs.Parse([], baseDir: @"C:\x\");
        Assert.Equal(@"C:\x\data", a.DataDir);
        Assert.Equal(8790, a.Port);
        Assert.Equal(@"C:\x\ChopItUp.Hub.exe", a.HubExe);
        Assert.Equal(ShellCommand.Run, a.Command);
    }

    [Fact]
    public void Explicit_values_win_and_are_made_absolute()
    {
        var a = ShellArgs.Parse(["--data", ".data", "--port", "8795", "--hub", @"src\Hub.exe"], baseDir: @"C:\x\", cwd: @"C:\repo\");
        Assert.Equal(@"C:\repo\.data", a.DataDir);
        Assert.Equal(8795, a.Port);
        Assert.Equal(@"C:\repo\src\Hub.exe", a.HubExe);
    }

    [Theory]
    [InlineData("--show", ShellCommand.Show)]
    [InlineData("--quit", ShellCommand.Quit)]
    public void Single_instance_verbs_parse(string flag, ShellCommand expected) =>
        Assert.Equal(expected, ShellArgs.Parse([flag], baseDir: @"C:\x\").Command);

    [Theory]
    [InlineData("--port")]
    [InlineData("--port", "nope")]
    [InlineData("--data")]
    [InlineData("--bogus")]
    public void Bad_input_throws_with_the_flag_named(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => ShellArgs.Parse(args, baseDir: @"C:\x\"));
        Assert.Contains(args[0], ex.Message);
    }
}
