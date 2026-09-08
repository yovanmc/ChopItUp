import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test, vi } from 'vitest';
import MemoryPanel from './MemoryPanel';
import { setRoster } from './participants';
import type { MemoryProposal } from './types';

/** Row 18, task 7 (AC5, critique P1-7). The card is what the owner reads when deciding whether a
 *  model's proposal enters shared memory, so the three things the hub now computes — what it
 *  replaces, why it might not be a fact, and what is already in the topic — have to be on the card,
 *  and a plain proposal has to look exactly as it did before. Static markup through
 *  `react-dom/server` like `RunBar.test.tsx`: the card's behaviour (the two buttons) is covered by
 *  the UIA gate, and what these cases prove is what is rendered.
 *
 *  `renderBody` is stubbed because it is the one part of the card that genuinely needs a browser —
 *  DOMPurify, `document.createElement` and a TreeWalker — and RunBar's note holds here too: a jsdom
 *  would only add a dependency to prove markdown this file is not about. The body still reaches the
 *  card; only its markdown pass is replaced. Nothing asserted below comes from it. */
vi.mock('./markdown', () => ({
  renderBody: (body: string) => `<p>${body.replace(/&/g, '&amp;').replace(/</g, '&lt;')}</p>`,
}));

setRoster([{ id: 'opus', displayName: 'Opus', kind: 'model', host: 'claude', model: 'opus' }]);

const BASE: MemoryProposal = {
  id: 3,
  roomId: 'general',
  authorId: 'opus',
  topic: 'user',
  title: 'Editor, new choice',
  body: 'VS Code.',
  status: 'pending',
  source: null,
  createdAt: '2026-09-08T00:00:00Z',
  decidedAt: null,
  writtenTo: null,
  commitHash: null,
  kind: 'append',
  replaces: null,
  flags: [],
  related: [],
};

const render = (p: MemoryProposal) =>
  renderToStaticMarkup(<MemoryPanel proposals={[p]} busyId={null} locked={false} onDecide={() => undefined} />);

describe('MemoryPanel (row 18, AC5)', () => {
  test('a plain proposal renders no replaces line, no flags and no related list', () => {
    const html = render(BASE);

    expect(html).not.toContain('memory-replaces');
    expect(html).not.toContain('memory-flags');
    expect(html).not.toContain('memory-related');
    expect(html).not.toContain('Hub checks');
  });

  test('a supersede with three flags and two related entries renders all three', () => {
    const html = render({
      ...BASE,
      kind: 'supersede',
      replaces: 'Shell',
      flags: ['instruction-like', 'fence', 'from-directory'],
      related: [
        { title: 'Shell', snippet: 'pwsh.', replaced: true },
        { title: 'Editor of choice', snippet: 'Vim.', replaced: false },
      ],
    });

    expect(html).toContain('Replaces <q>Shell</q> in user');
    // The hints are the hub's own checks, not tags the proposing model attached, and a sighted owner
    // has to be told that in words — an aria-label alone leaves two unlabelled pills in the same
    // idiom as the topic chip above them (screenshot judge, finding 3).
    expect(html).toContain('Hub checks');
    expect(html).toContain('reads like an instruction, not a fact');
    expect(html).toContain('contains a memory fence line');
    expect(html).toContain('proposed from a room with files and network');
    expect(html).toContain('Closest entries already in user');
    expect(html).toContain('retired on approval');
    expect(html).toContain('Editor of choice');
    expect(html).toContain('Vim.');
  });
});
