using System.Text.RegularExpressions;
using ChopItUp.Core.Memory;
using ChopItUp.Hub.Memory;

namespace ChopItUp.Hub.Tests.Memory;

public sealed class MemoryExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_export_" + Guid.NewGuid().ToString("N"));
    private readonly MemoryStore _store;

    public MemoryExportTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new MemoryStore(_dir);
    }

    public void Dispose() => TestDirs.DeleteTree(_dir);

    [Fact]
    public void T1_core_and_two_topics_render_one_file_per_live_entry_and_none_for_superseded()
    {
        _store.Append(MemoryStore.CoreTopic, "Core Fact", "Core body.", "prov");
        _store.Append("user", "A", "Body A.", "prov");
        _store.Append("feedback", "Old", "Old body.", "prov");
        _store.Supersede("feedback", "Old", "New", "New body.", "prov");

        var plan = MemoryExport.Render(_store);

        Assert.Equal(3, plan.EntryCount);
        Assert.Equal(new[] { "Core Fact", "New", "A" }, plan.Files.Select(f => f.Title));
        Assert.DoesNotContain(plan.Files, f => f.Title == "Old");
    }

    [Fact]
    public void T1_rendered_frontmatter_parses_under_MemoryImport_Frontmatter()
    {
        _store.Append("user", "Who the owner is", "Some body text.", "prov");
        var plan = MemoryExport.Render(_store);
        var file = Assert.Single(plan.Files);

        var fm = MemoryImport.Frontmatter(file.Text);
        Assert.NotNull(fm);
        Assert.Equal(Path.GetFileNameWithoutExtension(file.FileName), fm!.Value.Fields["name"]);
        Assert.Equal("Who the owner is", fm.Value.Fields["description"]);
        Assert.Equal("user", fm.Value.Fields["type"]);
        Assert.Equal("Some body text.", fm.Value.Body);
    }

    [Fact]
    public void T1_the_same_title_in_two_topics_gets_distinct_file_names()
    {
        _store.Append("user", "Shared", "u body.", "prov");
        _store.Append("feedback", "Shared", "f body.", "prov");

        var plan = MemoryExport.Render(_store);

        var names = plan.Files.Where(f => f.Title == "Shared").Select(f => f.FileName).ToList();
        Assert.Equal(2, names.Count);
        Assert.Equal(2, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void T1_the_index_has_one_line_per_file_and_no_body_text()
    {
        // Only the hook (the body's first line) is a pointer; the rest of the body is memory
        // content and must never reach the index.
        _store.Append("user", "A", "Hook A.\nDetail of A that should not appear in the index.", "prov");
        _store.Append("feedback", "B", "Hook B.\nDetail of B that should not appear either.", "prov");

        var plan = MemoryExport.Render(_store);

        var lines = plan.Index.Trim('\n').Split('\n');
        // "# Memories", "", then one line per file.
        Assert.Equal(plan.Files.Count + 2, lines.Length);
        Assert.Contains("Hook A.", plan.Index);
        Assert.Contains("Hook B.", plan.Index);
        Assert.DoesNotContain("Detail of A", plan.Index);
        Assert.DoesNotContain("Detail of B", plan.Index);
    }

    [Fact]
    public void T1_198_entries_render_and_199_throw()
    {
        for (var i = 0; i < 198; i++)
            _store.Append("bulk", $"Title {i}", $"Body {i}.", "prov");

        var plan = MemoryExport.Render(_store);
        Assert.Equal(198, plan.EntryCount);

        _store.Append("bulk", "Title 198", "Body 198.", "prov");
        var ex = Assert.Throws<ExportRefusedException>(() => MemoryExport.Render(_store));
        Assert.Contains("199", ex.Message);
    }

    [Fact]
    public void T1_an_index_whose_units_exceed_25000_throws_even_though_its_line_count_is_fine()
    {
        var longTitle = new string('t', MemoryStore.MaxTitleChars);
        for (var i = 0; i < 150; i++)
            _store.Append("bulk", longTitle, new string('b', 150) + "\n", "prov" + i);

        var ex = Assert.Throws<ExportRefusedException>(() => MemoryExport.Render(_store));
        Assert.Contains("25000", ex.Message);
    }

    [Fact]
    public void T1_an_empty_body_yields_a_hookless_line()
    {
        var topicsDir = Path.Combine(_dir, "topics");
        Directory.CreateDirectory(topicsDir);
        File.WriteAllText(Path.Combine(topicsDir, "misc.md"),
            "# misc\n\n## NoBody\n<!-- p -->\n\n## Second\n<!-- p2 -->\nSecond body.\n");

        var plan = MemoryExport.Render(_store);
        var file = plan.Files.Single(f => f.Title == "NoBody");

        Assert.Equal("", file.Hook);
        Assert.Contains($"[NoBody]({file.FileName})\n", plan.Index);
        Assert.DoesNotContain($"[NoBody]({file.FileName}) — ", plan.Index);
    }

    [Fact]
    public void T1_a_title_containing_a_closing_bracket_produces_a_link_that_still_parses()
    {
        const string title = "Weird] Title";
        _store.Append("user", title, "Body text.", "prov");

        var plan = MemoryExport.Render(_store);
        var file = Assert.Single(plan.Files);

        var line = plan.Index.Split('\n').Single(l => l.StartsWith("- [", StringComparison.Ordinal));
        var match = Regex.Match(line, @"^- \[((?:\\[\[\]]|[^\[\]])*)\]\(([^)]+)\)(?: — (.*))?$");

        Assert.True(match.Success, $"index line did not parse as a link: '{line}'");
        Assert.Equal(file.FileName, match.Groups[2].Value);
        var unescaped = match.Groups[1].Value.Replace("\\]", "]").Replace("\\[", "[");
        Assert.Equal(title, unescaped);
    }

    [Fact]
    public void T1_no_rendered_file_contains_an_approval_marker()
    {
        _store.Append("user", "A", "Body A.", "<!-- approved by owner -->");

        var plan = MemoryExport.Render(_store);

        Assert.All(plan.Files, f => Assert.DoesNotContain("<!--", f.Text));
    }
}
