import { memo } from 'react';
import type { Room } from './types';
import type { Liveness } from './realtime';

interface Props {
  rooms: Room[];
  activeRoomId: string | null;
  liveness: Liveness;
  showArchived: boolean;
  onSelect: (roomId: string) => void;
  onNewRoom: () => void;
  onToggleArchived: () => void;
}

const LIVENESS_LABEL: Record<Liveness, string> = {
  connecting: 'connecting',
  live: 'live',
  offline: 'offline',
};

/** M9: the rail is a chat list. Rooms arrive newest activity first (the hub orders them; App re-sorts
 *  after a live bump), an unread badge replaces the count on rooms with unread messages that are not
 *  open, an empty room shows no count at all, a folder mark says the room has a directory, and
 *  archived rooms show only behind the toggle. */
function RoomRail({ rooms, activeRoomId, liveness, showArchived, onSelect, onNewRoom, onToggleArchived }: Props) {
  return (
    <nav className="rail" aria-label="Rooms">
      <div className="rail-head">
        <span className="wordmark">Chop It Up</span>
        <span className={`liveness liveness-${liveness}`} title={`Realtime connection: ${LIVENESS_LABEL[liveness]}`}>
          <span className="dot" aria-hidden="true" />
          {LIVENESS_LABEL[liveness]}
        </span>
      </div>
      <ul className="room-list">
        {rooms.map((room) => {
          const active = room.id === activeRoomId;
          const archived = room.archivedAt !== null;
          const unread = !active && room.unread > 0;
          return (
            <li key={room.id}>
              <button
                type="button"
                className={`room-item${active ? ' active' : ''}${archived ? ' archived' : ''}`}
                onClick={() => onSelect(room.id)}
                aria-current={active ? 'true' : undefined}
              >
                {room.directory !== null && (
                  <span className="room-dir" title={room.directory} aria-label="Has a directory">
                    ▣
                  </span>
                )}
                <span className="room-name">{room.name}</span>
                {archived && <span className="room-archived">archived</span>}
                {unread ? (
                  <span className="room-unread" aria-label={`${room.unread} unread`}>
                    {room.unread > 99 ? '99+' : room.unread}
                  </span>
                ) : (
                  room.messageCount > 0 && (
                    <span className="room-count" title={`${room.messageCount} messages`}>
                      {room.messageCount}
                    </span>
                  )
                )}
              </button>
            </li>
          );
        })}
        {rooms.length === 0 && <li className="rail-empty">No rooms yet.</li>}
      </ul>
      <div className="rail-foot">
        <button type="button" className="quiet" onClick={onNewRoom}>
          New room
        </button>
        <label>
          <input type="checkbox" checked={showArchived} onChange={onToggleArchived} />
          Show archived
        </label>
      </div>
    </nav>
  );
}

export default memo(RoomRail);
