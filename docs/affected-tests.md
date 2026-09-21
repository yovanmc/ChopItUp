# Affected verification

Owner policy, 2026-09-21: affected checks are the normal local and CI gate. A commit or merge is not, by itself, a reason to repeat all tests. This supersedes older instructions to run every suite for every dispatch, commit or release.

```powershell
pwsh -NoProfile -File tools/Invoke-AffectedTests.ps1 -PlanOnly
pwsh -NoProfile -File tools/Invoke-AffectedTests.ps1
pwsh -NoProfile -File tools/Invoke-AffectedTests.ps1 -Base <accepted-base-sha>
pwsh -NoProfile -File tools/Invoke-AffectedTests.ps1 -Full
```

The default base is `main`. Selection includes every commit since the merge base, staged/unstaged changes and untracked files. Deletions and both sides of renames count. Use an explicit accepted base to check already-merged work; a clean default branch has no new work to verify. `-CommittedOnly` is for a clean CI checkout, not unfinished local edits. `-PlanOnly` previews without claiming verification. Execution retains `selection.json` with all changed paths and selection reasons.

Selection follows the actual project-reference graph and runs whole affected test projects. A Core edit reaches Core, Hub and Desktop tests plus the web client; a Hub edit reaches Hub, Desktop and client; a Desktop edit selects Desktop tests. Test-only edits select that test project. This deliberately does not guess individual tests from similar filenames. Maintain `tools/affected-tests.json` when adding projects or dependencies.

Narrative documentation does not run product tests. Unknown paths, missing history, new unmapped references, build/project configuration, CI and verification-runner changes use the full fallback. Shared behavior, schema, authentication, compatibility and cross-process changes also need their relevant integration/adverse-input checks; use `-Full` when their reach is uncertain. Add a mapping and regression before narrowing an unknown path. Source-reading tests and non-project inputs must be represented explicitly, not inferred from compile references alone.

While editing, build the relevant tests with warnings as errors and run focused regressions. CI supplies the final selected project/client gate; do not duplicate that entire gate locally merely before committing or opening a PR. Local final verification is available through the same command when CI is unavailable. Reuse a pass only for the unchanged relevant tree, test command, configuration, fixture and environment. Relevant corrections rerun affected checks. Diagnose failures first; one hypothesis-driven transient rerun is allowed, not automatic repetition until green.

CI always reports the protected `build-and-test` job, including documentation-only changes. It checks GitHub's PR merge tree, cancels obsolete runs of that PR, and retains the selection and TRX evidence. Protected `main` is delivered through this PR gate; the identical merged tree does not start a second push run. A manual Actions run uses the full suite. If direct pushes or a merge queue are introduced, update the triggering and validation policy before using them.

Deployment, installed identity and operating the changed UI remain required when application behavior/artifacts change. Test/guidance-only changes require no application redeployment. Selection is not proof that tests never overlap; removing coverage requires a separate comparison of the behavior each test catches.

Selector regressions: `node --test tools/affected-tests.test.mjs`. Whole-suite test-count floors live in `tools/Invoke-AffectedTests.ps1`; update them in the same change that intentionally changes counts. Missing, failed, skipped or truncated results fail the runner.
