import { isValidElement, type ReactElement, type ReactNode } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test } from 'vitest';
import ExchangeBar from './ExchangeBar';
import { setRoster } from './participants';
import type { ExchangeSnapshot, ExchangeView } from './types';

/** Row 22, AC4: one control per stop. While a run is `active` or `parked` the run strip owns the
 *  stop, so this bar must not offer a second one — and the moment that run is over, its button has to
 *  come back exactly as it was, because an exchange with a live spawn still needs stopping.
 *
 *  Rendered through `react-dom/server` like `RunBar.test.tsx`: presence and disabled state are all
 *  static markup, and this client has no jsdom to click in. */
setRoster([
  { id: 'sonnet', displayName: 'Sonnet', kind: 'model', host: 'claude', model: 'sonnet' },
  { id: 'codex', displayName: 'Codex', kind: 'model', host: 'codex', model: null },
]);

const BASE: ExchangeSnapshot = {
  roomId: 'lab',
  status: 'open',
  rootMessageId: 41,
  budget: 6,
  turnsUsed: 2,
  turnsCommitted: 3,
  remaining: 4,
  inFlight: ['sonnet'],
  pending: [],
  seq: 9,
  stoppedBy: null,
};

const NONE: ReadonlySet<number> = new Set();

const render = (
  exchange: ExchangeSnapshot | null,
  runStoppable = false,
  stopping = false,
  stoppingRoots: ReadonlySet<number> = NONE,
) =>
  renderToStaticMarkup(
    <ExchangeBar
      exchange={exchange}
      runStoppable={runStoppable}
      stopping={stopping}
      stoppingRoots={stoppingRoots}
      onStop={() => undefined}
    />,
  );

interface ButtonProps {
  children?: ReactNode;
  onClick?: () => void;
  disabled?: boolean;
}

/** Static markup carries no handlers, so the wiring assertions call the memoised component's own
 *  function and walk the element tree for every button, in document order — the `RunBar.test.tsx`
 *  trick, widened from one button to a list of them. */
function findButtons(node: ReactNode, into: ReactElement<ButtonProps>[] = []): ReactElement<ButtonProps>[] {
  if (Array.isArray(node)) {
    for (const child of node as ReactNode[]) findButtons(child, into);
    return into;
  }
  if (!isValidElement(node)) return into;
  if (node.type === 'button') {
    into.push(node as ReactElement<ButtonProps>);
    return into;
  }
  return findButtons((node.props as { children?: ReactNode }).children ?? null, into);
}

const buttons = (exchange: ExchangeSnapshot, onStop: (root: number | null) => void, stoppingRoots = NONE) =>
  findButtons(ExchangeBar.type({ exchange, runStoppable: false, stopping: false, stoppingRoots, onStop }));

/** Row 34's two-exchange room: #41 was superseded with Sonnet still talking, #57 is open with Codex
 *  queued and nothing of its own in flight. The room-wide `inFlight` is Sonnet's, so a strip that read
 *  the top-level list instead of its own would show the wrong chips and the wrong Stop. */
const OLDER: ExchangeView = {
  rootMessageId: 41,
  status: 'superseded',
  budget: 6,
  turnsUsed: 3,
  turnsCommitted: 3,
  remaining: 3,
  inFlight: ['sonnet'],
  pending: [],
  stoppedBy: null,
};

const NEWER: ExchangeView = {
  rootMessageId: 57,
  status: 'open',
  budget: 8,
  turnsUsed: 1,
  turnsCommitted: 1,
  remaining: 7,
  inFlight: [],
  pending: ['codex'],
  stoppedBy: null,
};

const TWO: ExchangeSnapshot = {
  ...BASE,
  rootMessageId: 57,
  budget: 8,
  turnsUsed: 1,
  turnsCommitted: 1,
  remaining: 7,
  inFlight: ['sonnet'],
  pending: ['codex'],
  exchanges: [OLDER, NEWER],
};

describe('ExchangeBar', () => {
  test('an open exchange with no run offers its own stop', () => {
    expect(render(BASE)).toContain('>Stop exchange</button>');
  });

  test('a live run takes the stop over, so the bar renders none', () => {
    expect(render(BASE, true)).not.toContain('Stop exchange');
  });

  /** The yield does not depend on the exchange being open: a run parks with its exchange already
   *  closed and its spawns already cancelled, and the bar must stay quiet there too. */
  test('a live run also takes it over from a closed exchange with a spawn still talking', () => {
    const closed: ExchangeSnapshot = { ...BASE, status: 'superseded', inFlight: ['sonnet'] };

    expect(render(closed)).toContain('>Stop exchange</button>');
    expect(render(closed, true)).not.toContain('Stop exchange');
  });

  /** AC4's second half, and the reason it is written down: the yield is for the life of the run, not
   *  for the life of the room. Once the run has `ended`, App's `runStoppable` is false again and this
   *  bar is exactly what it was before the row — including for a spawn that outlived its exchange. */
  test('an ended run gives the stop back, unchanged', () => {
    const afterRun: ExchangeSnapshot = { ...BASE, status: 'stopped', inFlight: ['sonnet'] };

    expect(render(afterRun, false)).toContain('>Stop exchange</button>');
  });

  test('with no run the old gate still decides: no spawns and a closed exchange means no button', () => {
    expect(render({ ...BASE, status: 'concluded', inFlight: [] })).not.toContain('<button');
  });

  test('a stop already in flight leaves the control disabled', () => {
    expect(render(BASE, false, true)).toContain('disabled=""');
  });

  /** Row 27, AC4. Every `RunPolicy` park is policy-driven and so is a ping-End, and all of them close
   *  the conductor's exchange — so before the cause rode the wire, a cap the owner never touched still
   *  told him he had stopped it. The marker has to attribute the stop to whoever actually made it. */
  test('a run-stopped exchange says the run did it, not the owner', () => {
    const markup = render({ ...BASE, status: 'stopped', stoppedBy: 'run', inFlight: [] });

    expect(markup).toContain('Stopped by the run');
    expect(markup).not.toContain('Stopped by you');
  });

  test('an owner-stopped exchange still reads exactly as it did', () => {
    expect(render({ ...BASE, status: 'stopped', stoppedBy: 'owner', inFlight: [] })).toContain(
      'Stopped by you',
    );
  });

  /** A snapshot from a hub older than row 27 — and any stop the hub could not attribute — sends no
   *  cause. Guessing "you" there is the very defect this row closes, so the null case is neutral. */
  test('a stop with no cause on the wire claims nothing about who caused it', () => {
    const markup = render({ ...BASE, status: 'stopped', stoppedBy: null, inFlight: [] });

    expect(markup).toContain('Exchange stopped ·');
    expect(markup).not.toContain('Stopped by you');
    expect(markup).not.toContain('Stopped by the run');
  });

  test('an idle room and a room with no exchange still render nothing at all', () => {
    expect(render({ ...BASE, status: 'idle' })).toBe('');
    expect(render(null)).toBe('');
  });
});

describe('ExchangeBar with one strip per exchange', () => {
  /** AC1: one strip per entry, oldest first, each drawn from its own fields. */
  test('two exchanges render two strips, oldest first', () => {
    const markup = render(TWO);

    const older = markup.indexOf('class="exchange exchange-superseded"');
    const newer = markup.indexOf('class="exchange exchange-open"');
    expect(older).toBeGreaterThanOrEqual(0);
    expect(newer).toBeGreaterThan(older);
    expect(markup.split('class="exchange ').length - 1).toBe(2);
  });

  test('each strip reads its own counters, chips and marker, not the room-wide top level', () => {
    const markup = render(TWO);
    const [olderStrip, newerStrip] = markup.split('class="exchange exchange-open"');

    expect(olderStrip).toContain('Superseded · 3 of 6 turns used');
    expect(olderStrip).toContain('Sonnet is replying');
    expect(newerStrip).toContain('7 of 8 turns left');
    expect(newerStrip).toContain('Codex is queued');
    // The open strip has nothing of its own in flight, whatever the room-wide list says.
    expect(newerStrip).not.toContain('Sonnet');
  });

  test("a stopped strip attributes its own stop through the same map", () => {
    const stopped: ExchangeSnapshot = {
      ...TWO,
      exchanges: [{ ...OLDER, status: 'stopped', inFlight: [], stoppedBy: 'run' }, NEWER],
    };

    expect(render(stopped)).toContain('Stopped by the run · 3 of 6 turns used');
  });

  /** AC2: with more than one strip the owner has to be able to tell them apart. */
  test('more than one strip names each by its root', () => {
    const markup = render(TWO);
    const [olderStrip, newerStrip] = markup.split('class="exchange exchange-open"');

    expect(olderStrip).toContain('#41');
    expect(newerStrip).toContain('#57');
    expect(olderStrip).not.toContain('#57');
  });

  test('a lone strip names no root, so a one-exchange room reads as it always did', () => {
    const markup = render({ ...TWO, exchanges: [NEWER] });

    expect(markup).not.toContain('#57');
    expect(markup).toContain('7 of 8 turns left');
  });

  /** AC4, first half: a live run owns the stop, for every strip. */
  test('a live run hides every strip\'s stop', () => {
    expect(render(TWO, true)).not.toContain('<button');
  });

  /** AC4, second half: the gate is per strip, on the strip's own `inFlight`. */
  test('a closed strip with nothing of its own in flight offers no stop, and its neighbours keep theirs', () => {
    const closed: ExchangeSnapshot = {
      ...TWO,
      exchanges: [{ ...OLDER, status: 'concluded', inFlight: [] }, NEWER],
    };
    const markup = render(closed);
    const [olderStrip, newerStrip] = markup.split('class="exchange exchange-open"');

    expect(olderStrip).not.toContain('<button');
    expect(newerStrip).toContain('>Stop exchange</button>');
  });

  test('a superseded strip with its own spawn still talking keeps its stop', () => {
    const [olderStrip] = render(TWO).split('class="exchange exchange-open"');

    expect(olderStrip).toContain('>Stop exchange</button>');
  });

  /** AC3, the component half: one strip's pending stop disables that strip and no other. */
  test('a stop in flight for one root disables only that strip', () => {
    const found = buttons(TWO, () => undefined, new Set([57]));

    expect(found).toHaveLength(2);
    expect(found[0]?.props.disabled).toBe(false);
    expect(found[1]?.props.disabled).toBe(true);
  });

  test('the room stop in flight does not disable the per-exchange stops', () => {
    expect(render(TWO, false, true)).not.toContain('disabled');
  });

  test('pressing one strip\'s stop hands the caller that strip\'s root and nothing else', () => {
    const roots: (number | null)[] = [];
    const found = buttons(TWO, (root) => roots.push(root));

    found[1]?.props.onClick?.();
    expect(roots).toEqual([57]);
    found[0]?.props.onClick?.();
    expect(roots).toEqual([57, 41]);
  });

  /** AC5: a hub older than row 32 sends no `exchanges`, and its only stop is the room's. */
  test('with no exchanges on the wire the one strip presses the room stop', () => {
    const roots: (number | null)[] = [];
    const found = buttons(BASE, (root) => roots.push(root));

    expect(found).toHaveLength(1);
    found[0]?.props.onClick?.();
    expect(roots).toEqual([null]);
    expect(render(BASE)).not.toContain('#41');
  });

  test('a new hub\'s idle room sends an empty list and still renders nothing', () => {
    expect(render({ ...BASE, status: 'idle', inFlight: [], exchanges: [] })).toBe('');
  });
});
