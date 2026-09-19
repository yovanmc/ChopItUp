using System.Text.RegularExpressions;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Git;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Rooms;

/// <summary>Row 35: where an exchange's git worktree lives, how it is created, and what a close does
/// with it - merge and delete the branch, or keep the branch and say why. Every write goes through the
/// room directory's <see cref="GitTrail"/> (via <see cref="RoomTrails"/>), so a worktree spawn, the
/// owner's own commit and a close never race: the gate is shared (<see cref="GitTrail.WithRoot"/>).</summary>
public sealed class ExchangeWorktrees(MessageStore store, RoomTrails trails, RoomPathRules rules)
{
    private static readonly Regex ExchangeFolderName = new(@"^x\d+$");

    public static string Branch(long rootMessageId) => $"chopitup/x{rootMessageId}";

    /// <summary>The folder beside <paramref name="roomDirectory"/> that holds every one of its
    /// exchanges' worktrees. Never under the hub's data directory: a spawned session loads the owner's
    /// global hooks, and those deny every read under the deployed hub's own data folder.</summary>
    public static string FolderFor(string roomDirectory)
    {
        var d = RoomPaths.Normalize(roomDirectory);
        return d + ".worktrees";
    }

    public static string PathFor(string roomDirectory, long rootMessageId) =>
        Path.Combine(FolderFor(roomDirectory), "x" + rootMessageId);

    /// <summary><see cref="Path"/> is the worktree's folder on success; <see cref="Refusal"/> is the
    /// sentence a hub note reads when it is non-null - nothing was created, and the branch (if any
    /// already existed) is untouched.</summary>
    public sealed record Lease(string? Path, string? Refusal);

    /// <summary>Creates (or, idempotently, confirms) the worktree for exchange <paramref name="root"/>
    /// of the room at <paramref name="roomDirectory"/>. Never starts a spawn anywhere when it refuses
    /// (AC8): the caller is expected to post the refusal and start nothing. <paramref name="continueBranch"/>
    /// (row 36) is set only for an exchange a reply reopened: an existing <c>chopitup/x&lt;root&gt;</c> is
    /// then that exchange's own kept work, and the worktree is added onto it.</summary>
    public async Task<Lease> EnsureAsync(string roomDirectory, long root, CancellationToken cancellation, bool continueBranch = false)
    {
        var path = PathFor(roomDirectory, root);
        if (RoomPaths.Refusal(path, rules) is { } refused) return new(null, "the worktree path is refused: " + refused);

        foreach (var other in store.ListRooms(includeArchived: true))
        {
            if (other.Directory is null) continue;
            var theirs = RoomPaths.Normalize(other.Directory);
            if (RoomPaths.IsUnderOrEqual(path, theirs) || RoomPaths.IsUnderOrEqual(theirs, path))
                return new(null, $"the worktree path overlaps room '{other.Id}'");
        }

        var main = trails.For(roomDirectory);
        var pruned = false;
        if ((await main.WorktreePathsAsync(cancellation)).Any(p => RoomPaths.Same(p, path)))
        {
            if (Directory.Exists(path)) return new(path, null);
            // A registration with no folder behind it (row 35): the folder was deleted
            // between two spawns of the same exchange. Prune only this exchange's own stale entry
            // (never every stale entry in the repository - that could also drop an owner's own
            // worktree elsewhere whose folder merely happens to be missing) and re-add below, onto the
            // branch this exchange already owns rather than refusing it as "already exists".
            await main.PruneWorktreeAsync(path, cancellation);
            pruned = true;
        }

        if (!await main.HasCommitsAsync(cancellation))
        {
            // The caller already committed the owner's dirty tree before this call; nothing new is
            // staged here, so this is an empty commit purely to give the room a HEAD a worktree can fork.
            var start = await main.CommitAllAsync("Room trail start", author: null, allowEmpty: true, cancellation: cancellation);
            if (start.Hash is null) return new(null, "the room directory has no commit and one could not be made: " + start.Reason);
        }

        var branch = Branch(root);
        var exists = await main.BranchExistsAsync(branch, cancellation);
        if (exists && !pruned && !continueBranch) return new(null, $"branch {branch} already exists");

        try { Directory.CreateDirectory(FolderFor(roomDirectory)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(null, "the worktrees folder could not be created: " + e.Message);
        }
        var failed = await main.AddWorktreeAsync(path, branch, newBranch: !exists, cancellation);
        if (failed is not null) return new(null, "git worktree add failed: " + failed);

        return new(path, null);
    }

    /// <summary>What <see cref="CloseAsync"/> needs to decide merge-or-keep and to write the owner's
    /// commit if the room tree is dirty, under the repository's identity. <see cref="Leased"/> is true
    /// only when a spawn of this exchange really ran in a worktree the hub handed it (not a refused
    /// lease); <see cref="Interrupted"/> is true when any of its spawns was cancelled or timed out,
    /// whatever <see cref="Status"/> says; <see cref="RunOwnsRoom"/> is true when a run is active or
    /// parked in the room at close time.</summary>
    public sealed record CloseRequest(string RoomDirectory, long Root, string RoomId, ExchangeStatus Status, bool Leased, bool Interrupted, bool RunOwnsRoom, string OwnerMessage);

    /// <summary>Merges a concluded or superseded exchange's worktree into the room directory's checked
    /// out branch and deletes it, or keeps the branch and says why not. Returns the hub note to post, or
    /// null when there is nothing to say (no worktree was ever registered for this exchange).</summary>
    public async Task<string?> CloseAsync(CloseRequest request, CancellationToken cancellation)
    {
        var (roomDirectory, root, roomId, status, leased, interrupted, runOwnsRoom, ownerMessage) = request;
        var path = PathFor(roomDirectory, root);
        var branch = Branch(root);
        var main = trails.For(roomDirectory);

        var removal = "";
        string Keep(string reason) => $"Exchange #{root} was not merged: {reason}. Its commits stay on branch {branch}.{removal}";

        var registered = (await main.WorktreePathsAsync(cancellation)).Any(p => RoomPaths.Same(p, path));
        if (!registered)
        {
            if (leased && await main.BranchExistsAsync(branch, cancellation)) return Keep("its worktree folder was gone at close");
            return null;   // never got a worktree (a refused lease, AC8): this branch, if any, is not the hub's to touch
        }
        if (Directory.Exists(path))
        {
            var w = trails.ForWorktree(roomDirectory, path);
            if (await w.IsDirtyAsync(cancellation))
                await w.CommitAllAsync($"Uncommitted at the close of exchange #{root}", author: null, allowEmpty: false, cancellation: cancellation);
            var failed = await main.RemoveWorktreeAsync(path, cancellation);
            if (failed is not null) removal = $" Its worktree at {path} was not removed: {failed}";
            else trails.Forget(path);
        }
        else
        {
            // The folder is already gone but git's own registration was never pruned: prune only this
            // exchange's own entry (never every stale entry in the repository) before the branch checks
            // below, so a stale "used by worktree at <gone path>" registration does not refuse the
            // `branch -d` a clean merge is about to attempt. Consistent with the already-pruned case
            // just above: a leased exchange whose folder vanished keeps its branch rather than merging.
            await main.PruneWorktreeAsync(path, cancellation);
            if (leased && await main.BranchExistsAsync(branch, cancellation)) return Keep("its worktree folder was gone at close");
        }

        if (!await main.BranchExistsAsync(branch, cancellation))
            return removal.Length > 0 ? $"Exchange #{root}:{removal}" : null;

        if (await main.OperationInProgressAsync(cancellation) is { } inProgress)
            return Keep($"the room directory has a {inProgress} in progress");

        if (status == ExchangeStatus.Stopped) return Keep("it was stopped");
        if (interrupted) return Keep("a spawn of it was stopped or timed out");
        if (runOwnsRoom) return Keep("a run owns this room");
        var current = await main.CurrentBranchAsync(cancellation);
        if (current is null) return Keep("the room directory has no branch checked out");

        if (await main.IsDirtyAsync(cancellation))
        {
            var oc = await main.CommitAllAsync(ownerMessage, author: null, allowEmpty: false, cancellation: cancellation);
            if (oc.Hash is null) return Keep($"the owner's edits could not be committed first: {oc.Reason}");
        }

        var m = await main.MergeAsync(branch, $"Merge exchange #{root} ({roomId})", cancellation);
        switch (m.Result)
        {
            case MergeResult.Merged:
                var notDeleted = await main.DeleteMergedBranchAsync(branch, cancellation);
                var note = m.Hash == m.Before
                    ? $"Exchange #{root} had nothing new to merge into {current}.{removal}"
                    : $"Exchange #{root} merged into {current} as {m.Hash}.{removal}";
                if (notDeleted is not null) note += $" Branch {branch} was not deleted: {notDeleted}";
                return note;
            case MergeResult.Conflict:
                return Keep($"merging into {current} conflicts in {string.Join(", ", m.Conflicts)}");
            default:
                return Keep($"the merge failed: {m.Reason}");
        }
    }

    /// <summary>Start-up cleanup for worktrees a previous hub process left open. Aborts a merge left in
    /// progress only when it is the hub's own exchange merge (never an owner's), then for every
    /// registered <c>x&lt;digits&gt;</c> worktree directly under this room's worktrees folder: commits
    /// any dirty edits under the repository's identity, removes the worktree, and deletes its branch
    /// when already merged or else names it as kept. Null when nothing needed doing.</summary>
    public async Task<string?> RecoverAsync(string roomDirectory, CancellationToken cancellation)
    {
        var main = trails.For(roomDirectory);
        if (!Directory.Exists(roomDirectory)) return null;

        string? prefix = null;
        var stale = await main.AbortStaleExchangeMergeAsync(cancellation);
        if (stale == "aborted") prefix = "The last hub's exchange merge was aborted. ";
        else if (stale is not null && stale != "left alone") prefix = $"The last hub's exchange merge was not aborted: {stale}. ";

        var folder = FolderFor(roomDirectory);
        var candidates = (await main.WorktreePathsAsync(cancellation))
            .Where(p => RoomPaths.Same(Path.GetDirectoryName(p) ?? "", folder) && ExchangeFolderName.IsMatch(Path.GetFileName(p)))
            .ToList();

        var kept = new List<string>();
        foreach (var path in candidates)
        {
            if (Directory.Exists(path))
            {
                var w = trails.ForWorktree(roomDirectory, path);
                if (await w.IsDirtyAsync(cancellation))
                    await w.CommitAllAsync("Uncommitted when the hub restarted", author: null, allowEmpty: false, cancellation: cancellation);
            }
            var branch = "chopitup/" + Path.GetFileName(path);
            var removeFailed = await main.RemoveWorktreeAsync(path, cancellation);
            if (removeFailed is not null)
            {
                // A worktree that could not be removed (locked, still in use) still has its branch
                // checked out there - `branch -d` would only fail too, so never attempt it; name the
                // worktree and why it stayed instead of silently reporting nothing. `RemoveWorktreeAsync`
                // itself already handles a folder that is simply gone (git prunes that registration as
                // part of removing it), so nothing further is needed for this exchange's own entry here -
                // and nothing blanket-prunes the rest of the repository's worktrees, which could otherwise
                // drop an owner's own registered worktree elsewhere whose folder merely happens to be
                // missing (row 35).
                kept.Add($"{branch} (its worktree at {path} was not removed: {removeFailed})");
                continue;
            }
            if (await main.IsAncestorOfHeadAsync(branch, cancellation)) await main.DeleteMergedBranchAsync(branch, cancellation);
            else kept.Add(branch);
        }

        if (kept.Count == 0) return prefix;
        return prefix + $"The hub restarted while exchange worktrees were open. Their commits stay on branch(es) {string.Join(", ", kept)}, not merged.";
    }
}
