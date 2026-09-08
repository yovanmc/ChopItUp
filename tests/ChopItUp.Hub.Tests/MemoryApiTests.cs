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

    [Fact]
    public async Task R18_approve_to_core_over_the_cap_is_409_with_the_projected_size_leaves_the_row_pending_and_notes_it()
    {
        File.WriteAllText(Memory.CorePath, "# Memory\n\n" + new string('x', 5_950) + "\n");
        Proposals.Create("general", "opus", "core", "Too much", new string('y', 100), null);
        var r = await _host.Client.PostAsync("api/memory/proposals/1/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        var chars = body.GetProperty("chars").GetInt64();
        Assert.InRange(chars, 6_050, 6_200);   // 5,961 on disk + the composed entry; the provenance stamp's width is the store's business (task 2 tests the exact composition)
        Assert.Equal(6_000L, body.GetProperty("cap").GetInt64());
        Assert.Equal($"Memory proposal #1 refused: the core would be {chars} characters, over the 6000 cap. Fold it into a topic, or propose it with replaces to update an entry the core already holds.", body.GetProperty("error").GetString());
        Assert.Equal("pending", Proposals.Get(1)!.Status);
        Assert.Equal(5_961, File.ReadAllText(Memory.CorePath).Length);
        Assert.False(Directory.Exists(Path.Combine(Memory.Root, ".git")));
        var note = (await Messages()).Last();
        Assert.Equal(ChopDb.HubParticipantId, note.Author);
        Assert.Equal(body.GetProperty("error").GetString(), note.Body);   // banner and note read the same

        var r2 = await _host.Client.PostAsync("api/memory/proposals/1/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        var notes = (await Messages()).Where(m => m.Author == ChopDb.HubParticipantId).ToList();
        Assert.Single(notes);   // the refusal note is posted once per proposal per hub process, never per click
    }

    [Fact]
    public async Task R18_approve_of_a_supersede_stubs_the_old_entry_appends_the_new_and_says_so()
    {
        Memory.Append("user", "Editor", "Vim.", "approved seed");
        Proposals.Create("general", "codex", "user", "Editor", "VS Code.", null, replaces: "Editor");
        var approved = await Post("api/memory/proposals/1/approve");
        Assert.Equal(("approved", "topics/user.md", "supersede", "Editor"), (approved.GetProperty("status").GetString(), approved.GetProperty("writtenTo").GetString(), approved.GetProperty("kind").GetString(), approved.GetProperty("replaces").GetString()));
        var text = File.ReadAllText(Path.Combine(Memory.TopicsDir, "user.md"));
        Assert.DoesNotContain("Vim.", text);
        Assert.Contains("<!-- superseded: approved ", text);
        Assert.Equal(new[] { "Editor" }, Memory.Titles("user"));
        Assert.Contains("approved: replaced 'Editor' in memory/topics/user.md (commit ", (await Messages()).Last().Body);
    }

    [Fact]
    public async Task R18_approve_of_a_supersede_whose_target_is_gone_is_409_and_leaves_the_row_pending()
    {
        Memory.Append("user", "Editor", "Vim.", "seed");
        Proposals.Create("general", "codex", "user", "Editor", "VS Code.", null, replaces: "Editor");
        File.WriteAllText(Path.Combine(Memory.TopicsDir, "user.md"), "# user\n\n## Something else\nx\n");   // the owner edited by hand
        var r = await _host.Client.PostAsync("api/memory/proposals/1/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("No entry titled 'Editor' to replace.", await r.Content.ReadAsStringAsync());
        Assert.Equal("pending", Proposals.Get(1)!.Status);
    }

    [Fact]
    public async Task R18_the_list_carries_kind_replaces_flags_and_related_entries()
    {
        Memory.Append("user", "Editor of choice", "Vim.", "p");
        Memory.Append("user", "Shell", "pwsh.", "p");
        Proposals.Create("general", "opus", "user", "Editor, new choice", "VS Code.", null, replaces: "Shell", flags: "instruction-like");
        var row = Assert.Single(await Get("api/memory/proposals?room=general"));
        Assert.Equal(("supersede", "Shell"), (row.GetProperty("kind").GetString(), row.GetProperty("replaces").GetString()));
        Assert.Equal(new[] { "instruction-like" }, row.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        var related = row.GetProperty("related").EnumerateArray().ToList();
        Assert.Equal(new[] { ("Shell", true, "pwsh."), ("Editor of choice", false, "Vim.") },
            related.Select(x => (x.GetProperty("title").GetString()!, x.GetProperty("replaced").GetBoolean(), x.GetProperty("snippet").GetString()!)));
    }

    [Fact]
    public async Task R18_an_import_computes_flags_and_never_replaces()
    {
        var folder = Path.Combine(_dir, "claude-mem");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "feedback_x.md"), "---\nname: x\ndescription: Rule\nmetadata:\n  type: feedback\n---\nAlways run RED first.\n");
        await Post("api/memory/import", new { source = "claude", path = folder, roomId = "general" });
        var row = Assert.Single(await Get("api/memory/proposals?room=general"));
        Assert.Equal(("append", JsonValueKind.Null), (row.GetProperty("kind").GetString(), row.GetProperty("replaces").ValueKind));
        Assert.Equal(new[] { "instruction-like" }, row.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task R18_a_pending_row_whose_body_breaks_the_entry_rule_is_409_and_stays_pending()
    {
        using (var conn = Db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO memory_proposals (room_id, author_id, topic, title, body, status, source, created_at, kind, replaces, flags)
                VALUES ('general', 'opus', 'user', 'Bad body', $body, 'pending', NULL, $at, 'append', NULL, NULL)
                """;
            cmd.Parameters.AddWithValue("$body", "Fact.\n## Not allowed\nmore");
            cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(DateTimeOffset.UtcNow));
            cmd.ExecuteNonQuery();
        }
        var r = await _host.Client.PostAsync("api/memory/proposals/1/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        Assert.Contains("cannot be written", text);
        Assert.Contains("'# ' or '## '", text);
        Assert.Equal("pending", Proposals.Get(1)!.Status);
        Assert.False(File.Exists(Path.Combine(Memory.TopicsDir, "user.md")));
    }

    private ChopDb Db => _host.Services.GetRequiredService<ChopDb>();
}
