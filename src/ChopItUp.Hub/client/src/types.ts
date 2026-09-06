/** Mirrors the hub's `/api` JSON (camelCase, see Web/ChatApi.cs). `authorId` is stamped by the hub,
 *  never typed by the writer — including for imported transcripts, which are always `owner` (D1). */
export interface Message {
  id: number;
  roomId: string;
  authorId: string;
  body: string;
  createdAt: string;
}

export interface Room {
  id: string;
  name: string;
  createdAt: string;
  messageCount: number;
  lastMessageId: number;
}

/** Mirrors `GET /api/participants`. `host` is which program speaks for the row; `model` is null for
 *  the human and for app-backed rows. */
export interface Participant {
  id: string;
  displayName: string;
  kind: 'human' | 'model' | 'system';
  host: string;
  model: string | null;
}

/** Mirrors `GET /api/rooms/{id}/exchange` and the `POST .../exchange/stop` response
 *  (Spawning/SpawnerService.cs `ExchangeSnapshot`). `seq` orders a snapshot fetched over HTTP against
 *  whatever `ExchangeChanged` delivers over the socket — never render one with a lower `seq` than
 *  what is already shown for the same room. */
export interface ExchangeSnapshot {
  roomId: string;
  status: 'idle' | 'open' | 'concluded' | 'superseded' | 'stopped';
  rootMessageId: number | null;
  budget: number;
  turnsUsed: number;
  turnsCommitted: number;
  remaining: number;
  inFlight: string[];
  pending: string[];
  seq: number;
}

/** Mirrors `GET /api/memory/proposals` and the approve/reject responses (Web/MemoryApi.cs). */
export interface MemoryProposal {
  id: number;
  roomId: string;
  authorId: string;
  topic: string;
  title: string;
  body: string;
  status: 'pending' | 'approved' | 'rejected';
  source: string | null;
  createdAt: string;
  decidedAt: string | null;
  writtenTo: string | null;
  commitHash: string | null;
}

export type MemorySource = 'claude' | 'codex';

/** Mirrors `POST /api/memory/import`. */
export interface MemoryImportResult {
  imported: number;
  skipped: number;
  proposals: MemoryProposal[];
}
