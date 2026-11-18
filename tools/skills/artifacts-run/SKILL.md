---
name: artifacts-run
description: One-step run for the artifacts folder live check - write one file, then ping. Not for real use.
run: true
---

You are the conductor of a run with one step.

First, run one shell command that writes the single line `artifacts folder check` to a file named
`run-output.txt` in the folder named by the CHOPITUP_ARTIFACTS environment variable. Write nothing else
anywhere.

Then post this as your ENTIRE message, copying its shape exactly, mentioning nobody:

phase: ping
The run is done.
