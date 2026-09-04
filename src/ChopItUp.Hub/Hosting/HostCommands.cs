using ChopItUp.Hub.Security;

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

    /// <summary>True when a hub process currently owns this data dir. Probes the same lock file
    /// HubLock takes, with the same FileShare.None, and releases it immediately.</summary>
    private static bool HubIsRunning(string dataDir)
    {
        var path = Path.Combine(dataDir, HubLock.FileName);
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
    }

    private static int RotateToken(HubOptions options, TextWriter output, TextWriter error)
    {
        // Rotating under a live hub writes a file nobody reads: the hub resolves tokens against the
        // snapshot it loaded at startup, so the leaked token keeps working. Refusing is the whole
        // difference between rotation and revocation (pass 2, MAJOR-6).
        if (HubIsRunning(options.DataDir))
        {
            error.WriteLine($"A hub is running on '{options.DataDir}'. Stop it first — rotating while it runs writes a new token that the running hub ignores, and the old token keeps working.");
            return 5;
        }
        try
        {
            _ = TokenStore.Rotate(options.DataDir, options.RotateParticipant!);
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
    }

    /// <summary>Task 5 completes this: writing <c>claude-desktop.json</c>, <c>codex-config.toml</c>
    /// and <c>README.md</c> into <c>&lt;data&gt;/host-configs/</c>. For Task 4 it is a genuine no-op
    /// rather than a placeholder that throws: --print-config only reads, so unlike --rotate-token it
    /// keeps the "works while a hub is running" contract (A7) even before it has a body — no lock
    /// check, no port bind, exit 0.</summary>
    private static int PrintConfig(HubOptions options, TextWriter output, TextWriter error) => 0;
}
