using System.Diagnostics;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Rooms;

namespace ChopItUp.Hub.Tests.Rooms;

public sealed class RoomArtifactsTests : IDisposable
{
    private static readonly RoomPathRules Rules = RoomPathRules.ForHub(
        dataDir: @"C:\hub\data", installDir: @"C:\hub\app", userProfile: @"C:\Users\hub-owner", getEnv: _ => null);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_artifacts_" + Guid.NewGuid().ToString("N"));
    private readonly MessageStore _store;
    private readonly RoomArtifacts _artifacts;
    private readonly List<string> _junctions = [];

    public RoomArtifactsTests()
    {
        Directory.CreateDirectory(_dir);
        TemplateDatabase.SeedInto(_dir);
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        _store = new MessageStore(db);
        _artifacts = new RoomArtifacts(_store, Rules);
    }

    public void Dispose()
    {
        foreach (var link in _junctions) Directory.Delete(link);
        TestDirs.DeleteTree(_dir);
    }

    private string Room(string id, string? path = null)
    {
        var dir = path ?? Path.Combine(_dir, "rooms", id);
        Directory.CreateDirectory(dir);
        _store.CreateRoom(id, id, dir);
        return dir;
    }

    [Fact]
    public void A_room_gets_a_folder_beside_its_directory_and_keeps_it_on_a_second_check()
    {
        var room = Room("lab");
        var first = _artifacts.Ensure(room);
        Assert.Equal(room + ".artifacts", first.Path);
        Assert.True(Directory.Exists(first.Path));
        File.WriteAllText(Path.Combine(first.Path!, "kept.png"), "x");

        Assert.Equal(first.Path, _artifacts.Ensure(room).Path);
        Assert.True(File.Exists(Path.Combine(first.Path!, "kept.png")));
    }

    private void Junction(string link, string target)
    {
        _junctions.Add(link);
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    [Fact]
    public void A_folder_inside_another_rooms_worktrees_is_refused()
    {
        var other = Room("other");
        var room = Path.Combine(other + ".worktrees", "x1");
        Directory.CreateDirectory(room);

        var lease = _artifacts.Ensure(room);

        Assert.Null(lease.Path);
        Assert.Contains("exchange worktrees", lease.Refusal);
    }

    [Fact]
    public void A_folder_that_is_another_rooms_directory_is_refused_and_nothing_is_created_in_it()
    {
        var room = Room("lab");
        var theirs = Room("other", room + ".artifacts");

        var lease = _artifacts.Ensure(room);

        Assert.Null(lease.Path);
        Assert.Contains("overlaps room 'other'", lease.Refusal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(theirs));
    }

    [Fact]
    public void A_folder_inside_another_rooms_tree_is_refused()
    {
        var outer = Room("outer");
        var room = Path.Combine(outer, "nested");
        Directory.CreateDirectory(room);

        var lease = _artifacts.Ensure(room);

        Assert.Null(lease.Path);
        Assert.Contains("overlaps room 'outer'", lease.Refusal);
        Assert.False(Directory.Exists(room + ".artifacts"));
    }

    [Fact]
    public void A_file_at_the_path_is_refused_and_left_alone()
    {
        var room = Room("lab");
        File.WriteAllText(room + ".artifacts", "owner's file");

        Assert.Contains("is a file", _artifacts.Ensure(room).Refusal);
        Assert.Equal("owner's file", File.ReadAllText(room + ".artifacts"));
    }

    [Fact]
    public void A_link_at_the_path_is_refused()
    {
        var room = Room("lab");
        var target = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(target);
        Junction(room + ".artifacts", target);

        Assert.Contains("is a link", _artifacts.Ensure(room).Refusal);
    }

    [Fact]
    public void A_refused_path_is_refused()
    {
        var rules = RoomPathRules.ForHub(dataDir: _dir, installDir: @"C:\hub\app", userProfile: @"C:\Users\hub-owner", getEnv: _ => null);
        var lease = new RoomArtifacts(_store, rules).Ensure(Path.Combine(_dir, "rooms", "lab"));
        Assert.Null(lease.Path);
        Assert.NotNull(lease.Refusal);
    }

    [Fact]
    public void A_store_fault_becomes_a_refusal_so_the_spawn_still_runs()
    {
        var broken = new MessageStore(new ChopDb(Path.Combine(_dir, "no-such-folder", "missing.db")));
        var lease = new RoomArtifacts(broken, Rules).Ensure(Path.Combine(_dir, "rooms", "lab"));
        Assert.Null(lease.Path);
        Assert.NotNull(lease.Refusal);
    }

    [Fact]
    public async Task A_check_that_hangs_becomes_a_refusal_for_this_launch_only()
    {
        using var release = new ManualResetEventSlim();
        var lease = await RoomArtifacts.WithinAsync(() => { release.Wait(); return new RoomArtifacts.Lease("late", null); }, TimeSpan.FromMilliseconds(50));
        release.Set();

        Assert.Null(lease.Path);
        Assert.True(lease.Transient);
        Assert.Contains("did not answer", lease.Refusal);
    }

    private string Folder()
    {
        var folder = Path.Combine(_dir, "scan");
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void Changed_names_new_and_rewritten_files_and_leaves_an_untouched_file_out()
    {
        var folder = Folder();
        File.WriteAllText(Path.Combine(folder, "same.png"), "same");
        File.WriteAllText(Path.Combine(folder, "edit.png"), "before");
        var before = RoomArtifacts.Scan(folder);

        File.WriteAllText(Path.Combine(folder, "edit.png"), "after, longer");
        Directory.CreateDirectory(Path.Combine(folder, "renders"));
        File.WriteAllText(Path.Combine(folder, "renders", "new.png"), "new");
        File.Delete(Path.Combine(folder, "same.png"));
        File.WriteAllText(Path.Combine(folder, "same2.png"), "x");

        Assert.Equal(["edit.png", "renders/new.png", "same2.png"], RoomArtifacts.Changed(before, RoomArtifacts.Scan(folder)));
    }

    [Fact]
    public void A_scan_skips_the_jobs_folder_and_does_not_follow_a_link()
    {
        var folder = Folder();
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.png"), "x");
        Junction(Path.Combine(folder, "linked"), outside);
        Directory.CreateDirectory(Path.Combine(folder, "jobs", "7"));
        File.WriteAllText(Path.Combine(folder, "jobs", "7", "frame.png"), "x");
        Directory.CreateDirectory(Path.Combine(folder, "renders", "jobs"));
        File.WriteAllText(Path.Combine(folder, "renders", "jobs", "kept.png"), "x");

        Assert.Equal(["renders/jobs/kept.png"], RoomArtifacts.Scan(folder).Files.Keys);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_scan_that_could_not_read_the_folder_credits_nobody(bool beforeFaults, bool afterFaults)
    {
        var folder = Folder();
        File.WriteAllText(Path.Combine(folder, "old.png"), "x");
        IEnumerable<FileSystemInfo> Denied(string _) => throw new UnauthorizedAccessException();

        var before = beforeFaults ? RoomArtifacts.Scan(folder, Denied) : RoomArtifacts.Scan(folder);
        File.WriteAllText(Path.Combine(folder, "new.png"), "x");
        var after = afterFaults ? RoomArtifacts.Scan(folder, Denied) : RoomArtifacts.Scan(folder);

        Assert.Empty(RoomArtifacts.Changed(before, after));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_subfolder_one_scan_could_not_read_is_left_out_and_the_rest_still_counts(bool beforeFaults, bool afterFaults)
    {
        var folder = Folder();
        var locked = Path.Combine(folder, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "theirs.png"), "x");
        IEnumerable<FileSystemInfo> Listing(string d) => string.Equals(d, locked, StringComparison.OrdinalIgnoreCase)
            ? throw new UnauthorizedAccessException()
            : new DirectoryInfo(d).EnumerateFileSystemInfos();

        var before = beforeFaults ? RoomArtifacts.Scan(folder, Listing) : RoomArtifacts.Scan(folder);
        File.WriteAllText(Path.Combine(folder, "mine.png"), "x");
        var after = afterFaults ? RoomArtifacts.Scan(folder, Listing) : RoomArtifacts.Scan(folder);

        Assert.Equal(["mine.png"], RoomArtifacts.Changed(before, after));
    }

    [Fact]
    public void A_reference_reached_through_a_link_does_not_exist()
    {
        var folder = Folder();
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "x.png"), "x");
        Junction(Path.Combine(folder, "linked"), outside);
        Directory.CreateDirectory(Path.Combine(folder, "real"));
        File.WriteAllText(Path.Combine(folder, "real", "y.png"), "y");

        Assert.False(RoomArtifacts.ReferenceExists(folder, "linked/x.png"));
        Assert.True(RoomArtifacts.ReferenceExists(folder, "real/y.png"));
        Assert.False(RoomArtifacts.ReferenceExists(folder, "real/missing.png"));
        Assert.False(RoomArtifacts.ReferenceExists(folder, "real"));
    }
}
