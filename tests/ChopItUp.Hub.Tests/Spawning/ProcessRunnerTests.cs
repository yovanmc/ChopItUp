using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed class ProcessRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static ProcessSpec Spec(string stdin, params string[] args) =>
        new(Cmd, args, new Dictionary<string, string>(), Path.GetTempPath(), stdin, "test");

    [Fact]
    public async Task Stdin_is_delivered_and_stdout_stderr_and_exit_code_come_back()
    {
        // findstr /L (literal, not regex) echoes every stdin line that contains a dot; "exit 3" sets the code afterwards.
        var spec = Spec("alpha.\nbeta\ngamma.\n", "/d", "/c", "findstr /L . & echo err>&2 & exit 3");
        var r = await new ProcessRunner().RunAsync(spec, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(r.TimedOut);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("alpha.", r.StandardOutput);
        Assert.Contains("gamma.", r.StandardOutput);
        Assert.DoesNotContain("beta", r.StandardOutput);
        Assert.Contains("err", r.StandardError);
    }

    [Fact]
    public async Task A_run_past_its_timeout_is_killed_as_a_tree_and_reported()
    {
        var spec = Spec("", "/d", "/c", "ping -n 60 -w 1000 127.0.0.1");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await new ProcessRunner().RunAsync(spec, TimeSpan.FromSeconds(2), CancellationToken.None);
        sw.Stop();
        Assert.True(r.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
        Assert.Equal(0, await ChildrenOf(r.ProcessId));   // the grandchild (ping under cmd) is gone too: a real tree kill
    }

    [Fact]
    public async Task A_child_that_never_reads_its_stdin_still_times_out()
    {
        // A CLI stuck before reading stdin (auth prompt, MCP startup hang) must not wedge the runner on
        // a full pipe: the timeout is armed before the write, and the write is cancellable.
        var bigPrompt = new string('x', 64 * 1024);
        var spec = Spec(bigPrompt, "/d", "/c", "ping -n 30 -w 1000 127.0.0.1");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await new ProcessRunner().RunAsync(spec, TimeSpan.FromSeconds(2), CancellationToken.None);
        sw.Stop();
        Assert.True(r.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
        Assert.Equal(0, await ChildrenOf(r.ProcessId));
    }

    /// <summary>Live processes whose parent was <paramref name="pid"/>. Windows keeps the parent id
    /// on an orphan, so this counts grandchildren that survived a tree kill of the parent.</summary>
    private static async Task<int> ChildrenOf(int pid)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-Command", $"@(Get-CimInstance Win32_Process -Filter 'ParentProcessId={pid}').Count" }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return int.Parse(text.Trim());
    }

    [Fact]
    public async Task Cancellation_kills_and_is_not_reported_as_a_timeout()
    {
        var spec = Spec("", "/d", "/c", "ping -n 60 -w 1000 127.0.0.1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var r = await new ProcessRunner().RunAsync(spec, TimeSpan.FromMinutes(1), cts.Token);
        Assert.False(r.TimedOut);
        Assert.True(r.Cancelled);
    }

    [Fact]
    public async Task A_running_child_is_inside_a_live_job_until_it_finishes()
    {
        var jobs = new SpawnJobs();
        var runner = new ProcessRunner(jobs);
        var spec = Spec("", "/d", "/c", "ping -n 40 -w 1000 127.0.0.1");
        using var cts = new CancellationTokenSource();
        var runTask = runner.RunAsync(spec, TimeSpan.FromSeconds(30), cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (jobs.LiveCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.Equal(1, jobs.LiveCount);

        cts.Cancel();
        await runTask;
        Assert.Equal(0, jobs.LiveCount);
    }

    [Fact]
    public async Task Environment_and_working_directory_reach_the_child()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_pr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var spec = new ProcessSpec(Cmd, ["/d", "/c", "echo %CHOPITUP_TEST_VAR% & cd"], new Dictionary<string, string> { ["CHOPITUP_TEST_VAR"] = "present" }, dir, "", "test");
            var r = await new ProcessRunner().RunAsync(spec, TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Contains("present", r.StandardOutput);
            Assert.Contains(dir, r.StandardOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
