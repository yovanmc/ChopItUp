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
  /** Absolute path of the room's git tree, or null for a room made before M9 and not yet bound. */
  directory: string | null;
  archivedAt: string | null;
  /** The newest message's time, or createdAt when there is none — the chat-list order. */
  lastActivityAt: string;
  /** Messages past the owner's read cursor. */
  unread: number;
}

/** One line of `git log` in a room's directory (Web/RoomsApi.cs `GetTrail`). */
export interface TrailCommit {
  hash: string;
  author: string;
  at: string;
  subject: string;
}

/** Mirrors `GET /api/rooms/{id}/trail`. A room with no directory answers `directory: null` and an
 *  empty list; a git call that failed answers an empty list and the failure in `error`. */
export interface Trail {
  directory: string | null;
  commits: TrailCommit[];
  error: string | null;
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

/** Mirrors `GET /api/skills` (Web/SkillsApi.cs). `chars` is the size of the text the hub renders
 *  into every spawn of an exchange the skill roots. `isRun` (row 19) says whether invoking this
 *  skill starts a run. Matches `SkillSummary` exactly. */
export interface Skill {
  name: string;
  title: string;
  description: string;
  chars: number;
  isRun: boolean;
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

/** Who last wrote a path inside a run, read out of the spawn's own git diff (Web/RunsApi.cs). */
export interface RunArtifact {
  path: string;
  authorId: string;
  at: string;
}

/** One recorded `run_gate` call. `exitCode` is null when the gate was refused rather than run. */
export interface RunGate {
  gate: string;
  callerId: string;
  exitCode: number | null;
  outcome: string;
  at: string;
}

/** Mirrors `GET /api/rooms/{id}/run` (Web/RunsApi.cs `RunSnapshot`). A room that has never had a run
 *  answers 204 and `api.getRun` turns that into `null`, so "no run here" is one value, not a throw.
 *
 *  `status` is the whole of `RunStatus` (Core/Model/Run.cs) and nothing else: RunBar's label map is
 *  keyed on this union, so a status added to the hub without a label here is a compile error.
 *
 *  `phaseEntries` counts entries into `phase` only — the cap it is read against is per tag.
 *  `elapsedMinutes` is time the run spent ACTIVE (parked time excluded, frozen while parked, stopped
 *  at `endedAt` once it ends), which is the number the wall-clock cap is spent against. */
export interface RunSnapshot {
  id: number;
  roomId: string;
  conductorId: string;
  skillName: string;
  status: 'active' | 'parked' | 'ended';
  reason: string | null;
  capSpent: boolean;
  phase: string;
  phaseEntries: number;
  phaseEntryCap: number;
  exchanges: number;
  spawnsUsed: number;
  spawnCap: number;
  startedAt: string;
  endedAt: string | null;
  elapsedMinutes: number;
  wallClockCapMinutes: number;
  artifacts: RunArtifact[];
  gateRuns: RunGate[];
  /** Every phase tag the run has entered, with its own entry count. The strip does not draw it — it
   *  is here because the field is on the wire and a type that omits half the payload invites the next
   *  reader to re-derive it. The M19 live check is what reads it. */
  phaseHistory: Record<string, number>;
}

/** One line of the hub's line diff (Core/Memory/MemoryDiff.cs). `skip` is not a line of either file:
 *  it is the label standing in for a run of unchanged lines, or for input the hub cut at its cap. The
 *  text of every op is spawn-authored file content and is rendered as a text node, never as markup. */
export interface MemoryDiffLine {
  op: 'same' | 'add' | 'del' | 'skip';
  text: string;
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
  kind: 'append' | 'supersede' | 'rewrite';
  /** Title of the same-topic entry this proposal retires on approval, or null. */
  replaces: string | null;
  /** Review hints the hub computed at creation: 'instruction-like', 'fence', 'from-directory'. */
  flags: string[];
  /** Up to three live entries of the topic: the replaced one first, then title-word matches. The
   *  hub computes these for `pending` rows only, so a decided proposal carries an empty list. Never
   *  null — a rewrite gets `[]`, and MemoryPanel reads `.length` unguarded. */
  related: { title: string; snippet: string; replaced: boolean }[];
  /* Row 23 (AC7). The list endpoint always sends the five fields below: populated for a `rewrite` that
     is pending or approved-but-unwritten, null/empty/0 for every other row. They are optional here
     because the approve/reject and import responses map the base shape only (MemoryApi.Map), and a
     type that claimed them on those payloads would be claiming something the hub does not send. */
  /** The line diff of the topic file as it is against what approval would write, elided runs included;
   *  null when the row is not a rewrite the owner can still act on. */
  diff?: MemoryDiffLine[] | null;
  /** Live entry titles present in the file today and absent from what approval would write. */
  removedTitles?: string[];
  /** Entry titles in the proposed file that the topic does not hold today. */
  addedTitles?: string[];
  /** Surviving entries whose approval record would NOT carry forward — a renamed heading loses it. */
  provenanceLost?: number;
  /** Whether the hub can make a commit at all. False means the per-proposal backup is the only copy
   *  after approval; null when the hub did not compute it for this row. */
  gitAvailable?: boolean | null;
}

export type MemorySource = 'claude' | 'codex';

/** Mirrors `POST /api/memory/import`. */
export interface MemoryImportResult {
  imported: number;
  skipped: number;
  proposals: MemoryProposal[];
}
