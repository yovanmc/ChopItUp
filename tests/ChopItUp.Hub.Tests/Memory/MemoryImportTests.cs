using ChopItUp.Hub.Memory;

namespace ChopItUp.Hub.Tests.Memory;

public sealed class MemoryImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_import_" + Guid.NewGuid().ToString("N"));

    public MemoryImportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);

    [Fact]
    public void A6_a_claude_memory_directory_yields_one_draft_per_frontmatter_file_and_skips_the_index()
    {
        Write("MEMORY.md", "- [User profile](user_profile.md) — hook\n");
        Write("user_profile.md", "---\nname: user-profile\ndescription: \"Who the owner is\"\nmetadata:\n  type: user\n---\n\nSoftware engineer. Likes tests.\n");
        Write("feedback_tdd.md", "---\nname: feedback-tdd\ndescription: RED before GREEN\nmetadata:\n  type: feedback\n---\nAlways run the failing test first.\n**Why:** trust.\n");
        Write("odd_type.md", "---\nname: odd\ndescription: Something\nmetadata:\n  type: mystery\n---\nBody.\n");
        Write("plain.md", "Just a note without frontmatter.\n\n## Second part\nMore.\n");
        Write("notes.txt", "ignored");

        var drafts = MemoryImport.Read("claude", _dir);
        Assert.Equal(new[]
        {
            ("feedback", "RED before GREEN", "feedback_tdd.md"),
            ("imported", "Something", "odd_type.md"),
            ("plain", "Just a note without frontmatter.", "plain.md"),
            ("plain", "Second part", "plain.md"),
            ("user", "Who the owner is", "user_profile.md"),
        }, drafts.Select(d => (d.Topic, d.Title, d.File)));
        Assert.Equal("Software engineer. Likes tests.", drafts.Single(d => d.Topic == "user").Body);
        Assert.StartsWith("Always run the failing test first.\n**Why:** trust.", drafts.Single(d => d.Topic == "feedback").Body);
    }

    [Fact]
    public void A6_a_codex_directory_is_split_on_headings_with_the_file_stem_as_topic()
    {
        Write("MEMORY.md", "# index\n");
        Write("raw_memories.md", "## Likes short answers\nKeep it brief.\n\n### Uses PowerShell 7\nNot 5.1.\n\n## Empty heading\n");
        Write("memory_summary.md", "The owner builds a chat hub.\nTwo lines.\n");

        var drafts = MemoryImport.Read("codex", _dir);
        Assert.Equal(new[]
        {
            ("memory-summary", "The owner builds a chat hub.", "The owner builds a chat hub.\nTwo lines."),
            ("raw-memories", "Likes short answers", "Keep it brief."),
            ("raw-memories", "Uses PowerShell 7", "Not 5.1."),
            ("raw-memories", "Empty heading", "Empty heading"),
        }, drafts.Select(d => (d.Topic, d.Title, d.Body)));
    }

    [Fact]
    public void A6_long_titles_and_bodies_are_capped_and_big_files_are_skipped()
    {
        Write("a.md", "## " + new string('t', 200) + "\n" + new string('b', 5_000) + "\n");
        Write("big.md", new string('x', MemoryImport.MaxFileBytes + 1));
        var d = Assert.Single(MemoryImport.Read("codex", _dir));
        Assert.Equal(120, d.Title.Length);
        Assert.EndsWith("…", d.Title);
        Assert.Equal(4_000, d.Body.Length);
        Assert.EndsWith("…(truncated on import)", d.Body);
    }

    [Fact]
    public void A6_bad_sources_and_paths_are_refused()
    {
        Assert.Throws<ArgumentException>(() => MemoryImport.Read("gemini", _dir));
        Assert.Throws<DirectoryNotFoundException>(() => MemoryImport.Read("claude", "relative\\path"));
        Assert.Throws<DirectoryNotFoundException>(() => MemoryImport.Read("claude", Path.Combine(_dir, "missing")));
        Assert.Throws<DirectoryNotFoundException>(() => MemoryImport.Read("claude", ""));
    }

    [Theory]
    [InlineData("raw_memories", "raw-memories")]
    [InlineData("My Notes (2026)!", "my-notes-2026")]
    [InlineData("---", "imported")]
    [InlineData("", "imported")]
    public void A6_slugify(string stem, string expected) => Assert.Equal(expected, MemoryImport.Slugify(stem));

    [Fact]
    public void R18_frontmatter_headings_in_the_body_are_demoted_so_they_are_not_entry_boundaries()
    {
        Write("note.md", "---\nname: note\ndescription: Has headings\nmetadata:\n  type: user\n---\nIntro.\n## Section\nDetail.\n");
        var d = Assert.Single(MemoryImport.Read("claude", _dir));
        Assert.Equal("Intro.\n### Section\nDetail.", d.Body);
    }
}
