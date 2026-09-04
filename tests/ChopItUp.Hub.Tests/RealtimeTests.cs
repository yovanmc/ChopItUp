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
}
