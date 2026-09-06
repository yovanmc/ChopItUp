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
        Assert.Equal("*.tmp\n", File.ReadAllText(Path.Combine(store.Root, ".gitignore")));
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
}
