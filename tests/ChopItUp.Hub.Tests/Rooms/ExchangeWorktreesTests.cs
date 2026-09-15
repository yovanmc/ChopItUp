using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Rooms;

public sealed class ExchangeWorktreesTests : IDisposable
{
    private static readonly GitIdentity Owner = new("Owner", "owner@chopitup.local");
    // A fabricated machine (as RoomPathsTests): nothing here reads the real profile or environment, so
    // the worktree paths under the test's own temp root are never refused by accident.
    private static readonly RoomPathRules Rules = RoomPathRules.ForHub(
        dataDir: @"C:\hub\data", installDir: @"C:\hub\app", userProfile: @"C:\Users\hub-owner",
        getEnv: _ => null);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_xwt_" + Guid.NewGuid().ToString("N"));
    private readonly MessageStore _store;
    private readonly RoomTrails _trails = new(dir => new GitTrail(dir));
    private readonly ExchangeWorktrees _worktrees;

    public ExchangeWorktreesTests()
    {
        Directory.CreateDirectory(_dir);
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        _store = new MessageStore(db);
        _worktrees = new ExchangeWorktrees(_store, _trails, Rules);
    }

    public void Dispose() => TestDirs.DeleteTree(_dir);

    private string Room(string id)
    {
        var dir = Path.Combine(_dir, "rooms", id);
        Directory.CreateDirectory(dir);
        _store.CreateRoom(id, id, dir);
        return dir;
    }

    private async Task<string> RoomWithCommit(string id)
    {
        var dir = Room(id);
        File.WriteAllText(Path.Combine(dir, "seed.txt"), "seed");
        var seed = await _trails.For(dir).CommitAllAsync("seed", Owner, allowEmpty: false);
        Assert.True(seed.Created);
        return dir;
    }

    private static async Task<string> GitOut(string dir, params string[] args)
    {
        var r = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, args, new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        return r.StandardOutput;
    }

    // A raw git call for setup that no GitTrail method covers (checkout, an in-progress merge or
    // cherry-pick left conflicted on purpose); carries a committer identity so it works on a machine
    // with no git config, and does not assert on the exit code - several callers expect a conflict.
    private static Task<ProcessResult> RawGit(string dir, params string[] args)
    {
        var withIdentity = new[] { "-c", "user.name=Test", "-c", "user.email=test@chopitup.local", "-c", "commit.gpgsign=false" }.Concat(args).ToArray();
        return new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, withIdentity, new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    private static ExchangeWorktrees.CloseRequest Close(string dir, long root, string roomId, ExchangeStatus status,
        bool leased = true, bool interrupted = false, bool runOwnsRoom = false) =>
        new(dir, root, roomId, status, leased, interrupted, runOwnsRoom, Owner, $"owner: edits before the next spawn in room {roomId}\n");

    [Fact]
    public async Task Ensure_creates_the_branch_worktree_beside_the_room_and_is_idempotent()
    {
        var dir = await RoomWithCommit("lab");
        var lease = await _worktrees.EnsureAsync(dir, 7, CancellationToken.None);
        Assert.Null(lease.Refusal);
        Assert.Equal(ExchangeWorktrees.PathFor(dir, 7), lease.Path);
        Assert.True(Directory.Exists(lease.Path));
        Assert.True(await _trails.For(dir).BranchExistsAsync("chopitup/x7"));

        var again = await _worktrees.EnsureAsync(dir, 7, CancellationToken.None);
        Assert.Null(again.Refusal);
        Assert.Equal(lease.Path, again.Path);
    }

    [Fact]
    public async Task Ensure_makes_a_start_commit_in_an_unborn_room()
    {
        var dir = Room("unborn");
        Assert.Null(await _trails.For(dir).HeadAsync());

        var lease = await _worktrees.EnsureAsync(dir, 1, CancellationToken.None);
        Assert.Null(lease.Refusal);
        Assert.NotNull(await _trails.For(dir).HeadAsync());
        var log = await _trails.For(dir).LogAsync(5);
        Assert.Contains(log, c => c.Subject == "Room trail start");
    }

    [Fact]
    public async Task Ensure_refuses_when_the_branch_already_exists()
    {
        var dir = await RoomWithCommit("lab2");
        var git = _trails.For(dir);
        var tmp = Path.Combine(_dir, "tmp-branch-holder");
        Assert.Null(await git.AddWorktreeAsync(tmp, ExchangeWorktrees.Branch(9), newBranch: true));
        Assert.Null(await git.RemoveWorktreeAsync(tmp));
        Assert.True(await git.BranchExistsAsync(ExchangeWorktrees.Branch(9)));

        var lease = await _worktrees.EnsureAsync(dir, 9, CancellationToken.None);
        Assert.Null(lease.Path);
        Assert.Contains("already exists", lease.Refusal);
        Assert.False(Directory.Exists(ExchangeWorktrees.PathFor(dir, 9)));
        var registered = await git.WorktreePathsAsync();
        Assert.DoesNotContain(registered, p => RoomPaths.Same(p, ExchangeWorktrees.PathFor(dir, 9)));
    }

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

    [Fact]
    public async Task Close_merges_a_concluded_exchange_and_deletes_branch_and_worktree()
    {
        var dir = await RoomWithCommit("lab3");
        var lease = await _worktrees.EnsureAsync(dir, 7, CancellationToken.None);
        File.WriteAllText(Path.Combine(lease.Path!, "feature.txt"), "feature");
        await _trails.ForWorktree(dir, lease.Path!).CommitAllAsync("agent work", Owner, allowEmpty: false);

        var note = await _worktrees.CloseAsync(Close(dir, 7, "lab3", ExchangeStatus.Concluded), CancellationToken.None);
        Assert.NotNull(note);
        Assert.StartsWith("Exchange #7 merged into", note);
        Assert.False(await _trails.For(dir).BranchExistsAsync("chopitup/x7"));
        Assert.False(Directory.Exists(lease.Path));
    }

    [Fact]
    public async Task Close_keeps_the_branch_of_a_stopped_exchange()
    {
        var dir = await RoomWithCommit("lab4");
        await _worktrees.EnsureAsync(dir, 3, CancellationToken.None);
        var note = await _worktrees.CloseAsync(Close(dir, 3, "lab4", ExchangeStatus.Stopped), CancellationToken.None);
        Assert.Contains("was not merged: it was stopped", note);
        Assert.Contains("chopitup/x3", note);
        Assert.True(await _trails.For(dir).BranchExistsAsync("chopitup/x3"));
    }

    [Fact]
    public async Task Close_keeps_the_branch_when_a_run_owns_the_room()
    {
        var dir = await RoomWithCommit("lab5");
        await _worktrees.EnsureAsync(dir, 4, CancellationToken.None);
        var note = await _worktrees.CloseAsync(Close(dir, 4, "lab5", ExchangeStatus.Concluded, runOwnsRoom: true), CancellationToken.None);
        Assert.Contains("a run owns this room", note);
        Assert.True(await _trails.For(dir).BranchExistsAsync("chopitup/x4"));
    }

    [Fact]
    public async Task Close_keeps_the_branch_when_head_is_detached()
    {
        var dir = await RoomWithCommit("lab6");
        await _worktrees.EnsureAsync(dir, 5, CancellationToken.None);
        var head = await _trails.For(dir).HeadAsync();
        Assert.Equal(0, (await RawGit(dir, "checkout", "-q", head!)).ExitCode);

        var note = await _worktrees.CloseAsync(Close(dir, 5, "lab6", ExchangeStatus.Concluded), CancellationToken.None);
        Assert.Contains("no branch checked out", note);
        Assert.True(await _trails.For(dir).BranchExistsAsync("chopitup/x5"));
    }

    [Fact]
    public async Task Close_on_conflict_aborts_and_names_branch_and_paths()
    {
        var dir = await RoomWithCommit("lab7");
        var lease = await _worktrees.EnsureAsync(dir, 8, CancellationToken.None);
        File.WriteAllText(Path.Combine(lease.Path!, "c.txt"), "branch");
        await _trails.ForWorktree(dir, lease.Path!).CommitAllAsync("branch edits c.txt", Owner, allowEmpty: false);
        File.WriteAllText(Path.Combine(dir, "c.txt"), "room");
        await _trails.For(dir).CommitAllAsync("room edits c.txt", Owner, allowEmpty: false);
        var headBefore = await _trails.For(dir).HeadAsync();

        var note = await _worktrees.CloseAsync(Close(dir, 8, "lab7", ExchangeStatus.Concluded), CancellationToken.None);
        Assert.Contains("chopitup/x8", note);
        Assert.Contains("c.txt", note);
        Assert.Equal(headBefore, await _trails.For(dir).HeadAsync());
        var status = await GitOut(dir, "status", "--porcelain");
        Assert.Equal("", status.Trim());
        Assert.True(await _trails.For(dir).BranchExistsAsync("chopitup/x8"));
    }

    [Fact]
    public async Task Close_commits_owner_edits_in_the_room_before_merging()
    {
        var dir = await RoomWithCommit("lab8");
        var lease = await _worktrees.EnsureAsync(dir, 6, CancellationToken.None);
        File.WriteAllText(Path.Combine(lease.Path!, "agent.txt"), "agent edit");
        await _trails.ForWorktree(dir, lease.Path!).CommitAllAsync("agent work", Owner, allowEmpty: false);
        File.WriteAllText(Path.Combine(dir, "owner.txt"), "owner edit");   // dirty room tree at close time

        var note = await _worktrees.CloseAsync(Close(dir, 6, "lab8", ExchangeStatus.Concluded), CancellationToken.None);
        Assert.StartsWith("Exchange #6 merged into", note);
        var log = await _trails.For(dir).LogAsync(5);
        Assert.Equal("owner: edits before the next spawn in room lab8", log[1].Subject);   // right below the merge commit
        Assert.True(File.Exists(Path.Combine(dir, "owner.txt")));
        Assert.True(File.Exists(Path.Combine(dir, "agent.txt")));
    }

    [Fact]
    public async Task Recover_commits_removes_and_names_the_branches_then_is_quiet_on_the_next_start()
    {
        var dir = await RoomWithCommit("lab9");
        var lease = await _worktrees.EnsureAsync(dir, 2, CancellationToken.None);
        File.WriteAllText(Path.Combine(lease.Path!, "dirty.txt"), "dirty");   // as if the hub died mid-spawn

        var note = await _worktrees.RecoverAsync(dir, CancellationToken.None);
        Assert.NotNull(note);
        Assert.Contains("chopitup/x2", note);
        Assert.False(Directory.Exists(lease.Path));
        var git = _trails.For(dir);
        Assert.True(await git.BranchExistsAsync("chopitup/x2"));
        // The dirty edit was committed onto the exchange's own (kept, unmerged) branch, not onto the
        // room directory's checked-out branch - recovery never merges.
        var branchSubject = (await GitOut(dir, "log", "-1", "--format=%s", "chopitup/x2")).Trim();
        Assert.Equal("Uncommitted when the hub restarted", branchSubject);

        Assert.Null(await _worktrees.RecoverAsync(dir, CancellationToken.None));
    }

    [Fact]
    public async Task Recover_aborts_the_hubs_merge_and_leaves_an_owner_merge_alone()
    {
        // The hub's own exchange merge, left conflicted at hub restart: aborted by recovery.
        var dir = await RoomWithCommit("lab10");
        var lease = await _worktrees.EnsureAsync(dir, 11, CancellationToken.None);
        File.WriteAllText(Path.Combine(lease.Path!, "c.txt"), "branch");
        await _trails.ForWorktree(dir, lease.Path!).CommitAllAsync("branch edits c.txt", Owner, allowEmpty: false);
        File.WriteAllText(Path.Combine(dir, "c.txt"), "room");
        await _trails.For(dir).CommitAllAsync("room edits c.txt", Owner, allowEmpty: false);
        await RawGit(dir, "merge", "--no-ff", "-m", "Merge exchange #11 (lab10)", "chopitup/x11");
        Assert.True(File.Exists(Path.Combine(dir, ".git", "MERGE_HEAD")));

        var note = await _worktrees.RecoverAsync(dir, CancellationToken.None);
        Assert.StartsWith("The last hub's exchange merge was aborted.", note!);
        Assert.False(File.Exists(Path.Combine(dir, ".git", "MERGE_HEAD")));

        // An owner's own conflicted merge in a different room is left exactly alone.
        var dir2 = await RoomWithCommit("lab11");
        var trunk = (await GitOut(dir2, "symbolic-ref", "--short", "HEAD")).Trim();
        Assert.Equal(0, (await RawGit(dir2, "checkout", "-q", "-b", "feature")).ExitCode);
        File.WriteAllText(Path.Combine(dir2, "seed.txt"), "feature");
        Assert.Equal(0, (await RawGit(dir2, "commit", "-q", "-am", "feature edits seed.txt")).ExitCode);
        Assert.Equal(0, (await RawGit(dir2, "checkout", "-q", trunk)).ExitCode);
        File.WriteAllText(Path.Combine(dir2, "seed.txt"), "trunk");
        Assert.Equal(0, (await RawGit(dir2, "commit", "-q", "-am", "trunk edits seed.txt")).ExitCode);
        var ownerMerge = await RawGit(dir2, "merge", "--no-ff", "feature");
        Assert.NotEqual(0, ownerMerge.ExitCode);
        Assert.True(File.Exists(Path.Combine(dir2, ".git", "MERGE_HEAD")));

        var note2 = await _worktrees.RecoverAsync(dir2, CancellationToken.None);
        Assert.Null(note2);
        Assert.True(File.Exists(Path.Combine(dir2, ".git", "MERGE_HEAD")));
    }

    [Fact]
    public async Task Recover_deletes_an_already_merged_branch_and_ignores_non_exchange_worktrees()
    {
        var dir = await RoomWithCommit("lab12");
        var lease = await _worktrees.EnsureAsync(dir, 13, CancellationToken.None);
        var git = _trails.For(dir);
        // Merged cleanly (as a normal close would), but the worktree stays registered - as if the hub
        // died between the merge and the worktree's removal.
        var merged = await git.MergeAsync("chopitup/x13", "Merge exchange #13 (lab12)", CancellationToken.None);
        Assert.Equal(MergeResult.Merged, merged.Result);
        Assert.True(await git.IsAncestorOfHeadAsync("chopitup/x13"));

        var notesPath = Path.Combine(ExchangeWorktrees.FolderFor(dir), "notes");
        Assert.Null(await git.AddWorktreeAsync(notesPath, "notes-branch", newBranch: true));

        var note = await _worktrees.RecoverAsync(dir, CancellationToken.None);
        Assert.Null(note);   // the already-merged branch is cleaned up quietly, nothing to report
        Assert.False(await git.BranchExistsAsync("chopitup/x13"));
        Assert.False(Directory.Exists(lease.Path));
        Assert.True(Directory.Exists(notesPath));
        Assert.True(await git.BranchExistsAsync("notes-branch"));
    }

    [Fact]
    public async Task Close_keeps_the_branch_when_a_spawn_was_interrupted()
    {
        var dir = await RoomWithCommit("lab13");
        await _worktrees.EnsureAsync(dir, 14, CancellationToken.None);
        var note = await _worktrees.CloseAsync(Close(dir, 14, "lab13", ExchangeStatus.Concluded, interrupted: true), CancellationToken.None);
        Assert.Contains("a spawn of it was stopped or timed out", note);
        Assert.True(await _trails.For(dir).BranchExistsAsync("chopitup/x14"));
    }

    [Fact]
    public async Task Close_keeps_a_leased_branch_whose_folder_vanished()
    {
        var dir = await RoomWithCommit("lab14");
        var lease = await _worktrees.EnsureAsync(dir, 15, CancellationToken.None);
        TestDirs.DeleteTree(lease.Path!);
        await _trails.For(dir).PruneWorktreeAsync(lease.Path!, CancellationToken.None);   // gone AND forgotten; only the branch remains

        var note = await _worktrees.CloseAsync(Close(dir, 15, "lab14", ExchangeStatus.Concluded), CancellationToken.None);
        Assert.Contains("its worktree folder was gone at close", note);
        Assert.True(await _trails.For(dir).BranchExistsAsync("chopitup/x15"));
    }

    [Fact]
    public async Task Close_keeps_the_branch_when_the_owner_commit_fails()
    {
        var dir = await RoomWithCommit("lab15");
        await _worktrees.EnsureAsync(dir, 16, CancellationToken.None);
        File.WriteAllText(Path.Combine(dir, "c.txt"), "one");
        await _trails.For(dir).CommitAllAsync("first c.txt", Owner, allowEmpty: false);
        var firstCommit = (await GitOut(dir, "rev-parse", "HEAD")).Trim();
        File.WriteAllText(Path.Combine(dir, "c.txt"), "two");
        await _trails.For(dir).CommitAllAsync("second c.txt", Owner, allowEmpty: false);
        File.WriteAllText(Path.Combine(dir, "c.txt"), "three");
        await _trails.For(dir).CommitAllAsync("third c.txt", Owner, allowEmpty: false);
        var pick = await RawGit(dir, "cherry-pick", firstCommit);
        Assert.NotEqual(0, pick.ExitCode);
        Assert.True(File.Exists(Path.Combine(dir, ".git", "CHERRY_PICK_HEAD")));

        var note = await _worktrees.CloseAsync(Close(dir, 16, "lab15", ExchangeStatus.Concluded), CancellationToken.None);
        Assert.Contains("cherry-pick", note);
        Assert.True(await _trails.For(dir).BranchExistsAsync("chopitup/x16"));
        Assert.True(File.Exists(Path.Combine(dir, ".git", "CHERRY_PICK_HEAD")));
    }

    [Fact]
    public async Task Close_without_a_registered_worktree_touches_no_branch()
    {
        var dir = await RoomWithCommit("lab16");
        var git = _trails.For(dir);
        Assert.Equal(0, (await RawGit(dir, "branch", "chopitup/x9")).ExitCode);   // same name, never leased a worktree

        var note = await _worktrees.CloseAsync(Close(dir, 9, "lab16", ExchangeStatus.Concluded, leased: false), CancellationToken.None);
        Assert.Null(note);
        Assert.True(await git.BranchExistsAsync("chopitup/x9"));
    }

    [Fact]
    public async Task Close_of_a_branch_with_nothing_new_says_so()
    {
        var dir = await RoomWithCommit("lab17");
        await _worktrees.EnsureAsync(dir, 17, CancellationToken.None);
        var note = await _worktrees.CloseAsync(Close(dir, 17, "lab17", ExchangeStatus.Concluded), CancellationToken.None);
        Assert.Contains("had nothing new to merge", note);
        Assert.False(await _trails.For(dir).BranchExistsAsync("chopitup/x17"));
    }

    [Fact]
    public async Task Ensure_refuses_when_the_worktrees_folder_is_a_file()
    {
        var dir = await RoomWithCommit("lab18");
        File.WriteAllText(ExchangeWorktrees.FolderFor(dir), "not a folder");

        var lease = await _worktrees.EnsureAsync(dir, 18, CancellationToken.None);
        Assert.Null(lease.Path);
        Assert.Contains("could not be created", lease.Refusal);
    }

    [Fact]
    public async Task Recover_names_a_kept_worktree_that_could_not_be_removed_and_never_deletes_its_branch()
    {
        // A locked worktree refuses `git worktree remove` regardless of dirt; its branch, still checked
        // out there, must never be handed to `branch -d` - the failure must be named, not swallowed, and
        // the branch and the locked, unremoved worktree must both stay behind and be reported.
        var dir = await RoomWithCommit("lab19");
        var lease = await _worktrees.EnsureAsync(dir, 20, CancellationToken.None);
        Assert.Equal(0, (await RawGit(dir, "worktree", "lock", lease.Path!)).ExitCode);

        var note = await _worktrees.RecoverAsync(dir, CancellationToken.None);
        Assert.NotNull(note);
        Assert.Contains("chopitup/x20", note);
        Assert.Contains(lease.Path!, note);
        var git = _trails.For(dir);
        Assert.True(await git.BranchExistsAsync("chopitup/x20"));
        Assert.True(Directory.Exists(lease.Path));
    }

    [Fact]
    public async Task Close_keeps_the_branch_of_a_registered_worktree_whose_folder_is_already_gone()
    {
        // The folder vanished (deleted by hand, or a crash) but git's own worktree registration was
        // never pruned - unlike Close_keeps_a_leased_branch_whose_folder_vanished, where the registration
        // was already pruned before close, here CloseAsync sees the worktree as still registered. Prunes
        // only this exchange's own registration (never a blanket prune, which could also drop an owner's
        // own worktree elsewhere) and, being leased, keeps the branch exactly as the already-pruned case
        // does - it never merges over a worktree whose folder is simply gone.
        var dir = await RoomWithCommit("lab20");
        var lease = await _worktrees.EnsureAsync(dir, 21, CancellationToken.None);
        TestDirs.DeleteTree(lease.Path!);

        var note = await _worktrees.CloseAsync(Close(dir, 21, "lab20", ExchangeStatus.Concluded), CancellationToken.None);
        Assert.Contains("its worktree folder was gone at close", note);
        Assert.True(await _trails.For(dir).BranchExistsAsync("chopitup/x21"));
    }

    [Fact]
    public async Task An_owners_own_worktree_elsewhere_stays_registered_through_ensure_close_and_recover()
    {
        // A worktree the OWNER made themselves, outside any exchange's own `.worktrees` folder, whose
        // folder happens to be missing (an unmounted drive, say). None of the three entry points that
        // prune a stale registration may ever touch it - only the exchange's own path.
        var dir = await RoomWithCommit("lab21");
        var git = _trails.For(dir);
        var ownersOwn = Path.Combine(_dir, "owner-worktree-elsewhere");
        Assert.Null(await git.AddWorktreeAsync(ownersOwn, "owner-elsewhere", newBranch: true));
        TestDirs.DeleteTree(ownersOwn);
        Assert.Contains(await git.WorktreePathsAsync(), p => RoomPaths.Same(p, ownersOwn));

        await _worktrees.RecoverAsync(dir, CancellationToken.None);
        Assert.Contains(await git.WorktreePathsAsync(), p => RoomPaths.Same(p, ownersOwn));
        Assert.True(await git.BranchExistsAsync("owner-elsewhere"));

        // EnsureAsync's own prunable-registration path: this exchange's worktree vanishes between two
        // spawns (a stale registration Ensure prunes and re-adds onto), while the owner's worktree is
        // never touched.
        var lease1 = await _worktrees.EnsureAsync(dir, 30, CancellationToken.None);
        Assert.Null(lease1.Refusal);
        TestDirs.DeleteTree(lease1.Path!);
        var lease2 = await _worktrees.EnsureAsync(dir, 30, CancellationToken.None);
        Assert.Null(lease2.Refusal);
        Assert.True(Directory.Exists(lease2.Path));
        Assert.Contains(await git.WorktreePathsAsync(), p => RoomPaths.Same(p, ownersOwn));
        Assert.True(await git.BranchExistsAsync("owner-elsewhere"));

        // CloseAsync's registered-but-folder-gone path: same story again at close.
        TestDirs.DeleteTree(lease2.Path!);
        var note = await _worktrees.CloseAsync(Close(dir, 30, "lab21", ExchangeStatus.Concluded), CancellationToken.None);
        Assert.Contains("its worktree folder was gone at close", note);
        Assert.Contains(await git.WorktreePathsAsync(), p => RoomPaths.Same(p, ownersOwn));
        Assert.True(await git.BranchExistsAsync("owner-elsewhere"));
    }
}
