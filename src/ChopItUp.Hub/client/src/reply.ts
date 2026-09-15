import type { Message } from './types';

/** Every event that changes what the composer is replying to. */
export type ReplyEvent =
  | { kind: 'reply'; message: Message }
  | { kind: 'cancel' }
  | { kind: 'sent' }
  | { kind: 'failed' }
  | { kind: 'roomChanged' };

/** A failed send keeps the target so the owner can retry; everything else but Reply clears it. */
export function nextReply(current: Message | null, event: ReplyEvent): Message | null {
  switch (event.kind) {
    case 'reply':
      return event.message;
    case 'failed':
      return current;
    case 'cancel':
    case 'sent':
    case 'roomChanged':
      return null;
  }
}

/** One line of a message for a quote or the reply chip: whitespace runs (newlines included) become one
 *  space, and a body longer than `max` is cut with a trailing ellipsis. CSS ellipsises it again when the
 *  line is narrower than the snippet. */
export function replySnippet(body: string, max = 80): string {
  const flat = body.replace(/\s+/g, ' ').trim();
  return flat.length > max ? `${flat.slice(0, max)}…` : flat;
}
