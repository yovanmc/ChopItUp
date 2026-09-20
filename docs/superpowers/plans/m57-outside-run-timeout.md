# Milestone 57: outside-run spawn timeout

Directory-room spawns outside a run have a 30-minute process timeout. Plain-room spawns outside a run keep five minutes. An active run always uses its existing 30-minute spawn timeout. The selected limit is captured once per launch, so the process runner and any timeout note agree. Owner Stop still cancels the process tree immediately.

The working chip shows elapsed working time from when the hub marks the spawn in flight. It is not a measure of model execution or billing. A local client clock advances the label without server broadcasts. A missing or invalid start leaves the plain chip; a disconnected client labels the chip as last known until a fresh snapshot arrives. A completed spawn removes its timestamp with the chip. Per-exchange timestamps keep superseded spawns attributed to their own strip.

Acceptance uses a fake process runner and synthetic rooms to verify directory/plain/run limits, timeout notes, Stop cancellation, snapshot timestamp ownership and removal. Client checks cover elapsed formatting, unavailable starts and connection state. A scratch UI session exercises a ticking chip, Stop, completion and reconnect without a real model call.
