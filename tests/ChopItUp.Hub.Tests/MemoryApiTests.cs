using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Memory;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests;

public sealed class MemoryApiTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_memapi_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;

    public async Task InitializeAsync() => _host = await HubTestHost.StartAsync(_dir);
    public async Task DisposeAsync() => await _host.DisposeAsync();

    private MemoryStore Memory => _host.Services.GetRequiredService<MemoryStore>();
    private MemoryProposalStore Proposals => _host.Services.GetRequiredService<MemoryProposalStore>();

    private async Task<JsonElement> Post(string path, object? body = null)
    {
        var r = body is null ? await _host.Client.PostAsync(path, null) : await _host.Client.PostAsJsonAsync(path, body);
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<List<JsonElement>> Get(string path)
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync(path));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private async Task<List<(string Author, string Body)>> Messages()
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/general/messages?afterId=0&limit=200"));
        return doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => (m.GetProperty("authorId").GetString()!, m.GetProperty("body").GetString()!)).ToList();
    }

    [Fact]
    public async Task A5_approve_appends_commits_marks_and_notes_then_refuses_a_second_decision()
    {
        Proposals.Create("general", "opus", "user", "Likes tests", "RED before GREEN.", null);

        var approved = await Post("api/memory/proposals/1/approve");
        Assert.Equal(("approved", "topics/user.md"), (approved.GetProperty("status").GetString(), approved.GetProperty("writtenTo").GetString()));
        Assert.Matches("^[0-9a-f]{7,}$", approved.GetProperty("commitHash").GetString());
        Assert.NotEqual(JsonValueKind.Null, approved.GetProperty("decidedAt").ValueKind);

        var path = Path.Combine(Memory.TopicsDir, "user.md");
        var text = File.ReadAllText(path);
        Assert.StartsWith("# user\n\n## Likes tests\n<!-- approved ", text);
        Assert.Contains(" proposal 1 by opus in room general -->\nRED before GREEN.\n", text);
        Assert.True(Directory.Exists(Path.Combine(Memory.Root, ".git")));

        var (author, body) = (await Messages()).Last();
        Assert.Equal(ChopDb.HubParticipantId, author);
        Assert.Matches(@"^Memory proposal #1 approved: written to memory/topics/user\.md \(commit [0-9a-f]{7,}\)\.$", body);

        Assert.Equal(HttpStatusCode.Conflict, (await _host.Client.PostAsync("api/memory/proposals/1/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _host.Client.PostAsync("api/memory/proposals/1/reject", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.PostAsync("api/memory/proposals/9/approve", null)).StatusCode);
        Assert.Equal(text, File.ReadAllText(path));   // the refused repeats wrote nothing
    }

    [Fact]
    public async Task A5_an_approval_that_died_after_marking_is_finished_by_a_repeat_without_a_duplicate_entry()
    {
        Proposals.Create("general", "opus", "user", "Likes tests", "RED before GREEN.", null);
        Assert.NotNull(Proposals.Decide(1, MemoryProposalStore.Approved, null, null));   // the crash state: approved, unwritten

        var first = await Post("api/memory/proposals/1/approve");
        Assert.Equal("topics/user.md", first.GetProperty("writtenTo").GetString());
        var path = Path.Combine(Memory.TopicsDir, "user.md");
        Assert.Equal(1, File.ReadAllText(path).Split("## Likes tests").Length - 1);

        // A second replay (written_to now set) is a plain 409, and the file is untouched.
        Assert.Equal(HttpStatusCode.Conflict, (await _host.Client.PostAsync("api/memory/proposals/1/approve", null)).StatusCode);
        Assert.Equal(1, File.ReadAllText(path).Split("## Likes tests").Length - 1);
    }

    [Fact]
    public async Task A5_reject_marks_and_notes_and_writes_nothing()
    {
        Proposals.Create("general", "gpt-6-astra", "core", "Owner", "Name is Yovan.", null);
        var rejected = await Post("api/memory/proposals/1/reject");
        Assert.Equal("rejected", rejected.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, rejected.GetProperty("writtenTo").ValueKind);
        Assert.Equal("Memory proposal #1 rejected.", (await Messages()).Last().Body);
        Assert.DoesNotContain("## Owner", File.ReadAllText(Memory.CorePath));
        Assert.False(Directory.Exists(Path.Combine(Memory.Root, ".git")));
    }

    [Fact]
    public async Task A5_approving_a_core_proposal_grows_MEMORY_md()
    {
        Proposals.Create("general", "sonnet", "core", "Owner", "Name is Yovan.", null);
        var approved = await Post("api/memory/proposals/1/approve");
        Assert.Equal("MEMORY.md", approved.GetProperty("writtenTo").GetString());
        var core = File.ReadAllText(Memory.CorePath);
        Assert.StartsWith("# Memory", core);
        Assert.Contains("\n## Owner\n<!-- approved ", core);
        Assert.EndsWith("Name is Yovan.\n", core);
    }

    [Fact]
    public async Task A5_list_filters_by_room_and_status()
    {
        Proposals.Create("general", "opus", "user", "A", "a", null);
        Proposals.Create("general", "opus", "user", "B", "b", null);
        await Post("api/memory/proposals/2/reject");
        Proposals.Create("general", "opus", "user", "C", "c", null);
        Proposals.Decide(3, MemoryProposalStore.Approved, null, null);               // the crash state (decision 15): approved, never written
        Assert.Equal(new[] { 1L, 3L }, (await Get("api/memory/proposals?room=general")).Select(p => p.GetProperty("id").GetInt64()));   // default = undecided
        Assert.Equal(new[] { 1L, 3L }, (await Get("api/memory/proposals")).Select(p => p.GetProperty("id").GetInt64()));
        Assert.Equal(new[] { 1L }, (await Get("api/memory/proposals?status=pending")).Select(p => p.GetProperty("id").GetInt64()));
        Assert.Equal(new[] { 2L }, (await Get("api/memory/proposals?status=rejected")).Select(p => p.GetProperty("id").GetInt64()));
        Assert.Equal(new[] { 1L, 2L, 3L }, (await Get("api/memory/proposals?status=all")).Select(p => p.GetProperty("id").GetInt64()));
        Assert.Empty(await Get("api/memory/proposals?room=nope"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.Client.GetAsync("api/memory/proposals?status=weird")).StatusCode);
    }

    [Fact]
    public async Task A6_import_creates_proposals_authored_as_the_app_backed_row_posts_one_note_and_is_idempotent()
    {
        var src = Path.Combine(_dir, "claude-memory");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "MEMORY.md"), "- index\n");
        File.WriteAllText(Path.Combine(src, "user_profile.md"), "---\nname: user-profile\ndescription: Who the owner is\nmetadata:\n  type: user\n---\nEngineer.\n");
        File.WriteAllText(Path.Combine(src, "feedback_tdd.md"), "---\nname: tdd\ndescription: RED first\nmetadata:\n  type: feedback\n---\nAlways.\n");

        var first = await Post("api/memory/import", new { source = "claude", path = src, roomId = "general" });
        Assert.Equal((2, 0), (first.GetProperty("imported").GetInt32(), first.GetProperty("skipped").GetInt32()));
        var pending = Proposals.List("general");
        Assert.Equal(2, pending.Count);
        Assert.All(pending, p => Assert.Equal("claude", p.AuthorId));
        Assert.All(pending, p => Assert.Equal("claude:" + src, p.Source));
        Assert.Equal(new[] { "feedback", "user" }, pending.Select(p => p.Topic));

        var notes = (await Messages()).Where(m => m.Author == ChopDb.HubParticipantId).ToList();
        Assert.Equal($"Memory import from claude ({src}): 2 proposal(s) added, 0 already proposed. Review them in the memory panel.", Assert.Single(notes).Body);

        var second = await Post("api/memory/import", new { source = "claude", path = src, roomId = "general" });
        Assert.Equal((0, 2), (second.GetProperty("imported").GetInt32(), second.GetProperty("skipped").GetInt32()));
        Assert.Equal(2, Proposals.List("general").Count);
    }

    [Fact]
    public async Task A6_a_rejected_import_row_does_not_block_the_same_title_from_the_right_folder_and_discard_undoes_an_import()
    {
        var wrong = Path.Combine(_dir, "wrong"); Directory.CreateDirectory(wrong);
        File.WriteAllText(Path.Combine(wrong, "a.md"), "---\nname: a\ndescription: Same title\nmetadata:\n  type: user\n---\nWrong body.\n");
        File.WriteAllText(Path.Combine(wrong, "b.md"), "---\nname: b\ndescription: Only here\nmetadata:\n  type: user\n---\nB.\n");
        var right = Path.Combine(_dir, "right"); Directory.CreateDirectory(right);
        File.WriteAllText(Path.Combine(right, "a.md"), "---\nname: a\ndescription: Same title\nmetadata:\n  type: user\n---\nRight body.\n");

        await Post("api/memory/import", new { source = "claude", path = wrong, roomId = "general" });
        await Post("api/memory/proposals/1/reject");                                             // "Same title" from the wrong folder
        var discard = await _host.Client.DeleteAsync($"api/memory/proposals?source=claude&path={Uri.EscapeDataString(wrong)}");
        Assert.Equal(HttpStatusCode.OK, discard.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(await discard.Content.ReadAsStringAsync()).RootElement.GetProperty("discarded").GetInt32());   // "Only here"
        Assert.Empty(Proposals.List("general"));

        var again = await Post("api/memory/import", new { source = "claude", path = right, roomId = "general" });
        Assert.Equal((1, 0), (again.GetProperty("imported").GetInt32(), again.GetProperty("skipped").GetInt32()));
        Assert.Equal("Right body.", Assert.Single(Proposals.List("general")).Body);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.Client.DeleteAsync("api/memory/proposals?source=gemini&path=x")).StatusCode);
    }

    [Fact]
    public async Task A6_a_folder_over_the_draft_cap_is_refused_whole()
    {
        var src = Path.Combine(_dir, "huge"); Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "raw_memories.md"), string.Concat(Enumerable.Range(1, MemoryImport.MaxDrafts + 1).Select(i => $"## Fact {i}\nBody {i}.\n")));
        var r = await _host.Client.PostAsJsonAsync("api/memory/import", new { source = "codex", path = src, roomId = "general" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains($"yields {MemoryImport.MaxDrafts + 1} proposals", await r.Content.ReadAsStringAsync());
        Assert.Empty(Proposals.List(null, null));
        Assert.Empty(await Messages());
    }

    [Fact]
    public async Task A6_a_bad_import_request_posts_nothing()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.Client.PostAsJsonAsync("api/memory/import", new { source = "gemini", path = _dir, roomId = "general" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.Client.PostAsJsonAsync("api/memory/import", new { source = "codex", path = "relative", roomId = "general" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.Client.PostAsJsonAsync("api/memory/import", new { source = "codex", path = Path.Combine(_dir, "missing"), roomId = "general" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.PostAsJsonAsync("api/memory/import", new { source = "codex", path = _dir, roomId = "nope" })).StatusCode);
        Assert.Empty(await Messages());
        Assert.Empty(Proposals.List(null, null));
    }
}
