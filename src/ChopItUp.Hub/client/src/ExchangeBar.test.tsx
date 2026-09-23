import { isValidElement, type ReactElement, type ReactNode } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test, vi } from 'vitest';
import ExchangeBar, { workingElapsed } from './ExchangeBar';
import { setRoster } from './participants';
import type { ExchangeSnapshot, ExchangeView } from './types';

/** One control per stop. While a run is `active` or `parked` the run strip owns the stop, so this bar
 *  must not offer a second one, and the moment that run is over, its button has to come back exactly
 *  as it was, because an exchange with a live spawn still needs stopping.
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
  continuingRoots: ReadonlySet<number> = NONE,
) =>
  renderToStaticMarkup(
    <ExchangeBar
      exchange={exchange}
      runStoppable={runStoppable}
      stopping={stopping}
      stoppingRoots={stoppingRoots}
      continuingRoots={continuingRoots}
      onStop={() => undefined}
      onContinue={() => undefined}
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

const buttons = (
  exchange: ExchangeSnapshot,
  onStop: (root: number | null) => void,
  stoppingRoots = NONE,
  onContinue: (root: number) => void = () => undefined,
  continuingRoots = NONE,
) =>
  findButtons(
    ExchangeBar.type({
      exchange,
      runStoppable: false,
      stopping: false,
      stoppingRoots,
      continuingRoots,
      onStop,
      onContinue,
    }),
  );

/** A two-exchange room: #41 was superseded with Sonnet still talking, #57 is open with Codex queued
 *  and nothing of its own in flight. The room-wide `inFlight` is Sonnet's, so a strip that read the
 *  top-level list instead of its own would show the wrong chips and the wrong Stop. */
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
  test('M57 elapsed formatting guards unknown, invalid and future starts', () => {
    const now = Date.parse('2026-09-20T12:00:00Z');
    expect(workingElapsed(undefined, now)).toBeNull();
    expect(workingElapsed('invalid', now)).toBeNull();
    expect(workingElapsed('2026-09-20T12:00:03Z', now)).toBeNull();
    expect(workingElapsed('2026-09-20T12:00:01Z', now)).toBe('0:00');
    expect(workingElapsed('2026-09-20T10:58:59Z', now)).toBe('1:01:01');
  });

  test('M57 shows elapsed working time from the hub timestamp and removes it with the chip', () => {
    vi.useFakeTimers();
    try {
      vi.setSystemTime(new Date('2026-09-20T12:01:05Z'));
      const timed = { ...BASE, inFlightStartedAt: { sonnet: '2026-09-20T12:00:00Z' } };
      expect(render(timed)).toContain('1:05');
      expect(render({ ...timed, inFlight: [] })).not.toContain('1:05');
      expect(render(BASE)).not.toContain('exchange-elapsed');
      const disconnected = renderToStaticMarkup(
        <ExchangeBar exchange={timed} connected={false} runStoppable={false} stopping={false}
          stoppingRoots={NONE} continuingRoots={NONE} onStop={() => undefined} onContinue={() => undefined} />,
      );
      expect(disconnected).toContain('last known');
      expect(disconnected).not.toContain('1:05');
    } finally {
      vi.useRealTimers();
    }
  });

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

  /** The yield is for the life of the run, not for the life of the room. Once the run has `ended`,
   *  App's `runStoppable` is false again and this bar is exactly what it would be with no run,
   *  including for a spawn that outlived its exchange. */
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

  /** Every `RunPolicy` park is policy-driven and so is a ping-End, and all of them close the
   *  conductor's exchange, so without the cause on the wire a cap the hub owner never touched would tell
   *  him he had stopped it. The marker has to attribute the stop to whoever actually made it. */
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

  /** A snapshot from an older hub, and any stop the hub could not attribute, sends no cause. Guessing
   *  "you" there would blame the hub owner wrongly, so the null case is neutral. */
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
  test('M57 each concurrent strip uses its own working start', () => {
    vi.useFakeTimers();
    try {
      vi.setSystemTime(new Date('2026-09-20T12:02:00Z'));
      const older = { ...OLDER, inFlightStartedAt: { sonnet: '2026-09-20T12:00:00Z' } };
      const newer = { ...NEWER, inFlight: ['codex'], pending: [], inFlightStartedAt: { codex: '2026-09-20T12:01:30Z' } };
      const markup = render({ ...TWO, exchanges: [older, newer] });
      expect(markup).toContain('Sonnet<span class="exchange-elapsed" aria-hidden="true"> · 2:00</span>');
      expect(markup).toContain('Codex<span class="exchange-elapsed" aria-hidden="true"> · 0:30</span>');
    } finally {
      vi.useRealTimers();
    }
  });
  /** One strip per entry, oldest first, each drawn from its own fields. */
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

  /** With more than one strip the hub owner has to be able to tell them apart. */
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

  /** A live run owns the stop, for every strip. */
  test('a live run hides every strip\'s stop', () => {
    expect(render(TWO, true)).not.toContain('<button');
  });

  /** The gate is per strip, on the strip's own `inFlight`. */
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

  /** The component half: one strip's pending stop disables that strip and no other. */
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

  /** An older hub sends no `exchanges`, and its only stop is the room's. */
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

/** `continuable` is the hub's decision (owner-rooted, closed, nothing of its own in flight, no active
 *  run), so these fixtures carry it the way a hub would: a concluded exchange with an empty
 *  `inFlight` has it, an open one does not, and an older hub sends no field at all. The client adds
 *  one gate of its own: the live run, the same one the stop already yields to. */
const CONTINUABLE: ExchangeView = { ...OLDER, status: 'concluded', inFlight: [], continuable: true };
const CONTINUABLE_NEWER: ExchangeView = { ...NEWER, status: 'concluded', pending: [], continuable: true };
const ONE: ExchangeSnapshot = { ...TWO, inFlight: [], pending: [], exchanges: [CONTINUABLE] };
const BOTH: ExchangeSnapshot = { ...TWO, inFlight: [], pending: [], exchanges: [CONTINUABLE, CONTINUABLE_NEWER] };

describe('ExchangeBar with a continuable exchange', () => {
  test('a continuable strip offers Continue exchange and wires its root', () => {
    const markup = render(ONE);

    expect(markup).toContain('>Continue exchange</button>');
    expect(markup).not.toContain('Stop exchange');

    const roots: number[] = [];
    const found = buttons(ONE, () => undefined, NONE, (root) => roots.push(root));
    expect(found).toHaveLength(1);
    found[0]?.props.onClick?.();
    expect(roots).toEqual([41]);

    // The top-level strip of a hub that sends no `exchanges` continues its own root, and has nothing
    // to reply to when that root is null, so it offers no button there.
    const topLevel: ExchangeSnapshot = { ...BASE, status: 'concluded', inFlight: [], continuable: true };
    const alone = buttons(topLevel, () => undefined, NONE, (root) => roots.push(root));
    expect(alone).toHaveLength(1);
    alone[0]?.props.onClick?.();
    expect(roots).toEqual([41, 41]);
    expect(render({ ...topLevel, rootMessageId: null })).not.toContain('Continue exchange');
  });

  test('a live run hides Continue', () => {
    expect(render(ONE, true)).not.toContain('Continue exchange');
  });

  test('an open exchange has no Continue', () => {
    expect(render({ ...TWO, exchanges: [{ ...NEWER, continuable: false }] })).not.toContain('Continue exchange');
  });

  test('a continue in flight disables only that strip', () => {
    const found = buttons(BOTH, () => undefined, NONE, () => undefined, new Set([57]));

    expect(found).toHaveLength(2);
    expect(found[0]?.props.disabled).toBe(false);
    expect(found[1]?.props.disabled).toBe(true);
  });

  test('a hub that sends no continuable renders no Continue', () => {
    const older: ExchangeSnapshot = { ...TWO, inFlight: [], pending: [], exchanges: [{ ...OLDER, status: 'concluded', inFlight: [] }] };

    expect(render(older)).not.toContain('Continue exchange');
    expect(render(older)).not.toContain('<button');
  });

  test('several strips give each Continue its own accessible name', () => {
    expect(render(BOTH)).toContain('aria-label="Continue exchange #41"');
    expect(render(BOTH)).toContain('aria-label="Continue exchange #57"');
    // A lone strip names no root anywhere else either, so its button keeps its visible text alone.
    expect(render(ONE)).not.toContain('aria-label="Continue exchange');
  });
});
