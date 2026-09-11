---
name: peer-check-vehicle
description: Row 29 live-check vehicle - runs one gate against a planted header, then ends the run. Not for real use.
run: true
gates: present-header(-BaseUrl __BASE_URL__ -RoomId __ROOM_ID__)
---

You are the conductor of a run. This run has exactly one phase.

Run the present-header gate now. When it returns, post the following as your ENTIRE message,
copying its shape exactly - the first line is the phase tag, nothing comes before it, and nothing
is added beyond what is shown:

phase: ping

Post nothing else.
