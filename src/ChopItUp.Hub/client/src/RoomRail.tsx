import { memo } from 'react';
import type { Room } from './types';
import type { Liveness } from './realtime';

interface Props {
  rooms: Room[];
  activeRoomId: string | null;
  liveness: Liveness;
  showArchived: boolean;
  /** Row 28: true once a `markRead` has been refused for want of an owner credential this session.
   *  That call is a background write and fails silently by design, so every unread badge in this rail
   *  is then wrong and stays wrong — this is the flag that stops the rail lying about it. */
  unreadBlocked: boolean;
  onSelect: (roomId: string) => void;
  onNewRoom: () => void;
  onToggleArchived: () => void;
  /** Raises the paste prompt. The line has to be reachable, not just readable: the other two places
   *  that take a token are a skill card that may not be on screen and a refused deliberate write the
   *  owner has not attempted. */
  onFixUnread: () => void;
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
function RoomRail({
  rooms,
  activeRoomId,
  liveness,
  showArchived,
  unreadBlocked,
  onSelect,
  onNewRoom,
  onToggleArchived,
  onFixUnread,
}: Props) {
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
      {/* Row 28, AC5's second half. Every badge above this line is a count the hub will not let this
          browser clear, so the rail says so where the wrong numbers are, and pressing it opens the
          paste prompt. Not a `role="alert"`: the owner did not do anything to cause it. */}
      {unreadBlocked && (
        <button type="button" className="quiet rail-unread-blocked" onClick={onFixUnread}>
          Unread counts are stuck until you paste the owner token.
        </button>
      )}
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
