using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests;

/// <summary>Task 6a: <c>GET /api/skills</c>. Fixture skills are synthetic (D-g: no third-party skill
/// text may enter the repo).</summary>
public sealed class SkillsApiTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_skillsapi_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;

    public async Task InitializeAsync() => _host = await HubTestHost.StartAsync(_dir);
    public async Task DisposeAsync() => await _host.DisposeAsync();

    private SkillStore Store => _host.Services.GetRequiredService<SkillStore>();
    private ChopDb Db => _host.Services.GetRequiredService<ChopDb>();

    private const string ValidSkillBody =
        "---\nname: demo\ndescription: A demo skill for tests.\n---\n# Demo Skill\n\nBody text here.\n";

    /// <summary>Mirrors <c>SkillStoreTests.WriteSkill</c>: writes a synthetic SKILL.md and, unless
    /// told otherwise, records its hash so the store answers Ok rather than Tampered.</summary>
    private void WriteSkill(string name, string content, bool recordHash = true)
    {
        var dir = Path.Combine(Store.Root, name);
        Directory.CreateDirectory(dir);
        var bytes = new UTF8Encoding(false).GetBytes(content);
        File.WriteAllBytes(Path.Combine(dir, "SKILL.md"), bytes);
        if (recordHash)
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            new SkillHashes(Db).Record(name, hash, "test");
        }
    }

    private async Task<JsonElement> GetSkills()
    {
        using var doc = JsonDocument.Parse(await _host.Client.GetStringAsync("api/skills"));
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task An_empty_store_answers_an_empty_list()
    {
        var skills = await GetSkills();

        Assert.Equal(JsonValueKind.Array, skills.ValueKind);
        Assert.Empty(skills.EnumerateArray());
    }

    [Fact]
    public async Task Two_fixture_skills_answer_both_rows_with_exactly_the_four_SkillSummary_fields()
    {
        WriteSkill("alpha", "---\nname: alpha\ndescription: First skill.\n---\n# Alpha\n\nAlpha body.\n");
        WriteSkill("bravo", "---\nname: bravo\ndescription: Second skill.\n---\n# Bravo\n\nBravo body text.\n");

        var rows = (await GetSkills()).EnumerateArray().ToList();

        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            var fields = row.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "chars", "description", "name", "title" }, fields);   // SkillSummary, no others (M-5)
        }

        var alpha = rows.Single(r => r.GetProperty("name").GetString() == "alpha");
        Assert.Equal("alpha", alpha.GetProperty("title").GetString());
        Assert.Equal("First skill.", alpha.GetProperty("description").GetString());
        Assert.True(alpha.GetProperty("chars").GetInt32() > 0);

        var bravo = rows.Single(r => r.GetProperty("name").GetString() == "bravo");
        Assert.Equal("bravo", bravo.GetProperty("title").GetString());
        Assert.Equal("Second skill.", bravo.GetProperty("description").GetString());
    }

    [Fact]
    public async Task A_directory_with_no_SKILL_md_is_absent_from_the_list()
    {
        Directory.CreateDirectory(Path.Combine(Store.Root, "empty-dir"));

        var skills = await GetSkills();

        Assert.Empty(skills.EnumerateArray());
    }

    [Fact]
    public async Task A_skill_whose_SKILL_md_fails_its_fingerprint_check_is_absent_from_the_list()
    {
        WriteSkill("tampered", ValidSkillBody, recordHash: false);   // no row in the skills table => Tampered

        var skills = await GetSkills();

        Assert.Empty(skills.EnumerateArray());
    }
}
