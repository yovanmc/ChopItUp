import type { Participant } from './types';

/** The roster as the hub reported it. Set once at startup (App.tsx); every lookup below reads it.
 *  Until it arrives, unknown ids fall back to the id itself, so a message never renders blank. */
let roster = new Map<string, Participant>();
let mention: RegExp | null = null;

export function setRoster(list: Participant[]): void {
  roster = new Map(list.map((p) => [p.id.toLowerCase(), p]));
  mention = list.length === 0 ? null : new RegExp(`@(${list.map((p) => escape(p.id)).join('|')})(?!\\.?[\\w-])`, 'gi');
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

export function displayName(authorId: string): string {
  const p = roster.get(authorId.toLowerCase());
  if (!p) return authorId;
  return p.kind === 'human' ? 'You' : p.displayName;
}

/** Two characters. Hosts keep the badges the UI shipped with, so app-backed rows look as they did;
 *  a spawn row takes the initials of its display name ("GPT-6 Astra" → GA, "Opus" → OP). */
const HOST_BADGE: Record<string, string> = { human: 'OW', claude: 'CL', codex: 'CX' };

export function badgeFor(authorId: string): string {
  const p = roster.get(authorId.toLowerCase());
  if (!p) return authorId.slice(0, 2).toUpperCase();
  if (p.model === null) return HOST_BADGE[p.host] ?? p.displayName.slice(0, 2).toUpperCase();
  const words = p.displayName.split(/\s+/).filter(Boolean);
  const initials = words.length >= 2 ? words[0]![0]! + words[1]![0]! : p.displayName.slice(0, 2);
  return initials.toUpperCase();
}

/** Drives `--accent` in styles.css. Colour is per host family: an `opus` row shares the Claude
 *  accent, a `gpt-*` row the Codex accent; name and badge tell rows of one family apart. */
export function accentClass(authorId: string): string {
  const host = hostOf(authorId);
  return host === 'human' ? 'p-owner' : `p-${host}`;
}

export function isHuman(authorId: string): boolean {
  return roster.get(authorId.toLowerCase())?.kind === 'human';
}
