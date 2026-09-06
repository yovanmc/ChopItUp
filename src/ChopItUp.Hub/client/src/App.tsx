import { useCallback, useEffect, useRef, useState } from 'react';
import { HubConnectionState, type HubConnection } from '@microsoft/signalr';
import * as api from './api';
import { createConnection, type Liveness } from './realtime';
import Composer from './Composer';
import ExchangeBar from './ExchangeBar';
import ImportDialog from './ImportDialog';
import MemoryImportDialog from './MemoryImportDialog';
import MemoryPanel from './MemoryPanel';
import NewRoomDialog from './NewRoomDialog';
import RoomHeader from './RoomHeader';
import RoomRail from './RoomRail';
import Thread from './Thread';
import TrailDialog from './TrailDialog';
import { isHuman, isSystem, setRoster } from './participants';
import type { ExchangeSnapshot, MemoryImportResult, MemoryProposal, Message, Room } from './types';

/** Shared by the fetch paths (GET on room switch/reconnect) and the socket path (`ExchangeChanged`):
 *  `null` accepts anything, a `seq` bump for the room already shown always wins, and a snapshot for a
 *  different room displaces whatever was left over from before — the room-switch effect resets to
 *  `null` first, so this only matters as a safety net if that reset and this update ever race. */
function applyExchange(current: ExchangeSnapshot | null, incoming: ExchangeSnapshot): ExchangeSnapshot | null {
  if (current === null || incoming.roomId !== current.roomId || incoming.seq > current.seq) return incoming;
  return current;
}

/** The chat-list order. Every stamp is UTC round-trip text, so string order is time order. */
function byActivity(rooms: Room[]): Room[] {
  return [...rooms].sort((a, b) => (a.lastActivityAt < b.lastActivityAt ? 1 : a.lastActivityAt > b.lastActivityAt ? -1 : 0));
}

export default function App() {
  const [rooms, setRooms] = useState<Room[]>([]);
  const [roomId, setRoomId] = useState<string | null>(null);
  const [messages, setMessages] = useState<Message[]>([]);
  const [liveness, setLiveness] = useState<Liveness>('connecting');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [importOpen, setImportOpen] = useState(false);
  const [exchange, setExchange] = useState<ExchangeSnapshot | null>(null);
  const [stopping, setStopping] = useState(false);
  const [proposals, setProposals] = useState<MemoryProposal[]>([]);
  const [deciding, setDeciding] = useState<number | null>(null);
  const [memoryImportOpen, setMemoryImportOpen] = useState(false);
  const [showArchived, setShowArchived] = useState(false);
  const [roomDialog, setRoomDialog] = useState<null | { mode: 'create' } | { mode: 'bind'; room: Room }>(null);
  const [trailOpen, setTrailOpen] = useState(false);
  const [roomBusy, setRoomBusy] = useState(false);

  const hub = useRef<HubConnection | null>(null);
  const currentRoom = useRef<string | null>(null);
  const lastId = useRef(0);
  const showArchivedRef = useRef(false);
  const readTimer = useRef<number | null>(null);

  /** Pending proposals of the open room. Guarded on the ref so a fetch that outlives a room switch
   *  cannot paint the previous room's cards into the new one. */
  const loadProposals = useCallback(async (room: string, signal?: AbortSignal) => {
    const list = await api.listProposals(room, signal);
    if (currentRoom.current === room) setProposals(list);
  }, []);

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
    const loaded = await api.listRooms(showArchivedRef.current, signal);
    setRooms(loaded);
    // Keep the open room if it is still listed; otherwise (archived and hidden, or gone) the first one.
    setRoomId((current) => (current !== null && loaded.some((room) => room.id === current) ? current : (loaded[0]?.id ?? null)));
  }, []);

  const toggleArchived = useCallback(() => {
    showArchivedRef.current = !showArchivedRef.current;
    setShowArchived(showArchivedRef.current);
    refreshRooms().catch((failure) => setError(api.describeError(failure)));
  }, [refreshRooms]);

  /** M9: the owner's read cursor moves while the room is open. Debounced, so a burst of messages is
   *  one call; dropped if the room changed before it fired. */
  const scheduleRead = useCallback((room: string) => {
    if (readTimer.current !== null) window.clearTimeout(readTimer.current);
    readTimer.current = window.setTimeout(() => {
      readTimer.current = null;
      if (currentRoom.current === room) api.markRead(room).catch(() => undefined);
    }, 750);
  }, []);

  // The roster loads BEFORE the first room is selected, so no message ever renders without it.
  useEffect(() => {
    const abort = new AbortController();
    api
      .listParticipants(abort.signal)
      .then((list) => {
        setRoster(list);
        return refreshRooms(abort.signal);
      })
      .catch((failure) => {
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
      const open = message.roomId === currentRoom.current;
      if (open) merge([message]);
      // Every memory state change is announced by a hub note that starts with "Memory " (a proposal,
      // an import, an approval, a rejection); that note IS the refresh signal — no second event.
      if (open && isSystem(message.authorId) && message.body.startsWith('Memory ')) {
        loadProposals(message.roomId).catch(() => undefined);
      }
      if (open && !isHuman(message.authorId)) scheduleRead(message.roomId);
      // The owner's own posts advance the owner's cursor on the hub, so they never count as unread.
      setRooms((previous) =>
        byActivity(
          previous.map((room) =>
            room.id === message.roomId
              ? {
                  ...room,
                  messageCount: room.messageCount + 1,
                  lastMessageId: message.id,
                  lastActivityAt: message.createdAt,
                  unread: open || isHuman(message.authorId) ? room.unread : room.unread + 1,
                }
              : room,
          ),
        ),
      );
    });
    connection.on('ExchangeChanged', (snapshot: ExchangeSnapshot) => {
      if (snapshot.roomId !== currentRoom.current) return;
      setExchange((previous) => applyExchange(previous, snapshot));
    });
    connection.onreconnecting(() => setLiveness('connecting'));
    connection.onclose(() => setLiveness('offline'));
    connection.onreconnected(() => {
      setLiveness('live');
      const room = currentRoom.current;
      if (!room) return;
      // Rejoin first, then read the gap the socket missed while it was down — including any
      // exchange stop/conclude that landed while we were offline and so never reached us as an event.
      void connection
        .invoke('JoinRoom', room)
        .then(() =>
          Promise.all([
            api.readMessages(room, lastId.current).then(merge),
            api.getExchange(room).then((snapshot) => setExchange((previous) => applyExchange(previous, snapshot))),
            loadProposals(room),
          ]),
        )
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
  }, [merge, loadProposals, scheduleRead]);

  // Join before reading, so a post that lands mid-read is broadcast to us and merged rather than
  // dropping into the gap between the read and the subscription. The exchange snapshot is reset here
  // and re-fetched only after the join settles, so a stale bar from the last room never lingers and
  // an in-flight fetch for the room we just left can't land after we've moved on.
  useEffect(() => {
    currentRoom.current = roomId;
    setExchange(null);
    setProposals([]);
    if (!roomId) return;
    const connection = hub.current;
    const abort = new AbortController();
    const joined =
      connection && connection.state === HubConnectionState.Connected
        ? connection.invoke('JoinRoom', roomId).catch(() => setLiveness('offline'))
        : Promise.resolve();
    void joined.then(() => {
      if (abort.signal.aborted) return undefined;
      return Promise.all([
        api
          .getExchange(roomId, abort.signal)
          .then((snapshot) => {
            if (abort.signal.aborted) return;
            setExchange((previous) => applyExchange(previous, snapshot));
          })
          .catch((failure) => {
            if (!abort.signal.aborted) setError(api.describeError(failure));
          }),
        loadProposals(roomId, abort.signal).catch((failure) => {
          if (!abort.signal.aborted) setError(api.describeError(failure));
        }),
      ]);
    });
    return () => {
      abort.abort();
      if (readTimer.current !== null) {
        window.clearTimeout(readTimer.current);
        readTimer.current = null;
      }
      if (connection && connection.state === HubConnectionState.Connected) {
        void connection.invoke('LeaveRoom', roomId).catch(() => undefined);
      }
    };
  }, [roomId, loadProposals]);

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
        if (loaded.length > 0) api.markRead(roomId).catch(() => undefined); // opening a room reads it
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

  // D17: Stop is "step in and end it" — the owner's next message opens a fresh exchange, this call
  // only ends the current one. The banner is the existing error surface; nothing new for failures.
  const stop = useCallback(async () => {
    if (!roomId) return;
    setStopping(true);
    try {
      const snapshot = await api.stopExchange(roomId);
      setExchange((previous) => applyExchange(previous, snapshot));
      setError(null);
    } catch (failure) {
      setError(api.describeError(failure));
    } finally {
      setStopping(false);
    }
  }, [roomId]);

  // D15: the owner's word, in the room. The card leaves the panel on success; the hub's note is what
  // the thread shows. Failures (409 already decided, 404) surface in the banner and the list reloads.
  const decide = useCallback(
    async (id: number, decision: 'approve' | 'reject') => {
      if (!roomId) return;
      setDeciding(id);
      try {
        await api.decideProposal(id, decision);
        setProposals((previous) => previous.filter((p) => p.id !== id));
        setError(null);
      } catch (failure) {
        setError(api.describeError(failure));
        loadProposals(roomId).catch(() => undefined);
      } finally {
        setDeciding(null);
      }
    },
    [roomId, loadProposals],
  );

  const onMemoryImported = useCallback(
    (_result: MemoryImportResult) => {
      if (roomId) loadProposals(roomId).catch(() => undefined);
    },
    [roomId, loadProposals],
  );

  const selectRoom = useCallback((id: string) => {
    setRoomId(id);
    setRooms((previous) => previous.map((room) => (room.id === id ? { ...room, unread: 0 } : room)));
  }, []);

  const onRoomCreated = useCallback((room: Room) => {
    setRoomDialog(null);
    setRooms((previous) => byActivity([room, ...previous.filter((r) => r.id !== room.id)]));
    setRoomId(room.id);
  }, []);

  const onRoomBound = useCallback((room: Room) => {
    setRoomDialog(null);
    setRooms((previous) => previous.map((r) => (r.id === room.id ? room : r)));
  }, []);

  // Archive hides, never blocks (plan decision 14): the hub keeps the room and its folder; the list
  // is re-read so the selection moves to the first visible room when the open one leaves the list.
  const toggleArchive = useCallback(async () => {
    if (!roomId) return;
    const room = rooms.find((r) => r.id === roomId);
    if (!room) return;
    setRoomBusy(true);
    try {
      if (room.archivedAt !== null) await api.unarchiveRoom(roomId);
      else await api.archiveRoom(roomId);
      setError(null);
      await refreshRooms();
    } catch (failure) {
      setError(api.describeError(failure));
    } finally {
      setRoomBusy(false);
    }
  }, [roomId, rooms, refreshRooms]);

  const activeRoom = rooms.find((room) => room.id === roomId) ?? null;

  return (
    <div className="app">
      <RoomRail
        rooms={rooms}
        activeRoomId={roomId}
        liveness={liveness}
        showArchived={showArchived}
        onSelect={selectRoom}
        onNewRoom={() => setRoomDialog({ mode: 'create' })}
        onToggleArchived={toggleArchived}
      />
      <main className="room">
        {activeRoom ? (
          <>
            <RoomHeader
              room={activeRoom}
              loadedCount={messages.length}
              busy={roomBusy}
              onImport={() => setImportOpen(true)}
              onImportMemory={() => setMemoryImportOpen(true)}
              onBind={() => setRoomDialog({ mode: 'bind', room: activeRoom })}
              onArchive={() => void toggleArchive()}
              onTrail={() => setTrailOpen(true)}
            />
            {error && (
              <p className="banner" role="alert">
                {error}
              </p>
            )}
            <Thread messages={messages} loading={loading} />
            <MemoryPanel
              proposals={proposals}
              busyId={deciding}
              locked={(exchange?.inFlight.length ?? 0) > 0}
              onDecide={decide}
            />
            <ExchangeBar exchange={exchange} stopping={stopping} onStop={stop} />
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
      {memoryImportOpen && activeRoom && (
        <MemoryImportDialog
          roomId={activeRoom.id}
          roomName={activeRoom.name}
          onClose={() => setMemoryImportOpen(false)}
          onImported={onMemoryImported}
        />
      )}
      {roomDialog && (
        <NewRoomDialog
          mode={roomDialog.mode}
          room={roomDialog.mode === 'bind' ? roomDialog.room : undefined}
          onClose={() => setRoomDialog(null)}
          onDone={roomDialog.mode === 'create' ? onRoomCreated : onRoomBound}
        />
      )}
      {trailOpen && activeRoom && <TrailDialog room={activeRoom} onClose={() => setTrailOpen(false)} />}
    </div>
  );
}
