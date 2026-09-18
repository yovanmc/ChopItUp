import { hostOf, recipientsOf } from './participants';
import type { Participant } from './types';

/** Row 43 (D5/AC6): who this draft will actually reach, said before it is sent. Only the @id run at
 *  the start of a draft addresses anyone, and that rule is invisible while typing — so the strip
 *  names the rows a send would spawn, the rows it would reach without spawning, a leading word that
 *  matches nobody, and the ids it read as references instead. It mirrors the reader and nothing else:
 *  for a `/skill` draft it says who the mention addresses, never whether the skill exists.
 *
 *  `role="status"` with a name is what makes the strip findable from outside the browser — a bare div
 *  with a class is invisible to the accessibility tree the UI gate queries — and each chip's visible
 *  text is its accessible name. */
function isSpawnable(p: Participant): boolean {
  return p.kind === 'model' && p.model !== null;
}

function nameList(names: string[]): string {
  if (names.length <= 1) return names[0] ?? '';
  return `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]!}`;
}

export default function RecipientStrip({ draft }: { draft: string }) {
  // Trimmed because that is the body `send` posts: a draft indented before its `/skill` or `phase:`
  // token would otherwise preview a prefix the hub never sees.
  const { recipients, unknown: read, references } = recipientsOf(draft.trim());
  // A word the draft ends with has no separator after it yet, so it is still half-typed. Flagging it
  // would announce "@o matches nobody", then "@op", then "@opu" on the way to a perfectly good @opus.
  const trailing = /@([A-Za-z0-9][A-Za-z0-9_.-]*)$/.exec(draft);
  const halfTyped = read.length > 0 && trailing !== null && read[read.length - 1]!.toLowerCase() === trailing[1]!.toLowerCase();
  const unknown = halfTyped ? read.slice(0, -1) : read;
  if (recipients.length === 0 && unknown.length === 0 && references.length === 0) return null;

  const spawns = recipients.filter(isSpawnable);
  const passive = recipients.filter((p) => !isSpawnable(p));
  const lines: string[] = [];
  if (spawns.length > 0) {
    lines.push(`Sends to ${nameList(spawns.map((p) => p.displayName))}.`);
  } else if (passive.length > 0) {
    const verb = passive.length === 1 ? 'reads' : 'read';
    lines.push(`${nameList(passive.map((p) => p.displayName))} ${verb} this from its own app; the hub spawns nothing.`);
  }
  for (const word of unknown) lines.push(`@${word} matches nobody.`);
  if (recipients.length === 0 && references.length > 0) {
    const named = nameList(references.map((p) => `@${p.id}`));
    lines.push(
      references.length === 1
        ? `Nobody is addressed. ${named} is inside the text, so it is a reference.`
        : `Nobody is addressed. ${named} are inside the text, so they are references.`,
    );
  }

  return (
    <div className="recipient-strip" role="status" aria-label="Recipients">
      {(recipients.length > 0 || unknown.length > 0) && (
        <ul className="recipient-chips" role="list">
          {recipients.map((p) => (
            <li
              key={p.id}
              role="listitem"
              className={`recipient-chip${isSpawnable(p) ? '' : ' passive'}`}
              data-host={hostOf(p.id)}
            >
              {isSpawnable(p) ? p.displayName : `${p.displayName} · not spawned`}
            </li>
          ))}
          {unknown.map((word) => (
            <li key={`?${word}`} role="listitem" className="recipient-chip unknown">
              {`@${word} · no such participant`}
            </li>
          ))}
        </ul>
      )}
      <span className="dispatch-preview">{lines.join(' ')}</span>
    </div>
  );
}
