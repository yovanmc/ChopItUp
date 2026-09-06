import { useCallback, useEffect, useState } from 'react';
import { describeError, getTrail } from './api';
import { exactTime } from './time';
import type { Room, Trail } from './types';

interface Props {
  room: Room;
  onClose: () => void;
}

/** The last 20 commits the hub made in this room's directory (D11), newest first. Read on open and on
 *  Refresh; nothing here writes. */
export default function TrailDialog({ room, onClose }: Props) {
  const [trail, setTrail] = useState<Trail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(
    async (signal?: AbortSignal) => {
      setBusy(true);
      try {
        const loaded = await getTrail(room.id, signal);
        if (signal?.aborted) return;
        setTrail(loaded);
        setError(loaded.error);
      } catch (failure) {
        if (!signal?.aborted) setError(describeError(failure));
      } finally {
        if (!signal?.aborted) setBusy(false);
      }
    },
    [room.id],
  );

  useEffect(() => {
    const abort = new AbortController();
    void load(abort.signal);
    return () => abort.abort();
  }, [load]);

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="overlay" role="presentation" onMouseDown={onClose}>
      <div
        className="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="trail-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <header className="dialog-head">
          <h2 id="trail-title">Trail of {room.name}</h2>
          <button type="button" className="quiet close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </header>

        <p className="dialog-note">
          {trail?.directory ? (
            <>
              Commits in <code className="chip-tight">{trail.directory}</code>, newest first. The hub commits your edits
              before each spawn and the model's turn after it; nothing is ever pushed.
            </>
          ) : (
            'This room has no directory, so it has no trail.'
          )}
        </p>

        {error && <p className="dialog-error">{error}</p>}

        {trail && !error && trail.commits.length === 0 && (
          <p className="dialog-note quiet-note">
            No commits yet: the first spawn in this room, or your first edit before one, creates the first commit.
          </p>
        )}

        {trail && trail.commits.length > 0 && (
          <ol className="trail-list" aria-label="Commits">
            {trail.commits.map((commit) => (
              <li key={commit.hash}>
                <code>{commit.hash}</code>
                {commit.subject}
                <span className="trail-meta">
                  {commit.author} · {exactTime(commit.at)}
                </span>
              </li>
            ))}
          </ol>
        )}

        <footer className="dialog-actions">
          <button type="button" className="quiet" onClick={() => void load()} disabled={busy}>
            {busy ? 'Loading…' : 'Refresh'}
          </button>
          <button type="button" className="quiet" onClick={onClose}>
            Close
          </button>
        </footer>
      </div>
    </div>
  );
}
