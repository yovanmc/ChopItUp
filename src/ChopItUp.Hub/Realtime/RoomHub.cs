using Microsoft.AspNetCore.SignalR;

namespace ChopItUp.Hub.Realtime;

/// <summary>The browser-facing side of the M3 realtime fan-out. Clients join a room's SignalR group
/// to receive that room's <c>MessagePosted</c> events; nothing here posts a message — every write
/// still goes through <c>MessageStore.Post</c> (MCP tool or <c>/api</c>), and <see
/// cref="ChopItUp.Core.Messaging.MessageSignal.Posted"/> is what triggers the broadcast (wired up in
/// <c>Hosting/HubHost.cs</c>). No auth on this hub, per D2: loopback is the boundary.</summary>
public sealed class RoomHub : Microsoft.AspNetCore.SignalR.Hub
{
    public Task JoinRoom(string roomId) => Groups.AddToGroupAsync(Context.ConnectionId, roomId);

    public Task LeaveRoom(string roomId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
}
