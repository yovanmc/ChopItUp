import { memo } from 'react';
import { exportUrl } from './api';
import type { Room } from './types';

interface Props {
  room: Room;
  loadedCount: number;
  /** An archive/unarchive/bind call is in flight. */
  busy: boolean;
  onImport: () => void;
  onImportMemory: () => void;
  onBind: () => void;
  onArchive: () => void;
  onTrail: () => void;
}

/** Import and export live here, quiet, rather than competing with the conversation. Export is a
 *  plain same-origin download link — the hub already returns text/markdown. M9 adds the room's
 *  directory (or the one-time Bind control on a room made before M9), Archive/Unarchive (never on
 *  general — the hub refuses it), and the Trail dialog. */
function RoomHeader({ room, loadedCount, busy, onImport, onImportMemory, onBind, onArchive, onTrail }: Props) {
  const archived = room.archivedAt !== null;
  return (
    <header className="room-head">
      <div className="room-title">
        <h1>
          {room.name}
          {archived && <span className="room-archived"> archived</span>}
        </h1>
        <span className="room-sub">
          {loadedCount === 1 ? '1 message' : `${loadedCount} messages`}
          {' · '}
          {room.directory !== null ? (
            <code className="room-path" title={room.directory}>
              {room.directory}
            </code>
          ) : (
            <button type="button" className="link" onClick={onBind} disabled={busy}>
              Bind directory
            </button>
          )}
        </span>
      </div>
      <div className="room-actions">
        <button
          type="button"
          className="quiet"
          onClick={onTrail}
          disabled={room.directory === null}
          title={room.directory === null ? 'This room has no directory' : 'Commits the hub made in this room'}
        >
          Trail
        </button>
        {room.id !== 'general' && (
          <button type="button" className="quiet" onClick={onArchive} disabled={busy}>
            {archived ? 'Unarchive' : 'Archive'}
          </button>
        )}
        <button type="button" className="quiet" onClick={onImport}>
          Import transcript
        </button>
        <button type="button" className="quiet" onClick={onImportMemory}>
          Import memory
        </button>
        <a className="quiet" href={exportUrl(room.id)} download={`${room.id}.md`}>
          Export markdown
        </a>
      </div>
    </header>
  );
}

export default memo(RoomHeader);
