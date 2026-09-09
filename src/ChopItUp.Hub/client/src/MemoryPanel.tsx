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

/** Row 23 (AC7): the name the pre-consolidation copy takes on approval, mirroring `MemoryStore.PathOf`
 *  plus `Rewrite`'s `<file>.rewrite-<id>.bak`. Built here because neither card state this warning can
 *  appear on has written anything yet — `writtenTo` is null on both — and a warning that cannot name
 *  the file is not a warning. */
function backupPath(topic: string, id: number): string {
  return `${topic === 'core' ? 'MEMORY.md' : `topics/${topic}.md`}.rewrite-${id}.bak`;
}

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
          // Row 23 (AC7, ticket 06): a consolidation's body is the whole topic file, so the card shows
          // the change instead. An empty or missing diff falls back to the body — the hub sends null
          // for every other kind, and an empty box would tell the owner less than the raw text does.
          const diff = p.kind === 'rewrite' && p.diff && p.diff.length > 0 ? p.diff : null;
          const removed = p.removedTitles ?? [];
          const lost = p.provenanceLost ?? 0;
          // Finding J: availability is knowable before the write, which is what makes it worth saying.
          // Both card states it can appear on are still approvable — Retry is what performs the write —
          // and on both, nothing has been written yet, so the backup really would be the only copy. AC7
          // scopes the whole card contract to a rewrite "pending or approved-but-unwritten", and
          // `MemoryApi.MapForList` computes `gitAvailable` for exactly that pair.
          const noGit = diff !== null && p.gitAvailable === false;
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
                <div className="memory-flags-group">
                  {/* Visible, not just an aria-label: in the same rounded-pill idiom as the topic chip
                      above, unlabelled hints read as tags the proposing model attached. They are the
                      hub's, and this panel says whose words are whose everywhere else. */}
                  <span className="memory-flags-label">Hub checks</span>
                  <ul className="memory-flags" aria-label="Hub checks">
                    {p.flags.map((f) => (
                      <li key={f} className={`memory-flag memory-flag-${f}`}>
                        {FLAG_TEXT[f] ?? f}
                      </li>
                    ))}
                  </ul>
                </div>
              )}
              {diff ? (
                <>
                  {/* What decides the answer sits above the diff, in the part of the card that never
                      scrolls: the entries this destroys, the approval records it drops, and whether
                      anything but the backup would survive it. */}
                  <div className="memory-rewrite-head">
                    <p className="memory-diff-removes">
                      {removed.length === 0
                        ? 'Removes no entries'
                        : removed.length === 1
                          ? 'Removes 1 entry: '
                          : `Removes ${removed.length} entries: `}
                      {removed.length > 0 && <span className="memory-diff-removed">{removed.join(', ')}</span>}
                    </p>
                    {/* `provenanceLost` counts every live entry whose approval record would not carry
                        forward: a renamed heading loses it and so does a dropped one, so the copy must
                        not call them survivors. It names the records rather than the entries, which is
                        also what keeps it from reading as a second count of the removals above. */}
                    {lost > 0 && (
                      <p className="memory-diff-lost">
                        {lost === 1
                          ? '1 approval record will not carry forward: who approved that entry, and when.'
                          : `${lost} approval records will not carry forward: who approved those entries, and when.`}
                      </p>
                    )}
                    {noGit && (
                      <p className="memory-diff-nogit">
                        No git trail here: once this is written <code>{backupPath(p.topic, p.id)}</code> is the
                        only copy of the current file.
                      </p>
                    )}
                  </div>
                  {/* Every line is spawn-authored file content, so every line is a text node. The op is
                      a class, never markup the diff itself could close. */}
                  <ul className="memory-diff" aria-label={`Changes to ${p.topic}`}>
                    {diff.map((line, i) => (
                      <li key={i} className={`memory-diff-line memory-diff-${line.op}`}>
                        {line.text}
                      </li>
                    ))}
                  </ul>
                </>
              ) : (
                <div className="body memory-body" dangerouslySetInnerHTML={{ __html: renderBody(p.body) }} />
              )}
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
