import { memo, useEffect, useState } from 'react';
import { displayName } from './participants';
import type { ExchangeSnapshot, ExchangeView } from './types';

interface ExchangeBarProps {
  exchange: ExchangeSnapshot | null;
  /** False while a socket is disconnected; a cached snapshot cannot prove a spawn is still running. */
  connected?: boolean;
  runStoppable: boolean;
  /** The room stop in flight: the only stop a hub without `exchanges` offers, shared with `RunBar`. */
  stopping: boolean;
  /** Row 34: the roots whose own stop is in flight. Per root, so pressing one strip greys that strip
   *  and leaves its neighbours pressable. */
  stoppingRoots: ReadonlySet<number>;
  /** Row 44: the roots whose `/continue` post is in flight, the twin of `stoppingRoots` above. */
  continuingRoots: ReadonlySet<number>;
  /** A strip's root, or `null` for the room stop an older hub's single strip presses. */
  onStop: (root: number | null) => void;
  /** Row 44: the root to continue. Always a root — a strip with none offers no Continue. */
  onContinue: (root: number) => void;
}

/** What one strip draws from: the fields an `ExchangeView` and the snapshot's top level share. */
type StripFields = Pick<
  ExchangeView,
  'status' | 'inFlight' | 'inFlightStartedAt' | 'pending' | 'budget' | 'remaining' | 'turnsUsed' | 'stoppedBy' | 'continuable'
>;

interface StripControl {
  key: string;
  /** Rendered only when the bar holds more than one strip; `null` keeps a lone strip as it was. */
  label: string | null;
  runStoppable: boolean;
  disabled: boolean;
  continueDisabled: boolean;
  connected: boolean;
  onStop: () => void;
  onContinue: () => void;
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

/** Elapsed since the hub first marked this spawn working. Missing, invalid and implausibly future
 *  instants have no clock label; a small clock difference is clamped to zero. */
export function workingElapsed(startedAt: string | undefined, nowMs: number): string | null {
  if (!startedAt || !Number.isFinite(nowMs)) return null;
  const startedMs = Date.parse(startedAt);
  if (!Number.isFinite(startedMs) || startedMs > nowMs + 2_000) return null;
  const seconds = Math.max(0, Math.floor((nowMs - startedMs) / 1_000));
  const minutes = Math.floor(seconds / 60);
  const tail = String(seconds % 60).padStart(2, '0');
  return minutes >= 60
    ? `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, '0')}:${tail}`
    : `${minutes}:${tail}`;
}

/** A local clock updates only this chip, without new server events or changing the parent status
 *  region's accessible text once a second. */
function WorkingChip({ id, startedAt, connected }: { id: string; startedAt?: string; connected: boolean }) {
  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    if (!connected || !startedAt || !Number.isFinite(Date.parse(startedAt))) return;
    setNowMs(Date.now());
    const timer = window.setInterval(() => setNowMs(Date.now()), 1_000);
    return () => window.clearInterval(timer);
  }, [connected, startedAt]);
  const elapsed = connected ? workingElapsed(startedAt, nowMs) : null;
  const name = displayName(id);
  return (
    <span className="exchange-chip working" title={connected ? `${name} is replying` : `${name} was working when the connection was lost`}>
      <span className="dot" aria-hidden="true" />
      {name}
      {elapsed !== null && <span className="exchange-elapsed" aria-hidden="true"> · {elapsed}</span>}
      {!connected && <span aria-hidden="true"> · last known</span>}
    </span>
  );
}

/** One compact strip above the composer, and nothing at all when the room is idle: a room that has
 *  never run an exchange should look exactly as it did before this row shipped.
 *
 *  `inFlight` is the ROOM's live spawns, not the current exchange's — a superseded exchange can still
 *  have a process talking — so the working chips and the Stop button are driven by `inFlight` rather
 *  than by `status`. That is why Stop survives `superseded`: there is still something to stop.
 *
 *  Idle is the only state that hides the bar outright. It is the zero state (seq 0, nothing has ever
 *  run) and a room never returns to it, so nothing live can be hidden behind that branch.
 *
 *  Row 34: a hub that sends `exchanges` gets one strip per entry, in the hub's order (a reopened
 *  exchange last), each on its own fields and its own stop; the strips are siblings rather than a
 *  wrapped list, so each keeps the `.exchange`
 *  row it always had and the stack reads like `RunBar` above it. That hub sends an empty list exactly
 *  when its top level is idle, so the empty case falls through to the idle branch below. A hub
 *  without `exchanges` renders the one top-level strip with the room stop, as before. */
function ExchangeBar({ exchange, connected = true, runStoppable, stopping, stoppingRoots, continuingRoots, onStop, onContinue }: ExchangeBarProps) {
  const views = exchange?.exchanges ?? [];
  if (views.length > 0) {
    const labelled = views.length > 1;
    return (
      <>
        {views.map((view) =>
          strip(view, {
            key: `x:${view.rootMessageId}`,
            label: labelled ? `#${view.rootMessageId}` : null,
            runStoppable,
            disabled: stoppingRoots.has(view.rootMessageId),
            continueDisabled: continuingRoots.has(view.rootMessageId),
            connected,
            onStop: () => onStop(view.rootMessageId),
            onContinue: () => onContinue(view.rootMessageId),
          }),
        )}
      </>
    );
  }

  if (exchange === null || exchange.status === 'idle') return null;
  /** Row 44: the top-level strip continues its own root. A snapshot with no root has nothing for the
   *  `/continue` post to reply to, so that strip offers no Continue however the hub marked it. */
  const root = exchange.rootMessageId;
  return strip(root === null ? { ...exchange, continuable: false } : exchange, {
    key: 'room',
    label: null,
    runStoppable,
    disabled: stopping,
    continueDisabled: root !== null && continuingRoots.has(root),
    connected,
    onStop: () => onStop(null),
    onContinue: () => {
      if (root !== null) onContinue(root);
    },
  });
}

/** One strip. A plain function rather than a component, so the element tree the bar returns holds the
 *  buttons themselves — that is what lets the tests press one without a DOM.
 *
 *  On a top-level strip `inFlight` is the room's; on a per-exchange strip it is that exchange's own,
 *  which is the truer gate for that strip's Stop. */
function strip(
  fields: StripFields,
  { key, label, runStoppable, disabled, continueDisabled, connected, onStop, onContinue }: StripControl,
) {
  const { status, inFlight, inFlightStartedAt, pending, budget, remaining, turnsUsed, stoppedBy, continuable } = fields;
  const open = status === 'open';
  /** The cause only speaks for a `stopped` exchange: a superseded one carries whatever cause its
   *  last stop left behind, and "Superseded" is still the truer word for it. */
  const marker = status === 'stopped' && stoppedBy !== null ? STOPPED_BY[stoppedBy] : OUTCOME[status];
  /** Row 22: one control per stop. `onStop` here and the run strip's button hit the same endpoint,
   *  and that endpoint ends the RUN whenever there is a live one — so while a run is `active` or
   *  `parked` this button would be a second, worse-labelled copy of `RunBar`'s. It yields for exactly
   *  as long as that run lives; once it has `ended` the gate is the old one again, because an
   *  exchange with a spawn still talking is still worth stopping on its own. The per-exchange stop
   *  (row 34) yields the same way: its endpoint refuses with 409 while a run owns the room. */
  const stoppable = !runStoppable && (open || inFlight.length > 0);

  /** Rendered in the lead while the exchange is open and in the tail after it closed, so a spawn that
   *  outlives its exchange stays visible beside the marker that explains why it is alone. */
  const working = inFlight.map((id) => (
    <WorkingChip key={`w:${id}:${inFlightStartedAt?.[id] ?? ''}`} id={id} startedAt={inFlightStartedAt?.[id]} connected={connected} />
  ));

  return (
    <div key={key} className={`exchange exchange-${status}`} role="status" aria-live="polite">
      <div className="exchange-lead">
        {label !== null && <span className="exchange-turns">{label}</span>}
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
        {/* Row 44 (D-e/AC5): whether this exchange can be continued is the hub's decision, sent as
            `continuable`; the client honours only the run gate its Stop already honours, so the two
            controls never disagree about who owns the room. Explicitly `=== true` because a hub older
            than this row sends no field, and that hub would refuse the post. */}
        {continuable === true && !runStoppable && (
          <button
            type="button"
            className="quiet"
            disabled={continueDisabled}
            onClick={onContinue}
            aria-label={label === null ? undefined : `Continue exchange ${label}`}
          >
            Continue exchange
          </button>
        )}
        {stoppable && (
          <button type="button" className="quiet danger" disabled={disabled} onClick={onStop}>
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
