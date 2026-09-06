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
