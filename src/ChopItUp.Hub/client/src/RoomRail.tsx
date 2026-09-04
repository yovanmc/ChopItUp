import { memo } from 'react';
import type { Room } from './types';
import type { Liveness } from './realtime';

interface Props {
  rooms: Room[];
  activeRoomId: string | null;
  liveness: Liveness;
  onSelect: (roomId: string) => void;
}

const LIVENESS_LABEL: Record<Liveness, string> = {
  connecting: 'connecting',
  live: 'live',
  offline: 'offline',
};

function RoomRail({ rooms, activeRoomId, liveness, onSelect }: Props) {
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
        {rooms.map((room) => (
          <li key={room.id}>
            <button
              type="button"
              className={`room-item${room.id === activeRoomId ? ' active' : ''}`}
              onClick={() => onSelect(room.id)}
              aria-current={room.id === activeRoomId ? 'true' : undefined}
            >
              <span className="room-name">{room.name}</span>
              <span className="room-count" title={`${room.messageCount} messages`}>
                {room.messageCount}
              </span>
            </button>
          </li>
        ))}
        {rooms.length === 0 && <li className="rail-empty">No rooms yet.</li>}
      </ul>
    </nav>
  );
}

export default memo(RoomRail);
