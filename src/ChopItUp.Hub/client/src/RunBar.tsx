import { memo } from 'react';
import { displayName } from './participants';
import type { RunSnapshot } from './types';

/** The word each state leads with. Total over `RunSnapshot['status']` on purpose: a status added to
 *  the hub (Core/Model/Run.cs `RunStatus`) and not to this map is a compile error here rather than a
 *  strip that renders a blank label in the one state nobody tested. */
const LABEL: Record<RunSnapshot['status'], string> = {
  active: 'Run',
  parked: 'Run parked',
  ended: 'Run finished',
};

/** Budget, not clock time: "45m", "3h 05m". Hours matter because the wall-clock cap is 8 of them. */
function duration(minutes: number): string {
  if (minutes < 60) return `${minutes}m`;
  const h = Math.floor(minutes / 60);
  return `${h}h ${String(minutes % 60).padStart(2, '0')}m`;
}

/** The run strip: `ExchangeBar`'s sibling, sitting directly above it (plan P6), and nothing at all in
 *  a room that has never had a run — that room looks exactly as it did before this row shipped.
 *
 *  A run is something the owner starts and walks away from, so the three states have to be told apart
 *  from across the room rather than read: `active` leads with the phase, `parked` leads with the
 *  REASON in the danger colour (it is the one state that needs the owner, so it is the loudest thing
 *  in the strip), `ended` is grey and says only that it finished.
 *
 *  Per-room and per-room only (LESSONS M9, decided in the plan): a park in a room the browser is not
 *  showing surfaces when the owner opens that room. The park note mentions `@owner` and the rail
 *  already badges a room with unread messages, so the signal exists — it is just not run-specific. */
function RunBar({ run }: { run: RunSnapshot | null }) {
  if (run === null) return null;

  const { status, phase, reason, conductorId } = run;
  const parked = status === 'parked';
  const ended = status === 'ended';

  return (
    <div className={`run run-${status}`} role="status" aria-live="polite">
      <div className="run-lead">
        <span className="run-label">{LABEL[status]}</span>
        {parked ? (
          <span className="run-reason">{reason ?? 'no reason recorded'}</span>
        ) : (
          !ended && <span className="run-phase">{phase}</span>
        )}
      </div>
      <div className="run-tail">
        <span className="run-counters">
          {parked && `${phase} · `}
          {!ended && `entry ${run.phaseEntries} of ${run.phaseEntryCap} · `}
          {run.spawnsUsed} of {run.spawnCap} spawns · {duration(run.elapsedMinutes)} of{' '}
          {duration(run.wallClockCapMinutes)}
        </span>
        <span className="run-conductor">{displayName(conductorId)} conducts</span>
      </div>
    </div>
  );
}

/** Memoised for the same reason `ExchangeBar` is: every merged message re-renders `App`, and this
 *  strip's only prop changes when the hub's run snapshot does. */
export default memo(RunBar);
