using System.Net;
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
/// compute, not a hand-typed stand-in.</summary>
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
