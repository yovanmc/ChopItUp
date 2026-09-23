import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test, vi } from 'vitest';
import MemoryPanel from './MemoryPanel';
import { setRoster } from './participants';
import type { MemoryProposal } from './types';

/** The card is what the hub owner reads when deciding whether a model's proposal enters shared memory,
 *  so the three things the hub computes (what it replaces, why it might not be a fact, and what is
 *  already in the topic) have to be on the card, and a plain proposal has to show none of them.
 *  Static markup through `react-dom/server` like `RunBar.test.tsx`: the card's behaviour (the two
 *  buttons) is covered by the UIA gate, and what these cases prove is what is rendered.
 *
 *  `renderBody` is stubbed because it is the one part of the card that genuinely needs a browser
 *  (DOMPurify, `document.createElement` and a TreeWalker), and a jsdom would only add a dependency
 *  to prove markdown this file is not about. The body still reaches the card; only its markdown pass
 *  is replaced. Nothing asserted below comes from it. */
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
    // idiom as the topic chip above them.
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

/** A consolidation's body is the WHOLE topic file, so a card that showed only the body would show
 *  the hub owner nothing about what is changing. These cases pin the four things that decide the answer
 *  (the comparison itself, the entries it removes, the entries losing their approval record, and,
 *  while the proposal is still rejectable, the absence of a commit trail) plus the two ways this card
 *  can go wrong: markup smuggled in through a spawn-authored diff line, and a rewrite that arrives
 *  without a diff rendering as an empty box. */
const REWRITE: MemoryProposal = {
  ...BASE,
  id: 9,
  kind: 'rewrite',
  title: 'Consolidate user',
  body: '# user\n\n## Editor\n\nVS Code.\n',
  diff: [
    { op: 'same', text: '# user' },
    { op: 'skip', text: '… 12 unchanged lines …' },
    { op: 'del', text: '## Editor of choice' },
    { op: 'add', text: '## Editor' },
  ],
  removedTitles: ['Editor of choice', 'Shell'],
  addedTitles: ['Editor'],
  provenanceLost: 2,
  gitAvailable: true,
};

describe('MemoryPanel, consolidation card (row 23, AC7)', () => {
  test('a rewrite card renders the diff with per-op classes and not the plain body', () => {
    const html = render(REWRITE);

    expect(html).toContain('memory-diff');
    expect(html).toContain('memory-diff-same');
    expect(html).toContain('memory-diff-add');
    expect(html).toContain('memory-diff-del');
    expect(html).toContain('memory-diff-skip');
    expect(html).toContain('… 12 unchanged lines …');
    expect(html).not.toContain('memory-body');
  });

  test('a diff line is a text node, never markup', () => {
    const html = render({
      ...REWRITE,
      diff: [{ op: 'add', text: '## <script>alert(1)</script>' }],
    });

    expect(html).toContain('&lt;script&gt;');
    expect(html).not.toContain('<script>');
  });

  test('the card names the entries it removes and how many lose their approval record', () => {
    const html = render(REWRITE);

    expect(html).toContain('Removes 2 entries');
    expect(html).toContain('Editor of choice');
    expect(html).toContain('Shell');
    // `ProvenanceLost` counts every live entry whose provenance would not carry forward, which is the
    // renamed headings AND the dropped ones (MemoryStore.ProvenanceLost: live, has provenance, title
    // absent from the proposed body). Calling them "surviving" asserted something untrue of the
    // dropped half and read as a second count of the line above it, so the copy names the records
    // rather than the entries, and says what is actually lost.
    expect(html).toContain('2 approval records will not carry forward: who approved those entries, and when.');
    expect(html).not.toContain('surviving');
  });

  test('a pending rewrite with no git trail says so and names the backup that will be the only copy', () => {
    const html = render({ ...REWRITE, gitAvailable: false });

    expect(html).toContain('memory-diff-nogit');
    expect(html).toContain('topics/user.md.rewrite-9.bak');
  });

  /* The Retry card is the one this warning matters most on: it is still approvable (Retry is what
     performs the write) and nothing has been written yet (`writtenTo` is null), so the file on disk
     is still the pre-consolidation copy and the backup is still the only thing that would survive.
     The hub computes `gitAvailable` for exactly the pending and approved-but-unwritten pair
     (`MemoryApi.MapForList`'s `inScope`). */
  test('an approved-but-unwritten rewrite with no git trail warns too, and names the same backup', () => {
    const html = render({ ...REWRITE, status: 'approved', writtenTo: null, gitAvailable: false });

    expect(html).toContain('approved, not written yet');
    expect(html).toContain('memory-diff-nogit');
    expect(html).toContain('topics/user.md.rewrite-9.bak');
  });

  test('the no-git warning is absent when a commit can be made, pending or awaiting a retry', () => {
    expect(render(REWRITE)).not.toContain('memory-diff-nogit');
    expect(render({ ...REWRITE, status: 'approved', writtenTo: null })).not.toContain('memory-diff-nogit');
  });

  test('a rewrite with a null diff falls back to the body rather than an empty box', () => {
    const html = render({ ...REWRITE, diff: null });

    expect(html).not.toContain('memory-diff');
    expect(html).toContain('memory-body');
    expect(html).toContain('VS Code.');
  });

  /* An import's `source` is the vendor it read the text out of, and the card says so in words. An
     editor save carries `source: 'editor'`, which is not an import and has no vendor path, so
     "imported from editor" would name the wrong door. Which button performs the write depends on the
     card's state, and naming the wrong one sends the owner looking for a button that is not on the
     card. */
  test('an editor row is edited by hand, and names the button its own card state offers', () => {
    const unwritten = render({ ...REWRITE, status: 'approved', writtenTo: null, source: 'editor' });
    const pending = render({ ...REWRITE, source: 'editor' });

    expect(unwritten).toContain('edited by hand; Retry writes it');
    expect(pending).toContain('edited by hand; Approve writes it');
    expect(unwritten).not.toContain('imported from editor');
    expect(render({ ...REWRITE, source: 'claude:MEMORY.md' })).toContain('imported from claude:MEMORY.md');
  });

  /* A 24 KB diff must scroll inside the card rather than push Reject and Approve off screen: that is
     `max-height` + `overflow-y` on `.memory-diff` in styles.css. It is not asserted here: vitest
     stubs every CSS import to an empty string, and reading the file instead would need @types/node.
     The interactive check against the live UI covers it. */
});
