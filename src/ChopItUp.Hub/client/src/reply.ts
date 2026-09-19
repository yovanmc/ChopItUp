import type { Message } from './types';

/** Every event that changes what a room's composer is replying to. */
export type ReplyEvent =
  | { kind: 'reply'; message: Message }
  | { kind: 'cancel'; roomId: string }
  | { kind: 'sent'; roomId: string }
  | { kind: 'failed'; roomId: string };

/** Each room keeps its own target. A failed send leaves it in place for a retry; everything else
 *  but Reply clears the room named on the event, leaving every other room's target untouched. */
export function nextReply(
  state: Readonly<Record<string, Message | null>>,
  event: ReplyEvent,
): Readonly<Record<string, Message | null>> {
  switch (event.kind) {
    case 'reply':
      return { ...state, [event.message.roomId]: event.message };
    case 'failed':
      return state;
    case 'cancel':
    case 'sent':
      return { ...state, [event.roomId]: null };
  }
}

/** One line of a message for a quote or the reply chip: whitespace runs (newlines included) become one
 *  space, and a body longer than `max` is cut with a trailing ellipsis. CSS ellipsises it again when the
 *  line is narrower than the snippet. */
export function replySnippet(body: string, max = 80): string {
  const flat = body.replace(/\s+/g, ' ').trim();
  return flat.length > max ? `${flat.slice(0, max)}…` : flat;
}
