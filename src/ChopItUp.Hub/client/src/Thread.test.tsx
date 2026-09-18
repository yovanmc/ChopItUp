import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test, vi } from 'vitest';
import ImportDialog from './ImportDialog';
import { setRoster } from './participants';
import Thread from './Thread';
import type { Message } from './types';

vi.mock('./markdown', () => ({ renderBody: (body: string) => `<p>${body}</p>` }));

setRoster([
  { id: 'owner', displayName: 'Owner', kind: 'human', host: 'human', model: null },
  { id: 'opus', displayName: 'Opus', kind: 'model', host: 'claude', model: 'opus' },
]);

const at = '2026-09-17T12:00:00.000Z';
const live: Message = { id: 1, roomId: 'general', authorId: 'owner', body: '@opus hi', createdAt: at };
const imported: Message = { id: 2, roomId: 'general', authorId: 'owner', body: 'Claude: from a paste', createdAt: at, imported: true };

describe('imported rows', () => {
  test('an imported message shows the tag and a live one does not', () => {
    const markup = renderToStaticMarkup(<Thread messages={[live, imported]} loading={false} />);
    expect(markup.match(/class="imported-tag"/g)?.length).toBe(1);
    const liveRow = markup.slice(markup.indexOf('id="msg-1"'), markup.indexOf('id="msg-2"'));
    expect(liveRow).not.toContain('imported-tag');
  });

  test('the import dialog says imported text is never acted on', () => {
    const markup = renderToStaticMarkup(
      <ImportDialog roomId="general" roomName="General" onClose={() => undefined} onImported={() => undefined} />,
    );
    expect(markup).toContain('the hub never acts on');
  });
});
