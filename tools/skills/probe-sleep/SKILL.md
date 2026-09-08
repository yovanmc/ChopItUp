---
name: probe-sleep
description: Rescue leg for the MCP timeout probe (row 20, task 5) - one gate that sleeps past the CLI's documented 5-minute idle default, to prove the raised knobs keep the call alive. Not for real use.
run: true
gates: sleep
---

You are the conductor. Call the run_gate tool exactly once with this room's id and gate "sleep", wait for its result, then post a message whose first line is "phase: ping" followed by the gate outcome line from the run record. Mention nobody.
