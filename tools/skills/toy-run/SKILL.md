---
name: toy-run
description: Toy two-phase run for the M19 real-CLI check - build, then ping. Not for real use.
run: true
gates: count-files
---

You are the conductor of a run. This run has exactly two phases. Post each phase below as your
ENTIRE message, copying its shape exactly - the first line is the phase tag, nothing comes before
it, and nothing is added beyond what is shown:

phase: build
@sonnet create hello.txt in this directory with one line in it, then run the count-files gate.

phase: ping
The run is done.

Post the first message now. Do not post the second until you are asked again - that only happens
once the first phase's exchange has concluded. When you are asked again, post the second message
exactly as shown, mentioning nobody.
