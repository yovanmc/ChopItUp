import type { Message, Participant, Room } from './types';

/** MessageStore.MaxLimit — the largest page the hub will hand back. */
const PAGE_SIZE = 200;
/** A guard, not a policy: it only stops a paging bug from spinning forever. */
const MAX_PAGES = 50;

interface MessagePage {
  messages: Message[];
  nextAfterId: number;
  hasMore: boolean;
}

export function describeError(error: unknown): string {
  if (error instanceof Error) return error.message;
  return String(error);
}

async function unwrap<T>(response: Response): Promise<T> {
  if (!response.ok) throw new Error(await failureText(response));
  return (await response.json()) as T;
}

async function failureText(response: Response): Promise<string> {
  try {
    const body = (await response.json()) as { error?: string };
    if (body?.error) return body.error;
  } catch {
    // Not a JSON error envelope; fall through to the status line.
  }
  return `${response.status} ${response.statusText}`.trim();
}

export async function listRooms(signal?: AbortSignal): Promise<Room[]> {
  return unwrap<Room[]>(await fetch('/api/rooms', { signal }));
}

export async function listParticipants(signal?: AbortSignal): Promise<Participant[]> {
  return unwrap<Participant[]>(await fetch('/api/participants', { signal }));
}

/** Reads forward from `afterId` to the end of the room. The hub only pages forward, so the whole
 *  thread is `afterId = 0`; a reconnect passes the last id it already has. */
export async function readMessages(roomId: string, afterId = 0, signal?: AbortSignal): Promise<Message[]> {
  const room = encodeURIComponent(roomId);
  const all: Message[] = [];
  let cursor = afterId;
  for (let page = 0; page < MAX_PAGES; page++) {
    const result = await unwrap<MessagePage>(
      await fetch(`/api/rooms/${room}/messages?afterId=${cursor}&limit=${PAGE_SIZE}`, { signal }),
    );
    for (const message of result.messages) all.push(message);
    if (!result.hasMore || result.nextAfterId === cursor) break;
    cursor = result.nextAfterId;
  }
  return all;
}

export async function postMessage(roomId: string, body: string, signal?: AbortSignal): Promise<Message> {
  return unwrap<Message>(
    await fetch(`/api/rooms/${encodeURIComponent(roomId)}/messages`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ body }),
      signal,
    }),
  );
}

/** D1: the hub authors every imported line as `owner` and leaves the original speaker inside the
 *  body. Nothing here may present them as anyone else. */
export async function importTranscript(roomId: string, text: string, signal?: AbortSignal): Promise<Message[]> {
  const result = await unwrap<{ messages: Message[] }>(
    await fetch(`/api/rooms/${encodeURIComponent(roomId)}/import`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ text }),
      signal,
    }),
  );
  return result.messages;
}

export function exportUrl(roomId: string): string {
  return `/api/rooms/${encodeURIComponent(roomId)}/export`;
}
