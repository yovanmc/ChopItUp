import { useCallback, useEffect, useRef, useState } from 'react';
import { HubConnectionState, type HubConnection } from '@microsoft/signalr';
import * as api from './api';
import { createConnection, type Liveness } from './realtime';
import Composer from './Composer';
import ImportDialog from './ImportDialog';
import RoomHeader from './RoomHeader';
import RoomRail from './RoomRail';
import Thread from './Thread';
import type { Message, Room } from './types';

export default function App() {
  const [rooms, setRooms] = useState<Room[]>([]);
  const [roomId, setRoomId] = useState<string | null>(null);
  const [messages, setMessages] = useState<Message[]>([]);
  const [liveness, setLiveness] = useState<Liveness>('connecting');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [importOpen, setImportOpen] = useState(false);

  const hub = useRef<HubConnection | null>(null);
  const currentRoom = useRef<string | null>(null);
  const lastId = useRef(0);

  /** Every path into the thread goes through here. Dedup is by id because the owner's own post
   *  arrives twice — once as the POST's response, once as the broadcast — and a reconnect's catch-up
   *  read overlaps whatever the socket already delivered. */
  const merge = useCallback((incoming: Message[]) => {
    if (incoming.length === 0) return;
    setMessages((previous) => {
      const seen = new Set(previous.map((m) => m.id));
      const fresh = incoming.filter((m) => !seen.has(m.id));
      if (fresh.length === 0) return previous;
      const next = previous.concat(fresh);
      for (let i = 1; i < next.length; i++) {
        if (next[i]!.id < next[i - 1]!.id) {
          next.sort((a, b) => a.id - b.id);
          break;
        }
      }
      return next;
    });
  }, []);

  useEffect(() => {
    lastId.current = messages.length > 0 ? messages[messages.length - 1]!.id : 0;
  }, [messages]);

  const refreshRooms = useCallback(async (signal?: AbortSignal) => {
    const loaded = await api.listRooms(signal);
    setRooms(loaded);
    setRoomId((current) => current ?? loaded[0]?.id ?? null);
  }, []);

  useEffect(() => {
    const abort = new AbortController();
    refreshRooms(abort.signal).catch((failure) => {
      if (!abort.signal.aborted) setError(api.describeError(failure));
    });
    return () => abort.abort();
  }, [refreshRooms]);

  // The rail's counts for rooms we are not watching go stale by design — only the open room has a
  // live subscription. Coming back to the window is the cheapest honest moment to re-read them.
  useEffect(() => {
    function onFocus() {
      refreshRooms().catch(() => {
        /* a failed background refresh must not replace what is on screen */
      });
    }
    window.addEventListener('focus', onFocus);
    return () => window.removeEventListener('focus', onFocus);
  }, [refreshRooms]);

  // One connection for the life of the app; rooms are joined and left on it.
  useEffect(() => {
    const connection = createConnection();
    hub.current = connection;

    connection.on('MessagePosted', (message: Message) => {
      if (message.roomId === currentRoom.current) merge([message]);
      setRooms((previous) =>
        previous.map((room) =>
          room.id === message.roomId
            ? { ...room, messageCount: room.messageCount + 1, lastMessageId: message.id }
            : room,
        ),
      );
    });
    connection.onreconnecting(() => setLiveness('connecting'));
    connection.onclose(() => setLiveness('offline'));
    connection.onreconnected(() => {
      setLiveness('live');
      const room = currentRoom.current;
      if (!room) return;
      // Rejoin first, then read the gap the socket missed while it was down.
      void connection
        .invoke('JoinRoom', room)
        .then(() => api.readMessages(room, lastId.current))
        .then(merge)
        .catch((failure) => setError(api.describeError(failure)));
    });

    connection
      .start()
      .then(() => {
        setLiveness('live');
        const room = currentRoom.current;
        return room ? connection.invoke('JoinRoom', room) : undefined;
      })
      .catch(() => setLiveness('offline'));

    return () => {
      hub.current = null;
      void connection.stop();
    };
  }, [merge]);

  // Join before reading, so a post that lands mid-read is broadcast to us and merged rather than
  // dropping into the gap between the read and the subscription.
  useEffect(() => {
    currentRoom.current = roomId;
    if (!roomId) return;
    const connection = hub.current;
    if (connection && connection.state === HubConnectionState.Connected) {
      void connection.invoke('JoinRoom', roomId).catch(() => setLiveness('offline'));
    }
    return () => {
      if (connection && connection.state === HubConnectionState.Connected) {
        void connection.invoke('LeaveRoom', roomId).catch(() => undefined);
      }
    };
  }, [roomId]);

  useEffect(() => {
    if (!roomId) return;
    const abort = new AbortController();
    setLoading(true);
    setMessages([]);
    api
      .readMessages(roomId, 0, abort.signal)
      .then((loaded) => {
        if (abort.signal.aborted) return;
        setError(null);
        merge(loaded);
      })
      .catch((failure) => {
        if (!abort.signal.aborted) setError(api.describeError(failure));
      })
      .finally(() => {
        if (!abort.signal.aborted) setLoading(false);
      });
    return () => abort.abort();
  }, [roomId, merge]);

  const send = useCallback(
    async (body: string) => {
      if (!roomId) return;
      try {
        merge([await api.postMessage(roomId, body)]);
        setError(null);
      } catch (failure) {
        setError(api.describeError(failure));
        throw failure;
      }
    },
    [roomId, merge],
  );

  const activeRoom = rooms.find((room) => room.id === roomId) ?? null;

  return (
    <div className="app">
      <RoomRail rooms={rooms} activeRoomId={roomId} liveness={liveness} onSelect={setRoomId} />
      <main className="room">
        {activeRoom ? (
          <>
            <RoomHeader room={activeRoom} loadedCount={messages.length} onImport={() => setImportOpen(true)} />
            {error && (
              <p className="banner" role="alert">
                {error}
              </p>
            )}
            <Thread messages={messages} loading={loading} />
            <Composer roomName={activeRoom.name} disabled={false} onSend={send} />
          </>
        ) : (
          <p className="thread-note standalone">{error ?? 'Looking for rooms…'}</p>
        )}
      </main>
      {importOpen && activeRoom && (
        <ImportDialog
          roomId={activeRoom.id}
          roomName={activeRoom.name}
          onClose={() => setImportOpen(false)}
          onImported={merge}
        />
      )}
    </div>
  );
}
