interface Participant {
  label: string;
  /** Two characters, because "Claude" and "Codex" share an initial. */
  badge: string;
}

const KNOWN: Record<string, Participant> = {
  owner: { label: 'You', badge: 'OW' },
  claude: { label: 'Claude', badge: 'CL' },
  codex: { label: 'Codex', badge: 'CX' },
};

export const MENTIONABLE = Object.keys(KNOWN);

export function displayName(authorId: string): string {
  return KNOWN[authorId.toLowerCase()]?.label ?? authorId;
}

export function badgeFor(authorId: string): string {
  return KNOWN[authorId.toLowerCase()]?.badge ?? authorId.slice(0, 2).toUpperCase();
}

/** Drives `--accent` in styles.css, so a participant's colour is the same everywhere it appears. */
export function accentClass(authorId: string): string {
  const id = authorId.toLowerCase();
  return id in KNOWN ? `p-${id}` : 'p-other';
}
