using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChopItUp.Hub.Tests;

/// <summary>SignalR broadcast fires off the same <c>MessageSignal</c> event that <c>wait_for_message</c>
/// uses, so a post reaching a browser client cannot depend on which path stored it.</summary>
public sealed class RealtimeTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_realtime_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;

    public async Task InitializeAsync() => _host = await HubTestHost.StartAsync(_dir);
    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<HubConnection> ConnectAsync(string roomId)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_host.BaseAddress, "hub/rooms"))
            .Build();
        await connection.StartAsync();
        await connection.InvokeAsync("JoinRoom", roomId);
        return connection;
    }

    [Fact]
    public async Task A_post_via_the_mcp_tool_path_reaches_a_connected_signalr_client()
    {
        await using var connection = await ConnectAsync("general");
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("MessagePosted", msg => received.TrySetResult(msg));

        await using var claude = await _host.ClientFor("claude");
        await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "hello over signalr" });

        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("hello over signalr", payload.GetProperty("body").GetString());
        Assert.Equal("claude", payload.GetProperty("authorId").GetString());   // hub-stamped, not client-supplied
        Assert.Equal("general", payload.GetProperty("roomId").GetString());
        Assert.True(payload.GetProperty("id").GetInt64() > 0);
    }

    [Fact]
    public async Task A_client_in_a_different_room_does_not_receive_the_broadcast()
    {
        await using var general = await ConnectAsync("general");
        await using var other = await ConnectAsync("other-room-that-does-not-exist-as-a-group-target");
        var generalReceived = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherReceived = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        general.On<JsonElement>("MessagePosted", msg => generalReceived.TrySetResult(msg));
        other.On<JsonElement>("MessagePosted", msg => otherReceived.TrySetResult(msg));

        await using var claude = await _host.ClientFor("claude");
        await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "only for general" });

        await generalReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        Assert.False(otherReceived.Task.IsCompleted);
    }

    [Fact]
    public async Task A_deduplicated_post_does_not_broadcast_again()
    {
        await using var connection = await ConnectAsync("general");
        var count = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("MessagePosted", _ =>
        {
            if (Interlocked.Increment(ref count) == 2) second.TrySetResult();
        });

        await using var claude = await _host.ClientFor("claude");
        await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "original", ["client_key"] = "dedupe-1" });
        // Wait for the first broadcast to land before firing the dup, so we know a lone broadcast
        // afterwards can only be the dup (if the bug exists) rather than a race on the first.
        await Task.Delay(500);
        await claude.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "discarded", ["client_key"] = "dedupe-1" });
        await Task.Delay(500);

        Assert.Equal(1, count);
        Assert.False(second.Task.IsCompleted);
    }

    [Fact]
    public async Task R36_a_reply_posted_through_the_web_api_carries_replyToId_in_the_broadcast()
    {
        _host.AuthorizeAs(ChopItUp.Core.Storage.ChopDb.OwnerParticipantId);
        var root = await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "root" });
        var rootId = JsonDocument.Parse(await root.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();
        await using var connection = await ConnectAsync("general");
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("MessagePosted", msg => received.TrySetResult(msg));

        var reply = await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "a reply", replyToId = rootId });
        Assert.Equal(System.Net.HttpStatusCode.Created, reply.StatusCode);

        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(rootId, payload.GetProperty("replyToId").GetInt64());
    }

    /// <summary>Row 42 AC3: the SignalR payload carries the imported flag too, so a connected browser
    /// can tell pasted history from a live post.</summary>
    [Fact]
    public async Task Row42_AC3_an_import_broadcast_carries_imported_true_and_a_post_false()
    {
        _host.AuthorizeAs(ChopItUp.Core.Storage.ChopDb.OwnerParticipantId);
        await using var connection = await ConnectAsync("general");
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("MessagePosted", msg => received.TrySetResult(msg));

        await _host.Client.PostAsJsonAsync("api/rooms/general/import", new { text = "Owner: pasted history" });
        var importedPayload = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(importedPayload.GetProperty("imported").GetBoolean());

        received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "live post" });
        var livePayload = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(livePayload.GetProperty("imported").GetBoolean());
    }
}
