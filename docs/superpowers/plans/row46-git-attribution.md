# Row 46 — Git attribution in hub commits

**Goal:** every commit the hub makes in a room directory carries the repository's own configured git identity as author and committer, and a `Co-authored-by` trailer naming the model's host whenever the commit contains a spawn's changes, so `git log` in an owner repository reads like every other commit there and nothing is credited to a human that a model wrote, or to a model that a human wrote.

**Architecture:** `GitTrail` learns one identity rule and one trailer rule. Identity: the hub asks git itself who it would commit as (`git -c user.useConfigOnly=true var GIT_COMMITTER_IDENT`: config or `GIT_*` environment, never a guess from the account or host name); when that resolves the hub injects nothing, otherwise it injects its own `ChopItUp hub <hub@chopitup.local>` through the four `GIT_AUTHOR_*`/`GIT_COMMITTER_*` environment variables, which beat every config. `MemoryGit` opts out of the rule and keeps the hub for both fields. Trailers: a caller may pass trailer lines; they land as the message's last paragraph only when something is staged (an empty turn commit carries none); a merge carries the distinct `Co-authored-by` trailers of the commits it merges. `RoomCommits` maps a host to its trailer line; the owner's and the participant's commits pass `author: null` (the repository's identity) and the turn commit passes the host trailer; the hub's bookkeeping commits (`Room trail start`, leftover sweeps) keep the hub as author. A stub-CLI scratch check proves the shape end to end without a model call.

**Author model:** Fable 5.1 (session model; matches the HIGH routing, so no mismatch to declare).

**Blast radius: HIGH.** Justification: `GitTrail` is the only writer of every room repository and the memory store; a wrong identity or a broken commit path silently mis-credits or loses the trail in the owner's real repositories, and the change touches the spawn loop and the exchange merge. Tier evidence (`Check-BlastRadius.ps1`, 2026-09-19, over the 13 planned paths):

```
TIER-EVIDENCE HIGH src/ChopItUp.Hub/Git/GitTrail.cs:403 delete-replace: try { Directory.Delete(admin, recursive: true); }
TIER-EVIDENCE HIGH src/ChopItUp.Hub/Spawning/SpawnerService.cs:1182 delete-replace: PostNote(room, $"@{id} replied without posting to the room (exit code {exit}). Its reply:\n\n{Trunca.
TIER-EVIDENCE HIGH src/ChopItUp.Hub/Spawning/SpawnerService.cs:1036 secrets: File.WriteAllText(mcpPath, SpawnCommands.ClaudeMcpConfigJson(McpUrl(), token, mcpToolTimeoutMs));
TIER-EVIDENCE HIGH tests/ChopItUp.Hub.Tests/Git/GitTrailTests.cs:346 delete-replace: File.Delete(Path.Combine(wt, "dirty.txt"));
new file (no content scan): tools/Invoke-Row46AttributionCheck.ps1
TIER-EVIDENCE: HIGH triggers in 3 of 13 files (delete-replace, secrets)
```
None of the four lines is touched by this plan (they are pre-existing code in touched files); the tier stands on the writer-of-every-room-repository argument above.

*Written for builder-subagent execution; if something doesn't match, STOP and report rather than guess.*

## Design rulings (made on the owner's behalf; the critic attacks these first)

- **R1 — the row amends M9 decision 6.** The room's git author is no longer the participant (`Opus <opus@chopitup.local>`); it is the repository's configured identity, which is what the folder `AGENTS.md` (`C:\Agent Projects\AGENTS.md`, 2026-06-07) requires and what a push of the room clone to GitHub would show sensibly. The participant is still named in every subject (`opus: turn 1/8 in room lab`). *Declined alternative:* keep the participant author and only add the trailer — it leaves the `AGENTS.md` first line unmet and, pushed, shows an unknown author on every hub commit.
- **R2 — a Claude turn gets a trailer too.** The row names only the Codex trailer, but under R1 a Claude turn's changes would otherwise be credited to the human alone, which is the false attribution the row exists to remove. The line is `Co-authored-by: Claude <noreply@anthropic.com>`, same key spelling as the Codex line so one `%(trailers:key=Co-authored-by)` query reads both. The trailer names the host tool, not the model row: the subject already carries the participant id, and `AGENTS.md` wants the exact Codex string. *Critique pass 1 (MINOR 6) noted this repo's own commits use model-specific lines (`Co-authored-by: Claude Opus 5 <noreply@anthropic.com>`); declined: GitHub aggregates co-authors by address, so `Claude` and `Claude Opus 5` are one contributor there, the `claude` participant row would otherwise read `Claude Claude`, and the generic line is Anthropic's documented convention for Claude Code.*
- **R3 — a trailer only when something is staged.** `AGENTS.md`: "Do not add the trailer to commits where Codex did not contribute to the committed changes." An empty turn commit (the spawn happened, nothing changed) and the owner's `edits before the next spawn` commit carry none.
- **R4 — the identity is what git would commit with, guesses excluded.** The probe is `git -c user.useConfigOnly=true var GIT_COMMITTER_IDENT` and the same for `GIT_AUTHOR_IDENT`, run at the trail's root: both exit 0 means someone set a name and an address for both roles (repository, global or system config, or the `GIT_COMMITTER_*` and `GIT_AUTHOR_*` environment pairs) and git commits with them, the hub injecting nothing; either failing (nothing set, half an identity, only `EMAIL` in the environment, a name git would guess from the Windows account, or only one role's environment pair set) means the hub injects its own identity through `GIT_AUTHOR_NAME/EMAIL` and `GIT_COMMITTER_NAME/EMAIL` on that one git call, which beat every config and any stray single `GIT_*` variable. *Critique pass 2 (F2) measured that a committer pair alone makes the committer probe pass while `git commit` still dies on the author, and an author pair alone the reverse; probing both roles closes that.* Measured 2026-09-19 on git 2.45.2: exit 0 with the global config and with `GIT_COMMITTER_NAME`+`EMAIL` set; exit 128 with no config, with a local `user.name` only, with a local `user.email` only, and with `EMAIL` only; the four env variables beat a configured global identity on `git commit`. *Critique pass 1 (MAJOR 4) replaced the earlier `git config --get` probe, which environment identities defeated.*
- **R5 — the memory store keeps the hub for both fields.** It is the hub's own repository under `data\memory\`, not an owner repository; `MemoryGitTests` stays green unchanged.
- **R6 — bookkeeping commits follow the identity rule and carry no trailer.** `Room trail start` (empty), `Uncommitted at the close of exchange #N` and `Uncommitted when the hub restarted` (leftovers a turn commit did not capture, which at HEAD only happens when the hub died between a spawn's end and its commit) pass `author: null` like every other room commit: `AGENTS.md` line 1 wants the repository identity as author and committer with no exemption, and a pushed room shows no invented author anywhere. No trailer: the branch's own turn commits say who ran, and the subject says what the commit is. *Critique pass 1 (MAJOR 5) replaced the earlier ruling that kept `--author=ChopItUp hub` on these three.* *Declined alternative:* derive trailers for leftovers from the branch's earlier turn commits — it over- or under-credits (the crashed spawn's host need not be among them). The explicit `author` parameter survives for the memory store (`MemoryGit` passes `Hub`) and for tests; no room path passes one.
- **R7 — a merge commit carries the union.** `Merge exchange #N (room)` is authored with the identity rule and appends, as a second `-m` paragraph, the distinct `Co-authored-by` lines found on `HEAD..<branch>` (`git log --format=%(trailers:key=Co-authored-by,valueonly)`), so `git log --first-parent` in the room stays honest. Measured 2026-09-19 on git 2.45.2: two `-m` values become two paragraphs and `%(trailers:key=…,valueonly)` on the merge lists both lines.
- **R8 — the hub note says `for`, not `as`.** `Committed <hash> as sonnet: …` would now be untrue (the author is the owner); it becomes `Committed <hash> for sonnet: …`. `Your edits were committed first as <hash>` keeps `as` (a hash, not an identity).
- **R9 — the Trail dialog is unchanged.** It shows `%an <%ae>` per line, which after this row is the owner's identity on every hub commit; the subject names the participant. A `with Codex` chip from the trailers is a visible-copy change the owner signs off separately, not this row.
- **R10 — unknown host, no trailer.** `RoomCommits.CoAuthorTrailer` returns null for any host other than `claude`/`codex` (the spawner throws on those before a commit anyway: `SpawnerService.cs:1063`); nothing is invented.
- **R11 — a trailer only for a spawn that launched; the run path shares the commit.** The turn commit at `SpawnerService.cs:1116` happens "always, empty or not, timed out or not", including when the runner threw before any process started (`launch failed: …`). The trailer is passed only when `_runner.RunAsync` returned (the process started, whatever it did after): a launch that never happened credits no host even if the tree changed meanwhile. On the worktree path the spawn is alone in its tree, so a non-empty turn commit is the model's. On the run path (`UsesWorktrees` false while a run is active or parked, `SpawnerService.cs:850-851`) the spawn works in the room directory itself; the owner's edits from before the spawn are committed separately first (`:1088-1090`), but an edit the owner makes while that spawn is working is swept into the turn commit and shares its trailer. Accepted and declared: `Co-authored-by` on a mixed commit is literally true, the one false case (the model changed nothing and the owner edited during its turn) is rare and the alternative (no trailer on the run path) would strip the Codex credit from the roadmap-in-room runs, which are exactly the room clone under the folder `AGENTS.md`. README's trail paragraph says so. *From critique pass 1 (MAJOR 3).*

## Acceptance

- **AC1** WHEN the hub commits in a room directory whose `git config` resolves both `user.name` and `user.email`, THE SYSTEM SHALL make that identity both the author and the committer of the commit.
- **AC2** WHEN git at the room directory cannot resolve a set identity (`git -c user.useConfigOnly=true var GIT_COMMITTER_IDENT` exits non-zero: no config, half a config, or only a guessable name or `EMAIL`), THE SYSTEM SHALL commit as `ChopItUp hub <hub@chopitup.local>` for both author and committer.
- **AC3** WHEN a spawn whose process was launched ends and its turn commit contains at least one staged change, THE SYSTEM SHALL end the commit message with a paragraph holding exactly `Co-authored-by: Codex <noreply@openai.com>` for a `codex` host or `Co-authored-by: Claude <noreply@anthropic.com>` for a `claude` host, readable by `git log -1 --format=%(trailers:key=Co-authored-by,valueonly)`.
- **AC4** WHEN a turn commit is empty, or the spawn's process never launched, or the commit is the owner's `edits before the next spawn`, THE SYSTEM SHALL write no `Co-authored-by` trailer.
- **AC5** WHEN the hub merges an exchange branch, THE SYSTEM SHALL author the merge commit under AC1/AC2 and append, as its last paragraph, the distinct `Co-authored-by` trailers of the commits being merged, and none when they carry none.
- **AC6** WHEN the hub makes a `Room trail start`, `Uncommitted at the close of exchange #N` or `Uncommitted when the hub restarted` commit, THE SYSTEM SHALL author and commit it under AC1/AC2 and write no trailer.
- **AC7** WHEN the memory store commits an approval, THE SYSTEM SHALL keep `ChopItUp hub <hub@chopitup.local>` as both author and committer, whatever the machine's git config says.
- **AC8** WHEN the hub posts the trail note for a turn, THE SYSTEM SHALL word it `Committed <hash> for <id>: …`; the spawn prompt SHALL no longer say `under your name`; and README's trail paragraph SHALL contain the literal `Co-authored-by: Codex <noreply@openai.com>` and no longer `id@chopitup.local`.
- **AC9** WHEN `tools\Invoke-Row46AttributionCheck.ps1` runs against a scratch hub with stub CLIs and a scratch directory room configured `Scratch Owner <scratch-owner@example.test>`, THE SYSTEM SHALL produce commits that satisfy AC1, AC3, AC4 and AC5 as read by `git log` in the room directory, and the same script SHALL fail at least one check when `RoomCommits.CoAuthorTrailer` is made to return null.

## Lessons consulted

`docs/LESSONS.md` headings grepped for git, attribution, notes, review: **M27** (a note that names an actor takes the actor as a parameter — `HubNotes.Trail` already takes `participantId`; the wording change keeps it), **Row 35** (a linked worktree's `.git` is a file; refuse while a merge is in progress; never blanket-prune — all three already encoded in `GitTrail`, untouched here), **Row 43** (a count claim's recheck enumerates the population — every population claim below has a grep count), **Row 36** (CI flakes: `gh pr checks --watch` needs a 20 s sleep after the push). No entry covers git identity resolution; the measured git facts in the ledger stand in.

## Claim ledger

| # | Claim | Verified at (commit) | Recheck (pwsh, exit 0 = holds) |
|---|-------|----------------------|--------------------------------|
| 1 | Baseline: Desktop 108 / Core 328 / Hub 910 green, 0 failed (measured this session 2026-09-19; the Hub project took 14 m 38 s under load, background it) | 7065938 | `pwsh -c "$o = dotnet test ChopItUp.slnx -c Debug --nologo -v minimal 2>&1 \| Out-String; if ($o -match 'Failed:\s+[1-9]' -or $o -match 'error') { exit 1 } else { exit 0 }"` |
| 2 | Client baseline: vitest 18 files / 264 tests green (untouched by this plan) | 7065938 | `pwsh -c "Push-Location src/ChopItUp.Hub/client; $o = npx vitest run 2>&1 \| Out-String; Pop-Location; if ($o -match 'Tests\s+264 passed') { exit 0 } else { exit 1 }"` |
| 3 | `GitTrail.cs:43` is `public static readonly GitIdentity Hub = new("ChopItUp hub", "hub@chopitup.local");` and `:48-49` is the `Committer` array with `user.name`/`user.email`/`commit.gpgsign=false`/`core.autocrlf=false` | 7065938 | `pwsh -c "$l = Get-Content src/ChopItUp.Hub/Git/GitTrail.cs; if ($l[42] -match 'GitIdentity Hub = new\(\"ChopItUp hub\", \"hub@chopitup.local\"\)' -and $l[48] -match 'user.name=' -and $l[48] -match 'core.autocrlf=false') { exit 0 } else { exit 1 }"` |
| 4 | `GitTrail.cs:166` builds the commit as `new List<string>(Committer) { "commit", "-q", "--author=" + author, "-F", "-" }`; `:439` builds the merge as `new List<string>(Committer) { "merge", "--no-ff", "-m", message, branch }` | 7065938 | `pwsh -c "$l = Get-Content src/ChopItUp.Hub/Git/GitTrail.cs; if ($l[165] -match '\"--author=\" \+ author' -and $l[438] -match '\"merge\", \"--no-ff\", \"-m\", message, branch') { exit 0 } else { exit 1 }"` |
| 5 | `CommitAllAsync(` appears 8 times in `src` (definition `GitTrail.cs:144`; `MemoryGit.cs:18`; `ExchangeWorktrees.cs:71,120,150,198`; `SpawnerService.cs:1090,1116`) — the whole call-site population | 7065938 | `pwsh -c "if ((git grep -n 'CommitAllAsync(' -- src \| Measure-Object).Count -eq 8) { exit 0 } else { exit 1 }"` |
| 6 | `MergeAsync(` is called once outside `GitTrail.cs`: `ExchangeWorktrees.cs:154` | 7065938 | `pwsh -c "if ((git grep -n -E '(^\|[^A-Za-z])MergeAsync\(' -- src ':!src/ChopItUp.Hub/Git/GitTrail.cs' \| Measure-Object).Count -eq 1) { exit 0 } else { exit 1 }"` |
| 7 | `RoomCommits.IdentityOf` is defined at `RoomCommits.cs:16` and used at `SpawnerService.cs:346, 1090, 1116` and `RoomCommitsTests.cs:16, 17` — 6 lines in `src`+`tests`, nowhere else | 7065938 | `pwsh -c "if ((git grep -n 'IdentityOf' -- src tests \| Measure-Object).Count -eq 6) { exit 0 } else { exit 1 }"` |
| 8 | `ExchangeWorktrees.CloseRequest` (`ExchangeWorktrees.cs:95`) has a `GitIdentity Owner` field, constructed at `SpawnerService.cs:345-346` and by the `Close(` helper in `ExchangeWorktreesTests.cs:70-72` | 7065938 | `pwsh -c "if ((git grep -n 'bool RunOwnsRoom, GitIdentity Owner, string OwnerMessage' -- src \| Measure-Object).Count -eq 1 -and (git grep -n 'runOwnsRoom, Owner, \$\"owner: edits' -- tests \| Measure-Object).Count -eq 1) { exit 0 } else { exit 1 }"` |
| 9 | `HubNotes.cs:92` reads `{TrailPrefix}{agent.Hash} as {participantId}:`; exactly 5 test lines assert the `as <id>:` wording: `RoomCommitsTests.cs:34,35,37`, `SpawnerServiceTests.Rooms.cs:132,544` | 7065938 | `pwsh -c "if ((git grep -n 'agent.Hash} as {participantId}' -- src \| Measure-Object).Count -eq 1 -and (git grep -n -E 'as (sonnet\|gpt-6-astra\|opus): ' -- tests \| Measure-Object).Count -eq 5) { exit 0 } else { exit 1 }"` |
| 10 | Participant hosts the spawner starts are exactly `"claude"` (`SpawnerService.cs:1022`) and `"codex"` (`:1051`); `host` is a local at `:1073` in scope at the turn commit `:1116` | 7065938 | `pwsh -c "if ((git grep -n -E 'case \"(claude\|codex)\":' -- src/ChopItUp.Hub/Spawning/SpawnerService.cs \| Measure-Object).Count -eq 2) { exit 0 } else { exit 1 }"` |
| 11 | Room repositories in `SpawnerServiceTests.Rooms.cs` get no local identity: `MakeRoom` (`:26-32`) only `InitAsync`es and the file contains no `user.name`. The whole population of author/committer format assertions in `tests` is exactly 6 lines: `GitTrailTests.cs:83`, `MemoryGitTests.cs:35`, `SpawnerServiceTests.Rooms.cs:139` (`%an <%ae>\|%cn\|%s`, expectations `:141-143`), `:233` (`%an` = `Opus` on the branch), `:548` (`%an <%ae>`, expectations `:550-552`), `:575` (`%an\|%s`, expectations `:576-577` = `ChopItUp hub\|Merge…`, `Sonnet\|sonnet: turn…`) | 7065938 | `pwsh -c "$f = 'tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.Rooms.cs'; if ((git grep -c 'user.name' -- $f) -eq $null -and (git grep -n -E '%an\|%cn\|%ae\|%ce' -- tests \| Measure-Object).Count -eq 6) { exit 0 } else { exit 1 }"` |
| 12 | `GitTrailTests.cs` asserts a committer (`%cn`/`%ce`) on exactly one line (`:84`) and an author from `LogAsync` at `:159`; both use the explicit `Opus` identity | 7065938 | `pwsh -c "$f = 'tests/ChopItUp.Hub.Tests/Git/GitTrailTests.cs'; if ((git grep -n '%cn' -- $f \| Measure-Object).Count -eq 1 -and (git grep -n 'Assert.Equal(\"Opus <opus@chopitup.local>\", log\[0\].Author)' -- $f \| Measure-Object).Count -eq 1) { exit 0 } else { exit 1 }"` |
| 13 | `RoomsApiTests.cs:220-221` call `CommitAllAsync` with explicit `Owner`/`Opus` identities and `:226` asserts `author` = `Opus <opus@chopitup.local>` | 7065938 | `pwsh -c "if ((git grep -n 'new GitIdentity(\"Opus\", \"opus@chopitup.local\")' -- tests/ChopItUp.Hub.Tests/RoomsApiTests.cs \| Measure-Object).Count -eq 1) { exit 0 } else { exit 1 }"` |
| 14 | `MemoryGitTests.cs:39` asserts `%an <%ae> %s` starts with `ChopItUp hub <hub@chopitup.local> Approve memory proposal` (no committer field yet) | 7065938 | `pwsh -c "if ((git grep -n 'ChopItUp hub <hub@chopitup.local> Approve memory proposal' -- tests/ChopItUp.Hub.Tests/Memory/MemoryGitTests.cs \| Measure-Object).Count -eq 1) { exit 0 } else { exit 1 }"` |
| 15 | `MemoryGit` derives from `GitTrail` and overrides only `LogName` (`MemoryGit.cs:10-12`); `GitTrail.LogName` is `protected virtual` (`GitTrail.cs:83`) | 7065938 | `pwsh -c "if ((git grep -n ': GitTrail(root, resolve, runner)' -- src/ChopItUp.Hub/Memory/MemoryGit.cs \| Measure-Object).Count -eq 1 -and (git grep -n 'protected virtual string LogName' -- src/ChopItUp.Hub/Git/GitTrail.cs \| Measure-Object).Count -eq 1) { exit 0 } else { exit 1 }"` |
| 16 | `ProcessSpec` is a record with an `Environment` dictionary the real runner applies on top of the inherited environment (`ProcessRunner.cs:7-15, 48`) | 7065938 | `pwsh -c "if ((git grep -n 'foreach (var (k, v) in spec.Environment) psi.Environment\[k\] = v;' -- src \| Measure-Object).Count -eq 1) { exit 0 } else { exit 1 }"` |
| 17 | `chopitup.local` appears in tracked `.md`/`.ps1` files (board row and plans excluded) on exactly 3 lines: README `:191-192` (the trail paragraph, which also carries the note `Committed <hash> as <id>` at `:193`) and `tools/Invoke-M9RoomCheck.ps1:154` | 7065938 | `pwsh -c "if ((git grep -n 'chopitup.local' -- '*.md' '*.ps1' ':!ROADMAP.md' ':!docs/superpowers/plans/*' \| Measure-Object).Count -eq 3) { exit 0 } else { exit 1 }"` |
| 22 | `tools/Invoke-M9RoomCheck.ps1` (a live check that spends real Claude and Codex calls; `docs/verification.md:50`, `README.md:210`) asserts the old identities: `:133` note `*as $Participant*`, `:135` `$ExpectAuthor\|ChopItUp hub\|…`, `:142`/`:147` `-ExpectAuthor 'Sonnet'`/`'GPT-6 Astra'`, `:144` `$authors[1] -eq 'Owner'`, `:154` `* <*@chopitup.local>`; its room directory is `$roomDir = Join-Path $RoomsRoot 'live-check'` (`:85`) and no other file under `tools/` names an expected author | 7065938 | `pwsh -c "if ((git grep -n -E 'ExpectAuthor\|authors\[1\] -eq\|as .Participant' -- tools \| Measure-Object).Count -eq 6) { exit 0 } else { exit 1 }"` |
| 23 | `SpawnPrompt.cs:369` says `the hub commits your work under your name when you finish`; the byte-for-byte golden capture `tests/ChopItUp.Hub.Tests/Spawning/golden-prompt-ddfa572.txt` carries the same sentence (its path is fixed in `SpawnPromptTests.cs:562`; a `.gitattributes` marks it `-text`) | 7065938 | `pwsh -c "if ((git grep -n 'commits your work under your name' -- src tests \| Measure-Object).Count -eq 2) { exit 0 } else { exit 1 }"` |
| 24 | `ExchangeWorktreesTests.RoomWithCommit` (`:41-48`) seeds with the explicit `Owner` identity and no local config; the file asserts no `%an`/`%cn`; one test mentions `Uncommitted` | 7065938 | `pwsh -c "$f = 'tests/ChopItUp.Hub.Tests/Rooms/ExchangeWorktreesTests.cs'; if ((git grep -n 'CommitAllAsync(\"seed\", Owner' -- $f \| Measure-Object).Count -eq 1 -and (git grep -c 'Uncommitted' -- $f) -match ':1$') { exit 0 } else { exit 1 }"` |
| 18 | git ≥ 2.32 on this machine (2.45.2 measured): `GIT_CONFIG_GLOBAL=<missing file>` + `GIT_CONFIG_NOSYSTEM=1` makes `git config --get user.name` exit 1; `git diff --cached --quiet` exits 1 with staged changes and 0 clean; `%(trailers:key=Co-authored-by,valueonly)` prints one value per line, key matched case-insensitively; two `-m` on `git merge` become two paragraphs | 7065938 | `pwsh -c "$m = [regex]::Match((git --version), '\d+\.\d+\.\d+'); if ($m.Success -and [version]$m.Value -ge [version]'2.32') { exit 0 } else { exit 1 }"` |
| 19 | The same git facts hold on the CI runner (`windows-latest`) | — | — (unautomatable here; the PR's CI run is the check) |
| 20 | `tools\Invoke-Row43MentionCheck.ps1` is the scaffold: scratch root under `$env:TEMP`, `stub\` first on a stripped PATH, `ChopTokenHelpers.ps1` for the owner bearer, `Add-Check`/`Invoke-Api`/`Wait-HubNotePrefix` helpers, hub started by PID and killed by `taskkill /T /F /PID`; `tools\Invoke-Row35LiveCheck.ps1:100-101` says a typed directory's PARENT must exist and the hub `git init`s the room directory itself | 7065938 | `pwsh -c "if ((git grep -n -E 'function (Add-Check\|Invoke-Api\|Wait-HubNotePrefix)' -- tools/Invoke-Row43MentionCheck.ps1 \| Measure-Object).Count -eq 3 -and (git grep -n 'PARENT to already exist' -- tools/Invoke-Row35LiveCheck.ps1 \| Measure-Object).Count -eq 1) { exit 0 } else { exit 1 }"` |
| 21 | The live room clone's newest commit is the defect: `git -C "C:\Agent Projects\ChopItUp-room" log -1` shows author `Opus <opus@chopitup.local>`, committer `ChopItUp hub <hub@chopitup.local>`, in a repository whose other commits are `yovanmc` | 5c924f0 (room clone) | — (read-only fact about a live directory; not rechecked by preflight) |

## Task 1 — `GitTrail`: the identity rule, trailers, the merge union (sonnet)

**Files:** `src/ChopItUp.Hub/Git/GitTrail.cs`, `src/ChopItUp.Hub/Memory/MemoryGit.cs`, `tests/ChopItUp.Hub.Tests/Git/GitTrailTests.cs`, `tests/ChopItUp.Hub.Tests/Memory/MemoryGitTests.cs`.

### 1a. RED — new tests in `GitTrailTests.cs`

Add a runner decorator and a fixed "no identity anywhere" environment at the top of the class (after `RawGit`):

```csharp
    /// <summary>The real runner with extra environment entries on every spec. The "no git identity
    /// anywhere" case is reproduced by pointing git's global config at a file that does not exist and
    /// skipping the system one, without touching this process's environment (tests run in parallel).</summary>
    private sealed class EnvRunner(IReadOnlyDictionary<string, string> extra) : IProcessRunner
    {
        private readonly ProcessRunner _inner = new();
        public Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation)
        {
            var env = new Dictionary<string, string>(spec.Environment);
            foreach (var (k, v) in extra) env[k] = v;
            return _inner.RunAsync(spec with { Environment = env }, timeout, cancellation);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> NoIdentity = new Dictionary<string, string>
    {
        ["GIT_CONFIG_GLOBAL"] = Path.Combine(Path.GetTempPath(), "chopitup-no-such-gitconfig"),
        ["GIT_CONFIG_NOSYSTEM"] = "1",
    };

    private static readonly GitIdentity RoomOwner = new("Room Owner", "room-owner@example.test");

    /// <summary>A repository at <paramref name="dir"/> whose own config names <see cref="RoomOwner"/>:
    /// what an owner's real repository looks like from the hub's side.</summary>
    private static async Task ConfigureRoomOwner(string dir)
    {
        Assert.Equal(0, (await RawGit(dir, "config", "user.name", RoomOwner.Name)).ExitCode);
        Assert.Equal(0, (await RawGit(dir, "config", "user.email", RoomOwner.Email)).ExitCode);
    }
```

Then the tests (names are the acceptance ids):

```csharp
    [Fact]
    public async Task Row46_A1_a_configured_repository_identity_is_author_and_committer_and_the_hub_never_overrides_it()
    {
        var git = new GitTrail(_dir);
        Assert.True(await git.InitAsync());
        await ConfigureRoomOwner(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");

        var c = await git.CommitAllAsync("owner: edits before the next spawn in room lab\n", author: null, allowEmpty: false);
        Assert.True(c.Created);
        Assert.Equal(RoomOwner, await git.ConfiguredIdentityAsync());
        var line = (await GitOut(_dir, "log", "-1", "--format=%an <%ae>|%cn <%ce>")).Trim();
        Assert.Equal("Room Owner <room-owner@example.test>|Room Owner <room-owner@example.test>", line);
    }

    [Fact]
    public async Task Row46_A2_no_identity_anywhere_or_half_an_identity_falls_back_to_the_hub_for_both()
    {
        var git = new GitTrail(_dir, runner: new EnvRunner(NoIdentity));
        Assert.True(await git.InitAsync());
        Assert.Null(await git.ConfiguredIdentityAsync());
        var c = await git.CommitAllAsync("opus: turn 1/8 in room lab\n", author: null, allowEmpty: true);
        Assert.True(c.Created);
        Assert.Equal("ChopItUp hub <hub@chopitup.local>|ChopItUp hub <hub@chopitup.local>",
            (await GitOut(_dir, "log", "-1", "--format=%an <%ae>|%cn <%ce>")).Trim());

        // Half an identity (a name, no address) is no identity: still the hub for both.
        Assert.Equal(0, (await RawGit(_dir, "config", "user.name", "Half")).ExitCode);
        Assert.Null(await git.ConfiguredIdentityAsync());
        await git.CommitAllAsync("opus: turn 2/8 in room lab\n", author: null, allowEmpty: true);
        Assert.Equal("ChopItUp hub <hub@chopitup.local>|ChopItUp hub <hub@chopitup.local>",
            (await GitOut(_dir, "log", "-1", "--format=%an <%ae>|%cn <%ce>")).Trim());
    }

    [Fact]
    public async Task Row46_A3_A4_trailers_are_the_last_paragraph_and_only_when_something_is_staged()
    {
        var git = new GitTrail(_dir);
        Assert.True(await git.InitAsync());
        await ConfigureRoomOwner(_dir);
        string[] codex = ["Co-authored-by: Codex <noreply@openai.com>"];

        var empty = await git.CommitAllAsync("gpt-6-astra: turn 1/8 in room lab\n\nShell commands run: none.\n", author: null, allowEmpty: true, trailers: codex);
        Assert.True(empty.Created);
        Assert.Equal(0, empty.FilesChanged);
        Assert.Equal("", (await GitOut(_dir, "log", "-1", "--format=%(trailers:key=Co-authored-by,valueonly)")).Trim());
        Assert.DoesNotContain("Co-authored-by", await GitOut(_dir, "log", "-1", "--format=%B"));

        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        var real = await git.CommitAllAsync("gpt-6-astra: turn 2/8 in room lab\n\nShell commands run (1):\n  1. dir\n", author: null, allowEmpty: true, trailers: codex);
        Assert.Equal(1, real.FilesChanged);
        Assert.Equal("Codex <noreply@openai.com>", (await GitOut(_dir, "log", "-1", "--format=%(trailers:key=Co-authored-by,valueonly)")).Trim());
        var body = (await GitOut(_dir, "log", "-1", "--format=%B")).Replace("\r\n", "\n");
        Assert.EndsWith("  1. dir\n\nCo-authored-by: Codex <noreply@openai.com>\n", body);
    }

    [Fact]
    public async Task Row46_A5_a_merge_carries_the_distinct_trailers_of_the_commits_it_merges_and_none_when_there_are_none()
    {
        var git = new GitTrail(_dir);
        Assert.True(await git.InitAsync());
        await ConfigureRoomOwner(_dir);
        await git.CommitAllAsync("Room trail start", GitTrail.Hub, allowEmpty: true);

        var wt = _dir + "_wt";
        Assert.Null(await git.AddWorktreeAsync(wt, "chopitup/x7", newBranch: true));
        var w = git.WithRoot(wt);
        File.WriteAllText(Path.Combine(wt, "one.txt"), "1");
        await w.CommitAllAsync("gpt-6-astra: turn 1/8 in room lab\n", author: null, allowEmpty: false, trailers: ["Co-authored-by: Codex <noreply@openai.com>"]);
        File.WriteAllText(Path.Combine(wt, "two.txt"), "2");
        await w.CommitAllAsync("opus: turn 2/8 in room lab\n", author: null, allowEmpty: false, trailers: ["Co-authored-by: Claude <noreply@anthropic.com>"]);
        File.WriteAllText(Path.Combine(wt, "three.txt"), "3");
        await w.CommitAllAsync("gpt-6-astra: turn 3/8 in room lab\n", author: null, allowEmpty: false, trailers: ["Co-authored-by: Codex <noreply@openai.com>"]);
        Assert.Null(await git.RemoveWorktreeAsync(wt));

        Assert.Equal(["Co-authored-by: Claude <noreply@anthropic.com>", "Co-authored-by: Codex <noreply@openai.com>"],
            (await git.CoAuthorTrailersAsync("HEAD..chopitup/x7")).Order());
        var m = await git.MergeAsync("chopitup/x7", "Merge exchange #7 (lab)");
        Assert.Equal(MergeResult.Merged, m.Result);
        Assert.Equal("Room Owner <room-owner@example.test>|Room Owner <room-owner@example.test>|Merge exchange #7 (lab)",
            (await GitOut(_dir, "log", "-1", "--format=%an <%ae>|%cn <%ce>|%s")).Trim());
        var values = (await GitOut(_dir, "log", "-1", "--format=%(trailers:key=Co-authored-by,valueonly)"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order().ToArray();
        Assert.Equal(["Claude <noreply@anthropic.com>", "Codex <noreply@openai.com>"], values);

        // A branch whose commits carry no trailers: the merge carries none either.
        Assert.Null(await git.AddWorktreeAsync(wt, "chopitup/x8", newBranch: true));
        File.WriteAllText(Path.Combine(wt, "four.txt"), "4");
        await git.WithRoot(wt).CommitAllAsync("owner: edits before the next spawn in room lab\n", author: null, allowEmpty: false);
        Assert.Null(await git.RemoveWorktreeAsync(wt));
        Assert.Empty(await git.CoAuthorTrailersAsync("HEAD..chopitup/x8"));
        Assert.Equal(MergeResult.Merged, (await git.MergeAsync("chopitup/x8", "Merge exchange #8 (lab)")).Result);
        Assert.DoesNotContain("Co-authored-by", await GitOut(_dir, "log", "-1", "--format=%B"));
    }

    [Fact]
    public async Task Row46_A6_a_bookkeeping_commit_carries_the_repository_identity_and_no_trailer_and_an_explicit_author_still_wins()
    {
        var git = new GitTrail(_dir);
        Assert.True(await git.InitAsync());
        await ConfigureRoomOwner(_dir);
        await git.CommitAllAsync("Room trail start", author: null, allowEmpty: true);
        Assert.Equal("Room Owner <room-owner@example.test>|Room Owner <room-owner@example.test>",
            (await GitOut(_dir, "log", "-1", "--format=%an <%ae>|%cn <%ce>")).Trim());
        Assert.DoesNotContain("Co-authored-by", await GitOut(_dir, "log", "-1", "--format=%B"));

        // The explicit-author branch (the memory store, tests): the author is kept, the committer
        // still follows the repository - and it wins even over the hub's injected environment.
        await git.CommitAllAsync("seed", Opus, allowEmpty: true);
        Assert.Equal("Opus <opus@chopitup.local>|Room Owner <room-owner@example.test>",
            (await GitOut(_dir, "log", "-1", "--format=%an <%ae>|%cn <%ce>")).Trim());
        var bare = new GitTrail(_dir + "_owner", runner: new EnvRunner(NoIdentity));
        Assert.True(await bare.InitAsync());
        await bare.CommitAllAsync("seed", Opus, allowEmpty: true);
        Assert.Equal("Opus <opus@chopitup.local>|ChopItUp hub <hub@chopitup.local>",
            (await GitOut(_dir + "_owner", "log", "-1", "--format=%an <%ae>|%cn <%ce>")).Trim());
    }
```

(`_dir + "_owner"` is already deleted by `Dispose`.) Also in `Row46_A5…` the `Room trail start` seed call becomes `author: null` (R6), and in `Row46_A2…` the "half an identity" step keeps its meaning under the `git var` probe: a local `user.name` alone still yields the hub for both (measured: exit 128, "no email was given and auto-detection is disabled").

`_dir + "_wt"` is already deleted by `Dispose`. Existing assertions to update in the same file (ledger 12): the test containing `:84` (`%an <%ae>|%cn <%ce>|%s` = `Opus … |ChopItUp hub …`) must call `ConfigureRoomOwner(_dir)` right after its `InitAsync`/first write and expect `Opus <opus@chopitup.local>|Room Owner <room-owner@example.test>|opus: turn 1/4 in room lab` (explicit author kept, committer follows the repository — the same rule as A6); `:159` (`log[0].Author` = `Opus <opus@chopitup.local>`) is unchanged because that test passes `Opus` explicitly. Any other test in the file that reads `%cn`/`%ce` (ledger 12 says there is none) would get the same treatment. Every test that creates the trail with `new GitTrail(_dir)` and asserts only `%an` with an explicit author stays as it is.

`MemoryGitTests.cs:35` — widen the format to `--format=%an <%ae>|%cn <%ce> %s` and the assertion at `:39` to `StartsWith("ChopItUp hub <hub@chopitup.local>|ChopItUp hub <hub@chopitup.local> Approve memory proposal", l)`: AC7 on a machine that has a global identity (this one does) is only proven when the committer is asserted too.

RED command: `dotnet test tests/ChopItUp.Hub.Tests -c Debug --nologo -v minimal --filter "FullyQualifiedName~GitTrailTests|FullyQualifiedName~MemoryGitTests"` — expected: compile errors on `author: null`, `trailers:`, `ConfiguredIdentityAsync`, `CoAuthorTrailersAsync` (that is the RED; paste the first error line in the report).

### 1b. GREEN — `GitTrail.cs`

Replace lines 42-49 (the `Timeout`, `Hub`, `FieldSep`, `Env`, `Committer` block) with:

```csharp
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public static readonly GitIdentity Hub = new("ChopItUp hub", "hub@chopitup.local");
    public const string CoAuthorKey = "Co-authored-by";
    private const char FieldSep = (char)0x1F;
    // LC_ALL=C: the no-op detection reads git's English "nothing to commit" (M10 critique, P2-7).
    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" };
    // No signing and no CRLF rewriting on any commit the hub makes: an owner with commit.gpgsign
    // configured has no agent prompt to answer here.
    private static readonly string[] BaseOptions = ["-c", "commit.gpgsign=false", "-c", "core.autocrlf=false"];
    // The hub's own identity, injected through the environment (which beats every config and any
    // stray single GIT_* variable) only when git resolves no set identity of its own (row 46, R4):
    // an unconfigured machine or a CI runner must still commit, a configured owner identity is
    // git's to apply and the hub never overrides it.
    private static readonly IReadOnlyDictionary<string, string> HubIdentityEnv = new Dictionary<string, string>
    {
        ["GIT_AUTHOR_NAME"] = Hub.Name, ["GIT_AUTHOR_EMAIL"] = Hub.Email,
        ["GIT_COMMITTER_NAME"] = Hub.Name, ["GIT_COMMITTER_EMAIL"] = Hub.Email,
    };
```

After `protected virtual string LogName => "room";` add:

```csharp
    /// <summary>True for a trail whose every commit is the hub's own (the memory store): author and
    /// committer stay <see cref="Hub"/> whatever the machine's git config says. A room trail is false:
    /// its commits belong to the owner's repository and carry that repository's identity (row 46).</summary>
    protected virtual bool CommitsAsHub => false;

    /// <summary>The identity git itself would commit with at <see cref="Root"/> - `git var
    /// GIT_COMMITTER_IDENT` under <c>user.useConfigOnly</c>, so only an identity someone set counts
    /// (repository, global or system config, or the GIT_* environment) and git's own guesses from the
    /// account name, the host name or EMAIL never do - or null when git cannot resolve one (nothing
    /// set, or only a name or only an address). Null, quietly, when git is unavailable.</summary>
    public async Task<GitIdentity?> ConfiguredIdentityAsync(CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !Directory.Exists(Root)) return null;
        return await ConfiguredIdentityUnlocked(git, cancellation);
    }

    private async Task<GitIdentity?> ConfiguredIdentityUnlocked(ResolvedCli git, CancellationToken cancellation)
    {
        // Both roles: a GIT_COMMITTER_* pair alone lets the committer resolve while `git commit`
        // still dies on the author, and the reverse (row 46, critique pass 2).
        var author = await Run(git, ["-c", "user.useConfigOnly=true", "var", "GIT_AUTHOR_IDENT"], "", cancellation);
        if (author.ExitCode != 0) return null;
        var r = await Run(git, ["-c", "user.useConfigOnly=true", "var", "GIT_COMMITTER_IDENT"], "", cancellation);
        if (r.ExitCode != 0) return null;
        var ident = r.StandardOutput.Trim();                 // "Name <address> 1726700000 -0400"
        var close = ident.LastIndexOf('>');
        var open = close < 0 ? -1 : ident.LastIndexOf(" <", close, StringComparison.Ordinal);
        if (open <= 0) return null;
        return new GitIdentity(ident[..open], ident[(open + 2)..close]);
    }

    /// <summary>The environment a hub commit or merge runs with: the hub's identity in
    /// <see cref="HubIdentityEnv"/> when this trail commits as the hub or git resolves no set identity
    /// at <see cref="Root"/>; otherwise nothing beyond <see cref="Env"/>, and git's own resolution applies.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> IdentityEnvUnlocked(ResolvedCli git, CancellationToken cancellation) =>
        CommitsAsHub || await ConfiguredIdentityUnlocked(git, cancellation) is null ? HubIdentityEnv : null;

    /// <summary>The message with <paramref name="trailers"/> as its own final paragraph: git reads
    /// trailers only from the last paragraph, and only when a blank line separates it from the body.</summary>
    internal static string WithTrailers(string message, IReadOnlyList<string> trailers) =>
        message.TrimEnd('\r', '\n') + "\n\n" + string.Join("\n", trailers) + "\n";
```

`CommitAllAsync` — new signature and body from the `git add` on (the summary gains the two sentences shown):

```csharp
    /// <summary>`git add -A` then a commit. <paramref name="author"/> null means the repository's own
    /// configured identity for both author and committer (the hub's when it has none, see
    /// <see cref="ConfiguredIdentityAsync"/>); an explicit author is kept while the committer still
    /// follows the repository. <paramref name="trailers"/> (e.g. <c>Co-authored-by: …</c> lines) become
    /// the message's last paragraph only when something is staged: an empty commit credits nobody
    /// (row 46). The message travels on stdin (`-F -`): a room commit carries a shell log, and a
    /// Windows command line is capped at 32,767 characters. Initialises the repository if it is missing.
    /// With <paramref name="allowEmpty"/> false, "nothing to commit" is not a failure: the outcome is
    /// HEAD with <see cref="CommitOutcome.Created"/> false.</summary>
    public async Task<CommitOutcome> CommitAllAsync(string message, GitIdentity? author, bool allowEmpty, CancellationToken cancellation = default, IReadOnlyList<string>? trailers = null)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            // … unchanged through the `git add` failure return …
            var staged = await Run(git, ["diff", "--cached", "--quiet"], "", cancellation);   // 1 = something is staged, 0 = nothing
            if (staged.ExitCode is not (0 or 1)) return new(null, false, 0, Fail("git diff --cached", staged));
            var text = staged.ExitCode == 1 && trailers is { Count: > 0 } ? WithTrailers(message, trailers) : message;

            var identityEnv = await IdentityEnvUnlocked(git, cancellation);
            var args = new List<string>(BaseOptions) { "commit", "-q" };
            if (author is not null) args.Add("--author=" + author);     // an explicit author (the memory store) beats the environment: command line wins in git
            if (allowEmpty) args.Add("--allow-empty");
            args.Add("-F"); args.Add("-");
            var commit = await Run(git, args, text, cancellation, identityEnv);
            // … unchanged from `if (commit.ExitCode != 0)` on …
```

The `cancellation` parameter keeps its position so the six existing positional call sites (`allowEmpty: …, cancellation`) still compile; only the turn commit names `trailers:`. `Run` gains an optional last parameter and merges it over `Env`:

```csharp
    private Task<ProcessResult> Run(ResolvedCli git, IReadOnlyList<string> args, string stdin, CancellationToken cancellation, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var env = extraEnv is null ? Env : new Dictionary<string, string>(Env).Concat(extraEnv).ToDictionary(kv => kv.Key, kv => kv.Value);
        return _runner.RunAsync(new ProcessSpec(git.FileName, [.. git.LeadingArguments, .. args], env, Root, stdin, LogName + "-git"), Timeout, cancellation);
    }
```

(`Env` and `HubIdentityEnv` share no key, so `Concat` cannot throw on a duplicate.)

`MergeAsync` — replace the single `var args = new List<string>(Committer) { "merge", "--no-ff", "-m", message, branch };` line and the `Run` call after it with:

```csharp
            // Row 46: the merge is authored under the identity rule and its last paragraph carries
            // every distinct Co-authored-by line on the commits being merged, so the room's
            // first-parent history says who contributed without opening the branch.
            var trailers = await CoAuthorTrailersUnlocked(git, "HEAD.." + branch, logFailure: false, cancellation);
            var args = new List<string>(BaseOptions) { "merge", "--no-ff", "-m", message };
            if (trailers.Count > 0) { args.Add("-m"); args.Add(string.Join("\n", trailers)); }
            args.Add(branch);
            var r = await Run(git, args, "", cancellation, await IdentityEnvUnlocked(git, cancellation));
```

and update its summary's "committed as the hub (<see cref="Committer"/>)" to "committed under the identity rule (<see cref="ConfiguredIdentityAsync"/>)". Add, next to `ChangedFilesAsync`:

```csharp
    /// <summary>The distinct <c>Co-authored-by</c> trailer lines on the commits in <paramref name="range"/>
    /// (e.g. <c>"HEAD..chopitup/x7"</c>), newest first, re-emitted as full <c>Co-authored-by: …</c>
    /// lines - what an exchange merge carries (row 46). Empty on failure, with <see cref="Reason"/> set.</summary>
    public async Task<IReadOnlyList<string>> CoAuthorTrailersAsync(string range, CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !HasGitEntry()) return [];
        return await CoAuthorTrailersUnlocked(git, range, logFailure: true, cancellation);
    }

    /// <param name="logFailure">False from a merge: a missing branch fails the merge itself a moment
    /// later with its own reason, and one failure should be logged once (critique pass 2, F5).</param>
    private async Task<IReadOnlyList<string>> CoAuthorTrailersUnlocked(ResolvedCli git, string range, bool logFailure, CancellationToken cancellation)
    {
        var r = await Run(git, ["log", "--format=%(trailers:key=" + CoAuthorKey + ",valueonly)", range], "", cancellation);
        if (r.ExitCode != 0) { if (logFailure) Fail("git log", r); return []; }
        var seen = new List<string>();
        foreach (var value in r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!seen.Contains(value, StringComparer.Ordinal)) seen.Add(value);
        Reason = null;
        return seen.Select(v => CoAuthorKey + ": " + v).ToList();
    }
```

Delete the `Committer` field; nothing else references it after these edits (`grep -n Committer GitTrail.cs` must return only the doc-comment word "committer" in prose). Update the class summary's "the committer is always the hub" sentence to: "the author and committer are the identity git itself resolves at the root, the hub's only when git resolves none; a caller may still name an explicit author (the memory store does) — row 46."

`MemoryGit.cs`: after `protected override string LogName => "memory";` add `protected override bool CommitsAsHub => true;` and change the class summary's "with the hub as author" to "with the hub as author and committer whatever the machine's git config says (row 46)".

GREEN command: the same filter; expected `Passed!` for both classes. Then the whole Hub project once: `dotnet test tests/ChopItUp.Hub.Tests -c Debug --nologo -v minimal` — expected: **exactly these failures**, whose expectations name the old identities and whose room repositories have no local identity (so the new author/committer is this machine's global identity, not the literal): `SpawnerServiceTests.Rooms` at `:141-143`, `:233`, `:550-552`, `:576-577` (four tests) and `RoomsApiTests` `:226` (one test) — Task 2 rewrites all five. Any other failure is a STOP with the test name and the assertion text.

Commit: `Row 46 task 1: GitTrail commits under the repository identity, trailers on staged changes, merge carries the union`.

## Task 2 — room call sites, the note, the tests that name identities (sonnet; blocked by 1)

**Files:** `src/ChopItUp.Hub/Spawning/RoomCommits.cs`, `src/ChopItUp.Hub/Spawning/SpawnerService.cs`, `src/ChopItUp.Hub/Rooms/ExchangeWorktrees.cs`, `src/ChopItUp.Hub/Memory/HubNotes.cs`, `tests/ChopItUp.Hub.Tests/Spawning/RoomCommitsTests.cs`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.Rooms.cs`, `tests/ChopItUp.Hub.Tests/Rooms/ExchangeWorktreesTests.cs`, `tests/ChopItUp.Hub.Tests/RoomsApiTests.cs`.

### 2a. RED

`RoomCommitsTests.cs`: delete `M9_A9_identity_is_display_name_and_id_at_the_hub_domain` (lines 13-18); add:

```csharp
    [Fact]
    public void Row46_A3_the_co_author_trailer_follows_the_host_and_names_nobody_for_an_unknown_one()
    {
        Assert.Equal("Co-authored-by: Codex <noreply@openai.com>", RoomCommits.CoAuthorTrailer("codex"));
        Assert.Equal("Co-authored-by: Claude <noreply@anthropic.com>", RoomCommits.CoAuthorTrailer("claude"));
        Assert.Null(RoomCommits.CoAuthorTrailer("hub"));
        Assert.Null(RoomCommits.CoAuthorTrailer(""));
    }
```

and change `as sonnet:` to `for sonnet:` on lines 34, 35 and 37 (three `Assert.Equal` literals).

`SpawnerServiceTests.Rooms.cs`: `MakeRoom` configures the room like an owner's repository — after `Assert.True(await new GitTrail(dir).InitAsync());` add `await GitConfig(dir, "user.name", "Room Owner"); await GitConfig(dir, "user.email", "room-owner@example.test");` with the helper (next to `GitLog`):

```csharp
    private static async Task GitConfig(string dir, string key, string value)
    {
        var r = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, ["config", key, value], new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
    }
```

Line 132: `"for sonnet: 1 file(s) changed, 2 shell command(s)."`. Lines 140-144 become:

```csharp
        Assert.Equal([
            $"Room Owner <room-owner@example.test>|Room Owner|Merge exchange #{root} (lab)",
            "Room Owner <room-owner@example.test>|Room Owner|sonnet: turn 1/4 in room lab",
            "Room Owner <room-owner@example.test>|Room Owner|owner: edits before the next spawn in room lab",
        ], roomLog);
```

(keep whatever turn/budget literal the line has at HEAD — the subject is not this row's). After the existing `body` assertion at `:146` add:

```csharp
        Assert.EndsWith("\n\nCo-authored-by: Claude <noreply@anthropic.com>\n", body.Replace("\r\n", "\n"));   // sonnet is a claude host and changed a file
        Assert.Equal("Claude <noreply@anthropic.com>", (await GitLog(dir, "%(trailers:key=Co-authored-by,valueonly)", 1)).Trim());   // the merge carries it
        Assert.DoesNotContain("Co-authored-by", await GitLog(dir, "%B", 1, skip: 2));                             // the owner's own commit credits no model
```

Line 544: `"for gpt-6-astra: 0 file(s) changed, 0 shell command(s)."`. Lines 549-553 become three `"Room Owner <room-owner@example.test>"` lines, and after them:

```csharp
        Assert.DoesNotContain("Co-authored-by", await GitLog(dir, "%B", 1, skip: 1));   // an empty turn credits nobody
        Assert.DoesNotContain("Co-authored-by", await GitLog(dir, "%B", 1));            // so the merge carries nothing either
```

Line 233 (`Assert.Equal("Opus", (await GitLog(dir, "%an", 1, ExchangeWorktrees.Branch(root))).Trim())`) becomes `"Room Owner"`; lines 576-577 become `$"Room Owner|Merge exchange #{root} (lab)"` and `"Room Owner|sonnet: turn 1/4 in room lab"` (keep the file's own turn/budget literal), and if `log[2]`/`log[3]` are asserted in that test, their author field becomes `Room Owner` too (the `Room trail start` seed is a bookkeeping commit, R6).

Add the R11 test next to the M9_A9 launch tests (a spawn whose runner throws before any process starts; the tree changed meanwhile; no trailer):

```csharp
    [Fact]
    public async Task Row46_R11_a_spawn_that_never_launched_credits_no_host_even_when_the_tree_changed()
    {
        var dir = await MakeRoom("lab");
        _runner.Handler = (spec, _, _) =>
        {
            File.WriteAllText(Path.Combine(spec.WorkingDirectory, "during.txt"), "owner edit while nothing ran");
            throw new InvalidOperationException("launch failed on purpose");
        };
        await PostAsOwnerIn("lab", "@sonnet hello");
        await _runner.NextSpecAsync(Wait);
        var note = await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith(HubNotes.TrailPrefix));
        Assert.Contains("for sonnet: 1 file(s) changed", note.Body);
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} merged into"));
        Assert.DoesNotContain("Co-authored-by", await GitLog(dir, "%B", 1, skip: 1));   // the turn commit
        Assert.DoesNotContain("Co-authored-by", await GitLog(dir, "%B", 1));            // the merge
    }
```

If the exchange does not merge after a launch failure (the close may keep the branch as "a spawn of it was stopped or timed out" — read `ExchangeWorktrees.CloseAsync:142-143` and the spawner's failure path), drop the two merge lines and read the turn commit on `ExchangeWorktrees.Branch(root)` instead (`GitLog(dir, "%B", 1, ExchangeWorktrees.Branch(root))`); report which shape held.

`ExchangeWorktreesTests.cs`: the `Close(` helper's `new(dir, root, roomId, status, leased, interrupted, runOwnsRoom, Owner, $"owner: …")` drops `Owner,`. `RoomWithCommit` (`:41-48`) configures the room like an owner's repository before seeding (`await RawGit(dir, "config", "user.name", "Room Owner")`, `… "user.email", "room-owner@example.test"`, both exit 0) and seeds with `author: null`; then delete the `Owner` static field at `:10` if nothing else in the file uses it (grep first; if something does, STOP and report the line). In the one test that mentions `Uncommitted` (ledger 24), add an assertion that the sweep commit's `%an <%ae>` is `Room Owner <room-owner@example.test>` and its `%B` has no `Co-authored-by` (AC6).

`SpawnPrompt.cs:369`: the clause `the hub commits your work under your name when you finish and records every shell command you run in the room's commit trail.` becomes `the hub commits your work when you finish, crediting you as co-author, and records every shell command you run in the room's commit trail. If you commit anyway, end the message with a Co-authored-by: trailer naming you.` (the folder-level AGENTS.md line 8 asks that sub-agent prompts carry the trailer requirement; critique pass 2, F4) — and the identical clause on line 7 of `tests/ChopItUp.Hub.Tests/Spawning/golden-prompt-ddfa572.txt` is edited to the same text by hand, preserving the file's line endings (it is `-text` in `.gitattributes`; `git diff --stat` must show one line changed, and `SpawnPromptTests.R14_…byte_for_byte…` must pass — if it does not, STOP with the first differing byte offset rather than regenerating the capture).

`SpawnerService.cs` around `:1108`: the runner call gains a launched flag —

```csharp
                    var launched = false;
                    try { result = await _runner.RunAsync(spec with { RoomId = roomId, ParticipantId = participant.Id }, timeout, handle.Cancel.Token); launched = true; }
                    catch (Exception e) { result = new ProcessResult(null, false, false, "", "launch failed: " + e.Message, TimeSpan.Zero); }
```

and the turn commit passes `RoomCommits.CoAuthorTrailer(host)` only when `launched` (code in 2b).

`RoomsApiTests.cs:218-226`: configure the room directory first (`RoomsApiTests` has no git helper — add the same `GitConfig` helper as above at the bottom of the class), then:

```csharp
        await GitConfig(directory, "user.name", "Room Owner");
        await GitConfig(directory, "user.email", "room-owner@example.test");
        var trail = _host.Services.GetRequiredService<RoomTrails>().For(directory);
        File.WriteAllText(Path.Combine(directory, "a.txt"), "a");
        await trail.CommitAllAsync("owner: edits before the next spawn in room lab", author: null, allowEmpty: false);
        File.WriteAllText(Path.Combine(directory, "b.txt"), "b");
        await trail.CommitAllAsync("opus: turn 1/4 in room lab\n\nShell commands run: none.\n", author: null, allowEmpty: true, trailers: [RoomCommits.ClaudeTrailer]);
        // … the existing assertions, with :226 now:
        Assert.Equal("Room Owner <room-owner@example.test>", commits[0].GetProperty("author").GetString());
```

(`using ChopItUp.Hub.Spawning;` if the file lacks it.)

RED command: `dotnet test tests/ChopItUp.Hub.Tests -c Debug --nologo -v minimal --filter "FullyQualifiedName~RoomCommitsTests|FullyQualifiedName~SpawnerServiceTests|FullyQualifiedName~ExchangeWorktreesTests|FullyQualifiedName~RoomsApiTests"` — expected: compile errors on `CoAuthorTrailer`/`ClaudeTrailer` and on the `Close(` arity.

### 2b. GREEN

`RoomCommits.cs` — the whole file becomes:

```csharp
using System.Text;
using ChopItUp.Core.Model;

namespace ChopItUp.Hub.Spawning;

/// <summary>What a room commit says and whom it credits (M9 decisions 6, 7; identity re-ruled by
/// row 46). The author and committer are the repository's own configured identity - git resolves it
/// and the hub never overrides it (<see cref="Git.GitTrail.ConfiguredIdentityAsync"/>) - so the room's
/// log reads like every other commit the owner makes there. The participant is named in the subject,
/// and a turn that changed something is credited with its host's <c>Co-authored-by</c> trailer, the
/// convention the owner's folder-level AGENTS.md sets for Codex. The subject is one line the trail
/// dialog shows; the body carries the shell log.</summary>
public static class RoomCommits
{
    public const string ShellHeader = "Shell commands run";
    public const string CodexTrailer = "Co-authored-by: Codex <noreply@openai.com>";
    public const string ClaudeTrailer = "Co-authored-by: Claude <noreply@anthropic.com>";

    /// <summary>The trailer that credits a spawn's host, or null for a host the spawner cannot start:
    /// nothing is credited rather than something invented.</summary>
    public static string? CoAuthorTrailer(string host) => host switch
    {
        "codex" => CodexTrailer,
        "claude" => ClaudeTrailer,
        _ => null,
    };

    public static string OwnerMessage(Participant owner, string roomId) =>
        $"{owner.Id}: edits before the next spawn in room {roomId}\n";

    // AgentSubject and AgentMessage exactly as at HEAD (lines 21-36).
}
```

(`using ChopItUp.Hub.Git;` goes; `Domain` and `IdentityOf` go.)

`SpawnerService.cs`:
- `:345-346`: `new ExchangeWorktrees.CloseRequest(dir, root, roomId, status, leased, interrupted, runOwns, RoomCommits.OwnerMessage(_owner, roomId))`.
- `:1090`: `owner = await roomGit.CommitAllAsync(RoomCommits.OwnerMessage(_owner, roomId), author: null, allowEmpty: false, CancellationToken.None);`
- `:1116` becomes:

```csharp
                        // Row 46: the repository's own identity is the author; the host is credited by
                        // trailer only when its process actually launched (R11) and the turn changed
                        // something (CommitAllAsync decides that part).
                        var trailer = launched ? RoomCommits.CoAuthorTrailer(host) : null;
                        var agent = await git.CommitAllAsync(RoomCommits.AgentMessage(participant, roomId, turn, budget, commands, headMoved), author: null, allowEmpty: true, CancellationToken.None,
                            trailers: trailer is null ? null : [trailer]);
```

`ExchangeWorktrees.cs`: `CloseRequest` loses `GitIdentity Owner` (record at `:95`, its summary sentence "to write the owner's commit if the room tree is dirty" gains "under the repository's identity"); `:102` deconstructs without `owner`; `:150` is `main.CommitAllAsync(ownerMessage, author: null, allowEmpty: false, cancellation)`. Lines 71, 120 and 198 (`Room trail start`, `Uncommitted at the close of exchange #N`, `Uncommitted when the hub restarted`) replace `GitTrail.Hub` with `author: null` (R6) — after this task `git grep -n 'GitTrail.Hub' -- src` matches only `MemoryGit.cs` and the definition. The `RecoverAsync` summary's "commits any dirty edits as the hub" becomes "commits any dirty edits under the repository's identity".

`HubNotes.cs:92`: `{TrailPrefix}{agent.Hash} for {participantId}:`.

GREEN command: the whole Hub suite `dotnet test tests/ChopItUp.Hub.Tests -c Debug --nologo -v minimal` — expected: all green. Then `dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal` — expected 0 warnings (an unused `using` after the `RoomCommits` rewrite is a warning-as-error).

Commit: `Row 46 task 2: room commits credit the host by trailer under the repository identity; note says for`.

## Task 3 — README, verification.md, the scratch check (sonnet; blocked by 2)

**Files:** `README.md` (lines 190-194), `docs/verification.md` (after line 22), new `tools/Invoke-Row46AttributionCheck.ps1`.

README paragraph at `:190-194` becomes (one paragraph, wrapped like its neighbours):

> The trail: before a spawn, if the tree is dirty, the hub commits the owner's edits; when the spawn ends the hub always commits the tree for that participant, with the shell commands it ran listed in the commit body. Every room commit carries the repository's own configured git identity as author and committer (the hub's `ChopItUp hub <hub@chopitup.local>` only when none is configured), the participant is named in the subject, and a turn that changed something is credited with `Co-authored-by: Codex <noreply@openai.com>` or `Co-authored-by: Claude <noreply@anthropic.com>` as its last paragraph; an exchange merge carries the trailers of the commits it merges, and the hub's own bookkeeping commits (`Room trail start`, leftovers swept at a close or a restart) carry the same repository identity and no trailer. During a run a spawn works in the room directory itself, so an edit you make while its turn is running is swept into that turn's commit and shares its trailer. A hub note `Committed <hash> for <id>: …` (or `Not committed for <id>: …`) lands in the room, and the Trail button in the header lists the last 20 commits. Nothing is ever pushed.

`docs/verification.md`, new line after `:22`: ``Git attribution in room commits (row 46, stub Claude and Codex, no model calls): `pwsh tools\Invoke-Row46AttributionCheck.ps1`.``

`tools/Invoke-Row46AttributionCheck.ps1` — copy the scaffold of `tools/Invoke-Row43MentionCheck.ps1` (header comment shape, `param` block with `$HubExe`, `$ScratchRoot`, `$Port = 8807`, `$TimeoutSeconds`; `ChopTokenHelpers.ps1`; `Add-Check`, `Invoke-Api`, `Wait-HubNotePrefix`; stripped PATH with `stub\` first, restored in `finally`; owner token seeded into the scratch `tokens.json` before the hub's first start; hub by PID, `taskkill /T /F /PID` in `finally`; summary line `Row 46 attribution check: N PASS / M FAIL` and exit 1 on any FAIL). No skill import is needed. Differences from row 43:

1. **Two stubs that exit at once.** `stub\claude.cmd` and `stub\codex.cmd`, both:

```bat
@echo off
if exist "%~dp0quiet" exit /b 0
echo made by the stub> "%CD%\turn-%RANDOM%.txt"
exit /b 0
```

   (`%CD%` is the worktree the hub launched them in; `%~dp0quiet` is a marker file the script creates before the empty-turn leg and deletes after.) The hub's parsers see no JSON on stdout, post `replied without posting to the room`, and still commit the tree — that commit is what this check reads.
2. **A scratch directory room.** `New-Item -ItemType Directory "$ScratchRoot\rooms"` (the parent must exist, ledger 20), then `POST /api/rooms` with `@{ name = 'Lab'; directory = "$ScratchRoot\rooms\lab" }` as the owner. The hub `git init`s the directory; immediately after the 201, run `git -C "$ScratchRoot\rooms\lab" config user.name 'Scratch Owner'` and `… user.email 'scratch-owner@example.test'` (check exit 0).
3. **Legs**, each waiting for the hub note `Committed ` and then `Exchange #<root> merged into` in room `lab` (helper `Wait-HubNotePrefix` with the room id), then reading git in `$ScratchRoot\rooms\lab` with `git log --date-order --format=<fmt> -n 1 [--skip=N]`:
   - L1 write `owner.txt` into the room directory, post `@sonnet make a file`. Checks: (a) note starts `Committed ` and contains ` for sonnet: 1 file(s) changed`; (b) the three newest commits' `%an <%ae>|%cn <%ce>` are all `Scratch Owner <scratch-owner@example.test>|Scratch Owner <scratch-owner@example.test>`; (c) `--skip=1` `%(trailers:key=Co-authored-by,valueonly)` = `Claude <noreply@anthropic.com>`; (d) `--skip=2` `%B` has no `Co-authored-by` (the owner's edit); (e) `-n 1` (the merge) `valueonly` = `Claude <noreply@anthropic.com>`.
   - L2 post `@gpt-6-astra make a file`. Checks: (f) `--skip=1` valueonly = `Codex <noreply@openai.com>`; (g) the merge's valueonly = `Codex <noreply@openai.com>`; (h) both `%an <%ae>|%cn <%ce>` = Scratch Owner.
   - L3 create `stub\quiet`, post `@sonnet do nothing`, delete the marker after the merge note. Checks: (i) note contains ` for sonnet: 0 file(s) changed`; (j) `--skip=1` `%B` has no `Co-authored-by`; (k) the merge's `%B` has no `Co-authored-by`.
   Expected: 11 PASS / 0 FAIL. The orchestrator then runs the mutation (AC9): `CoAuthorTrailer` returning null, rebuild, re-run — expected c, e, f, g FAIL (4/11 fail), then restore.
4. **Never touches** `C:\Self Apps` or any real data directory; every path is under `$ScratchRoot`; the header comment says so like row 43's does.

**`tools/Invoke-M9RoomCheck.ps1`** (ledger 22; a live check that spends real calls, so it is edited here and run by the orchestrator in Phase B only if `claude auth status` reports logged in): right after `$roomDir` exists as a repository (the hub `git init`s it on room creation; find the room-creation call after `:85` and the first `Test-Spawn` at `:142`) run `& git -C $roomDir config user.name 'Live Check'` and `& git -C $roomDir config user.email 'live-check@example.test'` (each checked for exit 0 with an `Add-Check 'git.identity-configured'`). `Test-Spawn` gains `[string]$ExpectTrailer` and: `:133` expects `*for $Participant*`; `:135` becomes `Add-Check -Name "$Prefix.git.author-is-repo-identity" -Passed ("$top" -like "Live Check|Live Check|$Participant`: turn *")`; a new check `"$Prefix.git.co-author"` reads `& git -C $roomDir log -1 --format='%(trailers:key=Co-authored-by,valueonly)'` and expects `-eq $ExpectTrailer`; `:142` passes `-ExpectTrailer 'Claude <noreply@anthropic.com>'`, `:147` passes `-ExpectTrailer 'Codex <noreply@openai.com>'`; `:144` expects `$authors[1] -eq 'Live Check'`; `:154` expects `$commits[0].author -eq 'Live Check <live-check@example.test>'`. Nothing else in the script changes.

Run: `pwsh -NoProfile -File tools\Invoke-Row46AttributionCheck.ps1` after `dotnet build src/ChopItUp.Hub -c Debug -v minimal` — expected `Row 46 attribution check: 11 PASS / 0 FAIL`. Paste the summary line and the three `git log` triples in the report. If the hub refuses the room directory or the stubs are not launched (no `Committed ` note within `$TimeoutSeconds`), STOP with the hub's stderr tail — do not loosen the check.

Commit: `Row 46 task 3: README and verification.md describe the identity rule; Invoke-Row46AttributionCheck.ps1`.

## Ticket graph

`01-repo-identity-and-trailers` → `02-room-call-sites` → `03-docs-and-scratch-check`. A linear chain: no parallel batch. Builders are dispatched synchronously.

## Could not verify in this environment

- Ledger 19: `GIT_CONFIG_GLOBAL`/`GIT_CONFIG_NOSYSTEM` and `%(trailers:key=…,valueonly)` on the CI runner's git (needs ≥ 2.32; `windows-latest` ships a current Git for Windows, unverified until the PR's checks run).
- A real Claude or Codex spawn producing the trailer on a real repository (the scratch check uses stubs; `tools\Invoke-Row35LiveCheck.ps1` spends ≤ 2 calls and would show it — not run by this plan).
- Codex committing on its own inside a worktree (`HEAD moved`) uses Codex's identity and its own `AGENTS.md` trailer, outside the hub's hands; the hub's turn commit after it credits only what it sweeps up.
- The live hub's room clone `C:\Agent Projects\ChopItUp-room` is only touched by the deployed hub; the deploy step ships the new behaviour, the next real turn there is the first live proof.
- A machine whose process environment sets `GIT_COMMITTER_NAME`/`GIT_COMMITTER_EMAIL` (or the author pair) makes `NoIdentity` unable to reproduce "nothing set" (the fixture blocks config files only); this machine and CI do not, unverified for other owners' machines.
- The edited `tools/Invoke-M9RoomCheck.ps1` is run in Phase B only when `claude auth status` reports logged in (2 real calls with `-IncludeCodex`); otherwise its edit is proof-read against the new `git log` shapes and stays listed here.

## Critique dispositions

| Pass | Finding | Disposition |
|---|---|---|
| 1 (opus, 6.8 FIX-THEN-SHIP) | M1 ledger 11 population false (`:233`, `:575-577` unlisted); Task 1 STOP list wrong | Fixed: ledger 11 re-scoped to the `%an\|%cn\|%ae\|%ce` population (6 lines), Task 1 expected-failure list names all five tests, Task 2 rewrites `:233` and `:576-577` |
| 1 | M2 `tools/Invoke-M9RoomCheck.ps1` asserts the old identities and was in no task | Fixed: ledger 22, Task 3 edits it (`Live Check` identity, `for`, trailer check), Phase B runs it when logged in |
| 1 | M3 AC3 credits a host for a launch that never ran, and for owner edits swept on the run path | Fixed for the launch case (R11, `launched` flag, `Row46_R11…` test, AC3/AC4 reworded); the run-path sharing is accepted and declared in R11 and README (the alternative strips Codex credit from the roadmap-in-room runs) |
| 1 | M4 `git config --get` is not git's resolution (env beats `-c`; gecos/EMAIL guesses) | Fixed: R4 re-ruled to `git -c user.useConfigOnly=true var GIT_COMMITTER_IDENT`, injection through the four `GIT_*` env variables (measured 2026-09-19), AC2 reworded |
| 1 | M5 R6 kept an invented author on real changes against `AGENTS.md` line 1 | Fixed: R6 re-ruled to `author: null`, AC6 reworded, A6 test rewritten, `RoomWithCommit`/sweep assertion added |
| 1 | m6 generic `Claude` trailer vs this repo's model-specific lines | Declined with rationale in R2 (GitHub keys on the address; `Claude Claude`; documented convention) |
| 1 | m7 `SpawnPrompt.cs:369` "under your name" + golden capture | Fixed: ledger 23, Task 2 rewords both, AC8 covers it |
| 1 | m8 Hub baseline pending | Fixed: 910 measured, ledger 1 |
| 1 | m9 AC6 silent on committer; AC8 README clause not falsifiable | Fixed: both reworded |
| 2 (fable, 7.2 FIX-THEN-SHIP) | F1 Task 2 text, README paragraph and ticket 03 still kept the hub as bookkeeping author after R6 was re-ruled | Fixed: Task 2 passes `author: null` at `:71`, `:120`, `:198` with a grep gate; README and ticket 03 reworded |
| 2 | F2 probing only `GIT_COMMITTER_IDENT` lets a committer-only env pair pass while `git commit` dies on the author (and the reverse overrides the owner's author) | Fixed: both `GIT_AUTHOR_IDENT` and `GIT_COMMITTER_IDENT` probed, inject on either failing; R4 reworded |
| 2 | F3 ledger 6 recheck counted `AbortStaleExchangeMergeAsync(` | Fixed: word-boundary regex |
| 2 | F4 `AGENTS.md` line 8 (prompts for sub-agents that commit carry the trailer requirement) unmet for a self-committing Codex | Fixed: one sentence appended to the `DirectoryRules` clause and the golden capture |
| 2 | F5 a missing branch logs `git log` then `git merge` failures | Fixed: `logFailure: false` from the merge |
| 2 | framing: `git commit --trailer` could replace `WithTrailers`; commit under `useConfigOnly` and retry on 128 could replace the probe | Declined: the staged-changes decision still needs the `diff --cached` call, and the probe is two read-only calls with a fully specified test; noted as a later simplification |
