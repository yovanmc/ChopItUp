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

/** Row 18 (L6): the hub's words for the hints it computes, because a slug is not a reason. */
const FLAG_TEXT: Record<string, string> = {
  'instruction-like': 'reads like an instruction, not a fact',
  fence: 'contains a memory fence line',
  'from-directory': 'proposed from a room with files and network',
};

/** The owner's approval surface (D15: agents propose, the owner approves in the room). Every undecided
 *  proposal of the open room, oldest first — pending ones with Reject and Approve, and the rare
 *  approved-but-unwritten one (the hub died between marking and writing) with Retry. Renders nothing
 *  when there is nothing to decide, so a room without proposals looks exactly as it did before this row
 *  shipped. Bodies go through the same sanitised markdown as messages; an imported proposal shows where
 *  it came from, so it can never pass for something a model said live in the room.
 *
 *  Row 18 (L6, AC5) puts on the card what the owner needs to judge a proposal rather than merely read
 *  it: which entry approval retires, why the text might be an instruction wearing a fact's clothes, and
 *  the closest entries the topic already holds. All three are absent from a plain proposal's card, so
 *  the ordinary case looks exactly as it did before. */
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
              {p.replaces && (
                <p className="memory-replaces">
                  Replaces <q>{p.replaces}</q> in {p.topic}
                </p>
              )}
              {p.flags.length > 0 && (
                <ul className="memory-flags" aria-label="Review hints">
                  {p.flags.map((f) => (
                    <li key={f} className={`memory-flag memory-flag-${f}`}>
                      {FLAG_TEXT[f] ?? f}
                    </li>
                  ))}
                </ul>
              )}
              <div className="body memory-body" dangerouslySetInnerHTML={{ __html: renderBody(p.body) }} />
              {p.related.length > 0 && (
                <section className="memory-related" aria-label={`Existing entries in ${p.topic}`}>
                  <span className="memory-related-title">Closest entries already in {p.topic}</span>
                  <ul>
                    {p.related.map((r) => (
                      <li key={r.title} className={r.replaced ? 'memory-related-replaced' : undefined}>
                        <span className="memory-related-entry">{r.title}</span>
                        {r.replaced && <span className="memory-related-mark">retired on approval</span>}
                        <span className="memory-related-snippet">{r.snippet}</span>
                      </li>
                    ))}
                  </ul>
                </section>
              )}
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
