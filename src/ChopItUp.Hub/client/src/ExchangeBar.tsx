import { memo } from 'react';
import { displayName } from './participants';
import type { ExchangeSnapshot } from './types';

interface ExchangeBarProps {
  exchange: ExchangeSnapshot | null;
  runStoppable: boolean;
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
  /** Deliberately neutral: it is what a stop with no cause on the wire reads as, and the only stop
   *  that has no cause is one this client cannot attribute (a pre-row-27 hub). Attribution lives in
   *  `STOPPED_BY` below — blaming the owner by default is the defect row 27 closes. */
  stopped: 'Exchange stopped',
  superseded: 'Superseded',
};

/** Row 27, AC4: who stopped it. A run parks itself on a cap the owner never touched, and the note in
 *  the transcript says so, so the marker must not tell him he did it.
 *
 *  Total over the cause union on purpose (the `OUTCOME` trick, one type down): a cause added to the
 *  hub's `ExchangeStopCause` and mirrored into `ExchangeSnapshot['stoppedBy']` without a label here
 *  fails `npm run typecheck` rather than rendering as `undefined` in the one state nobody tested.
 *
 *  The run arm names the run and stops there. `RunBar` sits directly above this strip and already
 *  leads with "Run parked · <reason>" or "Run finished", so repeating the reason here would say the
 *  same thing twice in two lines the owner reads as one. */
const STOPPED_BY: Record<NonNullable<ExchangeSnapshot['stoppedBy']>, string> = {
  owner: 'Stopped by you',
  run: 'Stopped by the run',
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
function ExchangeBar({ exchange, runStoppable, stopping, onStop }: ExchangeBarProps) {
  if (exchange === null || exchange.status === 'idle') return null;

  const { status, inFlight, pending, budget, remaining, turnsUsed, stoppedBy } = exchange;
  const open = status === 'open';
  /** The cause only speaks for a `stopped` exchange: a superseded one carries whatever cause its
   *  last stop left behind, and "Superseded" is still the truer word for it. */
  const marker = status === 'stopped' && stoppedBy !== null ? STOPPED_BY[stoppedBy] : OUTCOME[status];
  /** Row 22: one control per stop. `onStop` here and the run strip's button hit the same endpoint,
   *  and that endpoint ends the RUN whenever there is a live one — so while a run is `active` or
   *  `parked` this button would be a second, worse-labelled copy of `RunBar`'s. It yields for exactly
   *  as long as that run lives; once it has `ended` the gate is the old one again, because an
   *  exchange with a spawn still talking is still worth stopping on its own. */
  const stoppable = !runStoppable && (open || inFlight.length > 0);

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
            {marker} · {turnsUsed} of {budget} turns used
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
