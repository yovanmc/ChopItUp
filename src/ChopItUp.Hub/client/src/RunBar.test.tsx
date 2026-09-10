import { isValidElement, type ReactElement, type ReactNode } from 'react';
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

const render = (run: RunSnapshot | null, stopping = false) =>
  renderToStaticMarkup(<RunBar run={run} stopping={stopping} onStop={() => undefined} />);

interface ButtonProps {
  children?: ReactNode;
  onClick?: () => void;
  disabled?: boolean;
}

/** Static markup carries no handlers, and there is still no DOM here to click in. So for the one
 *  assertion that needs the wiring rather than the picture (row 22 AC2's client half), call the
 *  memoised component's own function and walk the element tree it returns for the button. */
function findButton(node: ReactNode): ReactElement<ButtonProps> | null {
  if (Array.isArray(node)) {
    for (const child of node as ReactNode[]) {
      const hit = findButton(child);
      if (hit !== null) return hit;
    }
    return null;
  }
  if (!isValidElement(node)) return null;
  if (node.type === 'button') return node as ReactElement<ButtonProps>;
  return findButton((node.props as { children?: ReactNode }).children ?? null);
}

const button = (run: RunSnapshot, stopping: boolean, onStop: () => void) =>
  findButton(RunBar.type({ run, stopping, onStop }));

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

  test('an active run offers the stop the owner can reach', () => {
    expect(render(BASE)).toContain('>Stop run</button>');
  });

  /** AC1. The strip takes no exchange prop at all, which is what makes "in every such state" true by
   *  construction: an active run with no open exchange and nothing in flight — the state that had no
   *  button before this row — renders the same strip as any other. */
  test('a parked run offers the same stop, which is the state that had none', () => {
    const html = render({ ...BASE, status: 'parked', reason: 'the 80-spawn cap is spent' });

    expect(html).toContain('>Stop run</button>');
    expect(html).toContain('class="quiet danger"');
  });

  test('the control is labelled for the run, not for the exchange', () => {
    expect(render(BASE)).not.toContain('Stop exchange');
  });

  /** AC3. Nothing left to end, so nothing to press — and a room that never had a run still renders
   *  the empty string, so the control cannot appear where there is no strip. */
  test('an ended run offers no stop', () => {
    expect(render({ ...BASE, status: 'ended', endedAt: '2026-03-01T12:05:00.0000000+00:00' })).not.toContain(
      '<button',
    );
  });

  test('a room that never had a run has no control either', () => {
    expect(render(null)).not.toContain('<button');
  });

  /** AC5. `stopping` is App's in-flight flag for the stop call itself, held until the refreshed run
   *  lands, so a second press cannot race the first. */
  test('a stop already in flight leaves the control disabled', () => {
    expect(render(BASE, true)).toContain('disabled=""');
    expect(render(BASE, false)).not.toContain('disabled');
  });

  /** AC2, client half: pressing it runs the caller's stop. That the run then reaches `ended` is the
   *  server's half and the interactive gate's to prove. */
  test('pressing the control calls the stop it was handed', () => {
    let calls = 0;
    const found = button(BASE, false, () => {
      calls += 1;
    });

    expect(found).not.toBeNull();
    found?.props.onClick?.();
    expect(calls).toBe(1);
  });

  test('the parked state is wired to the same stop', () => {
    let calls = 0;
    const found = button({ ...BASE, status: 'parked', reason: 'stalled' }, false, () => {
      calls += 1;
    });

    found?.props.onClick?.();
    expect(calls).toBe(1);
  });
});
