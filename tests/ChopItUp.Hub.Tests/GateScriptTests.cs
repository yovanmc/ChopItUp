using System.Diagnostics;
using System.Text;

namespace ChopItUp.Hub.Tests;

/// <summary>Regression tests for the roadmap-hub overlay's git-only gate scripts (plan
/// <c>docs/superpowers/plans/row20-roadmap-port.md</c>, "Task 4 — Overlay directory:
/// <c>tools/skills/roadmap-hub/</c>"). Every script is driven as an external <c>pwsh -NoProfile -File</c>
/// process, the way <see cref="DeployScriptTests"/> drives <c>Deploy-ChopItUp.ps1</c>, against a real
/// temporary git repository under <see cref="Path.GetTempPath"/> with its own local git identity.
///
/// <c>board-gate</c> and <c>plan-claims</c> against a REAL plan are exercised only by the scratch run
/// named in the plan (they need the harness's <c>preflight/Check-RoadmapBudget.ps1</c> /
/// <c>Check-PlanClaims.ps1</c>, which travel with the base `roadmap` skill and are composed in next to
/// the overlay's scripts only by <c>--import-skill --overlay</c> (Task 1) — CI does not have them at
/// <c>tools/skills/roadmap-hub/preflight/</c>, and this repo does not either, by design). The
/// <c>plan_claims_exits_0_with_no_plan_rows</c> test below is the one plan-claims case this file can
/// still characterize directly: with zero 📝/🔨 rows the preflight script is never invoked, so a stub
/// file that merely exists is enough. See that test for why even reaching that branch needs a composed
/// copy of the real, unmodified script.</summary>
public sealed class GateScriptFixture
{
    public string RepoRoot { get; }
    public string StartBranchScript { get; }
    public string FinishBranchScript { get; }
    public string TestGateScript { get; }
    public string PlanClaimsScript { get; }
    public bool PwshAvailable { get; }

    public GateScriptFixture()
    {
        RepoRoot = FindRepoRoot();
        string scriptsDir = Path.Combine(RepoRoot, "tools", "skills", "roadmap-hub", "scripts");
        StartBranchScript = Path.Combine(scriptsDir, "start-branch.ps1");
        FinishBranchScript = Path.Combine(scriptsDir, "finish-branch.ps1");
        TestGateScript = Path.Combine(scriptsDir, "test.ps1");
        PlanClaimsScript = Path.Combine(scriptsDir, "plan-claims.ps1");
        foreach (var script in new[] { StartBranchScript, FinishBranchScript, TestGateScript, PlanClaimsScript })
        {
            if (!File.Exists(script)) throw new InvalidOperationException($"Expected gate script at '{script}' but it does not exist.");
        }
        PwshAvailable = ProbePwsh();
    }

    private static bool ProbePwsh()
    {
        try
        {
            var psi = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("exit 0");
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            if (!proc.WaitForExit(15_000)) { try { proc.Kill(); } catch { /* best effort */ } return false; }
            return proc.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;   // pwsh not on PATH
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChopItUp.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("Could not locate the repo root (ChopItUp.slnx) above " + AppContext.BaseDirectory);
    }
}

public sealed class GateScriptTests : IClassFixture<GateScriptFixture>, IDisposable
{
    private readonly GateScriptFixture _fixture;
    private readonly List<string> _tempDirs = [];

    public GateScriptTests(GateScriptFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        foreach (var dir in _tempDirs) TestDirs.DeleteTree(dir);
    }

    [Fact]
    public void start_branch_creates_room_m_for_the_topmost_READY_row_and_is_idempotent()
    {
        if (!_fixture.PwshAvailable) return;   // pwsh not on PATH: nothing to characterize here
        string repo = NewTempRepo("startbranch_idem");
        WriteRoadmap(repo, "| 5 | Scratch task | [ ] | READY | — | LOW |");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "--quiet", "-m", "seed");

        var first = RunGate(_fixture.StartBranchScript, repo);
        Assert.Equal(0, first.ExitCode);
        Assert.Contains("start-branch: on room/m5 from main", first.Stdout);
        Assert.Equal("room/m5", CurrentBranch(repo));

        // Idempotent: same call again, tree still clean, already on room/m5.
        var second = RunGate(_fixture.StartBranchScript, repo);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("start-branch: already on room/m5", second.Stdout);
        Assert.Equal("room/m5", CurrentBranch(repo));
    }

    [Fact]
    public void start_branch_refuses_a_dirty_tree_with_exit_3()
    {
        if (!_fixture.PwshAvailable) return;
        string repo = NewTempRepo("startbranch_dirty");
        WriteRoadmap(repo, "| 9 | Scratch task | [ ] | READY | — | LOW |");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "--quiet", "-m", "seed");
        File.WriteAllText(Path.Combine(repo, "dirty.txt"), "uncommitted edit");

        var result = RunGate(_fixture.StartBranchScript, repo);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("start-branch: working tree is dirty on main; refusing", result.Stdout);
        Assert.Equal("main", CurrentBranch(repo));   // refused before any checkout
    }

    [Fact]
    public void finish_branch_commits_pending_and_merges_no_ff_into_main_without_a_remote()
    {
        if (!_fixture.PwshAvailable) return;
        string repo = NewTempRepo("finishbranch_merge");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "--quiet", "-m", "seed");
        RunGit(repo, "checkout", "--quiet", "-b", "room/m7");
        File.WriteAllText(Path.Combine(repo, "work.txt"), "pending work, never committed by the test");

        var result = RunGate(_fixture.FinishBranchScript, repo);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("finish-branch: merged ", result.Stdout);
        Assert.Equal("main", CurrentBranch(repo));
        Assert.True(File.Exists(Path.Combine(repo, "work.txt")), "the commit finish-branch made of the pending work should have merged into main");
        Assert.Empty(RunGit(repo, "branch", "--list", "room/m7").Stdout.Trim());   // room branch deleted
        Assert.Contains("Merge room/m7", RunGit(repo, "log", "--oneline", "-3").Stdout);
    }

    [Fact]
    public void finish_branch_refuses_off_a_room_branch_with_exit_3()
    {
        if (!_fixture.PwshAvailable) return;
        string repo = NewTempRepo("finishbranch_notroom");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "--quiet", "-m", "seed");

        var result = RunGate(_fixture.FinishBranchScript, repo);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("finish-branch: HEAD is 'main', not a room/m<row> branch", result.Stdout);
        Assert.Equal("main", CurrentBranch(repo));
    }

    [Fact]
    public void test_gate_exits_2_with_no_solution()
    {
        if (!_fixture.PwshAvailable) return;
        string repo = NewTempRepo("testgate_nosln");

        var result = RunGate(_fixture.TestGateScript, repo);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("test: no .slnx or .sln at " + repo, result.Stdout);
    }

    [Fact]
    public void plan_claims_exits_0_with_no_plan_rows()
    {
        if (!_fixture.PwshAvailable) return;
        string repo = NewTempRepo("planclaims_norows");
        // No 📝/🔨 row: plan-claims.ps1's own loop (tools/skills/roadmap-hub/scripts/plan-claims.ps1:12-24)
        // never finds a plan to check, so $checked stays 0 and $worst stays 0.
        WriteRoadmap(repo, "| 2 | Scratch task | [ ] | READY | — | LOW |");

        // But plan-claims.ps1:9-10 checks for a sibling preflight/Check-PlanClaims.ps1 relative to
        // $PSScriptRoot UNCONDITIONALLY, before it ever looks at a row — not only when a row needs it.
        // That sibling is only present after `--import-skill --overlay` composes the base `roadmap`
        // skill's own preflight/ next to this overlay's scripts/ (Task 1); neither this repo nor CI
        // carries tools/skills/roadmap-hub/preflight/. Driving the committed script uncomposed (cwd
        // pointed at this repo, but -File pointed at the real tools/skills/roadmap-hub/scripts/plan-claims.ps1)
        // exits 2 ("preflight script missing") even with zero rows — confirmed as this test's RED before
        // the fixture below was added (see the builder report for the exact run).
        //
        // So this fixture reproduces composition's shape instead: an UNMODIFIED copy of the real script
        // next to a stub preflight file that only needs to exist. Its content is never read — with
        // $checked staying 0, the `& pwsh -File $gate` call inside the loop (line 22) is never reached.
        string composedScripts = Path.Combine(repo, "_composed", "scripts");
        Directory.CreateDirectory(composedScripts);
        Directory.CreateDirectory(Path.Combine(repo, "_composed", "preflight"));
        string composedPlanClaims = Path.Combine(composedScripts, "plan-claims.ps1");
        File.Copy(_fixture.PlanClaimsScript, composedPlanClaims);
        File.WriteAllText(Path.Combine(repo, "_composed", "preflight", "Check-PlanClaims.ps1"), "");

        var result = RunGate(composedPlanClaims, repo);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("plan-claims: 0 plan(s) checked, worst exit 0", result.Stdout);
    }

    // --- helpers ---------------------------------------------------------------------------------

    private string NewTempRepo(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"chopitup_gatetest_{label}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        RunGit(dir, "init", "--quiet", "-b", "main");
        RunGit(dir, "config", "user.name", "Gate Script Test");
        RunGit(dir, "config", "user.email", "gate-script-test@chopitup.local");
        return dir;
    }

    private static void WriteRoadmap(string repoDir, params string[] rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Scratch ROADMAP");
        sb.AppendLine("<!-- roadmap-schema: whitelist-v3 -->");
        sb.AppendLine();
        sb.AppendLine("## Milestones");
        sb.AppendLine("| # | Title | Status | Ready | Plan | Notes |");
        sb.AppendLine("|---|-------|--------|-------|------|-------|");
        foreach (var row in rows) sb.AppendLine(row);
        File.WriteAllText(Path.Combine(repoDir, "ROADMAP.md"), sb.ToString());
    }

    private string CurrentBranch(string repo) => RunGit(repo, "rev-parse", "--abbrev-ref", "HEAD").Stdout.Trim();

    private static GitResult RunGit(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(30_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"git {string.Join(' ', args)} did not exit within 30s in '{dir}'.");
        }
        var result = new GitResult(proc.ExitCode, stdout, stderr);
        if (result.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({result.ExitCode}) in '{dir}': {stderr}");
        return result;
    }

    private static ScriptResult RunGate(string scriptPath, string workDir)
    {
        var psi = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        bool exited = proc.WaitForExit(60_000);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"{Path.GetFileName(scriptPath)} did not exit within 60s (cwd '{workDir}').");
        }
        // The timed overload returns as soon as the process exits, before the async output/error
        // handlers necessarily finish delivering buffered data; only the parameterless overload
        // guarantees that (same trap DeployScriptTests.cs documents for the deploy script).
        proc.WaitForExit();

        return new ScriptResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record GitResult(int ExitCode, string Stdout, string Stderr);
    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr);
}
