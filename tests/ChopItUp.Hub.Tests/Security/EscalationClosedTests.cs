using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Security;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests;

/// <summary>Row 28 Task 6 / issues/06-prove-it-closed.md: the threat model itself, as a test. Starts
/// from what a spawned process can actually do — read every file under the hub's data directory, and
/// make loopback HTTP calls — and ends at an owner-attributed write being unreachable. Deliberately
/// ONE test: a suite of narrow unit facts could each pass while the combination the escalation lives
/// in still works.
///
/// What this does NOT prove: whether Claude's own file-tool deny rules bind the data dir at all,
/// which needs a real spawn and is out of scope for a .NET test.
///
/// The other half of that residual, <c>owner-remote</c>'s token used from somewhere the data dir
/// never held it, is covered since row 29 by <see cref="Security.OwnerPeerCheckEndToEndTests"/>: a
/// real child process inside a hub-owned job, holding a live <c>owner-remote</c> bearer, is refused
/// over both loopback families, while the same command run outside every job is accepted.</summary>
public sealed class EscalationClosedTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_escalation_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;

    public async Task InitializeAsync() => _host = await HubTestHost.StartAsync(_dir);
    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static async Task<HttpResponseMessage> PostAsBearer(HttpClient client, string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/rooms/general/messages")
        {
            Content = JsonContent.Create(new { body = "forged" }),
        };
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await client.SendAsync(request);
    }

    /// <summary>Reads a file the hub may hold open (the sqlite database, a mid-write host-config) with
    /// sharing that matches how a second process would actually see it, retrying briefly rather than
    /// failing the whole scan on a transient lock. A real spawn reading these files would see the same
    /// thing; the retry exists only to absorb this test's own timing, not to hide a genuine failure —
    /// exhausting the retries still throws.</summary>
    private static async Task<string> ReadTextResilientlyAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(20);
            }
        }
    }

    [Fact]
    public async Task A_process_that_reads_the_whole_data_dir_and_calls_loopback_http_can_never_forge_an_owner_write()
    {
        using var anon = new HttpClient { BaseAddress = _host.BaseAddress };

        // 1) A no-credential POST is refused. Without this, reverting Task 4 alone (widening auth
        // back down to only /mcp and the two skills-decision routes) would leave every assertion
        // below green — this test would then prove nothing about the door it exists to guard.
        var noCred = await PostAsBearer(anon, bearer: null);
        Assert.Equal(HttpStatusCode.Unauthorized, noCred.StatusCode);
        var afterNoCred = await anon.GetFromJsonAsync<JsonElement>("api/rooms/general/messages");
        Assert.Empty(afterNoCred.GetProperty("messages").EnumerateArray());

        // 2) The stored hash strings themselves, presented as bearers, are refused. tokens.json holds
        // every host-file row's sha256 in the clear (that is the point of hashing it) — presenting
        // that hash back must not authenticate anything, i.e. TokenStore.TryResolve must hash the
        // PRESENTED value before comparing, never compare it to the stored hash directly.
        var rawTokens = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, TokenStore.FileName)));
        var storedHashes = rawTokens.RootElement.EnumerateObject().Select(p => p.Value.GetProperty("sha256").GetString()!).ToList();
        Assert.NotEmpty(storedHashes);   // sanity: there really are host-file rows to test against
        foreach (var hash in storedHashes)
        {
            var response = await PostAsBearer(_host.Client, hash);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // 3) Negative control: plant a KNOWN-LIVE token in a file under the data dir, after the host
        // has already started (Task 3's start-time sweep of host-configs\ only runs once, at start —
        // planting before that point would just have the sweep erase the plant, and "found nothing"
        // would hold for the wrong reason). Confirm TokenScan.Candidates actually finds it before any
        // absence found later is trusted. The plant lives under memory\ — neither tokens.json nor
        // host-configs\ — so this doubles as proof that only a walk of the WHOLE data dir, not the
        // two locations a narrower test would check, ever sees it.
        var liveOwnerToken = _host.TokenFor(ChopDb.OwnerParticipantId);
        var plantPath = Path.Combine(_dir, "memory", "planted-control.txt");
        await File.WriteAllTextAsync(plantPath, $"unrelated prose\nBearer {liveOwnerToken}\nmore prose");
        var plantCandidates = TokenScan.Candidates(await File.ReadAllTextAsync(plantPath)).ToList();
        Assert.Contains(liveOwnerToken, plantCandidates);
        File.Delete(plantPath);   // it was synthetic; the walk below must find nothing on its own merit

        // 4) The Residual, made concrete rather than just asserted in prose: data\spawns\<id>\mcp.json
        // legitimately holds a live MODEL bearer while that spawn runs (SpawnerService.Launch writes
        // exactly this shape). This plants that real shape with a genuinely live ephemeral token —
        // not a fake one — so the walk below actually exercises the design note: a candidate found
        // here MUST resolve (Task 4 does not touch /mcp) but MUST NOT authenticate an owner-attributed
        // write. Absence would be the wrong claim for this one; refusal is the right one.
        var spawnDir = Path.Combine(_dir, "spawns", "residual-example");
        Directory.CreateDirectory(spawnDir);
        var opusBearer = _host.Tokens.BearerFor("opus");
        File.WriteAllText(Path.Combine(spawnDir, "mcp.json"), SpawnCommands.ClaudeMcpConfigJson(new Uri(_host.BaseAddress, "mcp").ToString(), opusBearer));

        // 5) Walk the WHOLE data dir — not just tokens.json and host-configs\ (pass 2's critique of
        // an earlier draft of this test). memory\core.md and memory\.gitignore (MemoryStore.EnsureLayout)
        // exist by construction of every hub start, so this assertion fails on its own if the
        // enumeration below is ever narrowed back to the two known locations.
        var memoryDir = Path.Combine(_dir, "memory");
        var allFiles = Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).ToList();
        Assert.Contains(allFiles, f => f.StartsWith(memoryDir, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(allFiles, f => f.StartsWith(spawnDir, StringComparison.OrdinalIgnoreCase));

        var checkedAnyCandidate = false;
        foreach (var file in allFiles)
        {
            // HubLock.Acquire holds hub.lock open with FileShare.None for the life of the process —
            // by design, so two hubs can never share a data dir — and writes no content to it at all.
            // A real spawned process could not read it either; skipping it here is not a blind spot,
            // it is the one file this hub itself makes unreadable to everything but its own handle.
            if (Path.GetFileName(file) == ChopItUp.Hub.Hosting.HubLock.FileName) continue;
            var text = await ReadTextResilientlyAsync(file);
            foreach (var candidate in TokenScan.Candidates(text).Distinct(StringComparer.Ordinal))
            {
                if (!_host.Tokens.TryResolve(candidate, out var participant)) continue;   // not a live credential at all
                checkedAnyCandidate = true;

                Assert.False(participant is ChopDb.OwnerParticipantId or ChopDb.OwnerRemoteParticipantId,
                    $"'{file}' held a live value authenticating as owner-class participant '{participant}' — this is exactly AC6's escalation.");

                // A spawnable participant's ephemeral bearer (the residual-example plant above, and
                // in a live hub anything under a real spawns\<id>\) resolves — it is not forbidden on
                // /mcp — but Task 4 must still refuse it here.
                var response = await PostAsBearer(_host.Client, candidate);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
        Assert.True(checkedAnyCandidate, "the walk found no resolvable candidate at all — the residual-example plant above should have produced exactly one; a resolvable candidate went unscanned, or TryResolve/TokenScan disagree on what counts as live.");

        // Nothing above ever landed a message, despite every attempt.
        var finalPage = await anon.GetFromJsonAsync<JsonElement>("api/rooms/general/messages");
        Assert.Empty(finalPage.GetProperty("messages").EnumerateArray());
    }
}
