import { afterEach, describe, expect, test, vi } from 'vitest';
import * as api from './api';
import type { SkillProposal } from './types';

/** Row 28 Task 5. Every non-GET `/api` request now needs an owner credential, so the questions this
 *  file answers are transport questions: does the token go out, does it go out on the RIGHT calls,
 *  does a caller's own credential survive, and does the client still know 401 from 500 after
 *  `unwrap` is done with the response.
 *
 *  There is no jsdom here (see `RunBar.test.tsx` for the same choice), so `fetch` is a recording stub
 *  and the token arrives by stubbing `window`: `readOwnerToken` reads `window.localStorage` inside a
 *  try/catch, which means a test that stubs no `window` at all IS the tokenless client. */

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

/** The stored token, as `localStorage` would hand it back. */
function storeToken(token: string | null): void {
  vi.stubGlobal('window', {
    localStorage: {
      getItem: () => token,
      setItem: () => undefined,
      removeItem: () => undefined,
    },
  });
}

/** Read back through `Headers` rather than indexing the object: the attach is case-insensitive by
 *  construction, and this assertion has to be too or it would only prove the casing we happened to
 *  write. */
const authOf = (call: Call | undefined) => new Headers(call?.init?.headers).get('authorization');

/** The failure a call threw, so the assertion can be about the failure rather than about `rejects`
 *  matchers — the point of this task is that the thrown value carries something. */
async function refusal(run: () => Promise<unknown>): Promise<unknown> {
  try {
    await run();
  } catch (failure) {
    return failure;
  }
  throw new Error('the call was expected to fail and did not');
}

const PROPOSAL: SkillProposal = {
  id: 4,
  roomId: 'lab',
  authorId: 'sonnet',
  name: 'build-thing',
  replacesInstalled: false,
  force: false,
  fileCount: 1,
  bytes: 12,
  status: 'pending',
  createdAt: '2026-03-01T09:00:00.0000000+00:00',
  decidedAt: null,
  installedAt: null,
  sourceMissing: false,
  sourceChanged: false,
  approvable: true,
  treeSha256: 'abc123',
  entries: [],
  gates: [],
};

afterEach(() => vi.unstubAllGlobals());

describe('the owner token on writes', () => {
  test('a write carries the stored token', async () => {
    storeToken('owner-secret');
    const calls = stubFetch(() => json({ id: 1 }));

    await api.postMessage('lab', 'hello');

    expect(authOf(calls[0])).toBe('Bearer owner-secret');
  });

  /** AC2 keeps GETs open, and a read that carried the owner's credential would hand it to a surface
   *  that has no use for it. */
  test('a read stays a read: no credential on a GET', async () => {
    storeToken('owner-secret');
    const calls = stubFetch(() => json([]));

    await api.listRooms();

    expect(authOf(calls[0])).toBeNull();
  });

  test('with no token stored the write goes out bare rather than sending "Bearer null"', async () => {
    const calls = stubFetch(() => json({ id: 1 }));

    await api.postMessage('lab', 'hello');

    expect(authOf(calls[0])).toBeNull();
  });

  test('the headers a write already set survive the attach', async () => {
    storeToken('owner-secret');
    const calls = stubFetch(() => json({ id: 1 }));

    await api.postMessage('lab', 'hello');

    expect(new Headers(calls[0]?.init?.headers).get('content-type')).toBe('application/json');
  });

  test('the background read-cursor write carries it too — every non-GET does, by method not by list', async () => {
    storeToken('owner-secret');
    const calls = stubFetch(() => json({ roomId: 'lab', unread: 0 }));

    await api.markRead('lab');

    expect(authOf(calls[0])).toBe('Bearer owner-secret');
  });

  test('a DELETE is a write like any other', async () => {
    storeToken('owner-secret');
    const calls = stubFetch(() => json({ discarded: 2 }));

    await api.discardImport('claude', 'C:/x/CLAUDE.md');

    expect(authOf(calls[0])).toBe('Bearer owner-secret');
  });

  /** The regression the merge rule exists to stop: `decideSkillProposal` builds its own
   *  `authorization` from the token it was HANDED, which is the value the owner typed into the skill
   *  card. A blanket attach that overwrote it would silently send the stored one instead, so a
   *  just-pasted token would appear not to work and the owner would have no way to tell why. */
  test('the skills approve path keeps the token it was handed, not the stored one', async () => {
    storeToken('stored-owner-token');
    const calls = stubFetch(() => json({}));

    await api.decideSkillProposal(PROPOSAL, 'approve', 'the-token-the-owner-typed');

    expect(authOf(calls[0])).toBe('Bearer the-token-the-owner-typed');
  });

  test('and the reject path, which builds a different init, keeps it as well', async () => {
    storeToken('stored-owner-token');
    const calls = stubFetch(() => json({}));

    await api.decideSkillProposal(PROPOSAL, 'reject', 'the-token-the-owner-typed');

    expect(authOf(calls[0])).toBe('Bearer the-token-the-owner-typed');
  });
});

describe('a failure keeps its status', () => {
  test('a 401 is a credential refusal, and says so in words the owner can act on', async () => {
    stubFetch(() => json({ error: 'unauthorized' }, 401));

    const failure = await refusal(() => api.postMessage('lab', 'hello'));

    expect(api.isCredentialRefusal(failure)).toBe(true);
    expect(api.describeError(failure)).toContain('owner token');
    // The hub's own envelope says "unauthorized", which tells the owner nothing about what to do.
    expect(api.describeError(failure)).not.toBe('unauthorized');
  });

  test('a 403 is also a refusal a paste can fix, and names the different cause', async () => {
    stubFetch(() => json({ error: 'forbidden' }, 403));

    const failure = await refusal(() => api.postMessage('lab', 'hello'));

    expect(api.isCredentialRefusal(failure)).toBe(true);
    expect(api.describeError(failure)).toContain('not the owner');
  });

  /** The 401-vs-500 split is the whole reason `unwrap` had to stop discarding the status: every
   *  branch in this task keys off it, and a client that could not tell them apart would offer the
   *  paste prompt for a hub that had crashed. */
  test('a 500 is not a credential refusal and still shows the hub its own sentence', async () => {
    stubFetch(() => json({ error: 'the store is locked.' }, 500));

    const failure = await refusal(() => api.postMessage('lab', 'hello'));

    expect(api.isCredentialRefusal(failure)).toBe(false);
    expect(api.describeError(failure)).toBe('the store is locked.');
  });

  test('a 404 with no JSON envelope still carries its status and its status line', async () => {
    stubFetch(() => new Response('nope', { status: 404, statusText: 'Not Found' }));

    const failure = await refusal(() => api.postMessage('lab', 'hello'));

    expect(api.isCredentialRefusal(failure)).toBe(false);
    expect(api.describeError(failure)).toContain('404');
  });

  /** `markRead` fires on every room open, so App must be able to recognise its refusal WITHOUT a
   *  prompt. It throws like every other call; the quiet handling is App's, and this is the value it
   *  branches on. */
  test('a refused markRead throws a recognisable credential refusal', async () => {
    stubFetch(() => json({ error: 'unauthorized' }, 401));

    expect(api.isCredentialRefusal(await refusal(() => api.markRead('lab')))).toBe(true);
  });

  test('a markRead that failed for any other reason is not one, so the rail stays quiet', async () => {
    stubFetch(() => json({ error: "Unknown room 'lab'." }, 404));

    expect(api.isCredentialRefusal(await refusal(() => api.markRead('lab')))).toBe(false);
  });

  test('a thrown non-Error is still described rather than crashing the describe path', () => {
    expect(api.describeError('plain string')).toBe('plain string');
    expect(api.isCredentialRefusal('plain string')).toBe(false);
  });
});
