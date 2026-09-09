using ChopItUp.Core.Memory;
using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Tests.Memory;

/// <summary>Row 23, ticket 08 (item 9 in the plan): this row changes how memory files are written
/// (<c>Rewrite</c>) and adds a value to a persisted field (<c>kind = 'rewrite'</c>), but runs no schema
/// migration — v9 stands (claim 2). The argument that nothing breaks is only as good as the test behind
/// it, so every fixture here is a RAW LITERAL in the shape the row 18 build actually wrote, never
/// produced by this row's own code — except the last test, which closes the loop by reading back a file
/// this row's own writer produced, the shape every future consolidation will have to parse. Each
/// assertion is its own test so a failure names itself.</summary>
public sealed class MemoryV9CompatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_v9compat_" + Guid.NewGuid().ToString("N"));
    private MemoryStore Store => new(Path.Combine(_dir, "memory"));

    // A row 18 topic file, written by hand exactly as MemoryStore.Append/Supersede composed it at that
    // build: two live entries with an "approved ..." provenance comment, and one entry retired by
    // Supersede — heading and provenance kept, body replaced by a "superseded: ..." comment.
    private const string Row18TopicFile = """
        # user

        ## Editor
        <!-- approved 2026-08-01T00:00:00Z proposal 4 by opus in room general -->
        <!-- superseded: approved 2026-08-02T00:00:00Z proposal 7 by opus in room general -->

        ## Shell
        <!-- approved 2026-08-03T00:00:00Z proposal 8 by codex in room general -->
        pwsh.

        ## Editor, new choice
        <!-- approved 2026-08-04T00:00:00Z proposal 9 by opus in room general -->
        VS Code.
        """ + "\n";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private MemoryStore SeededTopic()
    {
        var store = Store;
        store.EnsureLayout();
        File.WriteAllText(Path.Combine(store.TopicsDir, "user.md"), Row18TopicFile);
        return store;
    }

    [Fact]
    public void Entries_over_a_row18_topic_file_returns_all_three_in_file_order_with_the_retired_one_flagged()
    {
        var entries = SeededTopic().Entries("user");
        Assert.Equal(new[] { "Editor", "Shell", "Editor, new choice" }, entries.Select(e => e.Title));
        Assert.Equal(new[] { true, false, false }, entries.Select(e => e.Superseded));
        Assert.Equal("", entries[0].Body);   // the retired entry's body was replaced by the superseded comment
        Assert.Equal("pwsh.", entries[1].Body);
        Assert.Equal("VS Code.", entries[2].Body);
    }

    [Fact]
    public void Titles_excludes_the_superseded_entry_but_keeps_file_order_for_the_rest()
    {
        Assert.Equal(new[] { "Shell", "Editor, new choice" }, SeededTopic().Titles("user"));
    }

    [Fact]
    public void Search_excludes_the_superseded_entry_and_still_finds_a_live_one()
    {
        var hits = SeededTopic().Search("VS Code", "user");
        Assert.DoesNotContain(hits, h => h.Title == "Editor");   // the retired heading, no live hit
        var hit = Assert.Single(hits);
        Assert.Equal("Editor, new choice", hit.Title);
    }

    [Fact]
    public void Related_never_offers_the_superseded_entry_but_still_finds_a_similar_live_one()
    {
        var related = SeededTopic().Related("user", "Editor, third choice", replaces: null);
        Assert.DoesNotContain(related, r => r.Title == "Editor");
        Assert.Contains(related, r => r.Title == "Editor, new choice");
    }

    [Fact]
    public void A_row18_append_row_with_null_flags_and_replaces_reads_back_with_kind_unchanged()
    {
        var db = OpenDb();
        InsertRow18Proposal(db, kind: "append", replaces: null, flags: null);
        var row = new MemoryProposalStore(db).Get(1)!;
        Assert.Equal(("append", (string?)null, (string?)null), (row.Kind, row.Replaces, row.Flags));
        Assert.Empty(ProposalFlags.Parse(row.Flags));
    }

    [Fact]
    public void A_row18_supersede_row_with_replaces_and_flags_reads_back_unchanged()
    {
        var db = OpenDb();
        InsertRow18Proposal(db, kind: "supersede", replaces: "Editor", flags: "instruction-like");
        var row = new MemoryProposalStore(db).Get(1)!;
        Assert.Equal(("supersede", "Editor", "instruction-like"), (row.Kind, row.Replaces, row.Flags));
        Assert.Equal(new[] { "instruction-like" }, ProposalFlags.Parse(row.Flags));
    }

    [Fact]
    public void ProposalFlags_Parse_of_null_is_empty()
    {
        Assert.Empty(ProposalFlags.Parse(null));
    }

    [Fact]
    public void EnsureLayout_upgrades_a_directory_holding_only_a_hand_written_core_without_altering_its_bytes()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "memory"));
        var core = "# Memory\n\nHand-written, predates topics\\ and .gitignore.\n";
        File.WriteAllBytes(Path.Combine(_dir, "memory", "MEMORY.md"), System.Text.Encoding.UTF8.GetBytes(core));
        var store = Store;

        Assert.False(Directory.Exists(store.TopicsDir));
        Assert.False(File.Exists(Path.Combine(store.Root, ".gitignore")));

        store.EnsureLayout();

        Assert.True(Directory.Exists(store.TopicsDir));
        Assert.True(File.Exists(Path.Combine(store.Root, ".gitignore")));
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(core), File.ReadAllBytes(store.CorePath));
    }

    [Fact]
    public void The_rewritten_marker_a_hand_written_row23_file_carries_never_lands_inside_an_entry_body()
    {
        // Exactly the shape MemoryStore.Rewrite composes: the H1, the marker line immediately under it,
        // then the entries — written here as a raw literal, not produced by ComposeRewrite.
        const string rewrittenFile = """
            # user
            <!-- rewritten: approved 2026-09-09T00:00:00Z proposal 12 by opus in room general -->

            ## Editor
            <!-- approved 2026-08-01T00:00:00Z proposal 4 by opus in room general -->
            VS Code.

            ## Shell
            pwsh.
            """ + "\n";
        var store = Store;
        store.EnsureLayout();
        File.WriteAllText(Path.Combine(store.TopicsDir, "user.md"), rewrittenFile);

        var entries = store.Entries("user");
        Assert.Equal(new[] { "Editor", "Shell" }, entries.Select(e => e.Title));
        Assert.All(entries, e => Assert.DoesNotContain("rewritten:", e.Body, StringComparison.Ordinal));
        Assert.All(entries, e => Assert.DoesNotContain("rewritten:", e.Title, StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_produced_by_Rewrite_re_parses_with_the_same_entry_count_it_was_composed_from()
    {
        var store = Store;
        store.Append("user", "Editor", "Vim.", "seed 1");
        store.Append("user", "Shell", "pwsh.", "seed 2");
        var body = "# user\n\n## Editor\nVS Code now.\n\n## New Fact\nSomething new.\n\n## Third Fact\nMore.\n";   // 3 headings

        store.Rewrite("user", body, "approved retest", proposalId: 1);

        var entries = store.Entries("user");
        Assert.Equal(3, entries.Count);
        Assert.Equal(new[] { "Editor", "New Fact", "Third Fact" }, entries.Select(e => e.Title));
    }

    private ChopDb OpenDb()
    {
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        return db;
    }

    private static void InsertRow18Proposal(ChopDb db, string kind, string? replaces, string? flags)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_proposals (room_id, author_id, topic, title, body, status, source, created_at, kind, replaces, flags)
            VALUES ('general', 'opus', 'user', 'Editor', 'VS Code.', 'pending', NULL, '2026-08-01T00:00:00Z', $kind, $replaces, $flags)
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$replaces", (object?)replaces ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$flags", (object?)flags ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
