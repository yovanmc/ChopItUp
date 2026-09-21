using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed class PanelProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Output_overflow_is_bounded_and_is_not_a_timeout(bool stderr)
    {
        var spec = new ProcessSpec(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/d", "/c", "for /L %i in (1,1,10000) do @echo 1234567890" + (stderr ? " 1>&2" : "")],
            new Dictionary<string,string>(), Path.GetTempPath(), "", "panel-cap", OutputLimitBytes: 128);
        var result = await new ProcessRunner().RunAsync(spec, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(result.OutputLimitExceeded);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.StandardOutput) <= 128);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.StandardError) <= 128);
    }

    [Fact]
    public async Task Panel_removes_inherited_hub_variables_and_preserves_a_control()
    {
        const string sentinel = "CHOPITUP_M49_TEST_SENTINEL";
        const string control = "M49_TEST_CONTROL";
        Environment.SetEnvironmentVariable(sentinel, "secret-synthetic-sentinel");
        Environment.SetEnvironmentVariable(control, "control-synthetic");
        try
        {
            var spec = new ProcessSpec(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                ["/d", "/c", "set CHOPITUP_M49_TEST_SENTINEL & set M49_TEST_CONTROL"],
                new Dictionary<string,string>(), Path.GetTempPath(), "", "panel-env", RemoveEnvironmentPrefixes: ["CHOPITUP"]);
            var result = await new ProcessRunner().RunAsync(spec, TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.DoesNotContain("secret-synthetic-sentinel", result.StandardOutput);
            Assert.Contains("control-synthetic", result.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinel, null);
            Environment.SetEnvironmentVariable(control, null);
        }
    }
}
