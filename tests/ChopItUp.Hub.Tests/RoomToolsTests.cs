namespace ChopItUp.Hub.Tests;

public sealed class RoomToolsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_tools_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;

    public async Task InitializeAsync() => _host = await HubTestHost.StartAsync(_dir);
    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Tools_list_is_exactly_the_four_room_tools()
    {
        await using var client = await _host.ClientFor("claude");
        var tools = await client.ListToolsAsync();
        Assert.Equal(new[] { "list_rooms", "post_message", "read_messages", "wait_for_message" }, tools.Select(t => t.Name).OrderBy(n => n));
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));

        // A7's default is part of the published contract: the SDK emits parameter defaults into the input schema.
        var wait = tools.Single(t => t.Name == "wait_for_message");
        Assert.Equal(25, wait.JsonSchema.GetProperty("properties").GetProperty("timeout_seconds").GetProperty("default").GetInt32());
    }

    [Theory]
    [InlineData(500, 50)]
    [InlineData(51, 50)]
    [InlineData(25, 25)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void Wait_timeout_is_clamped_to_one_through_fifty_seconds(int requested, int expected) =>
        Assert.Equal(expected, ChopItUp.Hub.Mcp.RoomTools.ClampWaitSeconds(requested));

    [Fact]
    public async Task Post_is_authored_by_the_token_owner_and_read_back_in_order()
    {
        await using var claude = await _host.ClientFor("claude");
        await using var codex = await _host.ClientFor("codex");

        var posted = HubTestHost.Json(await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "hello from claude" }));
        Assert.Equal("claude", posted.GetProperty("author_id").GetString());
        long firstId = posted.GetProperty("id").GetInt64();

        await codex.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "hi back" });

        var page = HubTestHost.Json(await claude.CallToolAsync("read_messages", new Dictionary<string, object?> { ["room_id"] = "general", ["after_id"] = 0 }));
        var msgs = page.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, msgs.Count);
        Assert.Equal(firstId, msgs[0].GetProperty("id").GetInt64());
        Assert.Equal("codex", msgs[1].GetProperty("author_id").GetString());
        Assert.True(msgs[1].GetProperty("id").GetInt64() > firstId);
        Assert.False(page.GetProperty("has_more").GetBoolean());
        Assert.Equal(msgs[1].GetProperty("id").GetInt64(), page.GetProperty("next_after_id").GetInt64());
    }

    [Fact]
    public async Task Client_cannot_choose_its_author()
    {
        await using var codex = await _host.ClientFor("codex");
        var posted = HubTestHost.Json(await codex.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "spoof", ["author_id"] = "claude" }));
        Assert.Equal("codex", posted.GetProperty("author_id").GetString());
    }

    [Fact]
    public async Task Read_without_after_id_uses_and_advances_the_callers_cursor()
    {
        await using var claude = await _host.ClientFor("claude");
        await using var codex = await _host.ClientFor("codex");
        await codex.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "one" });
        await codex.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "two" });

        var first = HubTestHost.Json(await claude.CallToolAsync("read_messages", new Dictionary<string, object?> { ["room_id"] = "general" }));
        Assert.Equal(2, first.GetProperty("messages").GetArrayLength());
        var second = HubTestHost.Json(await claude.CallToolAsync("read_messages", new Dictionary<string, object?> { ["room_id"] = "general" }));
        Assert.Equal(0, second.GetProperty("messages").GetArrayLength());

        await codex.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "three" });
        var third = HubTestHost.Json(await claude.CallToolAsync("read_messages", new Dictionary<string, object?> { ["room_id"] = "general" }));
        Assert.Equal("three", third.GetProperty("messages")[0].GetProperty("body").GetString());
    }

    [Fact]
    public async Task Explicit_after_id_is_a_peek_and_leaves_the_cursor_alone()
    {
        await using var claude = await _host.ClientFor("claude");
        await using var codex = await _host.ClientFor("codex");
        await codex.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "unread one" });
        await codex.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "unread two" });

        var peek = HubTestHost.Json(await claude.CallToolAsync("read_messages", new Dictionary<string, object?> { ["room_id"] = "general", ["after_id"] = 0 }));
        Assert.Equal(2, peek.GetProperty("messages").GetArrayLength());

        var rooms = HubTestHost.Json(await claude.CallToolAsync("list_rooms", new Dictionary<string, object?>()));
        Assert.Equal(2, rooms.GetProperty("rooms")[0].GetProperty("unread_count").GetInt32());

        var consume = HubTestHost.Json(await claude.CallToolAsync("read_messages", new Dictionary<string, object?> { ["room_id"] = "general" }));
        Assert.Equal(2, consume.GetProperty("messages").GetArrayLength());
        var again = HubTestHost.Json(await claude.CallToolAsync("read_messages", new Dictionary<string, object?> { ["room_id"] = "general" }));
        Assert.Equal(0, again.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Unknown_room_and_empty_body_are_tool_errors_not_crashes()
    {
        await using var claude = await _host.ClientFor("claude");
        var bad = await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "nope", ["body"] = "x" });
        Assert.True(bad.IsError);
        var empty = await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "  " });
        Assert.True(empty.IsError);
        var health = await _host.Client.GetAsync("/health");
        Assert.True(health.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Wait_returns_promptly_when_another_participant_posts()
    {
        await using var claude = await _host.ClientFor("claude");
        await using var codex = await _host.ClientFor("codex");
        // CallToolAsync returns a ValueTask; AsTask() so it can be observed and awaited later.
        var waiting = claude.CallToolAsync("wait_for_message", new Dictionary<string, object?> { ["room_id"] = "general", ["after_id"] = 0, ["timeout_seconds"] = 20 }).AsTask();
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);
        await codex.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "wake up" });
        var page = HubTestHost.Json(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("wake up", page.GetProperty("messages")[0].GetProperty("body").GetString());
    }

    [Fact]
    public async Task Wait_times_out_empty_and_clamps_the_timeout()
    {
        await using var claude = await _host.ClientFor("claude");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var page = HubTestHost.Json(await claude.CallToolAsync("wait_for_message", new Dictionary<string, object?> { ["room_id"] = "general", ["after_id"] = 0, ["timeout_seconds"] = 1 }));
        sw.Stop();
        Assert.Equal(0, page.GetProperty("messages").GetArrayLength());
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.8, 5);
        Assert.Equal(0, page.GetProperty("next_after_id").GetInt64());
    }

    [Fact]
    public async Task List_rooms_shows_general_with_counts_and_the_caller()
    {
        await using var claude = await _host.ClientFor("claude");
        await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "x" });
        var rooms = HubTestHost.Json(await claude.CallToolAsync("list_rooms", new Dictionary<string, object?>()));
        Assert.Equal("claude", rooms.GetProperty("you").GetString());
        var general = rooms.GetProperty("rooms")[0];
        Assert.Equal("general", general.GetProperty("id").GetString());
        Assert.Equal(1, general.GetProperty("message_count").GetInt32());
        Assert.Equal(0, general.GetProperty("unread_count").GetInt32());
    }
}
