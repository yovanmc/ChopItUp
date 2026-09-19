import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test, vi } from 'vitest';
import Composer from './Composer';
import { setRoster } from './participants';
import { nextReply } from './reply';
import Thread, { replySnippet } from './Thread';
import type { Message } from './types';

vi.mock('./markdown', () => ({ renderBody: (body: string) => `<p>${body}</p>` }));

// isSystem() reads the roster: an id it has never heard of is deliberately not a hub note.
setRoster([
  { id: 'owner', displayName: 'Owner', kind: 'human', host: 'human', model: null },
  { id: 'opus', displayName: 'Opus', kind: 'model', host: 'claude', model: 'opus' },
  { id: 'hub', displayName: 'Hub', kind: 'system', host: 'hub', model: null },
]);

const at = '2026-09-15T12:00:00.000Z';
const root: Message = { id: 1, roomId: 'general', authorId: 'opus', body: 'here is the\n\nplan', createdAt: at };
const reply: Message = { id: 2, roomId: 'general', authorId: 'owner', body: '@opus go on', createdAt: at, replyToId: 1 };
const note: Message = { id: 3, roomId: 'general', authorId: 'hub', body: 'Exchange concluded: 1 of 4 turns used.', createdAt: at };

describe('reply-to', () => {
  test('every non-hub message offers Reply and a hub note does not', () => {
    const markup = renderToStaticMarkup(<Thread messages={[root, reply, note]} loading={false} onReply={() => undefined} />);
    expect(markup.match(/class="reply-action"/g)?.length).toBe(2);
  });

  test('a reply shows a quote line naming the original author with a snippet', () => {
    const markup = renderToStaticMarkup(<Thread messages={[root, reply]} loading={false} onReply={() => undefined} />);
    expect(markup).toContain('reply-quote');
    expect(markup).toContain('here is the plan');
    expect(markup).toContain('id="msg-1"');
  });

  test('a reply whose original is not loaded names its id', () => {
    const markup = renderToStaticMarkup(<Thread messages={[reply]} loading={false} onReply={() => undefined} />);
    expect(markup).toContain('#1');
  });

  test('the composer shows a cancellable chip while replying and none otherwise', () => {
    const replying = renderToStaticMarkup(
      <Composer roomName="General" disabled={false} onSend={async () => undefined} replyTo={root} onCancelReply={() => undefined} />,
    );
    expect(replying).toContain('reply-chip');
    expect(replying).toContain('aria-label="Cancel reply"');
    const idle = renderToStaticMarkup(
      <Composer roomName="General" disabled={false} onSend={async () => undefined} replyTo={null} onCancelReply={() => undefined} />,
    );
    expect(idle).not.toContain('reply-chip');
  });

  test('a snippet collapses whitespace and cuts long bodies', () => {
    expect(replySnippet('a\n\n  b')).toBe('a b');
    expect(replySnippet('x'.repeat(100), 80)).toBe('x'.repeat(80) + '…');
  });

  test('Reply names the author it replies to', () => {
    const markup = renderToStaticMarkup(<Thread messages={[root]} loading={false} onReply={() => undefined} />);
    expect(markup).toContain('aria-label="Reply to Opus"');
  });
});

describe('nextReply', () => {
  test('Reply sets the target for its message\'s room', () =>
    expect(nextReply({}, { kind: 'reply', message: root })).toEqual({ general: root }));
  test('a successful send clears only that room', () =>
    expect(nextReply({ general: root, other: root }, { kind: 'sent', roomId: 'general' })).toEqual({
      general: null,
      other: root,
    }));
  test('a failed send returns the state unchanged', () => {
    const state = { general: root };
    expect(nextReply(state, { kind: 'failed', roomId: 'general' })).toBe(state);
  });
  test('Cancel clears only that room', () =>
    expect(nextReply({ general: root, other: root }, { kind: 'cancel', roomId: 'general' })).toEqual({
      general: null,
      other: root,
    }));
  test('a switch keeps the other room\'s target', () => {
    const state = { general: root };
    expect(nextReply(state, { kind: 'cancel', roomId: 'other' })).toEqual({ general: root, other: null });
  });
});
