import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr';

export type Liveness = 'connecting' | 'live' | 'offline';

/** The hub's RoomHub (`/hub/rooms`): `JoinRoom` / `LeaveRoom` and a `MessagePosted` callback that
 *  fires for every post regardless of which path stored it — MCP tool or the web API. */
export function createConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl('/hub/rooms')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
}
