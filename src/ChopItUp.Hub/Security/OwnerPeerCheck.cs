using Microsoft.AspNetCore.Http;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Security;

public abstract record OwnerPeerVerdict
{
    public sealed record Allowed : OwnerPeerVerdict;
    public sealed record Unresolvable(string Reason) : OwnerPeerVerdict;
    /// <summary><paramref name="NoteDue"/> is true for the first refusal against this job only.</summary>
    public sealed record InsideSpawn(int Pid, SpawnJobEntry Entry, bool NoteDue) : OwnerPeerVerdict;
}

/// <summary>The one question the middleware asks about an owner-class bearer: where is it coming
/// from? Seamed so tests can answer it without a real child process.</summary>
public interface IOwnerPeerCheck
{
    OwnerPeerVerdict Check(ConnectionInfo connection);

    /// <summary>Asked about every bearer while a background job is live, because a job may not act
    /// as anyone, even with a bearer its starter pasted into its command:
    /// <see cref="OwnerPeerVerdict.InsideSpawn"/> when the peer runs inside a background job,
    /// <see cref="OwnerPeerVerdict.Unresolvable"/> when its job membership cannot be read (a job can
    /// hide its own process from the query), null otherwise. A peer whose connection row cannot be
    /// found answers null, so a TCP table miss never locks the models out.</summary>
    OwnerPeerVerdict? InsideBackgroundJob(ConnectionInfo connection) => null;
}

public sealed class OwnerPeerCheck(SpawnJobs jobs) : IOwnerPeerCheck
{
    public OwnerPeerVerdict Check(ConnectionInfo connection)
    {
        if (!OperatingSystem.IsWindows()) return new OwnerPeerVerdict.Unresolvable("not windows");
        if (connection.RemoteIpAddress is null) return new OwnerPeerVerdict.Unresolvable("no remote address");
        if (connection.LocalIpAddress is null) return new OwnerPeerVerdict.Unresolvable("no local address");
        // Nothing is spawned for this check to be guarding against: with zero live jobs
        // SpawnJobs.Membership can only ever answer Outside, so skip the peer lookup rather than let
        // a lookup failure in this state turn into a lockout the check exists to prevent, not cause.
        if (jobs.LiveCount == 0) return new OwnerPeerVerdict.Allowed();
        var pid = PeerProcess.OwningPid(new PeerProcess.Endpoints(connection.RemoteIpAddress, connection.RemotePort, connection.LocalIpAddress, connection.LocalPort));
        if (pid is null) return new OwnerPeerVerdict.Unresolvable($"no established row for {connection.RemoteIpAddress}:{connection.RemotePort} -> {connection.LocalIpAddress}:{connection.LocalPort}");
        return jobs.Membership(pid.Value, out var entry) switch
        {
            JobMembership.Inside => new OwnerPeerVerdict.InsideSpawn(pid.Value, entry!, jobs.TryMarkNoted(entry!)),
            JobMembership.Unknown => new OwnerPeerVerdict.Unresolvable($"job membership unreadable for pid {pid.Value}"),
            _ => new OwnerPeerVerdict.Allowed(),
        };
    }

    public OwnerPeerVerdict? InsideBackgroundJob(ConnectionInfo connection)
    {
        if (!OperatingSystem.IsWindows() || jobs.LiveBackgroundJobs == 0) return null;
        if (connection.RemoteIpAddress is null || connection.LocalIpAddress is null) return null;
        var pid = PeerProcess.OwningPid(new PeerProcess.Endpoints(connection.RemoteIpAddress, connection.RemotePort, connection.LocalIpAddress, connection.LocalPort));
        if (pid is null) return null;
        return jobs.Membership(pid.Value, out var entry) switch
        {
            JobMembership.Inside when entry!.Kind == SpawnJobKind.BackgroundJob => new OwnerPeerVerdict.InsideSpawn(pid.Value, entry, jobs.TryMarkNoted(entry)),
            JobMembership.Unknown => new OwnerPeerVerdict.Unresolvable($"job membership unreadable for pid {pid.Value}"),
            _ => null,
        };
    }
}

/// <summary>`--owner-peer-check off`: every owner-class request is allowed wherever it comes from.
/// Exists so a lookup failure on the owner's own machine has a recovery that is not a rebuild.</summary>
public sealed class DisabledOwnerPeerCheck : IOwnerPeerCheck
{
    public OwnerPeerVerdict Check(ConnectionInfo connection) => new OwnerPeerVerdict.Allowed();
}
