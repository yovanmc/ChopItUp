import type { ExchangeSnapshot } from './types';

interface ExchangeBarProps {
  exchange: ExchangeSnapshot | null;
  stopping: boolean;
  onStop: () => void;
}

/** Wiring stub for row 16 task 1: `App.tsx` already threads the snapshot, the `stopping` flag and the
 *  stop callback through, but the real states (working/queued indicators, the turns line, the Stop
 *  button, the concluded/stopped/superseded marker — Acceptance 1-4) are task 2's job. Rendering
 *  `null` keeps every room's layout unchanged until that lands. */
export default function ExchangeBar(_props: ExchangeBarProps): null {
  return null;
}
