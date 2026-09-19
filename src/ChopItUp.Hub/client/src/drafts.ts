/** Every event that changes what a room's composer holds unsent. */
export type DraftEvent = { kind: 'edit'; roomId: string; text: string } | { kind: 'sent'; roomId: string };

/** Each room keeps its own draft: editing or clearing one never touches another's. */
export function nextDrafts(
  state: Readonly<Record<string, string>>,
  event: DraftEvent,
): Readonly<Record<string, string>> {
  switch (event.kind) {
    case 'edit':
      return { ...state, [event.roomId]: event.text };
    case 'sent':
      return { ...state, [event.roomId]: '' };
  }
}

/** A room that has never been typed in reads the same as one just cleared by a send. */
export function draftFor(state: Readonly<Record<string, string>>, roomId: string): string {
  return state[roomId] ?? '';
}
