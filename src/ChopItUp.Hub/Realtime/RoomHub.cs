using Microsoft.AspNetCore.SignalR;

namespace ChopItUp.Hub.Realtime;

/// <summary>The browser-facing side of the realtime fan-out. Clients join a room's SignalR group
/// to receive that room's <c>MessagePosted</c> events; nothing here posts a message. Every write
/// still goes through <c>MessageStore.Post</c> (MCP tool or <c>/api</c>, both gated by
/// <c>BearerTokenMiddleware</c>), and <see
/// cref="ChopItUp.Core.Messaging.MessageSignal.Posted"/> is what triggers the broadcast (wired up in
/// <c>Hosting/HubHost.cs</c>). No auth on this hub itself: <c>JoinRoom</c>/<c>LeaveRoom</c> are group
/// membership only, never a write, so this stays open deliberately, like GET and the other non-write
/// surfaces.</summary>
public sealed class RoomHub : Microsoft.AspNetCore.SignalR.Hub
{
    public Task JoinRoom(string roomId) => Groups.AddToGroupAsync(Context.ConnectionId, roomId);

    public Task LeaveRoom(string roomId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
}
