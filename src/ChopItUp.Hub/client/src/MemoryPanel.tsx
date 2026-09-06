import { memo } from 'react';
import { renderBody } from './markdown';
import { accentClass, badgeFor, displayName } from './participants';
import type { MemoryProposal } from './types';

interface Props {
  proposals: MemoryProposal[];
  busyId: number | null;
  /** True while the room's exchange has spawns in flight: the hub refuses decisions then (409), so
   *  the buttons say so instead of inviting a click that only produces a banner. */
  locked: boolean;
  onDecide: (id: number, decision: 'approve' | 'reject') => void;
}

/** The owner's approval surface (D15: agents propose, the owner approves in the room). Every undecided
 *  proposal of the open room, oldest first — pending ones with Reject and Approve, and the rare
 *  approved-but-unwritten one (the hub died between marking and writing) with Retry. Renders nothing
 *  when there is nothing to decide, so a room without proposals looks exactly as it did before this row
 *  shipped. Bodies go through the same sanitised markdown as messages; an imported proposal shows where
 *  it came from, so it can never pass for something a model said live in the room. */
function MemoryPanel({ proposals, busyId, locked, onDecide }: Props) {
  if (proposals.length === 0) return null;
  return (
    <section className="memory" aria-label="Memory proposals">
      <header className="memory-head">
        <span className="memory-title">
          {proposals.length === 1 ? '1 memory proposal' : `${proposals.length} memory proposals`}
        </span>
        <span className="memory-hint">
          {locked
            ? 'A spawn is running; decide when the exchange has finished.'
            : 'Approve writes the entry to the shared memory. Reject drops it.'}
        </span>
      </header>
      <ul className="memory-list">
        {proposals.map((p) => {
          const busy = busyId === p.id;
          const unwritten = p.status === 'approved';
          return (
            <li key={p.id} className={`memory-card ${accentClass(p.authorId)}`}>
              <div className="memory-meta">
                <span className="avatar memory-avatar" aria-hidden="true">
                  {badgeFor(p.authorId)}
                </span>
                <span className="memory-author">{displayName(p.authorId)}</span>
                <span className="memory-topic" title="Topic">
                  {p.topic}
                </span>
                {unwritten && <span className="memory-state">approved, not written yet</span>}
                <span className="memory-id">#{p.id}</span>
              </div>
              <h3 className="memory-card-title">{p.title}</h3>
              {p.source && <p className="memory-source">imported from {p.source}</p>}
              <div className="body memory-body" dangerouslySetInnerHTML={{ __html: renderBody(p.body) }} />
              <div className="memory-actions">
                {!unwritten && (
                  <button
                    type="button"
                    className="quiet"
                    disabled={busy || locked}
                    onClick={() => onDecide(p.id, 'reject')}
                  >
                    Reject
                  </button>
                )}
                <button
                  type="button"
                  className="send"
                  disabled={busy || locked}
                  onClick={() => onDecide(p.id, 'approve')}
                >
                  {busy ? 'Working…' : unwritten ? 'Retry write' : 'Approve'}
                </button>
              </div>
            </li>
          );
        })}
      </ul>
    </section>
  );
}

export default memo(MemoryPanel);
