using System.Text.RegularExpressions;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Skills;

namespace ChopItUp.Hub.Tests.Skills;

/// <summary>The shipped <c>tools/skills/goal</c> skill, asserted against the real directory in this
/// repo. What can break here is the file the owner imports: frontmatter that stops parsing, a lost
/// <c>run: true</c>, a body over the prompt budget, a phase tag the hub's grammar rejects, or a
/// participant it names that the roster no longer holds in the class its role needs.</summary>
public sealed class GoalSkillTests : IDisposable
{
    private const string SkillName = "goal";

    /// <summary>The body is rendered into every spawn of the run (conductor, builders and judges), so
    /// its size is a prompt-budget decision. The budget is 8 KB.</summary>
    private const int MaxBytes = 8 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "chopitup_goalskill_" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly ChopDb _db;
    private readonly SkillHashes _hashes;
    private readonly SkillStore _store;

    public GoalSkillTests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(FindRepoRoot(), "tools", "skills", SkillName);
        TemplateDatabase.SeedInto(_root);
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
            if (File.Exists(Path.Combine(dir.FullName, "ChopItUp.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Could not locate the repo root (ChopItUp.slnx) above " + AppContext.BaseDirectory);
    }

    private ResolvedSkill Import()
    {
        var result = SkillImport.Run(_source, _store.Root, force: false, _hashes);
        Assert.True(result.Outcome == SkillImportOutcome.Ok, result.Message);
        return Assert.IsType<SkillRead.Ok>(_store.Read(SkillName)).Skill;
    }

    [Fact]
    public void The_shipped_skill_imports_starts_a_run_and_stays_inside_the_prompt_budget()
    {
        var skill = Import();

        Assert.True(skill.IsRun);
        Assert.False(skill.Truncated);
        Assert.True(skill.Gates is null or { Count: 0 });
        var summary = Assert.Single(_store.List(), s => s.Name == SkillName);
        Assert.InRange(summary.Description.Length, 1, 299);
        var bytes = File.ReadAllBytes(Path.Combine(_source, "SKILL.md")).Length;
        Assert.True(bytes <= MaxBytes, $"SKILL.md is {bytes} bytes, over the {MaxBytes}-byte budget");
    }

    [Fact]
    public void Every_phase_tag_it_shows_parses_with_the_hubs_grammar()
    {
        var tags = Regex.Matches(Import().Body, @"`(phase: [^`]+)`").Select(m => m.Groups[1].Value.Replace("<n>", "1")).ToList();

        Assert.Equal(["plan", "build", "critique", "ping"], tags.Select(t => PhaseTag.TryParse(t, out var tag) ? tag!.Kind : "unparsed").Distinct());
    }

    [Theory]
    [InlineData("gpt-6-astra", "visible")]
    [InlineData("sonnet", "plumbing")]
    [InlineData("opus-judge", "judge")]
    [InlineData("gpt-5.6-sol", "judge")]
    public void Each_participant_it_names_is_seeded_in_the_class_its_role_needs(string id, string role)
    {
        Assert.Contains("@" + id, Import().Body, StringComparison.Ordinal);
        var seeded = Assert.Single(ChopDb.SeedRoster, p => p.Id == id);
        Assert.True(ParticipantClasses.Has(seeded, role), $"{id} is not seeded as {role}");
    }
}
