---
name: goal
description: A goal run. The owner sets one goal in a directory room and leaves; a conductor plans it in GOAL.md, builders make it in named rounds, two fresh judges score it against a rubric, and the conductor presents the result with images.
run: true
---

# Goal run

The owner started this run with `/goal @<conductor> hours: N <goal>`. The goal is the text after the
mentions in that first message. Every spawn in this run is a fresh session with no memory of earlier
turns: `GOAL.md` in the room directory is the run's memory. Read it before anything else, and update
it before you end your turn whenever your role below says so.

Find your role by your own id: the conductor is the participant the owner mentioned in the run's first
message. A participant mentioned in a plan or build post (its tag is `phase: plan` or
`phase: build/r<n>`) is a builder for that phase. A participant mentioned in a critique post
(`phase: critique/r<n>`) is a judge for that round.

Owner messages posted after the start are steers. They outrank this skill's plan and GOAL.md. Every
role reads the newest owner messages before it acts.

## GOAL.md

The plan worker writes it in the room directory, with exactly these sections:

1. **Goal**: the owner's goal, quoted, then one paragraph that restates it.
2. **Done when**: numbered criteria, each one a fact a judge can check by looking at a file.
3. **Rubric**: three to five criteria. Each has a 0 to 10 scale with worded anchors at 0, 5 and 10,
   and the pass mark (7 unless the goal implies another). The run passes when every judge scores
   every criterion at or above its pass mark.
4. **Plan**: the rounds expected, and what each builder does in round one.
5. **Artifacts**: a table of every file the run made: path (as `artifact:<path>` for the artifacts
   folder), what it is, which round made it and who made it.
6. **Rounds**: one entry per round: what was built, the judges' scores and their top findings.
7. **Decisions** and **Open questions**: anything the owner should know or answer.

Keep it under 400 lines. Replace stale content rather than appending history.

## Conductor

Each turn you post exactly one message. Its first line is a phase tag and nothing comes before it.
Use a new phase name for every round (`r1`, `r2`, ...): a phase name may be entered at most three
times. Never mention yourself. Before each post after a critique, record that round's scores and
top findings in GOAL.md's Rounds section.

1. **Plan** (first turn): `phase: plan`, mentioning one builder (`@sonnet`), asking it to write
   GOAL.md from the goal and set up anything the builders need. Name the goal in the post.
2. **Build round**: `phase: build/r<n>`, mentioning `@gpt-6-astra` and `@sonnet`. Give each builder
   its own part in one or two sentences, and in later rounds the judges' findings it must address.
3. **Critique round**: once a build round has concluded, `phase: critique/r<n>`, mentioning
   `@opus-judge` and `@gpt-5.6-sol`, with one `artifact: <path>` line per file to judge (the newest
   renders, never more than six). A judge may not have made a file it judges this round.
4. **Decide** after each critique: when every judge's score passes the rubric, or the run's state
   shows two spawns or less than 20 minutes left, present. Otherwise run the next build round with
   the findings.
5. **Present**: `phase: ping`, mentioning nobody, then:
   - a three to five sentence summary of what was made and whether the rubric passed;
   - each final image inline, as `![<caption>](artifact:<path>)`;
   - the judges' last scores per criterion;
   - the decisions made and the open questions;
   - where every file is (the GOAL.md Artifacts table, copied).

A job a builder started is not finished work: wait for its job note before you critique its output.
If a builder or judge failed, repeat its phase once with the failure named, then present what exists.

## Builders

Do the part the conductor gave you, with the shell and the tools this room allows.

- Read GOAL.md first, then the conductor's post and the newest owner messages.
- Write renders, images and other large or generated files into the artifacts folder named by the
  `CHOPITUP_ARTIFACTS` environment variable, under `renders/` or another clear subfolder. Source and
  scripts that belong to the project go in the room directory.
- Work that takes longer than a few minutes, or uses the GPU, goes to a background job with the hub's
  `start_job` tool (`gpu: true` for GPU work), and then you end your turn. Its output is credited to
  you when it ends, and its end wakes the conductor.
- Before you end, update GOAL.md: your rows in the Artifacts table, and in the plan phase the whole
  file.
- Post one short message: what you made, each file as an `artifact: <path>` line, and anything left
  undone. Do not judge your own work.

## Judges

You are judging work you did not make. You authored nothing this round.

- Read GOAL.md, above all the Rubric and Done when sections.
- Look at every file the conductor named: open each image and read it as an image, not as a
  description of one.
- Score every rubric criterion from 0 to 10 against its worded anchors, with one sentence of evidence
  each. Then list at most five findings, most important first, each one specific enough that a
  builder can act on it.
- Post one message with exactly this shape:

  Scores: <criterion> <n>/10, <criterion> <n>/10, ...
  Pass: yes | no
  Findings:
  1. ...

- Do not edit any file, GOAL.md included: the conductor records your scores.
