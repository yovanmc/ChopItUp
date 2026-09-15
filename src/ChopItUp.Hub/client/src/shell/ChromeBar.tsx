import { useEffect, useState, type ReactNode } from 'react';
import { host, isHosted, onState, type ShellState } from './hostBridge';

/** Row 12, AC3: the title bar, drawn by the page. Inside `ChopItUp.Desktop` the window has
 *  `WindowStyle=None` and no caption of its own, so this 36 px row is the only way to move, maximise
 *  or close it — WebView2 hit-tests the CSS `app-region` on this row (`.chrome` drag, `.chrome-btn`
 *  no-drag, both in `styles.css`) and reports the result to Windows as a non-client region.
 *
 *  In a browser tab it renders nothing: the same build serves both, and a page that drew fake window
 *  buttons into a real browser's viewport would be drawing buttons that cannot work.
 *
 *  `hosted` and `state` are props with live defaults rather than reads inside the body so the
 *  component can be rendered under node without a `window` (the whole client suite runs that way).
 *  Note where the default comes from: `isHosted()` is evaluated when the component renders, never
 *  when this module is imported. */

const BEFORE_THE_SHELL_ANSWERS: ShellState = {
  maximized: false,
  hub: { state: 'starting', port: 0, reason: null },
};

/** What the chip says. The port is worth showing because the shell may have attached to a hub on a
 *  port that is not the one it was asked for (B3), and because the owner pastes it into MCP configs. */
export function hubLabel(hub: ShellState['hub']): string {
  switch (hub.state) {
    case 'ready':
      return `hub · ${hub.port}`;
    case 'attached':
      return 'hub · attached';
    case 'failed':
      return 'hub · failed';
    case 'stopped':
      return 'hub · stopped';
    default:
      return 'hub · starting…';
  }
}

/** Segoe MDL2's shapes redrawn as 10×10 strokes, so the row needs no icon font and inherits its
 *  colour from the button (which is what makes the close button's white-on-red hover work). */
const glyph = (children: ReactNode) => (
  <svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" strokeWidth="1" aria-hidden="true" focusable="false">
    {children}
  </svg>
);

const MinimizeGlyph = glyph(<path d="M0.5 5.5h9" />);
const MaximizeGlyph = glyph(<rect x="0.5" y="0.5" width="9" height="9" />);
const RestoreGlyph = glyph(
  <>
    <path d="M2.5 2.5V0.5h7v7h-2" />
    <rect x="0.5" y="2.5" width="7" height="7" />
  </>,
);
const CloseGlyph = glyph(
  <>
    <path d="M0.5 0.5l9 9" />
    <path d="M9.5 0.5l-9 9" />
  </>,
);

export interface ChromeBarProps {
  /** Defaults to the live answer; a test passes it so the component can be rendered either way. */
  hosted?: boolean;
  /** Defaults to what the shell pushes; a test passes it instead of running the effect. */
  state?: ShellState;
}

export default function ChromeBar({ hosted = isHosted(), state }: ChromeBarProps) {
  const [live, setLive] = useState<ShellState>(BEFORE_THE_SHELL_ANSWERS);
  const told = state !== undefined;

  useEffect(() => {
    if (!hosted || told) return;
    // Subscribe before asking, so a change that lands between the two is not missed.
    const stop = onState(setLive);
    void host
      .getState()
      .then((answer) => {
        if (answer !== undefined) setLive(answer);
      })
      .catch(() => undefined);
    return stop;
  }, [hosted, told]);

  if (!hosted) return null;

  const shown = state ?? live;
  const maximizeLabel = shown.maximized ? 'Restore' : 'Maximize';

  return (
    <div className="chrome" data-testid="chrome">
      <span className="wordmark chrome-wordmark">Chop It Up</span>
      <span className="chrome-hub" data-state={shown.hub.state} title={shown.hub.reason ?? undefined}>
        {hubLabel(shown.hub)}
      </span>
      <div className="chrome-cluster">
        <button type="button" className="chrome-btn" aria-label="Minimize" title="Minimize" onClick={() => void host.minimize()}>
          {MinimizeGlyph}
        </button>
        <button
          type="button"
          className="chrome-btn"
          aria-label={maximizeLabel}
          title={maximizeLabel}
          onClick={() => void host.toggleMaximize()}
        >
          {shown.maximized ? RestoreGlyph : MaximizeGlyph}
        </button>
        <button type="button" className="chrome-btn chrome-close" aria-label="Close" title="Close" onClick={() => void host.close()}>
          {CloseGlyph}
        </button>
      </div>
    </div>
  );
}
