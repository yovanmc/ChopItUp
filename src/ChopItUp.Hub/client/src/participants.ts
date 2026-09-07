import type { Participant } from './types';

/** The roster as the hub reported it. Set once at startup (App.tsx); every lookup below reads it.
 *  Until it arrives, unknown ids fall back to the id itself, so a message never renders blank. */
let roster = new Map<string, Participant>();
let mention: RegExp | null = null;

/** Client-side twins of `ChopDb.OwnerParticipantId` and `ChopDb.OwnerRemoteParticipantId`. Two rows of
 *  kind `human` since schema v7: the owner at the desk, and the owner's hand on another device (grill
 *  ledger D3). They are the same person — same accent, same `mine` styling in the thread — but the
 *  transcript is supposed to show which hand typed, so name and badge must differ. */
export const OWNER_ID = 'owner';
export const OWNER_REMOTE_ID = 'owner-remote';

/** One place, so nothing below spells the id by hand. Case-folded like every other lookup here: the
 *  roster map is keyed lowercase and an author id arrives however the hub stamped it. */
export function isOwnerRemote(authorId: string): boolean {
  return authorId.toLowerCase() === OWNER_REMOTE_ID;
}

export function setRoster(list: Participant[]): void {
  roster = new Map(list.map((p) => [p.id.toLowerCase(), p]));
  const mentionable = list.filter((p) => p.kind !== 'system');
  mention = mentionable.length === 0 ? null : new RegExp(`@(${mentionable.map((p) => escape(p.id)).join('|')})(?!\\.?[\\w-])`, 'gi');
}

/** Ids like `gpt-5.5` carry regex metacharacters; the alternation must match them literally. */
function escape(id: string): string {
  return id.replace(/[.*+?^${}()|[\]\\-]/g, '\\$&');
}

/** `null` before the roster has loaded: nothing is decorated rather than something wrong. The
 *  lookahead `(?!\.?[\w-])` rejects `@gpt-5.5-x` and `@gpt-5.5.x` (an id continues) but accepts
 *  `@opus.` and `@claude,` (a sentence ends) — the old `\b` accepted the trailing period and so must
 *  this. Callers reset `lastIndex`. */
export function mentionPattern(): RegExp | null {
  return mention;
}

/** The host family an id belongs to, for colour: `human`, `claude`, `codex`, or `other`. */
export function hostOf(authorId: string): string {
  const p = roster.get(authorId.toLowerCase());
  if (!p) return 'other';
  return p.kind === 'human' ? 'human' : p.host === 'claude' || p.host === 'codex' ? p.host : 'other';
}

/** "You" is the owner's own row and nothing else. The remote hand keeps the roster's own label
 *  ("Owner (remote)"), because a post made from the phone rendered as "You" is exactly the thing D3
 *  says the room must not do. */
export function displayName(authorId: string): string {
  const p = roster.get(authorId.toLowerCase());
  if (!p) return authorId;
  if (isOwnerRemote(authorId)) return p.displayName;
  return p.kind === 'human' ? 'You' : p.displayName;
}

/** Two characters. Hosts keep the badges the UI shipped with, so app-backed rows look as they did;
 *  a spawn row takes the initials of its display name ("GPT-6 Astra" → GA, "Opus" → OP). */
const HOST_BADGE: Record<string, string> = { human: 'OW', claude: 'CL', codex: 'CX' };

export function badgeFor(authorId: string): string {
  const p = roster.get(authorId.toLowerCase());
  if (!p) return authorId.slice(0, 2).toUpperCase();
  // Both human rows share host `human` and a null model, so the host badge alone would stamp OW on
  // the remote hand too. One letter apart on purpose: same person, different keyboard.
  if (isOwnerRemote(authorId)) return 'OR';
  if (p.model === null) return HOST_BADGE[p.host] ?? p.displayName.slice(0, 2).toUpperCase();
  const words = p.displayName.split(/\s+/).filter(Boolean);
  const initials = words.length >= 2 ? words[0]![0]! + words[1]![0]! : p.displayName.slice(0, 2);
  return initials.toUpperCase();
}

/** Drives `--accent` in styles.css. Colour is per host family: an `opus` row shares the Claude
 *  accent, a `gpt-*` row the Codex accent; name and badge tell rows of one family apart. Both human
 *  rows keep `p-owner` deliberately — the owner's colour is the owner's colour whichever device they
 *  are on, and the badge and the name are what say which. */
export function accentClass(authorId: string): string {
  const host = hostOf(authorId);
  return host === 'human' ? 'p-owner' : `p-${host}`;
}

export function isHuman(authorId: string): boolean {
  return roster.get(authorId.toLowerCase())?.kind === 'human';
}

/** `hub` and anything else the roster calls `system` is the app narrating the room — an exchange
 *  concluding, a spawn timing out — not a participant in it, and Thread renders those rows without an
 *  avatar or an accent. An id the roster has never heard of is deliberately NOT system: an unknown id
 *  should render as an ordinary row under its own name rather than lose its author to a Hub label. */
export function isSystem(authorId: string): boolean {
  return roster.get(authorId.toLowerCase())?.kind === 'system';
}
