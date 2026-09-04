import { useEffect, useRef, useState } from 'react';
import { describeError, importTranscript } from './api';
import { clockTime } from './time';
import type { Message } from './types';

const PREVIEW_LIMIT = 12;
const FIRST_LINE_CHARS = 96;

interface Props {
  roomId: string;
  roomName: string;
  onClose: () => void;
  onImported: (messages: Message[]) => void;
}

/** D1, and the UI must not imply otherwise: the hub authors every imported line as `owner` and
 *  leaves the original speaker inside the body. There is deliberately no "import as Claude" control
 *  and no author chip on the preview — the participation prompt tells the models to trust the hub's
 *  stamp over any name in the text, and a fake chip here would make that sentence false. */
export default function ImportDialog({ roomId, roomName, onClose, onImported }: Props) {
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [landed, setLanded] = useState<Message[] | null>(null);
  const box = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    box.current?.focus();
    function onKey(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  async function run() {
    if (busy || text.trim().length === 0) return;
    setBusy(true);
    setError(null);
    try {
      const messages = await importTranscript(roomId, text);
      setLanded(messages);
      setText('');
      onImported(messages);
    } catch (failure) {
      setError(describeError(failure));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="overlay" role="presentation" onMouseDown={onClose}>
      <div
        className="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="import-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <header className="dialog-head">
          <h2 id="import-title">Import a transcript into {roomName}</h2>
          <button type="button" className="quiet close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </header>

        <p className="dialog-note">
          Paste a conversation. A line that opens with a name and a colon — <code>Claude:</code>,{' '}
          <code>Codex:</code> — starts a new message. Everything you import is stored as{' '}
          <strong>your</strong> message; the original speaker stays inside the text, because the author on a
          message is stamped by the hub and never re-attributed.
        </p>

        <textarea
          ref={box}
          className="paste"
          value={text}
          placeholder={'Claude: here is what I found…\n\nCodex: I would do it differently.'}
          onChange={(event) => setText(event.target.value)}
          disabled={busy}
          aria-label="Transcript to import"
        />

        {error && <p className="dialog-error">{error}</p>}

        {landed && (
          <section className="landed" aria-live="polite">
            <h3>
              {landed.length === 1 ? '1 message imported' : `${landed.length} messages imported`} — stored as you
            </h3>
            <ol>
              {landed.slice(0, PREVIEW_LIMIT).map((message) => (
                <li key={message.id}>
                  <span className="landed-id">#{message.id}</span>
                  <span className="landed-line">{firstLine(message.body)}</span>
                  <span className="landed-time">{clockTime(message.createdAt)}</span>
                </li>
              ))}
            </ol>
            {landed.length > PREVIEW_LIMIT && (
              <p className="dialog-note">…and {landed.length - PREVIEW_LIMIT} more, all in the thread behind this dialog.</p>
            )}
          </section>
        )}

        <footer className="dialog-actions">
          <button type="button" className="quiet" onClick={onClose}>
            {landed ? 'Done' : 'Cancel'}
          </button>
          <button type="button" className="send" onClick={() => void run()} disabled={busy || text.trim().length === 0}>
            {busy ? 'Importing…' : 'Import'}
          </button>
        </footer>
      </div>
    </div>
  );
}

function firstLine(body: string): string {
  const line = body.split('\n', 1)[0] ?? '';
  return line.length > FIRST_LINE_CHARS ? `${line.slice(0, FIRST_LINE_CHARS)}…` : line;
}
