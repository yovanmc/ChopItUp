namespace ChopItUp.Core.Model;

/// <summary>How a background job ended. <see cref="Running"/> is the only state a job is in before
/// it ends, whether or not its process has started yet.</summary>
public static class JobStatus
{
    public const string Running = "running";
    public const string Exited = "exited";
    public const string TimedOut = "timed-out";
    public const string Cancelled = "cancelled";
    /// <summary>The run ended or was stopped.</summary>
    public const string Stopped = "stopped";
    /// <summary>The hub stopped or crashed while the job ran.</summary>
    public const string Lost = "lost";
    /// <summary>The process could not be started.</summary>
    public const string Failed = "failed";
}

/// <summary>One background job a spawn started. <see cref="Name"/> is the id the tools and notes
/// use. <see cref="Pid"/> is set once the process started.</summary>
public sealed record Job(
    long Id, string RoomId, long RunId, string AuthorId, string Command, string? CancelCommand, bool Gpu,
    int TimeoutMinutes, string Folder, string Status, int? ExitCode, int? Pid, DateTimeOffset? ProcessStartedAt,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, DateTimeOffset? NotedAt)
{
    public string Name => NameFor(Id);

    public static string NameFor(long id) => "j" + id;

    /// <summary>The row id in a job name, or null when <paramref name="name"/> is not one.</summary>
    public static long? IdFrom(string name) =>
        name.Length > 1 && name[0] == 'j' && long.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.None, null, out var id) && id > 0 ? id : null;
}
