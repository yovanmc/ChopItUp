using ChopItUp.Core.Memory;
using ChopItUp.Hub.Memory;

namespace ChopItUp.Hub.Tests.Memory;

/// <summary>T5 (ticket 05): the strongest thing this repo can say about the exported shape is not that
/// it looks right, but that our own importer — written against the vendor's shape and shipped before
/// this milestone — reads it back and recovers the same memories. Round-trips a store through
/// <see cref="MemoryExportWriter.Run"/> (the real write path T4's verb uses) and
/// <see cref="MemoryImport.Read"/> (the real read path a vendor-shape import uses).</summary>
public sealed class MemoryExportRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chopitup_roundtrip_" + Guid.NewGuid().ToString("N"));
    private readonly string _storeDir;
    private readonly string _targetDir;
    private readonly MemoryStore _store;

    public MemoryExportRoundTripTests()
    {
        _storeDir = Path.Combine(_root, "store");
        _targetDir = Path.Combine(_root, "target");
        Directory.CreateDirectory(_storeDir);
        _store = new MemoryStore(_storeDir);
    }

    public void Dispose() => TestDirs.DeleteTree(_root);

    [Fact]
    public void T5_export_then_import_recovers_the_same_memories_with_the_known_lossy_topics()
    {
        // D10 newly routes the core through this path, and an earlier revision's fixture omitted it
        // (pass 2 M17) — so the fixture below deliberately includes core and a room topic alongside
        // the four Claude-recognised topics.
        _store.Append(MemoryStore.CoreTopic, "Core Fact", "Core body text.", "prov");
        _store.Append("user", "Who the owner is", "User body text.", "prov");
        _store.Append("feedback", "A piece of feedback", "Feedback body text.", "prov");
        _store.Append("project", "A project note", "Project body text.", "prov");
        _store.Append("reference", "A reference fact", "Reference body text.", "prov");
        _store.Append("room-general", "A room note", "Room body text.", "prov");

        var output = new StringWriter();
        var error = new StringWriter();
        var result = MemoryExportWriter.Run(_store, _targetDir, force: false, acceptNewSource: false, output, error);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", error.ToString());

        var drafts = MemoryImport.Read("claude", _targetDir);

        // The index (MEMORY.md) must not come back as a memory: one draft per exported entry, no more
        // (claim 6, plan Acceptance criterion 2).
        Assert.Equal(6, drafts.Count);

        var byTitle = drafts.ToDictionary(d => d.Title, StringComparer.Ordinal);
        Assert.Equal(6, byTitle.Count);   // titles are exactly the six above, each appearing once

        // Titles round-trip exactly.
        Assert.Contains("Core Fact", byTitle.Keys);
        Assert.Contains("Who the owner is", byTitle.Keys);
        Assert.Contains("A piece of feedback", byTitle.Keys);
        Assert.Contains("A project note", byTitle.Keys);
        Assert.Contains("A reference fact", byTitle.Keys);
        Assert.Contains("A room note", byTitle.Keys);

        // The four Claude-recognised topics round-trip their topic exactly (claim 4): MemoryImport
        // maps frontmatter's `type` straight through only for user/feedback/project/reference.
        Assert.Equal("user", byTitle["Who the owner is"].Topic);
        Assert.Equal("feedback", byTitle["A piece of feedback"].Topic);
        Assert.Equal("project", byTitle["A project note"].Topic);
        Assert.Equal("reference", byTitle["A reference fact"].Topic);

        // `core` and `room-general` are NOT among the four values MemoryImport's ClaudeTopics recognises,
        // so both come back under the "imported" fallback topic. This is asserted deliberately as the
        // known, documented lossy case (ticket 05 / plan T5): a test that omitted these two would let a
        // future change silently break the rest, and a test that claimed they round-trip would be false.
        Assert.Equal("imported", byTitle["Core Fact"].Topic);
        Assert.Equal("imported", byTitle["A room note"].Topic);

        // Bodies survive. MemoryImport.FromFrontmatter demotes a body line starting with '# ' or '## '
        // to '### ' (so a Claude Code memory carrying its own '## ' sections still parses), but
        // MemoryStore.Validate forbids exactly that pattern in any entry body at Append time — so none
        // of the bodies above can contain a line the demotion would touch, and they must come back
        // byte-identical to what was appended.
        Assert.Equal("Core body text.", byTitle["Core Fact"].Body);
        Assert.Equal("User body text.", byTitle["Who the owner is"].Body);
        Assert.Equal("Feedback body text.", byTitle["A piece of feedback"].Body);
        Assert.Equal("Project body text.", byTitle["A project note"].Body);
        Assert.Equal("Reference body text.", byTitle["A reference fact"].Body);
        Assert.Equal("Room body text.", byTitle["A room note"].Body);
    }
}
