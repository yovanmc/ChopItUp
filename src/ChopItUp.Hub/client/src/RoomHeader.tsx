import { memo } from 'react';
import { exportUrl } from './api';
import type { Room } from './types';

interface Props {
  room: Room;
  loadedCount: number;
  onImport: () => void;
}

/** Import and export live here, quiet, rather than competing with the conversation. Export is a
 *  plain same-origin download link — the hub already returns text/markdown. */
function RoomHeader({ room, loadedCount, onImport }: Props) {
  return (
    <header className="room-head">
      <div className="room-title">
        <h1>{room.name}</h1>
        <span className="room-sub">{loadedCount === 1 ? '1 message' : `${loadedCount} messages`}</span>
      </div>
      <div className="room-actions">
        <button type="button" className="quiet" onClick={onImport}>
          Import transcript
        </button>
        <a className="quiet" href={exportUrl(room.id)} download={`${room.id}.md`}>
          Export markdown
        </a>
      </div>
    </header>
  );
}

export default memo(RoomHeader);
