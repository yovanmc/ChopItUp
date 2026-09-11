using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace ChopItUp.Hub.Spawning;

/// <summary>What a live job was started for: enough for the middleware's refusal note to name the
/// spawn, nothing the child could use.</summary>
public sealed record SpawnJobEntry(int RootPid, string Label, string? RoomId, string? ParticipantId);

/// <summary><see cref="Unknown"/> is an answer, not an absence: a PID whose membership could not be
/// read is neither inside nor outside, and the middleware fails closed on it.</summary>
public enum JobMembership { Inside, Outside, Unknown }

/// <summary>Row 29: every process <see cref="ProcessRunner"/> starts is placed in its own kill-on-
/// close Job Object and recorded here for the life of the run. <see cref="Membership"/> is the
/// question <c>BearerTokenMiddleware</c> asks about the peer of a loopback connection: is this PID
/// inside any job the hub currently owns? Membership is inherited by every descendant the child
/// creates AFTER the assignment lands, which is the descendant that can hold a stolen credential and
/// make a request with it. It is NOT inherited by one created inside the assignment window: measured
/// 2026-09-10 over 15 isolated runs, a `cmd.exe` shim's `conhost.exe` reported outside the job on 10
/// of them, while the PING.EXE worker the shim's command line names was inside on all 15
/// (<c>SpawnJobsTests.FindPingChildOf</c> carries the same measurement from the test's side).
/// Off Windows there are no jobs: <see cref="Track"/> records the root PID only and the query
/// matches that PID alone — good enough for a dev box, and the middleware fails closed there anyway.</summary>
public sealed class SpawnJobs
{
    private sealed class Slot
    {
        public SafeFileHandle? Job;
        public required SpawnJobEntry Entry;
        public bool Noted;
    }

    private static readonly IDisposable Untracked = new NoOp();
    private readonly object _gate = new();
    private readonly Dictionary<long, Slot> _live = new();
    private long _next;

    public int LiveCount { get { lock (_gate) return _live.Count; } }

    /// <summary>Creates the job, assigns <paramref name="process"/> to it, verifies the assignment
    /// took, and records it. Dispose the result when the run is over: that closes the job, which
    /// kills anything still inside it. A child that has already exited (a `git rev-parse` can finish
    /// between <c>Start()</c> and this call) has nothing to confine and gets a no-op tracker. A LIVE
    /// child that cannot be confined throws — the caller kills it rather than running it unconfined.</summary>
    public IDisposable Track(Process process, ProcessSpec spec)
    {
        if (process.HasExited) return Untracked;
        SafeFileHandle? job = null;
        if (OperatingSystem.IsWindows())
        {
            job = JobObjectInterop.CreateKillOnCloseJob();
            try
            {
                JobObjectInterop.Assign(job, process.Handle);
            }
            catch (Win32Exception) when (process.HasExited)
            {
                job.Dispose();
                return Untracked;
            }
            catch
            {
                job.Dispose();
                throw;
            }
            // Assignment is verified, never assumed: a root that reports outside the job it was just
            // assigned to is a process the hub cannot confine.
            if (JobObjectInterop.IsInJob(process.Id, job) != true)
            {
                job.Dispose();
                if (process.HasExited) return Untracked;
                throw new InvalidOperationException($"process {process.Id} ({spec.Label}) is not inside the job it was assigned to");
            }
        }
        var entry = new SpawnJobEntry(process.Id, spec.Label, spec.RoomId, spec.ParticipantId);
        long id;
        lock (_gate)
        {
            id = ++_next;
            _live[id] = new Slot { Job = job, Entry = entry };
        }
        return new Tracked(this, id);
    }

    /// <summary>Inside with the entry when <paramref name="pid"/> is inside any live job (on
    /// Windows) or is a live root PID (elsewhere); Unknown when at least one live job could not be
    /// asked about it and none answered Inside; Outside otherwise.</summary>
    public JobMembership Membership(int pid, out SpawnJobEntry? entry)
    {
        Slot[] snapshot;
        lock (_gate) snapshot = _live.Values.ToArray();
        var unknown = false;
        foreach (var slot in snapshot)
        {
            if (slot.Job is null)
            {
                if (slot.Entry.RootPid == pid) { entry = slot.Entry; return JobMembership.Inside; }
                continue;
            }
            if (!OperatingSystem.IsWindows()) { unknown = true; continue; }
            switch (JobObjectInterop.IsInJob(pid, slot.Job))
            {
                case true: entry = slot.Entry; return JobMembership.Inside;
                case null: unknown = true; break;
            }
        }
        entry = null;
        return unknown ? JobMembership.Unknown : JobMembership.Outside;
    }

    /// <summary>True exactly once per live job: the middleware posts its room note on the first
    /// refusal and only writes stderr for every later one.</summary>
    public bool TryMarkNoted(SpawnJobEntry entry)
    {
        lock (_gate)
        {
            foreach (var slot in _live.Values)
            {
                if (!ReferenceEquals(slot.Entry, entry)) continue;
                if (slot.Noted) return false;
                slot.Noted = true;
                return true;
            }
            return false;
        }
    }

    private void Release(long id)
    {
        SafeFileHandle? job;
        lock (_gate)
        {
            if (!_live.Remove(id, out var slot)) return;
            job = slot.Job;
        }
        job?.Dispose();   // kill-on-close: whatever is still inside dies here
    }

    private sealed class NoOp : IDisposable { public void Dispose() { } }

    private sealed class Tracked(SpawnJobs owner, long id) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) owner.Release(id); }
    }
}
