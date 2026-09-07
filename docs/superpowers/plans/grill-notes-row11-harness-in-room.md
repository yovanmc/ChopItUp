# Grill notes — row 11: the harness process inside a room (definition ledger)

Interview completed 2026-09-06 (13 decisions, 5 confirmed assumptions, two exit-gate sweeps, frontier empty, owner confirmed). BINDING for rows 11, 19 and 20: a plan for any of them is checked against this file, and a plan that contradicts a decision below is wrong until the owner re-rules. Supersedes D16 of `grill-notes-m5-autonomy.md` and amends its D2, D5 and D7 as stated in D1, D2 and D4 below; every other row of that ledger still binds. Facts are labelled by how they were established; decisions carry no label because they are the owner's.

Paired delete: this file is deleted in the commit that flips the LAST of rows 11/19/20 to DONE.

## The goal (owner ruling 2026-09-06, binding)

The roadmap-driven build process that runs in the Claude Code harness runs inside a Chop It Up room, so the experience is consistent across models. Consensus among the participants first, then ONE designated agent writes. The new harness runs on its own; when needed the owner interacts with it from a mobile device THROUGH a session of another harness that holds the hub MCP tools. Any prior ruling may be overridden to serve this. Constraints: cost no higher than the Claude Code harness for the same milestone; memory management at least as good.

## Decisions

| # | Area | Decision |
|---|------|----------|
| D1 | Conductor | A conductor participant, re-spawned per phase. The owner's `/roadmap <args> @<conductor>` post names a roster row as conductor; the hub re-spawns it whenever the exchange it opened concludes; it reads `ROADMAP.md`, the plan and the hub run record, then posts the next mention. M5-D2 amended: the active run's conductor may root an exchange. Rejected: owner conducts (walk-away covers one phase), hub conducts mechanically (judgment in the loop becomes hub code), a persistent milestone-long process (breaks M5-D7/D9 and needs two vendor protocols kept alive). |
| D2 | Run lifecycle | A run starts on an owner `/roadmap` post in a directory room. It ends on the conductor's phase-end post, the stop button, a `/stop` post, or a hub cap. An owner message mid-run STEERS: the in-flight spawn finishes, then the conductor is woken with that message as its trigger before it advances. M5-D5's "owner message closes the exchange" does not apply inside a run. |
| D3 | Owner proxy | A separate roster row of kind human (`owner-remote`), minted by the existing `--rotate-token` path and configured into the MCP settings of the session the owner drives from a phone. Its posts start and steer runs as the owner's do; the hub stamps them with the proxy name so transcript and trail show which hand typed. Revoking the token cuts the path. `claude`/`codex` app credentials stay model-kind. |
| D4 | Budgets | Exchange budget stays 4 turns; skill phases are cut to fit (build step 1 turn, critique 2, consensus round up to 4). Runs get their own hard-coded caps beside `SpawnLimits.Default` (numbers in D9). Plain exchanges keep M5-D5 and M5-D7 untouched; no skill-declared budgets, no global raise. |
| D5 | Roles | Roster rows gain a class set once by the owner: `plumbing`, `visible`, `judge`. Skill prose pins by class, so it reads the same across vendors. The conductor designates by mention; after a consensus exchange the re-spawned conductor names ONE writer. The run record holds the author row of every artifact path (the trail commit carries it too); a critic's mention names the author. |
| D6 | Artifact home + run state | Workflow artifacts (`ROADMAP.md`, plans, tickets, claim ledger, `docs/LESSONS.md`) live in the room's git tree per the roadmap conventions: a run is a run against that repo. Mechanical run state is a hub table (run id, conductor row, phase count, exchanges opened, spawns used vs caps, author row per artifact path) rendered into every in-run spawn prompt like the memory core. No run-state file. Transcript trim stays character-based (F5). A room without a directory refuses `/roadmap` with a one-line reason; file-free skills such as grilling run anywhere. |
| D7 | Gate scripts | A hub-owned skill is a directory `data/skills/<name>/` holding `SKILL.md`, references and scripts, imported from the harness folder by a hub command. In-run spawns get a fourth MCP tool `run_gate` that runs only a script the skill manifest declares, `pwsh -NoProfile`, cwd = room directory, output capped and returned. The same seam lets the hub run a gate itself. No vendoring per repo, no copying into the room tree. |
| D8 | Enforcement | Every conductor post inside a run opens with a `phase:` line. The hub parses it and checks the mention set against roster classes: a critique phase must mention a judge-class row that is not the artifact's recorded author; a build phase must mention a plumbing or visible row; never the conductor itself. A failing post is refused with the reason and the conductor is re-spawned once with the refusal as trigger; a second refusal in the same phase parks the run and pings the owner. |
| D9 | Ceiling | Per run, hard code: 80 spawns, 8 h wall-clock, 30 min per in-run spawn, the same phase entered at most 3 times. Any cap trips = the run parks and pings the owner; the stop button is the fourth stop. Estimate for a HIGH milestone with 8 tickets ≈ 18 conductor + 21 worker spawns. |
| D10 | Effort | By roster class, hard code in `SpawnCommands`: conductor and judge-class spawns run `--effort high` (Claude) / `-c model_reasoning_effort=high` (Codex); plumbing and visible rows run the model default with no flag. Never xhigh or max. Codex accepted values unverified (F10): the first plan probes it in its claim ledger. Resolves R2. |
| D11 | Split | Three rows in dispatch order: row 11 (id kept) = skill substrate; row 19 = runs; row 20 = the roadmap skill ported with an end-to-end acceptance run. Row 18 memory v1.1 after all three. Each row ships something usable alone and has a real-CLI check like M5/M9/M10. |
| D12 | Acceptance | Row 20 acceptance: first a scratch .NET repo with a seeded `ROADMAP.md` and one lite-path row, run end-to-end; then, as the row's final task, a room bound to the ChopItUp repo runs its own next row driven from the owner's phone session through the proxy credential. The row is not DONE until the second run has shipped. |
| D13 | Skill source of truth | The harness folder `~/.claude/skills/roadmap/` stays canonical; a hub command re-imports it into `data/skills/roadmap/`. Room-specific mechanics (phase tags, `run_gate` names, class mentions, the ping as a post) live in ONE overlay file inside the hub skill folder that the import never touches. DEFERRED, trigger = the D12 dogfood run shipped: move to hub-canonical with a host-neutral core, in a way that does not disturb the other harnesses. |

## Confirmed assumptions (model-proposed, owner said yes 2026-09-06)

| # | Assumption |
|---|------------|
| A1 | The proxy session runs on the hub machine (loopback-only per CLAUDE.md): a Claude Code remote-control session qualifies, a cloud session does not. A constraint, not a feature. |
| A2 | A conductor spawn that times out or exits without posting is retried once with the same trigger; the second silence counts as a phase re-entry under D9. |
| A3 | The run record survives a hub restart; on start every active run is marked parked with a one-line reason and the owner resumes it with a post per D2. Nothing re-spawns on its own after a restart. |
| A4 | Consensus is the conductor's call: when the consensus exchange concludes, the re-spawned conductor judges agreement or one more round under the re-entry cap, then names the writer. No vote mechanism. |
| A5 | The explicitly-later list below, as written. |

## Facts (verified this session unless labelled)

| # | Fact | How |
|---|------|-----|
| F1 | Headless `claude -p --model sonnet --effort low --tools "Read" --setting-sources "" --disable-slash-commands --no-session-persistence` given a PNG path answered "Orange background, number 47": the Read tool returns image content to the model in print mode under the spawn's flag set. R1 resolved: screenshot judging is possible in a room. | Measured 2026-09-06 with a generated 240×120 PNG, Claude Code 2.1.220. |
| F2 | `claude --effort <level>` exists with values `low, medium, high, xhigh, max`; the F1 spawn accepted `--effort low` and ran to completion. | `claude --help`, 2.1.220 (MEASURED). |
| F3 | `codex exec -i, --image <FILE>...` attaches images to the initial prompt (codex-cli 0.153.3). | `codex exec --help` (MEASURED). |
| F4 | Codex reasoning effort is the config key `model_reasoning_effort` (the owner's `~/.codex/config.toml` sets `"high"`); `-c key=value` overrides config.toml keys per `codex exec --help`, and the spawn runs `--ignore-user-config`, so a per-spawn value must be passed as `-c model_reasoning_effort=<level>`. | config.toml + `--help` (MEASURED); effect of `-c` on this key not run. |
| F5 | `SpawnPrompt.Trim` trims by char budget only (`TranscriptChars` 24,000, oldest dropped first, newest always kept); `TranscriptMessages` 60 is declared in `SpawnLimits` but nothing in `Trim` reads it. The 60-message cap in the M5 ledger is not enforced. | Sonnet digest of `src/ChopItUp.Hub/Spawning/SpawnPrompt.cs` L94-105 and `SpawnLimits.cs` L12-18 at ecf9238 (READ, not run). |
| F6 | Claude directory spawns carry a `--settings` deny list: every git write verb as `Bash(git <verb> *)`, `Edit/Write(.git/**)`, and `Read/Write/Edit(~/<folder>/**)` for `.claude .codex .ssh .gnupg .aws .azure .kube .docker`. A spawn cannot Read `~/.claude/skills/**`: the harness scripts are fenced by the deny list, not only by the prompt. Codex directory spawns get NO deny list (prompt rule + trail only). | `SpawnCommands.cs` L84-117, L138-141 (READ). |
| F7 | No effort or reasoning flag is passed by any of the four spawn builders today; `--append-system-prompt` is already used by `ClaudeInDirectory` (L129-136); Codex takes the prompt on stdin. | `SpawnCommands.cs` (READ). |
| F8 | `ExchangePolicy.OnMessage` opens an exchange only for `author.Kind == "human"`; a model post with no open exchange is dropped (L58-59). Directory-room exclusivity is `Due`/`NextWake` with `exclusive` (L87-121). There is no slash parsing, mention autocomplete, skills UI, `data/skills/` directory or skills code anywhere in the repo. | `ExchangePolicy.cs`, `Composer.tsx` grep, `find -iname skills` (READ). |
| F9 | Claude effort precedence: `CLAUDE_CODE_EFFORT_LEVEL` env > `--effort` > saved `modelSettings` > `effortLevel` setting > model default. `--setting-sources ""` drops user/project/local sources, which drops skills, commands, subagents, CLAUDE.md and hooks; `--append-system-prompt` and `--append-system-prompt-file <path>` still apply. | code.claude.com/docs/en/model-config, /cli-reference, /skills, /headless (DOCUMENTED, sonnet research digest). |
| F10 | Codex `model_reasoning_effort` allowed values and the effect of `-c` on it are UNVERIFIED by docs (the reference page returned only the generic `-c key=value` override); F4 rests on the key existing in the owner config. | learn.chatgpt.com/docs/developer-commands?surface=cli (UNKNOWN beyond that). |
| F11 | Harness spend is ~93% builder context re-billing, which a room reproduces one-for-one; the orchestrator share (~32% for Fable-main) is where a per-phase re-spawned conductor spends less than a persistent one, because it is the /clear-between-phases lever applied by construction. | `~/.claude/skills/roadmap/references/cost-model.md` (measured 2026-08-21 there; INFERRED for rooms). |

## Rounds (one question per round, owner's answer in bold)

1. Who conducts: A owner / B conductor participant re-spawned per phase / C hub mechanical. Recommended C, then B after the goal refinement (owner asked how a persistent orchestrator would differ; answered: the harness relocated, blocked by M5-D7/D9, two vendor protocols). Owner: **B** → D1.
2. Owner message mid-run: A ends run / B steers / C mention decides. Owner: **B** → D2.
3. Mobile session speaks as owner: A owner-proxy credential / B elevate app credentials / C `/owner` prefix. Owner: **A** → D3.
4. Budget shape: A keep 4 per exchange + run caps in code / B skill-declared under a ceiling / C global raise. Owner: **A** → D4.
5. Role assignment: A conductor by mention + roster classes / B skill declares by row name / C owner designates. Owner: **A** → D5.
6. Artifact home + run state: A tree artifacts + hub run record / B run-state file / C both. Owner: **A** → D6.
7. Gate scripts: A hub skill folder + `run_gate` / B vendored per repo / C copied into the tree at run start. Owner: **A** → D7.
8. Enforcement: A phase-tagged posts + class rules at post time / B prose only / C caps + author-not-critic. Owner: **A** → D8.
9. Ceiling: A 80 / 8 h / re-entry 3; B 50 / 4 h / 2; C 150 / 24 h / 5. Owner: **A** → D9.
10. Effort: A by class hard code / B skill-declared / C none. Owner: **A** → D10.
11. Split: A three rows / B two / C one. Owner: **A** → D11.
12. Acceptance target: A dogfood ChopItUp / B scratch repo / C scratch then dogfood. Owner: **C** → D12.
13. Skill source of truth: A hub canonical / B harness canonical + overlay, revisit after dogfood / C fork. Owner asked for detail (given). Owner: **B until proven, then A without impacting the other harnesses** → D13.
14. Exit-gate sweep (goal · scope · users · constraints · dependencies · failure modes · prior art · verification · lifecycle · sensitivity · topic) raised A1–A5, put as per-item yes/no. Owner: **all yes**. A second sweep added nothing.

## Explicitly later

Parallel builders and worktree isolation (directory rooms are exclusive) · hook parity beyond the D8 checks · mobile push (M5-F6 stands) · the UIA interactive gate inside rooms · hub-canonical skill (D13 trigger) · restricted-account confinement for both CLIs (row 13, which also covers the Codex no-deny-list asymmetry in F6) · a vote mechanism for consensus.

## Milestone split (rows on the board)

- **Row 11 — Skill substrate** (HIGH: credential kind + prompt injection path). `data/skills/<name>/` store and the import command; slash parse in owner posts and expansion into the exchange; `/api/skills`; composer affordance; roster `class` column; `owner-remote` human-kind credential (D3). Ships grilling and codebase-design as plain skills. Acceptance: `/grill @opus @gpt-6-astra` in a room yields both answers side by side (M5-D19), and a post from a remote-control session through `owner-remote` opens an exchange.
- **Row 19 — Runs** (HIGH: amends exchange policy, new table). Runs table and prompt section (D6); conductor re-spawn on exchange conclusion and the M5-D2 amendment (D1); steer, `/stop`, park, resume (D2, A2, A3); `phase:` tag + class checks (D8); the four caps (D9); effort by class (D10); `run_gate` (D7). Real-CLI check: a two-phase toy skill runs to its ping with no owner post between phases.
- **Row 20 — Roadmap skill ported** (tier set by the plan; MEDIUM expected). Import + overlay file (D13); phases cut to four-turn exchanges (D4); gate manifest for the preflight scripts; ping as a room post; the Codex effort probe (F10). Acceptance per D12: scratch repo run, then the ChopItUp dogfood run from a phone.

Order: 11 → 19 → 20 → 18. Each plan pulls this file by its D/A/F ids.
