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

/// <summary>Row 29: the one question the middleware asks about an owner-class bearer — where is it
/// coming from? Seamed so tests can answer it without a real child process.</summary>
public interface IOwnerPeerCheck
{
    OwnerPeerVerdict Check(ConnectionInfo connection);
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
}

/// <summary>`--owner-peer-check off`: every owner-class request is allowed wherever it comes from.
/// Exists so a lookup failure on the owner's own machine has a recovery that is not a rebuild.</summary>
public sealed class DisabledOwnerPeerCheck : IOwnerPeerCheck
{
    public OwnerPeerVerdict Check(ConnectionInfo connection) => new OwnerPeerVerdict.Allowed();
}
