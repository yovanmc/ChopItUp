using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed class SpawnJobsTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static System.Diagnostics.Process StartPing(int seconds = 40)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Cmd)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "/c", $"ping -n {seconds} 127.0.0.1 >nul" }) psi.ArgumentList.Add(a);
        return System.Diagnostics.Process.Start(psi)!;
    }

    private static ProcessSpec Spec(string label = "test") =>
        new(Cmd, [], new Dictionary<string, string>(), Path.GetTempPath(), "", label);

    /// <summary>Polls for the live PING.EXE child of <paramref name="pid"/> (cmd's actual worker
    /// process) until it appears or <paramref name="timeout"/> elapses. Deliberately does not
    /// settle for "whichever child WMI enumerates first": cmd.exe also spawns a conhost.exe child
    /// very early, whose creation can race SpawnJobs.Track's AssignProcessToJobObject call closely
    /// enough that its membership flips Inside/Outside from run to run (measured 2026-09-10: 10 of
    /// 15 isolated runs saw conhost Outside while PING.EXE was Inside on all 15) - the same window
    /// the plan's threat model names as left open ("a child the shim creates BEFORE assignment
    /// inherits nothing"). PING.EXE is only created after cmd.exe finishes parsing its command
    /// line, well after Assign() has returned, so it is the deterministic descendant to assert
    /// against. A poll (rather than one fixed sleep before one query) also tolerates a slower or
    /// loaded machine taking longer to spawn or observe the child.</summary>
    private static async Task<System.Diagnostics.Process?> FindPingChildOf(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var a in new[] { "-NoProfile", "-Command", $"(Get-CimInstance Win32_Process -Filter \"ParentProcessId={pid} AND Name='PING.EXE'\" | Select-Object -First 1 -ExpandProperty ProcessId)" })
            {
                psi.ArgumentList.Add(a);
            }
            using (var p = System.Diagnostics.Process.Start(psi)!)
            {
                var text = (await p.StandardOutput.ReadToEndAsync()).Trim();
                await p.WaitForExitAsync();
                if (int.TryParse(text, out var childPid))
                {
                    try { return System.Diagnostics.Process.GetProcessById(childPid); }
                    catch (ArgumentException) { /* exited between the query and GetProcessById: keep polling */ }
                }
            }
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task A_tracked_child_and_its_grandchild_are_inside_and_this_process_is_not()
    {
        var jobs = new SpawnJobs();
        var process = StartPing();
        var tracker = jobs.Track(process, Spec());
        try
        {
            var child = await FindPingChildOf(process.Id, TimeSpan.FromSeconds(5));
            Assert.Equal(JobMembership.Inside, jobs.Membership(process.Id, out _));
            Assert.NotNull(child);
            Assert.Equal(JobMembership.Inside, jobs.Membership(child!.Id, out _));
            Assert.Equal(JobMembership.Outside, jobs.Membership(Environment.ProcessId, out _));
        }
        finally
        {
            tracker.Dispose();
            Assert.True(process.WaitForExit(5000));
        }
    }

    [Fact]
    public void Disposing_twice_is_harmless_and_LiveCount_tracks()
    {
        var jobs = new SpawnJobs();
        var process = StartPing();
        try
        {
            var tracker = jobs.Track(process, Spec());
            Assert.Equal(1, jobs.LiveCount);
            tracker.Dispose();
            Assert.Equal(0, jobs.LiveCount);
            tracker.Dispose();
            Assert.Equal(0, jobs.LiveCount);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    [Fact]
    public async Task A_child_that_already_exited_is_tracked_as_a_no_op()
    {
        var jobs = new SpawnJobs();
        var psi = new System.Diagnostics.ProcessStartInfo(Cmd) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "/c", "exit" }) psi.ArgumentList.Add(a);
        var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        await Task.Delay(50); // let the OS settle process teardown before we ask about it

        var tracker = jobs.Track(process, Spec());
        Assert.Equal(0, jobs.LiveCount);
        Assert.NotEqual(JobMembership.Inside, jobs.Membership(process.Id, out _));
        tracker.Dispose();
    }

    [Fact]
    public void Membership_is_Unknown_when_a_live_job_cannot_be_asked()
    {
        var jobs = new SpawnJobs();
        var process = StartPing();
        var tracker = jobs.Track(process, Spec());
        try
        {
            Assert.Equal(JobMembership.Unknown, jobs.Membership(int.MaxValue, out _));
            Assert.Equal(JobMembership.Outside, jobs.Membership(Environment.ProcessId, out _));
        }
        finally
        {
            tracker.Dispose();
        }
    }

    [Fact]
    public void TryMarkNoted_answers_true_once_per_job()
    {
        var jobs = new SpawnJobs();
        var process = StartPing();
        try
        {
            var tracker = jobs.Track(process, Spec());
            jobs.Membership(process.Id, out var entry);
            Assert.NotNull(entry);
            Assert.True(jobs.TryMarkNoted(entry!));
            Assert.False(jobs.TryMarkNoted(entry!));
            Assert.False(jobs.TryMarkNoted(entry!));
            tracker.Dispose();

            var process2 = StartPing();
            try
            {
                var tracker2 = jobs.Track(process2, Spec());
                jobs.Membership(process2.Id, out var entry2);
                Assert.NotNull(entry2);
                Assert.True(jobs.TryMarkNoted(entry2!));
                tracker2.Dispose();
            }
            finally
            {
                try { process2.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }
}
