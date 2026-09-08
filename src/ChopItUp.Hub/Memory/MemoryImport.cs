using System.Text;
using System.Text.RegularExpressions;
using ChopItUp.Core.Memory;

namespace ChopItUp.Hub.Memory;

public sealed record MemoryDraft(string Topic, string Title, string Body, string File);

/// <summary>Turns a vendor's memory directory into proposals (D15: "seeded by importing both vendors'
/// existing memory as proposals"). Pure: reads files, returns drafts; the API decides who authors them.
/// <c>claude</c> = Claude Code's memory directory, one frontmatter file per fact (plan decision 10;
/// verified shape). <c>codex</c> = <c>~/.codex/memories/</c>, known by file names only (F9), so its
/// files are split on headings; a Claude file without frontmatter takes the same path. Reads only
/// top-level <c>*.md</c>, at most <see cref="MaxFiles"/>, each at most <see cref="MaxFileBytes"/>;
/// both vendors' <c>MEMORY.md</c> is an index and skipped.</summary>
public static class MemoryImport
{
    public const int MaxFiles = 300;
    public const int MaxFileBytes = 64 * 1024;
    /// <summary>Drafts, not files: one file splits into many sections. Over this the API refuses the
    /// whole folder with the count, before a single row is created (plan decision 16).</summary>
    public const int MaxDrafts = 200;
    public static readonly string[] Sources = ["claude", "codex"];
    private static readonly string[] ClaudeTopics = ["user", "feedback", "project", "reference"];
    private const string Truncated = "\n\n…(truncated on import)";

    public static IReadOnlyList<MemoryDraft> Read(string source, string directory)
    {
        if (!Sources.Contains(source, StringComparer.Ordinal))
            throw new ArgumentException($"source must be one of: {string.Join(", ", Sources)}.", nameof(source));
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"'{directory}' is not an existing absolute directory.");

        var drafts = new List<MemoryDraft>();
        var files = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(MaxFiles);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, MemoryStore.CoreFileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (new FileInfo(file).Length > MaxFileBytes) continue;
            var text = File.ReadAllText(file).Replace("\r\n", "\n").Replace('\r', '\n');
            if (source == "claude" && Frontmatter(text) is { } fm)
            {
                drafts.Add(FromFrontmatter(fm.Fields, fm.Body, name));
                continue;
            }
            drafts.AddRange(ByHeadings(text, name));
        }
        return drafts;
    }

    /// <summary>`---` on the first line, `key: value` lines (a nested key keeps only its own name, so
    /// `metadata:` / `  type: user` yields `type`), a closing `---` line; the body is what follows.</summary>
    internal static (Dictionary<string, string> Fields, string Body)? Frontmatter(string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return null;
        var close = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (close < 0) return null;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text[4..close].Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (value.Length > 0 && !fields.ContainsKey(key)) fields[key] = value;
        }
        var newline = text.IndexOf('\n', close + 1);
        var body = newline < 0 ? "" : text[(newline + 1)..].Trim();
        return (fields, body);
    }

    private static MemoryDraft FromFrontmatter(Dictionary<string, string> fields, string body, string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var title = FirstLine(fields.GetValueOrDefault("description") ?? fields.GetValueOrDefault("name") ?? stem);
        var type = (fields.GetValueOrDefault("type") ?? "").Trim().ToLowerInvariant();
        var topic = ClaudeTopics.Contains(type, StringComparer.Ordinal) ? type : "imported";
        // Pass 2 P2-4: the Claude shape passes a whole file body through, and the owner's own memory
        // files carry "## " sections, so demote before capping - the structure survives as "###", which
        // MemoryStore's entry parser ignores.
        body = Regex.Replace(body, @"(?m)^(#{1,2}) ", "### ");
        return new MemoryDraft(topic, title, Cap(body.Length == 0 ? title : body), file);
    }

    /// <summary>One draft per `# `/`## `/`### ` section; text before the first heading is its own draft
    /// titled by its first line. The file stem, slugified, is the topic.</summary>
    internal static IReadOnlyList<MemoryDraft> ByHeadings(string text, string file)
    {
        var topic = Slugify(Path.GetFileNameWithoutExtension(file));
        var drafts = new List<MemoryDraft>();
        string? title = null;
        var buffer = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush(drafts, topic, title, buffer, file);
                title = line.TrimStart('#').Trim();
                buffer.Clear();
                continue;
            }
            buffer.Append(line).Append('\n');
        }
        Flush(drafts, topic, title, buffer, file);
        return drafts;
    }

    private static void Flush(List<MemoryDraft> drafts, string topic, string? title, StringBuilder buffer, string file)
    {
        var body = buffer.ToString().Trim();
        if (title is null && body.Length == 0) return;
        title = FirstLine(title is { Length: > 0 } t ? t : body);
        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(file);
        drafts.Add(new MemoryDraft(topic, title, Cap(body.Length == 0 ? title : body), file));
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n', 2)[0].Trim();
        return line.Length > MemoryStore.MaxTitleChars ? line[..(MemoryStore.MaxTitleChars - 1)] + "…" : line;
    }

    private static string Cap(string body) =>
        body.Length <= MemoryStore.MaxBodyChars ? body : body[..(MemoryStore.MaxBodyChars - Truncated.Length)] + Truncated;

    internal static string Slugify(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 64) slug = slug[..64].TrimEnd('-');
        return MemoryStore.TopicSlug.IsMatch(slug) ? slug : "imported";
    }
}
