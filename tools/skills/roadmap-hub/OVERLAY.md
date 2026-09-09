---
run: true
gates: board-gate, plan-claims, test, start-branch, finish-branch
---
# Room overlay for /roadmap

The skill text above is the harness workflow, unchanged. This overlay maps it onto a hub run. Where the two disagree inside a room, this overlay wins; where this overlay is silent, the skill text stands.

## What is different in a room
- You are one spawn in a run. Each spawn is one phase; the hub re-spawns the conductor after every exchange with the run record above as its memory. Durable state is ROADMAP.md, the brief or plan, tickets and git. There is no /clear, no hooks, no PushNotification, no AskUserQuestion, no Agent tool, no subagents.
- Tools: Read, Edit, Write, Glob, Grep, Bash, plus post_message, recall, propose_memory, propose_rewrite and run_gate. Git write verbs are denied to you. The hub commits your diff when your spawn ends, authored as you. Branch work is done by gates.
- Dispatch is a mention by class, one row per phase, and each phase is one exchange of at most 4 turns. "Dispatch a sonnet builder" means: mention one plumbing-class row. "Opus for what the owner sees" means: mention one visible-class row. "dissect-critic" means: mention one judge-class row that is not the artifact's recorded author. Never mention yourself. Workers mention nobody.
- Gates replace the pwsh commands the skill names. Call run_gate with the gate name; the hub runs it in the room directory and posts the outcome. Gates: `board-gate` (Check-RoadmapBudget on ROADMAP.md), `plan-claims` (Check-PlanClaims on every 📝/🔨 row's plan), `test` (the repo's solution tests, plus the client's npm test when one exists), `start-branch` (checks out `room/m<row>` for the topmost READY row), `finish-branch` (commits what is pending, then merges per the repo's flow: PR + checks + squash when a remote exists, local --no-ff merge otherwise, and prints the merged hash).
- Never ask. A question resolves repo-default → punch list in the ping → park. Park is a `phase: ping` post whose first line after the tag is `PARKED: <one-line why>`, followed by the punch list. If the text above the skill fence tells you this is your last turn and to ask the owner whether to continue, that sentence does not apply to a conductor: your turn always ends in a `phase:` post, never a question. A human message after the root post is a steer: obey it before advancing.
- Never deploy inside a run. The hub you run in may be the app being deployed. The ping names deploy as the next harness step when the repo has a launch dir.
- The run always works the topmost READY row. The arguments of the /roadmap post are free text for you (a hint such as `lite`), never a row selector: the start-branch gate names its branch from the topmost READY row and the two must agree.
- The tier fork holds: LOW and MEDIUM take the lite path below. HIGH takes the full path below. Doubt resolves upward. A lite-path run never calls `plan-claims`.
- Workers do not see this text. Everything a worker must know travels in your post: paste the worker block below into every build, verify and critique post, after your instruction.

## Worker block (paste verbatim into every post that mentions a worker)
```
Worker rules: read the brief or ticket named above. Build test-first, quietly. Run the `test` gate through the run_gate tool before you finish (room_id = this room, gate = test); a nonzero exit is a STOP: report it, do not paper over it. Never edit outside your task; never touch ROADMAP.md; never run git write verbs (the hub commits your diff when you finish, authored as you). Post one report (files touched, tests added, test gate exit code, anything you could not verify) and mention nobody. A judge asked to review posts findings with file:line and ends with `VERDICT: SHIP|FIX-THEN-SHIP|REFRAME`.
```

## Lite path (LOW, MEDIUM): three conductor spawns, one worker
Spawn 1, trigger = the /roadmap post. Read ROADMAP.md. List OWNER rows. Take the topmost READY row. Run `board-gate`; a FAIL parks. Run `start-branch`; a nonzero exit parks. Measure the baseline with the `test` gate. Write the brief at `.scratch/m<row>-<slug>/brief.md` per the skill (acceptance, baseline, ≤ 6 tasks with files, lessons consulted, could-not-verify). Flip the row to 📝 / READY with the brief path in the Plan cell, and run `board-gate` again. Post:
```
phase: build/<slug> @<one plumbing-class row, or a visible-class row when the owner will see the output>
Brief: .scratch/m<row>-<slug>/brief.md. Do the tasks in order.
<the worker block>
```
Spawn 2 is the worker.
Spawn 3, trigger = the build exchange concluded. Read the brief, then `git log --oneline -5` and `git show --stat HEAD` (read verbs are allowed). Check every acceptance criterion against the diff and the test gate result in the run record. Not met → post `phase: build/<slug>` again with the punch list (the fourth entry of the same phase parks the run; say so in the punch list). Pick workers from the peers line above, which shows each row's classes: a build mention must be a plumbing- or visible-class row, never a row with no class. Met → flip the row to ✅ / DONE with Notes `Merged by room run; hash in git log and in the ping.` (never a hash placeholder: the merge has not happened yet), delete the previous ✅ row, delete the brief directory, run `board-gate`, then run `finish-branch` exactly once. A nonzero exit parks with the gate's tail in the ping. Otherwise the hash the gate printed goes into the ping, and nothing is edited after the merge. Post the ping.

## Full path (HIGH): defined here, exercised by a later row
Consensus `phase: plan/consensus @<judge> @<visible>` (≤ 4 turns; each participant posts one position). Writer `phase: plan/write @<visible>` (the plan per the skill, claim ledger measured in-room, tickets under .scratch). Critique `phase: critique/pass-1` with `artifact: docs/superpowers/plans/<file>` and `@<judge not the author>`. Fold `phase: plan/fold @<the writer>`. Critique `phase: critique/pass-2` (another judge, or the same judge told what pass 1 found). Preflight: the conductor runs `plan-claims`. Build: one `phase: build/<ticket>` per ticket frontier, review each with `git show` and, for MEDIUM and HIGH, `phase: verify/review @<judge>` on the branch diff. Verify: `test`. Finish as in the lite path.

## The ping post
First line `phase: ping`. Then `<Project> row <n> <title>: merged <hash>` (or `PARKED: <why>` and the punch list), the count line `Owner items: A n / B 0 / C n`, deploy pending or not, then `Next: post /roadmap in this room, mentioning the conductor row, to plan the next row.` Never write an @id anywhere in a ping: a conductor post that mentions the conductor is refused, and a ping mentions nobody. The hub ends the run on this post.
