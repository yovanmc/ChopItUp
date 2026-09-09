using System.Text;
using ChopItUp.Core.Memory;

namespace ChopItUp.Hub.Memory;

/// <summary>One rendered vendor memory file. <see cref="Title"/> and <see cref="Hook"/> are kept
/// alongside the rendered <see cref="Text"/> so the index can be built without re-parsing it.</summary>
public sealed record ExportFile(string FileName, string Text, string Title, string Hook);

/// <summary>The whole export, in memory: one file per live entry plus the rendered index. Touches no
/// directory — <c>MemoryExportWriter</c> (T3) is what stages this to disk.</summary>
public sealed record ExportPlan(IReadOnlyList<ExportFile> Files, string Index, int EntryCount);

/// <summary>Thrown when the render would exceed a consumer limit; a dedicated type rather than
/// <see cref="InvalidOperationException"/> because <c>HostCommands</c>'s shared catch already maps
/// that to exit 3, and AC3 requires exit 6 (claim 21, plan pass 2 M5). <c>MemoryExportWriter</c>
/// catches this by name.</summary>
public sealed class ExportRefusedException(string message) : Exception(message);

/// <summary>Turns the hub's memory store (a few files, many entries each) into the vendor shape one
/// small file per fact reads (D10, plan T1) — pure: reads the store, returns an <see cref="ExportPlan"/>,
/// touches no directory of its own. The inverse of <see cref="MemoryImport"/>, whose shape this must
/// stay readable by: frontmatter carries <c>name</c>/<c>description</c>/<c>metadata.type</c>, and
/// <c>description</c> is where the importer looks for the title first.</summary>
public static class MemoryExport
{
    /// <summary>The consumer's own line cap on the rendered <c>MEMORY.md</c> index (claim 23, D6).</summary>
    public const int MaxIndexLines = 200;
    /// <summary>The consumer's own cap on the rendered index, in UTF-16 code units — the same unit
    /// JavaScript's <c>string.length</c> counts, and C#'s <c>string.Length</c> counts identically
    /// (claim 23, D6). Never UTF-8 bytes.</summary>
    public const int MaxIndexUnits = 25_000;
    /// <summary>A cheap early refusal only, checked before a single file is rendered: <see cref="MaxIndexLines"/>
    /// minus the two lines the header ("# Memories" and the blank line under it) always costs. The
    /// authoritative checks are on the rendered index itself, below — a future header change moves this
    /// floor, never the cap (D6, pass 2 B2).</summary>
    public const int MaxMemories = MaxIndexLines - 2;

    public static ExportPlan Render(MemoryStore store)
    {
        var topics = new List<string> { MemoryStore.CoreTopic };
        topics.AddRange(store.ListTopics().Select(t => t.Slug));   // claim 9, D10: core first, then ListTopics' order

        var live = new List<(string Topic, MemoryEntry Entry)>();
        foreach (var topic in topics)
            foreach (var entry in store.Entries(topic))
                if (!entry.Superseded)   // D7: a superseded entry is a tombstone, not a memory
                    live.Add((topic, entry));

        if (live.Count > MaxMemories)
            throw new ExportRefusedException(
                $"export holds {live.Count} live memories, over the {MaxMemories} floor (the rendered index caps at {MaxIndexLines} lines).");

        var files = new List<ExportFile>(live.Count);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (topic, entry) in live)
        {
            var baseName = MemoryImport.Slugify(topic) + "-" + MemoryImport.Slugify(entry.Title);
            var fileName = baseName + ".md";
            for (var n = 2; !usedNames.Add(fileName); n++)
                fileName = $"{baseName}-{n}.md";

            var stem = Path.GetFileNameWithoutExtension(fileName);
            var text =
                "---\n" +
                "name: " + stem + "\n" +
                "description: " + entry.Title + "\n" +
                "metadata:\n" +
                "  type: " + topic + "\n" +
                "---\n\n" +
                entry.Body + "\n";

            files.Add(new ExportFile(fileName, text, entry.Title, Hook(entry.Body)));
        }

        var indexBuilder = new StringBuilder("# Memories\n\n");
        foreach (var f in files)
        {
            // A title containing '[' or ']' would otherwise break the markdown link; escape both in
            // the link text only (T1).
            var linkTitle = f.Title.Replace("[", "\\[").Replace("]", "\\]");
            indexBuilder.Append("- [").Append(linkTitle).Append("](").Append(f.FileName).Append(')');
            if (f.Hook.Length > 0) indexBuilder.Append(" — ").Append(f.Hook);   // no trailing separator when there is no hook
            indexBuilder.Append('\n');
        }
        var index = indexBuilder.ToString();

        // The authoritative caps (D6, pass 2 B2): measured the way the consumer measures them — on the
        // WHOLE TRIMMED rendered index, not on the entry count. lineCount is newlines + 1 over the
        // trimmed text (so the two-line header costs two); units is the trimmed text's own C#
        // string.Length, the same unit the consumer's `t.length` counts. Never UTF-8 bytes.
        var trimmed = index.Trim();
        var lineCount = trimmed.Count(c => c == '\n') + 1;
        if (lineCount > MaxIndexLines)
            throw new ExportRefusedException($"rendered index is {lineCount} lines, over the {MaxIndexLines}-line cap.");
        if (trimmed.Length > MaxIndexUnits)
            throw new ExportRefusedException($"rendered index is {trimmed.Length} UTF-16 code units, over the {MaxIndexUnits} cap.");

        return new ExportPlan(files, index, files.Count);
    }

    /// <summary>The body's first non-empty line, trimmed, cut at <see cref="MemoryStore.MaxTitleChars"/>
    /// with an ellipsis. An empty body yields the empty string, so its index line carries no hook and
    /// no trailing separator (T1).</summary>
    private static string Hook(string body)
    {
        var line = "";
        foreach (var candidate in body.Split('\n'))
        {
            var trimmedLine = candidate.Trim();
            if (trimmedLine.Length == 0) continue;
            line = trimmedLine;
            break;
        }
        return line.Length > MemoryStore.MaxTitleChars ? line[..(MemoryStore.MaxTitleChars - 1)] + "…" : line;
    }
}
