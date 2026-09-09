using ChopItUp.Core.Storage;
using ChopItUp.Hub.Skills;

namespace ChopItUp.Hub.Tests.Skills;

/// <summary>Row 23, task 7 (AC8, ticket 07): the shipped <c>tools/skills/consolidate-memory</c> skill,
/// asserted against the real directory in this repo rather than a fixture — a fixture would prove only
/// that the import path works, which <see cref="SkillImportTests"/> already proves. What can break here
/// is the file the owner actually imports: frontmatter that stops parsing, a stray <c>run:</c> that
/// would turn one exchange into a run, or a gate declaration with no script to back it.
///
/// The repo root is found the way <c>GateScriptFixture</c> finds it (walk up from
/// <see cref="AppContext.BaseDirectory"/> to <c>ChopItUp.slnx</c>) — neither skill test file reads
/// <c>tools/skills</c> today, so there was no closer pattern to follow. The import target is a scratch
/// store under <see cref="Path.GetTempPath"/>: nothing here touches an installed skill or a real data
/// directory.</summary>
public sealed class ConsolidateMemorySkillTests : IDisposable
{
    private const string SkillName = "consolidate-memory";

    /// <summary>The skill body is rendered into every spawn of the exchange it roots, so its size is a
    /// prompt-budget decision, not a formatting one. The plan's budget is 3 KB.</summary>
    private const int MaxBytes = 3 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chopitup_consolidate_" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly ChopDb _db;
    private readonly SkillHashes _hashes;
    private readonly SkillStore _store;

    public ConsolidateMemorySkillTests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(FindRepoRoot(), "tools", "skills", SkillName);
        _db = new ChopDb(Path.Combine(_root, "chopitup.db"));
        _db.EnsureDatabase();
        _hashes = new SkillHashes(_db);
        _store = new SkillStore(Path.Combine(_root, "skills"), _hashes);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChopItUp.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("Could not locate the repo root (ChopItUp.slnx) above " + AppContext.BaseDirectory);
    }

    private ResolvedSkill Import()
    {
        var result = SkillImport.Run(_source, _store.Root, force: false, _hashes);
        Assert.True(result.Outcome == SkillImportOutcome.Ok, result.Message);
        var read = _store.Read(SkillName);
        return Assert.IsType<SkillRead.Ok>(read).Skill;
    }

    [Fact]
    public void The_shipped_skill_imports_and_reads_back_Ok_not_Tampered()
    {
        var skill = Import();

        Assert.Equal(SkillName, skill.Name);
        Assert.False(skill.Truncated);
        Assert.IsType<TreeVerification.Ok>(_store.VerifyTree(SkillName));
    }

    [Fact]
    public void The_skill_declares_no_gates_and_does_not_start_a_run()
    {
        var skill = Import();

        // Ticket 07: one exchange, not a run. `run: true` would put a conductor and a run record
        // behind an owner asking for one topic to be tidied, and a declared gate with no script under
        // scripts/ would make the import itself refuse (SkillImportTests covers that refusal).
        Assert.False(skill.IsRun);
        Assert.True(skill.Gates is null or { Count: 0 });
        Assert.Null(skill.Overlay);
    }

    [Fact]
    public void The_skill_carries_a_description_the_list_can_show_and_stays_inside_the_prompt_budget()
    {
        var skill = Import();

        var summary = Assert.Single(_store.List(), s => s.Name == SkillName);
        Assert.NotEqual("", summary.Description);
        Assert.True(summary.Description.Length < 300, $"description is {summary.Description.Length} characters");
        Assert.False(summary.IsRun);

        var bytes = File.ReadAllBytes(Path.Combine(_source, "SKILL.md")).Length;
        Assert.True(bytes <= MaxBytes, $"SKILL.md is {bytes} bytes, over the {MaxBytes}-byte budget");
    }

    [Fact]
    public void The_skill_tells_the_model_the_things_it_cannot_infer()
    {
        var body = Import().Body;

        // Not a style check: each of these is a rule whose absence loses data silently. The heading
        // rule is how the hub carries an entry's approval record forward (a rename drops it); the
        // tombstone rule stops a retired entry coming back as a live, empty one that blocks its own
        // title; the truncation rule stops a model proposing the deletion of a tail it never read.
        Assert.Contains("recall", body, StringComparison.Ordinal);
        Assert.Contains("propose_rewrite", body, StringComparison.Ordinal);
        Assert.Contains("truncated", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("heading", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("superseded", body, StringComparison.OrdinalIgnoreCase);
    }
}
