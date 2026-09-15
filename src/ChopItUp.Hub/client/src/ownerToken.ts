/** The owner's bearer token, from a one-time paste, kept in `localStorage`, and sent on EVERY non-GET
 *  `/api` request this client makes (row 28). It used to go on exactly two calls, because every other
 *  `/api` route was unauthenticated and loopback was treated as the boundary.
 *
 *  Loopback stopped being the boundary when the hub started spawning models with shell access INSIDE
 *  it: a spawn is another process on this machine, so "can reach 127.0.0.1" no longer distinguishes
 *  the owner's own hand from a participant the owner is merely talking to. An owner-attributed write
 *  therefore needs a credential the spawn does not have, and this is where the browser keeps its
 *  copy. `GET` stays open (row 28 AC2) — reads disclose only what a participant could already read
 *  through its own MCP session — so a credential on a read would buy nothing.
 *
 *  An `Authorization` header is also what makes such a request non-simple, so a cross-origin page
 *  cannot forge one and no `Origin` check is needed (D2's CSRF half).
 *
 *  Every access is wrapped: `localStorage` throws outright in a browser with site data blocked, and
 *  does not exist at all under vitest's node environment. A hub the owner has not pasted a token into
 *  reads as "no token", which is a client that can read everything and write nothing — never a crash,
 *  and never a request carrying the literal string "Bearer null".
 *
 *  Deliberately NOT here: any path that goes looking for a token on disk. The milestone's standing
 *  prohibition is that no agent reads the hub's `tokens.json` to obtain an owner credential; this
 *  value arrives by the owner pasting it, or it does not arrive.
 *
 *  Row 12 adds one source that is not a paste and is still not a disk read. When the desktop shell
 *  starts the hub itself, it mints a bearer for that launch, hands it to the child in an environment
 *  variable the hub deletes from its own environment before it can spawn anything, and sets it on
 *  every document of the hub origin as `window.__chopitupShellToken`. That global is read here and
 *  preferred over the stored value: it belongs to a hub this exact window started, and it outranks a
 *  token pasted for some earlier one. Nothing writes it — not this module, not `localStorage`, not
 *  the WebView2 profile — so it dies with the window, which is the point (a stored copy is readable
 *  by any process running as this user, including a spawn). A browser tab never sees it and keeps
 *  using the paste flow below, unchanged. */
const KEY = 'chopitup.ownerToken';

export function readOwnerToken(): string | null {
  if (typeof window !== 'undefined') {
    const injected = (window as unknown as { __chopitupShellToken?: unknown }).__chopitupShellToken;
    if (typeof injected === 'string' && injected.length > 0) return injected;
  }
  try {
    const stored = window.localStorage.getItem(KEY);
    return stored !== null && stored.length > 0 ? stored : null;
  } catch {
    return null;
  }
}

/** Stores a pasted token, or forgets the stored one when given `null` or blank text. Returns what a
 *  subsequent `readOwnerToken` would answer, so a caller can set its state from the same value
 *  without a second read. */
export function writeOwnerToken(token: string | null): string | null {
  const trimmed = token?.trim() ?? '';
  try {
    if (trimmed.length === 0) window.localStorage.removeItem(KEY);
    else window.localStorage.setItem(KEY, trimmed);
  } catch {
    // Storage is unavailable (private window, blocked site data). The token still works for this
    // session — the caller holds it in state — it just will not survive a reload.
  }
  return trimmed.length === 0 ? null : trimmed;
}
