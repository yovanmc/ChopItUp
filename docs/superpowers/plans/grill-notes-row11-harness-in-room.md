# Grill notes — row 11: the harness process inside a room (definition ledger)

Interview IN PROGRESS (started 2026-09-06). BINDING for row 11 and the rows it is cut into once the owner confirms the frontier empty. Supersedes D16 of `grill-notes-m5-autonomy.md`; every other D/F row of that ledger still binds. Facts are labelled by how they were established; decisions carry no label because they are the owner's.

Paired delete: this file is deleted in the commit that flips the LAST row cut from row 11 to DONE.

## Owner ruling (the goal, binding, 2026-09-06)

The roadmap-driven build process that runs in the Claude Code harness runs inside a Chop It Up room, so the experience is consistent across models. Consensus among the participants first, then ONE designated agent writes.

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

## Rounds

## Explicitly later

## Milestone split (rows on the board)
