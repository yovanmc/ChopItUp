import type {
  ExchangeSnapshot,
  MemoryImportResult,
  MemoryProposal,
  MemorySource,
  Message,
  Participant,
  Room,
  RunSnapshot,
  Skill,
  SkillProposal,
  Trail,
} from './types';

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

export async function listRooms(includeArchived = false, signal?: AbortSignal): Promise<Room[]> {
  return unwrap<Room[]>(await fetch(includeArchived ? '/api/rooms?archived=true' : '/api/rooms', { signal }));
}

/** M9 room lifecycle. Every refusal (a refused path, a spawn in flight, a room already bound) comes
 *  back through `unwrap` as a thrown `Error` carrying the hub's own sentence — the dialogs show it
 *  verbatim rather than inventing their own wording. */
export async function createRoom(name: string, directory: string, signal?: AbortSignal): Promise<Room> {
  return unwrap<Room>(
    await fetch('/api/rooms', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ name, directory }),
      signal,
    }),
  );
}

export async function archiveRoom(roomId: string, signal?: AbortSignal): Promise<Room> {
  return unwrap<Room>(await fetch(`/api/rooms/${encodeURIComponent(roomId)}/archive`, { method: 'POST', signal }));
}

export async function unarchiveRoom(roomId: string, signal?: AbortSignal): Promise<Room> {
  return unwrap<Room>(await fetch(`/api/rooms/${encodeURIComponent(roomId)}/unarchive`, { method: 'POST', signal }));
}

export async function bindDirectory(roomId: string, directory: string, signal?: AbortSignal): Promise<Room> {
  return unwrap<Room>(
    await fetch(`/api/rooms/${encodeURIComponent(roomId)}/directory`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ directory }),
      signal,
    }),
  );
}

/** Moves the owner's read cursor to the room's last message. Fire and forget: a failure here costs an
 *  unread badge, never a message. */
export async function markRead(roomId: string, signal?: AbortSignal): Promise<void> {
  await unwrap<unknown>(await fetch(`/api/rooms/${encodeURIComponent(roomId)}/read`, { method: 'POST', signal }));
}

export async function getTrail(roomId: string, signal?: AbortSignal): Promise<Trail> {
  return unwrap<Trail>(await fetch(`/api/rooms/${encodeURIComponent(roomId)}/trail`, { signal }));
}

export async function listParticipants(signal?: AbortSignal): Promise<Participant[]> {
  return unwrap<Participant[]>(await fetch('/api/participants', { signal }));
}

/** The installed skill store, for the composer's slash menu. An empty store answers `[]`, and so does
 *  one whose only skill failed its fingerprint check — the hub filters both out, so nothing here has
 *  to. A throw is the composer's cue to show no menu at all rather than an error. */
export async function listSkills(signal?: AbortSignal): Promise<Skill[]> {
  return unwrap<Skill[]>(await fetch('/api/skills', { signal }));
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

export async function getExchange(roomId: string, signal?: AbortSignal): Promise<ExchangeSnapshot> {
  return unwrap<ExchangeSnapshot>(await fetch(`/api/rooms/${encodeURIComponent(roomId)}/exchange`, { signal }));
}

/** 409 on nothing-to-stop and 404 on an unknown room both come back through `unwrap` as a thrown
 *  `Error` carrying the envelope's `error` text, same as every other endpoint here. */
export async function stopExchange(roomId: string, signal?: AbortSignal): Promise<ExchangeSnapshot> {
  return unwrap<ExchangeSnapshot>(
    await fetch(`/api/rooms/${encodeURIComponent(roomId)}/exchange/stop`, { method: 'POST', signal }),
  );
}

/** The room's active-or-most-recent run, or `null` for a room that has never had one — the hub says
 *  that with 204, which has no body to parse, so this is the one endpoint here that cannot go through
 *  `unwrap`. A 404 (unknown room) still throws like everywhere else. */
export async function getRun(roomId: string, signal?: AbortSignal): Promise<RunSnapshot | null> {
  const response = await fetch(`/api/rooms/${encodeURIComponent(roomId)}/run`, { signal });
  if (response.status === 204) return null;
  return unwrap<RunSnapshot>(response);
}

export async function listProposals(roomId: string, signal?: AbortSignal): Promise<MemoryProposal[]> {
  return unwrap<MemoryProposal[]>(
    await fetch(`/api/memory/proposals?room=${encodeURIComponent(roomId)}&status=undecided`, { signal }),
  );
}

/** 404 and 409 come back through `unwrap` as a thrown `Error` with the envelope's text, like every
 *  other endpoint here. */
export async function decideProposal(
  id: number,
  decision: 'approve' | 'reject',
  signal?: AbortSignal,
): Promise<MemoryProposal> {
  return unwrap<MemoryProposal>(await fetch(`/api/memory/proposals/${id}/${decision}`, { method: 'POST', signal }));
}

/** M25 (D1): reading skill proposals needs no credential — `GET` stays unauthenticated like the rest
 *  of `/api`, because a proposal discloses only what the proposer already put there. Deciding one does
 *  need the owner's token; that is `decideSkillProposal` below. */
export async function listSkillProposals(roomId: string, signal?: AbortSignal): Promise<SkillProposal[]> {
  return unwrap<SkillProposal[]>(
    await fetch(`/api/skills/proposals?room=${encodeURIComponent(roomId)}&status=undecided`, { signal }),
  );
}

/** D2: the owner's bearer token goes on these two calls and nowhere else. 401 (no or unresolvable
 *  credential), 403 (a credential that is not the owner's) and every 409 refusal come back through
 *  `unwrap` as a thrown `Error` carrying the hub's own sentence, which the card shows verbatim.
 *
 *  Approve sends back the tree hash the card displayed (D5): the hub compares it to the digest pinned
 *  at propose time and, when they agree, passes that manifest down to `SkillImport.Run`, which checks
 *  the STAGED copy against it before the swap. So the bytes the owner read are the bytes that install.
 *  A Retry sends it too — the hub enforces the check whenever the body carries a hash. */
export async function decideSkillProposal(
  proposal: SkillProposal,
  decision: 'approve' | 'reject',
  token: string,
  signal?: AbortSignal,
): Promise<void> {
  const init: RequestInit =
    decision === 'approve'
      ? {
          method: 'POST',
          headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
          body: JSON.stringify({ treeSha256: proposal.treeSha256 }),
          signal,
        }
      : { method: 'POST', headers: { authorization: `Bearer ${token}` }, signal };
  await unwrap<unknown>(await fetch(`/api/skills/proposals/${proposal.id}/${decision}`, init));
}

/** Undo for a mis-targeted import: drops every PENDING proposal that import created. */
export async function discardImport(source: MemorySource, path: string, signal?: AbortSignal): Promise<number> {
  const result = await unwrap<{ discarded: number }>(
    await fetch(`/api/memory/proposals?source=${source}&path=${encodeURIComponent(path)}`, { method: 'DELETE', signal }),
  );
  return result.discarded;
}

export async function importMemory(
  source: MemorySource,
  path: string,
  roomId: string,
  signal?: AbortSignal,
): Promise<MemoryImportResult> {
  return unwrap<MemoryImportResult>(
    await fetch('/api/memory/import', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ source, path, roomId }),
      signal,
    }),
  );
}
