using System.Text;
using System.Text.RegularExpressions;

namespace ChopItUp.Core.Memory;

public sealed record MemoryTopic(string Slug, long Bytes);

/// <summary>A memory file as handed out: <see cref="Text"/> is cut at the caller's cap
/// (<see cref="MemoryStore.CoreChars"/> for the core, <see cref="MemoryStore.TopicChars"/> for a
/// topic); <see cref="Truncated"/> says the file was longer, <see cref="FullChars"/> how long.</summary>
public sealed record MemoryText(string Text, bool Truncated, int FullChars);

/// <summary>D15: the centralised memory is markdown on disk under <c>&lt;data&gt;\memory\</c> —
/// <c>MEMORY.md</c> (the core, injected into every spawn's prompt) and <c>topics\&lt;slug&gt;.md</c>
/// (fetched with the <c>recall</c> tool). The owner edits these files by hand or approves proposals;
/// nothing else writes here. Pure file I/O: no git (that is the hub's <c>MemoryGit</c>), no SQLite.</summary>
public sealed class MemoryStore
{
    public const string CoreFileName = "MEMORY.md";
    public const string TopicsDirName = "topics";
    /// <summary>The pseudo-topic that appends to the core file itself.</summary>
    public const string CoreTopic = "core";
    /// <summary>≈1,500 tokens at ~4 characters per token (D15). The hub has no tokenizer for either
    /// vendor; characters are the cap, and a longer core is cut at injection with a line saying so.</summary>
    public const int CoreChars = 6_000;
    /// <summary>Approval only ever grows a topic (plan decision 8), so a topic read is capped too
    /// (critique pass 1, P1-6); <c>recall</c> reports the cut and <c>ListTopics</c> reports sizes.</summary>
    public const int TopicChars = 24_000;
    public const int MaxTitleChars = 120;
    public const int MaxBodyChars = 4_000;
    /// <summary>Also the path-traversal guard: a slug can only ever name <c>topics\&lt;slug&gt;.md</c>.</summary>
    public static readonly Regex TopicSlug = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A crash between the temp write and the move must not leave a <c>.tmp</c> that the next
    /// approval's <c>git add -A</c> commits (critique pass 1, P1-14).</summary>
    internal const string GitIgnore = "*.tmp\n";

    internal const string SeedCore = """
        # Memory

        The first 6,000 characters of this file go into every spawn's prompt, and `recall()` returns it to any host. Keep it to what every model should know before it says a word: who the owner is, how they work, standing rules. Detail lives in `topics/<topic>.md` and is fetched with `recall(topic)`. Approved proposals are appended here (topic `core`) or to a topic file; edit freely by hand.

        """;

    public string Root { get; }
    public string CorePath => Path.Combine(Root, CoreFileName);
    public string TopicsDir => Path.Combine(Root, TopicsDirName);

    public MemoryStore(string root) => Root = Path.GetFullPath(root);

    /// <summary>Idempotent: creates the directory, the seed core and the <c>.gitignore</c> once; never
    /// touches an existing file.</summary>
    public void EnsureLayout()
    {
        Directory.CreateDirectory(TopicsDir);
        if (!File.Exists(CorePath)) WriteAtomic(CorePath, SeedCore);
        var ignore = Path.Combine(Root, ".gitignore");
        if (!File.Exists(ignore)) WriteAtomic(ignore, GitIgnore);
    }

    public MemoryText ReadCore()
    {
        EnsureLayout();
        return Cut(File.ReadAllText(CorePath, Utf8), CoreChars);
    }

    /// <summary>Topic files whose stem is a valid slug, sorted ordinally. A file the owner drops in
    /// under a bad name is ignored rather than crashing every spawn.</summary>
    public IReadOnlyList<MemoryTopic> ListTopics()
    {
        EnsureLayout();
        return Directory.EnumerateFiles(TopicsDir, "*.md")
            .Select(f => new MemoryTopic(Path.GetFileNameWithoutExtension(f), new FileInfo(f).Length))
            .Where(t => TopicSlug.IsMatch(t.Slug))
            .OrderBy(t => t.Slug, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The topic's text cut at <see cref="TopicChars"/>; the WHOLE core, uncut, for
    /// <see cref="CoreTopic"/> (that pseudo-topic exists so a host can read past the injection cap);
    /// null when no such topic.</summary>
    public MemoryText? ReadTopic(string topic)
    {
        RequireSlug(topic);
        EnsureLayout();
        var path = PathOf(topic);
        return File.Exists(path) ? Cut(File.ReadAllText(path, Utf8), topic == CoreTopic ? int.MaxValue : TopicChars) : null;
    }

    /// <summary>Appends one approved entry — H2 title, an HTML-comment provenance line, the body — to
    /// the topic file, creating it with an H1 when new, and returns the path written relative to
    /// <see cref="Root"/> with forward slashes. A new file is written beside and moved over; an
    /// existing file is appended to, never rewritten, so a spawn reading it or the owner's editor
    /// holding it never collides with a whole-file replace (critique pass 1, P1-14). With a
    /// <paramref name="dedupKey"/> the write is idempotent: a file that already holds a provenance
    /// comment line containing the key gets nothing (a replayed approval, critique pass 1, P1-4). Only
    /// a comment line counts — a body that quotes the key must not suppress a real approval (critique
    /// pass 2, P2-3). One retry on a sharing violation.</summary>
    public string Append(string topic, string title, string body, string provenance, string? dedupKey = null)
    {
        RequireSlug(topic);
        Validate(title, body);
        EnsureLayout();
        var path = PathOf(topic);
        var entry = new StringBuilder();
        entry.Append('\n').Append("## ").Append(title.Trim()).Append('\n');
        entry.Append("<!-- ").Append(provenance.Replace("--", "- -", StringComparison.Ordinal)).Append(" -->\n");
        entry.Append(body.Trim()).Append('\n');
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) { WriteAtomic(path, "# " + topic + "\n" + entry); break; }
                var existing = File.ReadAllText(path, Utf8);
                if (dedupKey is not null && Regex.IsMatch(existing, "(?m)^<!-- [^\r\n]*" + Regex.Escape(dedupKey) + "[^\r\n]* -->\r?$")) break;
                File.AppendAllText(path, (existing.Length == 0 || existing[^1] == '\n' ? "" : "\n") + entry, Utf8);
                break;
            }
            catch (IOException) when (attempt == 0) { Thread.Sleep(50); }
        }
        return Path.GetRelativePath(Root, path).Replace('\\', '/');
    }

    private static MemoryText Cut(string text, int max)
    {
        if (text.Length <= max) return new MemoryText(text, false, text.Length);
        int cut = max;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;   // never split a surrogate pair
        return new MemoryText(text[..cut], true, text.Length);
    }

    public static void Validate(string? title, string? body)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title is empty.", nameof(title));
        if (title.Contains('\n') || title.Contains('\r')) throw new ArgumentException("title must be one line.", nameof(title));
        if (title.Trim().Length > MaxTitleChars) throw new ArgumentException($"title exceeds {MaxTitleChars} characters.", nameof(title));
        if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("body is empty.", nameof(body));
        if (body.Trim().Length > MaxBodyChars) throw new ArgumentException($"body exceeds {MaxBodyChars} characters.", nameof(body));
    }

    public static void RequireSlug(string? topic)
    {
        if (topic is null || !TopicSlug.IsMatch(topic))
            throw new ArgumentException("topic must be a slug: lowercase letters, digits and hyphens, 1-64 characters, starting with a letter or digit.", nameof(topic));
    }

    private string PathOf(string topic) => topic == CoreTopic ? CorePath : Path.Combine(TopicsDir, topic + ".md");

    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, Utf8);
        File.Move(tmp, path, overwrite: true);
    }
}
