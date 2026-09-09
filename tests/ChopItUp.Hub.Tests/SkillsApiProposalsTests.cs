using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Skills;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace ChopItUp.Hub.Tests;

/// <summary>M25 ticket 07 / plan Task 7: <c>/api/skills/proposals</c> — list, approve, reject. Fixture
/// skills are synthetic (D-g). Proposals are minted through the real <c>propose_skill</c> tool (task 5)
/// so their recorded <c>tree_sha256</c>/<c>files</c>/<c>bytes</c> are exactly what the hub itself would
/// compute, not a hand-typed stand-in.
///
/// Task 6 (D1) gated the two decision POSTs behind an owner credential; this file is about the
/// decision LOGIC once a caller is already let through, not the gate itself (<see
/// cref="SkillsApiAuthTests"/> covers 401/403), so <see cref="_host"/>'s client carries the owner's
/// token by default here.</summary>
public sealed class SkillsApiProposalsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_skillpropapi_" + Guid.NewGuid().ToString("N"));
    private readonly string _roomDir = Path.Combine(Path.GetTempPath(), "chopitup_skillpropapi_room_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_roomDir);
        _host = await HubTestHost.StartAsync(_dir);
        _host.Services.GetRequiredService<MessageStore>().CreateRoom("proj", "Proj", _roomDir);
        _host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.TokenFor(ChopDb.OwnerParticipantId));
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        if (Directory.Exists(_roomDir)) Directory.Delete(_roomDir, recursive: true);
    }

    private SkillProposalStore Proposals => _host.Services.GetRequiredService<SkillProposalStore>();
    private SkillStore Skills => _host.Services.GetRequiredService<SkillStore>();
    private ChopDb Db => _host.Services.GetRequiredService<ChopDb>();

    private const string ValidSkillBody = "---\nname: demo\ndescription: A demo skill for tests.\n---\n# Demo Skill\n\nBody text here.\n";

    private string NewRoomSource(string name, string skillMd)
    {
        var dir = Path.Combine(_roomDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), skillMd);
        return dir;
    }

    /// <summary>Proposes <paramref name="source"/> as "opus" and returns the parsed tool result, which
    /// carries the exact <c>tree_sha256</c> the card would show and the approve body must send back.</summary>
    private async Task<JsonElement> ProposeAsync(string source, bool force = false)
    {
        await using var client = await _host.ClientFor("opus");
        return HubTestHost.Json(await client.CallToolAsync("propose_skill", new Dictionary<string, object?> { ["room_id"] = "proj", ["source_dir"] = source, ["force"] = force }));
    }

    private async Task<JsonElement> Get(string path)
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync(path));
        return doc.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> PostApprove(long id, string? treeSha256) =>
        await _host.Client.PostAsJsonAsync($"api/skills/proposals/{id}/approve", new { treeSha256 });

    private async Task<HttpResponseMessage> PostReject(long id) =>
        await _host.Client.PostAsync($"api/skills/proposals/{id}/reject", null);

    private async Task<List<(string Author, string Body)>> Messages() =>
        (await Get("api/rooms/proj/messages?afterId=0&limit=200")).GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("authorId").GetString()!, m.GetProperty("body").GetString()!)).ToList();

    [Fact]
    public async Task GET_lists_every_relative_path_the_full_text_of_each_file_and_the_declared_gates()
    {
        var body = "---\nname: gated\ndescription: d.\nrun: true\ngates: check-it\n---\n# Gated\n";
        var source = NewRoomSource("gated", body);
        var scripts = Path.Combine(source, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "check-it.ps1"), "exit 0\n");
        await ProposeAsync(source);

        var rows = (await Get("api/skills/proposals")).EnumerateArray().ToList();

        var row = Assert.Single(rows);
        Assert.Equal(("gated", false, false, 2), (row.GetProperty("name").GetString(), row.GetProperty("sourceMissing").GetBoolean(), row.GetProperty("sourceChanged").GetBoolean(), row.GetProperty("fileCount").GetInt32()));
        var entries = row.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("path").GetString()!, e => e.GetProperty("text").GetString()!);
        Assert.Equal(new[] { "SKILL.md", "scripts/check-it.ps1" }, entries.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(body, entries["SKILL.md"]);
        Assert.Equal("exit 0\n", entries["scripts/check-it.ps1"]);
        var gate = Assert.Single(row.GetProperty("gates").EnumerateArray());
        Assert.Equal("check-it", gate.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GET_reports_sourceChanged_and_suppresses_file_contents_once_the_source_has_been_edited()
    {
        var source = NewRoomSource("demo", ValidSkillBody);
        await ProposeAsync(source);
        File.WriteAllText(Path.Combine(source, "SKILL.md"), "---\nname: demo\ndescription: swapped.\n---\n# Swapped\n");

        var row = Assert.Single((await Get("api/skills/proposals")).EnumerateArray());

        Assert.True(row.GetProperty("sourceChanged").GetBoolean());
        Assert.False(row.GetProperty("sourceMissing").GetBoolean());
        Assert.Empty(row.GetProperty("entries").EnumerateArray());
        Assert.Empty(row.GetProperty("gates").EnumerateArray());
    }

    [Fact]
    public async Task GET_reports_sourceMissing_rather_than_an_empty_tree_once_the_source_directory_is_gone()
    {
        var source = NewRoomSource("demo", ValidSkillBody);
        await ProposeAsync(source);
        Directory.Delete(source, recursive: true);

        var row = Assert.Single((await Get("api/skills/proposals")).EnumerateArray());

        Assert.True(row.GetProperty("sourceMissing").GetBoolean());
        Assert.False(row.GetProperty("sourceChanged").GetBoolean());
        Assert.Empty(row.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task Approve_installs_the_skill_lists_it_without_a_restart_and_then_refuses_a_repeat()
    {
        var source = NewRoomSource("demo", ValidSkillBody);
        var proposed = await ProposeAsync(source);
        var id = proposed.GetProperty("id").GetInt64();
        var tree = proposed.GetProperty("tree_sha256").GetString();

        var r = await PostApprove(id, tree);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var approved = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, approved.GetProperty("installedAt").ValueKind);
        Assert.True(Directory.Exists(Path.Combine(Skills.Root, "demo")));

        var skills = (await Get("api/skills")).EnumerateArray().ToList();
        Assert.Contains(skills, s => s.GetProperty("name").GetString() == "demo");   // no restart needed (SkillStore reads on demand)

        Assert.Equal("Skill proposal #1 approved: 'demo' installed.", (await Messages()).Last().Body);

        Assert.Equal(HttpStatusCode.Conflict, (await PostApprove(id, tree)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostReject(id)).StatusCode);
    }

    [Fact]
    public async Task Approve_refuses_a_stale_body_hash_and_leaves_the_proposal_decidable()
    {
        var source = NewRoomSource("demo", ValidSkillBody);
        var proposed = await ProposeAsync(source);
        var id = proposed.GetProperty("id").GetInt64();
        var tree = proposed.GetProperty("tree_sha256").GetString()!;

        var refused = await PostApprove(id, "not-the-real-hash");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("does not match", await refused.Content.ReadAsStringAsync());
        Assert.Equal("pending", Proposals.Get(id)!.Status);
        Assert.False(Directory.Exists(Path.Combine(Skills.Root, "demo")));

        Assert.Equal(HttpStatusCode.OK, (await PostApprove(id, tree)).StatusCode);   // still decidable
    }

    [Fact]
    public async Task Approve_refuses_when_the_installed_state_changed_since_the_listing()
    {
        var source = NewRoomSource("demo", ValidSkillBody);
        var proposed = await ProposeAsync(source);   // recorded replacesInstalled = false
        var id = proposed.GetProperty("id").GetInt64();
        var tree = proposed.GetProperty("tree_sha256").GetString()!;

        // Something else installs 'demo' directly, bypassing the proposal this card was reviewing.
        var installSource = Path.Combine(_dir, "install-source", "demo");
        Directory.CreateDirectory(installSource);
        File.WriteAllText(Path.Combine(installSource, "SKILL.md"), ValidSkillBody);
        Assert.Equal(SkillImportOutcome.Ok, SkillImport.Run(installSource, Skills.Root, force: false, new SkillHashes(Db)).Outcome);

        var refused = await PostApprove(id, tree);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("now exists", await refused.Content.ReadAsStringAsync());
        Assert.Equal("pending", Proposals.Get(id)!.Status);
    }

    [Fact]
    public async Task A_repeat_approval_after_a_crash_before_the_install_ran_finishes_it()
    {
        var source = NewRoomSource("demo", ValidSkillBody);
        var proposed = await ProposeAsync(source);
        var id = proposed.GetProperty("id").GetInt64();
        Assert.NotNull(Proposals.MarkApproved(id));   // the crash state: approved, nothing installed yet

        var r = await PostApprove(id, null);   // the Retry button resends no body

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var installed = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.NotEqual(JsonValueKind.Null, installed.GetProperty("installedAt").ValueKind);
        Assert.True(Directory.Exists(Path.Combine(Skills.Root, "demo")));
    }

    [Fact]
    public async Task A_repeat_approval_after_the_install_had_in_fact_completed_finishes_without_reinstalling()
    {
        var source = NewRoomSource("demo", ValidSkillBody);
        var proposed = await ProposeAsync(source);
        var id = proposed.GetProperty("id").GetInt64();
        var p = Proposals.Get(id)!;
        Assert.Equal(SkillImportOutcome.Ok, SkillImport.Run(p.SourceDir, Skills.Root, force: false, new SkillHashes(Db)).Outcome);
        Assert.NotNull(Proposals.MarkApproved(id));   // the crash state: install finished, installed_at never recorded

        var r = await PostApprove(id, null);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("installedAt").ValueKind);
        Assert.True(Directory.Exists(Path.Combine(Skills.Root, "demo")));
    }

    [Fact]
    public async Task Reject_leaves_the_store_untouched_and_deletes_the_leftover_source()
    {
        var source = NewRoomSource("demo", ValidSkillBody);
        var proposed = await ProposeAsync(source);
        var id = proposed.GetProperty("id").GetInt64();

        var r = await PostReject(id);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("rejected", Proposals.Get(id)!.Status);
        Assert.False(Directory.Exists(Path.Combine(Skills.Root, "demo")));
        Assert.False(Directory.Exists(source));
        Assert.Equal("Skill proposal #1 rejected.", (await Messages()).Last().Body);
    }

    /// <summary>Task 7 correction, item A: <c>Approve</c>'s <c>if (!isRetry)</c> block used to gate BOTH
    /// the body-hash check and the <c>ReplacesInstalled</c> re-check, so a Retry call skipped them and
    /// fell straight into <c>SkillImport.Run(..., p.Force, ...)</c>. With <c>force</c> the recorded
    /// caller flag and nothing installed for this name at propose time (so <c>ReplacesInstalled</c>
    /// recorded false), an unrelated tree placed at the target between the crash and the retry would
    /// have been silently overwritten by <c>Run</c> (refusal 8 never fires under force). The
    /// re-check now runs on the retry path too and must refuse before <c>Run</c> is ever called.</summary>
    [Fact]
    public async Task Retry_refuses_when_an_unrelated_tree_now_sits_at_the_target_even_with_force()
    {
        var source = NewRoomSource("demo", ValidSkillBody);
        var proposed = await ProposeAsync(source, force: true);   // nothing installed yet; force is just the caller's flag
        var id = proposed.GetProperty("id").GetInt64();
        Assert.False(proposed.GetProperty("replaces_installed").GetBoolean());
        Assert.NotNull(Proposals.MarkApproved(id));   // the crash state: approved, nothing installed yet

        // Between the crash and the retry, an unrelated tree lands at the target path — not the skill
        // this proposal would install, and not what the listing showed (nothing) either.
        var target = Path.Combine(Skills.Root, "demo");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "SKILL.md"), "---\nname: demo\ndescription: unrelated.\n---\n# Unrelated\n");
        var unrelatedBytes = File.ReadAllBytes(Path.Combine(target, "SKILL.md"));

        var r = await PostApprove(id, null);   // the Retry button resends no body

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(unrelatedBytes, File.ReadAllBytes(Path.Combine(target, "SKILL.md")));   // byte-for-byte untouched
        var stillPending = Proposals.Get(id)!;
        Assert.Equal("approved", stillPending.Status);   // still decidable, not stranded
        Assert.Null(stillPending.InstalledAt);
    }

    /// <summary>Task 7 correction, item B: the old cache key was the tree's single newest
    /// <c>LastWriteTimeUtc</c>, so a writer that preserves timestamps (<c>Copy-Item</c>, <c>robocopy</c>
    /// with default flags) could change a file's content without moving that maximum, and <c>GET</c>
    /// would keep answering with the stale cached text. Explicitly restoring the file's own write time
    /// after editing it (with a body of a different length, so a per-file fingerprint — not the old
    /// tree-wide max — is what has to catch it) reproduces exactly that.</summary>
    [Fact]
    public async Task GET_detects_a_content_rewrite_even_when_the_tree_wide_newest_write_time_does_not_move()
    {
        var source = NewRoomSource("demo", ValidSkillBody);
        var skillMd = Path.Combine(source, "SKILL.md");
        var originalWriteUtc = File.GetLastWriteTimeUtc(skillMd);
        await ProposeAsync(source);
        var warm = Assert.Single((await Get("api/skills/proposals")).EnumerateArray());
        Assert.False(warm.GetProperty("sourceChanged").GetBoolean());   // warms the cache with the ORIGINAL content

        File.WriteAllText(skillMd, "---\nname: demo\ndescription: swapped, a much longer body than the original so the byte length differs.\n---\n# Swapped\n");
        File.SetLastWriteTimeUtc(skillMd, originalWriteUtc);   // simulate a timestamp-preserving writer

        var row = Assert.Single((await Get("api/skills/proposals")).EnumerateArray());

        Assert.True(row.GetProperty("sourceChanged").GetBoolean());
        Assert.Empty(row.GetProperty("entries").EnumerateArray());
    }

    /// <summary>Task 7 correction, item C: the listing's <c>approvable</c> flag must agree, in every
    /// state, with whether <c>Approve</c> itself would actually succeed — a source-changed row, a
    /// source-missing row, and an already-decided row are all reported not approvable, and
    /// <c>Approve</c> refuses each; a plain pending row is reported approvable, and <c>Approve</c>
    /// succeeds.</summary>
    [Fact]
    public async Task Listing_approvable_flag_agrees_with_whether_Approve_would_actually_succeed()
    {
        // The frontmatter name must agree with the directory name (SkillImport refusal 5), so each
        // fixture below gets its own body rather than reusing ValidSkillBody's "demo".
        static string SkillBody(string name) => $"---\nname: {name}\ndescription: a demo skill for tests.\n---\n# {name}\n\nBody text here.\n";
        async Task<JsonElement> RowFor(long id) =>
            (await Get("api/skills/proposals?status=all")).EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == id);

        // Plain pending: approvable, and Approve succeeds.
        var okProposed = await ProposeAsync(NewRoomSource("ok", SkillBody("ok")));
        var okId = okProposed.GetProperty("id").GetInt64();
        Assert.True((await RowFor(okId)).GetProperty("approvable").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await PostApprove(okId, okProposed.GetProperty("tree_sha256").GetString())).StatusCode);
        Assert.False((await RowFor(okId)).GetProperty("approvable").GetBoolean());   // decided now: no longer approvable

        // sourceChanged: not approvable, and Approve refuses.
        var changedSource = NewRoomSource("changed", SkillBody("changed"));
        var changedProposed = await ProposeAsync(changedSource);
        var changedId = changedProposed.GetProperty("id").GetInt64();
        File.WriteAllText(Path.Combine(changedSource, "SKILL.md"), "---\nname: changed\ndescription: edited.\n---\n# Edited\n");
        Assert.False((await RowFor(changedId)).GetProperty("approvable").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await PostApprove(changedId, changedProposed.GetProperty("tree_sha256").GetString())).StatusCode);

        // sourceMissing: not approvable, and Approve refuses.
        var missingSource = NewRoomSource("missing", SkillBody("missing"));
        var missingProposed = await ProposeAsync(missingSource);
        var missingId = missingProposed.GetProperty("id").GetInt64();
        Directory.Delete(missingSource, recursive: true);
        Assert.False((await RowFor(missingId)).GetProperty("approvable").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await PostApprove(missingId, missingProposed.GetProperty("tree_sha256").GetString())).StatusCode);

        // Already rejected: not approvable, and Approve refuses.
        var rejectedProposed = await ProposeAsync(NewRoomSource("rejected", SkillBody("rejected")));
        var rejectedId = rejectedProposed.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await PostReject(rejectedId)).StatusCode);
        Assert.False((await RowFor(rejectedId)).GetProperty("approvable").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await PostApprove(rejectedId, rejectedProposed.GetProperty("tree_sha256").GetString())).StatusCode);
    }

    [Fact]
    public async Task Decisions_are_refused_while_a_spawn_is_in_flight()
    {
        var runner = new FakeProcessRunner();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Handler = async (_, _, ct) => { await release.Task.WaitAsync(ct); return FakeProcessRunner.Ok("""{"result":"done"}"""); };
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_skillpropapi_guard_" + Guid.NewGuid().ToString("N"));
        var roomDir = Path.Combine(Path.GetTempPath(), "chopitup_skillpropapi_guardroom_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(roomDir);
        await using var host = await HubTestHost.StartAsync(dir, processRunner: runner);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.TokenFor(ChopDb.OwnerParticipantId));
        host.Services.GetRequiredService<MessageStore>().CreateRoom("proj", "Proj", roomDir);
        var sourceDir = Path.Combine(roomDir, "demo");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "SKILL.md"), ValidSkillBody);
        await using (var client = await host.ClientFor("opus"))
            await client.CallToolAsync("propose_skill", new Dictionary<string, object?> { ["room_id"] = "proj", ["source_dir"] = sourceDir });

        Assert.Equal(HttpStatusCode.Created, (await host.Client.PostAsJsonAsync("api/rooms/proj/messages", new { body = "@opus hi" })).StatusCode);
        await runner.NextSpecAsync(TimeSpan.FromSeconds(15));
        Assert.True(host.Services.GetRequiredService<SpawnerService>().AnySpawnInFlight);

        var refused = await host.Client.PostAsJsonAsync("api/skills/proposals/1/approve", new { treeSha256 = (string?)null });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(ChopItUp.Hub.Web.SkillsApi.SpawnRunning, await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync("api/skills/proposals/1/reject", null)).StatusCode);
        Assert.Equal("pending", host.Services.GetRequiredService<SkillProposalStore>().Get(1)!.Status);

        release.SetResult();
        Directory.Delete(roomDir, recursive: true);
    }
}
