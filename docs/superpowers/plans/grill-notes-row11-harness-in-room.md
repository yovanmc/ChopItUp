# Grill notes — row 11: the harness process inside a room (definition ledger)

Interview IN PROGRESS (started 2026-09-06). BINDING for row 11 and the rows it is cut into once the owner confirms the frontier empty. Supersedes D16 of `grill-notes-m5-autonomy.md`; every other D/F row of that ledger still binds. Facts are labelled by how they were established; decisions carry no label because they are the owner's.

Paired delete: this file is deleted in the commit that flips the LAST row cut from row 11 to DONE.

## Owner ruling (the goal, binding, 2026-09-06)

The roadmap-driven build process that runs in the Claude Code harness runs inside a Chop It Up room, so the experience is consistent across models. Consensus among the participants first, then ONE designated agent writes.

Refined 2026-09-06 (owner, mid-interview, before Q1 was answered): the new harness runs on its own; when needed the owner interacts with it from a mobile device THROUGH a session of another harness (a Claude Code or Codex session that holds the hub MCP tools). Any prior ruling (D2, D5, D7, D9 included) may be overridden to serve this. Constraints: cost no higher than the Claude Code harness for the same milestone; memory management at least as good.

Consequences noted at the time: (1) a session posting through the `claude`/`codex` credential is `author.Kind` model, so under F8 it cannot open an exchange: the mobile-through-a-harness path needs either an owner-kind credential for that session or an amended D2 (new frontier item). (2) Harness spend is ~93% builder context re-billing (`cost-model.md`), which a room reproduces one-for-one; the orchestrator share (~32% for Fable-main) is where a per-phase re-spawned conductor spends less than a persistent one, because it is the /clear-between-phases lever applied by construction.

## Decisions

| # | Area | Decision |
|---|------|----------|
| D1 | Conductor | A conductor participant, re-spawned per phase (option B). The owner's `/roadmap` post names a roster row as conductor; the hub re-spawns it whenever the exchange it opened concludes; it reads `ROADMAP.md`, the plan and a run-state file in the room tree, then posts the next mention. D2 (M5 ledger) is amended: the active run's conductor may root an exchange. Hub enforces a per-run spawn cap and wall-clock cap in code; the stop button ends the run. Any roster row can conduct. Rejected: owner conducts (walk-away covers one phase), hub conducts mechanically (judgment in the loop becomes hub code). |
| D2 | Run lifecycle | A run starts on an owner `/roadmap <args> @<conductor>` post in a directory room. It ends on the conductor's phase-end ping, the stop button, a `/stop` post, or a hub cap. An owner message mid-run STEERS: the in-flight spawn finishes, then the conductor is woken with that message as its trigger before it advances. D5's "owner message closes the exchange" no longer applies inside a run. |
| D3 | Owner proxy | A separate roster row of kind human (`owner-remote`), minted by the existing `--rotate-token` path and configured into the MCP settings of the session the owner drives from a phone. Its posts start and steer runs as the owner's do; the hub stamps them with the proxy name so transcript and trail show which hand typed. Revoking the token cuts the path. `claude`/`codex` app credentials stay model-kind. |
| D4 | Budgets | Exchange budget stays 4 turns; skill phases are cut to fit (build step 1 turn, critique 2, consensus round up to 4). Runs get their own hard-coded caps beside `SpawnLimits.Default`: spawn timeout inside a run, wall-clock per run, spawns per run. Numbers fixed by D9: 30 min / 8 h / 80. Plain exchanges keep D5 and D7 untouched; no skill-declared budgets, no global raise. |
| D5 | Roles | Roster rows gain a class set once by the owner: `plumbing`, `visible`, `judge`. Skill prose pins by class ("critique to a judge row that did not author the plan"), so it reads the same across vendors. The conductor designates by mention; after a consensus exchange the re-spawned conductor names ONE writer. The run-state file records the author row of every artifact (the trail commit carries it too); a critic's mention names the author. Whether the hub validates mentions against classes is the enforcement decision. |
| D6 | Artifact home + run state | Workflow artifacts (`ROADMAP.md`, plans, tickets, claim ledger, `docs/LESSONS.md`) live in the room's git tree per the roadmap conventions: a run is a run against that repo. Mechanical run state is a hub table (run id, conductor row, phase count, exchanges opened, spawns used vs caps, author row per artifact path) rendered into every in-run spawn prompt like the memory core. No run-state file. Transcript trim stays character-based (F5). A room without a directory refuses `/roadmap` with a one-line reason; file-free skills such as grilling run anywhere. |
| D7 | Gate scripts | A hub-owned skill is a directory `data/skills/<name>/` holding `SKILL.md`, references and scripts, copied from the harness folder by a hub command. In-run spawns get a fourth MCP tool `run_gate` that runs only a script the skill manifest declares, `pwsh -NoProfile`, cwd = room directory, output capped and returned. Same seam lets the hub run a gate itself. No vendoring per repo, no copying into the room tree. |
| D8 | Enforcement | Every conductor post inside a run opens with a `phase:` line. The hub parses it and checks the mention set against roster classes: a critique phase must mention a judge-class row that is not the artifact's recorded author; a build phase must mention a plumbing or visible row; never the conductor itself. A failing post is refused with the reason and the conductor is re-spawned once with the refusal as trigger; a second refusal in the same phase parks the run and pings the owner. D4 caps stay in code. |
| D9 | Ceiling | Per run, hard code: 80 spawns, 8 h wall-clock, 30 min per in-run spawn, the same phase entered at most 3 times. Any cap trips = the run parks and pings the owner; the stop button is the fourth stop. Estimate for a HIGH milestone with 8 tickets ≈ 18 conductor + 21 worker spawns. This confirms the D4 numbers (no longer ASSUMED). |
| D10 | Effort | By roster class, hard code in `SpawnCommands`: conductor and judge-class spawns run `--effort high` (Claude) / `-c model_reasoning_effort=high` (Codex); plumbing and visible rows run the model default with no flag. Never xhigh or max. Codex accepted values unverified (F10): the first plan probes it in its claim ledger. Resolves R2. |
| D11 | Split | Three rows in dispatch order: row 11 (id kept) = skill substrate; new row = runs; new row = the roadmap skill ported with an end-to-end acceptance run. Row 18 memory v1.1 after all three (M5 ledger order). Each row ships something usable alone and has a real-CLI check like M5/M9/M10. |

## Facts (verified this session unless labelled)

| # | Fact | How |
|---|------|-----|
| F1 | Headless `claude -p --model sonnet --effort low --tools "Read" --setting-sources "" --disable-slash-commands --no-session-persistence` given a PNG path answered "Orange background, number 47" — the Read tool returns image content to the model in print mode under the spawn's exact flag set (R1 resolved: screenshot judging is possible in a room). | Measured 2026-09-06 with a generated 240×120 PNG (Claude Code 2.1.220). |
| F2 | `claude --effort <level>` exists with values `low, medium, high, xhigh, max`; the spawn in F1 accepted `--effort low` and ran to completion. | `claude --help`, 2.1.220 (MEASURED). |
| F3 | `codex exec -i, --image <FILE>...` attaches images to the initial prompt (codex-cli 0.153.3). | `codex exec --help` (MEASURED). |
| F4 | Codex reasoning effort is a config key `model_reasoning_effort` (the owner's `~/.codex/config.toml` sets `"high"`); `-c key=value` overrides config.toml keys per `codex exec --help`, and the spawn runs `--ignore-user-config`, so a per-spawn value must be passed as `-c model_reasoning_effort=<level>`. Allowed values UNVERIFIED beyond `high`. | config.toml + `--help` (MEASURED); effect of `-c` on this key not run. |
| F5 | `SpawnPrompt.Trim` trims by char budget only (`TranscriptChars` 24,000, oldest dropped first, newest always kept); `TranscriptMessages` 60 is declared in `SpawnLimits` but nothing in `Trim` reads it. The 60-message cap in the M5 ledger is not enforced. | Sonnet digest of `src/ChopItUp.Hub/Spawning/SpawnPrompt.cs` L94-105 and `SpawnLimits.cs` L12-18 at ecf9238 (READ, not run). |
| F6 | Claude directory spawns carry a `--settings` deny list: every git write verb as `Bash(git <verb> *)`, `Edit/Write(.git/**)`, and `Read/Write/Edit(~/<folder>/**)` for `.claude .codex .ssh .gnupg .aws .azure .kube .docker`. So a spawn cannot Read `~/.claude/skills/**`: the harness scripts are fenced by the deny list, not only by the prompt. Codex directory spawns get NO deny list (prompt rule + trail only). | `SpawnCommands.cs` L84-117, L138-141 (READ). |
| F7 | No effort or reasoning flag is passed by any of the four spawn builders today; `--append-system-prompt` is already used by `ClaudeInDirectory` (L129-136), Codex takes the prompt on stdin. | `SpawnCommands.cs` (READ). |
| F8 | `ExchangePolicy.OnMessage` opens an exchange only for `author.Kind == "human"`; a model post with no open exchange is dropped (L58-59). Directory-room exclusivity is `Due`/`NextWake` with `exclusive` (L87-121). There is no slash parsing, mention autocomplete, skills UI, `dataskills` directory or skills code anywhere in the repo. | `ExchangePolicy.cs`, `Composer.tsx` grep, `find -iname skills` (READ). |
| F9 | Claude effort precedence: `CLAUDE_CODE_EFFORT_LEVEL` env > `--effort` > saved `modelSettings` > `effortLevel` setting > model default. `--setting-sources ""` drops user/project/local sources, which drops skills, commands, subagents, CLAUDE.md and hooks; `--append-system-prompt` and `--append-system-prompt-file <path>` still apply. | code.claude.com/docs/en/model-config, /cli-reference, /skills, /headless (DOCUMENTED, sonnet research digest). |
| F10 | Codex `model_reasoning_effort` allowed values and the effect of `-c` on it are UNVERIFIED by docs (the reference page returned only the generic `-c key=value` override); F4 rests on the key existing in the owner config. | learn.chatgpt.com/docs/developer-commands?surface=cli (UNKNOWN beyond that). |

## Rounds

## Explicitly later

## Milestone split (rows on the board)

### Round 1
- Q1 Who conducts: A owner / B conductor participant re-spawned per phase / C hub mechanical. Recommended C, then B after the goal refinement. Owner: **B**. ANSWERED → D1.
- Q2 Owner message mid-run: A ends run / B steers / C mention decides. Recommended B. Owner: **B**. ANSWERED → D2.
- Q3 Mobile session speaks as owner: A owner-proxy credential / B elevate app credentials / C /owner prefix. Recommended A. Owner: **A**. ANSWERED → D3.
- Q4 Budget shape: A keep 4/exchange + run caps in code / B skill-declared under ceiling / C global raise. Recommended A (30 min / 8 h / 80). Owner: **A**. ANSWERED → D4; numbers ASSUMED pending the cost question.
- Q5 Role assignment: A conductor by mention + roster classes / B skill declares by row name / C owner designates. Recommended A. Owner: **A**. ANSWERED → D5.
- Q6 Artifact home + run state: A tree artifacts + hub run record / B run-state file / C both. Recommended A. Owner: **A**. ANSWERED → D6.
- Q7 Gate scripts: A hub skill folder + run_gate tool / B vendored per repo / C copied into tree at run start. Recommended A. Owner: **A**. ANSWERED → D7.
- Q8 Enforcement: A phase-tagged posts + class rules checked at post time / B prose only / C caps + author-not-critic. Recommended A. Owner: **A**. ANSWERED → D8.
- Q9 Ceiling: A 80 / 8 h / re-entry 3; B 50 / 4 h / 2; C 150 / 24 h / 5. Recommended A. Owner: **A**. ANSWERED → D9, D4 numbers confirmed.
- Q10 Effort: A by class hard code / B skill-declared / C none. Recommended A. Owner: **A**. ANSWERED → D10.
- Q11 Split: A three rows / B two / C one. Recommended A. Owner: **A**. ANSWERED → D11.
