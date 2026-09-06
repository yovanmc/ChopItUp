using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Memory;

/// <summary>The trail behind the memory store (D15: git-backed): every approval is one commit in a
/// repository that lives inside <c>&lt;data&gt;\memory\</c>, initialised lazily on the first commit.
/// git is not a spawn host, so it is resolved on its own (a real <c>git.exe</c> on PATH) rather than
/// through the spawner's <see cref="CliLocator"/> seam — a hub test that approves a proposal exercises
/// the real trail (plan decision 4). Nothing here throws: the file write is the mechanism, the commit
/// is the record, and a machine without git loses the record, not the memory
/// (<see cref="Reason"/> says why; the hub log says it once).</summary>
public sealed class MemoryGit
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    // LC_ALL=C: the no-op detection below reads git's English "nothing to commit" (critique pass 2, P2-7).
    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" };
    // A fixed identity and no signing: an unconfigured machine (or a CI runner) must still commit.
    private static readonly string[] Identity =
        ["-c", "user.name=ChopItUp hub", "-c", "user.email=hub@chopitup.local", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false"];

    private readonly string _root;
    private readonly Func<ResolvedCli> _resolve;
    private readonly IProcessRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ResolvedCli? _git;
    private bool _unavailable;

    public MemoryGit(string root, Func<ResolvedCli>? resolve = null, IProcessRunner? runner = null)
    {
        _root = Path.GetFullPath(root);
        _resolve = resolve ?? (() => CliResolver.Resolve("git"));
        _runner = runner ?? new ProcessRunner();
    }

    /// <summary>Why the last <see cref="CommitAsync"/> returned null; null after a success.</summary>
    public string? Reason { get; private set; }

    /// <summary>Stages everything under the root and commits it. Returns the short hash of HEAD — the
    /// new commit, or the unchanged HEAD when there was nothing to commit — or null with
    /// <see cref="Reason"/> set. Serialised: two approvals never race inside one repository.</summary>
    public async Task<string?> CommitAsync(string message, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var git = Resolve();
            if (git is null) return null;
            Directory.CreateDirectory(_root);
            if (!Directory.Exists(Path.Combine(_root, ".git")))
            {
                var init = await Run(git, ["init", "-q"], cancellation);
                if (init.ExitCode != 0) return Fail("git init", init);
            }
            var add = await Run(git, ["add", "-A", "--", "."], cancellation);
            if (add.ExitCode != 0) return Fail("git add", add);
            var commit = await Run(git, [.. Identity, "commit", "-q", "-m", message], cancellation);
            if (commit.ExitCode != 0 && !(commit.StandardOutput + commit.StandardError).Contains("nothing to commit", StringComparison.Ordinal))
                return Fail("git commit", commit);
            var head = await Run(git, ["rev-parse", "--short", "HEAD"], cancellation);
            if (head.ExitCode != 0) return Fail("git rev-parse", head);
            Reason = null;
            return head.StandardOutput.Trim();
        }
        finally { _gate.Release(); }
    }

    private ResolvedCli? Resolve()
    {
        if (_git is not null) return _git;
        if (_unavailable) return null;
        try { return _git = _resolve(); }
        catch (Exception e) when (e is FileNotFoundException or InvalidOperationException)
        {
            _unavailable = true;
            Reason = "git is not available: " + e.Message;
            Console.Error.WriteLine("memory: " + Reason + " Approved entries are still written; they are not committed.");
            return null;
        }
    }

    private Task<ProcessResult> Run(ResolvedCli git, IReadOnlyList<string> args, CancellationToken cancellation) =>
        _runner.RunAsync(new ProcessSpec(git.FileName, [.. git.LeadingArguments, .. args], Env, _root, "", "memory-git"), Timeout, cancellation);

    private string? Fail(string step, ProcessResult r)
    {
        Reason = $"{step} exited {(r.ExitCode?.ToString() ?? "killed")}: {r.StandardError.Trim()}";
        Console.Error.WriteLine("memory: " + Reason);
        return null;
    }
}
