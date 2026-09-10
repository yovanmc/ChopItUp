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
import { readOwnerToken } from './ownerToken';

/** MessageStore.MaxLimit — the largest page the hub will hand back. */
const PAGE_SIZE = 200;
/** A guard, not a policy: it only stops a paging bug from spinning forever. */
const MAX_PAGES = 50;

interface MessagePage {
  messages: Message[];
  nextAfterId: number;
  hasMore: boolean;
}

/** A refusal the hub answered with a status. Row 28: every non-GET `/api` request now needs an owner
 *  credential, and 401 is a failure the owner can FIX — so the client has to be able to tell it from
 *  a 500 it can only report. `unwrap` used to collapse every failure into `new Error(text)` and throw
 *  the status away, which made that distinction unavailable to every caller. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/** The two answers a pasted token can fix: 401 (none sent, or one the hub cannot resolve) and 403 (a
 *  token that resolved to somebody who is not the owner). Everything else is the hub saying no for a
 *  reason no credential will change, and offering the paste prompt for it would be a lie. */
export function isCredentialRefusal(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 401 || error.status === 403);
}

/** The sentence a failure is shown as. For the two credential statuses this deliberately replaces the
 *  hub's own envelope text: `BearerTokenMiddleware` answers `{"error":"unauthorized"}`, which is the
 *  right wire word and tells the owner nothing about what to do next. */
export function describeError(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 401) return 'The hub needs the owner token before it will accept a write from this browser.';
    if (error.status === 403) return "The hub refused that token: it is not the owner's.";
    return error.message;
  }
  if (error instanceof Error) return error.message;
  return String(error);
}

async function unwrap<T>(response: Response): Promise<T> {
  if (!response.ok) throw new ApiError(response.status, await failureText(response));
  return (await response.json()) as T;
}

/** The one door every non-GET request goes through, so "every write carries the owner's credential"
 *  is a property of this module rather than of a dozen call sites remembering to do it — the same
 *  reason `BearerTokenMiddleware` guards by method instead of by route list (row 28 D-28-a). */
function write(url: string, init: RequestInit): Promise<Response> {
  return fetch(url, withOwnerToken(init));
}

/** Attaches the stored owner token, MERGING rather than overwriting. `decideSkillProposal` builds its
 *  own `authorization` from a token it was handed — the value the owner typed onto that card — and a
 *  blanket attach that clobbered it would silently send the stored one instead, so a just-pasted
 *  token would look broken with nothing on screen explaining why. A caller's own header wins, and a
 *  client with no token stored sends none at all rather than the string "Bearer null". */
function withOwnerToken(init: RequestInit): RequestInit {
  const token = readOwnerToken();
  if (token === null) return init;
  const headers = new Headers(init.headers);
  if (headers.has('authorization')) return init;
  headers.set('authorization', `Bearer ${token}`);
  return { ...init, headers };
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
 *  back through `unwrap` as a thrown `ApiError` carrying the hub's own sentence — the dialogs show
 *  `describeError` of it rather than inventing their own wording, which since row 28 means the hub's
 *  sentence for everything except 401/403, where `{"error":"unauthorized"}` is not something a
 *  dialog can usefully show anybody. */
export async function createRoom(name: string, directory: string, signal?: AbortSignal): Promise<Room> {
  return unwrap<Room>(
    await write('/api/rooms', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ name, directory }),
      signal,
    }),
  );
}

export async function archiveRoom(roomId: string, signal?: AbortSignal): Promise<Room> {
  return unwrap<Room>(await write(`/api/rooms/${encodeURIComponent(roomId)}/archive`, { method: 'POST', signal }));
}

export async function unarchiveRoom(roomId: string, signal?: AbortSignal): Promise<Room> {
  return unwrap<Room>(await write(`/api/rooms/${encodeURIComponent(roomId)}/unarchive`, { method: 'POST', signal }));
}

export async function bindDirectory(roomId: string, directory: string, signal?: AbortSignal): Promise<Room> {
  return unwrap<Room>(
    await write(`/api/rooms/${encodeURIComponent(roomId)}/directory`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ directory }),
      signal,
    }),
  );
}

/** Moves the owner's read cursor to the room's last message. Fire and forget: a failure here costs an
 *  unread badge, never a message.
 *
 *  Row 28: it is a write, so it needs the credential like any other, and it fires on every room open
 *  — which is exactly why its refusal must stay quiet at the call site (a prompt here would reopen
 *  itself every time the owner opened a room). It still THROWS an `ApiError`, so App can recognise a
 *  credential refusal and put the reason in the rail instead of nowhere. */
export async function markRead(roomId: string, signal?: AbortSignal): Promise<void> {
  await unwrap<unknown>(await write(`/api/rooms/${encodeURIComponent(roomId)}/read`, { method: 'POST', signal }));
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
    await write(`/api/rooms/${encodeURIComponent(roomId)}/messages`, {
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
    await write(`/api/rooms/${encodeURIComponent(roomId)}/import`, {
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
    await write(`/api/rooms/${encodeURIComponent(roomId)}/exchange/stop`, { method: 'POST', signal }),
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
  return unwrap<MemoryProposal>(await write(`/api/memory/proposals/${id}/${decision}`, { method: 'POST', signal }));
}

/** M25 (D1): reading skill proposals needs no credential — every `GET` on `/api` stays open (row 28
 *  AC2), because a proposal discloses only what the proposer already put there. Deciding one does
 *  need the owner's token; that is `decideSkillProposal` below. */
export async function listSkillProposals(roomId: string, signal?: AbortSignal): Promise<SkillProposal[]> {
  return unwrap<SkillProposal[]>(
    await fetch(`/api/skills/proposals?room=${encodeURIComponent(roomId)}&status=undecided`, { signal }),
  );
}

/** D2, as row 28 leaves it: these two calls are the only ones that take the token as an ARGUMENT —
 *  the value the owner typed onto the card, rather than whatever `localStorage` happens to hold — and
 *  `withOwnerToken` is written to leave a caller's own header alone precisely so this stays true.
 *  Every other write now carries the stored token as well. 401 (no or unresolvable credential), 403
 *  (a credential that is not the owner's) and every 409 refusal come back through `unwrap` as a
 *  thrown `ApiError`; the card shows `describeError`'s sentence for it.
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
  await unwrap<unknown>(await write(`/api/skills/proposals/${proposal.id}/${decision}`, init));
}

/** Undo for a mis-targeted import: drops every PENDING proposal that import created. */
export async function discardImport(source: MemorySource, path: string, signal?: AbortSignal): Promise<number> {
  const result = await unwrap<{ discarded: number }>(
    await write(`/api/memory/proposals?source=${source}&path=${encodeURIComponent(path)}`, { method: 'DELETE', signal }),
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
    await write('/api/memory/import', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ source, path, roomId }),
      signal,
    }),
  );
}
