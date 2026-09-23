import { afterEach, describe, expect, test, vi } from 'vitest';
import { call, host, isHosted, onState, type ShellState } from './hostBridge';

/** The page half of the shell's wire. There is no jsdom here (the same choice every
 *  other client test makes), so `window` is a stub and the WebView2 object is a recording fake: the
 *  questions are what goes out on `postMessage`, which replies a pending call accepts, and that a
 *  page loaded in a plain browser — which is what a bare node environment looks like — never touches
 *  `window` at all.
 *
 *  The last one is the load-bearing case. `App.tsx` calls `isHosted()` and `ChromeBar` imports this
 *  module, so anything here that read `window` while the module was being evaluated would take out
 *  every other test in this suite rather than just these. */

interface Sent {
  id?: number;
  cmd?: string;
}

/** A hosted page: `window.chrome.webview` present, recording what the page posts and able to push a
 *  message back the way the shell would. */
function hosted() {
  const sent: Sent[] = [];
  const listeners: ((e: { data: unknown }) => void)[] = [];
  vi.stubGlobal('window', {
    chrome: {
      webview: {
        postMessage: (m: unknown) => void sent.push(m as Sent),
        addEventListener: (_type: 'message', handler: (e: { data: unknown }) => void) => void listeners.push(handler),
      },
    },
  });
  return {
    sent,
    listenerCount: () => listeners.length,
    deliver: (data: unknown) => {
      for (const listener of [...listeners]) listener({ data });
    },
  };
}

const READY: ShellState = { maximized: false, hub: { state: 'ready', port: 8790, reason: null } };

describe('detecting the shell', () => {
  afterEach(() => vi.unstubAllGlobals());

  test('a page with no window at all is not hosted, and asking does not throw', () => {
    expect(isHosted()).toBe(false);
  });

  test('a window without chrome.webview is a plain browser', () => {
    vi.stubGlobal('window', { localStorage: {} });

    expect(isHosted()).toBe(false);
  });

  test('a window carrying a WebView2 object is the shell', () => {
    hosted();

    expect(isHosted()).toBe(true);
  });
});

describe('calling the host', () => {
  afterEach(() => vi.unstubAllGlobals());

  test('it posts an id and the command, and resolves with the reply the shell sent back', async () => {
    const page = hosted();

    const answer = call<ShellState>('getState', 500);
    const id = page.sent[0]?.id;
    expect(page.sent[0]?.cmd).toBe('getState');
    expect(typeof id).toBe('number');
    page.deliver({ id, ok: true, result: READY });

    await expect(answer).resolves.toEqual(READY);
  });

  test('a reply for some other call is ignored, so the real one is still waiting', async () => {
    const page = hosted();

    const answer = call('minimize', 30);
    page.deliver({ id: (page.sent[0]?.id ?? 0) + 500, ok: true, result: 'not yours' });

    await expect(answer).rejects.toThrow(/minimize/);
  });

  test('a refusal comes back as the shell worded it', async () => {
    const page = hosted();

    const answer = call('nope', 500);
    page.deliver({ id: page.sent[0]?.id, ok: false, error: "unknown command 'nope'" });

    await expect(answer).rejects.toThrow("unknown command 'nope'");
  });

  test('the page listens once, however many calls it makes', () => {
    const page = hosted();

    void call('minimize', 10).catch(() => undefined);
    void call('close', 10).catch(() => undefined);

    expect(page.listenerCount()).toBe(1);
  });

  test('in a plain browser a call is a no-op that resolves, never a crash', async () => {
    await expect(call('minimize')).resolves.toBeUndefined();
  });
});

describe('the button helpers', () => {
  afterEach(() => vi.unstubAllGlobals());

  test('each one posts its own command', async () => {
    const page = hosted();

    const pressed = Promise.all([host.minimize(), host.toggleMaximize(), host.close()]);
    for (const message of page.sent) page.deliver({ id: message.id, ok: true, result: null });

    await pressed;
    expect(page.sent.map((m) => m.cmd)).toEqual(['minimize', 'toggleMaximize', 'close']);
  });

  /** A window button whose press turns into an unhandled rejection because the shell was busy is a
   *  console full of noise for a press that does not matter. The helpers swallow; `getState` does
   *  not, because its caller has a fallback to choose. */
  test('a refused press settles instead of rejecting', async () => {
    const page = hosted();

    const pressed = host.close();
    page.deliver({ id: page.sent[0]?.id, ok: false, error: 'busy' });

    await expect(pressed).resolves.toBeUndefined();
  });
});

describe('state events', () => {
  afterEach(() => vi.unstubAllGlobals());

  test('a state event reaches the handler', () => {
    const page = hosted();
    const seen: ShellState[] = [];

    onState((s) => void seen.push(s));
    page.deliver({ event: 'state', maximized: true, hub: { state: 'attached', port: 8795, reason: null } });

    expect(seen).toEqual([{ maximized: true, hub: { state: 'attached', port: 8795, reason: null } }]);
  });

  test('unsubscribing stops it', () => {
    const page = hosted();
    const seen: ShellState[] = [];

    const stop = onState((s) => void seen.push(s));
    stop();
    page.deliver({ event: 'state', maximized: false, hub: { state: 'ready', port: 8790, reason: null } });

    expect(seen).toEqual([]);
  });

  test('a reply is not a state event, and vice versa', () => {
    const page = hosted();
    const seen: ShellState[] = [];

    onState((s) => void seen.push(s));
    page.deliver({ id: 9999, ok: true, result: READY });
    page.deliver('not an object');

    expect(seen).toEqual([]);
  });

  test('in a plain browser subscribing hands back a no-op', () => {
    expect(() => onState(() => undefined)()).not.toThrow();
  });
});
