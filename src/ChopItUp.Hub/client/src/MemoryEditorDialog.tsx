import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, describeError, listMemoryFiles, previewMemoryFile, readMemoryFile, saveMemoryFile } from './api';
import { readOwnerToken } from './ownerToken';
import type { MemoryEditResult, MemoryFile, MemoryFileText, MemoryPreview, Room } from './types';

/** The slug of the core file; every other row is a topic under `topics/`. */
const CORE = 'core';

/** Long enough that a held key is one request rather than thirty, short enough that the number under
 *  the textarea is the number for the text on screen by the time it is read. */
const PREVIEW_DEBOUNCE = 300;

/** AC6. The hub answers 409 while any spawn is in flight, so the dialog says so before the click
 *  rather than after it. Its own sentence, not `MemoryPanel`'s: what is refused here is a save. */
export const LOCKED_HINT = 'A spawn is running; save when the exchange has finished.';

/** The 401 sentence, taken from `describeError` rather than copied, so this dialog cannot drift from
 *  the wording the rest of the UI shows for a browser with no owner token. */
export const NEEDS_TOKEN = describeError(new ApiError(401, 'unauthorized'));

/** What a refusal is prefixed with, because 401 and 403 are answered with a sentence about the token
 *  that says nothing about which write it refused (`RolesDialog.saveStanding`, same reason). */
export const DID_NOT_HAPPEN = 'That edit was not saved.';

/** R6: the trail's own body rule, said before the 400 rather than after it. */
export const SAVE_HINT =
  'Saving files an approved rewrite in this room: the previous text is kept beside the file, the write is one commit, and the room gets a note. The file needs at least one ## entry heading.';

/** Thousands separators without a locale: `toLocaleString` would make the count on screen depend on
 *  the browser's language, and these are the hub's numbers. */
function grouped(n: number): string {
  return n.toString().replace(/\B(?=(\d{3})+(?!\d))/g, ',');
}

/** One option of the picker: the path the hub uses for the file, its live size and the cap a save has
 *  to stay under. */
export function fileLabel(file: MemoryFile): string {
  const name = file.slug === CORE ? `${file.path} (core)` : file.path;
  return `${name} · ${grouped(file.chars)} of ${grouped(file.cap)}`;
}

/** Plain inequality: the hub LF-normalises every read (R12) and a browser textarea holds LF, so the
 *  text loaded and the text typed are comparable as they are. */
export function isDirty(loaded: string, text: string): boolean {
  return loaded !== text;
}

/** AC7. The cap is enforced on the composed file — the marker line and every carried approval record
 *  included — so the textarea's length is not the number that decides a save. It is still what is
 *  shown until the hub has answered once, labelled as the typed one so it cannot be mistaken for the
 *  count Save is gated on. */
export function countLine(preview: MemoryPreview | null, rawLength: number): string {
  if (preview === null) return `${grouped(rawLength)} characters (typed; the hub adds its bookkeeping lines)`;
  const line = `${grouped(preview.chars)} of ${grouped(preview.cap)} characters as the hub would write it`;
  return preview.over ? `${line} — over the cap by ${grouped(preview.chars - preview.cap)}` : line;
}

/** AC7. Which count the Save gate obeys. The hub's `over` is the authority, because it is taken on the
 *  composed file the cap is enforced on. Until the first preview answers, the typed length against the
 *  cap is the closest thing the dialog has, and it is enough for the obvious case: the composed file
 *  puts a marker line and every surviving entry's approval record on top of the typed text, so a text
 *  already over the cap on its own is over it composed too. Gating on nothing until the hub answers
 *  would offer a Save that could only come back a 409. */
export function isOver(preview: MemoryPreview | null, textLength: number, cap: number): boolean {
  return preview === null ? textLength > cap : preview.over;
}

/** Whether a dismissal that carries no intent — a mousedown landing on the overlay behind the dialog,
 *  or an Escape keypress — should be ignored. While the box is dirty it is, because the typed text
 *  exists nowhere else until a save writes it and there is nothing to undo a closed dialog with. The
 *  Close button is the explicit exit and is never gated on this. */
export function shouldIgnoreDismiss(dirty: boolean): boolean {
  return dirty;
}

/** Whether the dialog may ask the hub for a count. Every `POST` under `/api` needs an owner bearer
 *  and each accepted bearer costs a peer-process check, so a client with no token must not poll a
 *  route that can only answer 401; and there is nothing to count before a file has been read. */
export function canPreview(hasToken: boolean, loaded: MemoryFileText | null): boolean {
  return hasToken && loaded !== null;
}

/** Rollback is by file and by git, so the line shown after a save has to name the row,
 *  the commit and the copy of the pre-edit text. A hub without git still writes the file and the row,
 *  and saying so is better than showing an empty pair of brackets. */
export function savedLine(result: MemoryEditResult): string {
  const commit =
    result.proposal.commitHash === null
      ? 'not committed: git unavailable; see the hub log'
      : `commit ${result.proposal.commitHash}`;
  return `Saved as proposal #${result.proposal.id} (${commit}); the previous text is at ${result.backup}.`;
}

/** What one save reports back, one hook per state change it makes — the `SaveHooks` shape of
 *  `RolesDialog`, and for the same reason: it lifts the effectful half out of the component so it can
 *  be tested in a client with no DOM. */
export interface EditHooks {
  begin: () => void;
  done: (result: MemoryEditResult) => void;
  fail: (message: string) => void;
  end: () => void;
}

/** One save. `baseHash` is the hash the read returned (R3), so a file that moved on in between is a
 *  409 that Reload recovers from rather than a silent clobber. Every refusal is shown as
 *  `describeError` reads it: the credential sentence for 401/403, and the hub's own sentence verbatim
 *  for everything else — those are written to be read by a human, and this dialog has nothing better
 *  to say. */
export async function saveEdit(
  slug: string,
  roomId: string,
  text: string,
  baseHash: string,
  hooks: EditHooks,
): Promise<void> {
  hooks.begin();
  try {
    hooks.done(await saveMemoryFile(slug, roomId, text, baseHash));
  } catch (failure) {
    hooks.fail(`${DID_NOT_HAPPEN} ${describeError(failure)}`);
  } finally {
    hooks.end();
  }
}

export interface MemoryEditorProps {
  roomName: string;
  files: MemoryFile[];
  slug: string;
  /** The file as the hub last handed it over: what dirtiness is measured against, and the hash a
   *  save echoes. Null while a read is in flight. */
  loaded: MemoryFileText | null;
  text: string;
  /** The hub's count for the text on screen, or null until the first preview answers. */
  preview: MemoryPreview | null;
  hasToken: boolean;
  locked: boolean;
  saving: boolean;
  error: string | null;
  /** The line a successful save left, naming the row, the commit and the backup. */
  status: string | null;
  onPick: (slug: string) => void;
  onEdit: (text: string) => void;
  onReload: () => void;
  onSave: () => void;
  onClose: () => void;
}

/** The editor itself, taking the loaded file as a prop so it renders without a fetch — which is what
 *  lets it be tested at all in a client with no jsdom, exactly as `RolesEditor` does.
 *
 *  Save is gated on the four states the hub would refuse (nothing changed, a spawn in flight, over the
 *  cap, no owner token) and on one it cannot answer yet (a save already in flight). Reload is gated on
 *  none of the refusals: it is the way back from a stale file, and a 409 is the state it exists for
 *  (M25). A save in flight is the one state that shuts Reload, and Close with it. */
export function MemoryEditor({
  roomName,
  files,
  slug,
  loaded,
  text,
  preview,
  hasToken,
  locked,
  saving,
  error,
  status,
  onPick,
  onEdit,
  onReload,
  onSave,
  onClose,
}: MemoryEditorProps) {
  const box = useRef<HTMLTextAreaElement>(null);
  const dirty = loaded !== null && isDirty(loaded.text, text);
  const over = loaded !== null && isOver(preview, text.length, loaded.cap);

  useEffect(() => {
    box.current?.focus();
  }, []);

  return (
    <>
      <header className="dialog-head">
        <h2 id="memory-editor-title">Edit memory from {roomName}</h2>
        {/* Shut mid-save, like the footer's pair: the write is in flight and its status line is what
            says where the text and the backup landed. */}
        <button type="button" className="quiet close" onClick={onClose} aria-label="Close" disabled={saving}>
          ✕
        </button>
      </header>

      <p className="dialog-note">
        The shared memory as it is on disk, whole and uncut. An edit saved here goes through the same
        trail a model&apos;s consolidation does, and lands in this room.
      </p>

      <select
        className="field memory-editor-pick"
        aria-label="Memory file"
        value={slug}
        onChange={(event) => onPick(event.target.value)}
        // Switching with unsaved text in the box would drop it silently; Save or Reload first.
        disabled={dirty || saving}
      >
        {files.map((file) => (
          <option key={file.slug} value={file.slug}>
            {fileLabel(file)}
          </option>
        ))}
      </select>

      <textarea
        ref={box}
        className="field memory-editor-text"
        aria-label="Memory text"
        rows={18}
        value={text}
        onChange={(event) => onEdit(event.target.value)}
        disabled={saving || loaded === null}
        spellCheck={false}
      />

      <p className={over ? 'memory-editor-count memory-editor-over' : 'memory-editor-count'} aria-live="polite">
        {countLine(preview, text.length)}
      </p>

      <p className="memory-editor-hint">{locked ? LOCKED_HINT : hasToken ? SAVE_HINT : NEEDS_TOKEN}</p>

      {error && (
        <p className="dialog-error" role="alert">
          {error}
        </p>
      )}

      {status && (
        <p className="memory-editor-status" aria-live="polite">
          {status}
        </p>
      )}

      <footer className="dialog-actions">
        <button type="button" className="quiet" onClick={onClose} disabled={saving}>
          Close
        </button>
        {/* Open in every other state, a refusal included: it is the way back from a stale file (M25).
            Shut only while the save it would race is in flight. */}
        <button type="button" className="quiet" onClick={onReload} disabled={saving}>
          Reload
        </button>
        <button
          type="button"
          className="send"
          onClick={onSave}
          disabled={!dirty || locked || over || saving || !hasToken}
        >
          {saving ? 'Saving…' : 'Save'}
        </button>
      </footer>
    </>
  );
}

interface Props {
  room: Room;
  /** True while the room's exchange has spawns in flight: the hub refuses a save then (AC3), so the
   *  hint says so instead of inviting a click that can only produce a banner. */
  locked: boolean;
  onClose: () => void;
}

/** Row 40 (AC1, AC6, AC7): the surface for the memory files themselves — the one door into
 *  them from a phone, and a gated one everywhere else (the text editor on disk stays ungated).
 *
 *  Shown to everyone, exactly like every other panel here: there is no client-side "am I allowed"
 *  gate, because the hub is the thing that knows. What IS asked locally is whether this browser holds
 *  a token at all, because without one every count request and every save could only be a 401. */
export default function MemoryEditorDialog({ room, locked, onClose }: Props) {
  const [files, setFiles] = useState<MemoryFile[]>([]);
  const [slug, setSlug] = useState(CORE);
  const [loaded, setLoaded] = useState<MemoryFileText | null>(null);
  const [text, setText] = useState('');
  const [preview, setPreview] = useState<MemoryPreview | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  /** Bumped by Reload, which is a re-read of the file the picker is on. */
  const [reloads, setReloads] = useState(0);
  /** Which preview request is the latest: an earlier reply that lands after it must not overwrite the
   *  number for the text on screen. */
  const latest = useRef(0);
  const hasToken = readOwnerToken() !== null;
  const dirty = loaded !== null && isDirty(loaded.text, text);

  useEffect(() => {
    const abort = new AbortController();
    listMemoryFiles(abort.signal)
      .then((rows) => {
        if (!abort.signal.aborted) setFiles(rows);
      })
      .catch((failure) => {
        if (!abort.signal.aborted) setError(describeError(failure));
      });
    return () => abort.abort();
  }, [reloads]);

  useEffect(() => {
    const abort = new AbortController();
    setLoaded(null);
    setPreview(null);
    setStatus(null);
    setError(null);
    readMemoryFile(slug, abort.signal)
      .then((file) => {
        if (abort.signal.aborted) return;
        setLoaded(file);
        setText(file.text);
      })
      .catch((failure) => {
        if (!abort.signal.aborted) setError(describeError(failure));
      });
    return () => abort.abort();
  }, [slug, reloads]);

  useEffect(() => {
    if (!canPreview(hasToken, loaded)) return;
    const abort = new AbortController();
    const mine = ++latest.current;
    const timer = setTimeout(() => {
      previewMemoryFile(slug, room.id, text, abort.signal)
        .then((answer) => {
          if (!abort.signal.aborted && mine === latest.current) setPreview(answer);
        })
        .catch(() => {
          // A count is not a write: a failed or superseded call leaves the last good number, and the
          // cap is enforced by the hub on the save in any case.
        });
    }, PREVIEW_DEBOUNCE);
    return () => {
      clearTimeout(timer);
      abort.abort();
    };
  }, [hasToken, loaded, slug, text, room.id]);

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key === 'Escape' && !shouldIgnoreDismiss(dirty)) onClose();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose, dirty]);

  const save = useCallback(() => {
    if (loaded === null) return;
    void saveEdit(slug, room.id, text, loaded.hash, {
      begin: () => {
        setSaving(true);
        setError(null);
        setStatus(null);
      },
      // The response is the file as the hub WROTE it, which differs from the submitted text whenever a
      // surviving entry carried its approval record forward (AC2). Taking it here is what makes the
      // box clean again and gives the next save its base hash.
      done: (result) => {
        setLoaded(result);
        setText(result.text);
        setPreview({ slug: result.slug, chars: result.chars, cap: result.cap, over: result.chars > result.cap });
        setStatus(savedLine(result));
        setFiles((rows) => rows.map((row) => (row.slug === result.slug ? { ...row, chars: result.chars } : row)));
      },
      fail: setError,
      end: () => setSaving(false),
    });
  }, [loaded, room.id, slug, text]);

  return (
    <div
      className="overlay"
      role="presentation"
      onMouseDown={() => {
        if (!shouldIgnoreDismiss(dirty)) onClose();
      }}
    >
      <div
        className="dialog memory-editor-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="memory-editor-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        {loaded === null && files.length === 0 ? (
          <>
            <header className="dialog-head">
              <h2 id="memory-editor-title">Edit memory from {room.name}</h2>
              <button type="button" className="quiet close" onClick={onClose} aria-label="Close">
                ✕
              </button>
            </header>
            {error ? (
              <p className="dialog-error" role="alert">
                {error}
              </p>
            ) : (
              <p className="dialog-note">Reading the memory files…</p>
            )}
            <footer className="dialog-actions">
              <button type="button" className="quiet" onClick={onClose}>
                Close
              </button>
              <button type="button" className="quiet" onClick={() => setReloads((n) => n + 1)}>
                Reload
              </button>
            </footer>
          </>
        ) : (
          <MemoryEditor
            roomName={room.name}
            files={files}
            slug={slug}
            loaded={loaded}
            text={text}
            preview={preview}
            hasToken={hasToken}
            locked={locked}
            saving={saving}
            error={error}
            status={status}
            onPick={setSlug}
            onEdit={setText}
            onReload={() => setReloads((n) => n + 1)}
            onSave={save}
            onClose={onClose}
          />
        )}
      </div>
    </div>
  );
}
