using System.Security.Cryptography;
using System.Text;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Skills;

namespace ChopItUp.Hub.Tests.Skills;

public sealed class SkillStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_skills_" + Guid.NewGuid().ToString("N"));
    private readonly ChopDb _db;
    private readonly SkillHashes _hashes;
    private readonly SkillStore _store;

    public SkillStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        _db.EnsureDatabase();
        _hashes = new SkillHashes(_db);
        _store = new SkillStore(Path.Combine(_dir, "skills"), _hashes);
    }

    /// <summary>Writes a synthetic (never third-party) SKILL.md at <c>&lt;root&gt;/name/SKILL.md</c>
    /// and, unless told otherwise, records its hash in the skills table so Read/List see it as Ok.</summary>
    private byte[] WriteSkill(string name, string content, bool recordHash = true, string? hashOverride = null, string? crlfContent = null)
    {
        var dir = Path.Combine(_store.Root, name);
        Directory.CreateDirectory(dir);
        var bytes = new UTF8Encoding(false).GetBytes(crlfContent ?? content);
        File.WriteAllBytes(Path.Combine(dir, "SKILL.md"), bytes);
        if (recordHash)
        {
            var hash = hashOverride ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            _hashes.Record(name, hash, "test");
        }
        return bytes;
    }

    private const string ValidSkillBody =
        "---\nname: demo\ndescription: A demo skill for tests.\n---\n# Demo Skill\n\nBody text here.\n";

    [Fact]
    public void Read_returns_frontmatter_stripped_body_with_title_and_no_YAML()
    {
        WriteSkill("demo", ValidSkillBody);

        var result = _store.Read("demo");

        var ok = Assert.IsType<SkillRead.Ok>(result);
        Assert.Equal("demo", ok.Skill.Title);
        Assert.DoesNotContain("---", ok.Skill.Body);
        Assert.DoesNotContain("description:", ok.Skill.Body);
        Assert.Contains("Body text here.", ok.Skill.Body);
        Assert.False(ok.Skill.Truncated);
    }

    [Fact]
    public void CRLF_frontmatter_is_stripped_identically_to_LF_and_the_title_still_comes_from_it()
    {
        var crlf = ValidSkillBody.Replace("\n", "\r\n");
        WriteSkill("crlf-demo", ValidSkillBody, crlfContent: crlf);

        var result = _store.Read("crlf-demo");

        var ok = Assert.IsType<SkillRead.Ok>(result);
        Assert.Equal("demo", ok.Skill.Title);
        Assert.DoesNotContain("---", ok.Skill.Body);
        Assert.DoesNotContain("name:", ok.Skill.Body);
        Assert.DoesNotContain("\r", ok.Skill.Body);
    }

    [Fact]
    public void Title_falls_back_to_the_first_H1_heading_when_there_is_no_frontmatter()
    {
        WriteSkill("headed", "# Heading Title\n\nSome body text.\n");

        var ok = Assert.IsType<SkillRead.Ok>(_store.Read("headed"));

        Assert.Equal("Heading Title", ok.Skill.Title);
    }

    [Fact]
    public void Title_falls_back_to_the_directory_name_when_there_is_neither_frontmatter_nor_a_heading()
    {
        WriteSkill("bare-skill", "Just some prose, nothing else.\n");

        var ok = Assert.IsType<SkillRead.Ok>(_store.Read("bare-skill"));

        Assert.Equal("bare-skill", ok.Skill.Title);
    }

    [Fact]
    public void An_invalid_directory_name_is_skipped_by_List_and_NotFound_from_Read()
    {
        Directory.CreateDirectory(_store.Root);
        var badDir = Path.Combine(_store.Root, "Invalid_Name");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "SKILL.md"), ValidSkillBody);

        Assert.IsType<SkillRead.NotFound>(_store.Read("Invalid_Name"));
        Assert.DoesNotContain(_store.List(), s => s.Name == "Invalid_Name");
    }

    [Fact]
    public void A_directory_with_no_SKILL_md_is_skipped_by_List_and_NotFound_from_Read()
    {
        Directory.CreateDirectory(Path.Combine(_store.Root, "empty-dir"));

        Assert.IsType<SkillRead.NotFound>(_store.Read("empty-dir"));
        Assert.DoesNotContain(_store.List(), s => s.Name == "empty-dir");
    }

    [Fact]
    public void A_body_over_the_character_cap_is_truncated_and_flagged()
    {
        var huge = "---\nname: huge\ndescription: big one.\n---\n" + new string('A', 40_000);
        WriteSkill("huge", huge);

        var ok = Assert.IsType<SkillRead.Ok>(_store.Read("huge"));

        Assert.True(ok.Skill.Truncated);
        Assert.Equal(SkillStore.MaxSkillChars, ok.Skill.Body.Length);
    }

    [Fact]
    public void A_SKILL_md_edited_after_its_hash_was_recorded_reads_as_Tampered()
    {
        WriteSkill("edited", ValidSkillBody);
        File.AppendAllText(Path.Combine(_store.Root, "edited", "SKILL.md"), "\ntampered addition\n");

        var result = _store.Read("edited");

        var tampered = Assert.IsType<SkillRead.Tampered>(result);
        Assert.Equal("edited", tampered.Name);
    }

    [Fact]
    public void A_skill_with_no_row_in_the_skills_table_reads_as_Tampered_not_NotFound()
    {
        WriteSkill("unrecorded", ValidSkillBody, recordHash: false);

        var result = _store.Read("unrecorded");

        Assert.IsType<SkillRead.Tampered>(result);
    }

    [Fact]
    public void A_file_over_the_byte_cap_is_Tampered_and_is_never_read()
    {
        var dir = Path.Combine(_store.Root, "oversized");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "SKILL.md");
        using (var fs = new FileStream(path, FileMode.Create))
            fs.SetLength(SkillStore.MaxSkillFileBytes + 1);
        _hashes.Record("oversized", new string('0', 64), "test");

        var calls = 0;
        _store.ReadAllBytes = p => { calls++; return File.ReadAllBytes(p); };

        var result = _store.Read("oversized");

        Assert.IsType<SkillRead.Tampered>(result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_path_traversal_name_resolves_to_nothing()
    {
        Assert.IsType<SkillRead.NotFound>(_store.Read("../../secrets"));
    }

    [Fact]
    public void An_uppercase_name_never_matches_even_when_the_directory_exists()
    {
        WriteSkill("grill", ValidSkillBody);

        Assert.IsType<SkillRead.NotFound>(_store.Read("Grill"));
    }

    [Fact]
    public void List_is_sorted_ordinally_and_logs_a_reason_for_each_skipped_entry()
    {
        WriteSkill("bravo", ValidSkillBody);
        WriteSkill("alpha", ValidSkillBody);
        Directory.CreateDirectory(Path.Combine(_store.Root, "Bad_Name"));
        Directory.CreateDirectory(Path.Combine(_store.Root, "no-skill-file"));

        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        IReadOnlyList<SkillSummary> summaries;
        try
        {
            summaries = _store.List();
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Equal(["alpha", "bravo"], summaries.Select(s => s.Name).ToArray());
        var log = captured.ToString();
        Assert.Contains("Bad_Name", log);
        Assert.Contains("no-skill-file", log);
    }

    [Fact]
    public void List_carries_title_description_and_char_count()
    {
        WriteSkill("demo", ValidSkillBody);

        var summary = Assert.Single(_store.List());

        Assert.Equal("demo", summary.Name);
        Assert.Equal("demo", summary.Title);
        Assert.Equal("A demo skill for tests.", summary.Description);
        Assert.True(summary.Chars > 0);
    }

    [Fact]
    public void EnsureLayout_creates_a_missing_root()
    {
        Assert.False(Directory.Exists(_store.Root));

        _store.EnsureLayout();

        Assert.True(Directory.Exists(_store.Root));
    }

    /// <summary>Path.GetFullPath keeps a trailing separator when the caller passes one; a store
    /// constructed that way must still resolve skills, not answer NotFound on every read.</summary>
    [Fact]
    public void A_store_constructed_with_a_trailing_separator_still_resolves_its_skills()
    {
        WriteSkill("demo", ValidSkillBody);
        var trailing = new SkillStore(_store.Root + Path.DirectorySeparatorChar, _hashes);

        var result = trailing.Read("demo");

        Assert.IsType<SkillRead.Ok>(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
