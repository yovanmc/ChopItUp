using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Tests.Storage;

/// <summary>Schema-evolution guard: a database written in the OLD on-disk shape, read by the NEW
/// code, with every meaning asserted unchanged. The fixture is raw SQL on purpose — it must keep
/// describing v1 even after ChopDb stops being able to produce a v1.</summary>
public sealed class SchemaMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_mig_" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "chopitup.db");

    private void WriteRawV1()
    {
        Directory.CreateDirectory(_dir);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE participants (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, kind TEXT NOT NULL);
            CREATE TABLE rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
                author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE INDEX ix_messages_room_id ON messages(room_id, id);
            CREATE TABLE read_cursors (participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id TEXT NOT NULL REFERENCES rooms(id), last_read_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id));
            INSERT INTO participants (id, display_name, kind) VALUES ('owner','Owner','human'),('claude','Claude','model'),('codex','Codex','model');
            INSERT INTO rooms (id, name, created_at) VALUES ('general','General','2026-09-01T10:00:00.000+00:00');
            INSERT INTO messages (id, room_id, author_id, body, created_at) VALUES
                (1,'general','owner','first v1 message','2026-09-01T10:01:00.000+00:00'),
                (2,'general','claude','second v1 message','2026-09-01T10:02:00.000+00:00');
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ('codex','general',1);
            PRAGMA user_version = 1;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private void WriteRawV2()
    {
        // v1 shape plus exactly what ApplyV2 adds. Raw SQL on purpose: this must keep describing v2
        // after ChopDb can no longer produce one.
        Directory.CreateDirectory(_dir);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE participants (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, kind TEXT NOT NULL);
            CREATE TABLE rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
                author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL,
                client_key TEXT);
            CREATE INDEX ix_messages_room_id ON messages(room_id, id);
            CREATE UNIQUE INDEX ux_messages_client_key ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;
            CREATE TABLE read_cursors (participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id TEXT NOT NULL REFERENCES rooms(id), last_read_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id));
            INSERT INTO participants (id, display_name, kind) VALUES ('owner','Owner','human'),('claude','Claude','model'),('codex','Codex','model');
            INSERT INTO rooms (id, name, created_at) VALUES ('general','General','2026-09-01T10:00:00.000+00:00');
            INSERT INTO messages (id, room_id, author_id, body, created_at, client_key) VALUES
                (1,'general','owner','first v2 message','2026-09-01T10:01:00.000+00:00',NULL),
                (2,'general','codex','second v2 message','2026-09-01T10:02:00.000+00:00','k-1');
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ('claude','general',2);
            PRAGMA user_version = 2;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private void WriteRawV3()
    {
        // v2 shape plus exactly what ApplyV3 adds. Raw SQL on purpose: this must keep describing v3
        // after ChopDb can no longer produce one.
        Directory.CreateDirectory(_dir);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE participants (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, kind TEXT NOT NULL, host TEXT, model TEXT, note TEXT);
            CREATE TABLE rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
                author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL,
                client_key TEXT);
            CREATE INDEX ix_messages_room_id ON messages(room_id, id);
            CREATE UNIQUE INDEX ux_messages_client_key ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;
            CREATE TABLE read_cursors (participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id TEXT NOT NULL REFERENCES rooms(id), last_read_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id));
            INSERT INTO participants (id, display_name, kind, host, model, note) VALUES
                ('owner','Owner','human','human',NULL,NULL),
                ('claude','Claude','model','claude',NULL,'App-backed: Claude Desktop or Claude Code, whatever model the app has selected.'),
                ('codex','Codex','model','codex',NULL,'App-backed: the Codex app or CLI, whatever model the app has selected.'),
                ('opus','Opus','model','claude','opus',NULL),
                ('sonnet','Sonnet','model','claude','sonnet',NULL),
                ('fable','Fable','model','claude','fable','May bill to usage credits instead of the plan''s included limits.'),
                ('gpt-6-astra','GPT-6 Astra','model','codex','gpt-6-astra',NULL),
                ('gpt-5.6-sol','GPT-5.6 Sol','model','codex','gpt-5.6-sol',NULL),
                ('gpt-5.6-terra','GPT-5.6 Terra','model','codex','gpt-5.6-terra',NULL),
                ('gpt-5.6-luna','GPT-5.6 Luna','model','codex','gpt-5.6-luna',NULL),
                ('gpt-5.5','GPT-5.5','model','codex','gpt-5.5',NULL),
                ('gpt-5.4-mini','GPT-5.4 Mini','model','codex','gpt-5.4-mini',NULL);
            INSERT INTO rooms (id, name, created_at) VALUES ('general','General','2026-09-01T10:00:00.000+00:00');
            INSERT INTO messages (id, room_id, author_id, body, created_at, client_key) VALUES
                (1,'general','owner','@opus first v3 message','2026-09-01T10:01:00.000+00:00',NULL),
                (2,'general','opus','second v3 message','2026-09-01T10:02:00.000+00:00','k-1');
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ('opus','general',2);
            PRAGMA user_version = 3;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private void WriteRawV4()
    {
        // v3 shape plus exactly what ApplyV4 adds: the hub row. Raw SQL on purpose: this must keep
        // describing v4 after ChopDb can no longer produce one.
        Directory.CreateDirectory(_dir);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE participants (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, kind TEXT NOT NULL, host TEXT, model TEXT, note TEXT);
            CREATE TABLE rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
                author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL,
                client_key TEXT);
            CREATE INDEX ix_messages_room_id ON messages(room_id, id);
            CREATE UNIQUE INDEX ux_messages_client_key ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;
            CREATE TABLE read_cursors (participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id TEXT NOT NULL REFERENCES rooms(id), last_read_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id));
            INSERT INTO participants (id, display_name, kind, host, model, note) VALUES
                ('owner','Owner','human','human',NULL,NULL),
                ('claude','Claude','model','claude',NULL,'App-backed: Claude Desktop or Claude Code, whatever model the app has selected.'),
                ('codex','Codex','model','codex',NULL,'App-backed: the Codex app or CLI, whatever model the app has selected.'),
                ('opus','Opus','model','claude','opus',NULL),
                ('sonnet','Sonnet','model','claude','sonnet',NULL),
                ('fable','Fable','model','claude','fable','May bill to usage credits instead of the plan''s included limits.'),
                ('gpt-6-astra','GPT-6 Astra','model','codex','gpt-6-astra',NULL),
                ('gpt-5.6-sol','GPT-5.6 Sol','model','codex','gpt-5.6-sol',NULL),
                ('gpt-5.6-terra','GPT-5.6 Terra','model','codex','gpt-5.6-terra',NULL),
                ('gpt-5.6-luna','GPT-5.6 Luna','model','codex','gpt-5.6-luna',NULL),
                ('gpt-5.5','GPT-5.5','model','codex','gpt-5.5',NULL),
                ('gpt-5.4-mini','GPT-5.4 Mini','model','codex','gpt-5.4-mini',NULL),
                ('hub','Hub','system','hub',NULL,'The hub itself. Posts exchange notes: timeouts, budget, conclusions. Cannot be mentioned or spawned.');
            INSERT INTO rooms (id, name, created_at) VALUES ('general','General','2026-09-01T10:00:00.000+00:00');
            INSERT INTO messages (id, room_id, author_id, body, created_at, client_key) VALUES
                (1,'general','owner','@opus first v3 message','2026-09-01T10:01:00.000+00:00',NULL),
                (2,'general','opus','second v3 message','2026-09-01T10:02:00.000+00:00','k-1');
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ('opus','general',2);
            PRAGMA user_version = 4;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private void WriteRawV5()
    {
        // v4 shape plus exactly what ApplyV5 adds: the proposals table and its index. Raw SQL on
        // purpose: this must keep describing v5 after ChopDb can no longer produce one.
        Directory.CreateDirectory(_dir);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE participants (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, kind TEXT NOT NULL, host TEXT, model TEXT, note TEXT);
            CREATE TABLE rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
                author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL,
                client_key TEXT);
            CREATE INDEX ix_messages_room_id ON messages(room_id, id);
            CREATE UNIQUE INDEX ux_messages_client_key ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;
            CREATE TABLE read_cursors (participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id TEXT NOT NULL REFERENCES rooms(id), last_read_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id));
            CREATE TABLE memory_proposals (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                room_id     TEXT NOT NULL REFERENCES rooms(id),
                author_id   TEXT NOT NULL REFERENCES participants(id),
                topic       TEXT NOT NULL,
                title       TEXT NOT NULL,
                body        TEXT NOT NULL,
                status      TEXT NOT NULL DEFAULT 'pending',
                source      TEXT,
                created_at  TEXT NOT NULL,
                decided_at  TEXT,
                written_to  TEXT,
                commit_hash TEXT
            );
            CREATE INDEX ix_memory_proposals_status ON memory_proposals(status, room_id, id);
            INSERT INTO participants (id, display_name, kind, host, model, note) VALUES
                ('owner','Owner','human','human',NULL,NULL),
                ('claude','Claude','model','claude',NULL,'App-backed: Claude Desktop or Claude Code, whatever model the app has selected.'),
                ('codex','Codex','model','codex',NULL,'App-backed: the Codex app or CLI, whatever model the app has selected.'),
                ('opus','Opus','model','claude','opus',NULL),
                ('sonnet','Sonnet','model','claude','sonnet',NULL),
                ('fable','Fable','model','claude','fable','May bill to usage credits instead of the plan''s included limits.'),
                ('gpt-6-astra','GPT-6 Astra','model','codex','gpt-6-astra',NULL),
                ('gpt-5.6-sol','GPT-5.6 Sol','model','codex','gpt-5.6-sol',NULL),
                ('gpt-5.6-terra','GPT-5.6 Terra','model','codex','gpt-5.6-terra',NULL),
                ('gpt-5.6-luna','GPT-5.6 Luna','model','codex','gpt-5.6-luna',NULL),
                ('gpt-5.5','GPT-5.5','model','codex','gpt-5.5',NULL),
                ('gpt-5.4-mini','GPT-5.4 Mini','model','codex','gpt-5.4-mini',NULL),
                ('hub','Hub','system','hub',NULL,'The hub itself. Posts exchange notes: timeouts, budget, conclusions. Cannot be mentioned or spawned.');
            INSERT INTO rooms (id, name, created_at) VALUES ('general','General','2026-09-01T10:00:00.000+00:00');
            INSERT INTO messages (id, room_id, author_id, body, created_at, client_key) VALUES
                (1,'general','owner','@opus first v3 message','2026-09-01T10:01:00.000+00:00',NULL),
                (2,'general','opus','second v3 message','2026-09-01T10:02:00.000+00:00','k-1');
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ('opus','general',2);
            INSERT INTO memory_proposals (room_id, author_id, topic, title, body, status, created_at) VALUES
                ('general','opus','user','Likes tests','Yes.','pending','2026-09-01T10:03:00.000+00:00');
            PRAGMA user_version = 5;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private void WriteRawV6()
    {
        // v5 shape plus exactly what ApplyV6 adds: the two nullable room columns. Raw SQL on purpose:
        // this must keep describing v6 after ChopDb can no longer produce one.
        Directory.CreateDirectory(_dir);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE participants (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, kind TEXT NOT NULL, host TEXT, model TEXT, note TEXT);
            CREATE TABLE rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL, directory TEXT, archived_at TEXT);
            CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
                author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL,
                client_key TEXT);
            CREATE INDEX ix_messages_room_id ON messages(room_id, id);
            CREATE UNIQUE INDEX ux_messages_client_key ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;
            CREATE TABLE read_cursors (participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id TEXT NOT NULL REFERENCES rooms(id), last_read_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id));
            CREATE TABLE memory_proposals (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                room_id     TEXT NOT NULL REFERENCES rooms(id),
                author_id   TEXT NOT NULL REFERENCES participants(id),
                topic       TEXT NOT NULL,
                title       TEXT NOT NULL,
                body        TEXT NOT NULL,
                status      TEXT NOT NULL DEFAULT 'pending',
                source      TEXT,
                created_at  TEXT NOT NULL,
                decided_at  TEXT,
                written_to  TEXT,
                commit_hash TEXT
            );
            CREATE INDEX ix_memory_proposals_status ON memory_proposals(status, room_id, id);
            INSERT INTO participants (id, display_name, kind, host, model, note) VALUES
                ('owner','Owner','human','human',NULL,NULL),
                ('claude','Claude','model','claude',NULL,'App-backed: Claude Desktop or Claude Code, whatever model the app has selected.'),
                ('codex','Codex','model','codex',NULL,'App-backed: the Codex app or CLI, whatever model the app has selected.'),
                ('opus','Opus','model','claude','opus',NULL),
                ('sonnet','Sonnet','model','claude','sonnet',NULL),
                ('fable','Fable','model','claude','fable','May bill to usage credits instead of the plan''s included limits.'),
                ('gpt-6-astra','GPT-6 Astra','model','codex','gpt-6-astra',NULL),
                ('gpt-5.6-sol','GPT-5.6 Sol','model','codex','gpt-5.6-sol',NULL),
                ('gpt-5.6-terra','GPT-5.6 Terra','model','codex','gpt-5.6-terra',NULL),
                ('gpt-5.6-luna','GPT-5.6 Luna','model','codex','gpt-5.6-luna',NULL),
                ('gpt-5.5','GPT-5.5','model','codex','gpt-5.5',NULL),
                ('gpt-5.4-mini','GPT-5.4 Mini','model','codex','gpt-5.4-mini',NULL),
                ('hub','Hub','system','hub',NULL,'The hub itself. Posts exchange notes: timeouts, budget, conclusions. Cannot be mentioned or spawned.');
            INSERT INTO rooms (id, name, created_at) VALUES ('general','General','2026-09-01T10:00:00.000+00:00');
            INSERT INTO messages (id, room_id, author_id, body, created_at, client_key) VALUES
                (1,'general','owner','@opus first v3 message','2026-09-01T10:01:00.000+00:00',NULL),
                (2,'general','opus','second v3 message','2026-09-01T10:02:00.000+00:00','k-1');
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ('opus','general',2);
            INSERT INTO memory_proposals (room_id, author_id, topic, title, body, status, created_at) VALUES
                ('general','opus','user','Likes tests','Yes.','pending','2026-09-01T10:03:00.000+00:00');
            PRAGMA user_version = 6;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private void WriteRawV7()
    {
        // v6 shape plus exactly what ApplyV7 adds: the classes column (populated the way BackfillClasses
        // leaves it), the skills table, and the hub's owner-remote row. Raw SQL on purpose: this must keep
        // describing v7 after ChopDb can no longer produce one.
        Directory.CreateDirectory(_dir);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE participants (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, kind TEXT NOT NULL, host TEXT, model TEXT, note TEXT, classes TEXT);
            CREATE TABLE rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL, directory TEXT, archived_at TEXT);
            CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
                author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL,
                client_key TEXT);
            CREATE INDEX ix_messages_room_id ON messages(room_id, id);
            CREATE UNIQUE INDEX ux_messages_client_key ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;
            CREATE TABLE read_cursors (participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id TEXT NOT NULL REFERENCES rooms(id), last_read_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id));
            CREATE TABLE memory_proposals (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                room_id     TEXT NOT NULL REFERENCES rooms(id),
                author_id   TEXT NOT NULL REFERENCES participants(id),
                topic       TEXT NOT NULL,
                title       TEXT NOT NULL,
                body        TEXT NOT NULL,
                status      TEXT NOT NULL DEFAULT 'pending',
                source      TEXT,
                created_at  TEXT NOT NULL,
                decided_at  TEXT,
                written_to  TEXT,
                commit_hash TEXT
            );
            CREATE INDEX ix_memory_proposals_status ON memory_proposals(status, room_id, id);
            CREATE TABLE skills (
                name        TEXT PRIMARY KEY,
                body_sha256 TEXT NOT NULL,
                imported_at TEXT NOT NULL,
                source      TEXT
            );
            INSERT INTO participants (id, display_name, kind, host, model, note, classes) VALUES
                ('owner','Owner','human','human',NULL,NULL,NULL),
                ('claude','Claude','model','claude',NULL,'App-backed: Claude Desktop or Claude Code, whatever model the app has selected.',NULL),
                ('codex','Codex','model','codex',NULL,'App-backed: the Codex app or CLI, whatever model the app has selected.',NULL),
                ('opus','Opus','model','claude','opus',NULL,'visible,judge'),
                ('sonnet','Sonnet','model','claude','sonnet',NULL,'plumbing'),
                ('fable','Fable','model','claude','fable','May bill to usage credits instead of the plan''s included limits.','judge'),
                ('gpt-6-astra','GPT-6 Astra','model','codex','gpt-6-astra',NULL,NULL),
                ('gpt-5.6-sol','GPT-5.6 Sol','model','codex','gpt-5.6-sol',NULL,NULL),
                ('gpt-5.6-terra','GPT-5.6 Terra','model','codex','gpt-5.6-terra',NULL,NULL),
                ('gpt-5.6-luna','GPT-5.6 Luna','model','codex','gpt-5.6-luna',NULL,NULL),
                ('gpt-5.5','GPT-5.5','model','codex','gpt-5.5',NULL,NULL),
                ('gpt-5.4-mini','GPT-5.4 Mini','model','codex','gpt-5.4-mini',NULL,NULL),
                ('hub','Hub','system','hub',NULL,'The hub itself. Posts exchange notes: timeouts, budget, conclusions. Cannot be mentioned or spawned.',NULL),
                ('owner-remote','Owner (remote)','human','human',NULL,'The owner, posting from a session on another device. Same authority as owner; the hub stamps which hand typed. Last in the roster because rowid order is seed order and this row is newer than every other.',NULL);
            INSERT INTO rooms (id, name, created_at) VALUES ('general', 'General', '2026-09-01T10:00:00.000+00:00');
            INSERT INTO messages (id, room_id, author_id, body, created_at, client_key) VALUES
                (1,'general','owner','@opus first v3 message','2026-09-01T10:01:00.000+00:00',NULL),
                (2,'general','opus','second v3 message','2026-09-01T10:02:00.000+00:00','k-1');
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ('opus','general',2);
            INSERT INTO memory_proposals (room_id, author_id, topic, title, body, status, created_at) VALUES
                ('general','opus','user','Likes tests','Yes.','pending','2026-09-01T10:03:00.000+00:00');
            PRAGMA user_version = 7;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private void WriteRawV8()
    {
        // v7 shape plus exactly what ApplyV8 adds: the run tables. Raw SQL on purpose: this must keep
        // describing v8 after ChopDb can no longer produce one.
        Directory.CreateDirectory(_dir);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE participants (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, kind TEXT NOT NULL, host TEXT, model TEXT, note TEXT, classes TEXT);
            CREATE TABLE rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL, directory TEXT, archived_at TEXT);
            CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
                author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL,
                client_key TEXT);
            CREATE INDEX ix_messages_room_id ON messages(room_id, id);
            CREATE UNIQUE INDEX ux_messages_client_key ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;
            CREATE TABLE read_cursors (participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id TEXT NOT NULL REFERENCES rooms(id), last_read_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id));
            CREATE TABLE memory_proposals (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                room_id     TEXT NOT NULL REFERENCES rooms(id),
                author_id   TEXT NOT NULL REFERENCES participants(id),
                topic       TEXT NOT NULL,
                title       TEXT NOT NULL,
                body        TEXT NOT NULL,
                status      TEXT NOT NULL DEFAULT 'pending',
                source      TEXT,
                created_at  TEXT NOT NULL,
                decided_at  TEXT,
                written_to  TEXT,
                commit_hash TEXT
            );
            CREATE INDEX ix_memory_proposals_status ON memory_proposals(status, room_id, id);
            CREATE TABLE skills (
                name        TEXT PRIMARY KEY,
                body_sha256 TEXT NOT NULL,
                imported_at TEXT NOT NULL,
                source      TEXT
            );
            CREATE TABLE runs (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                room_id         TEXT NOT NULL REFERENCES rooms(id),
                conductor_id    TEXT NOT NULL REFERENCES participants(id),
                skill_name      TEXT NOT NULL,
                arguments       TEXT NOT NULL DEFAULT '',
                status          TEXT NOT NULL,
                reason          TEXT,
                cap_spent       INTEGER NOT NULL DEFAULT 0,
                phase           TEXT NOT NULL DEFAULT '(start)',
                root_message_id INTEGER NOT NULL,
                started_at      TEXT NOT NULL,
                parked_at       TEXT,
                parked_seconds  INTEGER NOT NULL DEFAULT 0,
                ended_at        TEXT,
                spawns_used     INTEGER NOT NULL DEFAULT 0,
                exchanges       INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX ux_runs_one_active_per_room ON runs(room_id) WHERE status = 'active';
            CREATE INDEX ix_runs_room ON runs(room_id, id);
            CREATE TABLE run_phases (
                run_id  INTEGER NOT NULL REFERENCES runs(id),
                phase   TEXT NOT NULL,
                entries INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (run_id, phase)
            );
            CREATE TABLE run_artifacts (
                run_id    INTEGER NOT NULL REFERENCES runs(id),
                path      TEXT NOT NULL,
                author_id TEXT NOT NULL REFERENCES participants(id),
                at        TEXT NOT NULL,
                PRIMARY KEY (run_id, path)
            );
            CREATE TABLE run_gate_runs (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id    INTEGER REFERENCES runs(id),
                room_id   TEXT NOT NULL,
                gate      TEXT NOT NULL,
                caller_id TEXT NOT NULL,
                exit_code INTEGER,
                outcome   TEXT NOT NULL,
                at        TEXT NOT NULL
            );
            CREATE TABLE skill_files (
                skill_name TEXT NOT NULL REFERENCES skills(name),
                path       TEXT NOT NULL,
                sha256     TEXT NOT NULL,
                PRIMARY KEY (skill_name, path)
            );
            INSERT INTO participants (id, display_name, kind, host, model, note, classes) VALUES
                ('owner','Owner','human','human',NULL,NULL,NULL),
                ('claude','Claude','model','claude',NULL,'App-backed: Claude Desktop or Claude Code, whatever model the app has selected.',NULL),
                ('codex','Codex','model','codex',NULL,'App-backed: the Codex app or CLI, whatever model the app has selected.',NULL),
                ('opus','Opus','model','claude','opus',NULL,'visible,judge'),
                ('sonnet','Sonnet','model','claude','sonnet',NULL,'plumbing'),
                ('fable','Fable','model','claude','fable','May bill to usage credits instead of the plan''s included limits.','judge'),
                ('gpt-6-astra','GPT-6 Astra','model','codex','gpt-6-astra',NULL,NULL),
                ('gpt-5.6-sol','GPT-5.6 Sol','model','codex','gpt-5.6-sol',NULL,NULL),
                ('gpt-5.6-terra','GPT-5.6 Terra','model','codex','gpt-5.6-terra',NULL,NULL),
                ('gpt-5.6-luna','GPT-5.6 Luna','model','codex','gpt-5.6-luna',NULL,NULL),
                ('gpt-5.5','GPT-5.5','model','codex','gpt-5.5',NULL,NULL),
                ('gpt-5.4-mini','GPT-5.4 Mini','model','codex','gpt-5.4-mini',NULL,NULL),
                ('hub','Hub','system','hub',NULL,'The hub itself. Posts exchange notes: timeouts, budget, conclusions. Cannot be mentioned or spawned.',NULL),
                ('owner-remote','Owner (remote)','human','human',NULL,'The owner, posting from a session on another device. Same authority as owner; the hub stamps which hand typed. Last in the roster because rowid order is seed order and this row is newer than every other.',NULL);
            INSERT INTO rooms (id, name, created_at) VALUES ('general', 'General', '2026-09-01T10:00:00.000+00:00');
            INSERT INTO messages (id, room_id, author_id, body, created_at, client_key) VALUES
                (1,'general','owner','@opus first v3 message','2026-09-01T10:01:00.000+00:00',NULL),
                (2,'general','opus','second v3 message','2026-09-01T10:02:00.000+00:00','k-1');
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ('opus','general',2);
            INSERT INTO memory_proposals (room_id, author_id, topic, title, body, status, created_at) VALUES
                ('general','opus','user','Likes tests','Yes.','pending','2026-09-01T10:03:00.000+00:00');
            PRAGMA user_version = 8;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private void WriteRawV9()
    {
        // v8 shape plus exactly what ApplyV9 adds: the three memory_proposals columns, already
        // populated the way a genuinely-v9 database (not a migrated v8 one) would have them. Raw SQL
        // on purpose: this must keep describing v9 after ChopDb can no longer produce one.
        Directory.CreateDirectory(_dir);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE participants (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, kind TEXT NOT NULL, host TEXT, model TEXT, note TEXT, classes TEXT);
            CREATE TABLE rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL, directory TEXT, archived_at TEXT);
            CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
                author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL,
                client_key TEXT);
            CREATE INDEX ix_messages_room_id ON messages(room_id, id);
            CREATE UNIQUE INDEX ux_messages_client_key ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;
            CREATE TABLE read_cursors (participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id TEXT NOT NULL REFERENCES rooms(id), last_read_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id));
            CREATE TABLE memory_proposals (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                room_id     TEXT NOT NULL REFERENCES rooms(id),
                author_id   TEXT NOT NULL REFERENCES participants(id),
                topic       TEXT NOT NULL,
                title       TEXT NOT NULL,
                body        TEXT NOT NULL,
                status      TEXT NOT NULL DEFAULT 'pending',
                source      TEXT,
                created_at  TEXT NOT NULL,
                decided_at  TEXT,
                written_to  TEXT,
                commit_hash TEXT,
                kind        TEXT NOT NULL DEFAULT 'append',
                replaces    TEXT,
                flags       TEXT
            );
            CREATE INDEX ix_memory_proposals_status ON memory_proposals(status, room_id, id);
            CREATE TABLE skills (
                name        TEXT PRIMARY KEY,
                body_sha256 TEXT NOT NULL,
                imported_at TEXT NOT NULL,
                source      TEXT
            );
            CREATE TABLE runs (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                room_id         TEXT NOT NULL REFERENCES rooms(id),
                conductor_id    TEXT NOT NULL REFERENCES participants(id),
                skill_name      TEXT NOT NULL,
                arguments       TEXT NOT NULL DEFAULT '',
                status          TEXT NOT NULL,
                reason          TEXT,
                cap_spent       INTEGER NOT NULL DEFAULT 0,
                phase           TEXT NOT NULL DEFAULT '(start)',
                root_message_id INTEGER NOT NULL,
                started_at      TEXT NOT NULL,
                parked_at       TEXT,
                parked_seconds  INTEGER NOT NULL DEFAULT 0,
                ended_at        TEXT,
                spawns_used     INTEGER NOT NULL DEFAULT 0,
                exchanges       INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX ux_runs_one_active_per_room ON runs(room_id) WHERE status = 'active';
            CREATE INDEX ix_runs_room ON runs(room_id, id);
            CREATE TABLE run_phases (
                run_id  INTEGER NOT NULL REFERENCES runs(id),
                phase   TEXT NOT NULL,
                entries INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (run_id, phase)
            );
            CREATE TABLE run_artifacts (
                run_id    INTEGER NOT NULL REFERENCES runs(id),
                path      TEXT NOT NULL,
                author_id TEXT NOT NULL REFERENCES participants(id),
                at        TEXT NOT NULL,
                PRIMARY KEY (run_id, path)
            );
            CREATE TABLE run_gate_runs (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id    INTEGER REFERENCES runs(id),
                room_id   TEXT NOT NULL,
                gate      TEXT NOT NULL,
                caller_id TEXT NOT NULL,
                exit_code INTEGER,
                outcome   TEXT NOT NULL,
                at        TEXT NOT NULL
            );
            CREATE TABLE skill_files (
                skill_name TEXT NOT NULL REFERENCES skills(name),
                path       TEXT NOT NULL,
                sha256     TEXT NOT NULL,
                PRIMARY KEY (skill_name, path)
            );
            INSERT INTO participants (id, display_name, kind, host, model, note, classes) VALUES
                ('owner','Owner','human','human',NULL,NULL,NULL),
                ('claude','Claude','model','claude',NULL,'App-backed: Claude Desktop or Claude Code, whatever model the app has selected.',NULL),
                ('codex','Codex','model','codex',NULL,'App-backed: the Codex app or CLI, whatever model the app has selected.',NULL),
                ('opus','Opus','model','claude','opus',NULL,'visible,judge'),
                ('sonnet','Sonnet','model','claude','sonnet',NULL,'plumbing'),
                ('fable','Fable','model','claude','fable','May bill to usage credits instead of the plan''s included limits.','judge'),
                ('gpt-6-astra','GPT-6 Astra','model','codex','gpt-6-astra',NULL,NULL),
                ('gpt-5.6-sol','GPT-5.6 Sol','model','codex','gpt-5.6-sol',NULL,NULL),
                ('gpt-5.6-terra','GPT-5.6 Terra','model','codex','gpt-5.6-terra',NULL,NULL),
                ('gpt-5.6-luna','GPT-5.6 Luna','model','codex','gpt-5.6-luna',NULL,NULL),
                ('gpt-5.5','GPT-5.5','model','codex','gpt-5.5',NULL,NULL),
                ('gpt-5.4-mini','GPT-5.4 Mini','model','codex','gpt-5.4-mini',NULL,NULL),
                ('hub','Hub','system','hub',NULL,'The hub itself. Posts exchange notes: timeouts, budget, conclusions. Cannot be mentioned or spawned.',NULL),
                ('owner-remote','Owner (remote)','human','human',NULL,'The owner, posting from a session on another device. Same authority as owner; the hub stamps which hand typed. Last in the roster because rowid order is seed order and this row is newer than every other.',NULL);
            INSERT INTO rooms (id, name, created_at) VALUES ('general', 'General', '2026-09-01T10:00:00.000+00:00');
            INSERT INTO messages (id, room_id, author_id, body, created_at, client_key) VALUES
                (1,'general','owner','@opus first v3 message','2026-09-01T10:01:00.000+00:00',NULL),
                (2,'general','opus','second v3 message','2026-09-01T10:02:00.000+00:00','k-1');
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ('opus','general',2);
            INSERT INTO memory_proposals (room_id, author_id, topic, title, body, status, created_at, kind, replaces, flags) VALUES
                ('general','opus','user','Likes tests','Yes.','pending','2026-09-01T10:03:00.000+00:00','append',NULL,NULL);
            PRAGMA user_version = 9;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>A real-shape v10 file: v9 plus the skill_proposals table, stamped 10.</summary>
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

    /// <summary>v10 shape plus exactly what ApplyV11 adds: reply_to_id on messages, stamped 11. Raw
    /// SQL on purpose: this must keep describing v11 after ChopDb can no longer produce one.</summary>
    private void WriteRawV11()
    {
        WriteRawV10();
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            ALTER TABLE messages ADD COLUMN reply_to_id INTEGER REFERENCES messages(id);
            PRAGMA user_version = 11;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>v11 shape plus exactly what ApplyV12 adds: role, persona, room_roles, stamped 12. Raw
    /// SQL on purpose: this must keep describing v12 after ChopDb can no longer produce one.</summary>
    private void WriteRawV12()
    {
        WriteRawV11();
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            ALTER TABLE participants ADD COLUMN role TEXT;
            ALTER TABLE rooms ADD COLUMN persona TEXT;
            CREATE TABLE IF NOT EXISTS room_roles (
                room_id        TEXT NOT NULL REFERENCES rooms(id),
                participant_id TEXT NOT NULL REFERENCES participants(id),
                role           TEXT NOT NULL,
                PRIMARY KEY (room_id, participant_id)
            );
            PRAGMA user_version = 12;
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

    /// <summary>Every column of every participant row, in seed/rowid order: the migration guard's
    /// capture-before/assert-after instrument. Explicit column list rather than SELECT * so an ALTER
    /// that appends a column (role) does not shift tuple shape out from under a capture taken before
    /// that column existed.</summary>
    private static List<(string Id, string DisplayName, string Kind, string? Host, string? Model, string? Note, string? Classes)> ReadParticipantsV11Shape(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, display_name, kind, host, model, note, classes FROM participants ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        var result = new List<(string, string, string, string?, string?, string?, string?)>();
        while (r.Read())
            result.Add((r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return result;
    }

    /// <summary>Every column of every room row, in rowid order — same rationale as
    /// <see cref="ReadParticipantsV11Shape"/>, ahead of the persona column ApplyV12 appends.</summary>
    private static List<(string Id, string Name, string CreatedAt, string? Directory, string? ArchivedAt)> ReadRoomsV11Shape(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, created_at, directory, archived_at FROM rooms ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        var result = new List<(string, string, string, string?, string?)>();
        while (r.Read())
            result.Add((r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return result;
    }

    /// <summary>Every column of every message row at the v11 shape (reply_to_id already present),
    /// in id order — v12 adds no message column, so this is the plain unchanged-rows check.</summary>
    private static List<(long Id, string RoomId, string AuthorId, string Body, string CreatedAt, string? ClientKey, long? ReplyToId)> ReadMessagesV11Shape(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, room_id, author_id, body, created_at, client_key, reply_to_id FROM messages ORDER BY id";
        using var r = cmd.ExecuteReader();
        var result = new List<(long, string, string, string, string, string?, long?)>();
        while (r.Read())
            result.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6)));
        return result;
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

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
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

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        using var check = db.Open();
        Assert.Equal(1L, ReplyColumnCount(check));
    }

    [Fact]
    public void Row14_T1_a_v11_database_is_migrated_to_v12_with_role_persona_and_room_roles_and_every_existing_row_unchanged()
    {
        WriteRawV11();

        List<(string Id, string DisplayName, string Kind, string? Host, string? Model, string? Note, string? Classes)> participantsBefore;
        List<(string Id, string Name, string CreatedAt, string? Directory, string? ArchivedAt)> roomsBefore;
        List<(long Id, string RoomId, string AuthorId, string Body, string CreatedAt, string? ClientKey, long? ReplyToId)> messagesBefore;
        using (var before = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            before.Open();
            using var v = before.CreateCommand();
            v.CommandText = "PRAGMA user_version;";
            Assert.Equal(11L, (long)v.ExecuteScalar()!);   // the premise: a real v11 shape

            participantsBefore = ReadParticipantsV11Shape(before);
            roomsBefore = ReadRoomsV11Shape(before);
            messagesBefore = ReadMessagesV11Shape(before);
        }
        Assert.True(participantsBefore.Count >= 1);
        Assert.True(roomsBefore.Count >= 1);
        Assert.True(messagesBefore.Count >= 2);

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());

        using var conn = db.Open();
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('participants') WHERE name = 'role'";
            Assert.Equal(1L, (long)probe.ExecuteScalar()!);
        }
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('rooms') WHERE name = 'persona'";
            Assert.Equal(1L, (long)probe.ExecuteScalar()!);
        }
        using (var nullCheck = conn.CreateCommand())
        {
            nullCheck.CommandText = "SELECT COUNT(*) FROM participants WHERE role IS NOT NULL";
            Assert.Equal(0L, (long)nullCheck.ExecuteScalar()!);
        }
        using (var nullCheck = conn.CreateCommand())
        {
            nullCheck.CommandText = "SELECT COUNT(*) FROM rooms WHERE persona IS NOT NULL";
            Assert.Equal(0L, (long)nullCheck.ExecuteScalar()!);
        }
        using (var roomRoles = conn.CreateCommand())
        {
            roomRoles.CommandText = "SELECT COUNT(*) FROM room_roles";
            Assert.Equal(0L, (long)roomRoles.ExecuteScalar()!);
        }

        Assert.Equal(participantsBefore, ReadParticipantsV11Shape(conn));
        Assert.Equal(roomsBefore, ReadRoomsV11Shape(conn));
        Assert.Equal(messagesBefore, ReadMessagesV11Shape(conn));
    }

    [Fact]
    public void Row14_T1_a_torn_v12_with_the_columns_present_but_stamp_11_is_finished_not_crashed()
    {
        WriteRawV11();
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                ALTER TABLE participants ADD COLUMN role TEXT;
                ALTER TABLE rooms ADD COLUMN persona TEXT;
                """;
            cmd.ExecuteNonQuery();
            // stamp deliberately left at 11 — the torn state
        }
        SqliteConnection.ClearAllPools();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        using var check = db.Open();
        using (var probe = check.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('participants') WHERE name = 'role'";
            Assert.Equal(1L, (long)probe.ExecuteScalar()!);
        }
        using (var probe = check.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='room_roles'";
            Assert.Equal(1L, (long)probe.ExecuteScalar()!);
        }
    }

    [Fact]
    public void Row14_T1_room_roles_rejects_a_duplicate_room_participant_pair()
    {
        WriteRawV11();
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        using var conn = db.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO room_roles (room_id, participant_id, role) VALUES ('general', 'opus', 'first')";
            cmd.ExecuteNonQuery();
        }
        using var dup = conn.CreateCommand();
        dup.CommandText = "INSERT INTO room_roles (room_id, participant_id, role) VALUES ('general', 'opus', 'second')";
        Assert.Throws<SqliteException>(() => dup.ExecuteNonQuery());
    }

    [Fact]
    public void Row42_T1_a_v12_database_is_backed_up_then_migrated_to_v13_with_imported_zero_and_every_message_unchanged()
    {
        WriteRawV12();

        List<(long Id, string RoomId, string AuthorId, string Body, string CreatedAt, string? ClientKey, long? ReplyToId)> messagesBefore;
        using (var before = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            before.Open();
            using var v = before.CreateCommand();
            v.CommandText = "PRAGMA user_version;";
            Assert.Equal(12L, (long)v.ExecuteScalar()!);   // the premise: a real v12 shape

            messagesBefore = ReadMessagesV11Shape(before);
        }
        Assert.True(messagesBefore.Count >= 2);

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.Contains(".v12.", Path.GetFileName(db.LastBackupPath!));
        Assert.True(File.Exists(db.LastBackupPath));
        Assert.Equal(messagesBefore.Count, BackupScalar(db.LastBackupPath!, "SELECT COUNT(*) FROM messages"));

        using var conn = db.Open();
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name = 'imported'";
            Assert.Equal(1L, (long)probe.ExecuteScalar()!);
        }
        using (var nonZero = conn.CreateCommand())
        {
            nonZero.CommandText = "SELECT COUNT(*) FROM messages WHERE imported <> 0";
            Assert.Equal(0L, (long)nonZero.ExecuteScalar()!);
        }
        using (var nullCheck = conn.CreateCommand())
        {
            nullCheck.CommandText = "SELECT COUNT(*) FROM messages WHERE imported IS NULL";
            Assert.Equal(0L, (long)nullCheck.ExecuteScalar()!);
        }

        Assert.Equal(messagesBefore, ReadMessagesV11Shape(conn));
    }

    [Fact]
    public void V13_history_never_becomes_accepted_context_during_upgrade()
    {
        WriteRawV12();
        using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString()))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = """
                ALTER TABLE messages ADD COLUMN imported INTEGER NOT NULL DEFAULT 0;
                INSERT INTO messages (room_id, author_id, body, created_at, imported) VALUES
                    ('general','owner','/objective Old unaccepted command','2026-09-01T12:00:00.000+00:00',0),
                    ('general','owner','/correction Old unaccepted correction','2026-09-01T12:01:00.000+00:00',0),
                    ('general','owner','> /objective Quoted','2026-09-01T12:02:00.000+00:00',0),
                    ('general','owner','/objective Imported','2026-09-01T12:03:00.000+00:00',1),
                    ('general','owner',$oversized,'2026-09-01T12:04:00.000+00:00',0);
                PRAGMA user_version = 13;
                """;
            command.Parameters.AddWithValue("$oversized", "/objective " + new string('x', 6_001));
            command.ExecuteNonQuery();
        }
        var db = new ChopDb(DbPath);
        var before = new MessageStore(db).Read("general", 0, 200).Messages;
        db.EnsureDatabase();
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.Contains(".v13.", db.LastBackupPath);
        Assert.Equal(13, BackupScalar(db.LastBackupPath!, "PRAGMA user_version"));
        Assert.Equal(before.Count, BackupScalar(db.LastBackupPath!, "SELECT COUNT(*) FROM messages"));
        var snapshot = new MessageStore(db).ReadSpawnContext("general", 200);
        Assert.Equal(before, snapshot.Transcript);
        Assert.Null(snapshot.Governing.Objective);
        Assert.Null(snapshot.Governing.Correction);
        db.EnsureDatabase();
        Assert.Null(new MessageStore(db).ReadSpawnContext("general", 60).Governing.Objective);
    }

    [Fact]
    public void Row42_T1_a_torn_v13_with_the_column_present_but_stamp_12_is_finished_not_crashed()
    {
        WriteRawV12();
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE messages ADD COLUMN imported INTEGER NOT NULL DEFAULT 0;";
            cmd.ExecuteNonQuery();
            // stamp deliberately left at 12 — the torn state
        }
        SqliteConnection.ClearAllPools();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        using var check = db.Open();
        using var probe = check.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name = 'imported'";
        Assert.Equal(1L, (long)probe.ExecuteScalar()!);
    }

    [Fact]
    public void R25_T3_v9_database_is_backed_up_then_migrated_to_v10_with_the_skill_proposals_table_and_nothing_else_changed()
    {
        WriteRawV9();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());   // the ladder runs through v11 too now
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.Contains(".v9.", Path.GetFileName(db.LastBackupPath!));

        using (var conn = db.Open())
        {
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='skill_proposals'";
                Assert.Equal(1L, (long)probe.ExecuteScalar()!);
            }
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_skill_proposals_status'";
                Assert.Equal(1L, (long)probe.ExecuteScalar()!);
            }
            foreach (var column in new[] { "id", "room_id", "author_id", "name", "source_dir", "tree_sha256", "replaces_installed", "force", "files", "bytes", "status", "created_at", "decided_at", "installed_at" })
            {
                using var probe = conn.CreateCommand();
                probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('skill_proposals') WHERE name = $name";
                probe.Parameters.AddWithValue("$name", column);
                Assert.True(Convert.ToInt64(probe.ExecuteScalar()) == 1L, $"column '{column}' missing from skill_proposals");
            }
            using (var count = conn.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM skill_proposals";
                Assert.Equal(0L, (long)count.ExecuteScalar()!);
            }
        }

        // Every meaning v9 carried survives: roster, messages, cursors, and the memory proposal with
        // its kind/replaces/flags columns untouched.
        Assert.Equal(ChopDb.SeedRoster.Select(p => p.Id), new ParticipantStore(db).List().Select(p => p.Id));
        using (var conn = db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM messages";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
            cmd.CommandText = "SELECT kind, title FROM memory_proposals WHERE id = 1";
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(("append", "Likes tests"), (r.GetString(0), r.GetString(1)));
        }
        Assert.Equal("Likes tests", Assert.Single(new MemoryProposalStore(db).List("general")).Title);

        db.EnsureDatabase();
        Assert.Null(db.LastBackupPath);
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
    }

    [Fact]
    public void R25_T3_a_torn_v10_with_the_table_present_but_stamp_9_is_repaired_not_crashed()
    {
        WriteRawV9();
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Simulate a torn v10: the table and index already landed by hand, the stamp did not.
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
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        using var check = db.Open();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='skill_proposals'";
        Assert.Equal(1L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public void Row19_Task1_v7_database_is_backed_up_then_migrated_to_v8_with_the_run_tables_and_nothing_else_changed()
    {
        WriteRawV7();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.Contains(".v7.", Path.GetFileName(db.LastBackupPath!));

        using (var conn = db.Open())
        {
            foreach (var table in new[] { "runs", "run_phases", "run_artifacts", "run_gate_runs", "skill_files" })
            {
                using var probe = conn.CreateCommand();
                probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
                probe.Parameters.AddWithValue("$name", table);
                Assert.Equal(1L, (long)probe.ExecuteScalar()!);
            }
            using var runCount = conn.CreateCommand();
            runCount.CommandText = "SELECT COUNT(*) FROM runs";
            Assert.Equal(0L, (long)runCount.ExecuteScalar()!);
        }

        // Every meaning v7 carried survives: roster, classes, messages, cursors, proposals.
        var roster = new ParticipantStore(db).List();
        Assert.Equal(ChopDb.SeedRoster.Select(p => p.Id), roster.Select(p => p.Id));
        Assert.Equal(new[] { "visible", "judge" }, ParticipantClasses.Parse(roster.Single(p => p.Id == "opus").Classes));

        using (var conn = db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM messages";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
            cmd.CommandText = "SELECT COUNT(*) FROM memory_proposals WHERE status = 'pending'";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }

        // A run can now be started and read back through RunStore against this migrated database.
        var run = new RunStore(db).Start("general", "opus", "roadmap", "", 1, DateTimeOffset.UtcNow);
        Assert.Equal(RunStatus.Active, run.Status);

        db.EnsureDatabase();
        Assert.Null(db.LastBackupPath);
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
    }

    [Fact]
    public void Row19_Task1_a_torn_v8_with_the_run_tables_present_but_stamp_7_is_repaired_not_crashed()
    {
        WriteRawV7();
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Simulate a torn v8: every table already landed by hand, the stamp did not.
            cmd.CommandText = """
                CREATE TABLE runs (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    room_id         TEXT NOT NULL REFERENCES rooms(id),
                    conductor_id    TEXT NOT NULL REFERENCES participants(id),
                    skill_name      TEXT NOT NULL,
                    arguments       TEXT NOT NULL DEFAULT '',
                    status          TEXT NOT NULL,
                    reason          TEXT,
                    cap_spent       INTEGER NOT NULL DEFAULT 0,
                    phase           TEXT NOT NULL DEFAULT '(start)',
                    root_message_id INTEGER NOT NULL,
                    started_at      TEXT NOT NULL,
                    parked_at       TEXT,
                    parked_seconds  INTEGER NOT NULL DEFAULT 0,
                    ended_at        TEXT,
                    spawns_used     INTEGER NOT NULL DEFAULT 0,
                    exchanges       INTEGER NOT NULL DEFAULT 0
                );
                CREATE UNIQUE INDEX ux_runs_one_active_per_room ON runs(room_id) WHERE status = 'active';
                CREATE INDEX ix_runs_room ON runs(room_id, id);
                CREATE TABLE run_phases (
                    run_id  INTEGER NOT NULL REFERENCES runs(id),
                    phase   TEXT NOT NULL,
                    entries INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (run_id, phase)
                );
                CREATE TABLE run_artifacts (
                    run_id    INTEGER NOT NULL REFERENCES runs(id),
                    path      TEXT NOT NULL,
                    author_id TEXT NOT NULL REFERENCES participants(id),
                    at        TEXT NOT NULL,
                    PRIMARY KEY (run_id, path)
                );
                CREATE TABLE run_gate_runs (
                    id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id    INTEGER REFERENCES runs(id),
                    room_id   TEXT NOT NULL,
                    gate      TEXT NOT NULL,
                    caller_id TEXT NOT NULL,
                    exit_code INTEGER,
                    outcome   TEXT NOT NULL,
                    at        TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        using var check = db.Open();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='runs'";
        Assert.Equal(1L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public void R18_T1_v8_database_is_backed_up_then_migrated_to_v9_with_three_proposal_columns_and_nothing_else_changed()
    {
        WriteRawV8();
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();
        // EnsureDatabase runs the whole ladder in one call, so a v8 start lands on
        // LatestSchemaVersion, not on 9. This test's own subject (the three proposal columns) still
        // landed at v9's step; it just does not stop there.
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.Contains(".v8.", Path.GetFileName(db.LastBackupPath!));
        using (var conn = db.Open())
        {
            foreach (var column in new[] { "kind", "replaces", "flags" })
            {
                using var probe = conn.CreateCommand();
                probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('memory_proposals') WHERE name = $name";
                probe.Parameters.AddWithValue("$name", column);
                Assert.Equal(1L, (long)probe.ExecuteScalar()!);
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT kind, replaces, flags, title, status FROM memory_proposals WHERE id = 1";
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(("append", true, true, "Likes tests", "pending"), (r.GetString(0), r.IsDBNull(1), r.IsDBNull(2), r.GetString(3), r.GetString(4)));
        }
        // Every meaning v8 carried survives: roster, messages, cursors, and the proposal reads back through the store.
        Assert.Equal(ChopDb.SeedRoster.Select(p => p.Id), new ParticipantStore(db).List().Select(p => p.Id));
        Assert.Equal("Likes tests", Assert.Single(new MemoryProposalStore(db).List("general")).Title);
        db.EnsureDatabase();
        Assert.Null(db.LastBackupPath);
    }

    [Fact]
    public void R18_T1_a_torn_v9_with_the_columns_present_but_stamp_8_is_repaired_not_crashed()
    {
        WriteRawV8();
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE memory_proposals ADD COLUMN kind TEXT NOT NULL DEFAULT 'append'; ALTER TABLE memory_proposals ADD COLUMN replaces TEXT; ALTER TABLE memory_proposals ADD COLUMN flags TEXT;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());   // ladder runs through v10 too now
        Assert.Single(new MemoryProposalStore(db).List("general"));
    }

    [Fact]
    public void M11_A1_v6_database_is_backed_up_then_migrated_to_v7_with_classes_the_skills_table_and_owner_remote_seeded()
    {
        WriteRawV6();

        // Simulate a torn v7: an operator already ran the ALTER by hand, with a value already set on
        // an existing seed row (gpt-6-astra, which the seed leaves NULL) and on a row the seed list
        // does not know about at all (guest). BackfillClasses must never touch either: only NULL is
        // filled.
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                ALTER TABLE participants ADD COLUMN classes TEXT;
                UPDATE participants SET classes = 'judge' WHERE id = 'gpt-6-astra';
                INSERT INTO participants (id, display_name, kind, host, model, note, classes) VALUES ('guest','Guest','model','codex',NULL,NULL,'plumbing');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.Contains(".v6.", Path.GetFileName(db.LastBackupPath!));

        using (var conn = db.Open())
        {
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('participants') WHERE name = 'classes'";
                Assert.Equal(1L, (long)probe.ExecuteScalar()!);
            }
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='skills'";
                Assert.Equal(1L, (long)probe.ExecuteScalar()!);
            }
        }

        var roster = new ParticipantStore(db).List();
        Assert.Equal("owner", new ParticipantStore(db).OwnerId());
        var ownerRemote = roster.Single(p => p.Id == ChopDb.OwnerRemoteParticipantId);
        Assert.Equal("human", ownerRemote.Kind);

        var opus = roster.Single(p => p.Id == "opus");
        Assert.Equal(new[] { "visible", "judge" }, ParticipantClasses.Parse(opus.Classes));
        var sonnet = roster.Single(p => p.Id == "sonnet");
        Assert.Equal(new[] { "plumbing" }, ParticipantClasses.Parse(sonnet.Classes));
        var fable = roster.Single(p => p.Id == "fable");
        Assert.Equal(new[] { "judge" }, ParticipantClasses.Parse(fable.Classes));

        // Hand-set classes survive the migration: BackfillClasses only fills NULL.
        Assert.Equal("judge", roster.Single(p => p.Id == "gpt-6-astra").Classes);
        Assert.Equal("plumbing", roster.Single(p => p.Id == "guest").Classes);

        db.EnsureDatabase();
        Assert.Null(db.LastBackupPath);
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
    }

    [Fact]
    public void M9_A1_v5_database_is_backed_up_then_migrated_to_v6_with_two_nullable_room_columns_and_nothing_else_changed()
    {
        WriteRawV5();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.Contains(".v5.", Path.GetFileName(db.LastBackupPath!));

        Assert.Equal(ChopDb.SeedRoster.Select(p => p.Id), new ParticipantStore(db).List().Select(p => p.Id));

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT body FROM messages WHERE id = 1";
        Assert.Equal("@opus first v3 message", (string)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT last_read_id FROM read_cursors WHERE participant_id = 'opus' AND room_id = 'general'";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM memory_proposals WHERE status = 'pending'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT name FROM pragma_table_info('rooms')";
        var roomColumns = new HashSet<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) roomColumns.Add(reader.GetString(0));
        Assert.Contains("directory", roomColumns);
        Assert.Contains("archived_at", roomColumns);
        cmd.CommandText = "SELECT COUNT(*) FROM rooms WHERE id = 'general' AND directory IS NULL AND archived_at IS NULL";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);

        var general = new MessageStore(db).GetRoom("general");
        Assert.NotNull(general);
        Assert.Null(general!.Directory);
        Assert.Null(general.ArchivedAt);
        Assert.Equal(2, general.MessageCount);

        db.EnsureDatabase();
        Assert.Null(db.LastBackupPath);
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
    }

    [Fact]
    public void M9_A1_torn_v6_with_both_columns_present_but_stamp_5_is_repaired_not_crashed()
    {
        WriteRawV5();
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE rooms ADD COLUMN directory TEXT; ALTER TABLE rooms ADD COLUMN archived_at TEXT;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        using var check = db.Open();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT name FROM pragma_table_info('rooms')";
        var roomColumns = new HashSet<string>();
        using (var reader = count.ExecuteReader())
            while (reader.Read()) roomColumns.Add(reader.GetString(0));
        Assert.Contains("directory", roomColumns);
        Assert.Contains("archived_at", roomColumns);
    }

    [Fact]
    public void M10_A7_v4_database_is_backed_up_then_migrated_to_v5_with_the_proposals_table_and_nothing_else_changed()
    {
        WriteRawV4();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.Contains(".v4.", Path.GetFileName(db.LastBackupPath!));

        var roster = new ParticipantStore(db).List();
        Assert.Equal(ChopDb.SeedRoster.Select(p => p.Id), roster.Select(p => p.Id));
        Assert.Equal("owner", new ParticipantStore(db).OwnerId());

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT body FROM messages WHERE id = 1";
        Assert.Equal("@opus first v3 message", (string)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT last_read_id FROM read_cursors WHERE participant_id = 'opus' AND room_id = 'general'";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM memory_proposals";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('memory_proposals')";
        Assert.Equal(15L, (long)cmd.ExecuteScalar()!);   // 12 through v8, plus v9's kind, replaces, flags

        db.EnsureDatabase();
        Assert.Null(db.LastBackupPath);
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
    }

    [Fact]
    public void M5_A8_v3_database_is_backed_up_then_migrated_to_v4_with_the_hub_row_seeded()
    {
        WriteRawV3();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.Contains(".v3.", Path.GetFileName(db.LastBackupPath!));

        var roster = new ParticipantStore(db).List();
        Assert.Equal(ChopDb.SeedRoster.Select(p => p.Id), roster.Select(p => p.Id));   // hub is last, by rowid
        var hub = roster.Single(p => p.Id == ChopDb.HubParticipantId);
        Assert.Equal(("system", "hub", (string?)null), (hub.Kind, hub.Host, hub.Model));
        Assert.Equal("owner", new ParticipantStore(db).OwnerId());                      // owner, not "the one human" (owner-remote lands at v7)
        var fable = roster.Single(p => p.Id == "fable");
        Assert.Contains("usage credits", fable.Note);                                   // v3 rows untouched

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT body FROM messages WHERE id = 1";
        Assert.Equal("@opus first v3 message", (string)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT client_key FROM messages WHERE id = 2";
        Assert.Equal("k-1", (string)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT last_read_id FROM read_cursors WHERE participant_id = 'opus' AND room_id = 'general'";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);

        db.EnsureDatabase();
        Assert.Null(db.LastBackupPath);
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
    }

    [Fact]
    public void M8_A1_v2_database_is_backed_up_then_migrated_to_v3_with_the_roster_seeded()
    {
        WriteRawV2();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.Contains(".v2.", Path.GetFileName(db.LastBackupPath!));

        var roster = new ParticipantStore(db).List();
        Assert.Equal(ChopDb.SeedRoster.Select(p => p.Id), roster.Select(p => p.Id));
        var owner = roster.Single(p => p.Id == "owner");
        Assert.Equal(("human", "human", (string?)null), (owner.Kind, owner.Host, owner.Model));
        var claude = roster.Single(p => p.Id == "claude");
        Assert.Equal(("model", "claude", (string?)null), (claude.Kind, claude.Host, claude.Model));
        var fable = roster.Single(p => p.Id == "fable");
        Assert.Equal(("model", "claude", "fable"), (fable.Kind, fable.Host, fable.Model));
        Assert.Contains("usage credits", fable.Note);
        var astra = roster.Single(p => p.Id == "gpt-6-astra");
        Assert.Equal(("model", "codex", "gpt-6-astra"), (astra.Kind, astra.Host, astra.Model));

        // Every v2 meaning survives: rows, the retry key, the cursor.
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT client_key FROM messages WHERE id = 2";
        Assert.Equal("k-1", (string)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT last_read_id FROM read_cursors WHERE participant_id = 'claude' AND room_id = 'general'";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);

        // Idempotent: a second EnsureDatabase migrates nothing and backs up nothing.
        db.EnsureDatabase();
        Assert.Null(db.LastBackupPath);
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
    }

    [Fact]
    public void M8_A1_torn_v3_with_a_column_present_but_stamp_2_is_repaired_not_crashed()
    {
        WriteRawV2();
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE participants ADD COLUMN host TEXT;";   // half of v3 landed, stamp did not
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.Equal("claude", new ParticipantStore(db).List().Single(p => p.Id == "claude").Host);
    }

    [Fact]
    public void A2_A3_v1_database_is_backed_up_then_migrated_with_every_meaning_intact()
    {
        WriteRawV1();
        var before = File.ReadAllBytes(DbPath);

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.True(File.Exists(db.LastBackupPath!));
        Assert.Contains(".v1.", Path.GetFileName(db.LastBackupPath!));

        // A2: the backup is a real database still readable at v1, holding the pre-migration rows.
        using (var backup = new SqliteConnection($"Data Source={db.LastBackupPath};Mode=ReadOnly;Pooling=False"))
        {
            backup.Open();
            using var q = backup.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM messages";
            Assert.Equal(2L, Convert.ToInt64(q.ExecuteScalar()));
            q.CommandText = "PRAGMA user_version";
            Assert.Equal(1L, Convert.ToInt64(q.ExecuteScalar()));
        }

        // A3: rows, ids, authors, bodies, timestamps and cursors are untouched.
        var store = new MessageStore(db);
        var page = store.Read("general", 0, 50);
        Assert.Equal(2, page.Messages.Count);
        Assert.Equal(new[] { 1L, 2L }, page.Messages.Select(m => m.Id));
        Assert.Equal(new[] { "owner", "claude" }, page.Messages.Select(m => m.AuthorId));
        Assert.Equal("first v1 message", page.Messages[0].Body);
        // Compare as instants, not as strings: Timestamps.Stamp emits round-trip "o" format with
        // seven fraction digits, so it never equals the fixture's three-digit literal.
        Assert.Equal(
            new[] { DateTimeOffset.Parse("2026-09-01T10:01:00.000+00:00"), DateTimeOffset.Parse("2026-09-01T10:02:00.000+00:00") },
            page.Messages.Select(m => m.CreatedAt.ToUniversalTime()));
        Assert.Equal(1L, store.GetCursor("codex", "general"));
        Assert.Single(store.ListRooms());
        Assert.Equal((long)ChopDb.SeedRoster.Count, ScalarLong("SELECT COUNT(*) FROM participants"));   // seeds not duplicated by the ladder
        Assert.True(new FileInfo(DbPath).Length > 0 && before.Length > 0);
    }

    [Fact]
    public void A2_the_backup_captures_writes_still_sitting_in_the_WAL()
    {
        WriteRawV1();

        // A writer holds the connection open with checkpointing disabled, so the extra rows exist
        // ONLY in the -wal file. A file copy of chopitup.db would miss all of them; the online
        // backup must not. This is the case the whole BackupDatabase choice exists for.
        using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            writer.Open();
            using (var off = writer.CreateCommand())
            {
                off.CommandText = "PRAGMA wal_autocheckpoint=0;";
                off.ExecuteNonQuery();
            }
            using var tx = writer.BeginTransaction();
            using var ins = writer.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO messages (room_id, author_id, body, created_at) VALUES ('general','claude',$b,'2026-09-01T10:03:00.000+00:00')";
            var p = ins.Parameters.Add("$b", Microsoft.Data.Sqlite.SqliteType.Text);
            for (int i = 0; i < 1_000; i++) { p.Value = $"wal message {i}"; ins.ExecuteNonQuery(); }
            tx.Commit();

            var db = new ChopDb(DbPath);
            db.EnsureDatabase();
            Assert.NotNull(db.LastBackupPath);
            Assert.Equal(1002L, BackupScalar(db.LastBackupPath!, "SELECT COUNT(*) FROM messages"));
            Assert.Equal(2L, BackupScalar(db.LastBackupPath!, "SELECT COUNT(*) FROM messages WHERE body LIKE '%v1 message'"));
        }
    }

    [Fact]
    public void A8_fresh_directory_lands_on_v2_with_no_backup()
    {
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.Null(db.LastBackupPath);
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
    }

    [Fact]
    public void A3_running_the_ladder_again_is_a_no_op()
    {
        WriteRawV1();
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();          // -> v2, one backup
        db.EnsureDatabase();          // already current: no ALTER, no second backup
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.Single(Directory.GetFiles(_dir, "*.bak"));
    }

    [Fact]
    public void A2_a_genuinely_torn_v2_finishes_instead_of_crashing()
    {
        // The real torn state: the ALTER landed, the process died before the stamp. The column is
        // there, user_version is still 1. Re-running must complete the step, not fail on a
        // duplicate column, and must still take a backup (the DB is stamped below latest).
        WriteRawV1();
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE messages ADD COLUMN client_key TEXT;";
            cmd.ExecuteNonQuery();
        }
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.Equal(2, new MessageStore(db).Read("general", 0, 50).Messages.Count);
    }

    [Fact]
    public void A2_a_backup_that_fails_aborts_the_migration_and_leaves_nothing_behind()
    {
        WriteRawV1();
        var db = new ChopDb(DbPath) { BackupDestinationFactory = _ => throw new IOException("disk full (injected)") };

        var ex = Assert.Throws<IOException>(() => db.EnsureDatabase());
        Assert.Contains("injected", ex.Message);

        // The database is untouched at v1 — the migration never started — and nothing on disk
        // claims to be a way back.
        Assert.Equal(1, new ChopDb(DbPath).GetSchemaVersion());
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
        Assert.Empty(Directory.GetFiles(_dir, "*.partial"));
    }

    [Theory]
    [InlineData(1, 99, "holds")]        // count parity fails
    [InlineData(7, 2, "stamped")]       // version stamp fails
    public void A2_each_verification_leg_rejects_a_bad_copy(int fromVersion, long expectedMessages, string messageFragment)
    {
        // `BackupDatabase` replaces the destination wholesale, so a bad copy cannot be staged
        // through the factory — the verifier is asserted directly. `VerifyBackup` is `internal`
        // and the test project already has InternalsVisibleTo on the Hub; add it on Core too.
        WriteRawV1();
        using var copy = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
        copy.Open();
        var ex = Assert.Throws<InvalidOperationException>(() => ChopDb.VerifyBackup(copy, fromVersion, expectedMessages));
        Assert.Contains(messageFragment, ex.Message);
    }

    [Fact]
    public void A2_an_earlier_verified_backup_is_kept_and_an_abandoned_partial_is_swept()
    {
        WriteRawV1();
        var earlier = DbPath + ".v1.20260101T000000Z.bak";
        File.Copy(DbPath, earlier);                                        // a real, verified-looking snapshot
        var abandoned = DbPath + ".v1.20260101T000000Z.bak.partial";
        File.WriteAllText(abandoned, "torn copy from a killed process");

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.True(File.Exists(earlier));                                 // older snapshots are never destroyed
        Assert.False(File.Exists(abandoned));                              // torn ones never survive to look like snapshots
        Assert.Equal(2, Directory.GetFiles(_dir, "*.bak").Length);
    }

    [Fact]
    public void A2_a_stamp_less_database_that_still_holds_messages_is_backed_up()
    {
        // Version 0 does not always mean "never finished being created": a .dump/.read rebuild or a
        // hand repair loses the stamp and keeps every message.
        WriteRawV1();
        using (var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadWrite;Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 0;";
            cmd.ExecuteNonQuery();
        }
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();
        Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.Equal(2L, BackupScalar(db.LastBackupPath!, "SELECT COUNT(*) FROM messages"));
    }

    [Fact]
    public void A2_a_newer_database_is_refused_rather_than_opened()
    {
        WriteRawV1();
        using (var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadWrite;Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {ChopDb.LatestSchemaVersion + 1};";
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<InvalidOperationException>(() => new ChopDb(DbPath).EnsureDatabase());
        Assert.Contains("newer build", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
    }

    private long ScalarLong(string sql)
    {
        using var conn = new ChopDb(DbPath).Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static long BackupScalar(string backupPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { }
            Directory.Delete(_dir, recursive: true);
        }
    }
}
