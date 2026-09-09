import { memo, useState } from 'react';
import { renderBody } from './markdown';
import { accentClass, badgeFor, displayName } from './participants';
import type { SkillFile, SkillProposal } from './types';

interface Props {
  proposals: SkillProposal[];
  busyId: number | null;
  /** True while the room's exchange has spawns in flight: the hub refuses decisions then (409), the
   *  same defence `MemoryPanel` takes, so the buttons say so instead of inviting a click. */
  locked: boolean;
  /** D2: whether the owner has pasted a bearer token. Without one the card is read-only. */
  hasToken: boolean;
  /** The hub's refusal text from the last decision attempt, per proposal id. */
  refusals: Record<number, string>;
  onDecide: (proposal: SkillProposal, decision: 'approve' | 'reject') => void;
  /** A pasted token, or `null` to forget the stored one. */
  onToken: (token: string | null) => void;
}

/** D7's reviewable-text allowlist is `.md .ps1 .psm1 .psd1 .txt .json .yml .yaml`; these three are the
 *  half that EXECUTES. `RunTools.Execute` copies an installed skill and runs `scripts/<gate>.ps1`
 *  under `pwsh -NoProfile -NonInteractive -File`, so approving a skill with a script in it is
 *  authorising that script to run on this machine later, as the owner. The card says that in words
 *  wherever such a file appears. */
const SCRIPT_EXTENSIONS = ['.ps1', '.psm1', '.psd1'];

function isScript(path: string): boolean {
  const lower = path.toLowerCase();
  return SCRIPT_EXTENSIONS.some((extension) => lower.endsWith(extension));
}

function fileCountText(n: number): string {
  return n === 1 ? '1 file' : `${n} files`;
}

function byteText(bytes: number): string {
  if (bytes < 1024) return `${bytes} bytes`;
  return `${(bytes / 1024).toFixed(1)} KiB`;
}

/** The executable files first, then `SKILL.md`, then everything else — each group in the hub's own
 *  ordinal path order. The brief for this card is that the PowerShell is impossible to miss, and the
 *  cheapest way to make something unmissable is to put it where the eye lands first rather than
 *  N screens below a rendered document. The file LIST above keeps the hub's order untouched, so
 *  nothing here changes what the owner is told is in the tree. */
function inAuditOrder(entries: SkillFile[]): SkillFile[] {
  const rank = (f: SkillFile) => (isScript(f.path) ? 0 : f.path === 'SKILL.md' ? 1 : 2);
  return [...entries].sort((a, b) => rank(a) - rank(b) || (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
}

/** One file's whole text. `SKILL.md` is the skill's own document and goes through the same sanitised
 *  markdown pass every message body takes; every other file is a `<pre>` text node and never markdown,
 *  so nothing a proposer wrote can execute, style or restructure this page.
 *
 *  `SKILL.md` additionally shows its LITERAL bytes below the render, because markdown does not show
 *  all of them. Measured against the real `renderBody` 2026-09-09: an HTML comment is dropped outright
 *  and a link's target never appears — `[docs](https://evil.example/x)` renders as the word "docs".
 *  `SKILL.md` is the file whose text is rendered into every spawn of an exchange this skill roots, so
 *  a card that showed only the rendered form would show the owner strictly less than the model
 *  receives, and D7's "the owner is shown every byte" would be untrue of the one file that matters
 *  most. The render stays because it is what makes the document readable. */
function FileBlock({ file }: { file: SkillFile }) {
  const script = isScript(file.path);
  const isSkillDoc = file.path === 'SKILL.md';
  return (
    <li className={`skill-file${script ? ' skill-script' : ''}`}>
      <div className="skill-file-head">
        <code className="skill-file-path">{file.path}</code>
        {script && <span className="skill-file-warn">PowerShell — runs on this machine under pwsh</span>}
      </div>
      {isSkillDoc ? (
        <>
          <div className="body skill-file-body" dangerouslySetInnerHTML={{ __html: renderBody(file.text) }} />
          <div className="skill-file-raw">
            <span className="skill-file-raw-label">
              Literal text, as the hub sends it to every spawn — markdown above hides comments and link targets
            </span>
            <pre className="skill-file-text">{file.text}</pre>
          </div>
        </>
      ) : (
        <pre className="skill-file-text">{file.text}</pre>
      )}
    </li>
  );
}

/** The owner's approval surface for a proposed skill import (M25, D1/D15's shape repeated from
 *  `MemoryPanel`). One card per undecided proposal of the open room.
 *
 *  What this card is FOR: an installed skill is executable, so approving one authorises code. The
 *  whole control is disclosure — the owner sees every byte the install would write, before deciding.
 *  Everything below follows from that and is not decoration:
 *   - every file's full text is on the card, expanded, with no disclosure widget and no truncation
 *     (D7 refuses an over-`MaxSkillChars` file at propose time, so nothing here is un-showable);
 *   - the executable files come first and are labelled as executable;
 *   - the pinned tree hash is shown, because that is the value the approval sends back and the value
 *     the staged copy is checked against before the swap (D5);
 *   - `sourceChanged`/`sourceMissing` are banners, because in both cases what the card CAN show is no
 *     longer what would install, and the hub suppresses the text rather than showing a second read.
 *
 *  `approvable` is the hub's flag, not a rule re-derived here (`SkillsApi.IsApprovable`). */
function SkillPanel({ proposals, busyId, locked, hasToken, refusals, onDecide, onToken }: Props) {
  const [paste, setPaste] = useState('');
  if (proposals.length === 0) return null;

  return (
    <section className="skills" aria-label="Skill proposals">
      <header className="skills-head">
        <span className="skills-title">
          {proposals.length === 1 ? '1 skill proposal' : `${proposals.length} skill proposals`}
        </span>
        <span className="skills-hint">
          {locked
            ? 'A spawn is running; decide when the exchange has finished.'
            : 'An installed skill can be run as a gate. Read every file before approving.'}
        </span>
        {hasToken ? (
          <button type="button" className="quiet skills-forget" onClick={() => onToken(null)}>
            Forget owner token
          </button>
        ) : (
          <form
            className="skills-token"
            onSubmit={(event) => {
              event.preventDefault();
              const value = paste.trim();
              if (value.length === 0) return;
              setPaste('');
              onToken(value);
            }}
          >
            <label className="skills-token-label" htmlFor="owner-token">
              Paste the owner token to decide
            </label>
            <input
              id="owner-token"
              className="field skills-token-field"
              type="password"
              autoComplete="off"
              spellCheck={false}
              placeholder="owner token"
              value={paste}
              onChange={(event) => setPaste(event.target.value)}
            />
            <button type="submit" className="send">
              Use this token
            </button>
          </form>
        )}
      </header>
      <ul className="skills-list">
        {proposals.map((p) => {
          const busy = busyId === p.id;
          const retry = p.status === 'approved' && p.installedAt === null;
          const canDecide = hasToken && !busy && !locked;
          const refusal = refusals[p.id];
          const files = inAuditOrder(p.entries);
          const scripts = files.filter((f) => isScript(f.path)).length;
          return (
            <li key={p.id} className={`skill-card ${accentClass(p.authorId)}`}>
              <div className="skill-meta">
                <span className="avatar skill-avatar" aria-hidden="true">
                  {badgeFor(p.authorId)}
                </span>
                <span className="skill-author">{displayName(p.authorId)}</span>
                {retry && <span className="skill-state">approved, not installed yet</span>}
                <span className="skill-id">#{p.id}</span>
              </div>
              <h3 className="skill-card-title">
                <span className="skill-name">{p.name}</span>
                <span className={`skill-badge ${p.replacesInstalled ? 'skill-badge-replace' : 'skill-badge-new'}`}>
                  {p.replacesInstalled ? 'Replaces the installed skill' : 'New skill'}
                </span>
              </h3>
              <p className="skill-summary">
                {fileCountText(p.fileCount)}, {byteText(p.bytes)}
                {scripts > 0 && (
                  <span className="skill-summary-scripts">
                    {scripts === 1 ? ' · 1 script that can be run' : ` · ${scripts} scripts that can be run`}
                  </span>
                )}
              </p>
              <p className="skill-pin">
                <span className="skill-pin-label">Pinned tree</span> <code>{p.treeSha256}</code>
              </p>

              {p.sourceMissing && (
                <p className="banner skill-banner" role="alert">
                  The proposed source is no longer there, so its files cannot be shown and it cannot be approved.
                  Reject it and propose it again.
                </p>
              )}
              {p.sourceChanged && (
                <p className="banner skill-banner" role="alert">
                  The source has changed since it was proposed. Its files are not shown — what this card could
                  display is no longer what would install — and it cannot be approved. Reject it and propose it again.
                </p>
              )}
              {refusal && (
                <p className="banner skill-banner" role="alert">
                  {refusal}
                </p>
              )}
              {retry && (
                <p className="skill-note">
                  This was approved but the install did not finish — the usual cause is a reader holding the skill
                  store open. Retry finishes it; it re-checks the pinned tree first and never installs twice.
                </p>
              )}

              {/* The gates are named first because they are the executable entry points: everything a
                  gate declares here is something `run_gate` can start under pwsh once this is installed. */}
              <section className="skill-gates" aria-label={`Gates declared by ${p.name}`}>
                <span className="skill-section-title">Gates</span>
                {p.gates.length === 0 ? (
                  <p className="skill-gates-none">Declares no gates.</p>
                ) : (
                  <ul>
                    {p.gates.map((gate) => (
                      <li key={gate.name}>
                        <code className="skill-gate-name">{gate.name}</code>
                        <span className="skill-gate-run">
                          runs <code>scripts/{gate.name}.ps1</code> under pwsh
                          {gate.arguments.length > 0 && (
                            <>
                              {' '}
                              with <code>{gate.arguments.join(' ')}</code>
                            </>
                          )}
                        </span>
                      </li>
                    ))}
                  </ul>
                )}
              </section>

              {p.entries.length > 0 && (
                <>
                  <section className="skill-paths" aria-label={`Files ${p.name} would install`}>
                    <span className="skill-section-title">Installs {fileCountText(p.entries.length)}</span>
                    <ul>
                      {p.entries.map((f) => (
                        <li key={f.path} className={isScript(f.path) ? 'skill-path-script' : undefined}>
                          <code>{f.path}</code>
                        </li>
                      ))}
                    </ul>
                  </section>
                  {/* Expanded, always: no disclosure widget, no truncation, no "show more". Hiding any
                      of this is exactly the failure this card exists to prevent. */}
                  <section className="skill-files" aria-label={`Full text of every file in ${p.name}`}>
                    <span className="skill-section-title">Every file, in full</span>
                    <ul>
                      {files.map((f) => (
                        <FileBlock key={f.path} file={f} />
                      ))}
                    </ul>
                  </section>
                </>
              )}

              <div className="skill-actions">
                {!hasToken && <span className="skill-readonly">Read-only: no owner token pasted.</span>}
                {!retry && (
                  <button type="button" className="quiet" disabled={!canDecide} onClick={() => onDecide(p, 'reject')}>
                    Reject
                  </button>
                )}
                <button
                  type="button"
                  className="send"
                  disabled={!canDecide || !p.approvable}
                  onClick={() => onDecide(p, 'approve')}
                >
                  {busy ? 'Working…' : retry ? 'Retry install' : 'Approve'}
                </button>
              </div>
            </li>
          );
        })}
      </ul>
    </section>
  );
}

export default memo(SkillPanel);
