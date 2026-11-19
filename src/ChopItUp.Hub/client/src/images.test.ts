import { describe, expect, test } from 'vitest';
import { escapeHtml, fileName, routeFor } from './images';

const route = (ref: string) => `/api/rooms/lab%20one/files?ref=${encodeURIComponent(ref)}`;

describe('routeFor', () => {
  test.each([
    ['artifact:renders/a.png', 'artifact:renders/a.png'],
    ['ARTIFACT:a.png', 'ARTIFACT:a.png'],
    ['docs/a.png', 'docs/a.png'],
    ['./docs/a.png', './docs/a.png'],
    ['my%20file.png', 'my file.png'],
    ['artifact:my%20file.png', 'artifact:my file.png'],
    ['my file.png', 'my file.png'],               // marked's angle-bracket form hands the space through
    ['a&b"c.png', 'a&b"c.png'],
  ])('%s is a room file', (href, ref) => {
    expect(routeFor(href, 'lab one')).toBe(route(ref));
  });

  test.each([
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
    '%E0%A4%A',                                   // not decodable
  ])('%s is not', (href) => {
    expect(routeFor(href, 'lab')).toBeNull();
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
