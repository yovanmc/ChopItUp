import { renderToStaticMarkup } from 'react-dom/server';
import { afterEach, describe, expect, test, vi } from 'vitest';
import {
  applyExchangeFromRequest,
  continueExchangeAt,
  stopExchangeAt,
  TokenGate,
  withStopping,
  type ExchangeContinueHooks,
  type ExchangeStopHooks,
} from './App';
import { isCredentialRefusal } from './api';
import type { ExchangeSnapshot, Message } from './types';

/** Row 28, AC5's first half. A deliberate action the hub refused for want of a credential has to say
 *  what did not happen and give the owner somewhere to put the token — "Send" that silently ate the
 *  message is the failure this row is closing, not a smaller version of it.
 *
 *  `renderToStaticMarkup` for the same reason as the other component tests here: no jsdom. What that
 *  cannot prove is the wiring in `App` that decides to show this — which write raised it, and that a
 *  refused `markRead` never does. `api.test.ts` covers the value those branches key off.
 *
 *  `./markdown` is stubbed because importing `App` reaches `SkillPanel` and so DOMPurify, which needs
 *  a real DOM — the same stub `MemoryPanel.test.tsx` and `SkillPanel.test.tsx` use, and nothing here
 *  renders a message body. */
vi.mock('./markdown', () => ({ renderBody: (body: string) => `<p>${body}</p>` }));

const NOTICE = 'Your message was not posted. The hub needs the owner token before it will accept a write from this browser.';

const render = () =>
  renderToStaticMarkup(<TokenGate notice={NOTICE} onToken={() => undefined} onDismiss={() => undefined} />);

describe('TokenGate', () => {
  test('it says what did not happen, in the words the caller handed it', () => {
    expect(render()).toContain('Your message was not posted.');
  });

  test('it is announced, because it appears in answer to something the owner just pressed', () => {
    expect(render()).toContain('role="alert"');
  });

  test('it offers somewhere to paste the token, masked and never autofilled', () => {
    const markup = render();

    expect(markup).toContain('type="password"');
    // Case-insensitive: `react-dom/server` writes the JSX prop name through as `autoComplete`, which
    // the browser reads as the attribute either way. What is asserted is the value, not the casing.
    expect(markup).toMatch(/autocomplete="off"/i);
    expect(markup).toContain('Paste the owner token');
  });

  /** The skill card's field owns `owner-token`; two live fields with one id is a label that points at
   *  the wrong box, and both can be on screen at once. */
  test('its field does not collide with the skill card\'s field id', () => {
    const markup = render();

    expect(markup).toContain('id="write-token"');
    expect(markup).not.toContain('id="owner-token"');
  });

  test('the owner can put it away without pasting anything', () => {
    expect(render()).toContain('Not now');
  });
});

describe('M57 exchange replies across reconnect and room switch', () => {
  const oldStop = { roomId: 'lab', seq: 900, inFlight: ['sonnet'], inFlightStartedAt: { sonnet: '2026-09-20T12:00:00Z' } } as unknown as ExchangeSnapshot;
  const fresh = { roomId: 'lab', seq: 2, inFlight: [] } as unknown as ExchangeSnapshot;
  const otherRoom = { roomId: 'general', seq: 3, inFlight: [] } as unknown as ExchangeSnapshot;

  test('a delayed Stop reply from the old connection cannot outrank the new hub sequence', () => {
    const afterReconnect = applyExchangeFromRequest(null, oldStop, 'lab', 1, 'lab', 2);
    expect(afterReconnect).toBeNull();
    expect(applyExchangeFromRequest(afterReconnect, fresh, 'lab', 2, 'lab', 2)).toBe(fresh);
  });

  test('a delayed Stop or GET reply for the room just left cannot replace the current room', () => {
    expect(applyExchangeFromRequest(otherRoom, oldStop, 'lab', 2, 'general', 2)).toBe(otherRoom);
    expect(applyExchangeFromRequest(otherRoom, oldStop, 'lab', 2, 'lab', 2)).toBe(oldStop);
  });
});

/** Row 34, AC3: one strip's Stop. The component half (which root a press hands up, which button greys)
 *  is `ExchangeBar.test.tsx`'s; this is App's half — the call that root makes and what happens to its
 *  answer — lifted out of the component so it can run without a DOM. `fetch` is a recording stub, as
 *  in `api.test.ts`, and no `window` means a tokenless client, which is all a stub reply needs. */
describe('stopping one exchange from its strip', () => {
  afterEach(() => vi.unstubAllGlobals());

  const SNAPSHOT = { roomId: 'lab', status: 'open', seq: 14, exchanges: [] } as unknown as ExchangeSnapshot;

  function json(body: unknown, status = 200): Response {
    return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
  }

  /** Every hook and every request, in the order they happened. `refused` answers the way App's does:
   *  only a credential refusal is one. */
  function record(reply: () => Response) {
    const events: string[] = [];
    const applied: ExchangeSnapshot[] = [];
    vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
      events.push(`${init?.method ?? 'GET'} ${url}`);
      return Promise.resolve(reply());
    });
    const hooks: ExchangeStopHooks = {
      begin: () => events.push('begin'),
      apply: (snapshot) => {
        events.push('apply');
        applied.push(snapshot);
      },
      refused: (failure, didNotHappen) => {
        events.push(`refused? ${didNotHappen}`);
        return isCredentialRefusal(failure);
      },
      fail: (message) => events.push(`fail: ${message}`),
      end: () => events.push('end'),
    };
    return { events, applied, hooks };
  }

  test('it stops that root and only that root, and applies the snapshot the hub answered with', async () => {
    const { events, applied, hooks } = record(() => json(SNAPSHOT));

    await stopExchangeAt('lab', 57, hooks);

    expect(events).toEqual(['begin', 'POST /api/rooms/lab/exchanges/57/stop', 'apply', 'end']);
    expect(applied[0]?.seq).toBe(14);
  });

  test('a credential refusal raises the paste prompt with what did not happen, and no banner', async () => {
    const { events, hooks } = record(() => json({ error: 'unauthorized' }, 401));

    await stopExchangeAt('lab', 57, hooks);

    expect(events).toContain('refused? That exchange was not stopped.');
    expect(events.some((e) => e.startsWith('fail'))).toBe(false);
    expect(events).not.toContain('apply');
    expect(events.at(-1)).toBe('end');
  });

  test('any other refusal shows the hub its own sentence, and the strip is released either way', async () => {
    const { events, hooks } = record(() => json({ error: 'A run owns this room; stop the run instead.' }, 409));

    await stopExchangeAt('lab', 57, hooks);

    expect(events).toContain('fail: A run owns this room; stop the run instead.');
    expect(events).not.toContain('apply');
    expect(events.at(-1)).toBe('end');
  });
});

/** Row 44, AC5: App's half of the Continue button, lifted out of the component for the same reason the
 *  stop above is — there is no DOM here to press in. D-e routes the press through the ordinary message
 *  endpoint rather than an endpoint of its own, so what this pins is the body and the reply target: a
 *  Continue that posted anything else would leave the hub nothing to read the command from. */
describe('continuing one exchange from its strip', () => {
  afterEach(() => vi.unstubAllGlobals());

  const POSTED = { id: 91, roomId: 'lab', authorId: 'owner', body: '/continue' } as unknown as Message;

  function json(body: unknown, status = 200): Response {
    return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
  }

  function record(reply: () => Response) {
    const events: string[] = [];
    const bodies: unknown[] = [];
    const applied: Message[][] = [];
    vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
      events.push(`${init?.method ?? 'GET'} ${url}`);
      bodies.push(typeof init?.body === 'string' ? JSON.parse(init.body) : null);
      return Promise.resolve(reply());
    });
    const hooks: ExchangeContinueHooks = {
      begin: () => events.push('begin'),
      apply: (messages) => {
        events.push('apply');
        applied.push(messages);
      },
      refused: (failure, didNotHappen) => {
        events.push(`refused? ${didNotHappen}`);
        return isCredentialRefusal(failure);
      },
      fail: (message) => events.push(`fail: ${message}`),
      end: () => events.push('end'),
    };
    return { events, bodies, applied, hooks };
  }

  test('it posts /continue as a reply to that root and merges the message the hub answered with', async () => {
    const { events, bodies, applied, hooks } = record(() => json(POSTED));

    await continueExchangeAt('lab', 57, hooks);

    expect(events).toEqual(['begin', 'POST /api/rooms/lab/messages', 'apply', 'end']);
    expect(bodies[0]).toEqual({ body: '/continue', replyToId: 57 });
    expect(applied[0]?.[0]?.id).toBe(91);
  });

  test('a credential refusal says what did not happen, and the strip is released either way', async () => {
    const { events, hooks } = record(() => json({ error: 'unauthorized' }, 401));

    await continueExchangeAt('lab', 57, hooks);

    expect(events).toContain('refused? The exchange was not continued.');
    expect(events.some((e) => e.startsWith('fail'))).toBe(false);
    expect(events).not.toContain('apply');
    expect(events.at(-1)).toBe('end');
  });
});

describe('the per-root stopping set', () => {
  test('marking one root pending adds it beside the others and leaves the old set alone', () => {
    const before: ReadonlySet<number> = new Set([41]);

    const after = withStopping(before, 57, true);

    expect([...after].sort()).toEqual([41, 57]);
    expect([...before]).toEqual([41]);
  });

  test('releasing one root removes that root and no other', () => {
    expect([...withStopping(new Set([41, 57]), 57, false)]).toEqual([41]);
  });
});
