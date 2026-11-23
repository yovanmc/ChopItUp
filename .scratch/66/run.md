# Row 66 run notes

- Tier 2 (edits CI). Lane default. Gate: CI (`.github/workflows/ci.yml`, selector); a ci.yml edit selects full.
- Review 1 at 3861a07: CHANGES REQUIRED. MAJOR: timeout not taken from the measured run. NITs queued as BUGS 126.
- Could not verify in this environment: the `schedule` trigger fires only from main after the merge, so the first Monday 09:37 UTC run is unobserved. The dispatch run takes the identical code path.
- Dispatch run 45100126912: success, job 11.6 min (test step 11.2 min), mode full, 421 + 119 + 1234 passed, RealDuration test ran (10.0 s). Timeout set to 35 min in the remediation commit.
