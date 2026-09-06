import { useEffect, useRef, useState } from 'react';
import { bindDirectory, createRoom, describeError } from './api';
import type { Room } from './types';

interface Props {
  mode: 'create' | 'bind';
  /** The room being bound (bind mode only). */
  room?: Room;
  onClose: () => void;
  onDone: (room: Room) => void;
}

const DIRECTORY_HINT =
  'Leave blank and the hub creates a folder under its rooms root. Or type an absolute path such as C:\\Projects\\thing: it becomes a git repository, or is adopted if it already is one. Drive roots, your profile folder itself, C:\\Self Apps, the hub\'s own folders, credential folders and Windows folders are refused.';

/** One dialog for "new room" and for "bind a directory to a room made before M9": the same directory
 *  field, hint and refusal line. The hub's refusal sentences are written for the owner and shown
 *  verbatim; nothing is validated here except emptiness. */
export default function NewRoomDialog({ mode, room, onClose, onDone }: Props) {
  const [name, setName] = useState('');
  const [directory, setDirectory] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const first = useRef<HTMLInputElement>(null);

  useEffect(() => {
    first.current?.focus();
    function onKey(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const canSubmit = !busy && (mode === 'bind' || name.trim().length > 0);

  async function submit() {
    if (!canSubmit) return;
    setBusy(true);
    setError(null);
    try {
      const done =
        mode === 'create'
          ? await createRoom(name.trim(), directory.trim())
          : await bindDirectory(room!.id, directory.trim());
      onDone(done);
    } catch (failure) {
      setError(describeError(failure));
    } finally {
      setBusy(false);
    }
  }

  const title = mode === 'create' ? 'New room' : `Bind a directory to ${room?.name ?? 'this room'}`;

  return (
    <div className="overlay" role="presentation" onMouseDown={onClose}>
      <div
        className="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="new-room-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <header className="dialog-head">
          <h2 id="new-room-title">{title}</h2>
          <button type="button" className="quiet close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </header>

        {mode === 'create' && (
          <>
            <label className="field-label" htmlFor="new-room-name">
              Title
            </label>
            <input
              id="new-room-name"
              ref={first}
              className="field"
              type="text"
              value={name}
              maxLength={80}
              placeholder="What this room is for"
              onChange={(event) => setName(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') void submit();
              }}
              disabled={busy}
            />
          </>
        )}

        <label className="field-label" htmlFor="new-room-directory">
          Directory
        </label>
        <input
          id="new-room-directory"
          ref={mode === 'bind' ? first : undefined}
          className="field"
          type="text"
          value={directory}
          placeholder={'C:\\Projects\\thing (blank = hub-created)'}
          onChange={(event) => setDirectory(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') void submit();
          }}
          disabled={busy}
          spellCheck={false}
        />
        <p className="dialog-note quiet-note">{DIRECTORY_HINT}</p>

        {error && (
          <p className="dialog-error" role="alert">
            {error}
          </p>
        )}

        <footer className="dialog-actions">
          <button type="button" className="quiet" onClick={onClose}>
            Cancel
          </button>
          <button type="button" className="send" onClick={() => void submit()} disabled={!canSubmit}>
            {mode === 'create' ? (busy ? 'Creating…' : 'Create room') : busy ? 'Binding…' : 'Bind directory'}
          </button>
        </footer>
      </div>
    </div>
  );
}
