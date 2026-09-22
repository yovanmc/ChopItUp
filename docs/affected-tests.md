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

The web client is the exception to project-directory matching. `src/ChopItUp.Hub/client/` sits inside the Hub project directory, but a path under it (listed in `clientSubtrees`) selects the client checks, the suites of registered `contractReaders`, and a `dotnet build` of the Hub project, whose `ClientBuild` target typechecks and bundles the client. It does not run Hub or Desktop tests. A Hub test that reads served client output, the client directory or the WebView bridge must be registered in `contractReaders`; one that only mentions `wwwroot`, `index.html`, `ClientOutDir` or the bridge names without depending on the client goes in `nonReaders` with its reason. The selector regression fails on any unclassified mention and on a registration whose file is gone. Today every mention is a reviewed non-reader. Vitest follows static imports only; a client test that reaches a file through a computed import or a raw file read has no edge and must be covered by the whole client run, which every client path already selects.

Narrative documentation does not run product tests. Unknown paths, missing history, new unmapped references, build/project configuration, CI and verification-runner changes use the full fallback. Shared behavior, schema, authentication, compatibility and cross-process changes also need their relevant integration/adverse-input checks; use `-Full` when their reach is uncertain. Add a mapping and regression before narrowing an unknown path. Source-reading tests and non-project inputs must be represented explicitly, not inferred from compile references alone.

While editing, build the relevant tests with warnings as errors and run focused regressions. CI supplies the final selected project/client gate; do not duplicate that entire gate locally merely before committing or opening a PR. Local final verification is available through the same command when CI is unavailable. Reuse a pass only for the unchanged relevant tree, test command, configuration, fixture and environment. Relevant corrections rerun affected checks. Diagnose failures first; one hypothesis-driven transient rerun is allowed, not automatic repetition until green.

Hosted CI is the single final gate. A local run before opening a PR is a working check for the change being edited; it does not repeat the unchanged workload CI will run on the merge tree, and CI does not treat a local pass as a substitute for its own.

CI always reports the protected `build-and-test` job, including documentation-only changes. It checks GitHub's PR merge tree, cancels obsolete runs of that PR, and retains the selection and TRX evidence. Protected `main` is delivered through this PR gate; the identical merged tree does not start a second push run. A manual Actions run uses the full suite. If direct pushes or a merge queue are introduced, update the triggering and validation policy before using them.

Deployment, installed identity and operating the changed UI remain required when application behavior/artifacts change. Test/guidance-only changes require no application redeployment. Selection is not proof that tests never overlap; removing coverage requires a separate comparison of the behavior each test catches.

A test whose subject is a production wait carries `[Trait("Category", "RealDuration")]`. The runner excludes that trait with `--filter Category!=RealDuration` unless `-IncludeRealDuration` is passed, so the shipped duration stays proven without every ordinary run paying it. Nothing passes that switch yet: the periodic whole-suite run is the caller it is there for, and until that run exists these tests are run by hand. Such a test asserts the real wall time against the default and nothing else; the behavior it shares with the fast test belongs in the fast test, which injects a short wait through the production seam. Filtered-out tests never reach the TRX, so the count floors stay meaningful.

Selector regressions: `node --test tools/affected-tests.test.mjs`. Runner regressions, including the trait exclusion: `./tools/Test-AffectedRunner.ps1`. Whole-suite test-count floors live in `tools/Invoke-AffectedTests.ps1`; update them in the same change that intentionally changes counts. Missing, failed, skipped or truncated results fail the runner.
