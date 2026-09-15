using ChopItUp.Hub.Git;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Git;

public sealed class GitTrailTests : IDisposable
{
    private static readonly GitIdentity Opus = new("Opus", "opus@chopitup.local");
    private static readonly GitIdentity Owner = new("Owner", "owner@chopitup.local");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_trail_" + Guid.NewGuid().ToString("N"));

    public GitTrailTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        TestDirs.DeleteTree(_dir);
        TestDirs.DeleteTree(_dir + "_wt");
        TestDirs.DeleteTree(_dir + "_owner");
    }

    private static async Task<string> GitOut(string dir, params string[] args)
    {
        var r = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, args, new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        return r.StandardOutput;
    }

    // A raw git call for setup that a GitTrail method does not cover (checkout, an in-progress merge or
    // cherry-pick left conflicted on purpose): carries a committer identity so it works on a machine
    // with no git config, and does not assert on the exit code - several callers expect a conflict.
    private static Task<ProcessResult> RawGit(string dir, params string[] args)
    {
        var withIdentity = new[] { "-c", "user.name=Test", "-c", "user.email=test@chopitup.local", "-c", "commit.gpgsign=false" }.Concat(args).ToArray();
        return new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, withIdentity, new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    // A repository with one commit, ready to grow worktrees off of.
    private static async Task<GitTrail> RepoWithCommit(string dir, IProcessRunner? runner = null)
    {
        Directory.CreateDirectory(dir);
        var git = new GitTrail(dir, runner: runner);
        File.WriteAllText(Path.Combine(dir, "seed.txt"), "seed");
        var seed = await git.CommitAllAsync("seed", Owner, allowEmpty: false);
        Assert.True(seed.Created);
        return git;
    }

    private sealed class ConcurrencyTrackingRunner : IProcessRunner
    {
        private readonly IProcessRunner _inner = new ProcessRunner();
        private readonly object _lock = new();
        private int _current;
        public int Peak { get; private set; }

        public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation)
        {
            lock (_lock) { _current++; if (_current > Peak) Peak = _current; }
            try { return await _inner.RunAsync(spec, timeout, cancellation); }
            finally { lock (_lock) { _current--; } }
        }
    }

    [Fact]
    public async Task M9_A9_commit_all_splits_author_from_the_hub_committer_and_counts_the_files()
    {
        var git = new GitTrail(_dir);
        Assert.True(git.IsAvailable());
        Assert.Null(await git.HeadAsync());
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "b");

        var first = await git.CommitAllAsync("opus: turn 1/4 in room lab\n\nShell commands run (1):\n  1. dir\n", Opus, allowEmpty: false);
        Assert.True(first.Created);
        Assert.Equal(2, first.FilesChanged);
        Assert.Matches("^[0-9a-f]{7,}$", first.Hash);
        Assert.Null(first.Reason);
        Assert.Equal(first.Hash, await git.HeadAsync());

        var line = (await GitOut(_dir, "log", "-1", "--format=%an <%ae>|%cn <%ce>|%s")).Trim();
        Assert.Equal("Opus <opus@chopitup.local>|ChopItUp hub <hub@chopitup.local>|opus: turn 1/4 in room lab", line);
        var body = await GitOut(_dir, "log", "-1", "--format=%B");
        Assert.Contains("Shell commands run (1):", body);
        Assert.Contains("  1. dir", body);
    }

    [Fact]
    public async Task M9_A9_allow_empty_makes_a_commit_with_no_files_and_without_it_nothing_to_commit_returns_head_uncreated()
    {
        var git = new GitTrail(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        var first = await git.CommitAllAsync("owner: edits", Owner, allowEmpty: false);

        var none = await git.CommitAllAsync("nothing", Owner, allowEmpty: false);
        Assert.False(none.Created);
        Assert.Equal(first.Hash, none.Hash);
        Assert.Null(none.Reason);
        Assert.Null(git.Reason);

        var empty = await git.CommitAllAsync("sonnet: turn 2/4 in room lab", Opus, allowEmpty: true);
        Assert.True(empty.Created);
        Assert.Equal(0, empty.FilesChanged);
        Assert.NotEqual(first.Hash, empty.Hash);
        Assert.Equal(2, (await git.LogAsync(10)).Count);
    }

    [Fact]
    public async Task M9_A8_dirty_is_false_before_init_false_when_clean_and_true_for_untracked_or_modified_files()
    {
        var git = new GitTrail(_dir);
        Assert.False(await git.IsDirtyAsync());              // not a repository yet
        Assert.True(await git.InitAsync());
        Assert.True(Directory.Exists(Path.Combine(_dir, ".git")));
        Assert.False(await git.IsDirtyAsync());              // empty repository, nothing to report
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        Assert.True(await git.IsDirtyAsync());               // untracked counts
        await git.CommitAllAsync("owner: edits", Owner, allowEmpty: false);
        Assert.False(await git.IsDirtyAsync());
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "changed");
        Assert.True(await git.IsDirtyAsync());
        Assert.True(await git.InitAsync());                  // idempotent
    }

    [Fact]
    public async Task M9_A2_toplevel_is_null_outside_a_repository_the_root_inside_one_and_the_same_root_from_a_subfolder()
    {
        var git = new GitTrail(_dir);
        Assert.Null(await git.TopLevelAsync());
        Assert.True(await git.InitAsync());
        Assert.Equal(Path.GetFullPath(_dir), await git.TopLevelAsync());

        var sub = Path.Combine(_dir, "sub", "deeper");
        Directory.CreateDirectory(sub);
        Assert.Equal(Path.GetFullPath(_dir), await new GitTrail(sub).TopLevelAsync());
        Assert.Null(await new GitTrail(Path.Combine(_dir, "missing")).TopLevelAsync());
    }

    [Fact]
    public async Task M9_A11_log_is_newest_first_parsed_and_limited_and_empty_before_the_first_commit()
    {
        var git = new GitTrail(_dir);
        Assert.Empty(await git.LogAsync(20));                // not a repository
        await git.InitAsync();
        Assert.Empty(await git.LogAsync(20));                // unborn HEAD, no failure
        Assert.Null(git.Reason);

        for (int i = 1; i <= 3; i++)
        {
            File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), i.ToString());
            await git.CommitAllAsync($"opus: turn {i}/4 in room lab", Opus, allowEmpty: false);
        }
        var log = await git.LogAsync(2);
        Assert.Equal(2, log.Count);
        Assert.Equal("opus: turn 3/4 in room lab", log[0].Subject);
        Assert.Equal("opus: turn 2/4 in room lab", log[1].Subject);
        Assert.Equal("Opus <opus@chopitup.local>", log[0].Author);
        Assert.Matches("^[0-9a-f]{7,}$", log[0].Hash);
        Assert.True(log[0].At >= log[1].At);
        Assert.True((DateTimeOffset.UtcNow - log[0].At).Duration() < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task M9_A9_a_long_multi_line_message_survives_because_it_travels_on_stdin()
    {
        var git = new GitTrail(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        var lines = Enumerable.Range(1, 60).Select(i => $"  {i}. {new string('x', 350)}");
        var message = "opus: turn 1/4 in room lab\n\nShell commands run (60):\n" + string.Join('\n', lines) + "\n";
        Assert.True(message.Length > 20_000);

        var outcome = await git.CommitAllAsync(message, Opus, allowEmpty: false);
        Assert.True(outcome.Created);
        var body = await GitOut(_dir, "log", "-1", "--format=%B");
        Assert.Contains("  60. xxx", body);
        Assert.Contains("Shell commands run (60):", body);
    }

    [Fact]
    public async Task M9_A9_without_git_every_call_is_quiet_and_reasoned_and_nothing_is_created()
    {
        var git = new GitTrail(_dir, () => throw new FileNotFoundException("'git' was not found on PATH"));
        Assert.False(git.IsAvailable());
        Assert.Contains("git is not available", git.Reason);
        Assert.False(await git.InitAsync());
        Assert.Null(await git.TopLevelAsync());
        Assert.Null(await git.HeadAsync());
        Assert.False(await git.IsDirtyAsync());
        Assert.Empty(await git.LogAsync(5));
        var outcome = await git.CommitAllAsync("x", Opus, allowEmpty: true);
        Assert.Null(outcome.Hash);
        Assert.False(outcome.Created);
        Assert.Contains("git is not available", outcome.Reason);
        Assert.False(Directory.Exists(Path.Combine(_dir, ".git")));
    }

    [Fact]
    public async Task M9_A9_a_failing_git_command_names_the_step_and_stderr()
    {
        var git = new GitTrail(_dir, () => new ResolvedCli("fake-git.exe", [], "fake-git.exe"), new FailingRunner());
        var outcome = await git.CommitAllAsync("x", Opus, allowEmpty: false);
        Assert.Null(outcome.Hash);
        Assert.StartsWith("git init exited 128: boom", outcome.Reason);
        Assert.StartsWith("git init exited 128: boom", git.Reason);
    }

    // --- Task 5c (row 19): artifact authorship, from the spawn's whole diff -----------------------

    [Fact]
    public async Task Run05_ChangedFilesAsync_lists_every_path_across_a_range_where_head_moved_mid_spawn()
    {
        var git = new GitTrail(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        var first = await git.CommitAllAsync("first", Owner, allowEmpty: false);
        var headBefore = first.Hash;

        // Simulates a spawn that commits its own work (Codex) BEFORE the hub's own after-commit -
        // the scenario P4 exists for: authorship must come from the WHOLE range, not the last commit.
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "b");
        await git.CommitAllAsync("agent's own commit", Opus, allowEmpty: false);
        File.WriteAllText(Path.Combine(_dir, "c.txt"), "c");
        var agent = await git.CommitAllAsync("hub's after-commit", Opus, allowEmpty: true);

        var changed = await git.ChangedFilesAsync($"{headBefore}..{agent.Hash}");
        Assert.Equal(["b.txt", "c.txt"], changed.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Run05_ChangedFilesInAsync_lists_one_commits_paths_for_the_no_before_hash_fallback()
    {
        var git = new GitTrail(_dir);
        File.WriteAllText(Path.Combine(_dir, "x.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "y.txt"), "y");
        var first = await git.CommitAllAsync("first ever commit", Opus, allowEmpty: false);

        var changed = await git.ChangedFilesInAsync(first.Hash!);
        Assert.Equal(["x.txt", "y.txt"], changed.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Run05_ChangedFilesAsync_is_empty_with_a_reason_when_the_range_is_bad()
    {
        var git = new GitTrail(_dir);
        await git.InitAsync();
        var changed = await git.ChangedFilesAsync("nonexistent..alsonone");
        Assert.Empty(changed);
        Assert.NotNull(git.Reason);
    }

    [Fact]
    public void M9_A9_room_trails_hand_out_one_trail_per_normalised_directory()
    {
        var trails = new RoomTrails(dir => new GitTrail(dir));
        var a = trails.For(@"C:\Rooms\lab\");
        var b = trails.For(@"c:\rooms\LAB");
        Assert.Same(a, b);
        Assert.NotSame(a, trails.For(@"C:\Rooms\other"));
    }

    // --- Row 35 Task 1: worktree, merge and branch primitives --------------------------------------

    [Fact]
    public async Task Worktree_trail_sees_dirt_and_commits_on_its_branch_without_reinitialising()
    {
        var main = await RepoWithCommit(_dir);
        var headBefore = await main.HeadAsync();
        var wt = Path.Combine(_dir + "_wt", "x1");
        Directory.CreateDirectory(_dir + "_wt");

        Assert.Null(await main.AddWorktreeAsync(wt, "chopitup/x1", newBranch: true));
        var w = main.WithRoot(wt);
        File.WriteAllText(Path.Combine(wt, "edit.txt"), "edit");
        Assert.True(await w.IsDirtyAsync());
        var commit = await w.CommitAllAsync("edit in worktree", Owner, allowEmpty: false);
        Assert.True(commit.Created);
        Assert.Equal("chopitup/x1", await w.CurrentBranchAsync());
        Assert.True(File.Exists(Path.Combine(wt, ".git")));
        Assert.False(Directory.Exists(Path.Combine(wt, ".git")));
        Assert.Equal(headBefore, await main.HeadAsync());
    }

    [Fact]
    public async Task Merge_of_a_clean_branch_creates_a_no_ff_merge_commit_and_the_branch_deletes()
    {
        var main = await RepoWithCommit(_dir);
        var wt = Path.Combine(_dir + "_wt", "x2");
        Directory.CreateDirectory(_dir + "_wt");
        await main.AddWorktreeAsync(wt, "chopitup/x2", newBranch: true);
        var w = main.WithRoot(wt);
        File.WriteAllText(Path.Combine(wt, "feature.txt"), "feature");
        await w.CommitAllAsync("feature work", Owner, allowEmpty: false);

        var outcome = await main.MergeAsync("chopitup/x2", "Merge exchange #2 (lab)");
        Assert.Equal(MergeResult.Merged, outcome.Result);
        Assert.NotNull(outcome.Hash);
        var parents = (await GitOut(_dir, "rev-list", "--parents", "-n", "1", "HEAD")).Trim().Split(' ');
        Assert.Equal(3, parents.Length);

        Assert.Null(await main.RemoveWorktreeAsync(wt));
        Assert.Null(await main.DeleteMergedBranchAsync("chopitup/x2"));
        Assert.False(await main.BranchExistsAsync("chopitup/x2"));
    }

    [Fact]
    public async Task A_conflicting_merge_is_aborted_and_names_the_paths()
    {
        var main = await RepoWithCommit(_dir);
        Directory.CreateDirectory(_dir + "_wt");
        var wtA = Path.Combine(_dir + "_wt", "xA");
        var wtB = Path.Combine(_dir + "_wt", "xB");
        await main.AddWorktreeAsync(wtA, "chopitup/xA", newBranch: true);
        await main.AddWorktreeAsync(wtB, "chopitup/xB", newBranch: true);
        File.WriteAllText(Path.Combine(wtA, "c.txt"), "from A");
        await main.WithRoot(wtA).CommitAllAsync("A edits c.txt", Owner, allowEmpty: false);
        File.WriteAllText(Path.Combine(wtB, "c.txt"), "from B");
        await main.WithRoot(wtB).CommitAllAsync("B edits c.txt", Owner, allowEmpty: false);

        var first = await main.MergeAsync("chopitup/xA", "Merge exchange #A (lab)");
        Assert.Equal(MergeResult.Merged, first.Result);
        var headAfterFirst = await main.HeadAsync();

        var second = await main.MergeAsync("chopitup/xB", "Merge exchange #B (lab)");
        Assert.Equal(MergeResult.Conflict, second.Result);
        Assert.Equal(["c.txt"], second.Conflicts);
        Assert.False(await main.IsDirtyAsync());
        Assert.Equal(headAfterFirst, await main.HeadAsync());
        Assert.False(File.Exists(Path.Combine(_dir, ".git", "MERGE_HEAD")));
        Assert.True(await main.BranchExistsAsync("chopitup/xB"));
    }

    [Fact]
    public async Task Remove_refuses_a_dirty_worktree_and_delete_refuses_an_unmerged_branch()
    {
        var main = await RepoWithCommit(_dir);
        var wt = Path.Combine(_dir + "_wt", "xD");
        Directory.CreateDirectory(_dir + "_wt");
        await main.AddWorktreeAsync(wt, "chopitup/xD", newBranch: true);
        File.WriteAllText(Path.Combine(wt, "committed.txt"), "committed");
        await main.WithRoot(wt).CommitAllAsync("branch-only work", Owner, allowEmpty: false);
        File.WriteAllText(Path.Combine(wt, "dirty.txt"), "dirty");

        Assert.NotNull(await main.RemoveWorktreeAsync(wt));   // dirty: refused, never forced

        File.Delete(Path.Combine(wt, "dirty.txt"));
        Assert.Null(await main.RemoveWorktreeAsync(wt));      // clean now: removed
        Assert.NotNull(await main.DeleteMergedBranchAsync("chopitup/xD"));   // unmerged: refused, never forced
    }

    [Fact]
    public async Task A_leftover_merge_head_is_never_committed()
    {
        var main = await RepoWithCommit(_dir);
        var wt = Path.Combine(_dir + "_wt", "xM");
        Directory.CreateDirectory(_dir + "_wt");
        await main.AddWorktreeAsync(wt, "chopitup/xM", newBranch: true);
        File.WriteAllText(Path.Combine(wt, "c.txt"), "branch");
        await main.WithRoot(wt).CommitAllAsync("branch edits c.txt", Owner, allowEmpty: false);
        File.WriteAllText(Path.Combine(_dir, "c.txt"), "main");
        await main.CommitAllAsync("main edits c.txt", Owner, allowEmpty: false);

        var merge = await RawGit(_dir, "merge", "--no-ff", "chopitup/xM");
        Assert.NotEqual(0, merge.ExitCode);
        Assert.True(File.Exists(Path.Combine(_dir, ".git", "MERGE_HEAD")));

        var headBefore = await main.HeadAsync();
        var outcome = await main.CommitAllAsync("finalize", Owner, allowEmpty: false);
        Assert.Null(outcome.Hash);
        Assert.Contains("merge", outcome.Reason);
        Assert.Contains("merge", main.Reason);
        Assert.True(File.Exists(Path.Combine(_dir, ".git", "MERGE_HEAD")));
        Assert.Equal(headBefore, await main.HeadAsync());
    }

    [Fact]
    public async Task A_cherry_pick_in_progress_is_never_committed()
    {
        var main = await RepoWithCommit(_dir);
        var wt = Path.Combine(_dir + "_wt", "xC");
        Directory.CreateDirectory(_dir + "_wt");
        await main.AddWorktreeAsync(wt, "chopitup/xC", newBranch: true);
        File.WriteAllText(Path.Combine(wt, "c.txt"), "branch");
        await main.WithRoot(wt).CommitAllAsync("branch edits c.txt", Owner, allowEmpty: false);
        var branchCommit = (await GitOut(wt, "rev-parse", "HEAD")).Trim();
        File.WriteAllText(Path.Combine(_dir, "c.txt"), "main");
        await main.CommitAllAsync("main edits c.txt", Owner, allowEmpty: false);

        var pick = await RawGit(_dir, "cherry-pick", branchCommit);
        Assert.NotEqual(0, pick.ExitCode);
        Assert.True(File.Exists(Path.Combine(_dir, ".git", "CHERRY_PICK_HEAD")));

        var headBefore = await main.HeadAsync();
        var outcome = await main.CommitAllAsync("finalize", Owner, allowEmpty: false);
        Assert.Null(outcome.Hash);
        Assert.Contains("cherry-pick", outcome.Reason);
        Assert.True(File.Exists(Path.Combine(_dir, ".git", "CHERRY_PICK_HEAD")));
        Assert.Equal(headBefore, await main.HeadAsync());
    }

    [Fact]
    public async Task Stale_abort_aborts_only_the_hubs_exchange_merge()
    {
        // The hub's own exchange merge, left conflicted: aborted, tree returns exactly clean.
        var main = await RepoWithCommit(_dir);
        var wt = Path.Combine(_dir + "_wt", "x4");
        Directory.CreateDirectory(_dir + "_wt");
        await main.AddWorktreeAsync(wt, "chopitup/x4", newBranch: true);
        File.WriteAllText(Path.Combine(wt, "c.txt"), "branch");
        await main.WithRoot(wt).CommitAllAsync("branch edits c.txt", Owner, allowEmpty: false);
        File.WriteAllText(Path.Combine(_dir, "c.txt"), "main");
        await main.CommitAllAsync("main edits c.txt", Owner, allowEmpty: false);
        await RawGit(_dir, "merge", "--no-ff", "-m", "Merge exchange #4 (lab)", "chopitup/x4");
        Assert.True(File.Exists(Path.Combine(_dir, ".git", "MERGE_HEAD")));

        Assert.Equal("aborted", await main.AbortStaleExchangeMergeAsync());
        Assert.False(File.Exists(Path.Combine(_dir, ".git", "MERGE_HEAD")));
        Assert.False(await main.IsDirtyAsync());

        // An owner's own conflicted merge of a plain branch is left exactly alone.
        var ownerDir = _dir + "_owner";
        Directory.CreateDirectory(ownerDir);
        var ownerMain = new GitTrail(ownerDir);
        File.WriteAllText(Path.Combine(ownerDir, "c.txt"), "base");
        Assert.True((await ownerMain.CommitAllAsync("base", Owner, allowEmpty: false)).Created);
        var trunk = (await GitOut(ownerDir, "symbolic-ref", "--short", "HEAD")).Trim();
        Assert.Equal(0, (await RawGit(ownerDir, "checkout", "-q", "-b", "feature")).ExitCode);
        File.WriteAllText(Path.Combine(ownerDir, "c.txt"), "feature");
        Assert.Equal(0, (await RawGit(ownerDir, "commit", "-q", "-am", "feature edits c.txt")).ExitCode);
        Assert.Equal(0, (await RawGit(ownerDir, "checkout", "-q", trunk)).ExitCode);
        File.WriteAllText(Path.Combine(ownerDir, "c.txt"), "trunk");
        Assert.Equal(0, (await RawGit(ownerDir, "commit", "-q", "-am", "trunk edits c.txt")).ExitCode);
        var ownerMerge = await RawGit(ownerDir, "merge", "--no-ff", "feature");
        Assert.NotEqual(0, ownerMerge.ExitCode);
        Assert.True(File.Exists(Path.Combine(ownerDir, ".git", "MERGE_HEAD")));
        File.WriteAllText(Path.Combine(ownerDir, "c.txt"), "resolved by hand");   // an unstaged resolution edit

        Assert.Equal("left alone", await ownerMain.AbortStaleExchangeMergeAsync());
        Assert.True(File.Exists(Path.Combine(ownerDir, ".git", "MERGE_HEAD")));
        Assert.Equal("resolved by hand", File.ReadAllText(Path.Combine(ownerDir, "c.txt")));
    }

    [Fact]
    public async Task WithRoot_shares_the_gate()
    {
        var runner = new ConcurrencyTrackingRunner();
        var main = await RepoWithCommit(_dir, runner);
        var wt = Path.Combine(_dir + "_wt", "xG");
        Directory.CreateDirectory(_dir + "_wt");
        Assert.Null(await main.AddWorktreeAsync(wt, "chopitup/xG", newBranch: true));
        var w = main.WithRoot(wt);

        var tasks = new List<Task<CommitOutcome>>();
        for (int i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Combine(_dir, $"m{i}.txt"), i.ToString());
            tasks.Add(main.CommitAllAsync($"main {i}", Owner, allowEmpty: true));
        }
        for (int i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Combine(wt, $"w{i}.txt"), i.ToString());
            tasks.Add(w.CommitAllAsync($"wt {i}", Owner, allowEmpty: true));
        }
        await Task.WhenAll(tasks);

        Assert.Equal(1, runner.Peak);
    }

    private sealed class FailingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation) =>
            Task.FromResult(new ProcessResult(128, false, false, "", "boom\n", TimeSpan.Zero));
    }
}
