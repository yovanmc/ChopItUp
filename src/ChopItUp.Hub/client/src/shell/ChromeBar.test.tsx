import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test } from 'vitest';
import ChromeBar from './ChromeBar';
import type { HubState, ShellState } from './hostBridge';

/** Inside the shell the window has no title bar of its own, so this row IS the title
 *  bar: lose it and the window cannot be moved, minimised or closed. Static markup through
 *  `react-dom/server` like every other component test here (no jsdom), which means the effect that
 *  subscribes to the shell never runs — the state a test wants is handed in as a prop instead.
 *
 *  What this cannot prove is the drag behaviour: `app-region` is a CSS property Chromium hit-tests
 *  geometrically, so the row's draggability and the buttons' opt-out are asserted on the real window
 *  by `tools\Invoke-Row12ShellCheck.ps1`. Here the claim is narrower and still worth making: the row
 *  carries the classes that rule declares on, and the buttons carry the ones that opt out. */

const state = (hub: HubState, port = 8790, maximized = false): ShellState => ({
  maximized,
  hub: { state: hub, port, reason: hub === 'failed' ? 'the hub exited before /health answered' : null },
});

const render = (props: { hosted: boolean; state?: ShellState }) => renderToStaticMarkup(<ChromeBar {...props} />);

describe('ChromeBar', () => {
  test('a plain browser gets nothing at all — the page is the whole window there', () => {
    expect(render({ hosted: false, state: state('ready') })).toBe('');
  });

  test('inside the shell it draws the wordmark, the hub chip and the three window buttons', () => {
    const markup = render({ hosted: true, state: state('ready') });

    expect(markup).toContain('data-testid="chrome"');
    expect(markup).toContain('Chop It Up');
    expect(markup).toContain('hub · 8790');
    expect(markup).toContain('aria-label="Minimize"');
    expect(markup).toContain('aria-label="Maximize"');
    expect(markup).toContain('aria-label="Close"');
  });

  test('the row declares itself draggable and the button cluster declares itself not', () => {
    const markup = render({ hosted: true, state: state('ready') });

    expect(markup).toContain('class="chrome"');
    expect(markup).toContain('class="chrome-cluster"');
    expect(markup).toContain('class="chrome-btn"');
    expect(markup).toContain('class="chrome-btn chrome-close"');
  });

  test('a maximized window offers Restore where it offered Maximize', () => {
    const markup = render({ hosted: true, state: state('ready', 8790, true) });

    expect(markup).toContain('aria-label="Restore"');
    expect(markup).not.toContain('aria-label="Maximize"');
  });

  test('the chip says which hub the window is looking at', () => {
    expect(render({ hosted: true, state: state('attached', 8795) })).toContain('hub · attached');
    expect(render({ hosted: true, state: state('starting') })).toContain('hub · starting…');
    expect(render({ hosted: true, state: state('stopped') })).toContain('hub · stopped');
  });

  /** The chip is the only thing on screen that can say the hub died, so its failure has to be
   *  reachable by CSS as well as readable. */
  test('a failed hub says so and carries the state for the stylesheet', () => {
    const markup = render({ hosted: true, state: state('failed') });

    expect(markup).toContain('hub · failed');
    expect(markup).toContain('data-state="failed"');
  });

  /** Before the shell answers `getState` the row is already on screen; it must not render a port it
   *  has not been told yet. */
  test('with no state yet it shows the row and says the hub is starting', () => {
    const markup = render({ hosted: true });

    expect(markup).toContain('data-testid="chrome"');
    expect(markup).toContain('hub · starting…');
    expect(markup).not.toContain('hub · 0');
  });
});
