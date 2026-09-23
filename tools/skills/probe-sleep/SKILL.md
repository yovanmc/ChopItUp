---
name: probe-sleep
description: Rescue leg for the MCP timeout probe - one gate that sleeps past the CLI's 300-second cut on a silent MCP call, to prove run_gate's progress notifications keep the call alive. Not for real use.
run: true
gates: sleep
---

You are the conductor. Call the run_gate tool exactly once with this room's id and gate "sleep", wait for its result, then post a message whose first line is "phase: ping" followed by the gate outcome line from the run record. Mention nobody.
