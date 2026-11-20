# Row 85 run notes

Branch row-85-background-jobs, created from main 28d54db. Design: design.md (risk lane).

## Punch list

## Residue records

- Branch row-85-background-jobs (local).

## Could not verify in this environment

## Design review

- dissect-critic pass 1: FIX-THEN-SHIP, 5 MAJOR, 8 DEFECT, 3 NIT. All folded into design.md "Pass 1 dispositions". No second pass (risk lane rule).

## Review

- code-reviewer dispatch 1: VOID (READ 39 of 46). Its findings were fixed before dispatch 2.
- Flake found while fixing: .NET 10 runs all of BackgroundService.ExecuteAsync on a pool thread, so the startup read of lost jobs could see a job this hub had just started and end it as lost. The read moved into StartAsync. Regression test A_job_started_just_after_the_runner_starts_is_never_recovered_as_lost went red 3 of 3 before the fix.
