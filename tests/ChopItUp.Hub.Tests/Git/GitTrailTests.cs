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
    public void Dispose() => TestDirs.DeleteTree(_dir);

    private static async Task<string> GitOut(string dir, params string[] args)
    {
        var r = await new ProcessRunner().RunAsync(
            new ProcessSpec(CliResolver.Resolve("git").FileName, args, new Dictionary<string, string>(), dir, "", "test-git"),
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(0, r.ExitCode);
        return r.StandardOutput;
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

    [Fact]
    public void M9_A9_room_trails_hand_out_one_trail_per_normalised_directory()
    {
        var trails = new RoomTrails(dir => new GitTrail(dir));
        var a = trails.For(@"C:\Rooms\lab\");
        var b = trails.For(@"c:\rooms\LAB");
        Assert.Same(a, b);
        Assert.NotSame(a, trails.For(@"C:\Rooms\other"));
    }

    private sealed class FailingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellation) =>
            Task.FromResult(new ProcessResult(128, false, false, "", "boom\n", TimeSpan.Zero));
    }
}
