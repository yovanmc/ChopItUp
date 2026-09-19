import { expect, test } from 'vitest';
import { governingCommand, messageBody } from './governing';

test('posting preserves command examples while ordinary drafts keep their trimming behavior', () => {
  for (const text of ['    /objective example', '\n/correction example', '\t/objective example', '> /objective example', '```\n/objective example\n```']) {
    expect(governingCommand(messageBody(text))).toBeNull();
  }
  expect(messageBody('  @opus hi  ')).toBe('@opus hi');
  expect(governingCommand(messageBody('/objective First\r\nSecond  '))).toEqual({ slot: 'objective', text: 'First\r\nSecond' });
  expect(governingCommand('/objective')).toEqual({ slot: 'objective', text: '' });
  for (const text of ['/objectives foo', '/Objective foo', '/objective\u00a0foo']) expect(governingCommand(text)).toBeNull();
});
