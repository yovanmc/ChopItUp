using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChopItUp.Core.Storage;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChopItUp.Hub.Tests;

/// <summary>The <c>/api</c> surface the web UI (a later task) talks to. No auth here per D2 — loopback
/// is the boundary — and every write goes through <c>MessageStore.Post</c>, the same path the MCP
/// tools use, so the cursor and broadcast rules cannot drift (brief Task 2).</summary>
public sealed class ChatApiTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_api_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;

    public async Task InitializeAsync() => _host = await HubTestHost.StartAsync(_dir);
    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task List_rooms_reports_general_with_a_message_count()
    {
        await using var claude = await _host.ClientFor("claude");
        await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "seed" });

        var rooms = await _host.Client.GetFromJsonAsync<JsonElement>("api/rooms");
        var general = rooms.EnumerateArray().Single(r => r.GetProperty("id").GetString() == "general");
        Assert.Equal("General", general.GetProperty("name").GetString());
        Assert.Equal(1, general.GetProperty("messageCount").GetInt32());
    }

    [Fact]
    public async Task Reading_an_unknown_room_is_404()
    {
        var response = await _host.Client.GetAsync("api/rooms/nope/messages");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_messages_is_paged_in_id_order_matching_MessageStore_Read()
    {
        await using var claude = await _host.ClientFor("claude");
        foreach (var body in new[] { "one", "two", "three" })
            await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = body });

        var page = await _host.Client.GetFromJsonAsync<JsonElement>("api/rooms/general/messages?limit=2");
        var msgs = page.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, msgs.Count);
        Assert.Equal("one", msgs[0].GetProperty("body").GetString());
        Assert.Equal("two", msgs[1].GetProperty("body").GetString());
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        long next = page.GetProperty("nextAfterId").GetInt64();

        var rest = await _host.Client.GetFromJsonAsync<JsonElement>($"api/rooms/general/messages?afterId={next}&limit=2");
        var restMsgs = rest.GetProperty("messages").EnumerateArray().ToList();
        Assert.Single(restMsgs);
        Assert.Equal("three", restMsgs[0].GetProperty("body").GetString());
        Assert.False(rest.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Posting_through_the_api_is_authored_owner_and_readable_back()
    {
        var response = await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "from the web ui" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var posted = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("owner", posted.GetProperty("authorId").GetString());
        Assert.Equal("from the web ui", posted.GetProperty("body").GetString());

        var page = await _host.Client.GetFromJsonAsync<JsonElement>("api/rooms/general/messages");
        var msg = page.GetProperty("messages").EnumerateArray().Single();
        Assert.Equal("owner", msg.GetProperty("authorId").GetString());
    }

    [Fact]
    public async Task Posting_an_empty_body_is_a_400()
    {
        var response = await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Posting_to_an_unknown_room_is_404()
    {
        var response = await _host.Client.PostAsJsonAsync("api/rooms/nope/messages", new { body = "x" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_api_post_goes_through_MessageStore_Post_and_broadcasts_over_signalr()
    {
        var connection = new HubConnectionBuilder().WithUrl(new Uri(_host.BaseAddress, "hub/rooms")).Build();
        await connection.StartAsync();
        await connection.InvokeAsync("JoinRoom", "general");
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("MessagePosted", msg => received.TrySetResult(msg));

        await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "broadcast me" });

        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("broadcast me", payload.GetProperty("body").GetString());
        Assert.Equal("owner", payload.GetProperty("authorId").GetString());
        await connection.DisposeAsync();

        // Also: the poster's own cursor moved, exactly like a store.Post from any other path would.
        await using var claude = await _host.ClientFor("owner");
        var rooms = HubTestHost.Json(await claude.CallToolAsync("list_rooms", new Dictionary<string, object?>()));
        Assert.Equal(0, rooms.GetProperty("rooms")[0].GetProperty("unread_count").GetInt32());
    }

    [Fact]
    public async Task Import_splits_on_speaker_prefixed_lines_and_authors_everything_owner()
    {
        const string transcript = "Claude: hello there\nsecond line of claude's turn\nCodex: hi back\nOwner: welcome both";
        var response = await _host.Client.PostAsJsonAsync("api/rooms/general/import", new { text = transcript });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var msgs = created.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(3, msgs.Count);
        Assert.All(msgs, m => Assert.Equal("owner", m.GetProperty("authorId").GetString()));
        Assert.Contains("Claude: hello there", msgs[0].GetProperty("body").GetString());
        Assert.Contains("second line of claude's turn", msgs[0].GetProperty("body").GetString());
        Assert.Contains("Codex: hi back", msgs[1].GetProperty("body").GetString());
        Assert.Contains("Owner: welcome both", msgs[2].GetProperty("body").GetString());
    }

    [Fact]
    public async Task Import_with_no_speaker_labels_becomes_one_message()
    {
        const string transcript = "just some pasted text\nwith no speaker prefix anywhere\nacross several lines";
        var response = await _host.Client.PostAsJsonAsync("api/rooms/general/import", new { text = transcript });
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var msgs = created.GetProperty("messages").EnumerateArray().ToList();
        Assert.Single(msgs);
        Assert.Equal("owner", msgs[0].GetProperty("authorId").GetString());
        Assert.Contains("just some pasted text", msgs[0].GetProperty("body").GetString());
    }

    /// <summary>D1's regression guard: however the paste is labelled — mixed case, extra spaces, the
    /// literal participant ids — an imported message is never attributed to <c>claude</c> or
    /// <c>codex</c>. The hub stamps the author; the label is just text inside the body.</summary>
    [Fact]
    public async Task Import_never_attributes_a_message_to_claude_or_codex_however_the_paste_is_labelled()
    {
        const string transcript = "claude: i am claude\nCODEX:   i am codex\nClAuDe:mixed case no space\nrandom Name: something else entirely";
        var response = await _host.Client.PostAsJsonAsync("api/rooms/general/import", new { text = transcript });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var msgs = created.GetProperty("messages").EnumerateArray().ToList();
        Assert.NotEmpty(msgs);
        Assert.All(msgs, m =>
        {
            var author = m.GetProperty("authorId").GetString();
            Assert.NotEqual("claude", author);
            Assert.NotEqual("codex", author);
            Assert.Equal("owner", author);
        });
    }

    [Fact]
    public async Task Import_broadcasts_over_signalr()
    {
        var connection = new HubConnectionBuilder().WithUrl(new Uri(_host.BaseAddress, "hub/rooms")).Build();
        await connection.StartAsync();
        await connection.InvokeAsync("JoinRoom", "general");
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("MessagePosted", msg => received.TrySetResult(msg));

        await _host.Client.PostAsJsonAsync("api/rooms/general/import", new { text = "Owner: a pasted line" });

        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("owner", payload.GetProperty("authorId").GetString());
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Import_to_an_unknown_room_is_404()
    {
        var response = await _host.Client.PostAsJsonAsync("api/rooms/nope/import", new { text = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Import_of_blank_text_is_a_400()
    {
        var response = await _host.Client.PostAsJsonAsync("api/rooms/general/import", new { text = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Export_produces_markdown_with_every_message_author_and_timestamp_in_id_order()
    {
        await using var claude = await _host.ClientFor("claude");
        await using var codex = await _host.ClientFor("codex");
        await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "first" });
        await codex.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "second" });

        var response = await _host.Client.GetAsync("api/rooms/general/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/markdown", response.Content.Headers.ContentType!.MediaType);
        var markdown = await response.Content.ReadAsStringAsync();

        int firstIdx = markdown.IndexOf("first", StringComparison.Ordinal);
        int secondIdx = markdown.IndexOf("second", StringComparison.Ordinal);
        Assert.True(firstIdx >= 0 && secondIdx >= 0 && firstIdx < secondIdx);
        Assert.Contains("claude", markdown);
        Assert.Contains("codex", markdown);
    }

    [Fact]
    public async Task Export_of_an_unknown_room_is_404()
    {
        var response = await _host.Client.GetAsync("api/rooms/nope/export");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task M8_A6_api_participants_returns_the_roster_in_camel_case()
    {
        var response = await _host.Client.GetAsync("/api/participants");
        response.EnsureSuccessStatusCode();
        var rows = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToArray();
        Assert.Equal(ChopDb.SeedRoster.Select(p => p.Id), rows.Select(r => r.GetProperty("id").GetString()));
        var owner = rows.Single(r => r.GetProperty("id").GetString() == "owner");
        Assert.Equal("human", owner.GetProperty("kind").GetString());
        Assert.Equal("Owner", owner.GetProperty("displayName").GetString());
        var sol = rows.Single(r => r.GetProperty("id").GetString() == "gpt-5.6-sol");
        Assert.Equal("codex", sol.GetProperty("host").GetString());
        Assert.Equal("gpt-5.6-sol", sol.GetProperty("model").GetString());
    }
}
