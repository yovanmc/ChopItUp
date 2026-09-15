/** Row 12: the page's half of the desktop shell's wire. `ChopItUp.Desktop` hosts this same client in a
 *  chromeless WebView2 window, which means the page draws the title bar and has to be able to ask the
 *  window to minimise, maximise and close itself. WebView2 gives exactly one channel for that,
 *  `window.chrome.webview`, and this module is the only place in the client that knows it exists.
 *
 *  The grammar is the shell's (`HostBridge.Handle`): a request is `{id, cmd}`, a reply is
 *  `{id, ok:true, result}` or `{id, ok:false, error}`, and the shell pushes `{event:'state', ...}`
 *  whenever the window's maximised state or the hub's changes. Ids exist because replies arrive on the
 *  same `message` event as events and as each other; a reply for an id nobody is waiting on is
 *  dropped rather than guessed at.
 *
 *  Nothing here touches `window` at module scope. Vitest runs this suite under node with no jsdom, and
 *  `App.tsx` imports it, so a top-level `window` read would take out the whole client suite rather
 *  than one test. Every entry point asks `webview()` first and answers harmlessly when the page is a
 *  plain browser tab, which is also what the deployed hub serves to a real browser. */

interface WebViewLike {
  postMessage(m: unknown): void;
  addEventListener(t: 'message', h: (e: { data: unknown }) => void): void;
}

function webview(): WebViewLike | null {
  if (typeof window === 'undefined') return null;
  const found = (window as unknown as { chrome?: { webview?: WebViewLike } }).chrome?.webview;
  if (!found || typeof found.postMessage !== 'function' || typeof found.addEventListener !== 'function') return null;
  return found;
}

/** True only inside `ChopItUp.Desktop`. Call it from inside a component body or a handler, never at
 *  module scope. */
export function isHosted(): boolean {
  return webview() !== null;
}

export type HubState = 'starting' | 'ready' | 'attached' | 'failed' | 'stopped';

export interface ShellState {
  maximized: boolean;
  hub: { state: HubState; port: number; reason: string | null };
}

interface Pending {
  settle: (value: unknown) => void;
  fail: (failure: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

const pending = new Map<number, Pending>();
const stateHandlers = new Set<(s: ShellState) => void>();
let nextId = 1;
/** The webview this module has already subscribed to. Compared by identity rather than kept as a
 *  boolean so a test that swaps the stubbed `window` gets a listener on the new one. */
let listeningTo: WebViewLike | null = null;

function receive(data: unknown): void {
  if (typeof data !== 'object' || data === null) return;
  const message = data as { id?: unknown; ok?: unknown; result?: unknown; error?: unknown; event?: unknown };

  if (message.event === 'state') {
    const event = data as { maximized?: unknown; hub?: unknown };
    if (typeof event.hub !== 'object' || event.hub === null) return;
    const state: ShellState = { maximized: event.maximized === true, hub: event.hub as ShellState['hub'] };
    for (const handler of [...stateHandlers]) handler(state);
    return;
  }

  if (typeof message.id !== 'number') return;
  const waiting = pending.get(message.id);
  if (waiting === undefined) return;
  pending.delete(message.id);
  clearTimeout(waiting.timer);
  if (message.ok === true) waiting.settle(message.result);
  else waiting.fail(new Error(typeof message.error === 'string' ? message.error : 'the shell refused the call'));
}

function connected(): WebViewLike | null {
  const found = webview();
  if (found === null) return null;
  if (listeningTo !== found) {
    found.addEventListener('message', (e) => receive(e.data));
    listeningTo = found;
  }
  return found;
}

/** Asks the shell to do one thing. Resolves with the shell's `result`, rejects with the error it
 *  worded, and resolves `undefined` in a plain browser — a caller that has nothing to show without an
 *  answer checks for `undefined`, and a caller that only wanted the side effect ignores it. */
export function call<T = unknown>(cmd: string, timeoutMs = 10_000): Promise<T | undefined> {
  const view = connected();
  if (view === null) return Promise.resolve(undefined);
  const id = nextId++;
  return new Promise<T | undefined>((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`the shell did not answer '${cmd}' within ${timeoutMs} ms`));
    }, timeoutMs);
    pending.set(id, { settle: (value) => resolve(value as T), fail: reject, timer });
    try {
      view.postMessage({ id, cmd });
    } catch (failure) {
      pending.delete(id);
      clearTimeout(timer);
      reject(failure instanceof Error ? failure : new Error(String(failure)));
    }
  });
}

/** Subscribes to the shell's state pushes. Returns the unsubscribe, which is a no-op in a browser. */
export function onState(handler: (s: ShellState) => void): () => void {
  if (connected() === null) return () => undefined;
  stateHandlers.add(handler);
  return () => void stateHandlers.delete(handler);
}

/** A window button's press is not worth an unhandled rejection: if the shell is busy or gone, the
 *  press simply did not happen and the row stays as it was. `getState` is deliberately not wrapped —
 *  its caller keeps the state it already had. */
async function settled(work: Promise<unknown>): Promise<void> {
  try {
    await work;
  } catch {
    // Nothing to say and nowhere to say it: the title bar is the only UI there is here.
  }
}

export const host = {
  minimize: () => settled(call('minimize')),
  toggleMaximize: () => settled(call('toggleMaximize')),
  close: () => settled(call('close')),
  getState: () => call<ShellState>('getState'),
};
