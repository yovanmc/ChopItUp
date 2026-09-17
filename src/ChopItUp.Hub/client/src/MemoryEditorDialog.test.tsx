import { renderToStaticMarkup } from 'react-dom/server';
import { afterEach, describe, expect, test, vi } from 'vitest';
import {
  canPreview,
  countLine,
  isDirty,
  LOCKED_HINT,
  MemoryEditor,
  NEEDS_TOKEN,
  saveEdit,
  savedLine,
  type EditHooks,
} from './MemoryEditorDialog';
import RoomHeader from './RoomHeader';
import type { MemoryEditResult, MemoryFile, MemoryFileText, MemoryPreview, Room } from './types';

/** Row 40, task 3. The dialog is the only door into the memory files from a phone, so what
 *  these cases bind is the part a naive build gets wrong: that Save is unreachable in exactly the
 *  states the hub would refuse (a spawn in flight, no owner token, over the cap), that the count on
 *  screen is the hub's composed size rather than the textarea's length, and that a refused save says
 *  the hub's own sentence instead of a dialect of it.
 *
 *  `renderToStaticMarkup` and a recording `fetch`, like `RolesDialog.test.tsx`: this client has no
 *  jsdom, so rendering IS the proof it renders, effects do not run here, and the effectful halves
 *  (`saveEdit`) and the decisions (`countLine`, `isDirty`, `canPreview`, `savedLine`) live outside the
 *  component where they can run without a DOM. The press itself is the UIA gate's job. What keeps the
 *  two halves honest is that the component renders `countLine` and gates its preview on `canPreview`,
 *  so the cases below drive the same functions the component does. */

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

/** The stored token, as `localStorage` would hand it back (`api.test.ts` uses the same stub: a test
 *  that stubs no `window` at all IS the tokenless client). */
function storeToken(token: string): void {
  vi.stubGlobal('window', {
    localStorage: { getItem: () => token, setItem: () => undefined, removeItem: () => undefined },
  });
}

afterEach(() => vi.unstubAllGlobals());

const CORE: MemoryFile = { slug: 'core', path: 'MEMORY.md', chars: 4210, cap: 6000 };
const USER: MemoryFile = { slug: 'user', path: 'topics/user.md', chars: 980, cap: 24000 };
const LOADED: MemoryFileText = {
  ...CORE,
  text: '# Memory\n\n## Standing rules\nRED before GREEN.\n',
  hash: 'a'.repeat(64),
};

const ROOM: Room = {
  id: 'general',
  name: 'General',
  createdAt: '2026-09-17T00:00:00Z',
  messageCount: 3,
  lastMessageId: 3,
  directory: null,
  archivedAt: null,
  lastActivityAt: '2026-09-17T00:00:00Z',
  unread: 0,
  persona: null,
};

const NOOP = () => undefined;

function render(overrides: Partial<Parameters<typeof MemoryEditor>[0]> = {}): string {
  return renderToStaticMarkup(
    <MemoryEditor
      roomName="General"
      files={[CORE, USER]}
      slug="core"
      loaded={LOADED}
      text={LOADED.text}
      preview={null}
      hasToken
      locked={false}
      saving={false}
      error={null}
      status={null}
      onPick={NOOP}
      onEdit={NOOP}
      onReload={NOOP}
      onSave={NOOP}
      onClose={NOOP}
      {...overrides}
    />,
  );
}

/** The text of the one element a class names, so a hint assertion is about the hint and not about the
 *  hint appearing anywhere in the dialog. */
function textOf(markup: string, className: string): string {
  const open = markup.indexOf(`class="${className}"`);
  expect(open).toBeGreaterThan(-1);
  const start = markup.indexOf('>', open) + 1;
  return markup.slice(start, markup.indexOf('<', start));
}

describe('what the picker offers', () => {
  test('the core comes first and every option carries its count against its cap', () => {
    const markup = render();

    expect(markup.indexOf('MEMORY.md (core)')).toBeLessThan(markup.indexOf('topics/user.md'));
    expect(markup).toContain('MEMORY.md (core) · 4,210 of 6,000');
    expect(markup).toContain('topics/user.md · 980 of 24,000');
    expect(markup).toContain('aria-label="Memory file"');
    expect(markup).toContain('aria-label="Memory text"');
  });

  test('switching files is blocked while the text is dirty, and open while it is not', () => {
    const clean = render();
    const dirty = render({ text: `${LOADED.text}## More\nTyped.\n` });

    expect(clean.slice(clean.indexOf('<select'), clean.indexOf('</select>'))).not.toContain('disabled');
    expect(dirty.slice(dirty.indexOf('<select'), dirty.indexOf('</select>'))).toContain('disabled');
  });
});

/** LESSON M25: a server-side rule that gates a button is part of the state machine, so the states the
 *  hub refuses have to be visible here and the way out of each has to stay reachable. */
describe('the states Save is unreachable in', () => {
  test('a clean file cannot be saved, and an edited one can', () => {
    const clean = render();
    const dirty = render({ text: `${LOADED.text}## More\nTyped.\n` });

    expect(clean.slice(clean.indexOf('<footer'))).toContain('disabled');
    expect(dirty.slice(dirty.indexOf('<footer'))).not.toContain('disabled');
    expect(isDirty(LOADED.text, LOADED.text)).toBe(false);
    expect(isDirty(LOADED.text, `${LOADED.text}x`)).toBe(true);
  });

  test('a spawn in flight disables Save and the hint is the sentence the panel promises', () => {
    const markup = render({ locked: true, text: `${LOADED.text}## More\nTyped.\n` });

    expect(textOf(markup, 'memory-editor-hint')).toBe('A spawn is running; save when the exchange has finished.');
    expect(LOCKED_HINT).toBe('A spawn is running; save when the exchange has finished.');
    expect(markup.slice(markup.indexOf('<footer'))).toContain('disabled');
  });

  test('a client with no owner token says so, cannot save, and never asks the hub for a count', () => {
    const markup = render({ hasToken: false, text: `${LOADED.text}## More\nTyped.\n` });

    expect(textOf(markup, 'memory-editor-hint')).toBe(NEEDS_TOKEN);
    expect(NEEDS_TOKEN).toBe('The hub needs the owner token before it will accept a write from this browser.');
    expect(markup.slice(markup.indexOf('<footer'))).toContain('disabled');
    // Every POST under /api needs an owner bearer and each accepted bearer costs a peer-process
    // check, so the debounced preview is gated on the same function the effect asks.
    expect(canPreview(false, LOADED)).toBe(false);
    expect(canPreview(true, null)).toBe(false);
    expect(canPreview(true, LOADED)).toBe(true);
  });

  test('Reload stays enabled while a save is refused, because it is the way back from a stale file', () => {
    const markup = render({
      text: `${LOADED.text}## More\nTyped.\n`,
      error: 'That edit was not saved. The file changed since you opened it. Reload it and apply your edit again.',
    });

    // The footer, not the whole dialog: the hub's stale sentence contains the word Reload itself.
    const footer = markup.slice(markup.indexOf('<footer'));
    const button = footer.indexOf('>Reload<');
    expect(button).toBeGreaterThan(-1);
    expect(footer.slice(footer.lastIndexOf('<button', button), button)).not.toContain('disabled');
    expect(markup).toContain('The file changed since you opened it.');
  });
});

/** AC7: the cap is enforced on the composed file — the marker line and every carried approval record
 *  included — so a count taken from the textarea would promise room the hub does not have. */
describe('which number the count line shows', () => {
  const under: MemoryPreview = { slug: 'core', chars: 5120, cap: 6000, over: false };
  const over: MemoryPreview = { slug: 'core', chars: 6120, cap: 6000, over: true };

  test('the hub count wins once it answers', () => {
    expect(countLine(under, 4000)).toBe('5,120 of 6,000 characters as the hub would write it');
    expect(countLine(under, 4000)).not.toContain('4,000');
  });

  test('over the cap says by how much', () => {
    expect(countLine(over, 4000)).toContain('over the cap by 120');
  });

  test('until it answers the raw length is shown and labelled as the typed one', () => {
    expect(countLine(null, 4000)).toBe('4,000 characters (typed; the hub adds its bookkeeping lines)');
  });

  test('the dialog renders the line, and refuses a save the hub would refuse on size', () => {
    const markup = render({ preview: over, text: `${LOADED.text}x` });

    expect(markup).toContain('over the cap by 120');
    expect(markup.slice(markup.indexOf('<footer'))).toContain('disabled');
  });
});

/** The trail is what a save buys, and the `.bak` is the way back: the line shown
 *  after a save has to name the row, the commit and that file. */
describe('what the status line says after a save', () => {
  const RESULT: MemoryEditResult = {
    ...LOADED,
    text: '# Memory\n<!-- rewritten: approved -->\n\n## Standing rules\nRED before GREEN.\n',
    hash: 'b'.repeat(64),
    chars: 71,
    proposal: {
      id: 12,
      roomId: 'general',
      authorId: 'owner',
      topic: 'core',
      title: 'Edit core',
      body: 'x',
      status: 'approved',
      source: 'editor',
      createdAt: '2026-09-17T00:00:00Z',
      decidedAt: '2026-09-17T00:00:01Z',
      writtenTo: 'MEMORY.md',
      commitHash: '9f3c1ab',
      kind: 'rewrite',
      replaces: null,
      flags: [],
      related: [],
    },
    backup: 'MEMORY.md.rewrite-12.bak',
  };

  test('it names the proposal, the commit and the backup', () => {
    expect(savedLine(RESULT)).toBe('Saved as proposal #12 (commit 9f3c1ab); the previous text is at MEMORY.md.rewrite-12.bak.');
    expect(render({ status: savedLine(RESULT) })).toContain('MEMORY.md.rewrite-12.bak');
  });

  test('a hub without git says the write is uncommitted rather than inventing a hash', () => {
    const line = savedLine({ ...RESULT, proposal: { ...RESULT.proposal, commitHash: null } });

    expect(line).toBe(
      'Saved as proposal #12 (not committed: git unavailable; see the hub log); the previous text is at MEMORY.md.rewrite-12.bak.',
    );
  });
});

/** The transport half. `saveEdit` is what the Save button runs, so driving it through the recording
 *  fetch is what proves the PUT carries the base hash it was read with and an owner bearer, and that
 *  every refusal arrives as the hub's own sentence (row 28's wording, prefixed with what did not
 *  happen — exactly as `RolesDialog.saveStanding` does it). */
describe('what a save sends and what a refusal says', () => {
  function record(): { events: string[]; hooks: EditHooks } {
    const events: string[] = [];
    return {
      events,
      hooks: {
        begin: () => events.push('begin'),
        done: (result) => events.push(`done #${result.proposal.id} ${result.backup}`),
        fail: (message) => events.push(`fail: ${message}`),
        end: () => events.push('end'),
      },
    };
  }

  test('the PUT carries the room, the text and the hash the read returned, with the owner token', async () => {
    storeToken('owner-token');
    const calls = stubFetch(() =>
      json({
        ...LOADED,
        proposal: { id: 12, commitHash: '9f3c1ab' },
        backup: 'MEMORY.md.rewrite-12.bak',
      }),
    );
    const { events, hooks } = record();

    await saveEdit('core', 'general', '# Memory\n\n## Rules\nNew.\n', LOADED.hash, hooks);

    expect(calls[0]?.url).toBe('/api/memory/topics/core');
    expect(calls[0]?.init?.method).toBe('PUT');
    expect(calls[0]?.init?.body).toBe(
      JSON.stringify({ roomId: 'general', text: '# Memory\n\n## Rules\nNew.\n', baseHash: LOADED.hash }),
    );
    expect(new Headers(calls[0]?.init?.headers).get('authorization')).toBe('Bearer owner-token');
    expect(events).toEqual(['begin', 'done #12 MEMORY.md.rewrite-12.bak', 'end']);
  });

  test('a 409 shows the hub its own sentence, verbatim, and writes nothing to the dialog', async () => {
    // The hub's own StaleEdit sentence (`MemoryApi.StaleEdit`), which this dialog must not paraphrase.
    stubFetch(() => json({ error: 'The file changed since you opened it. Reload it and apply your edit again.' }, 409));
    const { events, hooks } = record();

    await saveEdit('core', 'general', 'x', LOADED.hash, hooks);

    expect(events).toContain(
      'fail: That edit was not saved. The file changed since you opened it. Reload it and apply your edit again.',
    );
    expect(events.at(-1)).toBe('end');
    expect(events.some((event) => event.startsWith('done'))).toBe(false);
  });

  test('a 401 gets the credential sentence the rest of the UI uses, not the hub envelope word', async () => {
    stubFetch(() => json({ error: 'unauthorized' }, 401));
    const { events, hooks } = record();

    await saveEdit('core', 'general', 'x', LOADED.hash, hooks);

    expect(events).toContain(`fail: That edit was not saved. ${NEEDS_TOKEN}`);
    expect(events).not.toContain('fail: That edit was not saved. unauthorized');
  });
});

/** R9: the door is a header button beside `Roles`, not an eighth button called `Memory` next to
 *  `Import memory`. */
describe('the way in', () => {
  test('the room header renders an Edit memory button', () => {
    const markup = renderToStaticMarkup(
      <RoomHeader
        room={ROOM}
        loadedCount={3}
        busy={false}
        onImport={NOOP}
        onImportMemory={NOOP}
        onBind={NOOP}
        onArchive={NOOP}
        onTrail={NOOP}
        onRoles={NOOP}
        onMemory={NOOP}
      />,
    );

    expect(markup).toContain('Edit memory');
    expect(markup.indexOf('Roles')).toBeLessThan(markup.indexOf('Edit memory'));
  });
});
