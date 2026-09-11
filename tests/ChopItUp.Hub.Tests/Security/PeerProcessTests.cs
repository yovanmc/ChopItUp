using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using ChopItUp.Hub.Security;

namespace ChopItUp.Hub.Tests.Security;

/// <summary>Row 29 Task 2 / issues/02-peer-pid.md: which local process owns the client end of a
/// loopback TCP connection, read from the same owner-PID tables <c>netstat -ano</c> prints. The IPv6
/// test is what binds <see cref="PeerProcess"/>'s <c>MIB_TCP6ROW_OWNER_PID</c> field order — the plan
/// flags that order as recalled, not measured.
///
/// Marked <see cref="SupportedOSPlatform"/>("windows") to match <see cref="PeerProcess"/>'s own
/// attribute: CA1416 otherwise treats every call into a Windows-only API from this (unattributed by
/// default) test project as a platform-compat warning, which is an error under -warnaserror. CI is
/// windows-latest only (D12), so this test class never needs to run anywhere else.</summary>
[SupportedOSPlatform("windows")]
public sealed class PeerProcessTests
{
    private static (TcpListener listener, TcpClient client, TcpClient accepted) Connect(IPAddress loopback)
    {
        var listener = new TcpListener(loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var client = new TcpClient(loopback.AddressFamily);
        client.Connect(loopback, port);
        var accepted = listener.AcceptTcpClient();
        return (listener, client, accepted);
    }

    private static PeerProcess.Endpoints EndpointsFor(TcpClient accepted)
    {
        var remote = (IPEndPoint)accepted.Client.RemoteEndPoint!;
        var local = (IPEndPoint)accepted.Client.LocalEndPoint!;
        return new PeerProcess.Endpoints(remote.Address, remote.Port, local.Address, local.Port);
    }

    [Fact]
    public void The_client_end_of_an_IPv4_loopback_connection_resolves_to_this_process()
    {
        var (listener, client, accepted) = Connect(IPAddress.Loopback);
        try
        {
            var pid = PeerProcess.OwningPid(EndpointsFor(accepted));
            Assert.Equal(Environment.ProcessId, pid);
        }
        finally { client.Dispose(); accepted.Dispose(); listener.Stop(); }
    }

    [Fact]
    public void A_row_whose_hub_end_differs_is_not_matched()
    {
        var (listener, client, accepted) = Connect(IPAddress.Loopback);
        try
        {
            var e = EndpointsFor(accepted);
            using var spare = new TcpListener(IPAddress.Loopback, 0);
            spare.Start();
            var otherPort = ((IPEndPoint)spare.LocalEndpoint).Port;
            var wrong = new PeerProcess.Endpoints(e.RemoteAddress, e.RemotePort, e.LocalAddress, otherPort);
            Assert.Null(PeerProcess.OwningPid(wrong));
        }
        finally { client.Dispose(); accepted.Dispose(); listener.Stop(); }
    }

    [Fact]
    public void The_client_end_of_an_IPv6_loopback_connection_resolves_to_this_process()
    {
        var (listener, client, accepted) = Connect(IPAddress.IPv6Loopback);
        try
        {
            var pid = PeerProcess.OwningPid(EndpointsFor(accepted));
            Assert.Equal(Environment.ProcessId, pid);
        }
        finally { client.Dispose(); accepted.Dispose(); listener.Stop(); }
    }

    [Fact]
    public void A_port_with_no_established_connection_resolves_to_nothing()
    {
        var (listener, client, accepted) = Connect(IPAddress.Loopback);
        var e = EndpointsFor(accepted);
        client.Dispose();
        accepted.Dispose();
        listener.Stop();

        Assert.Null(PeerProcess.OwningPid(e));
    }

    [Fact]
    public void An_IPv4_mapped_peer_is_mapped_before_lookup()
    {
        var (listener, client, accepted) = Connect(IPAddress.Loopback);
        try
        {
            var e = EndpointsFor(accepted);
            var mapped = new PeerProcess.Endpoints(e.RemoteAddress.MapToIPv6(), e.RemotePort, e.LocalAddress.MapToIPv6(), e.LocalPort);
            Assert.Equal(Environment.ProcessId, PeerProcess.OwningPid(mapped));
        }
        finally { client.Dispose(); accepted.Dispose(); listener.Stop(); }
    }

    /// <summary>Row 29 finding: <c>Scan</c> sized its buffer once and read once, so a table that grew
    /// between the two <c>GetExtendedTcpTable</c> calls made the read return ERROR_INSUFFICIENT_BUFFER
    /// and <c>OwningPid</c> null for a connection that was genuinely established - the middleware would
    /// read that as an unresolvable peer and lock the owner out on ordinary network churn.
    ///
    /// Real socket churn was tried first to reproduce this end to end, but the table on a dev box
    /// already carries hundreds of established rows, so churn from a handful of test sockets tends to
    /// shrink the net row count between the two calls at least as often as it grows it - not a
    /// reliable trigger. <see cref="PeerProcess.GetExtendedTcpTable"/> and the new
    /// <see cref="PeerProcess.TcpTableFn"/> delegate it is exposed through are <c>internal</c> (not
    /// <c>private</c>) for exactly this: a fake can fall through to the real syscall for the honest
    /// size query and force ERROR_INSUFFICIENT_BUFFER on the read for a controlled number of attempts,
    /// which drives <c>Scan</c>'s retry loop deterministically without altering
    /// <see cref="PeerProcess.OwningPid(PeerProcess.Endpoints)"/>'s production call path (it always
    /// passes a null <c>tableCall</c> and never reaches this overload).</summary>
    [Fact]
    public void OwningPid_retries_past_repeated_ERROR_INSUFFICIENT_BUFFER_and_still_resolves()
    {
        const uint ErrorInsufficientBuffer = 122;
        var (listener, client, accepted) = Connect(IPAddress.Loopback);
        try
        {
            var e = EndpointsFor(accepted);
            var readAttempts = 0;
            const int failFirstNReads = 3; // under PeerProcess's MaxScanAttempts cap of 5

            uint FakeTableCall(nint table, ref int size, bool sorted, int family, int tableClass, uint reserved)
            {
                if (table == 0)
                {
                    // the size query: always answer honestly so the retry loop's own headroom math
                    // is exercised against real numbers.
                    return PeerProcess.GetExtendedTcpTable(table, ref size, sorted, family, tableClass, reserved);
                }
                readAttempts++;
                if (readAttempts <= failFirstNReads)
                {
                    // simulate the table having grown again since it was last measured, regardless
                    // of how much headroom the caller already allocated.
                    return ErrorInsufficientBuffer;
                }
                return PeerProcess.GetExtendedTcpTable(table, ref size, sorted, family, tableClass, reserved);
            }

            var pid = PeerProcess.OwningPid(e, FakeTableCall);

            Assert.Equal(Environment.ProcessId, pid);
            Assert.True(readAttempts > failFirstNReads,
                $"expected a real read attempt past the {failFirstNReads} induced failures; only saw {readAttempts} read attempt(s) total");
        }
        finally { client.Dispose(); accepted.Dispose(); listener.Stop(); }
    }

    /// <summary>The other half of the same finding: once retries are exhausted (every attempt hits
    /// ERROR_INSUFFICIENT_BUFFER), <c>OwningPid</c> must give up and return null rather than loop
    /// forever or throw - the middleware's existing "peer unresolvable" 403 is still the right answer
    /// when the table genuinely will not hold still.</summary>
    [Fact]
    public void OwningPid_returns_null_once_retries_are_exhausted()
    {
        const uint ErrorInsufficientBuffer = 122;
        var (listener, client, accepted) = Connect(IPAddress.Loopback);
        try
        {
            var e = EndpointsFor(accepted);
            var readAttempts = 0;

            uint AlwaysInsufficient(nint table, ref int size, bool sorted, int family, int tableClass, uint reserved)
            {
                if (table == 0) return PeerProcess.GetExtendedTcpTable(table, ref size, sorted, family, tableClass, reserved);
                readAttempts++;
                return ErrorInsufficientBuffer;
            }

            var pid = PeerProcess.OwningPid(e, AlwaysInsufficient);

            Assert.Null(pid);
            Assert.Equal(5, readAttempts); // PeerProcess.MaxScanAttempts, mirrored here since it is private
        }
        finally { client.Dispose(); accepted.Dispose(); listener.Stop(); }
    }
}
