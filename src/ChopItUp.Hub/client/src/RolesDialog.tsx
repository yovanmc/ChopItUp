import { useCallback, useEffect, useRef, useState } from 'react';
import { describeError, getRoomRoles, setGlobalRole, setPersona, setRoomRole } from './api';
import type { RoleRow, Room, RoomRoles } from './types';

/** Where the text the hub would actually render into a participant's spawn prompt here comes from.
 *  `suppressed` and `none` are both "nothing is in force", and they are NOT the same state: the
 *  first is a stored decision about this room, the second is the absence of any role at all. */
export type RoleSource = 'room' | 'global' | 'suppressed' | 'none';

export const SOURCE_LABEL: Record<RoleSource, string> = {
  room: 'the override in this room',
  global: 'the global role',
  suppressed: 'no role in this room',
  none: 'no role anywhere yet',
};

/** Which of the four states a row is in. This decides the SENTENCE only — the text shown as in force
 *  is always the server's `effectiveRole`, never re-derived here, because the hub's COALESCE is the
 *  thing AC3 is about and a second copy of it in the client is a second thing to get wrong. */
export function sourceOf(row: RoleRow): RoleSource {
  if (row.roomRole !== null) return row.roomRole.length === 0 ? 'suppressed' : 'room';
  return row.role !== null && row.role.length > 0 ? 'global' : 'none';
}

/** The two operations that are NOT "save what I typed", and are not each other either (D-b).
 *
 *  Clearing an override deletes the row, so the participant goes back to carrying its global role
 *  here. Suppressing stores the empty string, so it keeps its global role in every other room and has
 *  no role at all in this one. A dialog with only the first makes that fourth state unreachable, and
 *  `role` below is the whole of the difference on the wire: omitted versus `""` (see
 *  `api.setRoomRole`). The editor builds one control per entry, which is what keeps the two controls
 *  and the two payloads from drifting apart. */
export interface OverrideOp {
  key: 'clear' | 'suppress';
  label: string;
  hint: string;
  /** What `api.setRoomRole` is handed: `null` omits the key, `''` stores the sentinel. */
  role: string | null;
}

export const OVERRIDE_OPS: readonly OverrideOp[] = [
  {
    key: 'clear',
    label: 'Clear override',
    hint: 'Drop this room override so the global role applies here again.',
    role: null,
  },
  {
    key: 'suppress',
    label: 'No role in this room',
    hint: 'Keep the global role everywhere else and give this participant no role at all in this room.',
    role: '',
  },
];

/** What one save reports back, one hook per state change it makes — the shape `App`'s
 *  `stopExchangeAt` uses, and for the same reason: it lifts the effectful half out of the component
 *  so it can be tested without a DOM. */
export interface SaveHooks {
  begin: () => void;
  done: (roles: RoomRoles) => void;
  fail: (message: string) => void;
  end: () => void;
}

/** One save. `didNotHappen` names what the owner just failed to do, because 401 and 403 are answered
 *  with the same sentence the rest of the UI uses ("the hub needs the owner token…") and that sentence
 *  says nothing about which box was being saved; every other refusal is the hub's own sentence, shown
 *  verbatim — those are written for the owner and this dialog has nothing better to say. */
export async function saveStanding(
  write: () => Promise<RoomRoles>,
  didNotHappen: string,
  hooks: SaveHooks,
): Promise<void> {
  hooks.begin();
  try {
    hooks.done(await write());
  } catch (failure) {
    hooks.fail(`${didNotHappen} ${describeError(failure)}`);
  } finally {
    hooks.end();
  }
}

/** The key of one editable box, used both for its draft text and for which control is mid-save. */
export function draftKey(participantId: string, scope: 'global' | 'room'): string {
  return `${participantId}|${scope}`;
}

export const PERSONA_KEY = 'persona';

export interface RolesEditorProps {
  roomName: string;
  roles: RoomRoles;
  /** The key of the control whose save is in flight, or null when nothing is saving. */
  busy: string | null;
  error: string | null;
  onSavePersona: (persona: string) => void;
  onSaveGlobal: (row: RoleRow, role: string) => void;
  /** `role` is `null` to clear the override and a string (`''` included) to store one. `controlKey`
   *  is which of the row's three override controls asked, since only the editor knows that and the
   *  answer is only used to say "Saving…" on the one that was pressed. */
  onSaveOverride: (row: RoleRow, role: string | null, controlKey: string) => void;
  onClose: () => void;
}

function seedDrafts(roles: RoomRoles): Record<string, string> {
  const drafts: Record<string, string> = {};
  for (const row of roles.participants) {
    drafts[draftKey(row.id, 'global')] = row.role ?? '';
    drafts[draftKey(row.id, 'room')] = row.roomRole ?? '';
  }
  return drafts;
}

/** The editor itself, taking the loaded state as a prop so it renders without a fetch — which is what
 *  lets it be tested at all in a client with no jsdom, and mirrors `MemoryPanel`/`SkillPanel`.
 *
 *  Every Save is enabled on an empty box on purpose: clearing a role IS saving an empty box, so a
 *  `disabled={text.length === 0}` here would make three of the four states one-way (LESSON M25). */
export function RolesEditor({
  roomName,
  roles,
  busy,
  error,
  onSavePersona,
  onSaveGlobal,
  onSaveOverride,
  onClose,
}: RolesEditorProps) {
  const [persona, setPersonaDraft] = useState(roles.persona ?? '');
  const [drafts, setDrafts] = useState<Record<string, string>>(() => seedDrafts(roles));
  const first = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    first.current?.focus();
  }, []);

  // Every successful write answers with the room's whole state, and this is where the dialog stops
  // showing what was typed and starts showing what the hub stored — including the hub's own trimming,
  // and including a box the owner had edited and not saved. Deliberate: one dialog, one truth.
  useEffect(() => {
    setPersonaDraft(roles.persona ?? '');
    setDrafts(seedDrafts(roles));
  }, [roles]);

  const saving = busy !== null;
  const draft = (key: string) => drafts[key] ?? '';
  const edit = (key: string, value: string) => setDrafts((current) => ({ ...current, [key]: value }));

  return (
    <>
      <header className="dialog-head">
        <h2 id="roles-title">Roles in {roomName}</h2>
        <button type="button" className="quiet close" onClick={onClose} aria-label="Close">
          ✕
        </button>
      </header>

      <p className="dialog-note">
        The hub renders this text into every spawn it starts in this room. Your messages in the room, and
        the skill in force, outrank it. Saving an empty box clears that text.
      </p>

      <label className="field-label" htmlFor="roles-persona">
        Room persona — given to everyone spawned here
      </label>
      <textarea
        id="roles-persona"
        ref={first}
        className="field roles-text"
        rows={3}
        value={persona}
        placeholder="What this room is, for everyone who works in it."
        onChange={(event) => setPersonaDraft(event.target.value)}
        disabled={saving}
      />
      <div className="roles-actions">
        <button type="button" className="send" onClick={() => onSavePersona(persona)} disabled={saving}>
          {busy === PERSONA_KEY ? 'Saving…' : 'Save persona'}
        </button>
      </div>

      <ul className="roles-list" aria-label="Roles">
        {roles.participants.map((row) => {
          const source = sourceOf(row);
          const inForce = row.effectiveRole !== null && row.effectiveRole.length > 0 ? row.effectiveRole : null;
          const globalKey = draftKey(row.id, 'global');
          const roomKey = draftKey(row.id, 'room');
          return (
            <li key={row.id} className="roles-card">
              <div className="roles-card-head">
                <span className="roles-name">{row.displayName}</span>
                <code className="roles-id">{row.id}</code>
              </div>

              {/* What is actually in force, and where it came from. The hub computed both halves of
                  this; the dialog is not the place the owner should have to work out precedence. */}
              {inForce === null ? (
                <p className="roles-inforce roles-inforce-empty">
                  Nothing is in force here: {SOURCE_LABEL[source]}.
                </p>
              ) : (
                <p className="roles-inforce">
                  In force here, from {SOURCE_LABEL[source]}:{' '}
                  <span className="roles-inforce-text">{inForce}</span>
                </p>
              )}

              <label className="field-label" htmlFor={`${globalKey}-box`}>
                Global role — every room that does not override it
              </label>
              <textarea
                id={`${globalKey}-box`}
                className="field roles-text"
                rows={2}
                value={draft(globalKey)}
                onChange={(event) => edit(globalKey, event.target.value)}
                disabled={saving}
              />
              <div className="roles-actions">
                <button
                  type="button"
                  className="quiet"
                  onClick={() => onSaveGlobal(row, draft(globalKey))}
                  disabled={saving}
                >
                  {busy === globalKey ? 'Saving…' : 'Save global role'}
                </button>
              </div>

              <label className="field-label" htmlFor={`${roomKey}-box`}>
                This room only — replaces the global role here
              </label>
              <textarea
                id={`${roomKey}-box`}
                className="field roles-text"
                rows={2}
                value={draft(roomKey)}
                onChange={(event) => edit(roomKey, event.target.value)}
                disabled={saving}
              />
              <div className="roles-actions">
                <button
                  type="button"
                  className="quiet"
                  onClick={() => onSaveOverride(row, draft(roomKey), roomKey)}
                  disabled={saving}
                >
                  {busy === roomKey ? 'Saving…' : 'Save for this room'}
                </button>
                {OVERRIDE_OPS.map((op) => {
                  const key = `${row.id}|${op.key}`;
                  return (
                    <button
                      key={op.key}
                      type="button"
                      className="quiet"
                      title={op.hint}
                      onClick={() => onSaveOverride(row, op.role, key)}
                      disabled={saving}
                    >
                      {busy === key ? 'Saving…' : op.label}
                    </button>
                  );
                })}
              </div>
            </li>
          );
        })}
      </ul>

      {error && (
        <p className="dialog-error" role="alert">
          {error}
        </p>
      )}

      <footer className="dialog-actions">
        <button type="button" className="quiet" onClick={onClose}>
          Close
        </button>
      </footer>
    </>
  );
}

interface Props {
  room: Room;
  onClose: () => void;
}

/** Row 14 (AC10): the owner's surface for the three pieces of standing prompt text — this room's
 *  persona, each spawnable participant's global role, and this room's override of it.
 *
 *  Shown to everyone, exactly like every other panel here: there is no client-side "am I the owner"
 *  gate, because the hub is the thing that knows, and a write without the owner's credential comes
 *  back as the refusal the rest of the UI shows for one. Participants the hub can never spawn are not
 *  listed — the hub leaves them out, since a role stored on one could never render. */
export default function RolesDialog({ room, onClose }: Props) {
  const [roles, setRoles] = useState<RoomRoles | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const abort = new AbortController();
    getRoomRoles(room.id, abort.signal)
      .then((loaded) => {
        if (!abort.signal.aborted) setRoles(loaded);
      })
      .catch((failure) => {
        if (!abort.signal.aborted) setError(describeError(failure));
      });
    return () => abort.abort();
  }, [room.id]);

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const run = useCallback((controlKey: string, write: () => Promise<RoomRoles>, didNotHappen: string) => {
    void saveStanding(write, didNotHappen, {
      begin: () => {
        setBusy(controlKey);
        setError(null);
      },
      done: setRoles,
      fail: setError,
      end: () => setBusy(null),
    });
  }, []);

  const savePersona = useCallback(
    (persona: string) => run(PERSONA_KEY, () => setPersona(room.id, persona), 'The persona was not saved.'),
    [room.id, run],
  );

  /** The one write whose answer is not the room's shape: `POST /api/participants/{id}/role` knows
   *  nothing about a room, so it cannot say what is now in force HERE. Re-reading the room is what
   *  keeps the effective-role line the server's answer rather than a precedence recomputed locally. */
  const saveGlobal = useCallback(
    (row: RoleRow, role: string) =>
      run(
        draftKey(row.id, 'global'),
        async () => {
          await setGlobalRole(row.id, role);
          return getRoomRoles(room.id);
        },
        `The global role for ${row.displayName} was not saved.`,
      ),
    [room.id, run],
  );

  const saveOverride = useCallback(
    (row: RoleRow, role: string | null, controlKey: string) =>
      run(
        controlKey,
        () => setRoomRole(room.id, row.id, role),
        `The role for ${row.displayName} in this room was not saved.`,
      ),
    [room.id, run],
  );

  return (
    <div className="overlay" role="presentation" onMouseDown={onClose}>
      <div
        className="dialog roles-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="roles-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        {roles === null ? (
          <>
            <header className="dialog-head">
              <h2 id="roles-title">Roles in {room.name}</h2>
              <button type="button" className="quiet close" onClick={onClose} aria-label="Close">
                ✕
              </button>
            </header>
            {error ? (
              <p className="dialog-error" role="alert">
                {error}
              </p>
            ) : (
              <p className="dialog-note">Reading this room…</p>
            )}
            <footer className="dialog-actions">
              <button type="button" className="quiet" onClick={onClose}>
                Close
              </button>
            </footer>
          </>
        ) : (
          <RolesEditor
            roomName={room.name}
            roles={roles}
            busy={busy}
            error={error}
            onSavePersona={savePersona}
            onSaveGlobal={saveGlobal}
            onSaveOverride={saveOverride}
            onClose={onClose}
          />
        )}
      </div>
    </div>
  );
}
