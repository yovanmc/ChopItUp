using System.Text.Json;
using ChopItUp.Core.Storage;

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
        Assert.Equal(4, tools.Count);
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

    [Fact]
    public async Task A4_retrying_a_post_with_the_same_client_key_stores_one_message()
    {
        await using var claude = await _host.ClientFor("claude");

        var first = HubTestHost.Json(await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "the only message", ["client_key"] = "retry-1" }));
        Assert.False(first.TryGetProperty("deduplicated", out _));   // A5's shape survives on the first attempt

        var retry = HubTestHost.Json(await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "the only message", ["client_key"] = "retry-1" }));
        Assert.True(retry.GetProperty("deduplicated").GetBoolean());
        Assert.Equal(first.GetProperty("id").GetInt64(), retry.GetProperty("id").GetInt64());
        Assert.Equal("the only message", retry.GetProperty("body").GetString());

        var rooms = HubTestHost.Json(await claude.CallToolAsync("list_rooms", new Dictionary<string, object?>()));
        Assert.Equal(1, rooms.GetProperty("rooms")[0].GetProperty("message_count").GetInt32());
    }

    [Fact]
    public async Task A5_a_post_without_a_client_key_has_no_deduplicated_field()
    {
        await using var claude = await _host.ClientFor("claude");
        var posted = HubTestHost.Json(await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "plain post" }));
        Assert.False(posted.TryGetProperty("deduplicated", out _));
        Assert.Equal(
            new[] { "author_id", "body", "created_at", "id", "room_id" },
            posted.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Client_key_is_advertised_on_the_post_message_schema()
    {
        await using var claude = await _host.ClientFor("claude");
        var post = (await claude.ListToolsAsync()).Single(t => t.Name == "post_message");
        Assert.True(post.JsonSchema.GetProperty("properties").TryGetProperty("client_key", out var key));
        if (post.JsonSchema.TryGetProperty("required", out var required))
            Assert.DoesNotContain("client_key", required.EnumerateArray().Select(e => e.GetString()));
        // Being absent from "required" is not enough: a host reads the description, not the schema
        // keyword, so the description has to say it out loud.
        Assert.Contains("optional", key.GetProperty("description").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A9_health_reports_whether_hosts_are_actually_sending_keys()
    {
        await using var claude = await _host.ClientFor("claude");
        await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "keyed", ["client_key"] = "k-1" });
        await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "keyless one" });
        await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "keyless two" });

        var health = System.Text.Json.JsonDocument.Parse(await _host.Client.GetStringAsync("/health")).RootElement;
        var row = health.GetProperty("key_usage").EnumerateArray().Single(r => r.GetProperty("author").GetString() == "claude");
        Assert.Equal(1, row.GetProperty("keyed").GetInt64());
        Assert.Equal(2, row.GetProperty("keyless").GetInt64());
    }

    [Fact]
    public async Task A_deduplicated_post_does_not_wake_a_waiter()
    {
        await using var claude = await _host.ClientFor("claude");
        await using var codex = await _host.ClientFor("codex");

        await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "original", ["client_key"] = "dup-1" });
        await codex.CallToolAsync("read_messages", new Dictionary<string, object?> { ["room_id"] = "general" });   // codex is now caught up

        var waiting = codex.CallToolAsync("wait_for_message", new Dictionary<string, object?> { ["room_id"] = "general", ["timeout_seconds"] = 3 }).AsTask();
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);

        var duplicate = HubTestHost.Json(await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "text that is thrown away", ["client_key"] = "dup-1" }));
        Assert.True(duplicate.GetProperty("deduplicated").GetBoolean());

        var timedOut = HubTestHost.Json(await waiting.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(0, timedOut.GetProperty("messages").GetArrayLength());

        // ...and the signal still works, so the assertion above cannot pass by the wait being broken.
        var second = codex.CallToolAsync("wait_for_message", new Dictionary<string, object?> { ["room_id"] = "general", ["timeout_seconds"] = 20 }).AsTask();
        await Task.Delay(300);
        Assert.False(second.IsCompleted);
        await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "genuinely new", ["client_key"] = "fresh-2" });
        var woke = HubTestHost.Json(await second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("genuinely new", woke.GetProperty("messages")[0].GetProperty("body").GetString());
    }

    [Fact]
    public async Task Every_read_reply_reports_the_cursor()
    {
        await using var claude = await _host.ClientFor("claude");
        await using var codex = await _host.ClientFor("codex");
        foreach (var body in new[] { "one", "two", "three" })
            await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = body });

        var implicitRead = HubTestHost.Json(await codex.CallToolAsync("read_messages", new Dictionary<string, object?> { ["room_id"] = "general" }));
        Assert.Equal(3, implicitRead.GetProperty("messages").GetArrayLength());
        Assert.Equal(3, implicitRead.GetProperty("next_after_id").GetInt64());
        Assert.Equal(3, implicitRead.GetProperty("cursor").GetInt64());

        // The explicit form is a peek: it reports the same stored cursor, untouched.
        var peek = HubTestHost.Json(await codex.CallToolAsync("read_messages", new Dictionary<string, object?> { ["room_id"] = "general", ["after_id"] = 0 }));
        Assert.Equal(3, peek.GetProperty("next_after_id").GetInt64());
        Assert.Equal(3, peek.GetProperty("cursor").GetInt64());

        // A timeout with nothing to show is exactly when a model needs to know where it stands.
        var timedOut = HubTestHost.Json(await codex.CallToolAsync("wait_for_message", new Dictionary<string, object?> { ["room_id"] = "general", ["timeout_seconds"] = 1 }));
        Assert.Equal(0, timedOut.GetProperty("messages").GetArrayLength());
        Assert.Equal(3, timedOut.GetProperty("cursor").GetInt64());
    }

    [Fact]
    public async Task M8_A4_list_rooms_returns_the_roster()
    {
        await using var claude = await _host.ClientFor("claude");
        var rooms = HubTestHost.Json(await claude.CallToolAsync("list_rooms", new Dictionary<string, object?>()));
        var participants = rooms.GetProperty("participants").EnumerateArray().ToArray();
        Assert.Equal(ChopDb.SeedRoster.Select(p => p.Id), participants.Select(p => p.GetProperty("id").GetString()));
        var fable = participants.Single(p => p.GetProperty("id").GetString() == "fable");
        Assert.Equal("claude", fable.GetProperty("host").GetString());
        Assert.Equal("fable", fable.GetProperty("model").GetString());
        Assert.Equal("model", fable.GetProperty("kind").GetString());
        Assert.Equal("Fable", fable.GetProperty("display_name").GetString());
        // A4 says every row carries `model`: for an app-backed row it is present and null, not absent.
        var claudeRow = participants.Single(p => p.GetProperty("id").GetString() == "claude");
        Assert.True(claudeRow.TryGetProperty("model", out var model));
        Assert.Equal(JsonValueKind.Null, model.ValueKind);
    }

    [Fact]
    public async Task M8_A2_a_spawn_row_can_authenticate_and_post_today()
    {
        // The row is inert until M5 spawns it, but its token is real: a hand-run headless client
        // holding it must be a first-class participant already.
        await using var opus = await _host.ClientFor("opus");
        var posted = HubTestHost.Json(await opus.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "hello from opus" }));
        Assert.Equal("opus", posted.GetProperty("author_id").GetString());
    }
}
