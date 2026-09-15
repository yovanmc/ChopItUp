using System.ComponentModel;
using System.IO;
using ChopItUp.Desktop.Hub;
using Microsoft.Extensions.Time.Testing;

namespace ChopItUp.Desktop.Tests;

public class HubChildTests
{
    // Drives a FakeTimeProvider-based poll loop to completion without a real wall-clock wait: Task.Delay
    // (PollInterval, clock, ct) only resolves once the fake clock is advanced past it, so this repeatedly
    // advances by one poll interval and yields, until the state machine's Task finishes or maxSteps runs out.
    private static async Task<HubStatus> RunToCompletion(HubChild child, FakeTimeProvider clock, TimeSpan step, int maxSteps = 200)
    {
        var task = child.StartOrAttachAsync(CancellationToken.None);
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            clock.Advance(step);
            await Task.Delay(5);
        }
        await task;   // HubChild never throws out of StartOrAttachAsync; this just surfaces a stuck test as a hang
        return child.Status;
    }

    private static ShellArgs NewStartArgs(int port = 8790)
    {
        var (dir, hubExe) = TestDirs.NewWithHubExe();
        return new ShellArgs(dir, port, hubExe, ShellCommand.Run);
    }

    [Fact]
    public async Task Starts_with_data_and_port_and_a_43_char_token_then_becomes_Ready_once_the_probe_answers()
    {
        var args = NewStartArgs(8790);
        var proc = new FakeHubProcess();
        var factory = new FakeFactory(proc);
        var calls = 0;
        var probe = new FakeProbe(_ => ++calls > 3);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var events = new List<HubState>();
        var child = new HubChild(args, factory, probe, clock, _ => { });
        child.StatusChanged += s => events.Add(s.State);

        var status = await RunToCompletion(child, clock, HubChild.PollInterval);

        Assert.Equal(HubState.Ready, status.State);
        Assert.Equal(proc.Pid, status.Pid);
        Assert.Equal(args.Port, status.Port);
        Assert.True(factory.Called);
        Assert.Equal(args.DataDir, factory.DataDir);
        Assert.Equal(args.HubExe, factory.Exe);
        Assert.Equal(args.Port, factory.Port);
        Assert.Equal(43, factory.ShellToken?.Length);
        Assert.Equal(new[] { HubState.Ready }, events);
        Assert.Equal(new Uri($"http://127.0.0.1:{args.Port}/"), child.ResolvedOrigin);
    }

    [Fact]
    public async Task A_factory_that_throws_yields_Failed_naming_the_exception()
    {
        var args = NewStartArgs();
        var factory = new FakeFactory(new FakeHubProcess()) { ThrowOnStart = new Win32Exception("blocked") };
        var probe = new FakeProbe(_ => false);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var child = new HubChild(args, factory, probe, clock, _ => { });

        var status = await RunToCompletion(child, clock, HubChild.PollInterval);

        Assert.Equal(HubState.Failed, status.State);
        Assert.Contains("Win32Exception", status.Reason);
        Assert.Contains("blocked", status.Reason);
    }

    [Fact]
    public async Task Attach_resolves_the_real_port_never_touches_the_factory_and_Stop_is_a_no_op()
    {
        var dir = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, "hub.port"), "8811");
        using var lockHandle = new FileStream(Path.Combine(dir, "hub.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var args = new ShellArgs(dir, 8790, Path.Combine(dir, "ChopItUp.Hub.exe"), ShellCommand.Run);
        var proc = new FakeHubProcess();
        var factory = new FakeFactory(proc);
        var probe = new FakeProbe(origin => origin.Port == 8811);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var child = new HubChild(args, factory, probe, clock, _ => { });

        var status = await RunToCompletion(child, clock, HubChild.PollInterval);

        Assert.Equal(HubState.Attached, status.State);
        Assert.Equal(8811, status.Port);
        Assert.Equal(new Uri("http://127.0.0.1:8811/"), child.ResolvedOrigin);
        Assert.False(factory.Called);
        Assert.Null(child.ShellToken);

        child.Stop();
        Assert.Equal(HubState.Attached, child.Status.State);
        Assert.Equal(0, proc.KillCount);
    }

    [Fact]
    public async Task Attach_polls_for_the_port_file_to_appear_within_the_budget()
    {
        var dir = TestDirs.New();
        using var lockHandle = new FileStream(Path.Combine(dir, "hub.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var args = new ShellArgs(dir, 8790, Path.Combine(dir, "ChopItUp.Hub.exe"), ShellCommand.Run);
        var factory = new FakeFactory(new FakeHubProcess());
        var probe = new FakeProbe(origin => origin.Port == 8811);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var child = new HubChild(args, factory, probe, clock, _ => { });

        var task = child.StartOrAttachAsync(CancellationToken.None);
        for (var i = 0; i < 8 && !task.IsCompleted; i++)   // ~2s of fake time with no hub.port on disk yet
        {
            clock.Advance(HubChild.PollInterval);
            await Task.Delay(5);
        }
        Assert.False(task.IsCompleted);

        File.WriteAllText(Path.Combine(dir, "hub.port"), "8811");
        for (var i = 0; i < 60 && !task.IsCompleted; i++)
        {
            clock.Advance(HubChild.PollInterval);
            await Task.Delay(5);
        }
        await task;

        Assert.Equal(HubState.Attached, child.Status.State);
        Assert.Equal(8811, child.Status.Port);
        Assert.False(factory.Called);
    }

    [Fact]
    public async Task Attach_fails_naming_the_missing_port_file_when_it_never_appears()
    {
        var dir = TestDirs.New();
        using var lockHandle = new FileStream(Path.Combine(dir, "hub.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var args = new ShellArgs(dir, 8790, Path.Combine(dir, "ChopItUp.Hub.exe"), ShellCommand.Run);
        var factory = new FakeFactory(new FakeHubProcess());
        var probe = new FakeProbe(_ => true);   // would answer healthy, but no port is ever readable
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var child = new HubChild(args, factory, probe, clock, _ => { });

        var status = await RunToCompletion(child, clock, HubChild.PollInterval);

        Assert.Equal(HubState.Failed, status.State);
        Assert.Contains("wrote no hub.port", status.Reason);
    }

    [Fact]
    public async Task Output_lines_raised_synchronously_inside_BeginReading_still_land_in_the_tail()
    {
        var args = NewStartArgs();
        var proc = new FakeHubProcess(linesOnBeginReading: ["hello", "world"]);
        var factory = new FakeFactory(proc);
        var probe = new FakeProbe(_ => true);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var child = new HubChild(args, factory, probe, clock, _ => { });

        await RunToCompletion(child, clock, HubChild.PollInterval);

        Assert.True(proc.BeginReadingCalled);
        Assert.Equal(new[] { "hello", "world" }, child.Tail.Snapshot());
    }

    [Fact]
    public async Task Exiting_right_after_a_true_probe_is_caught_before_Ready_sticks()
    {
        var args = NewStartArgs();
        var proc = new FakeHubProcess();
        var factory = new FakeFactory(proc);
        var probe = new FakeProbe(_ => { proc.SetExitedQuietly(); return true; });
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var child = new HubChild(args, factory, probe, clock, _ => { });

        var status = await RunToCompletion(child, clock, HubChild.PollInterval);

        Assert.Equal(HubState.Failed, status.State);
        Assert.Contains("hub exited", status.Reason);
        Assert.Contains("hub.log", status.Reason);
    }

    [Fact]
    public async Task Exiting_before_healthy_is_Failed_and_Stop_afterwards_does_not_kill_again()
    {
        var args = NewStartArgs();
        var proc = new FakeHubProcess();
        var factory = new FakeFactory(proc);
        var probe = new FakeProbe(_ => { proc.SetExitedQuietly(); return false; });
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var child = new HubChild(args, factory, probe, clock, _ => { });

        var status = await RunToCompletion(child, clock, HubChild.PollInterval);

        Assert.Equal(HubState.Failed, status.State);
        Assert.Contains("exited before it was ready", status.Reason);

        child.Stop();
        Assert.Equal(0, proc.KillCount);
    }

    [Fact]
    public async Task Never_healthy_within_the_budget_is_Failed_naming_the_budget()
    {
        var args = NewStartArgs();
        var factory = new FakeFactory(new FakeHubProcess());
        var probe = new FakeProbe(_ => false);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var child = new HubChild(args, factory, probe, clock, _ => { });

        var status = await RunToCompletion(child, clock, HubChild.PollInterval, maxSteps: 120);

        Assert.Equal(HubState.Failed, status.State);
        Assert.Contains("did not answer within", status.Reason);
        Assert.Contains("20", status.Reason);
    }

    [Fact]
    public async Task Cancelling_the_token_while_still_Starting_yields_Stopped_not_Failed()
    {
        var args = NewStartArgs();
        var proc = new FakeHubProcess();
        var factory = new FakeFactory(proc);
        var probe = new FakeProbe(_ => false);   // never healthy: the loop is still polling when cancelled
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var child = new HubChild(args, factory, probe, clock, _ => { });
        using var cts = new CancellationTokenSource();

        var task = child.StartOrAttachAsync(cts.Token);
        for (var i = 0; i < 3 && !task.IsCompleted; i++)   // reach the poll loop (probe still false)
        {
            clock.Advance(HubChild.PollInterval);
            await Task.Delay(5);
        }
        Assert.False(task.IsCompleted);

        cts.Cancel();
        await task;

        Assert.Equal(HubState.Stopped, child.Status.State);
    }

    [Fact]
    public async Task Exiting_after_Ready_flips_to_Failed_with_the_log_hint()
    {
        var args = NewStartArgs();
        var proc = new FakeHubProcess();
        var factory = new FakeFactory(proc);
        var probe = new FakeProbe(_ => true);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var child = new HubChild(args, factory, probe, clock, _ => { });

        var status = await RunToCompletion(child, clock, HubChild.PollInterval);
        Assert.Equal(HubState.Ready, status.State);

        proc.RaiseExited();

        Assert.Equal(HubState.Failed, child.Status.State);
        Assert.Contains("hub exited", child.Status.Reason);
        Assert.Contains("hub.log", child.Status.Reason);
    }

    [Fact]
    public async Task Tail_keeps_only_the_last_40_output_lines()
    {
        var args = NewStartArgs();
        var proc = new FakeHubProcess();
        var factory = new FakeFactory(proc);
        var probe = new FakeProbe(_ => true);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var child = new HubChild(args, factory, probe, clock, _ => { });

        var status = await RunToCompletion(child, clock, HubChild.PollInterval);
        Assert.Equal(HubState.Ready, status.State);

        for (var i = 1; i <= 41; i++) proc.Emit($"line {i}");

        var snapshot = child.Tail.Snapshot();
        Assert.Equal(40, snapshot.Count);
        Assert.Equal("line 2", snapshot[0]);
        Assert.Equal("line 41", snapshot[^1]);
    }

    private sealed class FakeHubProcess : IHubProcess
    {
        private readonly string[] _linesOnBeginReading;

        public FakeHubProcess(int pid = 4321, string[]? linesOnBeginReading = null)
        {
            Pid = pid;
            _linesOnBeginReading = linesOnBeginReading ?? [];
        }

        public int Pid { get; }
        public bool HasExited { get; private set; }
        public event Action<string>? OutputLine;
        public event Action? Exited;
        public bool BeginReadingCalled { get; private set; }
        public int KillCount { get; private set; }

        public void BeginReading()
        {
            BeginReadingCalled = true;
            foreach (var line in _linesOnBeginReading) OutputLine?.Invoke(line);
        }

        public void Emit(string line) => OutputLine?.Invoke(line);

        /// <summary>Flips HasExited without firing Exited — simulates the loop's own poll noticing an
        /// exit, as opposed to the async Exited handler.</summary>
        public void SetExitedQuietly() => HasExited = true;

        public void RaiseExited()
        {
            HasExited = true;
            Exited?.Invoke();
        }

        public void Kill() => KillCount++;
        public void Dispose() { }
    }

    private sealed class FakeFactory : IHubProcessFactory
    {
        private readonly IHubProcess _process;

        public FakeFactory(IHubProcess process) => _process = process;

        public Exception? ThrowOnStart { get; init; }
        public bool Called { get; private set; }
        public string? Exe { get; private set; }
        public string? DataDir { get; private set; }
        public int Port { get; private set; }
        public string? ShellToken { get; private set; }

        public IHubProcess Start(string exe, string dataDir, int port, string shellToken)
        {
            Called = true;
            Exe = exe;
            DataDir = dataDir;
            Port = port;
            ShellToken = shellToken;
            if (ThrowOnStart is not null) throw ThrowOnStart;
            return _process;
        }
    }

    private sealed class FakeProbe : IHealthProbe
    {
        private readonly Func<Uri, bool> _decide;

        public FakeProbe(Func<Uri, bool> decide) => _decide = decide;

        public List<Uri> Calls { get; } = new();

        public Task<bool> IsHealthyAsync(Uri origin, CancellationToken ct)
        {
            Calls.Add(origin);
            return Task.FromResult(_decide(origin));
        }
    }
}
