using System.Formats.Tar;
using System.Text;
using System.Diagnostics;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed class PanelInputsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chopitup_panel_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Noisy_git_stream_is_bounded_and_cancels_the_other_pipe_and_process_wait()
    {
        using var cancellation = new CancellationTokenSource();
        using var noisy = new MemoryStream(new byte[64 * 1024 + 1]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PanelInputs.ReadGitOutputAsync(noisy, 64 * 1024, cancellation));
        Assert.Contains("size limit", error.Message);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(noisy.Position <= 64 * 1024 + 8192);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("file:stream")]
    [InlineData("CON.txt")]
    [InlineData("dir/../escape.txt")]
    public async Task Unsafe_archive_paths_are_refused(string name)
    {
        using var bytes = Archive((name, "bad"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PanelInputs.ExtractAsync(bytes, _root, CancellationToken.None));
    }

    [Fact]
    public async Task Case_collisions_are_refused()
    {
        using var bytes = Archive(("File.cs", "a"), ("file.cs", "b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PanelInputs.ExtractAsync(bytes, _root, CancellationToken.None));
    }

    [Fact]
    public async Task Regular_files_preserve_exact_content()
    {
        using var bytes = Archive(("src/Test.cs", "exact\r\nUTF-8: café\n"));
        await PanelInputs.ExtractAsync(bytes, _root, CancellationToken.None);
        Assert.Equal("exact\r\nUTF-8: café\n", File.ReadAllText(Path.Combine(_root, "src/Test.cs")));
    }

    private static MemoryStream Archive(params (string Name, string Body)[] files)
    {
        var bytes = new MemoryStream();
        using (var writer = new TarWriter(bytes, leaveOpen: true))
            foreach (var file in files)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, file.Name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(file.Body)) });
        bytes.Position = 0;
        return bytes;
    }

    [Fact]
    public async Task Git_snapshot_preserves_blobs_despite_export_attributes_and_rejects_a_changed_HEAD()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        await Git(repo, "init", "-q");
        await Git(repo, "config", "user.name", "Synthetic Test");
        await Git(repo, "config", "user.email", "synthetic@example.invalid");
        await Git(repo, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitattributes"), "secret.txt export-ignore\nversion.txt export-subst\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "secret.txt"), "synthetic tracked content");
        await File.WriteAllTextAsync(Path.Combine(repo, "version.txt"), "$Format:%H$\n");
        await Git(repo, "add", ".");
        await Git(repo, "commit", "-qm", "synthetic fixture");
        var commit = await PanelInputs.ReadyCommitAsync(repo, CancellationToken.None);
        var snapshots = Path.Combine(_root, "snapshots");
        await PanelInputs.PrepareAsync(repo, commit, snapshots, CancellationToken.None);
        foreach (var stage in new[] { "first", "second", "synthesis" })
        {
            Assert.Equal("synthetic tracked content", await File.ReadAllTextAsync(Path.Combine(snapshots, stage, "secret.txt")));
            Assert.Equal("$Format:%H$\n", await File.ReadAllTextAsync(Path.Combine(snapshots, stage, "version.txt")));
            Assert.False(Directory.Exists(Path.Combine(snapshots, stage, ".git")));
        }
        Assert.Equal(commit, await PanelInputs.ReadyCommitAsync(repo, CancellationToken.None));
        await File.WriteAllTextAsync(Path.Combine(repo, "version.txt"), "dirty");
        await Assert.ThrowsAsync<InvalidOperationException>(() => PanelInputs.ReadyCommitAsync(repo, CancellationToken.None));
        await Git(repo, "add", ".");
        await Git(repo, "commit", "-qm", "advance fixture");
        var refused = Path.Combine(_root, "refused");
        await Assert.ThrowsAsync<InvalidOperationException>(() => PanelInputs.PrepareAsync(repo, commit, refused, CancellationToken.None));
        Assert.False(Directory.Exists(refused));
    }

    [Fact]
    public async Task Symlink_archive_and_cancelled_preparation_are_refused()
    {
        using var bytes = new MemoryStream();
        using (var writer = new TarWriter(bytes, leaveOpen: true))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "escape") { LinkName = "../outside" });
        bytes.Position = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => PanelInputs.ExtractAsync(bytes, _root, CancellationToken.None));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PanelInputs.PrepareAsync(null, null, Path.Combine(_root, "cancelled"), cancel.Token));
        Assert.False(Directory.Exists(Path.Combine(_root, "cancelled")));
    }

    private static async Task Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public async Task A_directory_that_resolves_to_a_parent_repository_is_refused()
    {
        var parent = Path.Combine(_root, "parent");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(child);
        await Git(parent, "init", "-q");
        await Git(parent, "config", "user.name", "Synthetic Test");
        await Git(parent, "config", "user.email", "synthetic@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(parent, ".gitignore"), "child/\n");
        await File.WriteAllTextAsync(Path.Combine(parent, "outside.txt"), "must not enter a child room snapshot");
        await Git(parent, "add", ".");
        await Git(parent, "commit", "-qm", "parent fixture");
        await Git(child, "init", "-q");
        Directory.Move(Path.Combine(child, ".git"), Path.Combine(_root, "removed-child-metadata"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PanelInputs.ReadyCommitAsync(child, CancellationToken.None));
        Assert.Contains("repository root", error.Message);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        // Git stores objects read-only on Windows; only this test's uniquely allocated root is touched.
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
