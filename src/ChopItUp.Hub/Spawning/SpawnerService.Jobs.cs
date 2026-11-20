using ChopItUp.Core.Model;

namespace ChopItUp.Hub.Spawning;

/// <summary>Why the hub stops a job: its run ended or was stopped, or it passed its deadline with no
/// finish report.</summary>
public enum JobStopReason { Stopped, TimedOut }

/// <summary>A run's background jobs, as the run loop sees them: how many are in flight, how to kill
/// them, and the event that wakes the conductor when one ends. A park keeps a run's jobs running.
/// Every run end kills them (<see cref="EndRun"/>).
///
/// Contract for the job tool that starts jobs. It calls <see cref="TrackJob"/> before the job's
/// process starts and <see cref="FinishJob"/> once on every exit path, from any thread (both hop
/// onto the loop). Its report for a job the hub already stopped at the deadline posts nothing. It
/// starts jobs only for a room with a run. <c>kill</c> may block (a GPU service interrupt, then the
/// process tree): the hub always calls it off the loop. A job that passes its timeout plus
/// <see cref="JobGrace"/> with no finish report is killed and finished by the hub, so a wait always
/// ends.</summary>
public sealed partial class SpawnerService
{
    private static readonly TimeSpan JobGrace = TimeSpan.FromMinutes(2);

    private sealed record TrackedJob(string RoomId, Action<JobStopReason> Kill, DateTimeOffset Deadline);

    // Loop thread only. Run id -> job id -> job, and run id -> every job id that already finished in
    // that run (dropped when the run ends, so a later run may reuse a job id).
    private readonly Dictionary<long, Dictionary<string, TrackedJob>> _jobs = new();
    private readonly Dictionary<long, HashSet<string>> _finishedJobs = new();
    // Room id -> job ids a run end killed whose finish report has not come in yet.
    private readonly Dictionary<string, HashSet<string>> _killedJobs = new(StringComparer.Ordinal);
    // Room id -> job ids the hub stopped at their deadline and already noted.
    private readonly Dictionary<string, HashSet<string>> _expiredJobs = new(StringComparer.Ordinal);

    /// <summary>Counts <paramref name="jobId"/> against the room's active or parked run until
    /// <see cref="FinishJob"/>, or until <paramref name="timeout"/> plus <see cref="JobGrace"/> passes.
    /// A room with no such run has nothing to wait for the job, so it is killed at once.</summary>
    public void TrackJob(string roomId, string jobId, TimeSpan timeout, Action<JobStopReason> kill)
    {
        if (!_events.Writer.TryWrite(new InvokeEvent(() => OnJobStarted(roomId, jobId, timeout, kill))))
            KillOffLoop(roomId, jobId, () => kill(JobStopReason.Stopped));   // the hub is shutting down
    }

    /// <summary>The job ended: <paramref name="note"/> is posted to the room and the run decides,
    /// through <see cref="RunEvent.JobFinished"/>. <paramref name="onPosted"/> runs on the loop once
    /// the note is in the room, or once a duplicate report is dropped because the hub already noted
    /// that job. It never runs when the note could not be posted or the hub is shutting down.</summary>
    public void FinishJob(string roomId, string jobId, string note, Action? onPosted = null) =>
        _events.Writer.TryWrite(new InvokeEvent(() => OnJobFinished(roomId, jobId, note, onPosted)));

    private Run? RunOwning(string roomId) =>
        _runs.Active(roomId) ?? (_runs.Latest(roomId) is { Status: RunStatus.Parked } parked ? parked : null);

    private void OnJobStarted(string roomId, string jobId, TimeSpan timeout, Action<JobStopReason> kill)
    {
        if (RunOwning(roomId) is not { } run)
        {
            KillOffLoop(roomId, jobId, () => kill(JobStopReason.Stopped));
            PostNote(roomId, $"Job {jobId} was stopped: this room has no active or parked run to wait for it.");
            return;
        }
        // Its finish report already came in: nothing to track.
        if (_finishedJobs.TryGetValue(run.Id, out var finished) && finished.Contains(jobId)) return;
        if (!_jobs.TryGetValue(run.Id, out var jobs)) _jobs[run.Id] = jobs = new Dictionary<string, TrackedJob>(StringComparer.Ordinal);
        jobs[jobId] = new TrackedJob(roomId, kill, _clock.GetUtcNow() + timeout + JobGrace);
    }

    /// <summary>Only a job the room's current run is tracking reaches the policy: a report for a job
    /// an ended run already killed, or one the hub never tracked, posts its note and nothing else.</summary>
    private void OnJobFinished(string roomId, string jobId, string note, Action? onPosted = null)
    {
        // The hub's deadline stop already posted this job's note and decided: its own late report
        // adds nothing, whatever happened to the run since.
        if (_expiredJobs.TryGetValue(roomId, out var expired) && expired.Remove(jobId))
        {
            if (expired.Count == 0) _expiredJobs.Remove(roomId);
            onPosted?.Invoke();
            return;
        }
        var noteId = PostNote(roomId, note);
        if (noteId is not null) onPosted?.Invoke();
        // A report for a job an ended run killed is that run's, never the room's current run's.
        if (_killedJobs.TryGetValue(roomId, out var killed) && killed.Remove(jobId))
        {
            if (killed.Count == 0) _killedJobs.Remove(roomId);
            return;
        }
        if (RunOwning(roomId) is not { } run) return;
        if (!_finishedJobs.TryGetValue(run.Id, out var finished)) _finishedJobs[run.Id] = finished = new HashSet<string>(StringComparer.Ordinal);
        finished.Add(jobId);
        if (!_jobs.TryGetValue(run.Id, out var jobs) || !jobs.Remove(jobId)) return;
        if (jobs.Count == 0) _jobs.Remove(run.Id);
        if (noteId is not { } id) return;

        var pending = _steers.TryGetValue(run.RoomId, out var list) ? list : (_steers[run.RoomId] = new List<long>());
        var decision = _runPolicy.Decide(AssembleRunState(run), new RunEvent.JobFinished(id), pending);
        CarryOut(run, decision);
        if (decision is RunDecision.OpenConductor) pending.Clear();
        // Busy or parked now: the note rides the next conductor spawn, a resume or an extension
        // included. A run end drops the room's pending list, so this list is detached then.
        else pending.Add(id);
        Publish(roomId);
    }

    /// <summary>Every tracked job past its deadline is killed and finished by the hub.</summary>
    private void ExpireJobs()
    {
        var now = _clock.GetUtcNow();
        var expired = _jobs.Values.SelectMany(jobs => jobs).Where(j => j.Value.Deadline <= now).ToList();
        foreach (var (jobId, job) in expired)
        {
            KillOffLoop(job.RoomId, jobId, () => job.Kill(JobStopReason.TimedOut));
            OnJobFinished(job.RoomId, jobId, $"Job {jobId} passed its time limit with no finish report; the hub stopped it.");
            if (!_expiredJobs.TryGetValue(job.RoomId, out var noted)) _expiredJobs[job.RoomId] = noted = new HashSet<string>(StringComparer.Ordinal);
            noted.Add(jobId);
        }
    }

    /// <summary>Run end: kills every job of the run and forgets them and its finished ids.</summary>
    private void KillJobs(long runId)
    {
        _finishedJobs.Remove(runId);
        if (!_jobs.Remove(runId, out var jobs)) return;
        foreach (var (jobId, job) in jobs)
        {
            if (!_killedJobs.TryGetValue(job.RoomId, out var killed)) _killedJobs[job.RoomId] = killed = new HashSet<string>(StringComparer.Ordinal);
            killed.Add(jobId);
            KillOffLoop(job.RoomId, jobId, () => job.Kill(JobStopReason.Stopped));
        }
    }

    /// <summary>Off the loop: a kill may wait on a service interrupt. One kill that throws does not
    /// stop the others.</summary>
    private static void KillOffLoop(string roomId, string jobId, Action kill) => _ = Task.Run(() =>
    {
        try { kill(); }
        catch (Exception e) { Console.Error.WriteLine($"spawner: killing job {jobId} in room {roomId} failed: {e.GetType().Name}: {e.Message}"); }
    });
}
