import { afterEach, describe, expect, test, vi } from 'vitest';
import { readOwnerToken } from './ownerToken';

/** The desktop shell mints an owner bearer per launch and puts it on the page as a global on every
 *  document of the hub origin: in memory, never in storage, so nothing survives Quit and no other
 *  process on this machine can read a copy out of the WebView2 profile. This file is about the
 *  precedence that makes that work: the injected value wins, and a browser tab still has only the
 *  pasted token.
 *
 *  No jsdom here (`api.test.ts` says why), so `window` is a stub and "no stub at all" is the plain
 *  browser with storage unavailable. */

afterEach(() => vi.unstubAllGlobals());

/** A browser that refuses site data: every access throws, which is exactly what the injected path
 *  must not depend on. */
const blockedStorage = {
  getItem: () => {
    throw new Error('site data is blocked');
  },
  setItem: () => {
    throw new Error('site data is blocked');
  },
  removeItem: () => {
    throw new Error('site data is blocked');
  },
};

describe('readOwnerToken', () => {
  test("the shell's per-launch token wins, and storage is never consulted for it", () => {
    vi.stubGlobal('window', { __chopitupShellToken: 'launch', localStorage: blockedStorage });

    expect(readOwnerToken()).toBe('launch');
  });

  test('a plain browser still reads the pasted token out of storage', () => {
    vi.stubGlobal('window', { localStorage: { getItem: () => 'pasted', setItem: () => undefined, removeItem: () => undefined } });

    expect(readOwnerToken()).toBe('pasted');
  });

  test('an empty injected value is not a token, so the pasted one is still used', () => {
    vi.stubGlobal('window', { __chopitupShellToken: '', localStorage: { getItem: () => 'pasted', setItem: () => undefined, removeItem: () => undefined } });

    expect(readOwnerToken()).toBe('pasted');
  });

  test('with no window and nothing injected there is no token, and no throw', () => {
    expect(readOwnerToken()).toBeNull();
  });
});
