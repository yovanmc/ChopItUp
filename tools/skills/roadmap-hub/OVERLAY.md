---
run: true
gates: board-gate, plan-claims, test, start-branch
---
# Room adapter for /roadmap

The skill text above is the delivery core every host shares, with its Risk and review rules. This overlay is the room's adapter, and inside a room it wins where they differ. The files they point at (engineering.md, verification-tiers.md, a host's skills) are outside the room, and this overlay replaces them.

## The room
- You are one spawn in a run, and each spawn is one phase. The hub re-spawns the conductor after every exchange with the run record above as its memory. Durable state is ROADMAP.md, the brief and git. There are no hooks, subagents, owner questions or notifications.
- This replaces the core's Delegation section. The conductor never writes code and a room has no lean builder, so every code change comes from a worker row and the brief is its packet. A worker is a plumbing-class row, or a visible-class row when the owner will see the output. The peers line shows each row's classes.
- The hub commits every spawn's tracked and new files when it ends, even when nothing changed, but never an ignored file. A Claude row's git write verbs are denied. A Codex row's sandbox stops neither git nor the network, so for it the no-push rule is this text alone.
- Gates replace the commands the core names. Call run_gate with the gate name, and the hub runs it in the room directory. `board-gate` checks ROADMAP.md against its schema. `plan-claims` rechecks the claim ledger of each plan a 📝 or 🔨 row names, stages that plan even when it is ignored so the hub commits it, and prints `<n> plan(s) checked`. `test` runs the solution's tests, and the client's npm test when one exists. `start-branch` returns to the default branch (reset to origin when there is one) and checks out `room/m<row>` for the topmost READY row that is not 📝 or 🔨 and has no `room/m<row>` branch yet, printing that branch.

## The row and the stop boundary
- A run works one row: the number in the /roadmap post, else the row `start-branch` picks.
- The run stops at a reviewed room branch. The native owner session (Claude Code or Codex) that picks it up reviews it again, merges, deploys and checks the installed form. The room never merges, pushes, deploys or starts the app, which may be the hub it runs in.
- A defect outside the row never goes into its code. Before the 🔨 flip it becomes one `docs/BUGS.md` line, and after it the ping's punch list carries it.
- Never ask. A question resolves to the repository default, then the punch list, then a park: a `phase: ping` post whose first line after the tag is `PARKED: <one-line why>`, then the punch list. A human message after the root post is a steer: obey it before advancing.

## Spawn 1
Read ROADMAP.md and list its OWNER rows.
- The assigned row is 🔨 and `git rev-parse --abbrev-ref HEAD` prints `room/m<row>`: resume it per the core. Skip `start-branch`, run `board-gate`, `plan-claims` and `test`, then dispatch the build for what the branch does not yet do, or go to the review when it does it all. A 🔨 row with the clone on any other branch parks.
- Otherwise run `board-gate` and `start-branch`, then `test` for the baseline. A FAIL or nonzero exit parks, and so does a `start-branch` that picked a row other than the assigned one.
- Class the row per the core's Risk. Doubt resolves upward, and a surface the repository's instruction file marks HIGH is sensitive. A sensitive row parks with `PARKED: sensitive rows go to a native session`, because the room has no design-review path.

## The brief
At `.scratch/m<row>-<slug>/brief.md`, at most 6 tasks, and every claim a task rests on is a ledger row whose recheck exits 0 while it holds and fails once it does not:
```
# m<row> <title>
Risk: <class>, because <effect>
Sessions: room <id>, base <default-branch hash at spawn 1>
## Acceptance
## Baseline
## Tasks
1. <task>: <files>
## Claim ledger
| # | Claim | Verified at | Recheck |
|---|---|---|---|
| 1 | Greeter defines Greet(string) | <hash> | `pwsh -c "if((gc src/Lib/Greeter.cs -Raw) -match 'static string Greet\(string'){exit 0}else{exit 1}"` |
## Cannot verify here
```

## Worker block (paste verbatim into every post that mentions a worker)
```
Worker rules: read the brief named above. Build test-first and show the failing test before the fix. Run the `test` gate through the run_gate tool before you finish (room_id = this room, gate = test). A nonzero exit is a STOP: report it, never paper over it. Never edit outside your task, never touch ROADMAP.md or the brief, never run git write verbs or push (the hub commits your changes when you finish). Post one report (files touched, tests added, the test gate's exit code, anything you could not verify) and mention nobody.
```

## Tiny and standard rows
Spawn 1 writes the brief, flips the row to 🔨 with the brief in its Plan cell, and runs `board-gate` and `plan-claims`. Anything but exit 0 with `<n> plan(s) checked`, where n is the number of 📝 and 🔨 rows naming a plan, parks. Then it posts `phase: build/<slug> @<worker>` with the brief path, "Do the tasks in order" and the worker block. From here the conductor edits nothing, so the tree it reviews is the tree the owner merges.
Spawn 2 is the worker.
Spawn 3, after the build exchange: you wrote no code, so you are the non-author review. Read the brief and `git diff <base>...HEAD`, open every changed file, and check each acceptance criterion against the diff and the `test` result in the run record. Your verdict is PASS, CHANGES REQUIRED or BLOCKED, each finding `<BLOCKER|MAJOR|MINOR> <file:line>: how it fails, which way to fix it`, closed by `HEAD <short hash> · FILES <changed> · READ <opened>`. CHANGES REQUIRED posts `phase: build/<slug> @<the same worker>` with the findings. The hub stops a phase at its fourth entry. PASS posts the ping with your review in it.

## The ping post
First line `phase: ping`. Then `<Project> row <n> <title>: room/m<n> reviewed at <hash>, base <hash>, review PASS by <reviewer id without @>` (or `PARKED: <why>`), the Risk line, the review's verdict, findings and HEAD line, the punch list, and `Pending for the native owner: its own non-author review (the room's review does not replace it), merge, deploy, installed check`, adding `UI judgment, UIA` when the diff touches UI. Last, `Next: in a native session, git fetch <this room's directory> room/m<n>:room/m<n>, check git diff <hash> room/m<n> is empty (the hub's trail commits add nothing), check git ls-remote origin lists no room/ branch and git merge-base --is-ancestor <hash> origin/main exits 1, then resume row <n>.` Never write an @id in a ping: it mentions nobody, and the hub ends the run on it.
