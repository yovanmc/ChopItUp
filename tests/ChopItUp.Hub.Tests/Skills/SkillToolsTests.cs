using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Skills;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ChopItUp.Hub.Tests.Skills;

/// <summary>Ticket 05 / plan Task 5: the <c>propose_skill</c> MCP tool. D4 confines the source to the
/// calling room's own bound directory; D6 refuses an overlay at this point by passing none through to
/// <see cref="SkillImport.Validate"/>, so both overlay refusals fall out of <c>Validate</c> itself.
/// The tool never calls <see cref="SkillImport.Run"/> - nothing under the skill store changes as a
/// result of an offer.</summary>
public sealed class SkillToolsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_skilltools_" + Guid.NewGuid().ToString("N"));
    private readonly string _roomDir = Path.Combine(Path.GetTempPath(), "chopitup_skilltools_room_" + Guid.NewGuid().ToString("N"));
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

    private static async Task<CallToolResult> Call(McpClient client, string tool, Dictionary<string, object?> args) =>
        await client.CallToolAsync(tool, args);

    private static string ErrorText(CallToolResult r)
    {
        Assert.True(r.IsError, "expected a tool error");
        return string.Join("", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
    }

    private async Task<List<(string Author, string Body)>> Messages(string roomId = "proj")
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync($"api/rooms/{roomId}/messages?afterId=0&limit=200"));
        return doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => (m.GetProperty("authorId").GetString()!, m.GetProperty("body").GetString()!)).ToList();
    }

    private const string ValidSkillBody =
        "---\nname: demo\ndescription: A demo skill for tests.\n---\n# Demo Skill\n\nBody text here.\n";

    /// <summary>A source directory INSIDE the bound room directory, holding SKILL.md.</summary>
    private string NewRoomSource(string name, string skillMd)
    {
        var dir = Path.Combine(_roomDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), skillMd);
        return dir;
    }

    [Fact]
    public async Task Records_a_pending_proposal_stamped_with_the_caller_and_posts_the_note_writing_nothing_under_the_skills_root()
    {
        var source = NewRoomSource("demo", ValidSkillBody);
        await using var client = await _host.ClientFor("opus");

        var r = HubTestHost.Json(await Call(client, "propose_skill", new() { ["room_id"] = "proj", ["source_dir"] = source }));

        Assert.Equal((1L, "proj", "opus", "demo", "pending", false, false, 1),
            (r.GetProperty("id").GetInt64(), r.GetProperty("room_id").GetString(), r.GetProperty("author_id").GetString(),
             r.GetProperty("name").GetString(), r.GetProperty("status").GetString(),
             r.GetProperty("replaces_installed").GetBoolean(), r.GetProperty("force").GetBoolean(), r.GetProperty("files").GetInt32()));
        Assert.True(r.GetProperty("bytes").GetInt64() > 0);

        var stored = Assert.Single(Proposals.List("proj", SkillProposalStore.Undecided));
        Assert.Equal(("opus", "demo"), (stored.AuthorId, stored.Name));

        var note = (await Messages()).Last();
        Assert.Equal(ChopDb.HubParticipantId, note.Author);
        Assert.Contains("demo", note.Body);
        Assert.Contains("opus", note.Body);
        Assert.Contains("1 file", note.Body);

        Assert.False(Directory.Exists(Path.Combine(Skills.Root, "demo")));
    }

    [Fact]
    public async Task Refuses_a_source_outside_the_rooms_directory_the_same_way_whether_or_not_it_exists()
    {
        await using var client = await _host.ClientFor("opus");
        var outsideExisting = Path.Combine(_dir, "outside-exists");
        Directory.CreateDirectory(outsideExisting);
        var outsideMissing = Path.Combine(_dir, "outside-missing");

        var existingMsg = ErrorText(await Call(client, "propose_skill", new() { ["room_id"] = "proj", ["source_dir"] = outsideExisting }));
        var missingMsg = ErrorText(await Call(client, "propose_skill", new() { ["room_id"] = "proj", ["source_dir"] = outsideMissing }));

        Assert.Contains("outside the directory of room 'proj'", existingMsg);
        Assert.Contains("outside the directory of room 'proj'", missingMsg);
        Assert.DoesNotContain("does not exist", existingMsg);
        Assert.DoesNotContain("does not exist", missingMsg);
        Assert.Empty(Proposals.List(null, null));
    }

    [Fact]
    public async Task Returns_the_underlying_import_refusal_and_records_nothing_for_a_bad_source()
    {
        await using var client = await _host.ClientFor("opus");
        var noSkillMd = Path.Combine(_roomDir, "empty");
        Directory.CreateDirectory(noSkillMd);

        var err = ErrorText(await Call(client, "propose_skill", new() { ["room_id"] = "proj", ["source_dir"] = noSkillMd }));

        Assert.Contains("No SKILL.md directly in", err);
        Assert.Empty(Proposals.List(null, null));
        Assert.False(Directory.Exists(Path.Combine(Skills.Root, "empty")));
    }

    [Fact]
    public async Task Refuses_a_file_extension_outside_the_reviewable_allowlist_surfacing_Validates_message()
    {
        var source = NewRoomSource("demo", ValidSkillBody);
        File.WriteAllBytes(Path.Combine(source, "helper.exe"), [0x4D, 0x5A]);
        await using var client = await _host.ClientFor("opus");

        var err = ErrorText(await Call(client, "propose_skill", new() { ["room_id"] = "proj", ["source_dir"] = source }));

        Assert.Contains("helper.exe", err);
        Assert.Empty(Proposals.List(null, null));
    }

    [Fact]
    public async Task Offering_the_same_skill_and_tree_twice_returns_the_first_proposal_with_no_second_note()
    {
        var source = NewRoomSource("demo", ValidSkillBody);
        await using var opus = await _host.ClientFor("opus");
        await using var codex = await _host.ClientFor("codex");

        var first = HubTestHost.Json(await Call(opus, "propose_skill", new() { ["room_id"] = "proj", ["source_dir"] = source }));
        var again = HubTestHost.Json(await Call(codex, "propose_skill", new() { ["room_id"] = "proj", ["source_dir"] = source }));

        Assert.Equal((first.GetProperty("id").GetInt64(), true, "opus"),
            (again.GetProperty("id").GetInt64(), again.GetProperty("duplicate").GetBoolean(), again.GetProperty("author_id").GetString()));
        Assert.Single(Proposals.List("proj", SkillProposalStore.Undecided));
        Assert.Single(await Messages(), m => m.Body.StartsWith("Skill proposal #"));
    }

    [Fact]
    public async Task Refuses_a_source_carrying_its_own_overlay()
    {
        var source = NewRoomSource("demo", ValidSkillBody);
        File.WriteAllText(Path.Combine(source, "OVERLAY.md"), "sneaky\n");
        await using var client = await _host.ClientFor("opus");

        var err = ErrorText(await Call(client, "propose_skill", new() { ["room_id"] = "proj", ["source_dir"] = source }));

        Assert.Contains("OVERLAY.md", err);
        Assert.Empty(Proposals.List(null, null));
    }

    [Fact]
    public async Task Refuses_a_forced_offer_that_would_replace_an_already_installed_skill_carrying_an_overlay()
    {
        // Install a skill WITH an overlay directly, bypassing the hub's own state - same skills root
        // and database the hub's tool will read from, so this is a legitimate fixture, not a shortcut
        // around the refusal under test.
        var hashes = new SkillHashes(_host.Services.GetRequiredService<ChopDb>());
        var installedSourceDir = Path.Combine(_dir, "install-source", "demo");
        Directory.CreateDirectory(installedSourceDir);
        File.WriteAllText(Path.Combine(installedSourceDir, "SKILL.md"), ValidSkillBody);
        var overlayDir = Path.Combine(_dir, "overlay");
        Directory.CreateDirectory(overlayDir);
        File.WriteAllText(Path.Combine(overlayDir, "OVERLAY.md"), "Overlay prose.\n");
        Assert.Equal(SkillImportOutcome.Ok, SkillImport.Run(installedSourceDir, Skills.Root, force: false, hashes, overlayDir).Outcome);

        var source = NewRoomSource("demo", "---\nname: demo\ndescription: v2.\n---\n# v2\n");
        await using var client = await _host.ClientFor("opus");

        var err = ErrorText(await Call(client, "propose_skill", new() { ["room_id"] = "proj", ["source_dir"] = source, ["force"] = true }));

        Assert.Contains("overlay", err, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Proposals.List(null, null));
    }

    [Fact]
    public async Task A_room_with_no_bound_directory_refuses_any_offer()
    {
        _host.Services.GetRequiredService<MessageStore>().CreateRoom("unbound", "Unbound", null);
        await using var client = await _host.ClientFor("opus");

        var err = ErrorText(await Call(client, "propose_skill", new() { ["room_id"] = "unbound", ["source_dir"] = @"C:\anything" }));

        Assert.Contains("no bound directory", err);
        Assert.Empty(Proposals.List(null, null));
    }

    [Fact]
    public async Task An_unknown_room_is_refused()
    {
        await using var client = await _host.ClientFor("opus");
        var err = ErrorText(await Call(client, "propose_skill", new() { ["room_id"] = "nope", ["source_dir"] = @"C:\anything" }));
        Assert.Contains("Unknown room", err);
    }
}
