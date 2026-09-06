import { memo } from 'react';
import { displayName } from './participants';
import type { ExchangeSnapshot } from './types';

interface ExchangeBarProps {
  exchange: ExchangeSnapshot | null;
  stopping: boolean;
  onStop: () => void;
}

/** The word the marker line leads with once an exchange is over. `idle` renders no bar at all and
 *  `open` renders indicators instead of a marker, so those two entries exist only to keep the map
 *  total over the status union — that totality is what makes a new status a compile error here. */
const OUTCOME: Record<ExchangeSnapshot['status'], string> = {
  idle: 'Exchange over',
  open: 'Exchange running',
  concluded: 'Exchange concluded',
  stopped: 'Stopped by you',
  superseded: 'Superseded',
};

/** One compact strip above the composer, and nothing at all when the room is idle: a room that has
 *  never run an exchange should look exactly as it did before this row shipped.
 *
 *  `inFlight` is the ROOM's live spawns, not the current exchange's — a superseded exchange can still
 *  have a process talking — so the working chips and the Stop button are driven by `inFlight` rather
 *  than by `status`. That is why Stop survives `superseded`: there is still something to stop.
 *
 *  Idle is the only state that hides the bar outright. It is the zero state (seq 0, nothing has ever
 *  run) and a room never returns to it, so nothing live can be hidden behind that branch. */
function ExchangeBar({ exchange, stopping, onStop }: ExchangeBarProps) {
  if (exchange === null || exchange.status === 'idle') return null;

  const { status, inFlight, pending, budget, remaining, turnsUsed } = exchange;
  const open = status === 'open';
  const stoppable = open || inFlight.length > 0;

  /** Rendered in the lead while the exchange is open and in the tail after it closed, so a spawn that
   *  outlives its exchange stays visible beside the marker that explains why it is alone. */
  const working = inFlight.map((id) => (
    <span key={`w:${id}`} className="exchange-chip working" title={`${displayName(id)} is replying`}>
      <span className="dot" aria-hidden="true" />
      {displayName(id)}
    </span>
  ));

  return (
    <div className={`exchange exchange-${status}`} role="status" aria-live="polite">
      <div className="exchange-lead">
        {open ? (
          <>
            {working}
            {pending.map((id) => (
              <span key={`q:${id}`} className="exchange-chip queued" title={`${displayName(id)} is queued`}>
                {displayName(id)}
              </span>
            ))}
          </>
        ) : (
          <span className="exchange-marker">
            {OUTCOME[status]} · {turnsUsed} of {budget} turns used
          </span>
        )}
      </div>
      <div className="exchange-tail">
        {open ? (
          <span className="exchange-turns">
            {remaining} of {budget} turns left
          </span>
        ) : (
          working
        )}
        {stoppable && (
          <button type="button" className="quiet danger" disabled={stopping} onClick={onStop}>
            Stop exchange
          </button>
        )}
      </div>
    </div>
  );
}

/** Memoised for the same reason `RoomRail` is: every merged message re-renders `App`, and the bar's
 *  props only change when a snapshot does. */
export default memo(ExchangeBar);
