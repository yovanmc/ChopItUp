import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test } from 'vitest';
import RunBar from './RunBar';
import { setRoster } from './participants';
import type { RunSnapshot } from './types';

/** Row 19, task 14. `parked` and `ended` cannot be reached against a real hub inside a build task —
 *  the caps are hard code, so parking one for real costs 8 hours, 80 spawns or three phase re-entries
 *  (pass 2's F-20). The browser leg proves `active` and the no-run case; these hand-written rows are
 *  what prove the other two renders, and they are the reason a park is not "not tested".
 *
 *  Rendered through `react-dom/server`, not a DOM: the strip has no behaviour to click, so static
 *  markup is the whole of what it produces and a jsdom would only add a dependency. */
setRoster([{ id: 'sonnet', displayName: 'Sonnet', kind: 'model', host: 'claude', model: 'sonnet' }]);

const BASE: RunSnapshot = {
  id: 7,
  roomId: 'lab',
  conductorId: 'sonnet',
  skillName: 'build-thing',
  status: 'active',
  reason: null,
  capSpent: false,
  phase: 'build/api',
  phaseHistory: { 'build/api': 2, critique: 1 },
  phaseEntries: 2,
  phaseEntryCap: 3,
  exchanges: 5,
  spawnsUsed: 12,
  spawnCap: 80,
  startedAt: '2026-03-01T09:00:00.0000000+00:00',
  endedAt: null,
  elapsedMinutes: 185,
  wallClockCapMinutes: 480,
  artifacts: [],
  gateRuns: [],
};

const render = (run: RunSnapshot | null) => renderToStaticMarkup(<RunBar run={run} />);

describe('RunBar', () => {
  test('a room that has never had a run renders nothing at all', () => {
    expect(render(null)).toBe('');
  });

  test('an active run leads with the phase and trails the counters', () => {
    const html = render(BASE);

    expect(html).toContain('class="run run-active"');
    expect(html).toContain('role="status"');
    expect(html).toContain('aria-live="polite"');
    expect(html).toContain('<span class="run-phase">build/api</span>');
    expect(html).toContain('entry 2 of 3');
    expect(html).toContain('12 of 80 spawns');
    expect(html).toContain('3h 05m of 8h 00m');
    expect(html).toContain('Sonnet conducts');
    expect(html).not.toContain('run-reason');
  });

  test('a parked run makes the reason the loudest thing in the strip', () => {
    const html = render({
      ...BASE,
      status: 'parked',
      reason: 'the conductor broke the phase rules twice in a row',
      capSpent: false,
    });

    expect(html).toContain('class="run run-parked"');
    expect(html).toContain('<span class="run-reason">the conductor broke the phase rules twice in a row</span>');
    expect(html).toContain('Run parked');
    // The phase is still there, demoted into the quiet counters line — the reason is what the owner
    // is meant to see from across the room.
    expect(html).not.toContain('run-phase');
    expect(html).toContain('build/api ');
  });

  test('a spent hard cap parks with the cap as the reason, and reads the same way', () => {
    const html = render({ ...BASE, status: 'parked', reason: 'the 80-spawn cap is spent', capSpent: true });

    expect(html).toContain('class="run run-parked"');
    expect(html).toContain('the 80-spawn cap is spent');
  });

  test('an ended run says only that it finished, with no phase and no entry counter', () => {
    const html = render({
      ...BASE,
      status: 'ended',
      reason: 'the conductor posted phase: ping',
      endedAt: '2026-03-01T12:05:00.0000000+00:00',
    });

    expect(html).toContain('class="run run-ended"');
    expect(html).toContain('Run finished');
    expect(html).not.toContain('run-phase');
    expect(html).not.toContain('run-reason');
    expect(html).not.toContain('entry 2 of 3');
    expect(html).toContain('12 of 80 spawns');
  });

  test('a parked run with no recorded reason still says something rather than rendering blank', () => {
    expect(render({ ...BASE, status: 'parked', reason: null })).toContain('no reason recorded');
  });
});
