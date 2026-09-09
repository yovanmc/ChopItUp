/** D2: the owner's bearer token, from a one-time paste, kept in `localStorage`, and sent on exactly
 *  two calls — `POST /api/skills/proposals/{id}/approve` and `.../reject`. Nothing else in this client
 *  reads it, and no other request carries it: every other `/api` route is unauthenticated by design
 *  (loopback is the boundary), so attaching a credential to them would widen the surface for nothing.
 *
 *  An `Authorization` header is also what makes those two requests non-simple, so a cross-origin page
 *  cannot forge one and no `Origin` check is needed (D2's CSRF half).
 *
 *  Every access is wrapped: `localStorage` throws outright in a browser with site data blocked, and
 *  does not exist at all under vitest's node environment. A hub the owner has not pasted a token into
 *  reads as "no token", which is the read-only card — never a crash.
 *
 *  Deliberately NOT here: any path that goes looking for a token on disk. The milestone's standing
 *  prohibition is that no agent reads the hub's `tokens.json` to obtain an owner credential; this
 *  value arrives by the owner pasting it, or it does not arrive. */
const KEY = 'chopitup.ownerToken';

export function readOwnerToken(): string | null {
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
