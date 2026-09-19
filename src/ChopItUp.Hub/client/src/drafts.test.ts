import { describe, expect, test } from 'vitest';
import { draftFor, nextDrafts } from './drafts';

describe('nextDrafts', () => {
  test('edit sets that room\'s draft', () => {
    expect(nextDrafts({}, { kind: 'edit', roomId: 'a', text: 'hi' })).toEqual({ a: 'hi' });
  });

  test('sent clears only that room', () => {
    const state = nextDrafts({ a: 'hi', b: 'yo' }, { kind: 'edit', roomId: 'a', text: 'hi there' });
    expect(nextDrafts(state, { kind: 'sent', roomId: 'a' })).toEqual({ a: '', b: 'yo' });
  });
});

describe('draftFor', () => {
  test('an unknown room reads empty', () => {
    expect(draftFor({}, 'nope')).toBe('');
  });

  test('a known room reads its own text', () => {
    expect(draftFor({ a: 'hi' }, 'a')).toBe('hi');
  });
});
