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
});
