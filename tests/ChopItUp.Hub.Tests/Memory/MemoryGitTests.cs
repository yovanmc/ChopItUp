using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Memory;

public sealed class MemoryGitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_memgit_" + Guid.NewGuid().ToString("N"));

    public MemoryGitTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => TestDirs.DeleteTree(_dir);

    [Fact]
    public async Task A5_the_first_commit_initialises_the_repository_and_later_commits_advance_head()
    {
        File.WriteAllText(Path.Combine(_dir, "MEMORY.md"), "# Memory\n");
        var git = new MemoryGit(_dir);

        var first = await git.CommitAsync("Approve memory proposal #1 (user): Likes tests");
        Assert.NotNull(git.Reason is null ? first : null);
        Assert.Matches("^[0-9a-f]{7,}$", first);
        Assert.True(Directory.Exists(Path.Combine(_dir, ".git")));

        File.AppendAllText(Path.Combine(_dir, "MEMORY.md"), "\n## More\n");
        var second = await git.CommitAsync("Approve memory proposal #2 (core): More");
        Assert.Matches("^[0-9a-f]{7,}$", second);
        Assert.NotEqual(first, second);

        var third = await git.CommitAsync("nothing changed");   // no diff: HEAD stays, no error
        Assert.Equal(second, third);
        Assert.Null(git.Reason);

        // The trail is two commits, made with the fixed identity, whatever this machine's git config says.
        var log = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, ["log", "--format=%an <%ae> %s"], new Dictionary<string, string>(), _dir, "", "log"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
        var lines = log.StandardOutput.Trim().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("ChopItUp hub <hub@chopitup.local> Approve memory proposal", l));
    }

    [Fact]
    public async Task A5_without_git_a_commit_returns_null_with_a_reason_and_never_throws()
    {
        var git = new MemoryGit(_dir, () => throw new FileNotFoundException("'git' was not found on PATH"));
        Assert.Null(await git.CommitAsync("x"));
        Assert.Contains("git is not available", git.Reason);
        Assert.Null(await git.CommitAsync("y"));   // resolved once; still null, still quiet
        Assert.False(Directory.Exists(Path.Combine(_dir, ".git")));
    }

    [Fact]
    public async Task A5_a_failing_git_command_returns_null_with_the_step_and_stderr_in_the_reason()
    {
        var git = new MemoryGit(_dir, () => new ResolvedCli("fake-git.exe", [], "fake-git.exe"), new FailingRunner());
        Assert.Null(await git.CommitAsync("x"));
        Assert.StartsWith("git init exited 128: boom", git.Reason);
    }

    private sealed class FailingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation) =>
            Task.FromResult(new ProcessResult(128, false, false, "", "boom\n", TimeSpan.Zero));
    }
}
