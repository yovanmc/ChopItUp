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

/// <summary>How a <see cref="GitTrail.MergeAsync"/> call ended: a merge commit was made
/// (<see cref="Merged"/>), it conflicted and was aborted (<see cref="Conflict"/>), or it failed for
/// some other reason and was also aborted when a merge was left in progress (<see cref="Failed"/>).</summary>
public enum MergeResult { Merged, Conflict, Failed }

/// <summary><see cref="Hash"/> is the merge commit's HEAD, set only for <see cref="MergeResult.Merged"/>.
/// <see cref="Conflicts"/> lists the conflicting paths, set only for <see cref="MergeResult.Conflict"/>.
/// <see cref="Reason"/> is set for <see cref="MergeResult.Failed"/> (and for a failed abort of an
/// otherwise-conflicting merge). <see cref="Before"/> is HEAD as read from inside the same gated call,
/// before anything else could move it - a caller comparing "did the merge actually add anything" reads
/// this instead of a separately fetched HEAD, which could already be stale by the time the merge itself
/// starts (row 35).</summary>
public sealed record MergeOutcome(MergeResult Result, string? Hash, IReadOnlyList<string> Conflicts, string? Reason, string? Before = null);

/// <summary>One git working tree the hub commits into - the memory store (D15) and every room
/// directory (D11). Generalised from M10's MemoryGit: the author and committer are the identity git
/// itself resolves at the root, the hub's only when git resolves none; a caller may still name an
/// explicit author (the memory store does) — row 46. git is resolved directly (a real git.exe on
/// PATH), not through the spawner's CliLocator seam, so hub tests exercise the real trail (M10
/// decision 4). Nothing here throws: a failure is a null/false/empty result with <see cref="Reason"/>
/// set and one line in the hub log. Serialised per repository - every trail made by <see cref="WithRoot"/>
/// shares the gate.</summary>
public class GitTrail
{
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

    private readonly Func<ResolvedCli> _resolve;
    private readonly IProcessRunner _runner;
    private readonly SemaphoreSlim _gate;
    private ResolvedCli? _git;
    private bool _unavailable;

    public GitTrail(string root, Func<ResolvedCli>? resolve = null, IProcessRunner? runner = null)
        : this(root, resolve, runner, new SemaphoreSlim(1, 1)) { }

    private GitTrail(string root, Func<ResolvedCli>? resolve, IProcessRunner? runner, SemaphoreSlim gate)
    {
        Root = Path.GetFullPath(root);
        _resolve = resolve ?? (() => CliResolver.Resolve("git"));
        _runner = runner ?? new ProcessRunner();
        _gate = gate;
    }

    public string Root { get; }

    /// <summary>A trail over <paramref name="root"/> (a linked worktree of this repository) that shares
    /// this trail's git resolver, process runner and write gate: every write to one repository, from any
    /// of its worktrees, goes through one gate.</summary>
    public GitTrail WithRoot(string root) => new(root, _resolve, _runner, _gate);

    /// <summary>True when <see cref="Root"/> holds a repository entry: a `.git` folder (a main tree) or
    /// a `.git` file (a linked worktree, whose `.git` names the real one).</summary>
    private bool HasGitEntry() => Directory.Exists(Path.Combine(Root, ".git")) || File.Exists(Path.Combine(Root, ".git"));

    /// <summary>Why the last call failed; null after a success.</summary>
    public string? Reason { get; private set; }

    /// <summary>Prefix of this trail's hub-log lines.</summary>
    protected virtual string LogName => "room";

    /// <summary>True for a trail whose every commit is the hub's own (the memory store): author and
    /// committer stay <see cref="Hub"/> whatever the machine's git config says. A room trail is false:
    /// its commits belong to that room's own repository and carry that repository's identity (row 46).</summary>
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
            if (HasGitEntry()) { Reason = null; return true; }
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
        if (git is null || !HasGitEntry()) return null;
        return await HeadUnlocked(git, logFailure: false, cancellation);
    }

    /// <summary>True when `git status --porcelain` lists anything (modified, added, deleted, untracked).
    /// False when clean - and false, with <see cref="Reason"/>, when the question could not be asked.</summary>
    public async Task<bool> IsDirtyAsync(CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !HasGitEntry()) return false;
        var r = await Run(git, ["status", "--porcelain"], "", cancellation);
        if (r.ExitCode != 0) { Fail("git status", r); return false; }
        Reason = null;
        return r.StandardOutput.Trim().Length > 0;
    }

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
            var git = Resolve();
            if (git is null) return new(null, false, 0, Reason);
            Directory.CreateDirectory(Root);
            if (!HasGitEntry())
            {
                var init = await Run(git, ["init", "-q"], "", cancellation);
                if (init.ExitCode != 0) return new(null, false, 0, Fail("git init", init));
            }
            if (await OperationInProgressUnlocked(git, cancellation) is { } op)
            {
                Reason = $"a {op} is in progress in {Root}; nothing was committed";
                Console.Error.WriteLine($"{LogName}: {Reason}");
                return new(null, false, 0, Reason);
            }
            var add = await Run(git, ["add", "-A", "--", "."], "", cancellation);
            if (add.ExitCode != 0) return new(null, false, 0, Fail("git add", add));
            var staged = await Run(git, ["diff", "--cached", "--quiet"], "", cancellation);   // 1 = something is staged, 0 = nothing
            if (staged.ExitCode is not (0 or 1)) return new(null, false, 0, Fail("git diff --cached", staged));
            var text = staged.ExitCode == 1 && trailers is { Count: > 0 } ? WithTrailers(message, trailers) : message;

            var identityEnv = await IdentityEnvUnlocked(git, cancellation);
            var args = new List<string>(BaseOptions) { "commit", "-q" };
            if (author is not null) args.Add("--author=" + author);     // an explicit author (the memory store) beats the environment: command line wins in git
            if (allowEmpty) args.Add("--allow-empty");
            args.Add("-F"); args.Add("-");
            var commit = await Run(git, args, text, cancellation, identityEnv);
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
        if (git is null || !HasGitEntry()) return [];
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

    /// <summary>Every path that differs between the two ends of <paramref name="range"/> (e.g.
    /// <c>"abc123..def456"</c>), via <c>git diff --name-only</c>. Row 19, task 5c (P4): artifact
    /// authorship is read from the SPAWN'S WHOLE DIFF, not one commit, so a host that commits its own
    /// work mid-spawn (Codex) is still attributed correctly. Empty on failure or when git/the
    /// repository is unavailable, with <see cref="Reason"/> set.</summary>
    public async Task<IReadOnlyList<string>> ChangedFilesAsync(string range, CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !HasGitEntry()) return [];
        var r = await Run(git, ["diff", "--name-only", range], "", cancellation);
        if (r.ExitCode != 0) { Fail("git diff", r); return []; }
        Reason = null;
        return r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>The paths touched by exactly one commit, via <c>git show --name-only --pretty=format:</c>.
    /// Row 19, task 5c's fallback when there is no known "before" hash to diff a range against (the
    /// spawn's first ever commit in this room).</summary>
    public async Task<IReadOnlyList<string>> ChangedFilesInAsync(string commitHash, CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !HasGitEntry()) return [];
        var r = await Run(git, ["show", "--name-only", "--pretty=format:", commitHash], "", cancellation);
        if (r.ExitCode != 0) { Fail("git show", r); return []; }
        Reason = null;
        return r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

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

    // --- Row 35: worktree, merge and branch primitives ----------------------------------------------

    /// <summary>True when <see cref="Root"/> has at least one commit (HEAD is not unborn); false,
    /// quietly, otherwise or when git/the repository is unavailable.</summary>
    public async Task<bool> HasCommitsAsync(CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !HasGitEntry()) return false;
        var r = await Run(git, ["rev-parse", "--verify", "-q", "HEAD"], "", cancellation);
        return r.ExitCode == 0;
    }

    /// <summary>The branch checked out at <see cref="Root"/>, or null when HEAD is detached or unborn,
    /// or git/the repository is unavailable - none of those are failures worth a <see cref="Reason"/>.</summary>
    public async Task<string?> CurrentBranchAsync(CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !HasGitEntry()) return null;
        var r = await Run(git, ["symbolic-ref", "-q", "--short", "HEAD"], "", cancellation);
        return r.ExitCode == 0 ? r.StandardOutput.Trim() : null;
    }

    /// <summary>True when <paramref name="branch"/> exists as a local branch (`refs/heads/&lt;branch&gt;`).</summary>
    public async Task<bool> BranchExistsAsync(string branch, CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !HasGitEntry()) return false;
        var r = await Run(git, ["rev-parse", "--verify", "-q", "refs/heads/" + branch], "", cancellation);
        return r.ExitCode == 0;
    }

    /// <summary>True when <paramref name="branch"/>'s tip is an ancestor of HEAD - it has nothing left
    /// to merge (already contained).</summary>
    public async Task<bool> IsAncestorOfHeadAsync(string branch, CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !HasGitEntry()) return false;
        var r = await Run(git, ["merge-base", "--is-ancestor", "refs/heads/" + branch, "HEAD"], "", cancellation);
        return r.ExitCode == 0;
    }

    /// <summary>Adds a linked worktree at <paramref name="path"/> checked out to <paramref name="branch"/>
    /// - a new branch from HEAD when <paramref name="newBranch"/>, otherwise an existing one. Null on
    /// success, else the failure text (also left in <see cref="Reason"/>).</summary>
    public async Task<string?> AddWorktreeAsync(string path, string branch, bool newBranch, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var git = Resolve();
            if (git is null) return Reason;
            var full = Path.GetFullPath(path);
            var args = newBranch
                ? new List<string> { "worktree", "add", "-q", "-b", branch, full }
                : new List<string> { "worktree", "add", "-q", full, branch };
            var r = await Run(git, args, "", cancellation);
            if (r.ExitCode != 0) return Fail("git worktree add", r);
            Reason = null;
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes a linked worktree (never <c>--force</c>: a dirty worktree is refused, not
    /// discarded). Null on success, else the failure text.</summary>
    public async Task<string?> RemoveWorktreeAsync(string path, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var git = Resolve();
            if (git is null) return Reason;
            var r = await Run(git, ["worktree", "remove", Path.GetFullPath(path)], "", cancellation);
            if (r.ExitCode != 0) return Fail("git worktree remove", r);
            Reason = null;
            return null;
        }
        finally { _gate.Release(); }
    }

    private static readonly (string GitPath, string Name)[] InProgressMarkers =
    [
        ("MERGE_HEAD", "merge"),
        ("CHERRY_PICK_HEAD", "cherry-pick"),
        ("REVERT_HEAD", "revert"),
        ("rebase-merge", "rebase"),
        ("rebase-apply", "rebase"),
    ];

    /// <summary>The gated version of <see cref="OperationInProgressAsync"/>: a caller that already holds
    /// the gate (a commit, a merge) calls this instead of deadlocking on it.</summary>
    private async Task<string?> OperationInProgressUnlocked(ResolvedCli git, CancellationToken cancellation)
    {
        foreach (var (marker, name) in InProgressMarkers)
        {
            var p = await Run(git, ["rev-parse", "--git-path", marker], "", cancellation);
            if (p.ExitCode != 0) continue;
            var text = p.StandardOutput.Trim();
            var full = Path.IsPathRooted(text) ? text : Path.Combine(Root, text);
            if (File.Exists(full) || Directory.Exists(full)) return name;
        }
        var unmerged = await Run(git, ["ls-files", "-u"], "", cancellation);
        return unmerged.ExitCode == 0 && unmerged.StandardOutput.Trim().Length > 0 ? "unmerged paths" : null;
    }

    /// <summary>The short name of the repository operation in progress at <see cref="Root"/> (`"merge"`,
    /// `"cherry-pick"`, `"revert"`, `"rebase"`, `"unmerged paths"`), or null when none is. A commit or a
    /// merge over one of these would finalise it with conflict markers still in the tree (row 35).</summary>
    public async Task<string?> OperationInProgressAsync(CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !HasGitEntry()) return null;
        return await OperationInProgressUnlocked(git, cancellation);
    }

    /// <summary>Every worktree of the repository at <see cref="Root"/>, main tree first, via
    /// `worktree list --porcelain`. Empty on failure or when git/the repository is unavailable.</summary>
    public async Task<IReadOnlyList<string>> WorktreePathsAsync(CancellationToken cancellation = default)
    {
        var git = Resolve();
        if (git is null || !HasGitEntry()) return [];
        var r = await Run(git, ["worktree", "list", "--porcelain"], "", cancellation);
        if (r.ExitCode != 0) { Fail("git worktree list", r); return []; }
        Reason = null;
        var paths = new List<string>();
        foreach (var line in r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
                paths.Add(Path.GetFullPath(line["worktree ".Length..].Replace('/', '\\')));
        return paths;
    }

    /// <summary>Removes administrative data for exactly one worktree at <paramref name="path"/> - never
    /// every stale entry a blanket `worktree prune` would touch, which could also drop an owner's own
    /// registered worktree elsewhere in the same repository whose folder merely happens to be missing
    /// (an unmounted drive; row 35). Finds the admin directory under the repository's common
    /// git dir (`rev-parse --git-common-dir`) whose `worktrees/&lt;name&gt;/gitdir` file names
    /// <c>&lt;path&gt;\.git</c>, and deletes only that directory. Does nothing (quietly) when no such
    /// registration is found, or on a read/delete failure.</summary>
    public async Task PruneWorktreeAsync(string path, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var git = Resolve();
            if (git is null) return;
            var common = await Run(git, ["rev-parse", "--git-common-dir"], "", cancellation);
            if (common.ExitCode != 0) { Fail("git rev-parse --git-common-dir", common); return; }
            var commonText = common.StandardOutput.Trim();
            var commonDir = Path.IsPathRooted(commonText) ? commonText : Path.GetFullPath(Path.Combine(Root, commonText));
            var worktreesDir = Path.Combine(commonDir, "worktrees");
            if (!Directory.Exists(worktreesDir)) { Reason = null; return; }
            var target = Path.GetFullPath(Path.Combine(path, ".git"));
            foreach (var admin in Directory.GetDirectories(worktreesDir))
            {
                var gitdirFile = Path.Combine(admin, "gitdir");
                if (!File.Exists(gitdirFile)) continue;
                string text;
                try { text = File.ReadAllText(gitdirFile).Trim(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                var recorded = Path.IsPathRooted(text) ? Path.GetFullPath(text) : Path.GetFullPath(Path.Combine(admin, text));
                if (!string.Equals(recorded, target, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(admin, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Reason = $"could not remove the worktree registration at {admin}: {e.Message}";
                    Console.Error.WriteLine($"{LogName}: {Reason}");
                    return;
                }
                Reason = null;
                return;
            }
            Reason = null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Merges <paramref name="branch"/> into HEAD with `--no-ff`, committed under the identity
    /// rule (<see cref="ConfiguredIdentityAsync"/>). A conflicting or otherwise-failing merge is aborted before returning,
    /// so <see cref="Root"/>'s HEAD and working tree are exactly what they were before the call. Refuses
    /// (<see cref="MergeResult.Failed"/>) rather than merging when the tree already has uncommitted
    /// tracked changes right before the merge itself starts - checked from inside this same gated call,
    /// closing the window between a caller's own "commit the owner's edits first" step and the merge
    /// actually running, during which something else could dirty the tree again (row 35 review
    /// fix).</summary>
    public async Task<MergeOutcome> MergeAsync(string branch, string message, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var git = Resolve();
            if (git is null) return new(MergeResult.Failed, null, [], Reason, null);
            if (await OperationInProgressUnlocked(git, cancellation) is { } already)
                return new(MergeResult.Failed, null, [], $"a {already} is already in progress", null);
            var before = await HeadUnlocked(git, logFailure: false, cancellation);
            var status = await Run(git, ["status", "--porcelain", "--untracked-files=no"], "", cancellation);
            if (status.ExitCode == 0 && status.StandardOutput.Trim().Length > 0)
                return new(MergeResult.Failed, null, [], "the room directory has uncommitted changes", before);
            // Row 46: the merge is authored under the identity rule and its last paragraph carries
            // every distinct Co-authored-by line on the commits being merged, so the room's
            // first-parent history says who contributed without opening the branch.
            var trailers = await CoAuthorTrailersUnlocked(git, "HEAD.." + branch, logFailure: false, cancellation);
            var args = new List<string>(BaseOptions) { "merge", "--no-ff", "-m", message };
            if (trailers.Count > 0) { args.Add("-m"); args.Add(string.Join("\n", trailers)); }
            args.Add(branch);
            var r = await Run(git, args, "", cancellation, await IdentityEnvUnlocked(git, cancellation));
            if (r.ExitCode == 0)
            {
                Reason = null;
                return new(MergeResult.Merged, await HeadUnlocked(git, logFailure: true, cancellation), [], null, before);
            }
            var diff = await Run(git, ["diff", "--name-only", "--diff-filter=U"], "", cancellation);
            var paths = diff.ExitCode == 0
                ? diff.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : new List<string>();
            var head = await Run(git, ["rev-parse", "-q", "--verify", "MERGE_HEAD"], "", cancellation);
            if (head.ExitCode == 0)
            {
                var abort = await Run(git, ["merge", "--abort"], "", cancellation);
                if (abort.ExitCode != 0) return new(MergeResult.Failed, null, paths, Fail("git merge --abort", abort), before);
            }
            if (paths.Count > 0) { Reason = null; return new(MergeResult.Conflict, null, paths, null, before); }
            return new(MergeResult.Failed, null, [], Fail("git merge", r), before);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Deletes a fully-merged local branch (`branch -d`, never `-D`: an unmerged branch is
    /// refused, not force-deleted). Null on success, else the failure text.</summary>
    public async Task<string?> DeleteMergedBranchAsync(string branch, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var git = Resolve();
            if (git is null) return Reason;
            var r = await Run(git, ["branch", "-d", branch], "", cancellation);
            if (r.ExitCode != 0) return Fail("git branch -d", r);
            Reason = null;
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Aborts a merge left in progress at <see cref="Root"/> ONLY when it is the hub's own
    /// exchange merge - its message starts `Merge exchange #` and its `MERGE_HEAD` is the current tip
    /// of some `chopitup/*` branch - never an owner's own conflicted merge, which is left exactly alone
    /// (row 35). Null when nothing is in progress; otherwise `"aborted"`, the abort's failure
    /// text, or `"left alone"`.</summary>
    public async Task<string?> AbortStaleExchangeMergeAsync(CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var git = Resolve();
            if (git is null) return null;
            var head = await Run(git, ["rev-parse", "-q", "--verify", "MERGE_HEAD"], "", cancellation);
            if (head.ExitCode != 0) return null;
            var hash = head.StandardOutput.Trim();

            var message = "";
            var msgPath = await Run(git, ["rev-parse", "--git-path", "MERGE_MSG"], "", cancellation);
            if (msgPath.ExitCode == 0)
            {
                var text = msgPath.StandardOutput.Trim();
                var full = Path.IsPathRooted(text) ? text : Path.Combine(Root, text);
                if (File.Exists(full))
                {
                    // An unreadable message (locked, permission-denied) can never be proven the hub's
                    // own merge - treated as empty, which falls through to "left alone" below, exactly
                    // as if MERGE_MSG had no `Merge exchange #` prefix (row 35).
                    try { message = File.ReadAllText(full); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { message = ""; }
                }
            }
            var tips = await Run(git, ["for-each-ref", "--format=%(objectname)", "refs/heads/chopitup/"], "", cancellation);
            var isHub = message.StartsWith("Merge exchange #", StringComparison.Ordinal)
                && tips.ExitCode == 0
                && tips.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(hash, StringComparer.Ordinal);
            if (!isHub) return "left alone";

            var abort = await Run(git, ["merge", "--abort"], "", cancellation);
            if (abort.ExitCode != 0) return Fail("git merge --abort", abort);
            Reason = null;
            return "aborted";
        }
        finally { _gate.Release(); }
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

    private Task<ProcessResult> Run(ResolvedCli git, IReadOnlyList<string> args, string stdin, CancellationToken cancellation, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        // Env and HubIdentityEnv share no key, so Concat cannot throw on a duplicate.
        var env = extraEnv is null ? Env : new Dictionary<string, string>(Env).Concat(extraEnv).ToDictionary(kv => kv.Key, kv => kv.Value);
        return _runner.RunAsync(new ProcessSpec(git.FileName, [.. git.LeadingArguments, .. args], env, Root, stdin, LogName + "-git"), Timeout, cancellation);
    }

    private string Fail(string step, ProcessResult r)
    {
        Reason = $"{step} exited {(r.ExitCode?.ToString() ?? "killed")}: {r.StandardError.Trim()}";
        Console.Error.WriteLine($"{LogName}: {Reason}");
        return Reason;
    }
}
