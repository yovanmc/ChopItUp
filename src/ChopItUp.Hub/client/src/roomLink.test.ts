import { describe, expect, test } from 'vitest';
import { keepOrFirst, roomFromSearch } from './roomLink';

describe('roomFromSearch', () => {
  test('reads the room a notification link names, decoded', () => {
    expect(roomFromSearch('?room=lab')).toBe('lab');
    expect(roomFromSearch('?room=goal%20runs')).toBe('goal runs');
  });

  test('names no room when the link has none', () => {
    expect(roomFromSearch('')).toBeNull();
    expect(roomFromSearch('?room=')).toBeNull();
  });
});

describe('keepOrFirst', () => {
  const loaded = [{ id: 'general' }, { id: 'lab' }];

  test('a linked room that is listed is the one shown', () => {
    expect(keepOrFirst('lab', loaded)).toBe('lab');
  });

  test('a linked room that is not listed falls back to the first room', () => {
    expect(keepOrFirst('gone', loaded)).toBe('general');
    expect(keepOrFirst(null, loaded)).toBe('general');
  });

  test('no rooms, no room', () => {
    expect(keepOrFirst('lab', [])).toBeNull();
  });
});
