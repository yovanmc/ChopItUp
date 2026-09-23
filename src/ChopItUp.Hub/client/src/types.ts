/** Mirrors the hub's `/api` JSON (camelCase, see Web/ChatApi.cs). `authorId` is stamped by the hub,
 *  never typed by the writer, including for imported transcripts, which are always `owner`. */
export interface Message {
  id: number;
  roomId: string;
  authorId: string;
  body: string;
  createdAt: string;
  /** The id of the message this one replies to, always in the same room, or null/absent. */
  replyToId?: number | null;
  /** True for a transcript turn brought in by import. The hub stores it as history and never
   *  dispatches anything inside it. */
  imported?: boolean;
}

export interface Room {
  modeSettings?: RoomModeSettings;
  id: string;
  name: string;
  createdAt: string;
  messageCount: number;
  lastMessageId: number;
  /** Absolute path of the room's git tree, or null for a room with no directory bound. */
  directory: string | null;
  archivedAt: string | null;
  /** The newest message's time, or createdAt when there is none — the chat-list order. */
  lastActivityAt: string;
  /** Messages past the owner's read cursor. */
  unread: number;
  /** The room-wide text the hub renders into every spawn here, or null. */
  persona: string | null;
}

export interface RoomModeSettings {
  mode: 'primary' | 'relay' | 'panel';
  first: string;
  second: string | null;
  revision: number;
}

export interface DispatchPreview {
  quote: string;
  mode: string;
  participants: string[];
  turns: number | null;
  commit: string | null;
  error?: string | null;
}

/** One participant's standing text in one room, as `RolesApi` builds it. Mirrors the server shape
 *  exactly (Web/RolesApi.cs `BuildRoomRoles`), field for field.
 *
 *  The three role fields are three different things and collapsing any two of them loses a state the
 *  owner can reach: `role` is the global role the participant carries everywhere; `roomRole` is this
 *  room's override, `null` when none is stored and `''` when the room stores the "no role here"
 *  sentinel (a stored row, not the absence of one); `effectiveRole` is what the hub actually renders
 *  into the prompt, `COALESCE(roomRole, role)`, computed by the server and never re-derived here.
 *
 *  The three read-only roster fields: `model` is the name the host CLI is launched with (never null
 *  on a listed row: unspawnable rows are not listed). `classes` is the normalised set the dispatcher
 *  applies (`ParticipantClasses.Parse`), empty when the row has none. `effort` is the flag those
 *  classes earn inside a run, or null for "no flag, the CLI's default": the server's
 *  `EffortPolicy.ForClasses`, never a rule re-derived here. */
export interface RoleRow {
  id: string;
  displayName: string;
  role: string | null;
  roomRole: string | null;
  effectiveRole: string | null;
  model: string;
  classes: string[];
  effort: string | null;
}

/** Mirrors `GET /api/rooms/{id}/roles` and the answer to every write on it. `participants` holds only
 *  the rows the hub can actually spawn (`ExchangePolicy.IsSpawnable`), so the app-backed `claude` and
 *  `codex` rows are absent: a role stored on them could never render. `conductorEffort` is the effort
 *  a run's conductor is spawned at whatever its classes, sent so the dialog can say so without
 *  carrying the value itself. */
export interface RoomRoles {
  roomId: string;
  persona: string | null;
  conductorEffort: string;
  participants: RoleRow[];
}

/** The narrower answer `POST /api/participants/{id}/role` gives: the participant's new global role,
 *  with no room in the question and so no `effectiveRole` in the answer. */
export interface RoleUpdate {
  id: string;
  displayName: string;
  role: string | null;
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
 *  into every spawn of an exchange the skill roots. `isRun` says whether invoking this skill starts a
 *  run. Matches `SkillSummary` exactly. */
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
  mode?: string | null;
  modeParticipants?: string[] | null;
  preparing?: boolean;
  roomId: string;
  status: 'idle' | 'open' | 'concluded' | 'superseded' | 'stopped';
  rootMessageId: number | null;
  budget: number;
  turnsUsed: number;
  turnsCommitted: number;
  remaining: number;
  inFlight: string[];
  /** Working-chip start instants, keyed only for the live participants above. Optional so a client
   *  talking to an older hub still shows names without inventing an elapsed time. */
  inFlightStartedAt?: Record<string, string>;
  pending: string[];
  seq: number;
  /** The wire name of the `ExchangeStopCause` that stopped this exchange (Spawning/Exchange.cs), and
   *  null whenever nothing has stopped it: an open or concluded exchange, or a snapshot from an older
   *  hub. The whole of the enum and nothing else: ExchangeBar's marker map is keyed on this union, so
   *  a cause added to the hub without a label here is a compile error rather than a bar that silently
   *  blames the hub owner for it. */
  stoppedBy: 'owner' | 'run' | null;
  /** The `continuable` of the exchange these top-level fields describe (the newest open one, else the
   *  newest), on the same rule as every other field here. See `ExchangeView` below. */
  continuable?: boolean;
  /** Every exchange the room still holds, oldest first (Spawning/SpawnerService.cs `ExchangeView`);
   *  the top-level fields above describe the newest open one, else the newest. Optional because an
   *  older hub does not send it, and that hub is still served. */
  exchanges?: ExchangeView[];
}

/** One exchange of a snapshot's `exchanges`. Its `inFlight` is THIS exchange's own live spawns, unlike
 *  the snapshot's room-wide list, which is what lets one strip say whether it alone has anything left
 *  to stop. `status` and `stoppedBy` reuse the snapshot's unions so ExchangeBar's total maps cover
 *  both shapes with one declaration each. */
export interface ExchangeView {
  mode?: string | null;
  modeParticipants?: string[] | null;
  preparing?: boolean;
  rootMessageId: number;
  status: ExchangeSnapshot['status'];
  budget: number;
  turnsUsed: number;
  turnsCommitted: number;
  remaining: number;
  inFlight: string[];
  inFlightStartedAt?: Record<string, string>;
  pending: string[];
  stoppedBy: ExchangeSnapshot['stoppedBy'];
  /** The hub's own decision that `/continue` would be accepted for this exchange: it is rooted at a
   *  human's message, it is not open, nothing of its own is in flight, and no run is active in the
   *  room. The client re-derives none of that; it adds only the live-run gate its Stop already honours.
   *  Optional because an older hub sends no such field, and a bar that read `undefined` as "yes" would
   *  offer a button that hub cannot serve. */
  continuable?: boolean;
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
  /** Every phase tag the run has entered, with its own entry count. The strip does not draw it; it is
   *  typed because the field is on the wire and a type that omits half the payload invites the next
   *  reader to re-derive it. The run live check (tools/Invoke-M19RunCheck.ps1) reads it. */
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
  /* The list endpoint always sends the five fields below: populated for a `rewrite` that is pending
     or approved-but-unwritten, null/empty/0 for every other row. They are optional here because the
     approve/reject and import responses map the base shape only (MemoryApi.Map), and a type that
     claimed them on those payloads would be claiming something the hub does not send. */
  diff?: MemoryDiffLine[] | null;
  /** Live entry titles present in the file today and absent from what approval would write. */
  removedTitles?: string[];
  /** Entry titles in the proposed file that the topic does not hold today. */
  addedTitles?: string[];
  /** Live entries whose approval record would NOT carry forward: the hub matches provenance by exact
   *  heading, so a renamed entry loses its record and so does a removed one. Not a subset of the
   *  survivors, and not a restatement of `removedTitles` — the two sets overlap. */
  provenanceLost?: number;
  /** Whether the hub can make a commit at all. False means the per-proposal backup is the only copy
   *  after approval; null when the hub did not compute it for this row. */
  gitAvailable?: boolean | null;
}

/** One gate a proposed skill declares in its `SKILL.md` frontmatter. `run_gate` resolves it to
 *  `scripts/<name>.ps1` inside the installed tree and runs it under `pwsh` with `arguments` appended
 *  (Mcp/RunTools.cs), which is why the card shows both the declaration and the script's own text. */
export interface SkillGate {
  name: string;
  arguments: string[];
}

/** One file of a proposed skill's source tree: the relative path the install would create, and the
 *  whole of its text. The hub sends every file (propose refuses any extension outside the
 *  reviewable-text allowlist and any file over `SkillStore.MaxSkillChars`, so nothing that reaches a
 *  card is un-showable), or none of them, when `sourceMissing`/`sourceChanged` is set. */
export interface SkillFile {
  path: string;
  text: string;
}

/** Mirrors `GET /api/skills/proposals` (Web/SkillsApi.cs `Row`). An agent proposes a skill over
 *  `propose_skill`; only the hub owner, with a bearer token, may approve or reject it.
 *
 *  `approvable` is the hub's own answer (`SkillsApi.IsApprovable`), computed from the same conditions
 *  `Approve` enforces before it will attempt an install. The card renders that flag; it must never
 *  re-derive one from `sourceMissing`/`sourceChanged`, or the two drift apart the moment the hub adds
 *  a condition.
 *
 *  `treeSha256` is the manifest digest pinned at propose time: the one value the card, the approve
 *  body and the staged copy must all agree on before anything installs, so an approval sends back
 *  exactly the hash of the tree it displayed. */
export interface SkillProposal {
  id: number;
  roomId: string;
  authorId: string;
  name: string;
  /** Whether a skill of this name was already installed when the proposal was recorded. */
  replacesInstalled: boolean;
  /** The `force` the proposer asked for, persisted at propose time rather than derived at approve. */
  force: boolean;
  fileCount: number;
  bytes: number;
  status: 'pending' | 'approved' | 'rejected';
  createdAt: string;
  decidedAt: string | null;
  /** Null while a row is approved but its install has not finished: the Retry state. */
  installedAt: string | null;
  sourceMissing: boolean;
  sourceChanged: boolean;
  approvable: boolean;
  treeSha256: string;
  /** Empty when the hub suppressed the tree (`sourceMissing` or `sourceChanged`), never truncated. */
  entries: SkillFile[];
  gates: SkillGate[];
}

export type MemorySource = 'claude' | 'codex';

/** Mirrors `POST /api/memory/import`. */
export interface MemoryImportResult {
  imported: number;
  skipped: number;
  proposals: MemoryProposal[];
}

/** One row of `GET /api/memory/topics`: the core first, then topics in slug order. */
export interface MemoryFile {
  slug: string;
  /** `MEMORY.md` for the core, `topics/<slug>.md` otherwise. */
  path: string;
  chars: number;
  cap: number;
}

/** `GET /api/memory/topics/{slug}`: the whole file, uncut and LF-normalised, and the hash a save must
 *  echo. */
export interface MemoryFileText extends MemoryFile {
  text: string;
  hash: string;
}

/** `POST /api/memory/topics/{slug}/preview`: the size the hub would write, which is what the cap is
 *  enforced on. */
export interface MemoryPreview {
  slug: string;
  chars: number;
  cap: number;
  over: boolean;
}

/** What a save returns: the approved editor row, the file as the hub wrote it, and where the
 *  pre-edit copy went. */
export interface MemoryEditResult extends MemoryFileText {
  proposal: MemoryProposal;
  backup: string;
}
