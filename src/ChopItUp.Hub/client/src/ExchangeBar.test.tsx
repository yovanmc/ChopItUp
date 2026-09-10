import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test } from 'vitest';
import ExchangeBar from './ExchangeBar';
import { setRoster } from './participants';
import type { ExchangeSnapshot } from './types';

/** Row 22, AC4: one control per stop. While a run is `active` or `parked` the run strip owns the
 *  stop, so this bar must not offer a second one — and the moment that run is over, its button has to
 *  come back exactly as it was, because an exchange with a live spawn still needs stopping.
 *
 *  Rendered through `react-dom/server` like `RunBar.test.tsx`: presence and disabled state are all
 *  static markup, and this client has no jsdom to click in. */
setRoster([{ id: 'sonnet', displayName: 'Sonnet', kind: 'model', host: 'claude', model: 'sonnet' }]);

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
};

const render = (exchange: ExchangeSnapshot | null, runStoppable = false, stopping = false) =>
  renderToStaticMarkup(
    <ExchangeBar
      exchange={exchange}
      runStoppable={runStoppable}
      stopping={stopping}
      onStop={() => undefined}
    />,
  );

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

  test('an idle room and a room with no exchange still render nothing at all', () => {
    expect(render({ ...BASE, status: 'idle' })).toBe('');
    expect(render(null)).toBe('');
  });
});
