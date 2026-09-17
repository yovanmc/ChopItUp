using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Memory;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;
using ChopItUp.Hub.Tests.Spawning;
using ChopItUp.Hub.Web;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests;

/// <summary>Row 40: the editor's routes. Every save goes through the rewrite trail; every refusal leaves
/// no row; the cap is a 409 at every size above it.</summary>
public sealed class MemoryEditApiTests : IAsyncLifetime
{
    private static readonly SpawnLimits Fast = new(Budget: 4, Debounce: TimeSpan.FromMilliseconds(150), MinSpacing: TimeSpan.Zero, Timeout: TimeSpan.FromSeconds(30), TranscriptMessages: 60, TranscriptChars: 24_000);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private const string SeedProvenance = "approved 2026-01-01T00:00:00.0000000+00:00 proposal 0 by opus in room general";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_memedit_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await HubTestHost.StartAsync(_dir);
        _host.AuthorizeAs(ChopDb.OwnerParticipantId);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private MemoryStore Memory => _host.Services.GetRequiredService<MemoryStore>();
    private MemoryProposalStore Proposals => _host.Services.GetRequiredService<MemoryProposalStore>();

    private static string Sha(string text) => MemoryApi.Hash(text);

    private async Task<JsonElement> GetJson(string path)
    {
        var r = await _host.Client.GetAsync(path);
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<HttpResponseMessage> Put(string slug, string text, string baseHash, string room = "general") =>
        _host.Client.PutAsJsonAsync($"api/memory/topics/{slug}", new { roomId = room, text, baseHash });

    /// <summary>One appended entry through the store's own door: an H1, a heading, a provenance line.</summary>
    private string Seed(string topic, string body)
    {
        Memory.Append(topic, "Seed", body, SeedProvenance);
        return Memory.ReadTopic(topic, int.MaxValue)!.Text;
    }

    /// <summary>A file written straight to disk: the way past Append's 4,000-character body rule, and the
    /// way to fabricate CRLF.</summary>
    private string Drop(string topic, string text)
    {
        Memory.EnsureLayout();
        File.WriteAllText(Path.Combine(Memory.TopicsDir, topic + ".md"), text);
        return text;
    }

    private IEnumerable<string> Backups() =>
        Directory.GetFiles(Memory.Root, "*.bak").Concat(Directory.GetFiles(Memory.TopicsDir, "*.bak"));

    private async Task<List<(string Author, string Body)>> Messages()
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/general/messages?afterId=0&limit=200"));
        return doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => (m.GetProperty("authorId").GetString()!, m.GetProperty("body").GetString()!)).ToList();
    }

    [Fact]
    public async Task AC1_list_puts_the_core_first_with_counts_and_caps_and_get_returns_the_whole_file_uncut()
    {
        Seed("user", "Likes tests.");
        var big = Drop("big", "# big\n\n## Seed\n" + new string('x', 30_000) + "\n");   // over the 24,000 topic cap on purpose

        var list = (await GetJson("api/memory/topics")).EnumerateArray().ToList();
        Assert.Equal(["core", "big", "user"], list.Select(r => r.GetProperty("slug").GetString()).ToList());
        Assert.Equal(("MEMORY.md", 6000), (list[0].GetProperty("path").GetString(), list[0].GetProperty("cap").GetInt32()));
        Assert.Equal(("topics/big.md", 24000, big.Length), (list[1].GetProperty("path").GetString(), list[1].GetProperty("cap").GetInt32(), list[1].GetProperty("chars").GetInt32()));

        var file = await GetJson("api/memory/topics/big");
        Assert.Equal(big, file.GetProperty("text").GetString());
        Assert.Equal(Sha(big), file.GetProperty("hash").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.GetAsync("api/memory/topics/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.Client.GetAsync("api/memory/topics/Not-A-Slug")).StatusCode);
    }

    [Fact]
    public async Task AC1_a_crlf_file_reads_as_lf_and_its_hash_saves()
    {
        var crlf = Drop("notes", "# notes\r\n\r\n## Seed\r\nTyped in Notepad.\r\n");
        var file = await GetJson("api/memory/topics/notes");
        var text = file.GetProperty("text").GetString()!;
        Assert.DoesNotContain('\r', text);
        Assert.Equal(crlf.Replace("\r\n", "\n"), text);

        var r = await Put("notes", text + "\n## More\nStill LF.\n", file.GetProperty("hash").GetString()!);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.DoesNotContain('\r', File.ReadAllText(Path.Combine(Memory.TopicsDir, "notes.md")));
    }

    [Fact]
    public async Task AC2_save_writes_through_the_rewrite_trail_and_carries_a_surviving_entrys_record_forward()
    {
        var before = Seed("user", "Likes tests.");
        // "Seed" survives (same heading) with its approval line deleted from the submitted text; one entry is added.
        var edited = "# user\n\n## Seed\nLikes tests, RED before GREEN.\n\n## Drinks tea\nEvery morning.\n";

        var r = await Put("user", edited, Sha(before));
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.True(r.IsSuccessStatusCode, body.ToString());

        var written = File.ReadAllText(Path.Combine(Memory.TopicsDir, "user.md"));
        Assert.Equal(written, body.GetProperty("text").GetString());
        Assert.Equal(Sha(written), body.GetProperty("hash").GetString());
        Assert.Equal("topics/user.md.rewrite-1.bak", body.GetProperty("backup").GetString());
        Assert.StartsWith("# user\n<!-- rewritten: approved ", written);
        Assert.Contains(" proposal 1 by owner in room general -->\n", written);
        Assert.Contains("## Seed\n<!-- " + SeedProvenance + " -->\nLikes tests, RED before GREEN.\n", written);   // carried forward
        Assert.Contains("## Drinks tea\nEvery morning.\n", written);
        Assert.NotEqual(edited, written);   // what lands is composed, not echoed
        Assert.Equal(before, File.ReadAllText(Path.Combine(Memory.TopicsDir, "user.md.rewrite-1.bak")));

        var row = Proposals.Get(1)!;
        Assert.Equal(("rewrite", "approved", "editor", "owner", "Edit user", "topics/user.md"), (row.Kind, row.Status, row.Source, row.AuthorId, row.Title, row.WrittenTo));
        Assert.Null(row.Flags);
        Assert.Matches("^[0-9a-f]{7,}$", row.CommitHash);
        Assert.Equal(row.CommitHash, body.GetProperty("proposal").GetProperty("commitHash").GetString());

        var (author, note) = (await Messages()).Last();
        Assert.Equal(ChopDb.HubParticipantId, author);
        Assert.Matches(@"^Memory proposal #1 approved: edited memory/topics/user\.md \(commit [0-9a-f]{7,}\)\.$", note);
        Assert.DoesNotContain(await Messages(), m => m.Body.StartsWith("Memory proposal #1 by owner", StringComparison.Ordinal));   // no Proposed note

        Assert.Empty((await GetJson("api/memory/proposals?room=general")).EnumerateArray());   // nothing left to decide
    }

    [Fact]
    public async Task AC2_a_removed_entry_is_named_in_the_note_and_the_bearer_names_the_author()
    {
        _host.AuthorizeAs(ChopDb.OwnerRemoteParticipantId);
        var before = Seed("user", "Likes tests.");

        var r = await Put("user", "# user\n\n## Only this\nStays.\n", Sha(before));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());

        Assert.Equal(ChopDb.OwnerRemoteParticipantId, Proposals.Get(1)!.AuthorId);
        Assert.Matches(@"^Memory proposal #1 approved: edited memory/topics/user\.md, removing 'Seed' \(commit [0-9a-f]{7,}\)\.$", (await Messages()).Last().Body);
    }

    [Fact]
    public async Task AC2_the_editor_can_shrink_a_topic_already_over_its_cap()
    {
        var before = Drop("big", "# big\n\n## Seed\n" + new string('x', 30_000) + "\n");
        var r = await Put("big", "# big\n\n## Seed\nShort now.\n", Sha(before));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.True(Memory.ReadTopic("big", int.MaxValue)!.FullChars < 200);
    }

    [Fact]
    public async Task AC3_refusals_leave_no_row_no_backup_and_no_write()
    {
        var before = Seed("user", "Likes tests.");
        var core = Memory.ReadTopic(MemoryStore.CoreTopic)!.Text;
        var path = Path.Combine(Memory.TopicsDir, "user.md");

        var stale = await Put("user", "# user\n\n## Seed\nChanged.\n", "0000");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var staleBody = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal((MemoryApi.StaleEdit, Sha(before)), (staleBody.GetProperty("error").GetString(), staleBody.GetProperty("hash").GetString()));

        Assert.Equal(HttpStatusCode.BadRequest, (await Put("user", "   \n", Sha(before))).StatusCode);
        var noHeading = await Put("user", "just prose\n", Sha(before));
        Assert.Equal(HttpStatusCode.BadRequest, noHeading.StatusCode);
        Assert.Contains("at least one '## ' heading", await noHeading.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Put("user", "# user\n## Same\na\n## Same\nb\n", Sha(before))).StatusCode);

        // The cap is a 409 just above it AND far above it: the composed-size check runs before the floor.
        foreach (var pad in new[] { 6_010, 60_000 })
        {
            var overCap = await Put("core", "# Memory\n\n## Big\n" + new string('y', pad) + "\n", Sha(core));
            Assert.Equal(HttpStatusCode.Conflict, overCap.StatusCode);
            var overBody = JsonDocument.Parse(await overCap.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(6000, overBody.GetProperty("cap").GetInt32());
            Assert.Contains("over the 6000 cap", overBody.GetProperty("error").GetString());
            Assert.True(overBody.GetProperty("chars").GetInt32() > pad);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await Put("nope", "# nope\n\n## A\nb\n", "0000")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Put("user", "# user\n\n## A\nb\n", Sha(before), room: "no-such-room")).StatusCode);

        // A leading space hides `## Seed` from an untrimmed check but not from the trimmed text the row
        // stores; the carried-forward provenance then tips the composed size over the cap. The refusal must
        // come from the pre-check (409, no row), never from ApproveCore after the INSERT.
        Memory.Append(MemoryStore.CoreTopic, "Seed", "Core seed.", SeedProvenance);
        var coreSeeded = Memory.ReadTopic(MemoryStore.CoreTopic)!.Text;
        var sneaky = await Put("core", " ## Seed\n" + new string('y', 5_850) + "\n## Other\nz\n", Sha(coreSeeded));
        Assert.Equal(HttpStatusCode.Conflict, sneaky.StatusCode);
        Assert.Contains("over the 6000 cap", await sneaky.Content.ReadAsStringAsync());
        Assert.Equal(coreSeeded, Memory.ReadTopic(MemoryStore.CoreTopic)!.Text);

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Empty(Backups());
        Assert.Empty(Proposals.List(null, null));
    }

    [Fact]
    public async Task AC3_a_save_is_refused_while_a_spawn_is_in_flight_and_lands_after_it_ends()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopitup_memedit_spawn_" + Guid.NewGuid().ToString("N"));
        var runner = new FakeProcessRunner();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Handler = async (_, _, ct) => { await release.Task.WaitAsync(ct); return FakeProcessRunner.Ok("""{"result":"done"}"""); };
        await using var host = await HubTestHost.StartAsync(dir, processRunner: runner, limits: Fast);
        host.AuthorizeAs(ChopDb.OwnerParticipantId);
        var memory = host.Services.GetRequiredService<MemoryStore>();
        memory.Append("user", "Seed", "Likes tests.", SeedProvenance);
        var before = memory.ReadTopic("user", int.MaxValue)!.Text;
        var edit = new { roomId = "general", text = "# user\n\n## Seed\nChanged.\n", baseHash = MemoryApi.Hash(before) };

        Assert.Equal(HttpStatusCode.Created, (await host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "@opus hi" })).StatusCode);
        await runner.NextSpecAsync(Wait);
        var spawner = host.Services.GetRequiredService<SpawnerService>();
        Assert.True(spawner.AnySpawnInFlight);

        var refused = await host.Client.PutAsJsonAsync("api/memory/topics/user", edit);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(MemoryApi.SpawnRunning, await refused.Content.ReadAsStringAsync());
        Assert.Empty(host.Services.GetRequiredService<MemoryProposalStore>().List(null, null));

        release.SetResult();
        var deadline = DateTime.UtcNow + Wait;
        while (spawner.AnySpawnInFlight && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.False(spawner.AnySpawnInFlight);

        var ok = await host.Client.PutAsJsonAsync("api/memory/topics/user", edit);
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AC4_a_put_without_an_owner_class_bearer_is_401_or_403_and_writes_nothing()
    {
        var before = Seed("user", "Likes tests.");
        var edit = new { roomId = "general", text = "# user\n\n## Seed\nChanged.\n", baseHash = Sha(before) };

        _host.Client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client.PutAsJsonAsync("api/memory/topics/user", edit)).StatusCode);
        _host.AuthorizeAs("opus");
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client.PutAsJsonAsync("api/memory/topics/user", edit)).StatusCode);

        Assert.Equal(before, Memory.ReadTopic("user", int.MaxValue)!.Text);
        Assert.Empty(Proposals.List(null, null));
    }

    [Fact]
    public async Task AC5_a_model_consolidation_approved_from_the_panel_still_says_consolidated()
    {
        Seed("user", "Likes tests.");
        Proposals.Create("general", "opus", "user", "Consolidate user", "# user\n\n## Seed\nFolded.\n", null, null, null, MemoryProposalStore.KindRewrite);
        var r = await _host.Client.PostAsync("api/memory/proposals/1/approve", null);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Matches(@"^Memory proposal #1 approved: consolidated memory/topics/user\.md \(commit [0-9a-f]{7,}\)\.$", (await Messages()).Last().Body);
    }

    [Fact]
    public async Task AC7_preview_returns_the_composed_size_the_cap_is_enforced_on()
    {
        Seed("user", "Likes tests.");
        var typed = "# user\n\n## Seed\nNo approval line typed here.\n";
        var r = await _host.Client.PostAsJsonAsync("api/memory/topics/user/preview", new { roomId = "general", text = typed });
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.True(r.IsSuccessStatusCode, body.ToString());
        Assert.True(body.GetProperty("chars").GetInt32() > typed.Length + SeedProvenance.Length);   // marker line + carried provenance
        Assert.Equal((24000, false), (body.GetProperty("cap").GetInt32(), body.GetProperty("over").GetBoolean()));

        var over = await _host.Client.PostAsJsonAsync("api/memory/topics/core/preview", new { roomId = "general", text = "# Memory\n\n## Big\n" + new string('y', 6_100) + "\n" });
        Assert.True((JsonDocument.Parse(await over.Content.ReadAsStringAsync()).RootElement).GetProperty("over").GetBoolean());
        Assert.Empty(Proposals.List(null, null));
        Assert.Empty(Backups());
    }
}
