# Row 36: reply-to in the UI picks which exchange a post joins

**Goal:** An owner post that replies to a message joins that message's exchange, keeping its budget, its turn count and its skill, instead of superseding it or opening a new one.

**Architecture:** Messages gain a nullable `reply_to_id` (schema v11) that the web API accepts, stores, lists and broadcasts. Each in-memory `Exchange` records the ids of the messages that belong to it, and the spawner keeps the last 50 owner-opened exchanges per room so a reply can find its exchange after that exchange was pruned from the room's display list. `ExchangePolicy.OnRoomMessage` (pure) gets the exchange the service resolved and applies the join: accept the mentions against that exchange, reopening it when it is closed and idle; the service puts a reopened exchange back into the room's list and restarts its worktree bookkeeping. The web UI adds a Reply action per message, a reply chip in the composer, and a quote line on replies.

**Author model:** Opus 5. Session-model mismatch: HIGH planning routes to Fable. Critique pass 2 is mandatory.

**Blast radius:** HIGH. A schema migration on the one table every surface reads (`messages`), a new field on the `Message` record that the MCP tools serialize to every host, and a change to the spawner's routing rules that decides where real model turns are spent.

*Written for builder-subagent execution; if something doesn't match, STOP and report rather than guess.*

## Rulings this plan implements
Owner, 2026-09-13 (row 32 decisions record): "Reply-to (its own row) is the true join that keeps budget." Follow-ups join by mention and by reply-to in the UI.

Orchestrator defaults (Class B, reversible, logged here; the owner can overturn any in a new row):
- **B1 Closed exchanges reopen.** A reply with a spawnable mention to a concluded, stopped or superseded exchange that has nothing in flight reopens it. Its turns used, budget and skill carry on; turn numbering continues. Without a reopen the feature does nothing in practice: a one-line reply concludes an exchange in about 10 s.
- **B2 Still finishing refuses.** A reply with a mention to a closed exchange whose spawn is still running posts one note and spawns nothing. It spends no turn on a half-finished exchange.
- **B3 A join supersedes nothing.** The owner picked the exchange. Row 32's overlap rule does not apply to a reply, even when it mentions a model of another open exchange.
- **B4 No mention, no state change.** A reply with no spawnable mention is recorded as a member of the exchange and changes nothing else (no reopen, no note).
- **B5 Unresolved replies fall back.** A reply to a message in no exchange the hub still holds (a hub note, a run's exchange, a pre-restart exchange, anything older than the last 50 owner exchanges) is handled exactly as today's prompt, plus one note when it has a spawnable mention.
- **B6 Skills do not join.** A reply that invokes a skill (found) is a new prompt, plus one note. A reply whose skill is refused (unknown, tampered, unreadable) is handled exactly as the same post without reply-to: row 32's overlap supersede and the refusal note apply, and no reply note is added (critique pass 1, finding 5).
- **B7 Runs ignore reply-to.** Active and parked runs keep today's steer, resume and stop rules; reply-to is stored and shown, never routed.
- **B8 Worktree rooms re-lease.** A reopened exchange in a directory room leases `chopitup/x<root>` again: after a clean merge that forks a fresh branch from HEAD (which holds the merge); after a kept branch (conflict, stop, interrupt, run-owned) it continues that branch with its commits, so "fix the conflict" works. Only a reopened exchange may continue an existing branch; a first lease still refuses one. It never launches while a worktree close that could be its own is still running. (Critique pass 1, finding 1: the refusal the first draft accepted burned a turn and dead-ended every later reply.)
- **B9 Membership.** An exchange's members are its root, every owner reply that joined it, every post by its own spawns (open or not) and every app-backed model post routed to it while open. Hub notes are never members, and the UI offers no Reply on them.
- **B10 UI and web API only.** MCP `post_message` gains no reply parameter. `read_messages` shows `reply_to_id` on replies (omitted when null by the existing `WhenWritingNull` option).

## Acceptance
1. WHEN the hub starts on a v10 database THE SYSTEM SHALL back it up and migrate it to v11, adding a nullable `messages.reply_to_id`, with every existing message readable and unchanged and its reply-to null; WHEN the column already exists at stamp 10 (a torn v11) THE SYSTEM SHALL finish the step without error.
2. WHEN `POST /api/rooms/{id}/messages` carries `replyToId` naming a message of that room THE SYSTEM SHALL store it and return it as `replyToId` in the 201 body, in `GET /api/rooms/{id}/messages` and in the `MessagePosted` broadcast; WHEN `replyToId` names no message of that room THE SYSTEM SHALL answer 400 and store nothing. `read_messages` SHALL include `reply_to_id` on a reply and omit it on other messages.
3. WHEN an owner reply names a member of an open exchange the hub holds THE SYSTEM SHALL accept the reply's spawnable mentions into that exchange against its remaining budget, open no new exchange and supersede no exchange.
4. WHEN an owner reply with a spawnable mention names a member of a closed exchange with nothing in flight THE SYSTEM SHALL reopen that exchange with its turns, budget and skill kept, and the next spawn's prompt SHALL state the continued turn number; WHEN that closed exchange still has a spawn in flight THE SYSTEM SHALL post one note naming it and spawn nothing; WHEN the reply has no spawnable mention THE SYSTEM SHALL leave every exchange's status, pending and budget unchanged.
5. WHEN an owner reply names a message in no exchange the hub holds, or invokes a found skill, THE SYSTEM SHALL handle it as a post without reply-to and post one note saying it did not join; WHEN a run is active or parked in the room THE SYSTEM SHALL route the post as it does today and every existing test SHALL pass unchanged except the schema-version pins this plan names.
6. WHEN a reopened exchange belongs to a room with a directory and no run THE SYSTEM SHALL launch its spawn in that exchange's worktree on `chopitup/x<root>` (a fresh branch when the close merged it, the kept branch with its commits when the close kept it), and not while a worktree close that started before the reopen is still running in the room; a first lease of an exchange SHALL still refuse an existing branch.
7. WHEN the owner chooses Reply on a non-hub message THE UI SHALL show a dismissible reply chip naming the author above the composer, send `replyToId` with the next post, and clear the chip after a successful send and on a room change; a reply SHALL render a quote line naming the original author that scrolls to the original when it is loaded. The spawn prompt transcript SHALL mark a reply with `(reply to #R)`.

## Lessons consulted
- **M1** (stamp inside the DDL transaction): `ApplyV11` probes the column inside the transaction and stamps last, like `ApplyV2`. Task 1 adds a torn-v11 test.
- **M2** (prove the on-disk premise): the v10 fixture test asserts the stamp is 10 and the column is absent before migrating.
- **R25 schema sweep** (commit `63e7df3`): every script and test pinning the schema must move with it. Task 1 lists all 14 sites.
- **M24** (a timing-dependent guard binds nothing): every join rule gets a pure `ExchangePolicyTests` case; the worktree close guard is tested deterministically with the existing `DelayingMergeRunner` (merge held, not raced); Verification reverts each mechanism once.
- **M28** (name the call sites of a changed signature): `Message` gains an optional last parameter, so its two construction sites in `MessageStore` and the test helper `Msg(...)` compile unchanged; `OnRoomMessage` gains an optional last parameter, so its call sites compile unchanged. `MessageStore.Post` gains an optional parameter on the four-argument overload only.
- **M23 / Row 34** (UI gate in a hidden pane; owner bearer in the page is denied): the UI gate asserts hit-testing with `elementFromPoint`, dispatches `.click()`, and drives the authenticated post from a script with the scratch hub's token, confirming the page re-renders over SignalR.
- **Row 35** (worktree git facts): no new git command is added. The re-lease goes through `ExchangeWorktrees.EnsureAsync`, whose one change is letting a reopened exchange take the `newBranch: false` path it already uses after a prune.

## Could not verify in this environment
- Live behaviour with real CLIs (a real Claude or Codex spawn continuing a reopened exchange, in a worktree or not). Covered by the fake runner and a stub-CLI dry run; not run live (spend). Named in the ping.
- SQLite accepts `ALTER TABLE ... ADD COLUMN reply_to_id INTEGER REFERENCES messages(id)` with foreign keys on because the column defaults to NULL. The pass 1 critic probed it on SQLite 3.49.1 through Python; the bundled `e_sqlite3` provider is proven only by Task 1's migration tests running it.
- A real authenticated click in the web UI (the Row 34 wall): the gate proves render, hit-testing and the request shape, and drives the authenticated effect by script.
- The owner's reading of the three new note texts.
- The rollback path in Verification 6 (user_version back to 10 plus `-RestoreFrom`) is inferred from code, not rehearsed.

## Claim ledger
| # | Claim | Verified at (commit) | Recheck (pwsh, exit 0 = holds) |
|---|-------|----------------------|--------------------------------|
| 1 | Baseline: 993 .NET tests green (224 Core + 769 Hub) and 99 client tests, measured this session at fc9e015a; `src/`, `tests/` and `tools/` unchanged since | fc9e015a | `git diff --quiet fc9e015a HEAD -- src tests tools` |
| 2 | Schema is v10 and the ladder ends at `ApplyV10` | fc9e015a | `if ((Select-String -LiteralPath src/ChopItUp.Core/Storage/ChopDb.cs -SimpleMatch 'public const int LatestSchemaVersion = 10;' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Core/Storage/ChopDb.cs -SimpleMatch 'if (GetUserVersion(conn) < 10) ApplyV10(conn);' -Quiet)) { exit 0 } else { exit 1 }` |
| 3 | `Message` is `(long Id, string RoomId, string AuthorId, string Body, DateTimeOffset CreatedAt)` | fc9e015a | `if (Select-String -LiteralPath src/ChopItUp.Core/Model/Message.cs -SimpleMatch 'public sealed record Message(long Id, string RoomId, string AuthorId, string Body, DateTimeOffset CreatedAt);' -Quiet) { exit 0 } else { exit 1 }` |
| 4 | `new Message(` appears 4 times in `MessageStore.cs` (FindByClientKey, ReadLast, Read, Post) and nowhere else under src or tests; tests build messages through target-typed `new(...)` | fc9e015a | `$all = @(Get-ChildItem -Path src,tests -Recurse -Filter *.cs \| Where-Object { $_.FullName -notmatch '\\(bin\|obj)\\' } \| Select-String -SimpleMatch 'new Message(').Count; $m = @(Select-String -LiteralPath src/ChopItUp.Core/Storage/MessageStore.cs -SimpleMatch 'new Message(').Count; if ($m -eq 4 -and $all -eq 4) { exit 0 } else { exit 1 }` |
| 5 | Sixteen schema-10 pins outside src: 12 `tools/*.ps1` lines with `schema -eq 10`, `Invoke-M23MemoryCheck.ps1` `ExpectedSchema = 10`, `Invoke-Row28SelfCheck.ps1` `expectedSchema = 10`, `Invoke-M25DryRun.ps1` `user_version'] -eq 10`, plus `SchemaMigrationTests.cs:643` `Assert.Equal(10, db.GetSchemaVersion());` | fc9e015a | `$t = @(Select-String -Path tools/*.ps1 -Pattern 'schema -eq 10\|xpectedSchema = 10\|user_version''\] -eq 10').Count; $c = @(Select-String -LiteralPath tests/ChopItUp.Core.Tests/Storage/SchemaMigrationTests.cs -SimpleMatch 'Assert.Equal(10, db.GetSchemaVersion());').Count; if ($t -eq 15 -and $c -eq 1) { exit 0 } else { exit 1 }` |
| 6 | The MCP tools serialize with `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull` | fc9e015a | `if (Select-String -LiteralPath src/ChopItUp.Hub/Mcp/RoomTools.cs -SimpleMatch 'DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull' -Quiet) { exit 0 } else { exit 1 }` |
| 7 | The web post endpoint binds `internal sealed record PostBody(string? Body);` and maps `new { m.Id, m.RoomId, m.AuthorId, m.Body, m.CreatedAt }` | fc9e015a | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Web/ChatApi.cs -SimpleMatch 'internal sealed record PostBody(string? Body);' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/Web/ChatApi.cs -SimpleMatch 'new { m.Id, m.RoomId, m.AuthorId, m.Body, m.CreatedAt }' -Quiet)) { exit 0 } else { exit 1 }` |
| 8 | `OnRoomMessage` ends `bool startsRun = false, bool hasDirectory = false)` and the service calls it once | fc9e015a | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Spawning/ExchangePolicy.cs -SimpleMatch 'bool acceptMentions = true, SkillResolution? skill = null, RunContext? run = null, bool startsRun = false, bool hasDirectory = false)' -Quiet) -and (@(Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch '_policy.OnRoomMessage(').Count -eq 1)) { exit 0 } else { exit 1 }` |
| 9 | `AddExchange` prunes closed idle exchanges when a new one opens | fc9e015a | `if (Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch 'else list.RemoveAll(e => e.Status != ExchangeStatus.Open && e.InFlight.Count == 0);' -Quiet) { exit 0 } else { exit 1 }` |
| 10 | The worktree close handler removes the room from `_closingRooms`, and the directory-launch close wait appears twice (LaunchDue, ArmWake) | fc9e015a | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch '_closingRooms.Remove(w.RoomId);' -Quiet) -and (@(Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch 'if (exclusive && over is null && _closingRooms.Contains(x.RoomId)) continue;').Count -eq 2)) { exit 0 } else { exit 1 }` |
| 11 | `EnsureAsync` refuses an existing branch with no registered worktree: `if (exists && !pruned) return new(null, $"branch {branch} already exists");` | fc9e015a | `if (Select-String -LiteralPath src/ChopItUp.Hub/Rooms/ExchangeWorktrees.cs -SimpleMatch 'if (exists && !pruned) return new(null, $"branch {branch} already exists");' -Quiet) { exit 0 } else { exit 1 }` |
| 12 | Fixture seams: `WriteRawV9` in SchemaMigrationTests, `Msg`/`Policy`/`NoStarts`/`Nobody` in ExchangePolicyTests, `PostAsOwner`/`WaitForMessage`/`Spawner` in SpawnerServiceTests, `MakeRoom`/`PostAsOwnerIn`/`WaitForMessageIn`/`DelayingMergeRunner` in SpawnerServiceTests.Rooms, `FakeProcessRunner.HangUntilKilled`/`NoSpecWithin`/`ParticipantOf` | fc9e015a | `if ((Select-String -LiteralPath tests/ChopItUp.Core.Tests/Storage/SchemaMigrationTests.cs -SimpleMatch 'private void WriteRawV9' -Quiet) -and (Select-String -LiteralPath tests/ChopItUp.Hub.Tests/Spawning/ExchangePolicyTests.cs -SimpleMatch 'private static Message Msg(long id, string author, string body)' -Quiet) -and (Select-String -LiteralPath tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.Rooms.cs -SimpleMatch 'private sealed class DelayingMergeRunner' -Quiet) -and (Select-String -LiteralPath tests/ChopItUp.Hub.Tests/Spawning/FakeProcessRunner.cs -SimpleMatch 'HangUntilKilled' -Quiet)) { exit 0 } else { exit 1 }` |
| 13 | Client component tests render with `renderToStaticMarkup` (no jsdom); `vitest run` is the client test script | fc9e015a | `if ((Select-String -LiteralPath src/ChopItUp.Hub/client/src/App.test.tsx -SimpleMatch 'renderToStaticMarkup' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/client/package.json -SimpleMatch '"test": "vitest run"' -Quiet)) { exit 0 } else { exit 1 }` |
| 15 | `Launch` captures per-spawn values on one line and leases through a single `EnsureAsync` call | fc9e015a | `if ((Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch 'var turn = request.TurnNumber; var budget = x.Budget; var roomId = request.RoomId; var host = participant.Host;' -Quiet) -and (@(Select-String -Path src/ChopItUp.Hub/Spawning/*.cs -SimpleMatch '_worktrees.EnsureAsync(').Count -eq 1)) { exit 0 } else { exit 1 }` |
| 16 | Test seams for the folds: `ExchangeWorktreesTests` has `RoomWithCommit`/`Close(`, `SpawnerService.StopExchangeAsync` exists, `setRoster` is exported, `SpawnPromptTests` has `Input(int turn, int remainingAfter, params Message[] transcript)` | fc9e015a | `if ((Select-String -LiteralPath tests/ChopItUp.Hub.Tests/Rooms/ExchangeWorktreesTests.cs -SimpleMatch 'RoomWithCommit(' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/Spawning/SpawnerService.cs -SimpleMatch 'StopExchangeAsync(string roomId, long rootMessageId)' -Quiet) -and (Select-String -LiteralPath src/ChopItUp.Hub/client/src/participants.ts -SimpleMatch 'export function setRoster(' -Quiet) -and (Select-String -LiteralPath tests/ChopItUp.Hub.Tests/Spawning/SpawnPromptTests.cs -SimpleMatch 'Input(int turn, int remainingAfter, params Message[] transcript)' -Quiet)) { exit 0 } else { exit 1 }` |
| 14 | The run rules never reach the join branch: every run path returns before `_policy.OnRoomMessage`, or passes `run` which returns before it | — | — (critic: read `SpawnerService.OnMessage` top to bottom) |

## Tasks

Branch: `row36-reply-to-joins` from `main` at fc9e015a. Repo root `C:\Agent Projects\ChopItUp`.

Baseline (measured this session at fc9e015a): **993 .NET tests green (224 Core + 769 Hub, Hub run 7 m 49 s) and 99 client tests green.** Task 1 changes one existing assertion (`SchemaMigrationTests.cs:643`); no other existing test is edited by any task.

Commands used by every task:
```powershell
dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal
dotnet test tests/ChopItUp.Core.Tests -c Debug --nologo -v minimal
dotnet test tests/ChopItUp.Hub.Tests -c Debug --nologo -v minimal --filter "FullyQualifiedName~ExchangePolicyTests"
dotnet test tests/ChopItUp.Hub.Tests -c Debug --nologo -v minimal --filter "FullyQualifiedName~SpawnerServiceTests|FullyQualifiedName~ChatApiTests|FullyQualifiedName~RoomToolsTests|FullyQualifiedName~SpawnPromptTests|FullyQualifiedName~RealtimeTests"
dotnet test ChopItUp.slnx -c Debug --nologo -v minimal
npm --prefix src/ChopItUp.Hub/client test
```
Never build while a Debug hub runs (exe lock). Each task ends with the full `dotnet test ChopItUp.slnx` green and one commit.

### Task 1: messages carry a reply-to, schema v11 (sonnet)

Files: `src/ChopItUp.Core/Model/Message.cs`, `src/ChopItUp.Core/Storage/ChopDb.cs`, `src/ChopItUp.Core/Storage/MessageStore.cs`, `tests/ChopItUp.Core.Tests/Storage/SchemaMigrationTests.cs`, `tests/ChopItUp.Core.Tests/Storage/MessageStoreTests.cs`, and the 14 `tools/*.ps1` files holding the 15 version lines of ledger claim 5 plus their check names and comments.

**1a. RED.** Add to `SchemaMigrationTests` (reuse `WriteRawV9`; add the usings the file lacks):

```csharp
    /// <summary>A real-shape v10 file: v9 plus row 25's skill_proposals table, stamped 10.</summary>
    private void WriteRawV10()
    {
        WriteRawV9();
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE skill_proposals (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                room_id            TEXT NOT NULL REFERENCES rooms(id),
                author_id          TEXT NOT NULL REFERENCES participants(id),
                name               TEXT NOT NULL,
                source_dir         TEXT NOT NULL,
                tree_sha256        TEXT NOT NULL,
                replaces_installed INTEGER NOT NULL,
                force              INTEGER NOT NULL,
                files              INTEGER NOT NULL,
                bytes              INTEGER NOT NULL,
                status             TEXT NOT NULL DEFAULT 'pending',
                created_at         TEXT NOT NULL,
                decided_at         TEXT,
                installed_at       TEXT
            );
            CREATE INDEX ix_skill_proposals_status ON skill_proposals(status, room_id, id);
            PRAGMA user_version = 10;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private static long ReplyColumnCount(SqliteConnection conn)
    {
        using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name = 'reply_to_id'";
        return (long)probe.ExecuteScalar()!;
    }

    [Fact]
    public void R36_T1_v10_database_is_backed_up_then_migrated_to_v11_with_reply_to_id_and_every_message_unchanged()
    {
        WriteRawV10();
        using (var before = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            before.Open();
            Assert.Equal(0L, ReplyColumnCount(before));                      // the premise: a real v10 shape
            using var v = before.CreateCommand();
            v.CommandText = "PRAGMA user_version;";
            Assert.Equal(10L, (long)v.ExecuteScalar()!);
        }

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(11, db.GetSchemaVersion());
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.Contains(".v10.", Path.GetFileName(db.LastBackupPath!));
        using (var conn = db.Open()) Assert.Equal(1L, ReplyColumnCount(conn));

        var page = new MessageStore(db).Read("general", 0, 50);
        Assert.Equal([(1L, "owner", "@opus first v3 message", (long?)null), (2L, "opus", "second v3 message", null)],
            page.Messages.Select(m => (m.Id, m.AuthorId, m.Body, m.ReplyToId)));

        db.EnsureDatabase();
        Assert.Null(db.LastBackupPath);
    }

    [Fact]
    public void R36_T1_a_torn_v11_with_the_column_present_but_stamp_10_is_finished_not_crashed()
    {
        WriteRawV10();
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE messages ADD COLUMN reply_to_id INTEGER REFERENCES messages(id);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(11, db.GetSchemaVersion());
        using var check = db.Open();
        Assert.Equal(1L, ReplyColumnCount(check));
    }
```

Add to `MessageStoreTests` (match that file's existing setup names for the store and db; if it has no `CreateRoom` for a second room, call `store.CreateRoom("other", "Other", null)`):

```csharp
    [Fact]
    public void R36_a_reply_is_stored_and_read_back_with_its_target()
    {
        var first = store.Post("general", "owner", "@opus hello");
        var reply = store.Post("general", "owner", "@opus and this", null, replyToId: first.Id).Message;

        Assert.Equal(first.Id, reply.ReplyToId);
        Assert.Equal([null, first.Id], store.Read("general", 0, 50).Messages.Select(m => m.ReplyToId));
        Assert.Equal([null, first.Id], store.ReadLast("general", 10).Select(m => m.ReplyToId));
    }

    [Fact]
    public void R36_a_reply_to_a_message_of_another_room_or_no_message_is_refused_and_stores_nothing()
    {
        store.CreateRoom("other", "Other", null);
        var elsewhere = store.Post("other", "owner", "over here");

        var cross = Assert.Throws<ArgumentException>(() => store.Post("general", "owner", "reply", null, replyToId: elsewhere.Id));
        Assert.Equal("replyToId", cross.ParamName);
        var missing = Assert.Throws<ArgumentException>(() => store.Post("general", "owner", "reply", null, replyToId: 9_999));
        Assert.Equal("replyToId", missing.ParamName);
        Assert.Empty(store.Read("general", 0, 50).Messages);
    }

    [Fact]
    public void R36_a_retried_client_key_returns_the_original_reply_target()
    {
        var first = store.Post("general", "owner", "root");
        var original = store.Post("general", "opus", "answer", "k-36", replyToId: first.Id);
        var retry = store.Post("general", "opus", "answer", "k-36", replyToId: null);

        Assert.True(retry.Deduplicated);
        Assert.Equal(first.Id, retry.Message.ReplyToId);
        Assert.Equal(original.Message.Id, retry.Message.Id);
    }
```
Run `dotnet test tests/ChopItUp.Core.Tests` and record the compile errors (RED: `ReplyToId` and `replyToId` do not exist).

**1b. GREEN.**

`Message.cs`, replace the `Message` line:
```csharp
/// <summary>A stored message. <see cref="ReplyToId"/> (row 36) is the id of the message this one replies
/// to, always in the same room, or null.</summary>
public sealed record Message(long Id, string RoomId, string AuthorId, string Body, DateTimeOffset CreatedAt, long? ReplyToId = null);
```

`ChopDb.cs`: `LatestSchemaVersion = 11`; add `if (GetUserVersion(conn) < 11) ApplyV11(conn);` after the v10 line; add after `ApplyV10`:
```csharp
    /// <summary>v11 (row 36): <c>messages.reply_to_id</c>, the message a post replies to, or NULL for
    /// every message written before it and every post that is not a reply. Same room is enforced by
    /// <see cref="MessageStore.Post(string,string,string,string?,long?)"/>, not by the schema. The column
    /// is probed inside the transaction (a torn v11 re-runs cleanly) and the stamp is the last statement
    /// (LESSONS, M1). No index: nothing queries by it.</summary>
    private static void ApplyV11(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();

        bool hasColumn;
        using (var probe = conn.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name = 'reply_to_id'";
            hasColumn = Convert.ToInt64(probe.ExecuteScalar()) > 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = (hasColumn ? "" : "ALTER TABLE messages ADD COLUMN reply_to_id INTEGER REFERENCES messages(id);\n") + "PRAGMA user_version = 11;";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
```

`MessageStore.cs`:
- Replace the four-argument `Post` signature with `public PostResult Post(string roomId, string authorId, string body, string? clientKey, long? replyToId = null)` and extend its doc comment with one sentence: "<paramref name="replyToId"/> must name a message of the same room, or the post is refused with an <see cref="ArgumentException"/> whose ParamName is <c>replyToId</c>." The three-argument overload is unchanged.
- Directly after the client-key dedup check (`if (clientKey is not null && FindByClientKey(...) is { } already) return ...;`) and before `BeginTransaction`, add:
```csharp
        if (replyToId is { } target && !IsInRoom(conn, roomId, target))
            throw new ArgumentException($"Message #{target} is not in room '{roomId}'.", nameof(replyToId));
```
- The insert becomes `INSERT INTO messages (room_id, author_id, body, created_at, client_key, reply_to_id) VALUES ($room, $author, $body, $at, $key, $reply);` with `insert.Parameters.AddWithValue("$reply", (object?)replyToId ?? DBNull.Value);`, and the returned record is `new Message(id, roomId, authorId, body, createdAt, replyToId)`.
- Every `SELECT id, room_id, author_id, body, created_at` in this file (FindByClientKey, both levels of ReadLast, Read) becomes `SELECT id, room_id, author_id, body, created_at, reply_to_id`, and each `new Message(...)` there is replaced by `ReadMessage(reader)`:
```csharp
    private static Message ReadMessage(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Timestamps.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetInt64(5));

    private static bool IsInRoom(SqliteConnection conn, string roomId, long messageId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM messages WHERE id = $id AND room_id = $room";
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.Parameters.AddWithValue("$room", roomId);
        return cmd.ExecuteScalar() is not null;
    }
```

Schema pins (each a one-token edit, meaning unchanged: "the schema this build writes"):
- `tests/ChopItUp.Core.Tests/Storage/SchemaMigrationTests.cs:643` `Assert.Equal(10, db.GetSchemaVersion());` becomes `Assert.Equal(11, db.GetSchemaVersion());   // the ladder runs through v11 too now`.
- In `tools/*.ps1`: every `schema -eq 10` becomes `schema -eq 11`; every check name `health.schema-is-10` becomes `health.schema-is-11`; `Invoke-M23MemoryCheck.ps1` `[int]$ExpectedSchema = 10` becomes `11`; `Invoke-Row28SelfCheck.ps1` `$expectedSchema = 10` becomes `11` (its comment's "as of row 28" becomes "as of row 36"); `Invoke-M2DryRun.ps1:146` comment "schema 10" becomes "schema 11"; `Invoke-M25DryRun.ps1` `migrated.stamped-v10` / `-eq 10` become `migrated.stamped-v11` / `-eq 11`, and after that check add:
```powershell
    Add-Check -Name 'migrated.messages-have-reply-to-id' -Passed ([bool]$after['has_reply_to_id']) -Detail "has_reply_to_id=$($after['has_reply_to_id'])"
```
  `$after` is the `[ordered]` dictionary `Get-Counts` returns (`Invoke-M25DryRun.ps1:382`); add a `has_reply_to_id` key inside `Get-Counts`, filled from `SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name = 'reply_to_id'` in the same style as its other keys.
- Recheck after the sweep: `Select-String -Path tools/*.ps1 -Pattern 'schema[- ](is-|eq )?10\b|schema 10\b|stamped-v10|xpectedSchema = 10|user_version''\] -eq 10'` returns nothing except lines that describe row 25's own v9 to v10 history in `Invoke-M25DryRun.ps1`'s header comment (leave those, and list them in the commit message).

Commit: `Row 36 task 1: messages carry a reply-to (schema v11)`.

### Task 2: the web API, the broadcast, read_messages and the spawn prompt show it (sonnet)

Blocked by Task 1. Files: `src/ChopItUp.Hub/Web/ChatApi.cs`, `src/ChopItUp.Hub/Hosting/HubHost.cs`, `src/ChopItUp.Hub/Spawning/SpawnPrompt.cs`, `tests/ChopItUp.Hub.Tests/ChatApiTests.cs`, `tests/ChopItUp.Hub.Tests/RoomToolsTests.cs`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnPromptTests.cs`, and `tests/ChopItUp.Hub.Tests/RealtimeTests.cs`.

**2a. RED.** In `ChatApiTests`, using that file's owner-authorized client and host (open the file; the owner client is whatever its existing 201 post test uses):
```csharp
    [Fact]
    public async Task R36_a_post_with_replyToId_is_stored_listed_and_echoed()
    {
        var root = await PostJson(new { body = "root" });
        var reply = await PostJson(new { body = "a reply", replyToId = root.GetProperty("id").GetInt64() });

        Assert.Equal(root.GetProperty("id").GetInt64(), reply.GetProperty("replyToId").GetInt64());
        using var list = JsonDocument.Parse(await Client.GetStringAsync("api/rooms/general/messages?afterId=0"));
        var ids = list.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("replyToId").ValueKind == JsonValueKind.Null ? (long?)null : m.GetProperty("replyToId").GetInt64()).ToList();
        Assert.Equal([null, root.GetProperty("id").GetInt64()], ids);
    }

    [Fact]
    public async Task R36_a_replyToId_outside_the_room_is_400_and_stores_nothing()
    {
        var r = await Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "reply", replyToId = 9_999 });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("#9999", await r.Content.ReadAsStringAsync());
        using var list = JsonDocument.Parse(await Client.GetStringAsync("api/rooms/general/messages?afterId=0"));
        Assert.Empty(list.RootElement.GetProperty("messages").EnumerateArray());
    }
```
`PostJson` and `Client` stand for that file's own helper and owner client; add a small `PostJson(object)` helper (POST, assert 201, return the parsed root element clone) if none exists.

In `RoomToolsTests` (use its existing owner-post seam and MCP client for a participant): post a root and a reply (reply through `MessageStore.Post(..., null, replyToId: rootId)` resolved from the host's services), call `read_messages` with `after_id = 0`, and assert the reply element has `reply_to_id` equal to the root id and the root element has no `reply_to_id` property (`TryGetProperty` false). Name it `R36_read_messages_shows_reply_to_id_only_on_replies`.

In `SpawnPromptTests`:
```csharp
    [Fact]
    public void R36_a_reply_is_marked_in_the_transcript()
    {
        var prompt = SpawnPrompt.Render(Input(1, 3, Msg(1, "owner", "root"), Msg(2, "owner", "@opus reply") with { ReplyToId = 1 }), SpawnLimits.Default);
        var lines = prompt.Split('\n');
        Assert.Single(lines, l => l.StartsWith("#2 owner at ") && l.EndsWith(" (reply to #1)"));
        Assert.Single(lines, l => l.StartsWith("#1 owner at ") && !l.Contains("(reply to"));
    }
```

In `RealtimeTests`:
```csharp
    [Fact]
    public async Task R36_a_reply_posted_through_the_web_api_carries_replyToId_in_the_broadcast()
    {
        _host.AuthorizeAs(ChopItUp.Core.Storage.ChopDb.OwnerParticipantId);
        var root = await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "root" });
        var rootId = JsonDocument.Parse(await root.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();
        await using var connection = await ConnectAsync("general");
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("MessagePosted", msg => received.TrySetResult(msg));

        var reply = await _host.Client.PostAsJsonAsync("api/rooms/general/messages", new { body = "a reply", replyToId = rootId });
        Assert.Equal(System.Net.HttpStatusCode.Created, reply.StatusCode);

        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(rootId, payload.GetProperty("replyToId").GetInt64());
    }
```
Add `using System.Net.Http.Json;` if the file lacks it.

**2b. GREEN.**
- `ChatApi.PostBody` becomes `internal sealed record PostBody(string? Body, long? ReplyToId = null);`
- `PostMessage`: replace the `store.Post` line with
```csharp
        Message message;
        try { message = store.Post(roomId, authorId, body.Body, null, body.ReplyToId).Message; }   // no client_key on this surface
        catch (ArgumentException e) when (e.ParamName == "replyToId") { return Results.BadRequest(new { error = e.Message }); }
```
  and add to its doc comment: "Row 36: <c>replyToId</c> must name a message of the same room (400 otherwise); the spawner reads it to decide which exchange the post joins."
- `MapMessage` becomes `new { m.Id, m.RoomId, m.AuthorId, m.Body, m.CreatedAt, m.ReplyToId }`.
- `HubHost.BroadcastAsync` payload gains `message.ReplyToId,` after `message.CreatedAt,`.
- `SpawnPrompt` transcript loop header line becomes:
```csharp
            sb.Append('\n').Append('#').Append(m.Id).Append(' ').Append(m.AuthorId).Append(" at ").Append(Timestamps.Stamp(m.CreatedAt));
            if (m.ReplyToId is { } replyTo) sb.Append(" (reply to #").Append(replyTo).Append(')');
            sb.Append('\n');
```
  `SpawnPrompt.Trim` budgets 48 chars per message header; leave it (a reply marker is under 24 chars and the transcript cap is a soft budget). Note that in the commit message.

Commit: `Row 36 task 2: the API, broadcast, read_messages and spawn prompt show reply-to`.

### Task 3: the policy joins a reply to its exchange (sonnet)

Blocked by Task 1. Files: `src/ChopItUp.Hub/Spawning/Exchange.cs`, `src/ChopItUp.Hub/Spawning/ExchangePolicy.cs`, `tests/ChopItUp.Hub.Tests/Spawning/ExchangePolicyTests.cs`.

**3a. RED.** Add to `ExchangePolicyTests`:
```csharp
    private static Message Reply(long id, string author, string body, long replyTo) => new(id, "general", author, body, T0, replyTo);

    [Fact]
    public void R36_a_reply_to_an_open_exchange_joins_it_against_its_budget_and_supersedes_nothing()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);
        ExchangePolicy.Started(a!, p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).Single());

        var (opened, notes) = p.OnRoomMessage([a!], null, Reply(2, "owner", "@opus and also this @sonnet", 1), T0.AddSeconds(3), joins: a);

        Assert.Null(opened);
        Assert.Empty(notes);
        Assert.Equal(ExchangeStatus.Open, a!.Status);                 // row 32 would have superseded it (opus overlaps)
        Assert.Equal(["opus", "sonnet"], a.Pending.Keys);
        Assert.Equal(3, a.TurnsCommitted);
        Assert.Contains(2L, a.MessageIds);
    }

    [Fact]
    public void R36_a_reply_with_a_mention_reopens_a_concluded_exchange_with_its_turns_and_skill()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "/build-thing @opus go"), T0, skill: new SkillResolution.Found(RunSkill, "go"));
        var launch = p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).Single();
        ExchangePolicy.Started(a!, launch);
        Assert.NotNull(ExchangePolicy.Finished(a!, "opus"));
        Assert.Equal(ExchangeStatus.Concluded, a!.Status);

        var (opened, notes) = p.OnRoomMessage([a!], null, Reply(5, "owner", "@opus keep going", 1), T0.AddSeconds(9), joins: a);

        Assert.Null(opened);
        Assert.Empty(notes);
        Assert.Equal(ExchangeStatus.Open, a.Status);
        Assert.Same(RunSkill, a.Skill);
        var next = p.Due(a, T0.AddSeconds(12), NoStarts, Nobody).Single();
        Assert.Equal((1L, 2, 2), (next.RootMessageId, next.TurnNumber, next.RemainingAfter));
    }

    [Fact]
    public void R36_a_reply_reopens_a_stopped_or_superseded_exchange_and_clears_the_stop_cause()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);
        ExchangePolicy.Stop(a!, ExchangeStopCause.Owner);

        p.OnRoomMessage([a!], null, Reply(2, "owner", "@opus resume", 1), T0.AddSeconds(1), joins: a);

        Assert.Equal(ExchangeStatus.Open, a!.Status);
        Assert.Null(a.StopCause);
        Assert.Equal(["opus"], a.Pending.Keys);
    }

    [Fact]
    public void R36_a_reopen_spends_only_the_turns_that_ran_not_the_ones_a_stop_dropped()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus @sonnet @fable task"), T0);
        ExchangePolicy.Started(a!, p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).First(d => d.ParticipantId == "opus"));
        ExchangePolicy.Stop(a!, ExchangeStopCause.Owner);
        ExchangePolicy.Finished(a!, "opus");
        Assert.Equal((3, 1), (a!.TurnsCommitted, a.TurnsStarted));

        p.OnRoomMessage([a], null, Reply(2, "owner", "@sonnet go on", 1), T0.AddSeconds(5), joins: a);

        var next = p.Due(a, T0.AddSeconds(8), NoStarts, Nobody).Single();
        Assert.Equal((2, 2), (next.TurnNumber, next.RemainingAfter));
    }

    [Fact]
    public void R36_a_reply_to_a_closed_exchange_still_finishing_a_spawn_notes_it_and_accepts_nothing()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);
        ExchangePolicy.Started(a!, p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        p.OnRoomMessage([a!], null, Msg(2, "owner", "@opus redo"), T0.AddSeconds(3));   // supersedes a, opus still running
        Assert.Equal(ExchangeStatus.Superseded, a!.Status);

        var (opened, notes) = p.OnRoomMessage([a], null, Reply(3, "owner", "@sonnet help", 1), T0.AddSeconds(4), joins: a);

        Assert.Null(opened);
        Assert.Equal(["Exchange #1 is still finishing @opus; reply again once it has."], notes);
        Assert.Equal(ExchangeStatus.Superseded, a.Status);
        Assert.Empty(a.Pending);
    }

    [Fact]
    public void R36_a_reply_with_no_mention_changes_no_state()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);
        ExchangePolicy.Started(a!, p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        ExchangePolicy.Finished(a!, "opus");

        var (opened, notes) = p.OnRoomMessage([a!], null, Reply(2, "owner", "thanks", 1), T0.AddSeconds(3), joins: a);

        Assert.Null(opened);
        Assert.Empty(notes);
        Assert.Equal((ExchangeStatus.Concluded, 1, 0), (a!.Status, a.TurnsCommitted, a.Pending.Count));
        Assert.Contains(2L, a.MessageIds);
    }

    [Fact]
    public void R36_a_reply_whose_mentions_are_all_over_budget_does_not_reopen()
    {
        var p = new ExchangePolicy(ChopDb.SeedRoster, Limits with { Budget = 1 });
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);
        ExchangePolicy.Started(a!, p.Due(a!, T0.AddSeconds(2), NoStarts, Nobody).Single());
        ExchangePolicy.Finished(a!, "opus");

        var (_, notes) = p.OnRoomMessage([a!], null, Reply(2, "owner", "@sonnet more", 1), T0.AddSeconds(3), joins: a);

        Assert.Equal(ExchangeStatus.Concluded, a!.Status);
        Assert.Single(notes, n => n.StartsWith("Budget of 1 turns is used up for the exchange started at #1"));
    }

    [Fact]
    public void R36_an_unresolved_reply_with_a_mention_is_a_new_prompt_with_a_note()
    {
        var p = Policy();
        var (opened, notes) = p.OnRoomMessage([], null, Reply(4, "owner", "@opus go", 2), T0, joins: null);

        Assert.Equal(4, opened!.RootMessageId);
        Assert.Equal(["Reply to #2: that message is in no exchange this hub still holds, so this post was handled as a new prompt."], notes);
    }

    [Fact]
    public void R36_an_unresolved_reply_with_no_mention_posts_nothing()
    {
        var (opened, notes) = Policy().OnRoomMessage([], null, Reply(4, "owner", "just a thought", 2), T0, joins: null);
        Assert.Null(opened);
        Assert.Empty(notes);
    }

    [Fact]
    public void R36_a_reply_that_invokes_a_skill_is_a_new_prompt_with_a_note_even_when_its_exchange_is_held()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);

        var (opened, notes) = p.OnRoomMessage([a!], null, Reply(2, "owner", "/build-thing @sonnet go", 1), T0.AddSeconds(1),
            skill: new SkillResolution.Found(RunSkill, "go"), joins: a);

        Assert.Equal(2, opened!.RootMessageId);
        Assert.Equal("A reply that invokes /" + RunSkill.Name + " does not join an exchange; it was handled as a new prompt.", notes[0]);
        Assert.Equal(["opus"], a!.Pending.Keys);                        // disjoint: row 32's rule, untouched
    }

    [Fact]
    public void R36_a_reply_with_a_refused_skill_is_handled_as_the_same_post_without_reply_to()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);

        var (opened, notes) = p.OnRoomMessage([a!], null, Reply(2, "owner", "/typo @opus go", 1), T0.AddSeconds(1),
            skill: new SkillResolution.Unknown("typo", []), joins: a);

        Assert.Null(opened);
        Assert.Equal(ExchangeStatus.Superseded, a!.Status);           // row 32's overlap rule, as without reply-to
        Assert.Single(notes);
        Assert.StartsWith("No skill named '/typo'", notes[0]);
    }

    [Fact]
    public void R36_a_reply_to_a_run_exchange_is_an_unresolved_reply()
    {
        var p = Policy();
        var conductor = ExchangePolicy.OpenForConductor("general", "opus", 1, [1], T0, RunSkill);
        ExchangePolicy.Started(conductor, p.Due(conductor, T0.AddSeconds(2), NoStarts, Nobody).Single());
        ExchangePolicy.Finished(conductor, "opus");
        conductor.MessageIds.Add(2);                                     // the conductor's own post, as the service records it

        var (opened, notes) = p.OnRoomMessage([conductor], null, Reply(3, "owner", "@sonnet pick this up", 2), T0.AddSeconds(5), joins: conductor);

        Assert.False(conductor.Joinable);
        Assert.Equal(ExchangeStatus.Concluded, conductor.Status);
        Assert.Equal(3, opened!.RootMessageId);
        Assert.True(opened.Joinable);
        Assert.Equal(["Reply to #2: that message is in no exchange this hub still holds, so this post was handled as a new prompt."], notes);
    }

    [Fact]
    public void R36_a_run_start_exchange_is_not_joinable()
    {
        var (opened, _) = Policy().OnRoomMessage([], null, Msg(1, "owner", "/build-thing @opus go"), T0,
            skill: new SkillResolution.Found(RunSkill, "go"), startsRun: true, hasDirectory: true);
        Assert.False(opened!.Joinable);
    }

    [Fact]
    public void R36_inside_a_run_a_reply_is_left_to_the_run()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);

        var (opened, notes) = p.OnRoomMessage([a!], null, Reply(2, "owner", "@sonnet go", 1), T0.AddSeconds(1),
            run: new RunContext(7, "opus", "plan"), joins: a);

        Assert.Null(opened);
        Assert.Empty(notes);
        Assert.Equal(["opus"], a!.Pending.Keys);
    }

    [Fact]
    public void R36_an_exchange_counts_its_root_and_the_model_posts_routed_to_it_as_members()
    {
        var p = Policy();
        var (a, _) = p.OnRoomMessage([], null, Msg(1, "owner", "@opus task A"), T0);
        p.OnRoomMessage([a!], a, Msg(2, "claude", "noted"), T0.AddSeconds(1));

        Assert.Equal([1L, 2L], a!.MessageIds.Order());
    }
```
Before writing these, open the file: confirm `RunSkill` is a `ResolvedSkill` field with a `Name`, that `Limits` is a `SpawnLimits` record (so `with { Budget = 1 }` compiles), and that `claude` is app-backed in `ChopDb.SeedRoster`. If `RunSkill.IsRun` makes `OnRoomMessage` require `startsRun`, it does not: `startsRun` is only what the caller passes. If any of those do not hold, STOP and report. Run the filter; RED is the compile error on `joins` and `MessageIds`.

**3b. GREEN.**

`Exchange.cs`, add after `Participants`:
```csharp
    /// <summary>Row 36: the messages that belong to this exchange, so an owner reply to any of them joins
    /// it. Its root, every owner reply that joined it, every post of its own spawns and every app-backed
    /// model post routed to it while open. Hub notes are never members.</summary>
    public HashSet<long> MessageIds { get; } = new();

    /// <summary>Row 36: opened by an owner prompt that was not a run-start. Only such an exchange can be
    /// joined by a reply; a run's own exchanges never can.</summary>
    public bool Joinable { get; init; }
```

`ExchangePolicy.OnRoomMessage`:
- Signature gains a last parameter: `..., bool hasDirectory = false, Exchange? joins = null)`. Doc comment gains: "<paramref name="joins"/> (row 36) is the exchange that holds the message an owner post replies to, as the service resolved it (any status), or null when the post is not a reply or its target is in no exchange the service holds."
- In the model branch, after the `target is not { Status: ExchangeStatus.Open }` return, add `target.MessageIds.Add(message.Id);` before `Accept`.
- Directly after `if (run is not null) return (null, notes);` insert:
```csharp
        // Row 36: a reply joins the exchange its target belongs to. A skill invocation or a run-start is
        // always a new prompt, and says so when it was a reply.
        if (message.ReplyToId is { } replyTo)
        {
            if (skill is SkillResolution.Found invoked)
                notes.Add($"A reply that invokes /{invoked.Skill.Name} does not join an exchange; it was handled as a new prompt.");
            else if (skill is null or SkillResolution.None && !startsRun)
            {
                if (joins is { Joinable: true })
                {
                    Join(joins, mentioned, message.Id, now, notes);
                    return (null, notes);
                }
                if (mentioned.Count > 0)
                    notes.Add($"Reply to #{replyTo}: that message is in no exchange this hub still holds, so this post was handled as a new prompt.");
            }
        }
```
- Where `next` is built, add `Joinable = !startsRun,` to its object initializer and `next.MessageIds.Add(message.Id);` right after it. A run's exchanges (`OpenForWorkers`, `OpenForConductor`, and a run-start's conductor exchange) are never joinable, so a reply to a conductor's post after its run ended can never reopen a Budget-1 run-skill exchange outside a run; it is an unresolved reply instead.
- Add beside `Supersede`:
```csharp
    /// <summary>Row 36: an owner reply lands in <paramref name="x"/> and nothing is superseded. Open: its
    /// mentions are accepted against the exchange's own remaining budget. Closed with nothing in flight:
    /// it reopens with its turns and skill kept, but only if a mention was actually accepted; otherwise
    /// it stays exactly as it was. Closed with a spawn still running: nothing is accepted and a note names
    /// the spawn. With no mention the reply is only recorded as a member.</summary>
    private static void Join(Exchange x, IReadOnlyList<string> mentioned, long messageId, DateTimeOffset now, List<string> notes)
    {
        x.MessageIds.Add(messageId);
        if (mentioned.Count == 0) return;
        if (x.Status == ExchangeStatus.Open)
        {
            Accept(x, mentioned, messageId, now, notes);
            return;
        }
        if (x.InFlight.Count > 0)
        {
            notes.Add($"Exchange #{x.RootMessageId} is still finishing {string.Join(", ", x.InFlight.Order(StringComparer.Ordinal).Select(id => "@" + id))}; reply again once it has.");
            return;
        }
        var (status, cause, committed) = (x.Status, x.StopCause, x.TurnsCommitted);
        x.Status = ExchangeStatus.Open;
        x.StopCause = null;
        x.TurnsCommitted = x.TurnsStarted;   // a stop or supersede dropped queued turns that never ran; only launched turns stay spent
        Accept(x, mentioned, messageId, now, notes);
        if (x.Pending.Count == 0) (x.Status, x.StopCause, x.TurnsCommitted) = (status, cause, committed);
    }
```
- Update the class summary's supersede sentence to add: "An owner reply joins the exchange it replies to instead (row 36)."

Commit: `Row 36 task 3: the policy joins a reply to its exchange`.

### Task 4: the spawner resolves the exchange and puts a reopened one back (sonnet)

Blocked by Tasks 2 and 3. Files: `src/ChopItUp.Hub/Spawning/SpawnerService.cs`, `src/ChopItUp.Hub/Spawning/Exchange.cs`, `src/ChopItUp.Hub/Rooms/ExchangeWorktrees.cs`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.cs`, `tests/ChopItUp.Hub.Tests/Spawning/SpawnerServiceTests.Rooms.cs`, `tests/ChopItUp.Hub.Tests/Rooms/ExchangeWorktreesTests.cs`.

**4a. RED.** In `SpawnerServiceTests.cs` add a helper beside `PostAsOwner` and four tests:
```csharp
    private async Task<long> PostAsOwnerReply(string body, long replyToId, string room = "general")
    {
        var r = await _host.Client.PostAsJsonAsync($"api/rooms/{room}/messages", new { body, replyToId });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task R36_a_reply_to_a_concluded_exchange_reopens_it_and_continues_its_turns()
    {
        await PostAsOwner("@opus first ask");                                                        // #1
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        await WaitForMessage(m => m.Body == "Exchange concluded: 1 of 4 turns used.");
        await PostAsOwner("@gpt-6-astra unrelated");                                                 // opens a second exchange, pruning #1 from the room list
        await _runner.NextSpecAsync(Wait);
        var deadline = DateTime.UtcNow + Wait;
        while ((await Messages()).Count(m => m.Body.StartsWith("Exchange concluded")) < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "the second exchange never concluded");
            await Task.Delay(100);
        }
        Assert.DoesNotContain(Spawner.Snapshot("general").Exchanges!, e => e.RootMessageId == 1);   // the premise: #1 was pruned

        var reply = await PostAsOwnerReply("@sonnet follow up on the first ask", 1);
        var sonnet = await _runner.NextSpecAsync(Wait);

        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(sonnet));
        Assert.Contains("Turn 2 of 4; 2 turn(s) remain after yours.", sonnet.StandardInput);
        Assert.Contains($"#{reply} owner at ", sonnet.StandardInput);
        Assert.Contains("(reply to #1)", sonnet.StandardInput);
        await WaitForMessage(m => m.Body == "Exchange concluded: 2 of 4 turns used.");
        Assert.Contains(Spawner.Snapshot("general").Exchanges!, e => e.RootMessageId == 1 && e.TurnsUsed == 2);
    }
```
Then:
```csharp
    [Fact]
    public async Task R36_a_reply_to_an_open_exchange_joins_it_instead_of_superseding_it()
    {
        _runner.Handler = (_, timeout, ct) => FakeProcessRunner.HangUntilKilled(TimeSpan.FromSeconds(30), ct);
        await PostAsOwner("@opus task A");                                                           // #1
        await _runner.NextSpecAsync(Wait);                                                           // opus in flight

        await PostAsOwnerReply("@opus also look at B", 1);

        var deadline = DateTime.UtcNow + Wait;
        ExchangeView? view = null;
        while (DateTime.UtcNow < deadline)
        {
            view = Spawner.Snapshot("general").Exchanges?.SingleOrDefault();
            if (view is { TurnsCommitted: 2 }) break;
            await Task.Delay(50);
        }
        Assert.NotNull(view);
        Assert.Equal((1L, "open", 2), (view!.RootMessageId, view.Status, view.TurnsCommitted));
        Assert.Equal(["opus"], view.Pending);
        Assert.Single(Spawner.Snapshot("general").Exchanges!);
    }

    [Fact]
    public async Task R36_a_reply_to_a_hub_note_with_a_mention_is_a_new_prompt_and_says_so()
    {
        await PostAsOwner("@opus go");                                                               // #1
        await _runner.NextSpecAsync(Wait);
        await WaitForMessage(m => m.Body == "Exchange concluded: 1 of 4 turns used.");
        var noteId = await LastIdOf(ChopDb.HubParticipantId);

        var reply = await PostAsOwnerReply("@sonnet what about this note", noteId);

        var sonnet = await _runner.NextSpecAsync(Wait);
        Assert.Contains("Turn 1 of 4; 3 turn(s) remain after yours.", sonnet.StandardInput);
        await WaitForMessage(m => m.Body == $"Reply to #{noteId}: that message is in no exchange this hub still holds, so this post was handled as a new prompt.");
        Assert.Contains(Spawner.Snapshot("general").Exchanges!, e => e.RootMessageId == reply);
    }

    [Fact]
    public async Task R36_a_spawn_post_after_its_exchange_closed_is_still_a_member_so_a_reply_to_it_joins()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) != "opus") return FakeProcessRunner.Ok("""{"result":"done"}""");
            await stopped.Task;                                                                      // ignores cancellation on purpose
            await PostAs("opus", "a late answer");                                                   // the exchange is closed: only the service records membership
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };
        await PostAsOwner("@opus go");                                                               // #1
        await _runner.NextSpecAsync(Wait);
        var (outcome, _) = await Spawner.StopExchangeAsync("general", 1);
        Assert.Equal(ExchangeStopOutcome.Stopped, outcome);
        stopped.TrySetResult();
        await WaitForMessage(m => m.Author == "opus" && m.Body == "a late answer");
        var lateId = await LastIdOf("opus");
        var deadline = DateTime.UtcNow + Wait;
        while (Spawner.Snapshot("general").InFlight.Count > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "opus never finished");
            await Task.Delay(50);
        }

        await PostAsOwnerReply("@sonnet check that answer", lateId);

        var sonnet = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(sonnet));
        Assert.Contains("Turn 2 of 4; 2 turn(s) remain after yours.", sonnet.StandardInput);
    }

    private async Task<long> LastIdOf(string author)
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync("api/rooms/general/messages?afterId=0&limit=200"));
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .Last(m => m.GetProperty("authorId").GetString() == author).GetProperty("id").GetInt64();
    }
```
In `SpawnerServiceTests.Rooms.cs`, two tests:
```csharp
    [Fact]
    public async Task R36_a_reopened_exchange_in_a_directory_room_leases_its_worktree_again_after_the_merge()
    {
        var dir = await MakeRoom("lab");
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"type":"result","result":"done"}"""));

        await PostAsOwnerIn("lab", "@opus go");
        var first = await _runner.NextSpecAsync(Wait);
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;
        Assert.Equal(ExchangeWorktrees.PathFor(dir, root), first.WorkingDirectory);
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} merged into"));   // every worktree spawn makes an agent commit, so HEAD moves

        var r = await _host.Client.PostAsJsonAsync("api/rooms/lab/messages", new { body = "@sonnet continue", replyToId = root });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
        var second = await _runner.NextSpecAsync(Wait);

        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(second));
        Assert.Equal(ExchangeWorktrees.PathFor(dir, root), second.WorkingDirectory);
        Assert.Contains("Turn 2 of 4;", second.StandardInput);
    }

    /// <summary>Row 36: like <see cref="DelayingMergeRunner"/>, but holds `git worktree remove`. The close
    /// has committed and the exchange's worktree is still registered and on disk, which is exactly the window
    /// a reopened exchange must not lease into (EnsureAsync would hand back the path without taking the gate).</summary>
    private sealed class DelayingRemoveRunner : IProcessRunner
    {
        private readonly IProcessRunner _inner = new ProcessRunner();
        public readonly TaskCompletionSource Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource RemoveAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation)
        {
            if (spec.Arguments.Contains("worktree") && spec.Arguments.Contains("remove"))
            {
                RemoveAttempted.TrySetResult();
                await Hold.Task;
            }
            return await _inner.RunAsync(spec, timeout, cancellation);
        }
    }

    [Fact]
    public async Task R36_a_reopened_exchange_waits_for_a_close_still_running_in_its_room()
    {
        var delayingRunner = new DelayingRemoveRunner();
        var localDir = _dir + "_reply_close_wait";
        await using var host = await HubTestHost.StartAsync(localDir, processRunner: _runner, limits: Fast,
            roomGit: dir => new GitTrail(dir, runner: delayingRunner));
        host.AuthorizeAs(ChopDb.OwnerParticipantId);
        var spawner = host.Services.GetRequiredService<SpawnerService>();
        var dir = Path.Combine(host.RoomsRoot, "lab-reply-wait");
        Assert.True(await new GitTrail(dir).InitAsync());
        host.Services.GetRequiredService<MessageStore>().CreateRoom("lab-reply-wait", "Lab", dir);
        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"type":"result","result":"done"}"""));

        var post = await host.Client.PostAsJsonAsync("api/rooms/lab-reply-wait/messages", new { body = "@opus task A" });
        Assert.Equal(System.Net.HttpStatusCode.Created, post.StatusCode);
        await _runner.NextSpecAsync(Wait);
        await delayingRunner.RemoveAttempted.Task.WaitAsync(Wait);                                   // the close holds the worktree mid-removal
        var root = spawner.Snapshot("lab-reply-wait").RootMessageId!.Value;

        var reply = await host.Client.PostAsJsonAsync("api/rooms/lab-reply-wait/messages", new { body = "@sonnet continue", replyToId = root });
        Assert.Equal(System.Net.HttpStatusCode.Created, reply.StatusCode);
        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromSeconds(1)));                            // without the wait it would lease the dying worktree

        delayingRunner.Hold.TrySetResult();
        var second = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(second));
        Assert.Equal(ExchangeWorktrees.PathFor(dir, root), second.WorkingDirectory);
        using var doc = JsonDocument.Parse(await host.Client.GetStringAsync("api/rooms/lab-reply-wait/messages?afterId=0&limit=200"));
        var closeNote = doc.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.GetProperty("body").GetString()!)
            .First(b => b.StartsWith($"Exchange #{root} merged into"));
        Assert.DoesNotContain("was not", closeNote);
    }
```
A third Rooms test, for a kept branch:
```csharp
    [Fact]
    public async Task R36_a_reply_to_a_stopped_worktree_exchange_continues_its_kept_branch()
    {
        var dir = await MakeRoom("lab");
        _runner.Handler = async (spec, _, ct) =>
        {
            File.WriteAllText(Path.Combine(spec.WorkingDirectory, "half.txt"), "half done\n");
            return await FakeProcessRunner.HangUntilKilled(TimeSpan.FromSeconds(30), ct);
        };
        await PostAsOwnerIn("lab", "@opus start");
        await _runner.NextSpecAsync(Wait);
        var root = Spawner.Snapshot("lab").RootMessageId!.Value;
        var (outcome, _) = await Spawner.StopExchangeAsync("lab", root);
        Assert.Equal(ExchangeStopOutcome.Stopped, outcome);
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} was not merged"));

        _runner.Handler = (_, _, _) => Task.FromResult(FakeProcessRunner.Ok("""{"type":"result","result":"done"}"""));
        var r = await _host.Client.PostAsJsonAsync("api/rooms/lab/messages", new { body = "@opus finish it", replyToId = root });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
        var spec = await _runner.NextSpecAsync(Wait);

        Assert.Equal(ExchangeWorktrees.PathFor(dir, root), spec.WorkingDirectory);
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith($"Exchange #{root} merged into"));
        Assert.True(File.Exists(Path.Combine(dir, "half.txt")));                                  // only the kept branch had it
        Assert.DoesNotContain(await MessagesIn("lab"), m => m.Body.StartsWith("@opus was not started"));
    }
```
A fourth Rooms test, for a branch the exchange never leased:
```csharp
    [Fact]
    public async Task R36_a_reply_never_adopts_a_branch_its_exchange_did_not_lease()
    {
        var dir = await MakeRoom("lab");
        Assert.True((await new GitTrail(dir).CommitAllAsync("seed", GitTrail.Hub, allowEmpty: true)).Created);
        var probe = await _host.Client.PostAsJsonAsync("api/rooms/lab/messages", new { body = "no mention, no exchange" });
        var root = JsonDocument.Parse(await probe.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64() + 1;
        var made = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, ["branch", ExchangeWorktrees.Branch(root)], new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(0, made.ExitCode);
        var foreignHead = await GitLog(dir, "%H", 1, gitRef: ExchangeWorktrees.Branch(root));

        await PostAsOwnerIn("lab", "@opus go");                                                      // takes id root; its first lease is refused
        await WaitForMessageIn("lab", m => m.Author == "hub" && m.Body.StartsWith("Exchange concluded"));

        var r = await _host.Client.PostAsJsonAsync("api/rooms/lab/messages", new { body = "@opus try again", replyToId = root });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);

        Assert.True(await _runner.NoSpecWithin(TimeSpan.FromSeconds(1)));
        var deadline = DateTime.UtcNow + Wait;
        while ((await MessagesIn("lab")).Count(m => m.Body.StartsWith("@opus was not started:")) < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "the reopened lease was not refused");
            await Task.Delay(100);
        }
        Assert.Equal(foreignHead, await GitLog(dir, "%H", 1, gitRef: ExchangeWorktrees.Branch(root)));
    }
```
And in `ExchangeWorktreesTests` (its seams: `RoomWithCommit`, `_worktrees`, `_trails`, `Owner`, `Close(...)`):
```csharp
    [Fact]
    public async Task R36_Ensure_continues_an_existing_branch_only_when_asked()
    {
        var dir = await RoomWithCommit("lab36");
        var first = await _worktrees.EnsureAsync(dir, 11, CancellationToken.None);
        File.WriteAllText(Path.Combine(first.Path!, "kept.txt"), "kept");
        await _trails.ForWorktree(dir, first.Path!).CommitAllAsync("agent work", Owner, allowEmpty: false);
        Assert.StartsWith("Exchange #11 was not merged", await _worktrees.CloseAsync(Close(dir, 11, "lab36", ExchangeStatus.Stopped), CancellationToken.None));
        Assert.True(await _trails.For(dir).BranchExistsAsync(ExchangeWorktrees.Branch(11)));

        Assert.Contains("already exists", (await _worktrees.EnsureAsync(dir, 11, CancellationToken.None)).Refusal);

        var again = await _worktrees.EnsureAsync(dir, 11, CancellationToken.None, continueBranch: true);
        Assert.Null(again.Refusal);
        Assert.Equal(ExchangeWorktrees.PathFor(dir, 11), again.Path);
        Assert.True(File.Exists(Path.Combine(again.Path!, "kept.txt")));
    }
```
Open the existing row 35 test at `SpawnerServiceTests.Rooms.cs` that uses `DelayingMergeRunner` with a second host and copy its exact setup (usings, `RoomsRoot`, how the room is created) if anything above differs. Run the filter; record RED (the reply opens a fresh exchange, so `Turn 2 of 4` and the root assertions fail; the wait test fails on the missing join).

**4b. GREEN** in `SpawnerService.cs`:
1. Fields beside `_rooms`:
```csharp
    // Row 36: the last JoinableKept exchanges an owner prompt opened per room, oldest first, whatever
    // their status, so an owner reply finds its exchange after AddExchange pruned it from _rooms. Run
    // exchanges are never here: a reply never reopens a conductor's or its workers' exchange.
    private readonly Dictionary<string, List<Exchange>> _joinable = new(StringComparer.Ordinal);
    internal const int JoinableKept = 50;
```
2. In `OnMessage`, inside the `_inFlight.TryGetValue(...)` block after `handle.Posted = true;`: `handle.Exchange.MessageIds.Add(m.Id);`.
3. In `OnMessage`, replace the three lines from `var (opened, notes) = _policy.OnRoomMessage(` through `if (opened is not null || hadExchanges) Publish(m.RoomId);` with:
```csharp
        var joins = m.ReplyToId is { } replyTo ? JoinableFor(m.RoomId, replyTo) : null;
        var joinedClosed = joins is not null && joins.Status != ExchangeStatus.Open;
        var (opened, notes) = _policy.OnRoomMessage(exchanges, target, m, now, acceptMentions, skill, run, startsRun, hasDirectory, joins);
        if (opened is not null)
        {
            AddExchange(m.RoomId, opened);
            if (!startsRun) Remember(m.RoomId, opened);
        }
        var reopened = joinedClosed && joins!.Status == ExchangeStatus.Open;
        if (reopened) Reopen(m.RoomId, joins!);
        foreach (var note in notes) PostNote(m.RoomId, note);
        if (opened is not null || hadExchanges || reopened) Publish(m.RoomId);
```
4. Helpers beside `AppBackedTarget`:
```csharp
    /// <summary>Row 36: the exchange of <paramref name="roomId"/> that holds <paramref name="messageId"/>:
    /// the room's own list first (so an exchange still held there is found even after it left the
    /// remembered window), then the remembered ones; null when none does (a hub note, one older than the
    /// last <see cref="JoinableKept"/>, one a previous hub process held). A run's exchange can be returned;
    /// the policy refuses to join it (<see cref="Exchange.Joinable"/>).</summary>
    private Exchange? JoinableFor(string roomId, long messageId) =>
        ExchangesIn(roomId).LastOrDefault(x => x.MessageIds.Contains(messageId))
        ?? (_joinable.TryGetValue(roomId, out var list) ? list.LastOrDefault(x => x.MessageIds.Contains(messageId)) : null);

    private void Remember(string roomId, Exchange x)
    {
        if (!_joinable.TryGetValue(roomId, out var list)) _joinable[roomId] = list = new List<Exchange>();
        list.Add(x);
        if (list.Count > JoinableKept) list.RemoveAt(0);
    }

    /// <summary>Row 36: a closed exchange an owner reply just reopened becomes the room's newest entry
    /// again (AddExchange may have pruned it). Its interrupted mark is dropped: the owner chose to continue.
    /// If its worktree was already handed to a close, that bookkeeping starts over: the next launch leases
    /// <c>chopitup/x&lt;root&gt;</c> again, continuing a kept branch only when this exchange really leased it
    /// (a refused first lease never adopts someone else's branch), and while a close is still running in
    /// the room (it may be this exchange's own) the launch waits for it.</summary>
    private void Reopen(string roomId, Exchange x)
    {
        if (!_rooms.TryGetValue(roomId, out var list)) _rooms[roomId] = list = new List<Exchange>();
        list.Remove(x);
        list.Add(x);
        x.Interrupted = false;                         // the owner chose to continue from whatever state it left
        if (_worktreeExchanges.Contains(x)) return;    // never handed to a close: its worktree is still its own
        x.ContinuesBranch = x.ContinuesBranch || x.WorktreeLeased;   // only a branch this exchange really leased
        x.WorktreeRoom = null;
        x.WorktreeLeased = false;
        x.WaitsForClose = _closingRooms.Contains(roomId);
    }
```
5. `Exchange.cs`, after `Interrupted`:
```csharp
    /// <summary>Row 36: reopened while a worktree close was running in its room, which may be its own;
    /// it launches nothing until that close has finished.</summary>
    public bool WaitsForClose { get; set; }

    /// <summary>Row 36: reopened after its worktree was handed to a close, so its next lease may continue
    /// the branch that close kept instead of refusing it.</summary>
    public bool ContinuesBranch { get; set; }
```
6. `Handle`'s `WorktreeClosedEvent` case: after `_closingRooms.Remove(w.RoomId);` add `foreach (var x in ExchangesIn(w.RoomId)) x.WaitsForClose = false;`.
7. `LaunchDue` and `ArmWake` each contain the line `if (exclusive && over is null && _closingRooms.Contains(x.RoomId)) continue;` (claim 10; in `ArmWake` it sits just before `var wake = _policy.NextWake(`). Directly after it, in both methods, add `if (x.WaitsForClose) continue;`. The `WorktreeClosedEvent` wakes the loop, so the exchange needs no timer of its own.

8. `Launch`: on the line `var turn = request.TurnNumber; var budget = x.Budget; var roomId = request.RoomId; var host = participant.Host;` append ` var continueBranch = x.ContinuesBranch;` (read on the loop thread, before `Task.Run`), and change the lease call to `var lease = await _worktrees.EnsureAsync(directory, request.RootMessageId, CancellationToken.None, continueBranch);`.
9. `ExchangeWorktrees.EnsureAsync` signature becomes `EnsureAsync(string roomDirectory, long root, CancellationToken cancellation, bool continueBranch = false)`; its refusal line becomes `if (exists && !pruned && !continueBranch) return new(null, $"branch {branch} already exists");`; its summary gains: "<paramref name="continueBranch"/> (row 36) is set only for an exchange a reply reopened: an existing <c>chopitup/x&lt;root&gt;</c> is then that exchange's own kept work, and the worktree is added onto it."

10. The `ExchangeSnapshot` summary's "lists every exchange the room still holds, oldest first" becomes "lists every exchange the room still holds, in the order they were opened; a reply that reopens one moves it to the end". The `Displayed` summary gains: "A reopened exchange counts as the newest."

Do not change `AddExchange`, `OnStop`, `OnStopOne` or any run path. Commit: `Row 36 task 4: the spawner joins a reply to its exchange`.

### Task 5: Reply in the web UI, and the README (opus)

Blocked by Task 2. Files: `src/ChopItUp.Hub/client/src/types.ts`, `api.ts`, `api.test.ts`, `Composer.tsx`, `Thread.tsx`, `App.tsx`, `ExchangeBar.tsx` (comment only), `styles.css`, new `src/ChopItUp.Hub/client/src/reply.ts` and `src/ChopItUp.Hub/client/src/Reply.test.tsx`, and `README.md`.

Behaviour (the tests below pin it; styling follows the existing thread and composer tokens):
- `types.ts`: `Message` gains `replyToId?: number | null;`.
- `api.ts`: `postMessage(roomId, body, replyToId?: number | null, signal?)` sends `JSON.stringify(replyToId == null ? { body } : { body, replyToId })`. Update the one caller in `App.tsx` and any test passing `signal` positionally.
- `Thread.tsx`: `Props` gains `onReply?: (message: Message) => void`. Every non-system row gets a `button.reply-action` (`type="button"`, `aria-label="Reply to <display name>"`, text `Reply`) in `row-main` that calls `onReply(message)`; it is visually quiet, revealed on row hover and `:focus-within`, and always visible under `@media (hover: none)`. A system row gets none. Each `article` gets `id={`msg-${message.id}`}`. A message with `replyToId` renders, above its body, a `button.reply-quote` whose text is `↪ <author display name>: <first 80 chars of the original body, whitespace collapsed>` when the original is in `messages`, and `↪ #<id>` otherwise; clicking scrolls `#msg-<id>` into view (`block: 'center'`) and toggles a `flash` class on it for 1.2 s. `MessageRow` stays memoised: pass the resolved original (or undefined) as a prop computed in the parent map, never the whole list.
- `Composer.tsx`: `Props` gains `replyTo: Message | null` and `onCancelReply: () => void`; `onSend` becomes `(body: string, replyToId: number | null) => Promise<void>`. With `replyTo` set, a `div.reply-chip` renders above the field: `Replying to <display name>` plus a snippet and a `button` with `aria-label="Cancel reply"` calling `onCancelReply`. Escape with the skill menu closed and a reply set calls `onCancelReply`. `send()` passes `replyTo?.id ?? null`. The textarea is focused when `replyTo` changes to non-null.
- `reply.ts` holds the chip's transitions as a pure reducer, so they test without a DOM:
```ts
import type { Message } from './types';

/** Every event that changes what the composer is replying to. */
export type ReplyEvent =
  | { kind: 'reply'; message: Message }
  | { kind: 'cancel' }
  | { kind: 'sent' }
  | { kind: 'failed' }
  | { kind: 'roomChanged' };

/** A failed send keeps the target so the owner can retry; everything else but Reply clears it. */
export function nextReply(current: Message | null, event: ReplyEvent): Message | null {
  switch (event.kind) {
    case 'reply':
      return event.message;
    case 'failed':
      return current;
    case 'cancel':
    case 'sent':
    case 'roomChanged':
      return null;
  }
}
```
- `App.tsx`: `const [replyTo, dispatchReply] = useReducer(nextReply, null);`. An effect on `roomId` dispatches `roomChanged`. `send(body, replyToId)` dispatches `sent` after `api.postMessage` resolves and `failed` in its catch before rethrowing. `Thread` gets `onReply={(message) => dispatchReply({ kind: 'reply', message })}`; `Composer` gets `replyTo` and `onCancelReply={() => dispatchReply({ kind: 'cancel' })}`.
- `ExchangeBar.tsx`'s header comment "one strip per entry, oldest first" becomes "one strip per entry, in the hub's order (a reopened exchange last)". No rendering change.
- Export the pure helpers from `Thread.tsx` so they test without a DOM: `export function replySnippet(body: string, max = 80): string` (collapse whitespace runs to one space, trim, cut to `max` chars with a trailing `…` when cut).

`Reply.test.tsx` (vitest, `renderToStaticMarkup`, `vi.mock('./markdown', ...)` as `App.test.tsx` does):
```tsx
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test, vi } from 'vitest';
import Composer from './Composer';
import { setRoster } from './participants';
import { nextReply } from './reply';
import Thread, { replySnippet } from './Thread';
import type { Message } from './types';

vi.mock('./markdown', () => ({ renderBody: (body: string) => `<p>${body}</p>` }));

// isSystem() reads the roster: an id it has never heard of is deliberately not a hub note.
setRoster([
  { id: 'owner', displayName: 'Owner', kind: 'human', host: 'human', model: null },
  { id: 'opus', displayName: 'Opus', kind: 'model', host: 'claude', model: 'opus' },
  { id: 'hub', displayName: 'Hub', kind: 'system', host: 'hub', model: null },
]);

const at = '2026-09-15T12:00:00.000Z';
const root: Message = { id: 1, roomId: 'general', authorId: 'opus', body: 'here is the\n\nplan', createdAt: at };
const reply: Message = { id: 2, roomId: 'general', authorId: 'owner', body: '@opus go on', createdAt: at, replyToId: 1 };
const note: Message = { id: 3, roomId: 'general', authorId: 'hub', body: 'Exchange concluded: 1 of 4 turns used.', createdAt: at };

describe('reply-to', () => {
  test('every non-hub message offers Reply and a hub note does not', () => {
    const markup = renderToStaticMarkup(<Thread messages={[root, reply, note]} loading={false} onReply={() => undefined} />);
    expect(markup.match(/class="reply-action"/g)?.length).toBe(2);
  });

  test('a reply shows a quote line naming the original author with a snippet', () => {
    const markup = renderToStaticMarkup(<Thread messages={[root, reply]} loading={false} onReply={() => undefined} />);
    expect(markup).toContain('reply-quote');
    expect(markup).toContain('here is the plan');
    expect(markup).toContain('id="msg-1"');
  });

  test('a reply whose original is not loaded names its id', () => {
    const markup = renderToStaticMarkup(<Thread messages={[reply]} loading={false} onReply={() => undefined} />);
    expect(markup).toContain('#1');
  });

  test('the composer shows a cancellable chip while replying and none otherwise', () => {
    const replying = renderToStaticMarkup(
      <Composer roomName="General" disabled={false} onSend={async () => undefined} replyTo={root} onCancelReply={() => undefined} />,
    );
    expect(replying).toContain('reply-chip');
    expect(replying).toContain('aria-label="Cancel reply"');
    const idle = renderToStaticMarkup(
      <Composer roomName="General" disabled={false} onSend={async () => undefined} replyTo={null} onCancelReply={() => undefined} />,
    );
    expect(idle).not.toContain('reply-chip');
  });

  test('a snippet collapses whitespace and cuts long bodies', () => {
    expect(replySnippet('a\n\n  b')).toBe('a b');
    expect(replySnippet('x'.repeat(100), 80)).toBe('x'.repeat(80) + '…');
  });

  test('Reply names the author it replies to', () => {
    const markup = renderToStaticMarkup(<Thread messages={[root]} loading={false} onReply={() => undefined} />);
    expect(markup).toContain('aria-label="Reply to Opus"');
  });
});

describe('nextReply', () => {
  test('Reply sets the target', () => expect(nextReply(null, { kind: 'reply', message: root })).toBe(root));
  test('a successful send clears it', () => expect(nextReply(root, { kind: 'sent' })).toBeNull());
  test('a failed send keeps it', () => expect(nextReply(root, { kind: 'failed' })).toBe(root));
  test('a room change clears it', () => expect(nextReply(root, { kind: 'roomChanged' })).toBeNull());
  test('Cancel clears it', () => expect(nextReply(root, { kind: 'cancel' })).toBeNull());
});
```
If `setRoster`'s parameter type needs fields the literal above lacks, add them from `types.ts` rather than casting. In `api.test.ts`, following its existing fetch stub: `postMessage('general', 'hi', 7)` sends body `{"body":"hi","replyToId":7}` and `postMessage('general', 'hi')` sends `{"body":"hi"}`.

`README.md`, after the paragraph that ends "`GET /api/rooms/<room>/exchange`)." add:
> Reply to a message in the web UI (the Reply button on any message that is not a hub note) and your post joins that message's exchange instead of starting a new one. Its mentions spend the exchange's remaining turns, the skill it started with stays in force, and a concluded or stopped exchange opens again. A reply to a hub note, to a run's messages, or to an exchange from before a hub restart is handled as a new prompt, and the hub says so. The hub remembers the last 50 exchanges per room for this.

Keep the README text free of process vocabulary (no row numbers, rulings or plan ids). Commit: `Row 36 task 5: Reply in the web UI`.

## Verification (orchestrator, after Task 5)
1. Full suite + client tests green. Expected: Core 224 + 5 = 229; Hub 769 + 5 (Task 2) + 15 (Task 3) + 9 (Task 4) = 798; client 99 + 11 (`Reply.test.tsx`) + 2 (`api.test.ts`) = 112. A different count is explained before merge.
2. Mutation pass (M24), one revert at a time on a scratch worktree, each must fail a named test: (a) `Join` without the reopen; (b) `Join` without the `InFlight` refusal; (c) `Join` without the restore when nothing was accepted; (d) the service without `Remember`; (e) the service without `handle.Exchange.MessageIds.Add`; (f) `Reopen` without the list re-add; (g) `LaunchDue` without `WaitsForClose`; (h) `IsInRoom` check removed; (i) `ApplyV11` probe removed (torn test); (j) `HubHost` broadcast without `ReplyToId`; (k) `nextReply` `failed` returning null; (l) `EnsureAsync` ignoring `continueBranch`; (m) the policy's `Joinable: true` check removed; (n) `Join` without resetting `TurnsCommitted`; (o) `Reopen` setting `ContinuesBranch = true` unconditionally. Known unbound: `JoinableFor` searching `_rooms` before `_joinable` (reaching it needs 51 exchanges in one room; accepted, pass 1 finding 4).
3. Branch-level `mattpocock-skills:code-review` (Standards + Spec, no agents).
4. Synthetic dry run (HIGH): `tools/Invoke-M25DryRun.ps1` on the built hub (v9 fixture through v10 and v11, fabricated data). Then a scratch hub with the Row 34 stub `codex.cmd` on PATH: post `@gpt-6-astra` (id R), wait for its conclusion note, post a reply to R mentioning `@gpt-5.5` from a script with the scratch token, and assert `GET /api/rooms/general/exchange` lists R with `turnsUsed` 2 and no second exchange.
5. UI gate (Row 34/M23 recipe): Reply button `elementFromPoint` hit, `.click()` shows the chip, the scripted reply renders a quote line over SignalR, and a headless Edge capture judged by a pinned opus subagent.
6. Deploy with `tools\Deploy-ChopItUp.ps1`, then `tools\Invoke-M4SelfCheck.ps1`; live `/health` reports schema 11. Rollback, if the new build misbehaves: a v10 exe refuses a v11 store, so restoring the exe alone leaves the hub down, and restoring the `.v10` backup loses every message since. The cheap path is: stop the hub by verified PID, set `PRAGMA user_version = 10` on the store (the extra nullable column is ignored, no SQL in `src` or `tools` uses `SELECT *`), then `tools\Deploy-ChopItUp.ps1 -RestoreFrom <backup dir>`. The owner runs that, not an agent (no agent writes the deployed data directory). It is written into the ping, not rehearsed.

## Critique dispositions
Pass 1 (fable, 6.9, FIX-THEN-SHIP):
1. MAJOR, kept branch dead-ends a reply. Fixed: B8 and AC6 reworded; `EnsureAsync(continueBranch)` plus `Exchange.ContinuesBranch` set by `Reopen`; a worktree unit test and a Rooms stop-then-reply test (Task 4).
2. MAJOR, `isSystem` needs the roster in the UI test. Fixed: `Reply.test.tsx` calls `setRoster` with a system `hub` row.
3. MAJOR, AC7 chip lifecycle and AC2 broadcast untested. Fixed: pure `nextReply` reducer in `reply.ts` with five transition tests; a `RealtimeTests` reply-broadcast test; both in the mutation list.
4. MINOR, `_joinable` eviction of an exchange still held. Fixed in code (`JoinableFor` searches `_rooms` first). No test: reaching it needs 51 exchanges in one room; named as known unbound in Verification.
5. MINOR, refused-skill reply arm. Fixed by ruling B6: handled exactly as the same post without reply-to (overlap supersede applies); policy test added.
6. MINOR, `SpawnPromptTests` snippet used a missing `T`. Fixed: uses `Msg(...) with { ReplyToId = 1 }`, `Input(...)` and `SpawnLimits.Default`.
7. MINOR, sweep count and recheck pattern. Fixed: 15 lines in 14 files; recheck covers check names, `stamped-v10` and comments.
8. MINOR, no expected totals. Fixed: Verification 1 states the totals (229 / 798 / 112 after pass 2).
9. MINOR, B2 gives no signal when the in-flight spawn ends. Declined for this row (the row 34 exchange bar shows it); named in the ping.
Found while folding: a reply could reopen a run's conductor exchange after its run ended (Budget 1, run skill, no run) once `JoinableFor` searched `_rooms`. Fixed: `Exchange.Joinable` set only for non-run-start owner prompts, checked by the policy; two policy tests.

Pass 2 (opus, 7.0, FIX-THEN-SHIP):
1. MAJOR, a reopen after a stop or supersede counted dropped turns as spent. Fixed: `Join` resets `TurnsCommitted` to `TurnsStarted` before `Accept` (restored if nothing was accepted); policy test with three mentions, one run, stop, reply.
2. MAJOR, `ContinuesBranch` was set for any handed-off exchange, so a refused first lease could adopt a foreign branch. Fixed: `ContinuesBranch ||= WorktreeLeased`; Rooms test pre-creates the branch and asserts a second refusal and an unchanged branch head.
3. MAJOR, the lease-again test waited for "had nothing new to merge", which never posts (every worktree spawn makes an agent commit). Fixed: waits for "merged into".
4. MAJOR, mutations (g) and (e) could not fail. Fixed: the close-wait test stalls `git worktree remove` (ungated `EnsureAsync` would lease the dying worktree) and asserts a clean close note; the spawn-membership test posts after `StopExchangeAsync`, where only the service records membership.
5. MINOR, a stale `Interrupted` on an exchange still queued for close. Fixed: `Reopen` clears it in both paths.
6. MINOR, the kept-branch test raced the close. Fixed: waits for the merge note and checks `half.txt` in the room directory.
7. MINOR, "oldest first" no longer true. Fixed: `ExchangeSnapshot`/`Displayed` summaries and the `ExchangeBar` comment reworded; order unchanged.
8. MINOR, no rollback path. Fixed: Verification 6 documents it (owner-run, not rehearsed) and Could-not-verify names it.
9. MINOR, M25 `$after` is an ordered dictionary. Fixed: wording names `Get-Counts`.
