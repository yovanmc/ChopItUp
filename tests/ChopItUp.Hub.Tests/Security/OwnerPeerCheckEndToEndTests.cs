using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests.Security;

/// <summary>Row 29 Task 4 / issues/04-end-to-end.md: the escalation, end to end, against a real
/// child process in a real Job Object - not a fake verdict (that is
/// <see cref="OwnerPeerCheckMiddlewareTests"/>'s job). One test, in the spirit of
/// <see cref="EscalationClosedTests"/>: a scatter of narrow unit facts could each pass while the
/// combination this row exists to close still worked.
///
/// D9: <see cref="HubTestHost"/> normally starts on port 0, which never gets the <c>[::1]</c>
/// listener (<c>HubHost.cs:40-49</c>, ledger 23). This fixture starts its own host on a fixed free
/// port instead, so it listens on both loopback families like every deployed hub, and proves the
/// inside leg over both <c>127.0.0.1</c> and <c>[::1]</c>, with the outside control over
/// <c>[::1]</c>.</summary>
public sealed class OwnerPeerCheckEndToEndTests : IAsyncLifetime
{
    private static readonly string PowerShellExe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    private static readonly string CmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_peer_e2e_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;
    private SpawnJobs _jobs = null!;
    private int _port;
    private string _remoteToken = null!;

    public async Task InitializeAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _host = await HubTestHost.StartAsync(_dir, port: _port);
        _jobs = _host.Services.GetRequiredService<SpawnJobs>();
        _remoteToken = _host.TokenFor(ChopDb.OwnerRemoteParticipantId);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>The stolen credential's own attempt, run with no shell (no <c>cmd.exe</c> shim, so
    /// there is no shim-worker/conhost distinction to get wrong here - the process the peer table
    /// resolves IS <c>powershell.exe</c>, the one <see cref="SpawnJobs"/> tracked). The whole
    /// <c>-Command</c> script is ONE <see cref="ProcessStartInfo.ArgumentList"/> entry, so .NET's own
    /// argv marshalling carries it, never <c>cmd.exe</c>'s quoting.</summary>
    private static ProcessSpec ForgeSpec(string host, int port, string token) =>
        new(PowerShellExe,
            ["-NoProfile", "-NonInteractive", "-Command", ForgeScript(host, port, token)],
            new Dictionary<string, string>(),
            Path.GetTempPath(),
            "",
            "peer-check-e2e");

    /// <summary>Built with plain concatenation, not interpolation, so the script's own <c>{</c>/<c>}</c>
    /// (the <c>@{ }</c> header hashtable and the JSON body) never has to be escaped against C#'s
    /// interpolated-string syntax. Windows PowerShell 5.1's <c>Invoke-WebRequest</c> throws a
    /// terminating <c>System.Net.WebException</c> on a non-2xx status; <c>$_.Exception.Response</c> is
    /// an <c>HttpWebResponse</c> there, so <c>.StatusCode</c> is the plan's flagged-unverified shape -
    /// this leg is what measures it.</summary>
    private static string ForgeScript(string host, int port, string token) =>
        "try { $r = Invoke-WebRequest -UseBasicParsing -Method POST -Uri 'http://" + host + ":" + port + "/api/rooms/general/messages'" +
        " -Headers @{ Authorization = 'Bearer " + token + "' } -ContentType 'application/json' -Body '{\"body\":\"forged\"}';" +
        " [int]$r.StatusCode } catch { [int]$_.Exception.Response.StatusCode }";

    private async Task<List<JsonElement>> GeneralMessagesAsync()
    {
        var doc = await _host.Client.GetFromJsonAsync<JsonElement>("api/rooms/general/messages");
        return doc.GetProperty("messages").EnumerateArray().ToList();
    }

    private static Process StartUntrackedPing(int seconds = 60)
    {
        var psi = new ProcessStartInfo(CmdExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "/c", $"ping -n {seconds} 127.0.0.1 >nul" }) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    /// <summary>Runs a child with no tracking at all - not through <see cref="ProcessRunner"/>, no
    /// <see cref="SpawnJobs.Track"/> call - which is exactly what "outside the hub's jobs" means for
    /// the control leg.</summary>
    private static async Task<string> RunUntrackedAsync(ProcessSpec spec, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = spec.WorkingDirectory,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var exited = await Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds));
        Assert.True(exited, "the outside-control child did not exit within its timeout");
        return (await stdoutTask).Trim();
    }

    [Fact]
    public async Task A_stolen_bearer_is_refused_from_inside_a_spawn_over_both_families_and_accepted_from_outside_any_job()
    {
        // --- Inside, IPv4: the stolen owner-remote bearer, presented by a real child ProcessRunner
        // put in its own Job Object for @opus's spawn in 'general'. ---
        var insideV4 = ForgeSpec("127.0.0.1", _port, _remoteToken) with { RoomId = "general", ParticipantId = "opus" };
        var v4Result = await new ProcessRunner(_jobs).RunAsync(insideV4, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.Equal("403", v4Result.StandardOutput.Trim());

        var afterV4 = await GeneralMessagesAsync();
        Assert.DoesNotContain(afterV4, m => m.GetProperty("body").GetString() == "forged");
        var lastAfterV4 = afterV4[^1];
        Assert.Equal(ChopDb.HubParticipantId, lastAfterV4.GetProperty("authorId").GetString());
        Assert.Contains("@opus's spawn (pid ", lastAfterV4.GetProperty("body").GetString());

        // --- Inside, IPv6: the SAME bearer, a NEW spawn job, over [::1] - the browser's own path.
        // HubTestHost.StartAsync's default port (0) never binds [::1] at all (D9); this fixture's
        // fixed port is what makes the family reachable in the first place. ---
        var insideV6 = ForgeSpec("[::1]", _port, _remoteToken) with { RoomId = "general", ParticipantId = "opus" };
        var v6Result = await new ProcessRunner(_jobs).RunAsync(insideV6, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.Equal("403", v6Result.StandardOutput.Trim());

        var afterV6 = await GeneralMessagesAsync();
        Assert.DoesNotContain(afterV6, m => m.GetProperty("body").GetString() == "forged");
        var refusalNotes = afterV6.Count(m => m.GetProperty("authorId").GetString() == ChopDb.HubParticipantId
            && (m.GetProperty("body").GetString() ?? "").Contains("@opus's spawn (pid "));
        // A new job draws a new note (TryMarkNoted is once PER JOB, not once ever) - two refusals
        // against two different spawns must leave exactly two notes, not one.
        Assert.Equal(2, refusalNotes);

        // --- Control, outside, IPv6: the identical command, the identical stolen bearer, run with NO
        // tracking at all. Without this half, a reverted middleware would still pass the two legs
        // above for the wrong reason (a broken command also prints a non-201), and running it over
        // [::1] is what proves the browser's own path is not locked out by this row.
        //
        // SpawnJobs.Membership only ever answers Outside when NO live job exists at all - with zero
        // live jobs OwnerPeerCheck short-circuits straight to Allowed (9da479b) without ever calling
        // PeerProcess.OwningPid. By this point BOTH inside legs above have already finished and their
        // jobs disposed (ProcessRunner closes the job before returning), so LiveCount would read 0
        // here on its own. A decoy job is kept alive for the whole control leg so the check is forced
        // through the real peer-table lookup and Membership scan, and Outside is the answer THAT
        // lookup gives for this child's PID - not a side effect of nothing being tracked at all. ---
        var decoy = StartUntrackedPing();
        var decoySpec = new ProcessSpec(CmdExe, [], new Dictionary<string, string>(), Path.GetTempPath(), "", "decoy-keep-alive");
        var decoyTracker = _jobs.Track(decoy, decoySpec);
        try
        {
            Assert.True(_jobs.LiveCount > 0, "the decoy job must be live for the control leg to exercise the real lookup");

            var outsideSpec = ForgeSpec("[::1]", _port, _remoteToken);
            var outsideStatus = await RunUntrackedAsync(outsideSpec, TimeSpan.FromSeconds(60));
            Assert.Equal("201", outsideStatus);
        }
        finally
        {
            decoyTracker.Dispose();
            await Task.Run(() => decoy.WaitForExit(5000));
        }

        var afterOutside = await GeneralMessagesAsync();
        var forged = Assert.Single(afterOutside, m => m.GetProperty("body").GetString() == "forged");
        Assert.Equal(ChopDb.OwnerRemoteParticipantId, forged.GetProperty("authorId").GetString());
    }
}
