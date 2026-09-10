using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using ChopItUp.Hub.Security;
using ChopItUp.Hub.Spawning;
using Microsoft.AspNetCore.Http;

namespace ChopItUp.Hub.Tests.Security;

/// <summary>Row 29 finding on <see cref="OwnerPeerCheck"/>: the peer lookup runs before
/// <see cref="SpawnJobs"/> is even consulted, so a lookup that cannot resolve produces
/// <see cref="OwnerPeerVerdict.Unresolvable"/> — a 403 against the owner — even when nothing is
/// spawned for the check to be guarding against. With zero live jobs <see cref="SpawnJobs.Membership"/>
/// can only ever answer <see cref="JobMembership.Outside"/> (see
/// <see cref="SpawnJobsTests"/>), so short-circuiting to <see cref="OwnerPeerVerdict.Allowed"/> before
/// the lookup changes no verdict the check would otherwise reach — it only removes a failure mode.
///
/// Marked <see cref="SupportedOSPlatform"/>("windows") like <see cref="PeerProcessTests"/>: this
/// exercises the real Windows peer-table lookup, not a fake.</summary>
[SupportedOSPlatform("windows")]
public sealed class OwnerPeerCheckTests
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

    /// <summary>An <see cref="Endpoints"/>-shaped loopback pair that used to be a real established
    /// connection and no longer is — the same "cannot possibly resolve" seam
    /// <see cref="PeerProcessTests.A_port_with_no_established_connection_resolves_to_nothing"/> uses,
    /// here wrapped in a <see cref="ConnectionInfo"/> for <see cref="OwnerPeerCheck.Check"/>.</summary>
    private static ConnectionInfo UnresolvableConnection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var client = new TcpClient(AddressFamily.InterNetwork);
        client.Connect(IPAddress.Loopback, port);
        var accepted = listener.AcceptTcpClient();
        var remote = (IPEndPoint)accepted.Client.RemoteEndPoint!;
        var local = (IPEndPoint)accepted.Client.LocalEndPoint!;
        client.Dispose();
        accepted.Dispose();
        listener.Stop();

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote.Address;
        context.Connection.RemotePort = remote.Port;
        context.Connection.LocalIpAddress = local.Address;
        context.Connection.LocalPort = local.Port;
        return context.Connection;
    }

    [Fact]
    public void With_no_live_job_an_unresolvable_peer_is_allowed()
    {
        var check = new OwnerPeerCheck(new SpawnJobs());
        var verdict = check.Check(UnresolvableConnection());
        Assert.IsType<OwnerPeerVerdict.Allowed>(verdict);
    }

    [Fact]
    public async Task With_a_live_job_an_unresolvable_peer_is_still_unresolvable()
    {
        var jobs = new SpawnJobs();
        var process = StartPing();
        var tracker = jobs.Track(process, Spec());
        try
        {
            var check = new OwnerPeerCheck(jobs);
            var verdict = check.Check(UnresolvableConnection());
            Assert.IsType<OwnerPeerVerdict.Unresolvable>(verdict);
        }
        finally
        {
            tracker.Dispose();
            await Task.Run(() => process.WaitForExit(5000));
        }
    }
}
