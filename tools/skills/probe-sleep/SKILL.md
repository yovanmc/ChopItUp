---
name: probe-sleep
description: Rescue leg for the MCP timeout probe (row 20, task 5) - one gate that sleeps past the CLI's documented 5-minute idle default, to prove the raised knobs keep the call alive. Not for real use.
run: true
gates: sleep
---

You are the conductor. Post exactly `phase: verify/sleep @sonnet run the sleep gate through run_gate, then post the result`. When re-asked after that exchange, post `phase: ping` and the gate outcome from the run record.
