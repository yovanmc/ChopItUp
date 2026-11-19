import { describe, expect, test } from 'vitest';
import { escapeHtml, fileName, refFromHref, routeFor } from './images';

const route = (ref: string) => `/api/rooms/lab%20one/files?ref=${encodeURIComponent(ref)}`;

const notRoomFiles = [
  'https://example.com/a.png',
  'http://127.0.0.1:9/a.png',
  '//example.com/a.png',
  'data:image/png;base64,AAAA',
  'javascript:alert(1)',
  'JavaScript:alert(1)',
  'file:///C:/a.png',
  '/rooted/a.png',
  '\\rooted\\a.png',
  'C:\\pics\\a.png',
  'c:/pics/a.png',
  '',
  '   ',
];

describe('refFromHref', () => {
  test.each([
    ['artifact:renders/a.png', 'artifact:renders/a.png'],
    ['ARTIFACT:a.png', 'ARTIFACT:a.png'],
    ['docs/a.png', 'docs/a.png'],
    ['./docs/a.png', './docs/a.png'],
    ['my%20file.png', 'my file.png'],
    ['artifact:my%20file.png', 'artifact:my file.png'],
    ['my file.png', 'my file.png'],               // marked's angle-bracket form hands the space through
    ['artifact:progress 50%.png', 'artifact:progress 50%.png'],   // not valid percent encoding: taken as written
  ])('%s names %s', (href, ref) => {
    expect(refFromHref(href)).toBe(ref);
  });

  test.each(notRoomFiles)('%s names no room file', (href) => {
    expect(refFromHref(href)).toBeNull();
  });
});

describe('routeFor', () => {
  test.each([
    'artifact:renders/a.png',
    'docs/a.png',
    'my file.png',
    'a&b"c.png',
    'artifact:progress 50%.png',                  // a listed name with a percent sign is not decoded again
    'artifact:literal%20name.png',
  ])('%s is routed as it is', (ref) => {
    expect(routeFor(ref, 'lab one')).toBe(route(ref));
  });

  test.each(notRoomFiles)('%s is not routed', (ref) => {
    expect(routeFor(ref, 'lab')).toBeNull();
  });

  test('a room with no id loads nothing', () => {
    expect(routeFor('a.png', '')).toBeNull();
  });
});

describe('labels', () => {
  test('fileName is the last segment', () => {
    expect(fileName('artifact:renders/shot 1.png')).toBe('shot 1.png');
    expect(fileName('a.png')).toBe('a.png');
  });

  test('escapeHtml escapes what can end an attribute or open a tag', () => {
    expect(escapeHtml('a"><img src=x>&')).toBe('a&quot;&gt;&lt;img src=x&gt;&amp;');
  });
});
