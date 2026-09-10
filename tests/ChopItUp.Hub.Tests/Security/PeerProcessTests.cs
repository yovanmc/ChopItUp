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
}
