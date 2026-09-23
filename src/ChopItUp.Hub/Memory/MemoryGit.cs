using ChopItUp.Hub.Git;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Memory;

/// <summary>The trail behind the memory store: every approval is one commit, as the hub, in a
/// repository inside &lt;data&gt;\memory\, initialised lazily on the first commit. This is
/// <see cref="GitTrail"/> with the hub as author and committer whatever the machine's git config
/// says. A machine without git loses the record, not the memory (<see cref="GitTrail.Reason"/> says
/// why).</summary>
public sealed class MemoryGit(string root, Func<ResolvedCli>? resolve = null, IProcessRunner? runner = null) : GitTrail(root, resolve, runner)
{
    protected override string LogName => "memory";
    protected override bool CommitsAsHub => true;

    /// <summary>Stages everything under the root and commits it. Returns the short hash of HEAD - the
    /// new commit, or the unchanged HEAD when there was nothing to commit - or null with
    /// <see cref="GitTrail.Reason"/> set. Serialised: two approvals never race inside one repository.</summary>
    public async Task<string?> CommitAsync(string message, CancellationToken cancellation = default) =>
        (await CommitAllAsync(message, Hub, allowEmpty: false, cancellation: cancellation)).Hash;
}
