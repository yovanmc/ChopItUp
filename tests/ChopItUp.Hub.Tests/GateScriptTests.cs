using System.Diagnostics;
using System.Text;

namespace ChopItUp.Hub.Tests;

/// <summary>Regression tests for the roadmap-hub overlay's git-only gate scripts
/// (<c>tools/skills/roadmap-hub/</c>). Every script is driven as an external <c>pwsh -NoProfile -File</c>
/// process, the way <see cref="DeployScriptTests"/> drives <c>Deploy-ChopItUp.ps1</c>, against a real
/// temporary git repository under <see cref="Path.GetTempPath"/> with its own local git identity.
///
/// <c>board-gate</c> and <c>plan-claims</c> against a REAL plan are not exercised here: they need the
/// harness's <c>preflight/Check-RoadmapBudget.ps1</c> / <c>Check-PlanClaims.ps1</c>, which
/// <c>tools/Build-RoomSkill.ps1</c> copies into the room skill it builds on the hub's machine. The
/// repo never carries them, so CI cannot run them. The plan-claims tests below reproduce composition's
/// shape instead: an unmodified copy of the real script next to a stub preflight script.</summary>
public sealed class GateScriptFixture
{
    public string RepoRoot { get; }
    public string StartBranchScript { get; }
    public string TestGateScript { get; }
    public string PlanClaimsScript { get; }

    public GateScriptFixture()
    {
        RepoRoot = FindRepoRoot();
        string scriptsDir = Path.Combine(RepoRoot, "tools", "skills", "roadmap-hub", "scripts");
        StartBranchScript = Path.Combine(scriptsDir, "start-branch.ps1");
        TestGateScript = Path.Combine(scriptsDir, "test.ps1");
        PlanClaimsScript = Path.Combine(scriptsDir, "plan-claims.ps1");
        foreach (var script in new[] { StartBranchScript, TestGateScript, PlanClaimsScript })
        {
            if (!File.Exists(script)) throw new InvalidOperationException($"Expected gate script at '{script}' but it does not exist.");
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
    public void start_branch_with_an_origin_needs_no_gh_login()
    {
        // The room opens no PR, so a clone whose gh has no login must still get its branch.
        string repo = NewRepoWithOrigin("startbranch_nogh", "| 5 | Scratch task | [ ] | READY | — | LOW |");

        var result = RunGate(_fixture.StartBranchScript, repo, NoGhLogin());
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("room/m5", CurrentBranch(repo));
    }

    [Fact]
    public void start_branch_leaves_a_handed_off_room_branch_for_the_next_row_at_origins_main()
    {
        // A finished run leaves the clone on room/m1 with row 1 at 🔨 there. The next run must start
        // row 2 from origin's main, not re-pick row 1 or stack on the unmerged room/m1.
        string repo = NewRepoWithOrigin("startbranch_next",
            "| 1 | First | [ ] | READY | — | LOW |",
            "| 2 | Second | [ ] | READY | — | LOW |");
        RunGit(repo, "checkout", "--quiet", "-b", "room/m1");
        WriteRoadmap(repo,
            "| 1 | First | 🔨 | READY | .scratch/m1-first/brief.md | LOW |",
            "| 2 | Second | [ ] | READY | — | LOW |");
        RunGit(repo, "commit", "--quiet", "-am", "flip row 1");

        var result = RunGate(_fixture.StartBranchScript, repo, NoGhLogin());
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("room/m2", CurrentBranch(repo));
        Assert.Equal(Rev(repo, "origin/main"), Rev(repo, "HEAD"));
    }

    [Fact]
    public void start_branch_without_an_origin_returns_to_main_and_skips_a_row_with_a_room_branch()
    {
        string repo = NewTempRepo("startbranch_noorigin_next");
        WriteRoadmap(repo,
            "| 1 | First | [ ] | READY | — | LOW |",
            "| 2 | Second | [ ] | READY | — | LOW |");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "--quiet", "-m", "seed");
        string mainHead = Rev(repo, "main");
        RunGit(repo, "checkout", "--quiet", "-b", "room/m1");
        WriteRoadmap(repo,
            "| 1 | First | 🔨 | READY | .scratch/m1-first/brief.md | LOW |",
            "| 2 | Second | [ ] | READY | — | LOW |");
        RunGit(repo, "commit", "--quiet", "-am", "flip row 1");

        var result = RunGate(_fixture.StartBranchScript, repo);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("room/m2", CurrentBranch(repo));
        Assert.Equal(mainHead, Rev(repo, "HEAD"));
    }

    [Fact]
    public void start_branch_skips_a_row_a_native_session_already_holds()
    {
        string repo = NewTempRepo("startbranch_skip_building");
        WriteRoadmap(repo,
            "| 1 | First | 🔨 | READY | .scratch/m1-first/brief.md | LOW |",
            "| 2 | Second | [ ] | READY | — | LOW |");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "--quiet", "-m", "seed");

        var result = RunGate(_fixture.StartBranchScript, repo);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("room/m2", CurrentBranch(repo));
    }

    [Fact]
    public void plan_claims_stages_an_ignored_brief_so_the_hub_commits_it()
    {
        // The hub commits with `git add -A`, which skips ignored files, and a room spawn cannot run
        // git add, so the gate that checks the brief is also what puts it in the index.
        string repo = NewTempRepo("planclaims_stage");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".scratch/\n_composed/\n");
        WriteRoadmap(repo, "| 3 | Scratch task | 🔨 | READY | .scratch/m3-task/brief.md | LOW |");
        Directory.CreateDirectory(Path.Combine(repo, ".scratch", "m3-task"));
        File.WriteAllText(Path.Combine(repo, ".scratch", "m3-task", "brief.md"), "# m3 Scratch task\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "--quiet", "-m", "seed");
        string planClaims = ComposePlanClaims(repo, preflightBody: "exit 0");

        var result = RunGate(planClaims, repo);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("plan-claims: 1 plan(s) checked, worst exit 0", result.Stdout);
        Assert.Equal(".scratch/m3-task/brief.md", RunGit(repo, "ls-files", "--", ".scratch/m3-task/brief.md").Stdout.Trim());
    }

    [Fact]
    public void test_gate_exits_2_with_no_solution()
    {
        string repo = NewTempRepo("testgate_nosln");

        var result = RunGate(_fixture.TestGateScript, repo);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("test: no .slnx or .sln at " + repo, result.Stdout);
    }

    [Fact]
    public void plan_claims_exits_0_with_no_plan_rows()
    {
        string repo = NewTempRepo("planclaims_norows");
        // No 📝/🔨 row: plan-claims.ps1's own loop (tools/skills/roadmap-hub/scripts/plan-claims.ps1:12-24)
        // never finds a plan to check, so $checked stays 0 and $worst stays 0.
        WriteRoadmap(repo, "| 2 | Scratch task | [ ] | READY | — | LOW |");

        // But plan-claims.ps1 checks for a sibling preflight/Check-PlanClaims.ps1 relative to
        // $PSScriptRoot UNCONDITIONALLY, before it ever looks at a row, not only when a row needs it.
        // That sibling is only present after `--import-skill --overlay` composes the base `roadmap`
        // skill's own preflight/ next to this overlay's scripts/; neither this repo nor CI carries
        // tools/skills/roadmap-hub/preflight/. Driving the committed script uncomposed (cwd pointed at
        // this repo, but -File pointed at the real tools/skills/roadmap-hub/scripts/plan-claims.ps1)
        // exits 2 ("preflight script missing") even with zero rows.
        //
        // So this fixture reproduces composition's shape instead: an UNMODIFIED copy of the real script
        // next to a stub preflight file that only needs to exist. Its content is never read: with
        // $checked staying 0, the `& pwsh -File $gate` call inside the loop is never reached.
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

    /// <summary>A repo seeded with <paramref name="rows"/> on main and pushed to a bare origin beside
    /// it, with origin/HEAD set, so start-branch takes the same fetch-and-reset path a real room does.</summary>
    private string NewRepoWithOrigin(string label, params string[] rows)
    {
        string origin = Path.Combine(Path.GetTempPath(), $"chopitup_gatetest_{label}_origin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(origin);
        _tempDirs.Add(origin);
        RunGit(origin, "init", "--quiet", "--bare", "-b", "main");

        string repo = NewTempRepo(label);
        WriteRoadmap(repo, rows);
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "--quiet", "-m", "seed");
        RunGit(repo, "remote", "add", "origin", origin);
        RunGit(repo, "push", "--quiet", "-u", "origin", "main");
        RunGit(repo, "remote", "set-head", "origin", "main");
        return repo;
    }

    /// <summary>gh with an empty config folder and no token variables has no login, the state that
    /// made the old start-branch park before any branch existed.</summary>
    private Dictionary<string, string?> NoGhLogin()
    {
        string config = Path.Combine(Path.GetTempPath(), $"chopitup_gatetest_ghconfig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(config);
        _tempDirs.Add(config);
        return new Dictionary<string, string?>
        {
            ["GH_CONFIG_DIR"] = config,
            ["GH_TOKEN"] = null,
            ["GITHUB_TOKEN"] = null,
            ["GH_ENTERPRISE_TOKEN"] = null,
            ["GITHUB_ENTERPRISE_TOKEN"] = null,
        };
    }

    /// <summary>An unmodified copy of the real plan-claims.ps1 next to a stub preflight script, the
    /// shape `--import-skill --overlay` composes.</summary>
    private string ComposePlanClaims(string repo, string preflightBody)
    {
        string composedScripts = Path.Combine(repo, "_composed", "scripts");
        Directory.CreateDirectory(composedScripts);
        Directory.CreateDirectory(Path.Combine(repo, "_composed", "preflight"));
        string composedPlanClaims = Path.Combine(composedScripts, "plan-claims.ps1");
        File.Copy(_fixture.PlanClaimsScript, composedPlanClaims);
        File.WriteAllText(Path.Combine(repo, "_composed", "preflight", "Check-PlanClaims.ps1"), preflightBody);
        return composedPlanClaims;
    }

    private string CurrentBranch(string repo) => RunGit(repo, "rev-parse", "--abbrev-ref", "HEAD").Stdout.Trim();

    private static string Rev(string repo, string rev) => RunGit(repo, "rev-parse", rev).Stdout.Trim();

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
        // Read stdout and stderr concurrently, not sequentially: reading one synchronously to
        // completion before starting the other can deadlock if git fills the OTHER pipe's OS buffer
        // while this call is blocked waiting on the first (the same trap DeployScriptTests.cs avoids
        // with its async event handlers).
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(30_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"git {string.Join(' ', args)} did not exit within 30s in '{dir}'.");
        }
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        var result = new GitResult(proc.ExitCode, stdout, stderr);
        if (result.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({result.ExitCode}) in '{dir}': {stderr}");
        return result;
    }

    private static ScriptResult RunGate(string scriptPath, string workDir, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var psi = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null) psi.Environment.Remove(name);
            else psi.Environment[name] = value;
        }
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
