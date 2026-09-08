using System.Text;
using System.Text.RegularExpressions;

namespace ChopItUp.Core.Memory;

public sealed record MemoryTopic(string Slug, long Bytes);
public sealed record MemoryEntry(string Title, string Provenance, string Body, bool Superseded, int Line);
public sealed record MemoryHit(string Topic, string Title, string Snippet);
public sealed record RelatedEntry(string Title, string Snippet, bool Replaced);

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
    /// <summary>Row 18 (L7), budget ruling: the room topic (decision 8) gets its own, smaller cap on
    /// top of the core's ≈1,500 tokens — ≈500 tokens more, the rest reachable with <c>recall(topic)</c>.</summary>
    public const int RoomChars = 2_000;
    /// <summary>A topic can be any size (row 18's supersede shrinks it; approvals grow it), so a topic
    /// read is capped too (critique pass 1, P1-6); <c>recall</c> reports the cut and <c>ListTopics</c>
    /// reports sizes.</summary>
    public const int TopicChars = 24_000;
    public const int MaxTitleChars = 120;
    public const int MaxBodyChars = 4_000;
    /// <summary>Also the path-traversal guard: a slug can only ever name <c>topics\&lt;slug&gt;.md</c>.</summary>
    public static readonly Regex TopicSlug = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

    public const string SupersededPrefix = "<!-- superseded: ";
    public const int SnippetChars = 300;
    public const int RelatedSnippetChars = 160;
    public const int MaxHits = 50;
    public const int MaxRelated = 3;
    public const int MinQueryChars = 2;
    public const int MaxQueryChars = 200;
    private const string CommentOpen = "<!-- ";
    private const string CommentClose = " -->";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A crash between the temp write and the move must not leave a <c>.tmp</c> that the next
    /// approval's <c>git add -A</c> commits (critique pass 1, P1-14). <c>*.bak</c> (row 18, decision 1)
    /// is <see cref="Supersede"/>'s pre-rewrite copy of the file: not part of the git history either.</summary>
    internal const string GitIgnore = "*.tmp\n*.bak\n";

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
        else { var text = File.ReadAllText(ignore, Utf8); if (!text.Contains("*.bak", StringComparison.Ordinal)) File.AppendAllText(ignore, (text.Length == 0 || text[^1] == '\n' ? "" : "\n") + "*.bak\n", Utf8); }
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

    /// <summary>Row 18 (L7): the topic a directory room's spawns also receive. Room ids are at most
    /// <c>RoomIds.MaxChars</c> (40) of <c>[a-z0-9-]</c>, so this always satisfies <see cref="TopicSlug"/>.</summary>
    public static string RoomTopic(string roomId) => "room-" + roomId;

    /// <summary>Like <see cref="ReadTopic(string)"/> but cut at <paramref name="max"/> — the spawn
    /// injection of a room topic uses <see cref="RoomChars"/>.</summary>
    public MemoryText? ReadTopic(string topic, int max)
    {
        RequireSlug(topic);
        EnsureLayout();
        var path = PathOf(topic);
        return File.Exists(path) ? Cut(File.ReadAllText(path, Utf8), max) : null;
    }

    public IReadOnlyList<MemoryEntry> Entries(string topic)
    {
        RequireSlug(topic);
        EnsureLayout();
        var path = PathOf(topic);
        return File.Exists(path) ? ParseEntries(File.ReadAllText(path, Utf8)) : [];
    }

    /// <summary>Non-superseded titles, file order: what <c>recall()</c> lists per topic.</summary>
    public IReadOnlyList<string> Titles(string topic) => Entries(topic).Where(e => !e.Superseded).Select(e => e.Title).ToList();

    /// <summary>Row 18 (L1, decision 1): the entry titled <paramref name="replaces"/> keeps its heading
    /// and provenance and gains a superseded comment; its body goes; the new entry is appended. The file
    /// is rewritten whole (decision 2). Idempotent on <paramref name="dedupKey"/> like <see cref="Append"/>.
    /// Throws <see cref="KeyNotFoundException"/> when the topic or a live entry with that title is missing.</summary>
    public string Supersede(string topic, string replaces, string title, string body, string provenance, string? dedupKey = null)
    {
        RequireSlug(topic);
        Validate(title, body);
        EnsureLayout();
        var path = PathOf(topic);
        if (!File.Exists(path)) throw new KeyNotFoundException($"No topic '{topic}'.");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var existing = File.ReadAllText(path, Utf8);
                if (dedupKey is not null && HasProvenance(existing, dedupKey)) break;
                var composed = ComposeSupersede(existing, replaces, title, body, provenance);   // throws before anything is touched
                File.Copy(path, path + ".bak", overwrite: true);                                 // decision 1, critique P1-6: the old body survives without git
                WriteAtomic(path, composed);
                break;
            }
            catch (IOException) when (attempt == 0) { Thread.Sleep(50); }
        }
        return Path.GetRelativePath(Root, path).Replace('\\', '/');
    }

    /// <summary>What the core would be, in characters, after this approval — composed exactly as
    /// <see cref="Append"/> or <see cref="Supersede"/> would write it (decision 3).</summary>
    public int ProjectedCoreChars(string? replaces, string title, string body, string provenance)
    {
        Validate(title, body);
        EnsureLayout();
        var existing = File.ReadAllText(CorePath, Utf8);
        return replaces is null
            ? existing.Length + Separator(existing).Length + Entry(title, body, provenance).Length
            : ComposeSupersede(existing, replaces, title, body, provenance).Length;
    }

    /// <summary>Case-insensitive substring over titles and bodies of every non-superseded entry, core
    /// first then topics in slug order, or one topic; at most <see cref="MaxHits"/> (decision 7).</summary>
    public IReadOnlyList<MemoryHit> Search(string query, string? topic = null)
    {
        var q = (query ?? "").Trim();
        if (q.Length < MinQueryChars || q.Length > MaxQueryChars)
            throw new ArgumentException($"query must be {MinQueryChars} to {MaxQueryChars} characters.", nameof(query));
        if (topic is not null) RequireSlug(topic);
        EnsureLayout();
        IEnumerable<string> topics = topic is not null ? [topic] : ListTopics().Select(t => t.Slug).Prepend(CoreTopic);
        var hits = new List<MemoryHit>();
        foreach (var t in topics)
            foreach (var e in Entries(t))
            {
                if (e.Superseded) continue;
                if (!e.Title.Contains(q, StringComparison.OrdinalIgnoreCase) && !e.Body.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                hits.Add(new MemoryHit(t, e.Title, Snippet(e.Body, SnippetChars)));
                if (hits.Count == MaxHits) return hits;
            }
        return hits;
    }

    /// <summary>The approval card's context (L6): the entry <paramref name="replaces"/> names first,
    /// then live entries whose title shares a word of four or more letters or digits with
    /// <paramref name="title"/>, file order, at most <see cref="MaxRelated"/>.</summary>
    public IReadOnlyList<RelatedEntry> Related(string topic, string title, string? replaces)
    {
        var words = Words(title);
        var replaced = new List<RelatedEntry>();
        var similar = new List<RelatedEntry>();
        foreach (var e in Entries(topic))
        {
            if (e.Superseded) continue;
            if (replaces is not null && e.Title == replaces.Trim()) replaced.Add(new RelatedEntry(e.Title, Snippet(e.Body, RelatedSnippetChars), true));
            else if (Words(e.Title).Overlaps(words)) similar.Add(new RelatedEntry(e.Title, Snippet(e.Body, RelatedSnippetChars), false));
        }
        return replaced.Concat(similar).Take(MaxRelated).ToList();
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
        var entry = Entry(title, body, provenance);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) { WriteAtomic(path, "# " + topic + "\n" + entry); break; }
                var existing = File.ReadAllText(path, Utf8);
                if (dedupKey is not null && HasProvenance(existing, dedupKey)) break;
                File.AppendAllText(path, Separator(existing) + entry, Utf8);
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
        if (Regex.IsMatch(body, @"(?m)^#{1,2} ")) throw new ArgumentException("body must not contain a line starting with '# ' or '## ' (that starts a new entry); indent it or use '###'.", nameof(body));
    }

    public static void RequireSlug(string? topic)
    {
        if (topic is null || !TopicSlug.IsMatch(topic))
            throw new ArgumentException("topic must be a slug: lowercase letters, digits and hyphens, 1-64 characters, starting with a letter or digit.", nameof(topic));
    }

    private string PathOf(string topic) => topic == CoreTopic ? CorePath : Path.Combine(TopicsDir, topic + ".md");

    internal static IReadOnlyList<MemoryEntry> ParseEntries(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var entries = new List<MemoryEntry>();
        var i = 0;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("## ", StringComparison.Ordinal)) { i++; continue; }
            var start = i;
            var title = lines[i++][3..].Trim();
            var provenance = "";
            if (i < lines.Length && IsComment(lines[i]) && !lines[i].StartsWith(SupersededPrefix, StringComparison.Ordinal))
                provenance = CommentText(lines[i++]);
            // The marker counts only here, in the header position (decision 1, critique P1-2); a body
            // line that quotes it is text, like a body that quotes a dedup key (M10 P2-3).
            var superseded = false;
            if (i < lines.Length && lines[i].StartsWith(SupersededPrefix, StringComparison.Ordinal)) { superseded = true; i++; }
            var body = new StringBuilder();
            while (i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal))
                body.Append(lines[i++]).Append('\n');
            entries.Add(new MemoryEntry(title, provenance, body.ToString().Trim(), superseded, start));
        }
        return entries;
    }

    internal static string ComposeSupersede(string existing, string replaces, string title, string body, string provenance)
    {
        var entries = ParseEntries(existing);
        var target = entries.FirstOrDefault(e => !e.Superseded && e.Title == replaces.Trim())
            ?? throw new KeyNotFoundException($"No entry titled '{replaces.Trim()}' to replace.");
        var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();
        var next = entries.SkipWhile(e => e != target).Skip(1).FirstOrDefault();
        var end = next?.Line ?? lines.Count;
        var stub = new List<string> { lines[target.Line] };
        if (target.Provenance.Length > 0) stub.Add(lines[target.Line + 1]);
        stub.Add(SupersededPrefix + Sanitize(provenance) + CommentClose);
        stub.Add("");
        lines.RemoveRange(target.Line, end - target.Line);
        lines.InsertRange(target.Line, stub);
        var text = string.Join('\n', lines);
        if (next is null) text = text.TrimEnd('\n') + "\n";
        return text + Separator(text) + Entry(title, body, provenance);
    }

    private static string Entry(string title, string body, string provenance) =>
        "\n## " + title.Trim() + "\n" + CommentOpen + Sanitize(provenance) + CommentClose + "\n" + body.Trim() + "\n";

    private static string Separator(string existing) => existing.Length == 0 || existing[^1] == '\n' ? "" : "\n";
    private static string Sanitize(string provenance) => provenance.Replace("--", "- -", StringComparison.Ordinal);
    private static bool IsComment(string line) => line.StartsWith(CommentOpen, StringComparison.Ordinal) && line.TrimEnd().EndsWith(CommentClose, StringComparison.Ordinal);
    private static string CommentText(string line) => line.Trim()[CommentOpen.Length..^CommentClose.Length].Trim();
    private static bool HasProvenance(string existing, string dedupKey) =>
        Regex.IsMatch(existing, "(?m)^<!-- [^\r\n]*" + Regex.Escape(dedupKey) + "[^\r\n]* -->\r?$");
    private static string Snippet(string body, int max) => body.Length <= max ? body : Cut(body, max).Text + "…";
    private static HashSet<string> Words(string s) =>
        new(Regex.Split(s.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+").Where(w => w.Length >= 4), StringComparer.Ordinal);

    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, Utf8);
        File.Move(tmp, path, overwrite: true);
    }
}
