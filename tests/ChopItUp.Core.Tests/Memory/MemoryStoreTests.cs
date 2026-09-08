using ChopItUp.Core.Memory;

namespace ChopItUp.Core.Tests.Memory;

public sealed class MemoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_memory_" + Guid.NewGuid().ToString("N"));
    private MemoryStore Store => new(Path.Combine(_dir, "memory"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void A1_EnsureLayout_seeds_the_core_once_and_keeps_an_edited_one()
    {
        var store = Store;
        store.EnsureLayout();
        Assert.True(File.Exists(store.CorePath));
        Assert.True(Directory.Exists(store.TopicsDir));
        Assert.StartsWith("# Memory", File.ReadAllText(store.CorePath));
        File.WriteAllText(store.CorePath, "# Mine\n");
        store.EnsureLayout();
        Assert.Equal("# Mine\n", File.ReadAllText(store.CorePath));
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "memory")), store.Root);
        Assert.Equal("*.tmp\n*.bak\n", File.ReadAllText(Path.Combine(store.Root, ".gitignore")));
    }

    [Fact]
    public void A2_ReadCore_returns_the_file_whole_when_short_and_cut_at_6000_when_long()
    {
        var store = Store;
        store.EnsureLayout();
        File.WriteAllText(store.CorePath, "short core");
        var core = store.ReadCore();
        Assert.Equal(("short core", false, 10), (core.Text, core.Truncated, core.FullChars));

        File.WriteAllText(store.CorePath, new string('x', 7_000));
        core = store.ReadCore();
        Assert.Equal(MemoryStore.CoreChars, core.Text.Length);
        Assert.True(core.Truncated);
        Assert.Equal(7_000, core.FullChars);
    }

    [Fact]
    public void A2_a_cut_never_splits_a_surrogate_pair()
    {
        var store = Store;
        store.EnsureLayout();
        File.WriteAllText(store.CorePath, new string('x', MemoryStore.CoreChars - 1) + "😀" + "tail");
        var core = store.ReadCore();
        Assert.Equal(MemoryStore.CoreChars - 1, core.Text.Length);   // the pair straddled the cut and was dropped whole
        Assert.False(char.IsHighSurrogate(core.Text[^1]));
    }

    [Fact]
    public void A3_ListTopics_sorts_slugs_and_ignores_files_that_are_not_slugs()
    {
        var store = Store;
        store.EnsureLayout();
        File.WriteAllText(Path.Combine(store.TopicsDir, "user.md"), "u");
        File.WriteAllText(Path.Combine(store.TopicsDir, "career-ops.md"), "cc");
        File.WriteAllText(Path.Combine(store.TopicsDir, "Bad Name.md"), "no");
        File.WriteAllText(Path.Combine(store.TopicsDir, "notes.txt"), "no");
        Assert.Equal(new[] { "career-ops", "user" }, store.ListTopics().Select(t => t.Slug));
        Assert.Equal(2, store.ListTopics().Single(t => t.Slug == "career-ops").Bytes);
    }

    [Fact]
    public void A3_ReadTopic_returns_null_for_a_missing_topic_and_the_core_for_core()
    {
        var store = Store;
        Assert.Null(store.ReadTopic("nope"));
        Assert.StartsWith("# Memory", store.ReadTopic(MemoryStore.CoreTopic)!.Text);
    }

    [Fact]
    public void A3_ReadTopic_cuts_at_24000_and_says_so()
    {
        var store = Store;
        store.EnsureLayout();
        File.WriteAllText(Path.Combine(store.TopicsDir, "big.md"), new string('t', 30_000));
        var topic = store.ReadTopic("big")!;
        Assert.Equal((MemoryStore.TopicChars, true, 30_000), (topic.Text.Length, topic.Truncated, topic.FullChars));
        File.WriteAllText(Path.Combine(store.TopicsDir, "small.md"), "s");
        Assert.Equal(("s", false, 1), (store.ReadTopic("small")!.Text, store.ReadTopic("small")!.Truncated, store.ReadTopic("small")!.FullChars));
        File.WriteAllText(store.CorePath, new string('c', 30_000));
        Assert.False(store.ReadTopic(MemoryStore.CoreTopic)!.Truncated);   // the whole core, past the injection cap
        Assert.True(store.ReadCore().Truncated);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("A")]
    [InlineData("-a")]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a.b")]
    public void A3_a_non_slug_topic_is_refused_everywhere(string topic)
    {
        var store = Store;
        Assert.Throws<ArgumentException>(() => store.ReadTopic(topic));
        Assert.Throws<ArgumentException>(() => store.Append(topic, "t", "b", "p"));
        Assert.Throws<ArgumentException>(() => MemoryStore.RequireSlug(topic));
    }

    [Fact]
    public void A3_a_65_character_slug_is_refused_and_64_is_accepted()
    {
        Assert.Throws<ArgumentException>(() => MemoryStore.RequireSlug(new string('a', 65)));
        MemoryStore.RequireSlug(new string('a', 64));
    }

    [Fact]
    public void A5_Append_creates_a_topic_with_an_h1_then_appends_entries_separated_by_a_blank_line()
    {
        var store = Store;
        var rel = store.Append("user", " Likes tests ", "  Yes, very much.\n", "approved 2026-09-06T00:00:00Z proposal 1 by opus in room general");
        Assert.Equal("topics/user.md", rel);
        var text = File.ReadAllText(Path.Combine(store.TopicsDir, "user.md"));
        Assert.Equal("# user\n\n## Likes tests\n<!-- approved 2026-09-06T00:00:00Z proposal 1 by opus in room general -->\nYes, very much.\n", text);

        store.Append("user", "Second", "More.", "p2 -- with dashes");
        text = File.ReadAllText(Path.Combine(store.TopicsDir, "user.md"));
        Assert.EndsWith("Yes, very much.\n\n## Second\n<!-- p2 - - with dashes -->\nMore.\n", text);
        Assert.False(File.Exists(Path.Combine(store.TopicsDir, "user.md.tmp")));

        // An owner edit that dropped the trailing newline still gets a clean separator.
        File.WriteAllText(Path.Combine(store.TopicsDir, "user.md"), "# user\n\n## Hand-written\nBy the owner.");
        store.Append("user", "Third", "T.", "p3");
        Assert.EndsWith("By the owner.\n\n## Third\n<!-- p3 -->\nT.\n", File.ReadAllText(Path.Combine(store.TopicsDir, "user.md")));
    }

    [Fact]
    public void A5_Append_with_a_dedup_key_writes_once()
    {
        var store = Store;
        store.Append("user", "Likes tests", "Yes.", "approved 2026-09-06T00:00:00Z proposal 1 by opus in room general", "proposal 1 by opus");
        store.Append("user", "Likes tests", "Yes.", "approved 2026-09-06T00:00:01Z proposal 1 by opus in room general", "proposal 1 by opus");
        store.Append("user", "Other", "No.", "approved 2026-09-06T00:00:02Z proposal 12 by opus in room general", "proposal 12 by opus");
        var text = File.ReadAllText(Path.Combine(store.TopicsDir, "user.md"));
        Assert.Equal(1, text.Split("## Likes tests").Length - 1);
        Assert.Equal(1, text.Split("## Other").Length - 1);
    }

    [Fact]
    public void A5_Append_with_a_dedup_key_matches_a_CRLF_provenance_line()
    {
        var store = Store;
        store.Append("user", "Likes tests", "Yes.", "approved 2026-09-06T00:00:00Z proposal 1 by opus in room general", "proposal 1 by opus");
        var path = Path.Combine(store.TopicsDir, "user.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n"));   // a CRLF editor saved the file
        store.Append("user", "Likes tests", "Yes.", "approved 2026-09-06T00:00:01Z proposal 1 by opus in room general", "proposal 1 by opus");
        var text = File.ReadAllText(path);
        Assert.Equal(1, text.Split("## Likes tests").Length - 1);
    }

    [Fact]
    public void A5_a_body_that_quotes_the_dedup_key_does_not_suppress_a_later_approval()
    {
        var store = Store;
        store.Append("user", "Quoting", "As the note said: Memory proposal 7 by opus was fine.", "approved 2026-09-06T00:00:00Z proposal 3 by sonnet in room general", "proposal 3 by sonnet");
        store.Append("user", "Real seven", "Seven.", "approved 2026-09-06T00:00:01Z proposal 7 by opus in room general", "proposal 7 by opus");
        var text = File.ReadAllText(Path.Combine(store.TopicsDir, "user.md"));
        Assert.Equal(1, text.Split("## Real seven").Length - 1);
    }

    [Fact]
    public void A5_Append_to_core_grows_MEMORY_md_itself()
    {
        var store = Store;
        var rel = store.Append(MemoryStore.CoreTopic, "Owner", "Name is Yovan.", "p");
        Assert.Equal("MEMORY.md", rel);
        var text = File.ReadAllText(store.CorePath);
        Assert.StartsWith("# Memory", text);
        Assert.EndsWith("\n\n## Owner\n<!-- p -->\nName is Yovan.\n", text);
    }

    [Theory]
    [InlineData("", "b")]
    [InlineData("two\nlines", "b")]
    [InlineData("t", "")]
    [InlineData("t", "   ")]
    public void A4_Validate_refuses_an_empty_or_multiline_title_and_an_empty_body(string title, string body) =>
        Assert.Throws<ArgumentException>(() => MemoryStore.Validate(title, body));

    [Fact]
    public void A4_Validate_enforces_the_size_caps()
    {
        Assert.Throws<ArgumentException>(() => MemoryStore.Validate(new string('t', 121), "b"));
        Assert.Throws<ArgumentException>(() => MemoryStore.Validate("t", new string('b', 4_001)));
        MemoryStore.Validate(new string('t', 120), new string('b', 4_000));
    }

    [Fact]
    public void R18_ParseEntries_reads_title_provenance_body_and_the_superseded_mark()
    {
        var text = "# user\n\n## Likes tests\n<!-- approved 2026 proposal 1 by opus in room general -->\nRED first.\n\n## Old fact\n<!-- approved 2026 proposal 2 by codex in room general -->\n<!-- superseded: approved 2026 proposal 3 by opus in room general -->\n\n## Hand written\nNo provenance here.\nTwo lines.\n";
        var entries = MemoryStore.ParseEntries(text);
        Assert.Equal(new[] { "Likes tests", "Old fact", "Hand written" }, entries.Select(e => e.Title));
        Assert.Equal(("approved 2026 proposal 1 by opus in room general", "RED first.", false, 2), (entries[0].Provenance, entries[0].Body, entries[0].Superseded, entries[0].Line));
        Assert.Equal(("", true), (entries[1].Body, entries[1].Superseded));
        Assert.Equal(("", "No provenance here.\nTwo lines.", false), (entries[2].Provenance, entries[2].Body, entries[2].Superseded));
    }

    [Fact]
    public void R18_Supersede_stubs_the_old_entry_appends_the_new_one_and_is_idempotent_on_the_dedup_key()
    {
        var store = Store;
        store.Append("user", "Editor", "Uses Vim.", "approved p1", "proposal 1 by opus");
        store.Append("user", "Shell", "Uses pwsh.", "approved p2", "proposal 2 by opus");
        var written = store.Supersede("user", "Editor", "Editor", "Uses VS Code now.", "approved p3 proposal 3 by codex", "proposal 3 by codex");
        Assert.Equal("topics/user.md", written);
        var text = File.ReadAllText(Path.Combine(store.TopicsDir, "user.md"));
        Assert.Contains("## Editor\n<!-- approved p1 -->\n<!-- superseded: approved p3 proposal 3 by codex -->\n\n## Shell\n<!-- approved p2 -->\nUses pwsh.\n\n## Editor\n<!-- approved p3 proposal 3 by codex -->\nUses VS Code now.\n", text);
        Assert.DoesNotContain("Uses Vim.", text);
        var entries = store.Entries("user");
        Assert.Equal(new[] { ("Editor", true), ("Shell", false), ("Editor", false) }, entries.Select(e => (e.Title, e.Superseded)));
        Assert.Equal(new[] { "Shell", "Editor" }, store.Titles("user"));
        store.Supersede("user", "Editor", "Editor", "Uses VS Code now.", "approved p3 proposal 3 by codex", "proposal 3 by codex");   // replay
        Assert.Equal(text, File.ReadAllText(Path.Combine(store.TopicsDir, "user.md")));
    }

    [Fact]
    public void R18_Supersede_refuses_a_missing_topic_an_unknown_title_and_an_already_superseded_one()
    {
        var store = Store;
        Assert.Throws<KeyNotFoundException>(() => store.Supersede("user", "X", "Y", "b", "p"));
        store.Append("user", "A", "a", "p1");
        Assert.Throws<KeyNotFoundException>(() => store.Supersede("user", "Nope", "B", "b", "p2"));
        store.Supersede("user", "A", "A2", "a2", "p3");
        Assert.Throws<KeyNotFoundException>(() => store.Supersede("user", "A", "A3", "a3", "p4"));
    }

    [Fact]
    public void R18_ProjectedCoreChars_equals_the_length_Append_and_Supersede_actually_write()
    {
        var store = Store;
        store.EnsureLayout();
        var appendProjected = store.ProjectedCoreChars(null, "Rule", "Body.", "prov");
        store.Append("core", "Rule", "Body.", "prov");
        Assert.Equal(File.ReadAllText(store.CorePath).Length, appendProjected);
        var supersedeProjected = store.ProjectedCoreChars("Rule", "Rule", "Longer body here.", "prov2");
        store.Supersede("core", "Rule", "Rule", "Longer body here.", "prov2");
        Assert.Equal(File.ReadAllText(store.CorePath).Length, supersedeProjected);
        Assert.Throws<KeyNotFoundException>(() => store.ProjectedCoreChars("Gone", "T", "b", "p"));
    }

    [Fact]
    public void R18_Search_matches_title_or_body_case_insensitively_skips_superseded_entries_and_caps_hits()
    {
        var store = Store;
        store.Append("core", "Owner", "Yovan, Clinton Township.", "p");
        store.Append("user", "Editor", "Uses Vim.", "p");
        store.Append("user", "Testing", "TDD always; vim keybindings.", "p");
        store.Supersede("user", "Editor", "Editor", "Uses VS Code.", "p2");
        var hits = store.Search("VIM");
        Assert.Equal(new[] { ("user", "Testing") }, hits.Select(h => (h.Topic, h.Title)));
        Assert.Equal("TDD always; vim keybindings.", hits[0].Snippet);
        Assert.Equal(new[] { ("core", "Owner") }, store.Search("clinton").Select(h => (h.Topic, h.Title)));
        Assert.Empty(store.Search("clinton", "user"));
        Assert.Throws<ArgumentException>(() => store.Search("a"));
        Assert.Throws<ArgumentException>(() => store.Search(new string('q', 201)));
        for (var i = 0; i < 60; i++) store.Append("many", $"Entry {i}", "needle " + new string('x', 400), "p");
        var capped = store.Search("needle");
        Assert.Equal(MemoryStore.MaxHits, capped.Count);
        Assert.Equal(MemoryStore.SnippetChars + 1, capped[0].Snippet.Length);   // 300 chars + the ellipsis
    }

    [Fact]
    public void R18_Related_puts_the_replaced_entry_first_then_title_word_matches_up_to_three()
    {
        var store = Store;
        store.Append("user", "Editor of choice", "Vim.", "p");
        store.Append("user", "Shell", "pwsh.", "p");
        store.Append("user", "Editor plugins", "fzf.", "p");
        store.Append("user", "Editor theme", "dark.", "p");
        store.Append("user", "Editor font", "mono.", "p");
        var related = store.Related("user", "Editor: new choice", "Shell");
        Assert.Equal(3, related.Count);
        Assert.Equal(("Shell", true), (related[0].Title, related[0].Replaced));
        Assert.Equal(new[] { "Editor of choice", "Editor plugins" }, related.Skip(1).Select(r => r.Title));
        Assert.Empty(store.Related("user", "Nothing shared", null));
        Assert.Empty(store.Related("missing", "T", null));
    }

    [Fact]
    public void R18_RoomTopic_is_a_valid_slug_for_the_longest_room_id()
    {
        var topic = MemoryStore.RoomTopic(new string('a', 40));
        Assert.Matches(MemoryStore.TopicSlug, topic);
        Assert.Equal("room-general", MemoryStore.RoomTopic("general"));
    }

    [Theory]
    [InlineData("Fine.\n### Sub-heading is fine\n  ## indented is fine")]
    [InlineData("Text.\n```\n# not a heading in a fence? still refused: the parser cannot tell\n```")]
    public void R18_Validate_refuses_a_body_line_that_would_start_an_entry(string body)
    {
        // Writer and parser agree (critique P1-1): a line starting "# " or "## " is an entry boundary everywhere.
        if (body.Contains("# not")) Assert.Throws<ArgumentException>(() => MemoryStore.Validate("T", body));
        else MemoryStore.Validate("T", body);
    }

    [Fact]
    public void R18_a_body_that_quotes_the_superseded_marker_does_not_hide_the_entry()
    {
        var store = Store;
        store.Append("user", "Quoting", "The marker looks like this:\n<!-- superseded: something -->\nand means nothing here.", "p");
        var e = Assert.Single(store.Entries("user"));
        Assert.False(e.Superseded);
        Assert.Equal(new[] { "Quoting" }, store.Titles("user"));
        Assert.Single(store.Search("marker"));
    }

    [Fact]
    public void R18_Supersede_leaves_a_bak_of_the_file_it_rewrote_and_the_bak_is_gitignored()
    {
        var store = Store;
        store.Append("user", "A", "old body", "p1");
        var before = File.ReadAllText(Path.Combine(store.TopicsDir, "user.md"));
        store.Supersede("user", "A", "A", "new body", "p2");
        Assert.Equal(before, File.ReadAllText(Path.Combine(store.TopicsDir, "user.md.bak")));
        Assert.Contains("*.bak", File.ReadAllText(Path.Combine(store.Root, ".gitignore")));
    }

    [Fact]
    public void R18_EnsureLayout_adds_the_bak_ignore_to_an_existing_gitignore_once()
    {
        var store = Store;
        store.EnsureLayout();
        File.WriteAllText(Path.Combine(store.Root, ".gitignore"), "*.tmp\n");   // an M10-era store
        store.EnsureLayout();
        store.EnsureLayout();
        Assert.Equal("*.tmp\n*.bak\n", File.ReadAllText(Path.Combine(store.Root, ".gitignore")));
    }
}
