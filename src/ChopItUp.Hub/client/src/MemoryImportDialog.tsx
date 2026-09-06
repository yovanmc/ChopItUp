import { useEffect, useRef, useState } from 'react';
import { describeError, discardImport, importMemory } from './api';
import type { MemoryImportResult, MemorySource } from './types';

interface Props {
  roomId: string;
  roomName: string;
  onClose: () => void;
  onImported: (result: MemoryImportResult) => void;
}

const HINT: Record<MemorySource, string> = {
  claude:
    'Claude Code keeps one markdown file per memory under ~\\.claude\\projects\\<project>\\memory\\. Each file becomes a proposal; MEMORY.md is the index and is skipped.',
  codex: 'Codex keeps its memory under ~\\.codex\\memories\\. Each heading becomes a proposal; MEMORY.md is skipped.',
};

/** Seeds the shared memory from a vendor's own store (D15). Nothing is written to memory here: every
 *  file or section becomes a PENDING proposal, authored as the vendor's app row, for the owner to
 *  approve in the panel. The path is typed by the owner; the hub only reads top-level markdown. */
export default function MemoryImportDialog({ roomId, roomName, onClose, onImported }: Props) {
  const [source, setSource] = useState<MemorySource>('claude');
  const [path, setPath] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<MemoryImportResult | null>(null);
  const [discarded, setDiscarded] = useState<number | null>(null);
  const box = useRef<HTMLInputElement>(null);

  useEffect(() => {
    box.current?.focus();
    function onKey(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  async function run() {
    if (busy || path.trim().length === 0) return;
    setBusy(true);
    setError(null);
    setDiscarded(null);
    try {
      const landed = await importMemory(source, path.trim(), roomId);
      setResult(landed);
      onImported(landed);
    } catch (failure) {
      setError(describeError(failure));
    } finally {
      setBusy(false);
    }
  }

  /** The wrong folder is one click to undo: every pending proposal this import made goes. */
  async function undo() {
    if (busy || !result) return;
    setBusy(true);
    setError(null);
    try {
      const count = await discardImport(source, path.trim());
      setDiscarded(count);
      onImported({ imported: 0, skipped: 0, proposals: [] });
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
        aria-labelledby="memory-import-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <header className="dialog-head">
          <h2 id="memory-import-title">Import memory into {roomName}</h2>
          <button type="button" className="quiet close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </header>

        <p className="dialog-note">
          Every file or section becomes a <strong>proposal</strong> in this room. Nothing reaches the shared memory
          until you approve it in the memory panel. Importing the same folder twice adds nothing new.
        </p>

        <fieldset className="field-group" disabled={busy}>
          <legend className="field-label">Source</legend>
          <label className="choice">
            <input type="radio" name="memory-source" checked={source === 'claude'} onChange={() => setSource('claude')} />
            Claude Code memory
          </label>
          <label className="choice">
            <input type="radio" name="memory-source" checked={source === 'codex'} onChange={() => setSource('codex')} />
            Codex memories
          </label>
        </fieldset>
        <p className="dialog-note quiet-note">{HINT[source]}</p>

        <label className="field-label" htmlFor="memory-path">
          Folder
        </label>
        <input
          id="memory-path"
          ref={box}
          className="field"
          type="text"
          value={path}
          placeholder={source === 'claude' ? 'C:\\Users\\you\\.claude\\projects\\...\\memory' : 'C:\\Users\\you\\.codex\\memories'}
          onChange={(event) => setPath(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') void run();
          }}
          disabled={busy}
          spellCheck={false}
        />

        {error && <p className="dialog-error">{error}</p>}

        {result && (
          <section className="landed" aria-live="polite">
            <h3>
              {result.imported === 1 ? '1 proposal added' : `${result.imported} proposals added`}
              {result.skipped > 0 && ` · ${result.skipped} already proposed`}
            </h3>
            {result.imported > 0 && discarded === null && (
              <p className="dialog-note">
                They are waiting in the memory panel behind this dialog. Wrong folder?{' '}
                <button type="button" className="link" onClick={() => void undo()} disabled={busy}>
                  Discard these proposals
                </button>
              </p>
            )}
            {discarded !== null && (
              <p className="dialog-note">
                {discarded === 1 ? '1 proposal discarded.' : `${discarded} proposals discarded.`}
              </p>
            )}
          </section>
        )}

        <footer className="dialog-actions">
          <button type="button" className="quiet" onClick={onClose}>
            {result ? 'Done' : 'Cancel'}
          </button>
          <button type="button" className="send" onClick={() => void run()} disabled={busy || path.trim().length === 0}>
            {busy ? 'Importing…' : 'Import'}
          </button>
        </footer>
      </div>
    </div>
  );
}
