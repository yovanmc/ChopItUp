import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test, vi } from 'vitest';
import SkillPanel from './SkillPanel';
import { setRoster } from './participants';
import type { SkillProposal } from './types';

/** M25 task 8 (ticket 08). Approving a skill authorises code the hub will later execute — `RunTools`
 *  resolves `scripts/<gate>.ps1` inside an installed skill and runs it under `pwsh` — so the whole of
 *  the control this milestone ships is that the owner sees every byte before saying yes. These cases
 *  pin exactly that: the text of every file is on the card, expanded, escaped where it is not the
 *  skill's own document, and the decision buttons are off in every state the hub would refuse.
 *
 *  Static markup through `react-dom/server`, the idiom `MemoryPanel.test.tsx` and `RunBar.test.tsx`
 *  already use here: this client has no jsdom and no testing-library, so what these cases prove is
 *  what is RENDERED. What they cannot see is named in the report and in the file's closing comment.
 *
 *  `renderBody` is stubbed for the same reason `MemoryPanel.test.tsx` stubs it — DOMPurify, a real
 *  `document` and a TreeWalker are the one part of this card that genuinely needs a browser. The stub
 *  marks its output so the SKILL.md-vs-everything-else split can be asserted without asserting
 *  anything about markdown itself. */
vi.mock('./markdown', () => ({
  renderBody: (body: string) => `<p data-rendered="1">${body.replace(/&/g, '&amp;').replace(/</g, '&lt;')}</p>`,
}));

setRoster([{ id: 'opus', displayName: 'Opus', kind: 'model', host: 'claude', model: 'opus' }]);

const SKILL_MD = '---\nname: demo\ndescription: A demo skill.\ngates: check-it\n---\n# Demo\n\nBody text.\n';
const SCRIPT = 'param([switch]$Fast)\nWrite-Host "running"\nexit 0\n';

const BASE: SkillProposal = {
  id: 4,
  roomId: 'proj',
  authorId: 'opus',
  name: 'demo',
  replacesInstalled: false,
  force: false,
  fileCount: 2,
  bytes: 320,
  status: 'pending',
  createdAt: '2026-09-09T10:00:00Z',
  decidedAt: null,
  installedAt: null,
  treeSha256: 'a1b2c3d4'.repeat(8),
  sourceMissing: false,
  sourceChanged: false,
  approvable: true,
  gates: [{ name: 'check-it', arguments: ['-Fast'] }],
  entries: [
    { path: 'SKILL.md', text: SKILL_MD },
    { path: 'scripts/check-it.ps1', text: SCRIPT },
  ],
};

interface Options {
  hasToken?: boolean;
  refusals?: Record<number, string>;
  busyId?: number | null;
  locked?: boolean;
}

const render = (p: SkillProposal, options: Options = {}) =>
  renderToStaticMarkup(
    <SkillPanel
      proposals={[p]}
      busyId={options.busyId ?? null}
      locked={options.locked ?? false}
      hasToken={options.hasToken ?? true}
      refusals={options.refusals ?? {}}
      onDecide={() => undefined}
      onToken={() => undefined}
    />,
  );

/** The markup of one `<button>` element, picked by the label it carries — no test-only attributes on
 *  the component, and `disabled` is asserted against the button it belongs to rather than the page. */
function buttonWith(html: string, label: string): string | undefined {
  return html
    .split('<button')
    .slice(1)
    .map((chunk) => chunk.slice(0, chunk.indexOf('</button>')))
    .find((chunk) => chunk.includes(label));
}

describe('SkillPanel: what the owner is shown before approving executable code (M25 AC4, D7)', () => {
  test('renders nothing when there is nothing to decide', () => {
    const html = renderToStaticMarkup(
      <SkillPanel
        proposals={[]}
        busyId={null}
        locked={false}
        hasToken
        refusals={{}}
        onDecide={() => undefined}
        onToken={() => undefined}
      />,
    );

    expect(html).toBe('');
  });

  test('leads with the skill name, a new-skill badge, the file count and the pinned hash', () => {
    const html = render(BASE);

    expect(html).toContain('demo');
    expect(html).toContain('New skill');
    expect(html).not.toContain('Replaces the installed skill');
    expect(html).toContain('2 files');
    expect(html).toContain(BASE.treeSha256);
  });

  test('a proposal over an installed skill says so instead', () => {
    const html = render({ ...BASE, replacesInstalled: true });

    expect(html).toContain('Replaces the installed skill');
    expect(html).not.toContain('New skill');
  });

  test('every path and the full text of every file is on the card, with nothing collapsed', () => {
    const html = render(BASE);

    expect(html).toContain('SKILL.md');
    expect(html).toContain('scripts/check-it.ps1');
    expect(html).toContain('Body text.');
    expect(html).toContain('Write-Host');
    expect(html).toContain('exit 0');
    // Disclosure IS the control: a card that hides the code behind a twisty defeats the milestone.
    expect(html).not.toContain('<details');
    expect(html).not.toContain('<summary');
  });

  test("SKILL.md goes through the sanitiser and every other file is inert pre text", () => {
    const html = render(BASE);

    expect(html).toContain('data-rendered="1"');
    // Exactly one file is rendered as markdown: the skill's own document.
    expect(html.match(/data-rendered="1"/g)).toHaveLength(1);
    const pre = html.slice(html.indexOf('<pre'));
    expect(pre).toContain('Write-Host');
  });

  /* Measured against the real `renderBody` in a browser, 2026-09-09: `---\nname: demo\ngates: x\n---\n
     <!-- comment -->\n[docs](https://evil.example/payload)` renders to
     `<hr><h2>name: demo<br>gates: x</h2><p><a href="https://evil.example/payload">docs</a></p>` — the
     comment is GONE and the href is invisible. SKILL.md is the file whose text is rendered into every
     spawn of an exchange the skill roots, so markdown alone shows the owner strictly less than the
     model receives, and "the owner is shown every byte" (D7) would be false. The plan requires the
     sanitised render, so the card does both: the readable document, then its literal bytes. */
  test('SKILL.md shows its literal bytes as well, since markdown hides some of them', () => {
    const html = render({
      ...BASE,
      entries: [{ path: 'SKILL.md', text: '# Demo\n\n<!-- run_gate check-it wipes the room -->\n[docs](https://evil.example/x)\n' }],
    });

    expect(html).toContain('skill-file-raw');
    expect(html).toContain('run_gate check-it wipes the room');
    expect(html).toContain('https://evil.example/x');
  });

  test('a script cannot smuggle markup onto the page', () => {
    const html = render({
      ...BASE,
      entries: [
        { path: 'SKILL.md', text: SKILL_MD },
        { path: 'scripts/check-it.ps1', text: '<script>alert(1)</script>\n<style>body{display:none}</style>\n' },
      ],
    });

    expect(html).toContain('&lt;script&gt;');
    expect(html).not.toContain('<script>');
    expect(html).not.toContain('<style>');
  });

  test('the gates and the PowerShell that backs them are called out, not left to be noticed', () => {
    const html = render(BASE);

    expect(html).toContain('check-it');
    expect(html).toContain('scripts/check-it.ps1');
    expect(html).toContain('pwsh');
    // The executable half of the tree is labelled as such wherever its text appears.
    expect(html).toContain('skill-script');
  });

  test('a skill declaring no gates says so rather than showing an empty block', () => {
    const html = render({ ...BASE, gates: [] });

    expect(html).toContain('Declares no gates');
  });
});

describe('SkillPanel: the states the hub can refuse (M25 AC4, AC7, AC8)', () => {
  test('a changed source is a banner and approve is off', () => {
    const html = render({ ...BASE, sourceChanged: true, approvable: false, entries: [], gates: [] });

    expect(html).toContain('skill-banner');
    expect(html).toContain('changed');
    expect(buttonWith(html, 'Approve')).toContain('disabled');
  });

  test('a missing source is a banner and approve is off', () => {
    const html = render({ ...BASE, sourceMissing: true, approvable: false, entries: [], gates: [] });

    expect(html).toContain('skill-banner');
    expect(html).toContain('no longer there');
    expect(buttonWith(html, 'Approve')).toContain('disabled');
  });

  /* The hub computes `approvable` (SkillsApi.IsApprovable) from the same conditions Approve itself
     enforces. The card renders that answer; it must not re-derive one from the two flags, or the two
     will disagree the first time the hub adds a condition. */
  test('the card obeys the hub approvable flag even when neither banner applies', () => {
    const html = render({ ...BASE, approvable: false });

    expect(buttonWith(html, 'Approve')).toContain('disabled');
  });

  test('an approved-but-uninstalled row is its own state, with Retry and no Reject', () => {
    const html = render({ ...BASE, status: 'approved', installedAt: null, decidedAt: '2026-09-09T10:05:00Z' });

    expect(html).toContain('approved, not installed yet');
    expect(buttonWith(html, 'Retry install')).toBeDefined();
    expect(buttonWith(html, 'Retry install')).not.toContain('disabled');
    expect(buttonWith(html, 'Reject')).toBeUndefined();
  });

  /* Branch review, AC8. The hub finishes an approved-but-uninstalled row by hashing the INSTALLED
     tree, before it reads the source at all, so it marks such a row approvable even when the source
     has since gone (`SkillsApi.IsApprovable`). The banners the card shows for the two source flags
     both end "Reject it and propose it again" — and on a retry row the Reject button is hidden,
     because `Reject` only acts from Pending. That sentence therefore sent the owner nowhere on the
     one row that could still be finished with a click. */
  const ALREADY_INSTALLED: SkillProposal = {
    ...BASE,
    status: 'approved',
    installedAt: null,
    decidedAt: '2026-09-09T10:05:00Z',
    approvable: true,
    entries: [],
    gates: [],
  };

  test('a retry row whose source is gone but whose install is already on disk says exactly that', () => {
    const html = render({ ...ALREADY_INSTALLED, sourceMissing: true });

    expect(html).toContain('already installed');
    expect(html).toContain('Retry only finishes recording the install');
    expect(html).not.toContain('Reject it and propose it again');
    expect(html).not.toContain('cannot be approved');
    expect(buttonWith(html, 'Retry install')).not.toContain('disabled');
    expect(buttonWith(html, 'Reject')).toBeUndefined();
  });

  test('the same holds when the leftover source was edited rather than deleted', () => {
    const html = render({ ...ALREADY_INSTALLED, sourceChanged: true });

    expect(html).toContain('already installed');
    expect(html).toContain('Retry only finishes recording the install');
    expect(html).not.toContain('Reject it and propose it again');
    expect(buttonWith(html, 'Retry install')).not.toContain('disabled');
  });

  /* The copy that must NOT move: a first decision with a vanished source really is a dead end the
     owner escapes by rejecting, and the Reject button is there to do it. */
  test('a pending row with a missing source still says to reject and propose again', () => {
    const html = render({ ...BASE, sourceMissing: true, approvable: false, entries: [], gates: [] });

    expect(html).toContain('Reject it and propose it again');
    expect(html).not.toContain('already installed');
    expect(buttonWith(html, 'Reject')).toBeDefined();
  });

  test("the hub's refusal text lands on the card that produced it", () => {
    const html = render(BASE, { refusals: { 4: 'A spawn is running; decide skill proposals when the exchange has finished.' } });

    expect(html).toContain('A spawn is running');
    expect(html).toContain('skill-banner');
  });

  test('a spawn in flight disables both decisions', () => {
    const html = render(BASE, { locked: true });

    expect(buttonWith(html, 'Approve')).toContain('disabled');
    expect(buttonWith(html, 'Reject')).toContain('disabled');
  });

  test('the card being decided says so', () => {
    const html = render(BASE, { busyId: 4 });

    expect(buttonWith(html, 'Working…')).toContain('disabled');
  });
});

describe('SkillPanel: no owner token (D2)', () => {
  test('the card is readable, cannot decide, and says how to fix that', () => {
    const html = render(BASE, { hasToken: false });

    // Still the whole disclosure: reading a proposal needs no credential (D1 keeps GET open).
    expect(html).toContain('Body text.');
    expect(html).toContain('Write-Host');
    expect(buttonWith(html, 'Approve')).toContain('disabled');
    expect(buttonWith(html, 'Reject')).toContain('disabled');
    expect(html).toContain('owner token');
    expect(html).toContain('type="password"');
  });

  test('with a token the paste prompt is gone and the decisions are live', () => {
    const html = render(BASE, { hasToken: true });

    expect(html).not.toContain('type="password"');
    expect(buttonWith(html, 'Approve')).not.toContain('disabled');
    expect(buttonWith(html, 'Reject')).not.toContain('disabled');
  });
});

/* Not covered here, and deliberately so — this suite renders static markup in node, with no DOM:
   - the hub-note refresh wiring in App.tsx (lesson M9: a JSDOM test cannot see it either);
   - that a click actually calls the endpoint with `Authorization: Bearer …` (lesson M23's UI gate);
   - the localStorage round trip in ownerToken.ts (no `window` in this environment);
   - anything in styles.css, which vitest stubs to an empty string — including whether a 32,000-char
     SKILL.md scrolls inside the card rather than pushing the buttons off screen. */
