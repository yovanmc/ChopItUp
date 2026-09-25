using System.Diagnostics;
using System.Text;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Skills;

namespace ChopItUp.Hub.Tests.Skills;

/// <summary>The room's <c>/roadmap</c>: <c>tools/Build-RoomSkill.ps1</c> assembles the skill from the
/// owner's shared delivery texts, and the committed <c>tools/skills/roadmap-hub</c> overlay composes
/// onto it at import. The shared texts live on the owner's machine and never enter this repo, so the
/// build runs here against fixture roots of the same shape. The import target is a scratch store
/// under <see cref="Path.GetTempPath"/>: nothing here touches an installed skill or a data
/// directory.</summary>
public sealed class RoomRoadmapSkillTests : IDisposable
{
    private const string CoreHeading = "# Milestone delivery: the shared core";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chopitup_roomroadmap_" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _claudeRoot;
    private readonly string _codexRoot;
    private readonly ChopDb _db;
    private readonly SkillHashes _hashes;
    private readonly SkillStore _store;

    public RoomRoadmapSkillTests()
    {
        _repo = FindRepoRoot();
        _claudeRoot = Path.Combine(_root, "claude");
        _codexRoot = Path.Combine(_root, "codex");
        Write(Path.Combine(_claudeRoot, "skills", "roadmap", "references", "core.md"), CoreHeading + "\n\nThe core's body.\n");
        Write(Path.Combine(_claudeRoot, "skills", "roadmap", "preflight", "Check-RoadmapBudget.ps1"), "# budget preflight stand-in\nexit 0\n");
        Write(Path.Combine(_claudeRoot, "skills", "roadmap", "preflight", "Check-PlanClaims.ps1"), "# claims preflight stand-in\nexit 0\n");
        Write(Path.Combine(_codexRoot, "guidance", "engineering.md"),
            "# Engineering\n\n## Before\n\nNot copied.\n\n## Risk and review\n\nThe shared review rules.\n\n## User data and release\n\nNot copied either.\n");
        _db = new ChopDb(Path.Combine(_root, "chopitup.db"));
        _db.EnsureDatabase();
        _hashes = new SkillHashes(_db);
        _store = new SkillStore(Path.Combine(_root, "skills"), _hashes);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestDirs.DeleteTree(_root);
    }

    [Fact]
    public void The_build_is_the_core_then_the_risk_and_review_section_and_the_two_preflight_scripts()
    {
        string outDir = Path.Combine(_root, "built", "roadmap");

        var result = Build(outDir);

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        string skill = File.ReadAllText(Path.Combine(outDir, "SKILL.md"));
        Assert.StartsWith("---\nname: roadmap\n", skill.Replace("\r\n", "\n"));
        Assert.Contains(CoreHeading + "\n\nThe core's body.", skill.Replace("\r\n", "\n"));
        Assert.Contains("## Risk and review\n\nThe shared review rules.", skill.Replace("\r\n", "\n"));
        Assert.DoesNotContain("Not copied", skill);
        foreach (var script in new[] { "Check-RoadmapBudget.ps1", "Check-PlanClaims.ps1" })
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(_claudeRoot, "skills", "roadmap", "preflight", script)),
                File.ReadAllBytes(Path.Combine(outDir, "preflight", script)));
        }
    }

    [Fact]
    public void The_built_skill_imports_with_the_room_overlay_as_a_run_with_exactly_the_room_gates()
    {
        string outDir = Path.Combine(_root, "built", "roadmap");
        Assert.Equal(0, Build(outDir).ExitCode);

        var import = SkillImport.Run(outDir, _store.Root, force: false, _hashes,
            Path.Combine(_repo, "tools", "skills", "roadmap-hub"));

        Assert.True(import.Outcome == SkillImportOutcome.Ok, import.Message);
        var skill = Assert.IsType<SkillRead.Ok>(_store.Read("roadmap")).Skill;
        Assert.False(skill.Truncated);
        Assert.IsType<TreeVerification.Ok>(_store.VerifyTree("roadmap"));
        Assert.True(skill.IsRun);
        Assert.Equal(new[] { "board-gate", "plan-claims", "start-branch", "test" }, skill.Gates!.Select(g => g.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.StartsWith(CoreHeading, skill.Body.TrimStart());
        // The adapter's two load-bearing rules: the run stops at a reviewed branch, and a sensitive
        // row is not attempted in a room.
        Assert.Contains("The run stops at a reviewed room branch", skill.Overlay);
        Assert.Contains("PARKED: sensitive rows go to a native session", skill.Overlay);
    }

    [Fact]
    public void The_build_refuses_when_the_risk_and_review_section_is_missing()
    {
        Write(Path.Combine(_codexRoot, "guidance", "engineering.md"), "# Engineering\n\n## Something else\n\ntext\n");
        string outDir = Path.Combine(_root, "built", "roadmap");

        var result = Build(outDir);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Risk and review", result.Stdout);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void The_build_refuses_an_output_folder_not_named_roadmap()
    {
        // The import names the skill after its source folder, so any other leaf would install a
        // second skill beside the room's roadmap instead of replacing it.
        string outDir = Path.Combine(_root, "built", "roadmap-room");

        var result = Build(outDir);

        Assert.Equal(1, result.ExitCode);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void The_build_refuses_a_skill_and_overlay_over_the_prompt_budget()
    {
        // Every spawn of a run is given the skill and the overlay, so their size is a per-spawn cost.
        Write(Path.Combine(_claudeRoot, "skills", "roadmap", "references", "core.md"), CoreHeading + "\n\n" + new string('x', 20_000) + "\n");
        string outDir = Path.Combine(_root, "built", "roadmap");

        var result = Build(outDir);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("budget", result.Stdout);
        Assert.False(Directory.Exists(outDir));
    }

    private (int ExitCode, string Stdout, string Stderr) Build(string outDir)
    {
        var psi = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-NoProfile", "-File", Path.Combine(_repo, "tools", "Build-RoomSkill.ps1"),
            "-ClaudeRoot", _claudeRoot, "-CodexRoot", _codexRoot, "-Out", outDir,
        })
        {
            psi.ArgumentList.Add(arg);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("Build-RoomSkill.ps1 did not exit within 60s.");
        }
        proc.WaitForExit();
        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
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
