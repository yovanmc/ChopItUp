import { renderToStaticMarkup } from 'react-dom/server';
import { afterEach, describe, expect, test, vi } from 'vitest';
import * as api from './api';
import {
  describeClasses,
  describeEffort,
  draftKey,
  OVERRIDE_OPS,
  PERSONA_KEY,
  reseedDrafts,
  RolesEditor,
  seedDrafts,
  SOURCE_LABEL,
  saveStanding,
  sourceOf,
  type SaveHooks,
} from './RolesDialog';
import type { RoleRow, RoomRoles } from './types';

/** The dialog is the hub owner's only surface for three pieces of prompt text, so what these cases bind
 *  is the part a naive build gets wrong: that all four states of a participant's role in a room are
 *  reachable, that clearing is reachable at all, and that the row says what is actually in force
 *  rather than leaving the hub owner to do the precedence by hand.
 *
 *  `renderToStaticMarkup` and a recording `fetch`, for the same reason as `App.test.tsx` and
 *  `api.test.ts`: this client has no jsdom, so rendering is the proof it renders, and the effectful
 *  half lives outside the component (`saveStanding`) where it can run without a DOM. The one thing
 *  that cannot be proved here is the press itself; the UIA gate covers that. What keeps the two
 *  halves honest is that the editor builds its override controls by mapping `OVERRIDE_OPS`, and the
 *  payload cases below drive that same array through the same `api.setRoomRole` the editor calls. */

interface Call {
  url: string;
  init: RequestInit | undefined;
}

function stubFetch(reply: () => Response): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
    calls.push({ url, init });
    return Promise.resolve(reply());
  });
  return calls;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

afterEach(() => vi.unstubAllGlobals());

/** One row per state of the four-state space. Each fixture also carries what the roster says about
 *  the row (model, classes and the effort its classes earn in a run), exactly as `RolesApi` sends it. */
const PLANNER: RoleRow = {
  id: 'opus',
  displayName: 'Opus',
  role: 'You plan.',
  roomRole: null,
  effectiveRole: 'You plan.',
  model: 'opus',
  classes: ['visible', 'judge'],
  effort: 'high',
};
const SCRIBE: RoleRow = {
  id: 'gpt-5.6-sol',
  displayName: 'GPT-5.6 Sol',
  role: 'You review.',
  roomRole: 'You keep the notes here.',
  effectiveRole: 'You keep the notes here.',
  model: 'gpt-5.6-sol',
  classes: [],
  effort: null,
};
const SILENT: RoleRow = {
  id: 'sonnet',
  displayName: 'Sonnet',
  role: 'You build.',
  roomRole: '',
  effectiveRole: '',
  model: 'sonnet',
  classes: ['plumbing'],
  effort: null,
};
const BLANK: RoleRow = {
  id: 'fable',
  displayName: 'Fable',
  role: null,
  roomRole: null,
  effectiveRole: null,
  model: 'fable',
  classes: ['judge'],
  effort: 'high',
};

const ROLES: RoomRoles = {
  roomId: 'lab',
  persona: 'This room ships the migration.',
  conductorEffort: 'high',
  participants: [PLANNER, SCRIBE, SILENT, BLANK],
};

const EMPTY: RoomRoles = { roomId: 'lab', persona: null, conductorEffort: 'high', participants: [BLANK] };

const NOOP = () => undefined;

function render(roles: RoomRoles = ROLES, error: string | null = null, busy: string | null = null): string {
  return renderToStaticMarkup(
    <RolesEditor
      roomName="Lab"
      roles={roles}
      busy={busy}
      error={error}
      onSavePersona={NOOP}
      onSaveGlobal={NOOP}
      onSaveOverride={NOOP}
      onClose={NOOP}
    />,
  );
}

describe('what the dialog shows', () => {
  test('it shows the room persona and one row per participant the hub can spawn', () => {
    const markup = render();

    expect(markup).toContain('This room ships the migration.');
    expect(markup).toContain('Opus');
    expect(markup).toContain('GPT-5.6 Sol');
    expect(markup).toContain('Sonnet');
    expect(markup).toContain('Fable');
  });

  test('both textareas of a row carry the stored text, so an edit starts from what is stored', () => {
    const markup = render();

    expect(markup).toContain('You review.');
    expect(markup).toContain('You keep the notes here.');
  });
});

/** Classes decide who a run dispatches to (ExchangePolicy) and what effort a row is
 *  spawned at (EffortPolicy), and until now the only way to see them was the roster's own SQL. The
 *  dialog shows what the hub sent, read-only: the model name the host is launched with, the class
 *  set, and the effort the classes earn. What these cases bind is honesty at the edges: a row with no
 *  classes says so, a row that earns no flag says the CLI decides, and no value is invented locally. */
describe('what the roster says about each row', () => {
  test('every row shows the model name its host is launched with', () => {
    const markup = render();

    for (const row of ROLES.participants) expect(markup).toContain(`roles-model">${row.model}<`);
  });

  test('a row with classes lists them in the roster order', () => {
    expect(describeClasses(PLANNER)).toBe('visible, judge');
    expect(render()).toContain('visible, judge');
  });

  test('a row with no classes says so instead of showing nothing', () => {
    expect(describeClasses(SCRIBE)).toBe('none');
    const markup = render({ roomId: 'lab', persona: null, conductorEffort: 'high', participants: [SCRIBE] });
    expect(markup).toContain('roles-classes-empty');
  });

  test('a judge row says what effort it earns in a run, and that outside one the CLI decides', () => {
    const text = describeEffort(PLANNER, ROLES.conductorEffort);
    expect(text).toContain('high in a run');
    expect(text).toContain('CLI default');
    expect(render()).toContain(text);
  });

  test('a row that earns no flag says the CLI default applies, and names the conductor exception', () => {
    const text = describeEffort(SILENT, ROLES.conductorEffort);
    expect(text).toContain('CLI default');
    expect(text).not.toContain('high in a run');
    expect(text).toContain('high when it conducts a run');
  });

  test('the effort values shown are the server values, never a literal of this file', () => {
    const custom = describeEffort({ ...PLANNER, effort: 'SERVER-SAID' }, 'CONDUCTOR-SAID');
    expect(custom).toContain('SERVER-SAID in a run');
    expect(describeEffort(SILENT, 'CONDUCTOR-SAID')).toContain('CONDUCTOR-SAID when it conducts a run');
  });

  test('the note says the metadata is read-only and where classes are set', () => {
    const markup = render();
    expect(markup).toContain('--set-classes');
  });
});

/** A dialog showing two textareas makes the hub owner work out which text is in force by hand. The text
 *  shown is the server's `effectiveRole`, never a precedence this file recomputes, which is why the
 *  fixture below carries an `effectiveRole` that matches neither of its own two fields. */
describe('which text is actually in force', () => {
  test('the row renders the server effective role, not a locally derived one', () => {
    const markup = render({
      roomId: 'lab',
      persona: null,
      conductorEffort: 'high',
      participants: [{ ...PLANNER, role: 'the global one', roomRole: null, effectiveRole: 'WHAT THE HUB RENDERS' }],
    });

    expect(markup).toContain('WHAT THE HUB RENDERS');
    expect(markup).toContain(SOURCE_LABEL.global);
  });

  test('each of the four states names its own source', () => {
    expect(sourceOf(PLANNER)).toBe('global');
    expect(sourceOf(SCRIBE)).toBe('room');
    expect(sourceOf(SILENT)).toBe('suppressed');
    expect(sourceOf(BLANK)).toBe('none');
  });

  test('a suppressed row says so rather than presenting the global role as in force', () => {
    const markup = render({ roomId: 'lab', persona: null, conductorEffort: 'high', participants: [SILENT] });

    expect(markup).toContain(SOURCE_LABEL.suppressed);
    // The in-force text element is absent entirely: there is no text in force, and the global role
    // this row is hiding must not be shown as though there were.
    expect(markup).toContain('roles-inforce-empty');
    expect(markup).not.toContain('roles-inforce-text');
  });

  test('a participant with no role anywhere reads as that, not as an error', () => {
    const markup = render(EMPTY);

    expect(markup).toContain(SOURCE_LABEL.none);
    expect(markup).not.toContain('roles-inforce-text');
  });

  test('every source has wording, so no state renders a bare slug', () => {
    for (const source of ['room', 'global', 'suppressed', 'none'] as const) {
      expect(SOURCE_LABEL[source].length).toBeGreaterThan(0);
    }
  });
});

/** A server-side rule that gates a button is part of the state machine. Clearing a role means saving
 *  an empty field, so a Save disabled on an empty textarea makes "clear this role" unreachable: the
 *  whole of state 1, and the only way back out of a typo. */
describe('the states the controls have to be reachable in', () => {
  test('nothing is disabled while no save is in flight, empty textareas included', () => {
    expect(render(EMPTY)).not.toContain('disabled');
  });

  // The implementation (`const saving = busy !== null`) disables every control regardless of row, so
  // the added assertion below checks that a control on a different row from the one being saved is
  // disabled too, not only the pressed row's.
  test('a save in flight disables every control and labels the pressed one Saving', () => {
    const markup = render(ROLES, null, `${PLANNER.id}|global`);

    expect(markup).toContain('disabled');
    expect(markup).toContain('Saving');

    const scribeGlobalBoxId = `${draftKey(SCRIBE.id, 'global')}-box`;
    const idIndex = markup.indexOf(`id="${scribeGlobalBoxId}"`);
    expect(idIndex).toBeGreaterThan(-1);
    expect(markup.slice(idIndex, idIndex + 300)).toContain('disabled');
  });

  test('the override row offers BOTH clear and suppress, which are different operations', () => {
    const markup = render();

    for (const op of OVERRIDE_OPS) expect(markup).toContain(op.label);
    expect(OVERRIDE_OPS).toHaveLength(2);
    expect(new Set(OVERRIDE_OPS.map((op) => op.role)).size).toBe(2);
  });
});

/** An empty save clears the persona and the global role, but the room box's Save posts the suppress
 *  sentinel rather than deleting the override. The note is the only place the hub owner is told which of
 *  those two an empty save does, so it has to name both room controls. */
describe('what the note promises about an empty save', () => {
  function noteText(markup: string): string {
    const open = markup.indexOf('<p class="dialog-note">');
    expect(open).toBeGreaterThan(-1);
    const start = open + '<p class="dialog-note">'.length;
    return markup.slice(start, markup.indexOf('</p>', start));
  }

  test('the note names both room controls rather than promising one rule for every box', () => {
    const note = noteText(render());

    for (const op of OVERRIDE_OPS) expect(note).toContain(op.label);
  });

  test('the note no longer claims an empty save clears every box', () => {
    expect(noteText(render())).not.toContain('Saving an empty box clears that text.');
  });
});

/** Every write answers with the room's whole state; re-seeding all 2N+1 boxes from it would throw away
 *  unsaved text in every other box, so a save re-seeds only the box it saved. `renderToStaticMarkup`
 *  runs no effects, so the seam that can be tested here is the decision itself, lifted out as a pure
 *  function exactly as `saveStanding` lifts out the effectful half of a write. */
describe('what a save re-seeds', () => {
  const TYPED: Record<string, string> = {
    [PERSONA_KEY]: 'a persona the owner has not saved yet',
    [draftKey(PLANNER.id, 'global')]: 'half a global role',
    [draftKey(PLANNER.id, 'room')]: 'half an override',
    [draftKey(SCRIBE.id, 'global')]: 'another unsaved edit',
    [draftKey(SCRIBE.id, 'room')]: 'and another',
  };

  function expectKeptExcept(next: Record<string, string>, saved: string) {
    for (const key of Object.keys(TYPED)) {
      if (key !== saved) expect(next[key]).toBe(TYPED[key]);
    }
  }

  test('the first roles seeds every box, persona included', () => {
    const seeded = seedDrafts(ROLES);

    expect(reseedDrafts({}, ROLES, null)).toEqual(seeded);
    expect(seeded[PERSONA_KEY]).toBe(ROLES.persona);
    expect(Object.keys(seeded)).toHaveLength(2 * ROLES.participants.length + 1);
  });

  test('a persona save re-seeds the persona box and leaves every other box typed', () => {
    const next = reseedDrafts(TYPED, ROLES, PERSONA_KEY);

    expect(next[PERSONA_KEY]).toBe(ROLES.persona);
    expectKeptExcept(next, PERSONA_KEY);
  });

  test('a global-role save re-seeds that participant global box only', () => {
    const saved = draftKey(PLANNER.id, 'global');

    const next = reseedDrafts(TYPED, ROLES, saved);

    expect(next[saved]).toBe(PLANNER.role);
    expectKeptExcept(next, saved);
  });

  test('an override save re-seeds that participant room box only', () => {
    const saved = draftKey(SCRIBE.id, 'room');

    const next = reseedDrafts(TYPED, ROLES, saved);

    expect(next[saved]).toBe(SCRIBE.roomRole);
    expectKeptExcept(next, saved);
  });

  test('a cleared override re-seeds its own box to empty rather than leaving the old text', () => {
    const saved = draftKey(SCRIBE.id, 'room');
    const cleared: RoomRoles = {
      ...ROLES,
      participants: ROLES.participants.map((row) =>
        row.id === SCRIBE.id ? { ...row, roomRole: null, effectiveRole: row.role } : row,
      ),
    };

    expect(reseedDrafts(TYPED, cleared, saved)[saved]).toBe('');
  });
});

/** The transport half. The editor hands each op's `role` straight to `api.setRoomRole`, so driving
 *  the same array through the same call is what proves the two controls are not one control with
 *  two labels. */
describe('what each control posts', () => {
  test('the persona goes to the room persona route with the typed text', async () => {
    const calls = stubFetch(() => json(ROLES));

    await api.setPersona('lab', 'A quiet room.');

    expect(calls[0]?.url).toBe('/api/rooms/lab/persona');
    expect(calls[0]?.init?.method).toBe('POST');
    expect(calls[0]?.init?.body).toBe(JSON.stringify({ persona: 'A quiet room.' }));
  });

  test('a room override goes to the per-room route, never the global one', async () => {
    const calls = stubFetch(() => json(ROLES));

    await api.setRoomRole('lab', 'gpt-5.6-sol', 'You keep the notes here.');

    expect(calls[0]?.url).toBe('/api/rooms/lab/roles/gpt-5.6-sol');
    expect(calls[0]?.url).not.toContain('/participants/');
    expect(calls[0]?.init?.body).toBe(JSON.stringify({ role: 'You keep the notes here.' }));
  });

  test('a global role goes to the participant route, and an empty one is the clear path', async () => {
    const calls = stubFetch(() => json({ id: 'opus', displayName: 'Opus', role: null }));

    await api.setGlobalRole('opus', '');

    expect(calls[0]?.url).toBe('/api/participants/opus/role');
    expect(calls[0]?.init?.body).toBe(JSON.stringify({ role: '' }));
  });

  test('clear override and no-role-in-this-room post DIFFERENT bodies', async () => {
    const bodies: string[] = [];
    for (const op of OVERRIDE_OPS) {
      const calls = stubFetch(() => json(ROLES));
      await api.setRoomRole('lab', 'opus', op.role);
      bodies.push(String(calls[0]?.init?.body));
    }

    // `role` omitted falls back to the global; `role: ""` stores the suppress sentinel.
    expect(bodies).toEqual(['{}', '{"role":""}']);
    expect(bodies[0]).not.toBe(bodies[1]);
  });
});

/** The shared refusal wording, not a second dialect of it: 401/403 get the sentence the rest of the UI
 *  uses, and every other refusal is the hub's own sentence shown verbatim (the hub writes those for
 *  the hub owner). Both are prefixed with what did not happen, exactly as `App`'s token prompt is. */
describe('when the hub refuses a save', () => {
  function record(): { events: string[]; hooks: SaveHooks } {
    const events: string[] = [];
    return {
      events,
      hooks: {
        begin: () => events.push('begin'),
        done: (roles) => events.push(`done ${roles.roomId}`),
        fail: (message) => events.push(`fail: ${message}`),
        end: () => events.push('end'),
      },
    };
  }

  test('a successful save applies what the server answered with', async () => {
    stubFetch(() => json(ROLES));
    const { events, hooks } = record();

    await saveStanding(() => api.setPersona('lab', 'x'), 'The persona was not saved.', hooks);

    expect(events).toEqual(['begin', 'done lab', 'end']);
  });

  test('a 403 gets the credential sentence the rest of the UI uses, and says what did not happen', async () => {
    stubFetch(() => json({ error: 'unauthorized' }, 403));
    const { events, hooks } = record();

    await saveStanding(() => api.setPersona('lab', 'x'), 'The persona was not saved.', hooks);

    expect(events).toContain("fail: The persona was not saved. The hub refused that token: it is not the owner's.");
    expect(events.at(-1)).toBe('end');
  });

  test('every other refusal shows the hub its own sentence, verbatim', async () => {
    stubFetch(() => json({ error: 'Persona exceeds 2000 characters.' }, 400));
    const { events, hooks } = record();

    await saveStanding(() => api.setPersona('lab', 'x'), 'The persona was not saved.', hooks);

    expect(events).toContain('fail: The persona was not saved. Persona exceeds 2000 characters.');
    expect(events).not.toContain('done lab');
  });

  test('the dialog shows the sentence it was handed', () => {
    expect(render(ROLES, 'The persona was not saved. Persona exceeds 2000 characters.')).toContain(
      'Persona exceeds 2000 characters.',
    );
  });
});
