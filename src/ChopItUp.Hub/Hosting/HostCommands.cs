using System.Text.Json;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Security;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Hub.Hosting;

/// <summary>The non-serving verbs. Each writes to the data dir, prints paths or a token, and
/// returns a process exit code. None of them binds a port or takes the hub lock.</summary>
public static class HostCommands
{
    public static int Run(HubOptions options, TextWriter output, TextWriter error) => options.Command switch
    {
        HubCommand.RotateToken => RotateToken(options, output, error),
        HubCommand.PrintConfig => PrintConfig(options, output, error),
        _ => throw new InvalidOperationException($"{options.Command} is not a non-serving command."),
    };

    /// <summary>The roster as the last hub start left it, read through a plain connection that runs
    /// NO pragmas. Never migrates and leaves the file byte-identical: a non-serving verb must have no
    /// write side effect (pass 2, MINOR-12 for tokens; the same rule for the schema).
    /// <see cref="ChopDb.Open"/> is not used on purpose — its <c>PRAGMA journal_mode=WAL</c> rewrites
    /// the header of a database that is not already WAL (a restored .bak is exactly that). Nor is
    /// <c>Mode=ReadOnly</c>: measured on Microsoft.Data.Sqlite 10.0.11, a read-only open of a WAL
    /// database whose -wal/-shm are absent creates both and does not remove them on close, so the
    /// verb would litter the data dir. <c>ReadWrite</c> (no Create) with no pragmas creates the
    /// sidecars on open and deletes them on close, leaves a cleanly-closed rollback-journal database
    /// untouched (a hot journal would be rolled back on open, which is SQLite recovery, not this
    /// verb's doing), and reads correctly beside a running hub. A database below the current version, above it, or
    /// absent is an exit-4 "start the hub once" / "run a newer build", mirroring the
    /// missing-tokens.json case.</summary>
    private static IReadOnlyList<string>? TryReadRoster(HubOptions options, TextWriter error, out IReadOnlyList<Participant> roster)
    {
        roster = [];
        var dbPath = Path.Combine(options.DataDir, "chopitup.db");
        if (!File.Exists(dbPath))
        {
            error.WriteLine($"No chopitup.db in '{options.DataDir}'. Start the hub once against this data directory first, or check --data.");
            return null;
        }
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        conn.Open();   // no PRAGMAs: see the summary
        int version;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(cmd.ExecuteScalar());
        }
        if (version < ChopDb.LatestSchemaVersion)
        {
            error.WriteLine($"'{dbPath}' is at schema v{version}; this build needs v{ChopDb.LatestSchemaVersion}. Start the hub once to migrate it and mint the new tokens, then re-run this command.");
            return null;
        }
        if (version > ChopDb.LatestSchemaVersion)
        {
            error.WriteLine($"'{dbPath}' is at schema v{version}; this build understands v{ChopDb.LatestSchemaVersion}. Run a newer build.");
            return null;
        }
        roster = ParticipantStore.ReadAll(conn);
        return roster.Select(p => p.Id).ToArray();
    }

    private static int RotateToken(HubOptions options, TextWriter output, TextWriter error)
    {
        // Rotating under a live hub writes a file nobody reads: the hub resolves tokens against the
        // snapshot it loaded at startup, so the leaked token keeps working. Refusing is the whole
        // difference between rotation and revocation (pass 2, MAJOR-6).
        if (HubLock.IsHeld(options.DataDir))
        {
            error.WriteLine($"A hub is running on '{options.DataDir}'. Stop it first — rotating while it runs writes a new token that the running hub ignores, and the old token keeps working.");
            return 5;
        }
        var tokenFile = Path.Combine(options.DataDir, TokenStore.FileName);
        if (!File.Exists(tokenFile))   // same reasoning as PrintConfig: never mint into a mistyped --data
        {
            error.WriteLine($"No {TokenStore.FileName} in '{options.DataDir}'. Start the hub once against this data directory first, or check --data.");
            return 4;
        }
        try
        {
            if (TryReadRoster(options, error, out _) is not { } ids) return 4;
            _ = TokenStore.Rotate(options.DataDir, ids, options.RotateParticipant!);
            // The token itself is deliberately NOT printed (critique pass 1, F7): every run of this
            // command lands in a terminal buffer, a shell history and often an agent transcript.
            // --print-config writes it to a file in the gitignored data dir instead.
            output.WriteLine($"Rotated the token for '{options.RotateParticipant}'. The old one is now dead.");
            output.WriteLine("Next: run --print-config to regenerate the host files, re-paste that host's config,");
            output.WriteLine("then start the hub.");
            return 0;
        }
        catch (ArgumentException e)
        {
            error.WriteLine(e.Message);
            return 2;
        }
        catch (FileNotFoundException e)
        {
            error.WriteLine(e.Message);
            return 4;
        }
        catch (SqliteException e)
        {
            error.WriteLine($"Could not read the roster: {e.Message}");
            return 3;
        }
    }

    /// <summary>Writes <c>claude-desktop.json</c>, <c>codex-config.toml</c> and <c>README.md</c> into
    /// <c>&lt;data&gt;/host-configs/</c>. Unlike --rotate-token this only reads the credentials, so it
    /// deliberately takes no lock check: it keeps working while a hub owns the directory (A7).</summary>
    private static int PrintConfig(HubOptions options, TextWriter output, TextWriter error)
    {
        var tokenFile = Path.Combine(options.DataDir, TokenStore.FileName);
        if (!File.Exists(tokenFile))   // same reasoning as Rotate: never mint into a mistyped --data
        {
            error.WriteLine($"No {TokenStore.FileName} in '{options.DataDir}'. Start the hub once against this data directory first, or check --data.");
            return 4;
        }
        try
        {
            if (TryReadRoster(options, error, out var roster) is not { } ids) return 4;

            // Read WITHOUT back-filling: TokenStore.Load mints any missing participant and rewrites
            // the file, so a hand-edited tokens.json would have a credential silently rotated by a
            // command that is supposed to only read (pass 2, MINOR-12).
            var tokens = TokenStore.ReadExisting(options.DataDir, ids);

            // Prefer the port the hub actually bound over the one this invocation happened to
            // resolve: a hub started with --port 9000 and a --print-config run without it would
            // otherwise emit configs pointing at 8790, exit 0, and be undetectable (pass 2,
            // MINOR-13).
            int? recorded = HubPortFile.Read(options.DataDir);
            int port = recorded ?? options.Port;
            if (recorded is { } r && r != options.Port)
                output.WriteLine($"Note: using port {r} from the last hub start, not the {options.Port} this command resolved.");

            var folder = HostConfigs.Write(options.DataDir, port, tokens, roster);
            output.WriteLine("Wrote host configurations to:");
            output.WriteLine(folder);
            output.WriteLine("Each file contains a live token — read them from disk, do not paste them anywhere public.");
            return 0;
        }
        // Broad on purpose: a torn tokens.json (JsonException), a contended mutex (TimeoutException)
        // or a tokens.json missing a participant (InvalidOperationException, from ReadExisting —
        // the one case this command must diagnose rather than repair) must each produce one line,
        // not a stack trace (critique pass 1, F14).
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or TimeoutException or InvalidOperationException or SqliteException)
        {
            error.WriteLine($"Could not write host configurations: {e.Message}");
            return 3;
        }
    }
}
