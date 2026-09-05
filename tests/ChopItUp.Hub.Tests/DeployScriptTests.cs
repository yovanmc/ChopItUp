using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ChopItUp.Hub.Tests;

/// <summary>Regression tests for <c>tools/Deploy-ChopItUp.ps1</c>'s safety properties (plan
/// <c>docs/superpowers/plans/m4-release.md</c>, "Task 4 — Deploy-script tests"). The staging fixture
/// this class shares is SYNTHETIC, not a real <c>dotnet publish</c> (Task 4 dispatch amendment,
/// 2026-09-04): CI is <c>windows-latest</c> running <c>dotnet test --no-build</c>, and every property
/// under test here is about where bytes go, not about the exe being a real .NET binary. The fixture
/// builds one staging directory — a &gt;30&nbsp;MB <c>ChopItUp.Hub.exe</c> of known bytes,
/// <c>wwwroot\index.html</c>, and a file under <c>wwwroot\assets\</c> — once for the whole class; every
/// test drives the script against it (or a modified copy of it) via
/// <c>-StagingDir &lt;synthetic&gt; -SkipPublish</c>, and none of them publishes.
///
/// The script is driven as an external <c>pwsh -NoProfile -File</c> process throughout, always with an
/// explicit <c>-TargetDir</c> pointed at a scratch directory under <see cref="Path.GetTempPath"/> —
/// never the script's default, which is the owner's real install.</summary>
public sealed class DeployScriptFixture : IDisposable
{
    public string RepoRoot { get; }
    public string DeployScriptPath { get; }
    public string StagingDir { get; }
    public byte[] StagingExeBytes { get; }

    public DeployScriptFixture()
    {
        RepoRoot = FindRepoRoot();
        DeployScriptPath = Path.Combine(RepoRoot, "tools", "Deploy-ChopItUp.ps1");
        if (!File.Exists(DeployScriptPath))
        {
            throw new InvalidOperationException($"Expected the deploy script at '{DeployScriptPath}' but it does not exist.");
        }

        StagingDir = Path.Combine(Path.GetTempPath(), "chopitup_deploytest_staging_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(StagingDir, "wwwroot", "assets"));

        // Just over the script's 30 MB sanity-check floor, deterministic so tests can assert
        // byte-identity after a robocopy round trip.
        StagingExeBytes = KnownBytes((30 * 1024 * 1024) + 4096, seed: 42);
        File.WriteAllBytes(Path.Combine(StagingDir, "ChopItUp.Hub.exe"), StagingExeBytes);
        File.WriteAllText(Path.Combine(StagingDir, "wwwroot", "index.html"), "<!doctype html><html><body>synthetic shell</body></html>");
        File.WriteAllBytes(Path.Combine(StagingDir, "wwwroot", "assets", "index-synthetic.js"), KnownBytes(4096, seed: 7));
    }

    public static byte[] KnownBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChopItUp.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Could not locate the repo root (ChopItUp.slnx) above " + AppContext.BaseDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, recursive: true);
    }
}

public sealed class DeployScriptTests : IClassFixture<DeployScriptFixture>
{
    private readonly DeployScriptFixture _fixture;

    public DeployScriptTests(DeployScriptFixture fixture) => _fixture = fixture;

    [Fact]
    public void Deploy_never_touches_an_existing_data_directory()
    {
        string target = NewScratchPath("target");
        Directory.CreateDirectory(target);
        string dataDir = Path.Combine(target, "data");
        Directory.CreateDirectory(dataDir);
        byte[] dbBytes = DeployScriptFixture.KnownBytes(4096, seed: 101);
        byte[] tokenBytes = DeployScriptFixture.KnownBytes(256, seed: 102);
        File.WriteAllBytes(Path.Combine(dataDir, "chopitup.db"), dbBytes);
        File.WriteAllBytes(Path.Combine(dataDir, "tokens.json"), tokenBytes);
        try
        {
            var result = RunDeploy(target, _fixture.StagingDir);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(dbBytes, File.ReadAllBytes(Path.Combine(dataDir, "chopitup.db")));
            Assert.Equal(tokenBytes, File.ReadAllBytes(Path.Combine(dataDir, "tokens.json")));
        }
        finally
        {
            CleanupTargetAndBackups(target);
        }
    }

    [Fact]
    public void Deploy_copies_the_previous_install_aside_before_writing()
    {
        string target = NewScratchPath("target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "marker.txt"), "previous install marker");
        Directory.CreateDirectory(Path.Combine(target, "data"));
        File.WriteAllText(Path.Combine(target, "data", "chopitup.db"), "must not be backed up");
        try
        {
            var result = RunDeploy(target, _fixture.StagingDir);
            Assert.Equal(0, result.ExitCode);

            using var deployResult = ParseDeployResult(result.Stdout);
            Assert.True(deployResult.RootElement.GetProperty("backup_made").GetBoolean());
            string backupDir = deployResult.RootElement.GetProperty("backup_dir").GetString()!;

            Assert.True(Directory.Exists(backupDir));
            Assert.True(File.Exists(Path.Combine(backupDir, "marker.txt")));
            Assert.False(Directory.Exists(Path.Combine(backupDir, "data")));
        }
        finally
        {
            CleanupTargetAndBackups(target);
        }
    }

    [Fact]
    public void Deploy_aborts_and_changes_nothing_when_a_process_is_running_from_the_target()
    {
        string target = NewScratchPath("target");
        Directory.CreateDirectory(target);
        string guardExe = Path.Combine(target, "ping.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), guardExe);

        var guardPsi = new ProcessStartInfo(guardExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        guardPsi.ArgumentList.Add("-n");
        guardPsi.ArgumentList.Add("60");
        guardPsi.ArgumentList.Add("-w");
        guardPsi.ArgumentList.Add("1000");
        guardPsi.ArgumentList.Add("127.0.0.1");

        using var guard = Process.Start(guardPsi) ?? throw new InvalidOperationException("Failed to start the process-guard fixture executable.");
        try
        {
            // Poll: Windows populates the module list asynchronously, so a single read here
            // returns null on a loaded machine and fails an assertion about the wrong thing.
            string? actualPath = WaitForMainModulePath(guard, TimeSpan.FromSeconds(10));
            Assert.Equal(guardExe, actualPath, ignoreCase: true);

            string[] before = SnapshotRecursive(target);

            var result = RunDeploy(target, _fixture.StagingDir);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("DEPLOY_FAILED", result.Stderr);
            Assert.Contains(guard.Id.ToString(), result.Stderr);

            string[] after = SnapshotRecursive(target);
            Assert.Equal(before, after);
        }
        finally
        {
            // No Assert.* here: an assertion in a finally can throw and replace the try block's real
            // failure. Neither is the kill gated on having identified the process any more — that
            // gate is what let a failed identification skip the kill, leave ping holding its own
            // image, and turn the cleanup below into an exception that buried the real failure.
            // We hold the handle from Process.Start, so the PID cannot have been reused.
            StopGuardAndCleanup(guard, target);
        }
    }

    [Fact]
    public void Deploy_refuses_a_staging_output_that_is_missing_the_client()
    {
        string staging = CopyStaging();
        File.Delete(Path.Combine(staging, "wwwroot", "index.html"));
        string target = NewScratchPath("target");
        try
        {
            var result = RunDeploy(target, staging);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("DEPLOY_FAILED", result.Stderr);
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            CleanupTargetAndBackups(target);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public void Deploy_lands_the_client_in_the_target()
    {
        string target = NewScratchPath("target");
        try
        {
            var result = RunDeploy(target, _fixture.StagingDir);
            Assert.Equal(0, result.ExitCode);

            string indexPath = Path.Combine(target, "wwwroot", "index.html");
            Assert.True(File.Exists(indexPath));
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(_fixture.StagingDir, "wwwroot", "index.html")),
                File.ReadAllBytes(indexPath));

            string stagingAssets = Path.Combine(_fixture.StagingDir, "wwwroot", "assets");
            string targetAssets = Path.Combine(target, "wwwroot", "assets");
            var assetFiles = Directory.GetFiles(stagingAssets);
            Assert.NotEmpty(assetFiles);
            foreach (var file in assetFiles)
            {
                string targetFile = Path.Combine(targetAssets, Path.GetFileName(file));
                Assert.True(File.Exists(targetFile));
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(targetFile));
            }
        }
        finally
        {
            CleanupTargetAndBackups(target);
        }
    }

    [Fact]
    public void Deploy_writes_the_backup_as_a_sibling_of_the_target()
    {
        string[] selfAppsBefore = SnapshotTopLevel(SelfAppsDir);

        string tempRoot = NewScratchPath("deployroot");
        Directory.CreateDirectory(tempRoot);
        string target = Path.Combine(tempRoot, "ChopItUp");
        try
        {
            var first = RunDeploy(target, _fixture.StagingDir);
            Assert.Equal(0, first.ExitCode);

            var second = RunDeploy(target, _fixture.StagingDir);
            Assert.Equal(0, second.ExitCode);

            var backups = Directory.GetDirectories(tempRoot, "ChopItUp.backup-*");
            Assert.NotEmpty(backups);
            foreach (var backup in backups)
            {
                Assert.Equal(tempRoot, Path.GetDirectoryName(backup));
                Assert.StartsWith("ChopItUp.backup-", Path.GetFileName(backup));
            }

            string[] selfAppsAfter = SnapshotTopLevel(SelfAppsDir);
            Assert.Equal(selfAppsBefore, selfAppsAfter);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Deploy_replaces_the_exe_by_rename_not_in_place()
    {
        string target = NewScratchPath("target");
        try
        {
            var result = RunDeploy(target, _fixture.StagingDir);
            Assert.Equal(0, result.ExitCode);
            Assert.False(File.Exists(Path.Combine(target, "ChopItUp.Hub.exe.new")));
            Assert.Equal(_fixture.StagingExeBytes, File.ReadAllBytes(Path.Combine(target, "ChopItUp.Hub.exe")));
        }
        finally
        {
            CleanupTargetAndBackups(target);
        }
    }

    [Fact]
    public void Deploy_replaces_wwwroot_wholesale_and_leaves_data_untouched()
    {
        string target = NewScratchPath("target");
        string stagingV2 = CopyStaging();
        File.Move(
            Path.Combine(stagingV2, "wwwroot", "assets", "index-synthetic.js"),
            Path.Combine(stagingV2, "wwwroot", "assets", "index-v2.js"));
        try
        {
            var deployV1 = RunDeploy(target, _fixture.StagingDir);
            Assert.Equal(0, deployV1.ExitCode);

            string staleAssetPath = Path.Combine(target, "wwwroot", "assets", "index-stale.js");
            string staleTextPath = Path.Combine(target, "wwwroot", "stale.txt");
            File.WriteAllBytes(staleAssetPath, DeployScriptFixture.KnownBytes(512, seed: 321));
            File.WriteAllText(staleTextPath, "stale");

            Directory.CreateDirectory(Path.Combine(target, "data"));
            byte[] dbBytes = DeployScriptFixture.KnownBytes(2048, seed: 654);
            File.WriteAllBytes(Path.Combine(target, "data", "chopitup.db"), dbBytes);

            var deployV2 = RunDeploy(target, stagingV2);
            Assert.Equal(0, deployV2.ExitCode);
            using var deployV2Result = ParseDeployResult(deployV2.Stdout);
            string backupDir = deployV2Result.RootElement.GetProperty("backup_dir").GetString()!;

            Assert.False(File.Exists(staleAssetPath));
            Assert.False(File.Exists(staleTextPath));
            Assert.False(File.Exists(Path.Combine(target, "wwwroot", "assets", "index-synthetic.js")));

            Assert.Equal(
                File.ReadAllBytes(Path.Combine(stagingV2, "wwwroot", "assets", "index-v2.js")),
                File.ReadAllBytes(Path.Combine(target, "wwwroot", "assets", "index-v2.js")));
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(stagingV2, "wwwroot", "index.html")),
                File.ReadAllBytes(Path.Combine(target, "wwwroot", "index.html")));

            string[] stagingWwwrootFiles = RelativeFileList(Path.Combine(stagingV2, "wwwroot"));
            string[] targetWwwrootFiles = RelativeFileList(Path.Combine(target, "wwwroot"));
            Assert.Equal(stagingWwwrootFiles, targetWwwrootFiles);

            Assert.Equal(dbBytes, File.ReadAllBytes(Path.Combine(target, "data", "chopitup.db")));

            Assert.True(File.Exists(Path.Combine(backupDir, "wwwroot", "assets", "index-stale.js")));
        }
        finally
        {
            CleanupTargetAndBackups(target);
            if (Directory.Exists(stagingV2)) Directory.Delete(stagingV2, recursive: true);
        }
    }

    [Fact]
    public void Restore_puts_the_backup_back_and_leaves_data_untouched()
    {
        string target = NewScratchPath("target");
        string stagingV2 = CopyStaging();
        byte[] v2ExeBytes = DeployScriptFixture.KnownBytes(_fixture.StagingExeBytes.Length, seed: 999);
        File.WriteAllBytes(Path.Combine(stagingV2, "ChopItUp.Hub.exe"), v2ExeBytes);
        File.WriteAllBytes(
            Path.Combine(stagingV2, "wwwroot", "assets", "index-v2-only.js"),
            DeployScriptFixture.KnownBytes(1024, seed: 888));
        try
        {
            var deployV1 = RunDeploy(target, _fixture.StagingDir);
            Assert.Equal(0, deployV1.ExitCode);
            byte[] v1ExeBytes = File.ReadAllBytes(Path.Combine(target, "ChopItUp.Hub.exe"));

            Directory.CreateDirectory(Path.Combine(target, "data"));
            byte[] dbBytes = DeployScriptFixture.KnownBytes(2048, seed: 555);
            File.WriteAllBytes(Path.Combine(target, "data", "chopitup.db"), dbBytes);

            var deployV2 = RunDeploy(target, stagingV2);
            Assert.Equal(0, deployV2.ExitCode);
            using var deployV2Result = ParseDeployResult(deployV2.Stdout);
            string backupDir = deployV2Result.RootElement.GetProperty("backup_dir").GetString()!;
            Assert.Equal(v1ExeBytes, File.ReadAllBytes(Path.Combine(backupDir, "ChopItUp.Hub.exe")));
            Assert.True(File.Exists(Path.Combine(target, "wwwroot", "assets", "index-v2-only.js")));

            var restore = RunRestore(target, backupDir);
            Assert.Equal(0, restore.ExitCode);

            Assert.Equal(v1ExeBytes, File.ReadAllBytes(Path.Combine(target, "ChopItUp.Hub.exe")));
            Assert.Equal(dbBytes, File.ReadAllBytes(Path.Combine(target, "data", "chopitup.db")));
            Assert.False(File.Exists(Path.Combine(target, "wwwroot", "assets", "index-v2-only.js")));
            Assert.True(File.Exists(Path.Combine(target, "wwwroot", "assets", "index-synthetic.js")));
        }
        finally
        {
            CleanupTargetAndBackups(target);
            if (Directory.Exists(stagingV2)) Directory.Delete(stagingV2, recursive: true);
        }
    }

    // --- helpers ---------------------------------------------------------------------------------

    private const string SelfAppsDir = @"C:\Self Apps";

    private DeployRunResult RunDeploy(string targetDir, string stagingDir)
        => RunScript(targetDir, stagingDir: stagingDir, restoreFrom: null);

    private DeployRunResult RunRestore(string targetDir, string restoreFrom)
        => RunScript(targetDir, stagingDir: null, restoreFrom: restoreFrom);

    private DeployRunResult RunScript(string targetDir, string? stagingDir, string? restoreFrom)
    {
        var psi = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(_fixture.DeployScriptPath);
        psi.ArgumentList.Add("-TargetDir");
        psi.ArgumentList.Add(targetDir);
        if (restoreFrom is not null)
        {
            psi.ArgumentList.Add("-RestoreFrom");
            psi.ArgumentList.Add(restoreFrom);
        }
        else
        {
            psi.ArgumentList.Add("-StagingDir");
            psi.ArgumentList.Add(stagingDir!);
            psi.ArgumentList.Add("-SkipPublish");
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        bool exited = proc.WaitForExit(120_000);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"Deploy-ChopItUp.ps1 did not exit within 120s (target '{targetDir}').");
        }

        // The timed WaitForExit above returns as soon as the process exits; it does not wait for the
        // async OutputDataReceived/ErrorDataReceived handlers to finish delivering buffered data. Only
        // the parameterless overload guarantees that. Without it, the last line written before exit
        // (the DEPLOY_RESULT sentinel every parsing test needs) can be dropped under load.
        proc.WaitForExit();

        return new DeployRunResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static JsonDocument ParseDeployResult(string stdout)
    {
        string? line = stdout
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .LastOrDefault(l => l.StartsWith("DEPLOY_RESULT: ", StringComparison.Ordinal));
        Assert.NotNull(line);
        return JsonDocument.Parse(line!["DEPLOY_RESULT: ".Length..]);
    }

    private static string NewScratchPath(string label)
        => Path.Combine(Path.GetTempPath(), $"chopitup_deploytest_{label}_{Guid.NewGuid():N}");

    private string CopyStaging()
    {
        string dest = NewScratchPath("staging");
        CopyDirectoryRecursive(_fixture.StagingDir, dest);
        return dest;
    }

    private static void CopyDirectoryRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, dest));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, dest));
        }
    }

    [Fact]
    public void Stopping_a_guard_kills_it_and_clears_the_target_without_needing_an_image_match()
    {
        // The invariant the old code broke. It gated the kill on Process.MainModule matching the
        // copied exe, and MainModule returns null for a process whose module list Windows has not
        // finished populating - reproduced at 5/250 under CPU contention, 0/250 idle. When it
        // returned null the kill was SKIPPED, ping stayed alive holding its own image, the cleanup
        // Directory.Delete threw UnauthorizedAccessException from the finally, and that exception
        // replaced the real assertion failure. Two CI runs failed with a cleanup error and no
        // diagnosis. Stopping the guard must therefore not depend on identifying it: holding the
        // handle from Process.Start already guarantees the target, because Windows will not recycle
        // a PID while a handle to it is open.
        string target = NewScratchPath("guardstop");
        Directory.CreateDirectory(target);
        string guardExe = Path.Combine(target, "ping.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), guardExe);

        var psi = new ProcessStartInfo(guardExe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-n", "60", "-w", "1000", "127.0.0.1" }) psi.ArgumentList.Add(a);
        var guard = Process.Start(psi) ?? throw new InvalidOperationException("guard fixture failed to start");
        int guardPid = guard.Id;

        using (guard)
        {
            StopGuardAndCleanup(guard, target);
            Assert.True(guard.HasExited, "the guard process was left running");
        }

        Assert.False(Directory.Exists(target), "the target directory was left behind");
        Assert.Null(Process.GetProcesses().FirstOrDefault(x => x.Id == guardPid && !x.HasExited));
    }

    [Fact]
    public void The_main_module_path_of_a_just_started_process_is_resolved_by_waiting_for_it()
    {
        string target = NewScratchPath("mainmodule");
        Directory.CreateDirectory(target);
        string guardExe = Path.Combine(target, "ping.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), guardExe);

        var psi = new ProcessStartInfo(guardExe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-n", "60", "-w", "1000", "127.0.0.1" }) psi.ArgumentList.Add(a);
        using var guard = Process.Start(psi) ?? throw new InvalidOperationException("guard fixture failed to start");
        try
        {
            Assert.Equal(guardExe, WaitForMainModulePath(guard, TimeSpan.FromSeconds(10)), ignoreCase: true);
        }
        finally
        {
            StopGuardAndCleanup(guard, target);
        }
    }

    /// <summary>Reads the process's main module path, retrying until it is available. Windows
    /// populates a new process's module list asynchronously, so the first read can return null (or
    /// throw) for a process that has only just started - more often on a loaded machine, which is
    /// why this only ever failed on CI.</summary>
    private static string? WaitForMainModulePath(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                process.Refresh();
                if (process.MainModule?.FileName is { } path) return path;
            }
            catch (InvalidOperationException) { /* not ready, or already gone */ }
            catch (System.ComponentModel.Win32Exception) { /* module list not yet readable */ }

            if (DateTime.UtcNow >= deadline || process.HasExited) return null;
            Thread.Sleep(20);
        }
    }

    /// <summary>Kills the guard process and clears its directory. Takes no "is this really our
    /// process" flag on purpose: the caller holds the handle from Process.Start, which pins the PID
    /// against reuse, so the identity is already guaranteed by construction.</summary>
    private static void StopGuardAndCleanup(Process guard, string target)
    {
        try
        {
            if (!guard.HasExited)
            {
                guard.Kill();
                guard.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { /* already gone between the check and the kill */ }

        CleanupTargetAndBackups(target);
    }

    /// <summary>Recursive delete that tolerates a briefly-held handle. Even with the guard process
    /// stopped first, a freshly written executable can be held for a moment by a scanner, and a
    /// cleanup that throws in a finally destroys whatever failure the test was actually reporting.</summary>
    private static void DeleteWithRetry(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var delayMs in new[] { 0, 25, 50, 100, 250, 500 })
        {
            if (delayMs > 0) Thread.Sleep(delayMs);
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        Directory.Delete(dir, recursive: true);   // last attempt: let it throw and name the file
    }

    private static void CleanupTargetAndBackups(string target)
    {
        DeleteWithRetry(target);
        string? parent = Path.GetDirectoryName(target);
        string leaf = Path.GetFileName(target);
        if (parent is not null && Directory.Exists(parent))
        {
            foreach (var backup in Directory.GetDirectories(parent, leaf + ".backup-*"))
            {
                DeleteWithRetry(backup);
            }
        }
    }

    private static string[] SnapshotRecursive(string dir)
        => Directory.Exists(dir)
            ? Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    /// <summary>Top-level entry NAMES only — never recurses into a subdirectory, so this never reads
    /// anything under a real install's `data\`. This is the assertion Task 4 requires: a hardcoded
    /// backup path would write into the owner's real `C:\Self Apps` on every test run, and this is
    /// what catches it without touching the owner's actual data.</summary>
    private static string[] SnapshotTopLevel(string dir)
        => Directory.Exists(dir)
            ? Directory.GetFileSystemEntries(dir).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    /// <summary>File paths relative to <paramref name="dir"/>, so a staging directory and a target
    /// directory with different absolute paths can be compared for an identical file list.</summary>
    private static string[] RelativeFileList(string dir)
        => Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dir, f))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

    private sealed record DeployRunResult(int ExitCode, string Stdout, string Stderr);
}
