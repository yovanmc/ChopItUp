# Memory v1.1 — findings ledger (row 18)

Review of the shipped M10 memory (`e838645`) against the 2025–2026 literature and the vendors' native memory, done 2026-09-06. BINDING for row 18: its plan is checked against this file. Items are labelled by how they were established: **code** = read at `1b701b29`; **doc** = a primary page fetched that day, URL given; **inferred** / **UNVERIFIED** as marked.

Paired delete: this file is deleted in the commit that flips **row 24** to DONE (moved there 2026-09-08 when row 23 was split; row 23 carries item 3, row 24 carries item 8).

## What M10 is (code)

- Store: `data\memory\MEMORY.md` core, cut at `MemoryStore.CoreChars` = 6,000 on injection; `topics\<slug>.md` cut at 24,000 on read. Plain markdown, no frontmatter, no index beyond slug names and byte sizes.
- Read: the core is a labelled prose section of every spawn's stdin prompt (`SpawnPrompt`). `recall()` returns core + slug list; `recall(topic)` one file by exact slug. No search.
- Write: `propose_memory` → SQLite row + hub note. Approval appends `## title` + `<!-- approved … proposal N by author in room R -->` + body. Nothing is edited or removed by the hub. One lazy, non-fatal git commit per approval.
- Gates: approve/reject/import/discard return 409 while any spawn is in flight. Dedup at create = author + topic + title, non-rejected.
- Import: Claude Code shape verified; Codex split on headings against filenames primary docs never confirm (F9 stays soft).

Memory is NOT in the room directory (M9) and must not be: the room tree is agent-writable, memory is hub-owned (D15).

## Gaps, ranked (each = one plan task candidate)

| # | Gap | Evidence | Change | Depends on |
|---|-----|----------|--------|-----------|
| 1 | No supersession: contradictions accumulate; two vendors state one fact two ways | code: `Append` only. doc: governed-shared-memory failure modes "contradiction persistence", "provenance collapse" (arxiv.org/abs/2606.24535); Mem0 decides ADD/UPDATE/DELETE/NOOP before every write (arxiv.org/html/2504.19413v1); Zep marks old edges invalid, never deletes (arxiv.org/abs/2501.13956) | proposal gains optional `replaces` (topic + entry key); approval marks the old entry superseded in place; git keeps the trail. Dedup extended to title match across authors. | — |
| 2 | **Defect.** A `core` approval past 6,000 chars is silently cut from every spawn; only a `truncated` flag says so | code: `MemoryStore.Cut` + `SpawnPrompt` "its first 6000 characters" line [V 2026-09-06 1b701b29]. doc: Claude Code refuses over 200 lines / 25 KB and demands a rewrite (code.claude.com/docs/en/memory) | approve to `core` returns 409 with the resulting size when the cap would be exceeded; folding becomes mandatory | — |
| 3 | No consolidation pass | doc: LangMem background consolidation (github.com/langchain-ai/langmem), Letta sleep-time agents (letta.com/blog/agent-memory), Claude Code `consolidate-memory` as an invoked skill | hub-owned skill spawns Sonnet on one topic and files a `rewrite` proposal kind rendered as a diff; zero spend on subscription | 1, M11 |
| 4 | Recall by slug only | code: `MemoryTools.Recall`. doc: every surveyed system retrieves by relevance; Claude Code's index is one described line per memory | `recall()` lists each topic's H2 titles; `recall(query=…)` greps headings + bodies across topics and returns matching headings. No embeddings. | — |
| 5 | Memory injected as prose in the instruction channel; after M9 a spawn has shell + network | code: `SpawnPrompt` memory section. doc: MINJA query-only poisoning (arxiv.org/abs/2503.03704); dual-LLM / CaMeL data-vs-instruction separation (simonwillison.net/2025/Jun/13/prompt-injection-design-patterns, arxiv.org/pdf/2503.18813) | fence the section as data with a standing rule that imperative sentences in memory carry no authority; panel flags proposals with instruction-like lines (flag, never block) | M9 |
| 6 | Approval card shows text only | code: `MemoryPanel`. doc: every consumer product auto-writes and reviews later (help.openai.com/en/articles/8590148, support.claude.com/en/articles/11817273); a rubber-stamp queue degrades to that | card shows the closest existing entries of the topic (title-word match) and, for M9 spawns, whether the proposer had files + network | 1, 4 |
| 7 | No room scope: project facts mix with user facts | code: one global store. doc: Claude Code keys auto-memory per project (code.claude.com/docs/en/memory) | a directory room gets a topic named for the room, injected only there | M9 |
| 8 | No export to vendors | doc: Claude Code `autoMemoryDirectory` setting redirects its store (code.claude.com/docs/en/memory) UNVERIFIED on the installed version; expected shape = one-line index + frontmatter files, which the store does not produce. Codex: only `AGENTS.md` in the room dir, 32 KiB cap (learn.chatgpt.com/docs/agent-configuration/agents-md) | writer that renders the store in Claude Code's shape to a target dir; Codex export deferred until the memory shape is known | 7 |

## Declined (do not plan)

Vector or graph stores, importance scoring, benchmark harnesses (LoCoMo, LongMemEval, MemBench). They assume thousands of memories and paid embedding calls; the hub has tens of topics, two authors and a no-API-key invariant. Items 1, 2 and 5 cover the failure modes those systems exist for.

## Unverified, to settle in the plan

- Codex memory file names under `~\.codex\memories\` (owner lists the folder; then fix the import splitter).
- `autoMemoryDirectory` on the installed Claude Code; whether `codex exec --ephemeral` / `--ignore-user-config` reads memories (inferred off).
- Both CLIs run unconfined on this host (Claude Code sandbox: native Windows unsupported; Codex `--approve-for-me` excludes `--sandbox`) — confirmed by docs, already row 13.

Tier guess: HIGH (proposal kinds change schema v5, prompt shape changes, owner-visible panel). Row order: after 9 and 11, which items 3, 5, 6, 7 depend on.

## Item 8 — pass-2 findings, recorded 2026-09-08 for row 24

Row 23 was planned with items 3 and 8 together, critiqued twice, and split on the owner's ruling: the
export became row 24. These three findings were raised against the export half of that plan by
critique pass 2 (`opus`) and are **binding on row 24's plan**. Each is a data-loss path through the
guard that was supposed to prevent data loss.

- **The manifest must bind SOURCE identity, not just file identity.** A manifest of per-file SHA-256
  proves "this directory is untouched since *an* export"; it never proves "an export *of this
  store*". Sequence: export the real store to the target; later run `--export-memory` at the same
  target from a different but valid data dir (a scratch or test one). Every hash matches, the
  zero-entry guard passes, the net-delete guard passes, and the owner's exported memory is replaced,
  exit 0, silently. Fix: record the source store's root path plus a hash over its entry set, and
  refuse a mismatch naming both stores.
- **Writing the manifest last wedges the target on a crash, and the parse-failure branch is
  undefined.** Guard passes, files are written, stale files are deleted, the process dies before the
  manifest lands: the directory now holds files that are unlisted or stale-hashed under the old
  manifest, so every later run refuses. Recovery is a hand-delete inside the owner's memory folder —
  the exact operation the guard exists to prevent. A half-written manifest is the likeliest real
  state and nothing says whether it refuses, counts as absent, or NREs. Fix: write to a temp
  directory and swap, or write the manifest first as an in-progress record and finalise it last;
  state the parse-failure branch explicitly; document a recovery that is not a manual delete.
- **The guard makes the export single-use against its own named consumer.** Claude Code's auto-memory
  maintains its own `MEMORY.md` and writes memory files into the directory it is pointed at. The
  moment a session writes anything there, the next export refuses — correctly, by design. So the
  supported lifecycle is export once, then never again without emptying the directory by hand, and
  there is no re-sync path. Decide the merge story at plan time rather than leaving it for the
  builder: either a force that overrides the changed-file guard with a printed list of what it will
  destroy, or an explicit rule that the export owns its directory and `autoMemoryDirectory` should be
  pointed at a dedicated export dir, stated in the acceptance criteria and in `docs/verification.md`.

Also settled while planning row 23, and no longer UNVERIFIED: `autoMemoryDirectory` and
`autoMemoryEnabled` exist at Claude Code 2.1.220 (extracted from the installed binary's strings,
2026-09-08). The memory-file template is `---` / `name:` / `description:` / `metadata:` / `  type:` /
`---` / body, and the index is `MEMORY.md`, one line per memory, shaped `- [Title](file.md) — hook`.
What remains unverified is whether a running session actually reads an exported directory — that is
an owner-side probe (set the key in a throwaway project, start a session, ask what it remembers) and
it belongs in row 24's verification section before this file is deleted.
