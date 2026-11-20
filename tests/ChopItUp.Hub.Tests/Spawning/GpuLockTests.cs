using System.Diagnostics;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed class GpuLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_gpulock_" + Guid.NewGuid().ToString("N"));
    private string LockPath => Path.Combine(_dir, "gpu.lock");
    private static readonly GpuHolder Me = new(Environment.ProcessId, "j1", @"C:\r.artifacts\jobs\j1-a", "stop.ps1");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A_second_holder_waits_until_the_first_releases_and_a_clean_release_leaves_no_record()
    {
        var a = new GpuLock(LockPath);
        var b = new GpuLock(LockPath);

        var first = a.TryAcquire(Me)!;
        Assert.Null(first.Stale);
        Assert.Null(b.TryAcquire(Me with { Job = "j2" }));

        first.Dispose();
        using var second = b.TryAcquire(Me with { Job = "j2" })!;
        Assert.Null(second.Stale);
    }

    [Fact]
    public async Task A_waiter_acquires_once_the_holder_releases()
    {
        var holder = new GpuLock(LockPath).TryAcquire(Me)!;
        var waiting = new GpuLock(LockPath, TimeSpan.FromMilliseconds(20)).AcquireAsync(Me with { Job = "j2" }, CancellationToken.None);

        await Task.Delay(200);
        Assert.False(waiting.IsCompleted);
        holder.Dispose();
        using var lease = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(lease.Stale);
    }

    [Fact]
    public async Task Cancelling_a_wait_throws_without_taking_the_slot()
    {
        using var holder = new GpuLock(LockPath).TryAcquire(Me)!;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GpuLock(LockPath, TimeSpan.FromMilliseconds(20)).AcquireAsync(Me with { Job = "j2" }, cancel.Token));
    }

    /// <summary>A holder process killed mid-render: the OS frees the file, and the next holder gets the
    /// dead holder's record back so it can stop that render first.</summary>
    [Fact]
    public async Task A_killed_holder_process_frees_the_slot_and_leaves_its_record_as_stale()
    {
        Directory.CreateDirectory(_dir);
        var script = $$"""
            $f = [IO.File]::Open('{{LockPath}}', 'OpenOrCreate', 'ReadWrite', 'None')
            $b = [Text.Encoding]::UTF8.GetBytes('{"HubPid":4242,"Job":"j9","Folder":"C:\\dead","CancelCommand":"stop-render"}')
            $f.Write($b, 0, $b.Length); $f.Flush()
            'held'
            Start-Sleep -Seconds 60
            """;
        using var child = Process.Start(new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command", script])
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.Equal("held", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Null(new GpuLock(LockPath).TryAcquire(Me));
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
        }

        using var lease = await new GpuLock(LockPath, TimeSpan.FromMilliseconds(20)).AcquireAsync(Me, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new GpuHolder(4242, "j9", @"C:\dead", "stop-render"), lease.Stale);
    }
}
