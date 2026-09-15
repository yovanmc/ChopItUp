# Row 35: a git worktree per exchange in a directory room, merged on close

**Goal:** two exchanges in a directory room edit the room's repository at the same time, each in its own git worktree on its own branch, and each branch is merged back into the room directory's checked-out branch when its exchange closes; a conflict leaves the branch and posts a note naming it.

**Architecture:** `GitTrail` learns worktree, merge and branch primitives and stops assuming `.git` is a directory. A new `ExchangeWorktrees` service owns the path and branch names, creation (`EnsureAsync`), close-and-merge (`CloseAsync`) and start-up recovery (`RecoverAsync`), all through `RoomTrails`, so every trail over one repository shares one write gate. `SpawnerService` launches a non-run directory-room spawn with its working directory in the exchange's worktree, gates exclusivity per exchange instead of per room, and hands a closed exchange to `CloseAsync` off the loop thread, posting the returned note when a `WorktreeClosedEvent` comes back.

**Author model:** Opus 5 (claude-opus-5). Session routing for a HIGH plan is Fable; mismatch declared, so critique pass 2 is mandatory.

**Blast radius: HIGH.** It merges into, removes worktrees from and deletes branches in the owner's repositories (delete/replace paths), and changes a cross-process contract (the working directory a spawned CLI edits). No schema or serialized type changes.

*Written for builder-subagent execution; if something doesn't match, STOP and report rather than guess.*

## Acceptance

1. WHEN two exchanges with distinct participants are open in a directory room and no run is active or parked there THE SYSTEM SHALL run a spawn of each at the same time, and SHALL still run at most one spawn at a time within one exchange.
2. WHEN a spawn of such an exchange launches THE SYSTEM SHALL first commit the owner's uncommitted edits in the room directory, then run the CLI with its working directory at `<room dir>.worktrees\x<root>` on branch `chopitup/x<root>` created from the room directory's HEAD; the after-spawn commit SHALL land on that branch and not on the room directory's branch.
3. WHEN such an exchange is closed (concluded or superseded) with no spawn of it in flight THE SYSTEM SHALL remove its worktree, commit the owner's uncommitted edits in the room directory as the owner, merge its branch into the room directory's checked-out branch with a `--no-ff` merge commit, delete the branch, and post a hub note naming the merge commit.
4. WHEN that merge conflicts, fails or is interrupted THE SYSTEM SHALL abort it so the room directory's HEAD and working tree are what they were before the merge, keep the branch, and post a hub note naming the branch and the conflicted paths; and no hub or owner commit SHALL ever be made while a repository operation (merge, cherry-pick, revert, rebase, or any unmerged index entry) is in progress.
5. WHEN such an exchange closes because the owner stopped it, or any spawn of it was cancelled or timed out, or a run is active or parked in the room at close, or the room directory has no branch checked out, THE SYSTEM SHALL remove the worktree, keep the branch unmerged, and post a hub note naming the branch and the reason.
6. WHILE a run is active in the room THE SYSTEM SHALL launch that room's spawns in the room directory itself with at most one spawn in flight across the room, exactly as before this row.
7. WHEN the hub starts and a directory room's worktrees folder holds registered exchange worktrees (`x<digits>`) THE SYSTEM SHALL commit any uncommitted changes in each as the hub, remove each, delete those branches already contained in the room's HEAD, keep the rest, and post one hub note in that room naming the kept branches; a merge in progress in the room directory SHALL be aborted only when it is the hub's own exchange merge, and left alone otherwise.
8. IF an exchange's worktree cannot be created THEN THE SYSTEM SHALL NOT start that spawn in any directory, the room SHALL get a hub note saying the participant was not started and why, and the exchange's close SHALL NOT merge or delete any branch.
9. WHILE a worktree close is running for a room THE SYSTEM SHALL NOT launch a spawn in that room's directory itself, and WHILE a spawn is in flight in the room directory itself (including one cancelled but not yet ended) THE SYSTEM SHALL NOT start a worktree close or launch a worktree spawn for that room.
10. WHEN a spawn runs in a worktree THE SYSTEM SHALL tell it that the room directory is checked out for it at the worktree path, that paths under the room directory mean the same relative path under the worktree, and that it must never write under the room directory.

## Decisions taken (reversible, recorded for veto)

- Worktrees live beside the room directory (`<room dir>.worktrees\x<root>`), never under the hub's data directory: spawned Claude Code sessions load the owner's global hooks, and the privacy guard denies every read under `C:\Self Apps\...\data`, where the deployed hub's data directory is.
- A stopped exchange is never merged: a stop kills a spawn mid-edit and the after-spawn commit captures a half-done tree.
- Runs keep the room directory: a run room holds exactly one exchange (`AddExchange(..., replaceAll: true)`), its gates run in the room directory (`src/ChopItUp.Hub/Mcp/RunTools.cs:86`) and its artifacts diff the room tree, so there is no concurrency to isolate.
- The merge commit's author and committer are the hub (`GitTrail.Hub`); its subject is `Merge exchange #<root> (<room id>)`.
- A room whose HEAD is unborn gets one empty hub commit (`Room trail start`) before the first worktree, because a worktree of an unborn HEAD is an unborn branch that cannot be merged.
- An exchange dropped from the room list while still open (a run starting with `replaceAll`) whose spawn later ends without concluding it keeps its worktree until the next hub start; `RecoverAsync` clears it. Declined: a separate sweep for that edge.
- A close commits the owner's dirty room tree as the owner before merging (AC3), the same rule every spawn applies before it starts.
- A worktree holds only tracked files: gitignored content (`.env`, `node_modules`, `bin`/`obj`, `.scratch`) and submodule checkouts are absent, so a spawn that needs them must recreate them in its worktree. Accepted as the cost of the owner's ruling that both exchanges really edit at once; the prompt says so (AC10).
- A spawn killed by a stop or a timeout keeps its exchange's branch unmerged, whatever the exchange's status (pass 2 F2).
- Whether Codex accepts `-C <worktree>` (a `.git` file) without `--skip-git-repo-check` is unverified; inferred yes (its repo check is a git discovery, which follows gitfiles). No flag is added; the live check is a punch-list item.

## Critique dispositions

Pass 1 (Fable, 6.4, FIX-THEN-SHIP): M1 fixed (MERGE_HEAD guard in `CommitAllAsync`, abort-whenever-present in `MergeAsync`, `AbortStaleMergeAsync` in recovery, AC4 widened). M2 fixed (AC9; `_closingRooms` blocks room-directory launches, a close defers while a room-directory spawn is in flight, `_worktreeExchanges` keeps dropped exchanges closable). M3 fixed (no registered worktree → no merge or delete; "nothing new to merge" arm; AC8 widened). M4 fixed (two-participant exchange for the branch-commit test; a file at the worktrees folder forces the refusal; `CreateDirectory` guarded). m5 fixed (deploy + self-check step). m6 fixed (AC3 names the owner commit). m7 fixed (write primitives return failure text). m8 fixed (ledger row 1 carries the count; RunTools path). m9 fixed (prunable registration re-added). m10 fixed (dedicated "was not started" note).

Pass 2 (Opus, 5.9, FIX-THEN-SHIP): F1 fixed (`AbortStaleExchangeMergeAsync` aborts only a merge whose message is `Merge exchange #` and whose MERGE_HEAD is a `chopitup/` tip; AC7). F2 fixed (`Exchange.Interrupted` from any cancelled or timed-out spawn keeps the branch; AC5; tests). F3 fixed (`OperationInProgressUnlocked` covers merge, cherry-pick, revert, rebase and unmerged paths; AC4). F4 fixed (worktree launches wait on an in-flight room-directory spawn; AC9 test rewritten around a conductor that ignores cancellation). F5 fixed (pruned registration re-adds onto the existing branch; `WorktreeLeased` via `TrailReport.Leased`; close keeps and names a leased branch whose folder vanished). F6 fixed by declared decision plus prompt mapping (AC10). F7 fixed (Task 5 step 7 test follow-through with named log shapes). m1 fixed (filter before commit/remove). m2 fixed (owner-commit failure keeps the branch). m3 fixed (recovery deletes already-merged branches). m4 fixed (order + mutation pass). m5 declined with rationale (Decision: Codex worktree acceptance inferred, live check on the punch list). m6 fixed (AC1 distinct participants). m7 fixed (room bind refuses paths inside a bound room's worktrees folder).
- Declined: automatic conflict resolution, a branch list in the web UI, worktrees for app-backed participants (they have no spawn).

## Lessons consulted

M9 (directory rooms, one writer per tree), M20 (spawn timeouts), M25 (a recovery arm must be reachable in every state it exists for: hence the close hook at `Publish`, `AddExchange` and `OnFinished`), M24 (race/clock guard tests), row 34 (stub `codex.cmd` for a live dry run), 2026-09-07 Start-Process quoting (the dry run passes paths with spaces).

## Could not verify in this environment

- The git version on the CI runner (`windows-latest`); locally 2.45.2. `git worktree add` on an unborn HEAD succeeding silently was measured on 2.45.2 only.
- A real Claude Code or Codex spawn editing inside a worktree (spends model calls). The dry run uses a stub `codex.cmd` that writes files.
- Windows file locks from a killed CLI blocking `git worktree remove`: handled as a reported failure, not reproduced.

## Claim ledger

| # | Claim | Verified at (commit) | Recheck (pwsh, exit 0 = holds) |
|---|-------|----------------------|--------------------------------|
| 1 | Baseline: 945 tests green (Core 224, Hub 721), measured 02:31 this session | 849953f | — |
| 2 | Directory exclusivity gates on the room's whole in-flight set | 849953f | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Spawning/ExchangePolicy.cs -SimpleMatch 'if (exclusive && inFlightInRoom.Count > 0) return [];' -Quiet)) { exit 0 } else { exit 1 }` |
| 3 | LaunchDue and ArmWake compute exclusive from the room directory | 849953f | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch 'var exclusive = _store.GetRoom(x.RoomId)?.Directory is not null;' -Quiet)) { exit 0 } else { exit 1 }` |
| 4 | The spawn body takes the room trail by directory | 849953f | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch 'git = _trails.For(directory);' -Quiet)) { exit 0 } else { exit 1 }` |
| 5 | GitTrail guards on `.git` being a directory (breaks in a worktree, where `.git` is a file) | 849953f | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Git/GitTrail.cs -SimpleMatch 'Directory.Exists(Path.Combine(Root, ".git"))').Count -ge 6) { exit 0 } else { exit 1 }` |
| 6 | GitTrail ctor shape | 849953f | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Git/GitTrail.cs -SimpleMatch 'public GitTrail(string root, Func<ResolvedCli>? resolve = null, IProcessRunner? runner = null)' -Quiet)) { exit 0 } else { exit 1 }` |
| 7 | OnFinished computes the conclusion note here | 849953f | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch 'var note = ExchangePolicy.Finished(h.Exchange, id);' -Quiet)) { exit 0 } else { exit 1 }` |
| 8 | AddExchange drops closed idle exchanges | 849953f | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch 'else list.RemoveAll(e => e.Status != ExchangeStatus.Open && e.InFlight.Count == 0);' -Quiet)) { exit 0 } else { exit 1 }` |
| 9 | RoomTrails registration with the test seam | 849953f | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Hosting/HubHost.cs -SimpleMatch 'builder.Services.AddSingleton(new RoomTrails(roomGit ?? (dir => new GitTrail(dir))));' -Quiet)) { exit 0 } else { exit 1 }` |
| 10 | ProcessResult positional shape used for a not-started spawn | 849953f | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch 'new ProcessResult(null, false, false, "", "not started", TimeSpan.Zero)' -Quiet)) { exit 0 } else { exit 1 }` |
| 11 | The R32 directory test that this row inverts exists | 849953f | `if ((Select-String -LiteralPath tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.Rooms.cs -SimpleMatch 'R32_a_directory_room_runs_one_spawn_at_a_time_across_two_exchanges' -Quiet)) { exit 0 } else { exit 1 }` |
| 12 | Spawned agents may not run `git worktree` themselves | 849953f | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnCommands.cs -SimpleMatch '"worktree"' -Quiet)) { exit 0 } else { exit 1 }` |
| 13 | Test rooms live at `_host.RoomsRoot\<id>`, so worktrees land in the test temp root | 849953f | `if ((Select-String -LiteralPath tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.Rooms.cs -SimpleMatch 'var dir = Path.Combine(_host.RoomsRoot, id);' -Quiet)) { exit 0 } else { exit 1 }` |
| 14 | Local git supports worktrees and polite remove/delete refusals | 849953f | `if ([version]((git --version) -replace '^git version (\d+\.\d+).*$','$1') -ge [version]'2.40') { exit 0 } else { exit 1 }` |
| 15 | PostNote is loop-thread only; the Task.Run body reports back through `_events` | 849953f | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch 'finally { _events.Writer.TryWrite(new FinishedEvent(handle, result, trail)); }' -Quiet)) { exit 0 } else { exit 1 }` |

## Tasks

Order is the ticket order. Each task is TDD: write the named tests, run them RED, implement, GREEN, then the full suite, one commit.

### Task 1: GitTrail works inside a worktree and gains worktree, merge and branch primitives

Files: `src/ChopItUp.Hub/Git/GitTrail.cs`, `tests/ChopItUp.Hub.Tests/Git/GitTrailTests.cs`.

1. Add a private helper and replace every `Directory.Exists(Path.Combine(Root, ".git"))` with it:
   ```csharp
   /// <summary>True when <see cref="Root"/> holds a repository entry: a `.git` folder (a main tree) or
   /// a `.git` file (a linked worktree, whose `.git` names the real one).</summary>
   private bool HasGitEntry() => Directory.Exists(Path.Combine(Root, ".git")) || File.Exists(Path.Combine(Root, ".git"));
   ```
   In `InitAsync` and `CommitAllAsync` the "already a repository, do not init" test uses `HasGitEntry()` too.
2. Shared gate. Change the field to `private readonly SemaphoreSlim _gate;` and the ctor to
   ```csharp
   public GitTrail(string root, Func<ResolvedCli>? resolve = null, IProcessRunner? runner = null)
       : this(root, resolve, runner, new SemaphoreSlim(1, 1)) { }

   private GitTrail(string root, Func<ResolvedCli>? resolve, IProcessRunner? runner, SemaphoreSlim gate)
   {
       Root = Path.GetFullPath(root);
       _resolve = resolve ?? (() => CliResolver.Resolve("git"));
       _runner = runner ?? new ProcessRunner();
       _gate = gate;
   }

   /// <summary>A trail over <paramref name="root"/> (a linked worktree of this repository) that shares
   /// this trail's git resolver, process runner and write gate: every write to one repository, from any
   /// of its worktrees, goes through one gate.</summary>
   public GitTrail WithRoot(string root) => new(root, _resolve, _runner, _gate);
   ```
   Update the class summary's last sentence to "Serialised per repository - every trail made by `WithRoot` shares the gate."
3. New members, each following the existing shape (`Resolve()`, `Run`, `Fail`, `Reason = null` on success):
   ```csharp
   public enum MergeResult { Merged, Conflict, Failed }
   public sealed record MergeOutcome(MergeResult Result, string? Hash, IReadOnlyList<string> Conflicts, string? Reason);
   ```
   (top-level records beside `CommitOutcome`.)
   - `Task<bool> HasCommitsAsync(ct)`: `rev-parse --verify -q HEAD`, true on exit 0; no `Fail` on a nonzero exit.
   - `Task<string?> CurrentBranchAsync(ct)`: `symbolic-ref -q --short HEAD`, trimmed stdout on exit 0, null otherwise (detached HEAD is not a failure).
   - `Task<bool> BranchExistsAsync(string branch, ct)`: `rev-parse --verify -q refs/heads/<branch>`.
   - `Task<bool> IsAncestorOfHeadAsync(string branch, ct)`: `merge-base --is-ancestor refs/heads/<branch> HEAD`, true on exit 0.
   - `Task<string?> AddWorktreeAsync(string path, string branch, bool newBranch, ct)`: under the gate, `worktree add -q -b <branch> <full path>` when `newBranch`, else `worktree add -q <full path> <branch>`; returns null on success, else the failure text from `Fail("git worktree add", r)`. (Critique m7: the three write primitives below RETURN their failure text; callers never read the shared `Reason` after the fact, because several closes and spawn commits now share one trail.)
   - `Task<string?> RemoveWorktreeAsync(string path, ct)`: under the gate, `worktree remove <full path>` (never `--force`); null on success, else the failure text.
   - `Task<string?> OperationInProgressAsync(ct)` and a private `OperationInProgressUnlocked(git, ct)` (pass 2 F3 widens pass 1's MERGE_HEAD check): returns a short name (`"merge"`, `"cherry-pick"`, `"revert"`, `"rebase"`, `"unmerged paths"`) or null. Checks, in order: for each of `MERGE_HEAD`, `CHERRY_PICK_HEAD`, `REVERT_HEAD`, `rebase-merge`, `rebase-apply`, resolve `rev-parse --git-path <name>` (relative results are relative to `Root`) and test `File.Exists || Directory.Exists`; then `ls-files -u` non-empty → `"unmerged paths"`.
   - `CommitAllAsync` change (pass 1 M1, pass 2 F3): under the gate, after `git` resolves and the repository exists, before `add`: `if (await OperationInProgressUnlocked(git, cancellation) is { } op) return new(null, false, 0, <reason "a <op> is in progress in <Root>; nothing was committed", set Reason, one log line>);`. A `git add -A; git commit` over an in-progress merge or cherry-pick finalises it with conflict markers (both reproduced by the critics).
   - `Task<IReadOnlyList<string>> WorktreePathsAsync(ct)`: `worktree list --porcelain`; every line starting `worktree ` gives `Path.GetFullPath(rest.Replace('/', '\\'))`; the first entry (the main tree) is included.
   - `Task PruneWorktreesAsync(ct)`: under the gate, `worktree prune`.
   - `Task<MergeOutcome> MergeAsync(string branch, string message, ct)`: under the gate, `[.. Committer, "merge", "--no-ff", "-m", message, branch]`. Before merging, if `OperationInProgressUnlocked` is non-null return `Failed` "a <op> is already in progress". Exit 0: `new(Merged, await HeadUnlocked(...), [], null)`. Nonzero (conflict, failure, or a kill at `Timeout`): read `diff --name-only --diff-filter=U` into `paths`; then, whenever `rev-parse -q --verify MERGE_HEAD` exits 0 (regardless of `paths`), run `merge --abort`; if the abort exits nonzero return `new(Failed, null, paths, Fail("git merge --abort", abort))`. Then `paths.Count > 0` → `new(Conflict, null, paths, null)`, else `new(Failed, null, [], Fail("git merge", r))`.
   - `Task<string?> DeleteMergedBranchAsync(string branch, ct)`: under the gate, `branch -d <branch>` (never `-D`); null on success, else the failure text.
   - `Task<string?> AbortStaleExchangeMergeAsync(ct)` (pass 2 F1: never abort the owner's own merge): under the gate; null when no `MERGE_HEAD`. Otherwise read the file at `rev-parse --git-path MERGE_MSG` and `rev-parse MERGE_HEAD`; abort (`merge --abort`) only when the message starts with `Merge exchange #` AND `for-each-ref --format=%(objectname) refs/heads/chopitup/` lists that MERGE_HEAD hash; return `"aborted"`, the abort failure text, or `"left alone"` when it is not the hub's merge.
4. Tests (real git, the existing `_dir` fixture; add a helper that makes a repo with one commit):
   - `Worktree_trail_sees_dirt_and_commits_on_its_branch_without_reinitialising`: main repo with a commit, `AddWorktreeAsync(wt, "chopitup/x1")`, `var w = trail.WithRoot(wt)`, write a file in `wt`, assert `w.IsDirtyAsync()` true, `w.CommitAllAsync(...)` created, `w.CurrentBranchAsync()` == `chopitup/x1`, `File.Exists(Path.Combine(wt, ".git"))` still a file and `Directory.Exists(Path.Combine(wt, ".git"))` false, main `HeadAsync` unchanged.
   - `Merge_of_a_clean_branch_creates_a_no_ff_merge_commit_and_the_branch_deletes`: parents of HEAD after merge == 2 (`rev-list --parents -n 1 HEAD` has three hashes), then `RemoveWorktreeAsync` true, `DeleteMergedBranchAsync` true, `BranchExistsAsync` false.
   - `A_conflicting_merge_is_aborted_and_names_the_paths`: two worktrees edit `c.txt` differently, merge the first, merge the second → `Conflict`, `Conflicts` == `["c.txt"]`, main `IsDirtyAsync` false, HEAD equals the hash after the first merge, `.git\MERGE_HEAD` absent, branch still exists.
   - `Remove_refuses_a_dirty_worktree_and_delete_refuses_an_unmerged_branch`: both return non-null failure text.
   - `A_leftover_merge_head_is_never_committed`: leave a conflicting merge in progress with raw git (`git merge --no-ff` exits 1), then `CommitAllAsync` returns `Hash == null` with a reason naming the merge, `MERGE_HEAD` still present, no new commit.
   - `A_cherry_pick_in_progress_is_never_committed` (same shape with a conflicting `git cherry-pick`).
   - `Stale_abort_aborts_only_the_hubs_exchange_merge`: a conflicting `MergeAsync`-style merge left by raw `git merge --no-ff -m "Merge exchange #4 (lab)" chopitup/x4` → `"aborted"`, clean tree; an owner merge of a branch `feature` with an unstaged resolution edit → `"left alone"`, the edit intact.
   - `WithRoot_shares_the_gate`: build the main trail with a private test `IProcessRunner` that delegates to `new ProcessRunner()` and records the peak number of concurrent `RunAsync` calls; run 10 `CommitAllAsync` calls on the main trail and 10 on its `WithRoot` worktree trail concurrently (`Task.WhenAll`); assert the peak is 1. RED first by constructing the worktree trail with `new GitTrail(wt, runner: sameRunner)` (a separate gate), which must show a peak above 1; then switch to `WithRoot`.

### Task 2: ExchangeWorktrees owns names, creation, close-and-merge and recovery

Files: new `src/ChopItUp.Hub/Rooms/ExchangeWorktrees.cs`; `src/ChopItUp.Hub/Rooms/RoomTrails.cs`; `src/ChopItUp.Hub/Hosting/HubHost.cs` (registration); new `tests/ChopItUp.Hub.Tests/Rooms/ExchangeWorktreesTests.cs`.

1. `RoomTrails` gains:
   ```csharp
   /// <summary>The trail of a linked worktree of <paramref name="roomDirectory"/>, sharing the room trail's
   /// write gate (<see cref="GitTrail.WithRoot"/>).</summary>
   public GitTrail ForWorktree(string roomDirectory, string worktree) =>
       _trails.GetOrAdd(RoomPaths.Normalize(worktree), path => For(roomDirectory).WithRoot(path));

   public void Forget(string directory) => _trails.TryRemove(RoomPaths.Normalize(directory), out _);
   ```
2. `ExchangeWorktrees(MessageStore store, RoomTrails trails, RoomPathRules rules)`:
   ```csharp
   public static string Branch(long rootMessageId) => $"chopitup/x{rootMessageId}";
   public static string FolderFor(string roomDirectory) { var d = RoomPaths.Normalize(roomDirectory); return d + ".worktrees"; }
   public static string PathFor(string roomDirectory, long rootMessageId) => Path.Combine(FolderFor(roomDirectory), "x" + rootMessageId);
   public sealed record Lease(string? Path, string? Refusal);
   ```
   - `Task<Lease> EnsureAsync(string roomDirectory, long root, CancellationToken ct)`:
     1. `path = PathFor(...)`. If `RoomPaths.Refusal(path, rules)` is non-null → `new(null, "the worktree path is refused: " + refusal)`.
     2. If any other room's directory (`store.ListRooms(includeArchived: true)`) satisfies `RoomPaths.IsUnderOrEqual(path, other)` or `RoomPaths.IsUnderOrEqual(other, path)` → refusal "the worktree path overlaps room '<id>'".
     3. `main = trails.For(roomDirectory)`. If `(await main.WorktreePathsAsync(ct)).Any(p => RoomPaths.Same(p, path))`: when `Directory.Exists(path)` → `new(path, null)`; otherwise (a prunable registration, critique m9) `await main.PruneWorktreesAsync(ct)`, set `pruned = true`, and continue to step 4.
     4. If `!await main.HasCommitsAsync(ct)` → `main.CommitAllAsync("Room trail start", GitTrail.Hub, allowEmpty: true, ct)` BUT with nothing staged: the owner's edits were already committed by the caller, so `add -A` stages nothing new. If `Hash` is null → refusal "the room directory has no commit and one could not be made: " + reason.
     5. `var exists = await main.BranchExistsAsync(Branch(root), ct);` If `exists && !pruned` → refusal "branch chopitup/x<root> already exists". If `exists && pruned` the branch is this exchange's own (its folder was deleted between two spawns, pass 2 F5): step 6 re-adds the worktree onto it with `newBranch: false`.
     6. `try { Directory.CreateDirectory(FolderFor(roomDirectory)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(null, "the worktrees folder could not be created: " + e.Message); }` (critique M4b: unguarded, an `IOException` escaped as a silent "trail error"); `AddWorktreeAsync(path, Branch(root), newBranch: !exists, ct)` non-null → refusal "git worktree add failed: " + that text.
     7. `new(path, null)`.
   - `public sealed record CloseRequest(string RoomDirectory, long Root, string RoomId, ExchangeStatus Status, bool Leased, bool Interrupted, bool RunOwnsRoom, GitIdentity Owner, string OwnerMessage);` and `Task<string?> CloseAsync(CloseRequest request, CancellationToken ct)` returns the note or null (the step text below names the record's fields as plain parameters):
     1. `path = PathFor(...)`, `branch = Branch(root)`, `main = trails.For(roomDirectory)`.
     2. `removal = ""`. If the path is NOT registered (`WorktreePathsAsync`): when `Leased && await main.BranchExistsAsync(branch, ct)` → `keep("its worktree folder was gone at close")` (pass 2 F5: the exchange did get a worktree, so the branch is the hub's and its commits must be named); otherwise return null: this exchange never got a worktree (a refused lease, AC8), so any branch of that name is not the hub's to merge or delete (critique M3). Otherwise: if `Directory.Exists(path)`, `w = trails.ForWorktree(roomDirectory, path)`; if `await w.IsDirtyAsync(ct)` → `w.CommitAllAsync($"Uncommitted at the close of exchange #{root}", GitTrail.Hub, allowEmpty: false, ct)`; then `var failed = await main.RemoveWorktreeAsync(path, ct); if (failed is not null) removal = $" Its worktree at {path} was not removed: {failed}"; else trails.Forget(path);`.
     3. If `!await main.BranchExistsAsync(branch, ct)` → return `removal.Length > 0 ? $"Exchange #{root}:{removal}" : null`.
     3a. If `await main.OperationInProgressAsync(ct)` is `{ } op` → `keep($"the room directory has a {op} in progress")`.
     4. `keep(reason) => $"Exchange #{root} was not merged: {reason}. Its commits stay on branch {branch}.{removal}"`.
     5. `status == ExchangeStatus.Stopped` → `keep("it was stopped")`. `Interrupted` → `keep("a spawn of it was stopped or timed out")`. `runOwnsRoom` → `keep("a run owns this room")`. `await main.CurrentBranchAsync(ct)` null → `keep("the room directory has no branch checked out")`.
     6. If `await main.IsDirtyAsync(ct)` → `var oc = await main.CommitAllAsync(ownerMessage, owner, allowEmpty: false, ct); if (oc.Hash is null) return keep($"the owner's edits could not be committed first: {oc.Reason}");` (pass 2 m2: never merge over a dirty tree, since `merge --abort` may not restore uncommitted changes).
     7. `before = await main.HeadAsync(ct)`; `m = await main.MergeAsync(branch, $"Merge exchange #{root} ({roomId})", ct)`:
        - `Merged` → `var notDeleted = await main.DeleteMergedBranchAsync(branch, ct)`; return `m.Hash == before ? $"Exchange #{root} had nothing new to merge into {current}.{removal}" : $"Exchange #{root} merged into {current} as {m.Hash}.{removal}"`, appending `$" Branch {branch} was not deleted: {notDeleted}"` when `notDeleted` is non-null (critique M3: "Already up to date" exits 0 with no commit).
        - `Conflict` → `keep($"merging into {current} conflicts in {string.Join(", ", m.Conflicts)}")`.
        - `Failed` → `keep($"the merge failed: {m.Reason}")`.
   - `Task<string?> RecoverAsync(string roomDirectory, CancellationToken ct)`: `main = trails.For(roomDirectory)`; if `!Directory.Exists(roomDirectory)` return null; first `var stale = await main.AbortStaleExchangeMergeAsync(ct)` (a hub killed mid-merge; pass 2 F1: only the hub's own exchange merge is aborted) and, when it is `"aborted"` or a failure text, prefix the note with `"The last hub's exchange merge was " + (stale == "aborted" ? "aborted. " : "not aborted: " + stale + ". ")` (`"left alone"` adds nothing; return the prefix sentence alone when nothing else is collected); `folder = FolderFor(roomDirectory)`; for every registered path whose parent is exactly `folder` AND whose folder name matches `^x\d+$` (pass 2 m1: filter BEFORE touching anything): if the directory exists and `trails.ForWorktree(...).IsDirtyAsync` → commit as `GitTrail.Hub` with message `"Uncommitted when the hub restarted"`; `RemoveWorktreeAsync`; then for its branch `chopitup/x<digits>`: when `IsAncestorOfHeadAsync` → `DeleteMergedBranchAsync` (pass 2 m3: already merged, nothing to report), else collect it. Then `PruneWorktreesAsync`. Return the prefix (or null) when nothing was collected, else prefix + `$"The hub restarted while exchange worktrees were open. Their commits stay on branch(es) {string.Join(", ", branches)}, not merged."`.
   - `RoomDirectories.PrepareAsync` (pass 2 m7): extend its overlap refusal so a typed path under any bound room's `ExchangeWorktrees.FolderFor(room.Directory)` is refused with "… is inside room '<id>''s exchange worktrees". Test in `RoomsApiTests` or the existing `RoomDirectories` tests.
3. Register in `HubHost.Build` directly after `RoomDirectories`:
   `builder.Services.AddSingleton(sp => new ExchangeWorktrees(sp.GetRequiredService<MessageStore>(), sp.GetRequiredService<RoomTrails>(), RoomPathRules.ForHub(options.DataDir)));`
4. Tests (real git under a temp root; construct `MessageStore` the way `RoomsApiTests` or `HubTestHost` does; STOP if there is no cheap way and report):
   - `Ensure_creates_the_branch_worktree_beside_the_room_and_is_idempotent`.
   - `Ensure_makes_a_start_commit_in_an_unborn_room`.
   - `Ensure_refuses_when_the_branch_already_exists` and never creates the folder entry.
   - `Close_merges_a_concluded_exchange_and_deletes_branch_and_worktree` (note starts `Exchange #7 merged into`).
   - `Close_keeps_the_branch_of_a_stopped_exchange` / `..._when_a_run_owns_the_room` / `..._when_head_is_detached`.
   - `Close_on_conflict_aborts_and_names_branch_and_paths` (room HEAD and `git status --porcelain` unchanged; note contains `chopitup/x8` and `c.txt`).
   - `Close_commits_owner_edits_in_the_room_before_merging` (owner-authored commit precedes the merge commit).
   - `Recover_commits_removes_and_names_the_branches_then_is_quiet_on_the_next_start`.
   - `Recover_aborts_the_hubs_merge_and_leaves_an_owner_merge_alone`.
   - `Recover_deletes_an_already_merged_branch_and_ignores_non_exchange_worktrees` (a registered worktree `<folder>\notes` stays registered and untouched).
   - `Close_keeps_the_branch_when_a_spawn_was_interrupted` and `Close_keeps_a_leased_branch_whose_folder_vanished`.
   - `Close_keeps_the_branch_when_the_owner_commit_fails` (a cherry-pick in progress in the room: note names it, no merge).
   - `Close_without_a_registered_worktree_touches_no_branch` (pre-created `chopitup/x9` at HEAD, no worktree: `CloseAsync` returns null and the branch still exists).
   - `Close_of_a_branch_with_nothing_new_says_so` (worktree made, no commit on it: note says `had nothing new to merge`).
   - `Ensure_refuses_when_the_worktrees_folder_is_a_file` (a file at `FolderFor(dir)`: refusal text, no exception).

### Task 3: exclusivity is per exchange in a directory room without a run

Files: `src/ChopItUp.Hub/Spawning/ExchangePolicy.cs`, `src/ChopItUp.Hub/Spawning/SpawnerService.cs` (`LaunchDue`, `ArmWake`), tests in the policy test file and `SpawnerServiceTests.Rooms.cs`.

1. `Due` and `NextWake` gain a trailing optional parameter `IReadOnlySet<string>? exclusiveOver = null`; the exclusive guards become `if (exclusive && (exclusiveOver ?? inFlightInRoom).Count > 0) return [];` (`return null;` in `NextWake`). Doc comment: "<paramref name="exclusiveOver"/> narrows what exclusivity waits on: the exchange's own in-flight set when it has a worktree (row 35); the room's whole set otherwise." The `inFlightInRoom.Contains(id)` skip is unchanged.
2. `SpawnerService` gains `private bool UsesWorktrees(string roomId, string? directory) => directory is not null && _runs.Active(roomId) is null && _runs.Latest(roomId) is not { Status: RunStatus.Parked };`. In `LaunchDue` and `ArmWake`: `var directory = _store.GetRoom(x.RoomId)?.Directory; var exclusive = directory is not null; var over = UsesWorktrees(x.RoomId, directory) ? x.InFlight : null;` and pass `over` last. Launch uses the same `UsesWorktrees` call (Task 4) so the policy and the launch never disagree. Pass 2 F4: a worktree launch must still wait while a room-directory spawn of the same room is in flight (a run just ended or stopped, its cancelled conductor not yet finished, would otherwise have its partial edits committed as the owner's by the worktree launch's before-commit). In `LaunchDue` and `ArmWake`, when `over is not null`, `if (_inFlight.Values.Any(h => h.Request.RoomId == x.RoomId && h.Directory is not null && !h.InWorktree)) continue;` (`SpawnHandle.InWorktree` is added in Task 4; if Task 3 lands before Task 4, add the property here with every current launch setting it false).
3. Tests: policy unit tests for both parameters; replace `R32_a_directory_room_runs_one_spawn_at_a_time_across_two_exchanges` with `R35_a_directory_room_runs_two_exchanges_at_once_and_one_spawn_at_a_time_within_each` (two exchanges → two specs without releasing; a second participant queued in the first exchange stays unlaunched until its sibling ends). Keep a run-room test asserting AC6 (find the existing one in `SpawnerServiceTests.Runs.cs`; if none asserts one-at-a-time in a run room, add `R35_a_run_room_still_runs_one_spawn_at_a_time`).
   This task's commit also contains Task 4 if the suite cannot pass with launches still in the room directory (two concurrent spawns would commit into one tree); if so, do Tasks 3 and 4 as one commit and say so in the report.

### Task 4: a non-run directory-room spawn runs in its exchange's worktree

Files: `src/ChopItUp.Hub/Spawning/Exchange.cs`, `src/ChopItUp.Hub/Spawning/SpawnerService.cs` (ctor injection, `Launch`), `SpawnerServiceTests.Rooms.cs` and any other test that asserted the room directory as a non-run directory spawn's working directory or read the agent commit from the room directory's log.

1. `Exchange` gains (loop-thread state, documented as such):
   ```csharp
   /// <summary>Row 35: the room directory whose worktree this exchange used, set at its first worktree
   /// launch; null for an exchange that never launched in a worktree.</summary>
   public string? WorktreeRoom { get; set; }
   /// <summary>Row 35: set once the close has been handed off, so it is handed off once.</summary>
   public bool WorktreeClosing { get; set; }
   /// <summary>Row 35: a spawn of this exchange ran in a worktree it was really given (not a refused lease).</summary>
   public bool WorktreeLeased { get; set; }
   /// <summary>Row 35: a spawn of this exchange was cancelled or timed out, so its tree may be half-written
   /// and the close keeps the branch unmerged whatever the status says.</summary>
   public bool Interrupted { get; set; }
   ```
   `TrailReport` gains a trailing `bool Leased` (true when the spawn body ran in a leased worktree). In `OnFinished`, first lines after `_inFlight.Remove`: `if (r.Cancelled || r.TimedOut) h.Exchange.Interrupted = true; if (trail?.Leased == true) h.Exchange.WorktreeLeased = true;`.
2. `SpawnerService` takes `ExchangeWorktrees worktrees` through its constructor like `RoomTrails` (follow the existing DI registration of `SpawnerService`; STOP if it is not constructed by DI).
3. In `Launch`, after `var directory = room?.Directory;`:
   ```csharp
   // Row 35: outside a run, a directory room's spawn edits its exchange's own worktree.
   var inWorktree = UsesWorktrees(request.RoomId, directory);
   var tree = inWorktree ? ExchangeWorktrees.PathFor(directory!, x.RootMessageId) : directory;
   if (inWorktree) x.WorktreeRoom = directory;
   ```
   `SpawnHandle` gains `public bool InWorktree { get; init; }`, set from `inWorktree` (Task 5 reads it). Every later use of `directory` in `Launch` that describes where the CLI works (`SpawnPromptInput(... Directory: ...)`, `SpawnPrompt.DirectoryRules(...)`, the `roomDir` argument of `ClaudeInDirectory`/`CodexInDirectory`, `SpawnHandle.Directory`) takes `tree`. The `directory is not null` tests that choose a spawn shape and the room-memory block keep `directory`.
   Pass 2 F6 (AC10): `SpawnPrompt.DirectoryRules(string directory)` becomes `DirectoryRules(string directory, string? checkoutOf = null)`; when `checkoutOf` is non-null and differs, append: `$"This folder is a git worktree: the room directory {checkoutOf} is checked out for you at {directory}. A path under {checkoutOf} in the conversation means the same relative path under {directory}. Never write under {checkoutOf}. Gitignored files (dependencies, build output, local settings) are not in this checkout; recreate what you need here."`. `SpawnPromptInput` gains `string? DirectoryCheckoutOf = null`, passed into its own `DirectoryRules` call; `Launch` passes `directory` as `checkoutOf`/`DirectoryCheckoutOf` when `inWorktree`. Test in `SpawnPromptTests`: the sentence appears for a worktree, and a null `checkoutOf` renders byte-for-byte as before.
4. In the `Task.Run` body replace the `if (directory is not null) { ... }` before-block with:
   ```csharp
   if (directory is not null)
   {
       var roomGit = _trails.For(directory);
       if (await roomGit.IsDirtyAsync(CancellationToken.None))
           owner = await roomGit.CommitAllAsync(RoomCommits.OwnerMessage(_owner, roomId), RoomCommits.IdentityOf(_owner), allowEmpty: false, CancellationToken.None);
       if (inWorktree)
       {
           var lease = await _worktrees.EnsureAsync(directory, request.RootMessageId, CancellationToken.None);
           if (lease.Refusal is not null)
           {
               result = new ProcessResult(null, false, false, "", WorktreeRefused + lease.Refusal, TimeSpan.Zero);
               return;   // the finally still reports FinishedEvent; no CLI starts anywhere (AC8)
           }
           git = _trails.ForWorktree(directory, tree!);
           leased = true;   // a `bool leased = false;` local beside `owner`, passed as TrailReport's new last argument
       }
       else git = roomGit;
       headBefore = await git.HeadAsync(CancellationToken.None);
   }
   ```
   Capture `inWorktree` and `tree` into locals before `Task.Run` like `turn`/`budget`. `return` inside the `try` of an async lambda runs the `finally`: confirm by the AC8 test, not by assumption. Add `private const string WorktreeRefused = "the exchange's worktree could not be created: ";`.
   In `OnFinished`, ahead of the `else if (!h.Posted)` arm, add (critique m10: the generic arm would claim a process ran): `else if (r.ExitCode is null && r.StandardError.StartsWith(WorktreeRefused, StringComparison.Ordinal)) PostNote(room, $"@{id} was not started: {r.StandardError}.");`
5. Tests:
   - `R35_a_directory_room_spawn_works_in_its_exchange_worktree_and_commits_on_its_branch` (critique M4a): one owner message mentions two spawnable participants, so they share one exchange and run one at a time; the fake handler lets the first return at once and holds the second on a `TaskCompletionSource`. While the second is held: the first spec's `WorkingDirectory == ExchangeWorktrees.PathFor(dir, root)`, `git log -1 --format=%an chopitup/x<root>` in `dir` names the first participant, and the room directory's `HEAD` is unchanged. Release the second afterwards.
   - `R35_owner_edits_are_committed_in_the_room_before_the_worktree_branches` (the worktree contains the owner's file).
   - `R35_a_refused_worktree_starts_no_cli_and_says_why` (critique M4b): before posting, create a FILE at `ExchangeWorktrees.FolderFor(dir)`, so the refusal is deterministic. Assert `_runner.NoSpecWithin(TimeSpan.FromSeconds(1))`, a hub note starting `@<participant> was not started:` and containing `could not be created`, and no branch `chopitup/x*` in `dir`.
   - Update the M9 tests that assert `spec.WorkingDirectory == dir` or read the agent commit from `dir`'s HEAD: the working directory becomes the worktree path; the author assertion reads branch `chopitup/x<root>` before close, or the merged history after close (`git log --format=%an -n 2` shows the merge by the hub and the agent commit beneath). Do not weaken any other assertion. List every test changed in the report.

### Task 5: a closed exchange's worktree is merged or kept, and the note is posted

Files: `src/ChopItUp.Hub/Spawning/SpawnerService.cs`, `SpawnerServiceTests.Rooms.cs`.

1. New event `private sealed record WorktreeClosedEvent(string RoomId, string? Note) : Event;` handled in `Handle` by `_closingRooms.Remove(e.RoomId); if (e.Note is not null) PostNote(e.RoomId, e.Note);` (the loop's `LaunchDue`/`ArmWake` pass after the handler then launches anything that waited).
2. Loop-thread-only fields and the close hand-off (critique M2: a close is a writer of the room directory, so it must be exclusive with room-directory spawns in both directions, and an exchange dropped from the room list by a run's `replaceAll` must still be closable):
   ```csharp
   private readonly List<Task> _closing = new();
   /// <summary>Row 35: every exchange that launched in a worktree and has not been handed to a close yet,
   /// whether or not it is still in its room's list.</summary>
   private readonly List<Exchange> _worktreeExchanges = new();
   /// <summary>Row 35: rooms with a worktree close running; a room-directory launch waits while its room is here.</summary>
   private readonly HashSet<string> _closingRooms = new(StringComparer.Ordinal);

   /// <summary>Row 35: hands each closed, idle worktree exchange of <paramref name="roomId"/> to
   /// <see cref="ExchangeWorktrees.CloseAsync"/>, one at a time per room, off the loop thread. Waits
   /// (returns, to be called again) while any spawn is in flight in the room directory itself or another
   /// close of this room is running. Called from OnFinished, Publish and AddExchange.</summary>
   private void CloseIdleWorktrees(string roomId)
   {
       if (_closingRooms.Contains(roomId)) return;
       if (_inFlight.Values.Any(h => h.Request.RoomId == roomId && h.Directory is not null && !h.InWorktree)) return;
       var x = _worktreeExchanges.FirstOrDefault(e => e.RoomId == roomId && e.Status != ExchangeStatus.Open && e.InFlight.Count == 0);
       if (x is null) return;
       _worktreeExchanges.Remove(x);
       x.WorktreeClosing = true;
       _closingRooms.Add(roomId);
       var runOwns = _runs.Active(roomId) is not null || _runs.Latest(roomId) is { Status: RunStatus.Parked };
       var (dir, root, status, leased, interrupted) = (x.WorktreeRoom!, x.RootMessageId, x.Status, x.WorktreeLeased, x.Interrupted);
       _closing.RemoveAll(t => t.IsCompleted);
       _closing.Add(Task.Run(async () =>
       {
           string? note;
           try { note = await _worktrees.CloseAsync(new ExchangeWorktrees.CloseRequest(dir, root, roomId, status, leased, interrupted, runOwns, RoomCommits.IdentityOf(_owner), RoomCommits.OwnerMessage(_owner, roomId)), CancellationToken.None); }
           catch (Exception e) { note = $"Exchange #{root}: its worktree could not be closed: {e.GetType().Name}: {e.Message}"; }
           _events.Writer.TryWrite(new WorktreeClosedEvent(roomId, note));
       }));
   }
   ```
   The `WorktreeClosedEvent` handler also calls `CloseIdleWorktrees(e.RoomId)` after posting, so a second closed exchange of the same room closes next. In `Launch`, where Task 4 sets `x.WorktreeRoom = directory`, also `if (!_worktreeExchanges.Contains(x)) _worktreeExchanges.Add(x);`.
3. Exclusivity with a running close: in `LaunchDue`, skip an exchange whose room is in `_closingRooms` when `over is null && exclusive` (`if (exclusive && over is null && _closingRooms.Contains(x.RoomId)) continue;`); in `ArmWake`, the same condition skips its wake (the close's event wakes the loop). Worktree launches (`over` non-null) do not wait on a close: they write only their own worktree and the gated owner commit.
4. Call sites: in `OnFinished` directly after `var note = ExchangePolicy.Finished(h.Exchange, id);` call `CloseIdleWorktrees(room);` and again at the end, before `Publish(room)` (a room-directory spawn finishing releases a deferred close); in `Publish` first line `CloseIdleWorktrees(roomId);`; in `AddExchange` before `list.Clear()` / `list.RemoveAll(...)`, `CloseIdleWorktrees(roomId);`.
5. `StopAsync`: after the existing wait on in-flight spawns, `try { await Task.WhenAll(_closing.ToList()).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); } catch (Exception e) when (e is TimeoutException or OperationCanceledException) { Console.Error.WriteLine("spawner: some worktree closes did not finish within 10 s of shutdown"); }`.
6. Tests (fake runner writes files into `spec.WorkingDirectory`):
   - `R35_two_exchanges_edit_at_once_and_both_merge_into_the_room_on_conclusion` (files `a.txt` and `b.txt` both in `dir`, two merge notes, no `chopitup/x*` branch left, `<dir>.worktrees` has no `x*` children).
   - `R35_a_conflict_keeps_the_branch_and_names_it` (both write `c.txt`; one merge note, one note containing the losing branch and `c.txt`; `dir` clean).
   - `R35_a_stopped_exchange_keeps_its_branch` (hold the spawn, `POST .../exchanges/{root}/stop`, note says `was not merged: it was stopped`).
   - `R35_a_superseded_exchange_merges_when_its_last_spawn_ends`.
   - `R35_a_stopped_or_timed_out_spawn_keeps_the_branch_of_a_concluded_or_superseded_exchange` (pass 2 F2): stop-one on a superseded exchange with a live spawn → kept-branch note `a spawn of it was stopped or timed out`; and a spawn that times out under the test limits → same.
   - `R35_a_worktree_launch_and_a_close_wait_for_a_dying_room_directory_spawn` (AC9, pass 2 F4, replacing pass 1's vacuous test): start a run in the room (reuse the run fixture in `SpawnerServiceTests.Runs.cs`) whose fake conductor handler awaits a `TaskCompletionSource` WITHOUT observing the cancellation token; `POST .../exchange/stop` ends the run; then post an owner message mentioning a worker → `_runner.NoSpecWithin(TimeSpan.FromSeconds(1))`; release the conductor → the worker's spec arrives with a worktree working directory. If the run fixture cannot express this, STOP and report.
7. Test follow-through (pass 2 F7): after this task every directory-room test whose exchange concludes (e.g. `M9_A6_A8_A9_A10_...`, `M9_A6_a_codex_...`, `M9_A9_...`) must wait for the close note (`WaitForMessageIn(room, m => m.Author == "hub" && m.Body.StartsWith("Exchange #"))`) before reading `dir`, then assert on the merged history: `git log --format=%an` newest first is the hub (merge), then the agent, then the owner when there were owner edits; a room whose HEAD was unborn adds `ChopItUp hub` for `Room trail start` at the bottom. Never switch an assertion to read the worktree after close, never delete an assertion. List every test touched in the report.
7. Declared residue: an exchange dropped from the room list while still `Open` with pending entries never becomes closed; it stays in `_worktreeExchanges` until the hub stops and `RecoverAsync` clears its worktree at the next start.

### Task 6: start-up recovery and docs

Files: `src/ChopItUp.Hub/Spawning/SpawnerService.cs` (`ExecuteAsync` start-up loop), `README.md` (the directory-room paragraph), `SpawnerServiceTests.Rooms.cs`.

1. In the `ExecuteAsync` start-up `foreach`, after the dirty-tree warning: `var recovered = await _worktrees.RecoverAsync(room.Directory, stoppingToken); if (recovered is not null) PostNote(room.Id, recovered);`.
2. README: where it says a directory room runs one spawn at a time (grep `one spawn at a time`; if README has no such sentence, add two sentences to the directory-room section), state that each exchange works in `<room dir>.worktrees\x<root>` on branch `chopitup/x<root>`, is merged when it concludes, and keeps its branch on a conflict, a stop or a run.
3. Test `R35_hub_start_recovers_leftover_worktrees`: create a worktree + branch + dirty file under a room before starting the host (or restart the host if `HubTestHost` supports it), then assert the note and the committed file on the branch.

## Verification (orchestrator, after Task 6)

Order: 1, 2, mutation pass, 3, then 5 (deploy last). Mutation pass (pass 2 m4, as row 32): revert one at a time and confirm a named test fails, then restore: the `OperationInProgressUnlocked` guard in `CommitAllAsync`; the `Interrupted` keep arm; the `_closingRooms` launch skip; the worktree-launch wait on a room-directory spawn; the not-registered → return-null arm; the `IsAncestorOfHeadAsync` filter in recovery.
1. `dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal` 0 warnings; `dotnet test ChopItUp.slnx -c Debug --nologo -v minimal` all green, count = baseline + new.
2. Branch-level `mattpocock-skills:code-review` (no subagents) with this plan as the spec.
5. After step 3: deploy with `tools\Deploy-ChopItUp.ps1` (live hub stopped by verified PID, idle rooms only), `tools\Invoke-M4SelfCheck.ps1 -PublishDir <staging> -TargetDir 'C:\Self Apps\ChopItUp'`, restart the exe once, `GET /health` schema 10, and the live room `chopitup` (a directory room) shows no startup recovery note unless a worktree was really left (critique m5).
3. Synthetic dry run on a scratch hub (`--data` in the session scratchpad, a scratch room bound to a scratch repo under the scratchpad, a stub `codex.cmd` first on PATH that writes a file named from its own `%RANDOM%` into its cwd, or `c.txt` for the conflict pair): two concurrent exchanges merge; a conflicting pair keeps its branch; a stop keeps its branch; kill the hub by PID with a worktree open and restart → recovery note. Evidence: hub notes via `GET /api/rooms/<id>/messages`, `git -C <scratch repo> log --graph --oneline`, `git worktree list`.
