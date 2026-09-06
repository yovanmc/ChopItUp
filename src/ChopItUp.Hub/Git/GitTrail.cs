using System.Globalization;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Git;

public sealed record GitIdentity(string Name, string Email)
{
    public override string ToString() => $"{Name} <{Email}>";
}

public sealed record TrailCommit(string Hash, string Author, DateTimeOffset At, string Subject);

/// <summary><see cref="Hash"/> is HEAD after the call - the new commit, or the unchanged HEAD when there
/// was nothing to commit and empties were not allowed - or null on failure with <see cref="Reason"/>;
/// <see cref="Created"/> says whether a commit was made; <see cref="FilesChanged"/> counts the paths in
/// the commit that was made (0 for an empty one).</summary>
public sealed record CommitOutcome(string? Hash, bool Created, int FilesChanged, string? Reason);

/// <summary>One git working tree the hub commits into - the memory store (D15) and every room
/// directory (D11). Generalised from M10's MemoryGit: the committer is always the hub, the author is
/// whoever the caller says (a spawned model, the owner). git is resolved directly (a real git.exe on
/// PATH), not through the spawner's CliLocator seam, so hub tests exercise the real trail (M10
/// decision 4). Nothing here throws: a failure is a null/false/empty result with <see cref="Reason"/>
/// set and one line in the hub log. Serialised per instance - one tree, one writer at a time.</summary>
public class GitTrail
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public static readonly GitIdentity Hub = new("ChopItUp hub", "hub@chopitup.local");
    private const char FieldSep = (char)0x1F;
    // LC_ALL=C: the no-op detection reads git's English "nothing to commit" (M10 critique, P2-7).
    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" };
    // A fixed committer and no signing: an unconfigured machine (or a CI runner) must still commit.
    private static readonly string[] Committer =
        ["-c", "user.name=" + Hub.Name, "-c", "user.email=" + Hub.Email, "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false"];

    private readonly Func<ResolvedCli> _resolve;
    private readonly IProcessRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ResolvedCli? _git;
    private bool _unavailable;

    public GitTrail(string root, Func<ResolvedCli>? resolve = null, IProcessRunner? runner = null)
    {
        Root = Path.GetFullPath(root);
        _resolve = resolve ?? (() => CliResolver.Resolve("git"));
        _runner = runner ?? new ProcessRunner();
    }

    public string Root { get; }

    /// <summary>Why the last call failed; null after a success.</summary>
    public string? Reason { get; private set; }

    /// <summary>Prefix of this trail's hub-log lines.</summary>
    protected virtual string LogName => "room";

    /// <summary>True when git.exe was found (resolved once; a miss is remembered for this instance).</summary>
    public bool IsAvailable() => Resolve() is not null;

    /// <summary>The repository root that contains <see cref="Root"/>, as a Windows path, or null when
    /// <see cref="Root"/> is not inside any repository (or does not exist, or git is unavailable).</summary>
    public async Task<string?> TopLevelAsync(CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !Directory.Exists(Root)) return null;
        var r = await Run(git, ["rev-parse", "--show-toplevel"], "", cancellation);
        if (r.ExitCode != 0) return null;
        var text = r.StandardOutput.Trim();
        return text.Length == 0 ? null : Path.GetFullPath(text.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>`git init` when <see cref="Root"/> is not yet a repository (creating the folder if needed).</summary>
    public async Task<bool> InitAsync(CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var git = Resolve();
            if (git is null) return false;
            Directory.CreateDirectory(Root);
            if (Directory.Exists(Path.Combine(Root, ".git"))) { Reason = null; return true; }
            var init = await Run(git, ["init", "-q"], "", cancellation);
            if (init.ExitCode != 0) { Fail("git init", init); return false; }
            Reason = null;
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>HEAD's short hash, or null before the first commit (quietly - an unborn HEAD is a
    /// normal state, not a failure) or when git is unavailable.</summary>
    public async Task<string?> HeadAsync(CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !Directory.Exists(Path.Combine(Root, ".git"))) return null;
        return await HeadUnlocked(git, logFailure: false, cancellation);
    }

    /// <summary>True when `git status --porcelain` lists anything (modified, added, deleted, untracked).
    /// False when clean - and false, with <see cref="Reason"/>, when the question could not be asked.</summary>
    public async Task<bool> IsDirtyAsync(CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !Directory.Exists(Path.Combine(Root, ".git"))) return false;
        var r = await Run(git, ["status", "--porcelain"], "", cancellation);
        if (r.ExitCode != 0) { Fail("git status", r); return false; }
        Reason = null;
        return r.StandardOutput.Trim().Length > 0;
    }

    /// <summary>`git add -A` then a commit authored by <paramref name="author"/>; the committer is the
    /// hub. The message travels on stdin (`-F -`): a room commit carries a shell log, and a Windows
    /// command line is capped at 32,767 characters. Initialises the repository if it is missing. With
    /// <paramref name="allowEmpty"/> false, "nothing to commit" is not a failure: the outcome is HEAD
    /// with <see cref="CommitOutcome.Created"/> false.</summary>
    public async Task<CommitOutcome> CommitAllAsync(string message, GitIdentity author, bool allowEmpty, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var git = Resolve();
            if (git is null) return new(null, false, 0, Reason);
            Directory.CreateDirectory(Root);
            if (!Directory.Exists(Path.Combine(Root, ".git")))
            {
                var init = await Run(git, ["init", "-q"], "", cancellation);
                if (init.ExitCode != 0) return new(null, false, 0, Fail("git init", init));
            }
            var add = await Run(git, ["add", "-A", "--", "."], "", cancellation);
            if (add.ExitCode != 0) return new(null, false, 0, Fail("git add", add));

            var args = new List<string>(Committer) { "commit", "-q", "--author=" + author, "-F", "-" };
            if (allowEmpty) args.Insert(args.Count - 2, "--allow-empty");
            var commit = await Run(git, args, message, cancellation);
            if (commit.ExitCode != 0)
            {
                if (!allowEmpty && (commit.StandardOutput + commit.StandardError).Contains("nothing to commit", StringComparison.Ordinal))
                {
                    Reason = null;
                    return new(await HeadUnlocked(git, logFailure: true, cancellation), false, 0, Reason);
                }
                return new(null, false, 0, Fail("git commit", commit));
            }
            var head = await HeadUnlocked(git, logFailure: true, cancellation);
            if (head is null) return new(null, true, 0, Reason);
            var names = await Run(git, ["show", "--name-only", "--format=", "HEAD"], "", cancellation);
            var files = names.ExitCode == 0
                ? names.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length
                : 0;
            Reason = null;
            return new(head, true, files, null);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The newest <paramref name="limit"/> commits, newest first; empty before the first commit
    /// (quietly) or on failure (with <see cref="Reason"/>).</summary>
    public async Task<IReadOnlyList<TrailCommit>> LogAsync(int limit, CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !Directory.Exists(Path.Combine(Root, ".git"))) return [];
        var format = "--format=%h" + FieldSep + "%an <%ae>" + FieldSep + "%aI" + FieldSep + "%s";
        var r = await Run(git, ["log", format, "-n", Math.Clamp(limit, 1, 200).ToString(CultureInfo.InvariantCulture)], "", cancellation);
        if (r.ExitCode != 0)
        {
            if (!r.StandardError.Contains("does not have any commits", StringComparison.Ordinal)) Fail("git log", r);
            return [];
        }
        var commits = new List<TrailCommit>();
        foreach (var line in r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(FieldSep);
            if (parts.Length < 4 || !DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) continue;
            commits.Add(new TrailCommit(parts[0], parts[1], at, parts[3]));
        }
        Reason = null;
        return commits;
    }

    private async Task<string?> HeadUnlocked(ResolvedCli git, bool logFailure, CancellationToken cancellation)
    {
        var head = await Run(git, ["rev-parse", "--short", "HEAD"], "", cancellation);
        if (head.ExitCode == 0) return head.StandardOutput.Trim();
        if (logFailure) Fail("git rev-parse", head);
        return null;
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
            Console.Error.WriteLine($"{LogName}: {Reason}");
            return null;
        }
    }

    private Task<ProcessResult> Run(ResolvedCli git, IReadOnlyList<string> args, string stdin, CancellationToken cancellation) =>
        _runner.RunAsync(new ProcessSpec(git.FileName, [.. git.LeadingArguments, .. args], Env, Root, stdin, LogName + "-git"), Timeout, cancellation);

    private string Fail(string step, ProcessResult r)
    {
        Reason = $"{step} exited {(r.ExitCode?.ToString() ?? "killed")}: {r.StandardError.Trim()}";
        Console.Error.WriteLine($"{LogName}: {Reason}");
        return Reason;
    }
}
