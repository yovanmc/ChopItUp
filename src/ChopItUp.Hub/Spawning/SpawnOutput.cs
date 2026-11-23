using System.Text.Json;

namespace ChopItUp.Hub.Spawning;

public sealed record ShellCommand(string Command, bool Denied);

/// <summary>One web call a Claude spawn made: <c>WebSearch</c> with its query or <c>WebFetch</c> with its URL.</summary>
public sealed record WebRequest(string Tool, string Target, bool Denied);

/// <summary>Reads the two CLIs' structured stdout for what the trail records: which shell commands
/// the model ran and, for Claude, which web searches and fetches it made. Shapes measured: Claude
/// `--output-format stream-json` emits one JSON object per line, Bash calls as
/// <c>{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_…","name":"Bash","input":{"command":"…"}}]}}</c>
/// and a final <c>{"type":"result",…,"result":"…","permission_denials":[{"tool_use_id":"…",…}]}</c>;
/// Codex `--json` emits <c>{"type":"item.started"|"item.completed","item":{"id":"item_1","type":"command_execution","command":"…",…}}</c>.
/// Lenient by design: a line that is not JSON, or JSON of another shape, contributes nothing and
/// nothing here throws — the commit still happens, with an empty log.</summary>
public static class SpawnOutput
{
    public const int MaxCommands = 50;
    public const int MaxCommandChars = 400;

    /// <summary>Claude: every Bash tool_use in assistant messages, in order; one the final result lists
    /// under permission_denials (by tool_use_id) is marked denied — it never ran.</summary>
    public static IReadOnlyList<ShellCommand> ClaudeShellCommands(string stdout) =>
        ClaudeToolCalls(stdout, new Dictionary<string, string> { ["Bash"] = "command" })
            .Select(c => new ShellCommand(Flatten(c.Target), c.Denied)).Take(MaxCommands).ToList();

    /// <summary>Claude: every WebSearch (its query) and WebFetch (its URL) tool_use, in order, under the
    /// same denial rule and caps as <see cref="ClaudeShellCommands"/>.</summary>
    public static IReadOnlyList<WebRequest> ClaudeWebRequests(string stdout) =>
        ClaudeToolCalls(stdout, new Dictionary<string, string> { ["WebSearch"] = "query", ["WebFetch"] = "url" })
            .Select(c => new WebRequest(c.Tool, Flatten(c.Target), c.Denied)).Take(MaxCommands).ToList();

    /// <summary>The tool_use blocks whose name is a key of <paramref name="inputField"/>, with that
    /// input field's string value, in order; a call the final result lists under permission_denials
    /// (by tool_use_id) is marked denied, since it never ran.</summary>
    private static List<(string Tool, string Target, bool Denied)> ClaudeToolCalls(string stdout, IReadOnlyDictionary<string, string> inputField)
    {
        var found = new List<(string Id, string Tool, string Target)>();
        var denied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in Lines(stdout))
        {
            var type = Str(root, "type");
            if (type == "assistant"
                && root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (Str(block, "type") != "tool_use" || Str(block, "name") is not { } name || !inputField.TryGetValue(name, out var field)) continue;
                    if (!block.TryGetProperty("input", out var input) || Str(input, field) is not { } target) continue;
                    found.Add((Str(block, "id") ?? "", name, target));
                }
            }
            else if (type == "result"
                && root.TryGetProperty("permission_denials", out var denials)
                && denials.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in denials.EnumerateArray())
                    if (Str(d, "tool_use_id") is { Length: > 0 } id) denied.Add(id);
            }
        }
        return found.Select(f => (f.Tool, f.Target, f.Id.Length > 0 && denied.Contains(f.Id))).ToList();
    }

    /// <summary>Codex: every command_execution item in first-seen order, the completed form replacing
    /// the started one (an item that only ever started is still listed — it was launched).</summary>
    public static IReadOnlyList<ShellCommand> CodexShellCommands(string stdout)
    {
        var order = new List<string>();
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        int anonymous = 0;
        foreach (var root in Lines(stdout))
        {
            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) continue;
            if (Str(item, "type") != "command_execution" || Str(item, "command") is not { } command) continue;
            var id = Str(item, "id") ?? $"anon-{anonymous++}";
            if (!byId.ContainsKey(id)) order.Add(id);
            byId[id] = command;
        }
        return order.Select(id => new ShellCommand(Flatten(byId[id]), false)).Take(MaxCommands).ToList();
    }

    /// <summary>Codex: every web_search item in first-seen order, the completed form replacing the
    /// started one (measured on 0.169.1: the started event has an empty query and an <c>other</c>
    /// action). A search takes <c>action.query</c>, else the item's <c>query</c>; an opened page its
    /// <c>action.url</c>; any other action its type with its first <c>url</c>, <c>pattern</c> or
    /// <c>query</c>. A missing target reads "(no target)", and an item that never completed is a
    /// WebSearch marked "(not completed)". Same caps as the shell lists.</summary>
    public static IReadOnlyList<WebRequest> CodexWebRequests(string stdout)
    {
        var order = new List<string>();
        var byId = new Dictionary<string, (WebRequest Call, bool Completed)>(StringComparer.Ordinal);
        int anonymous = 0;
        foreach (var root in Lines(stdout))
        {
            var type = Str(root, "type");
            if (type is not ("item.started" or "item.completed") || !root.TryGetProperty("item", out var item)
                || item.ValueKind != JsonValueKind.Object || Str(item, "type") != "web_search") continue;
            var id = Str(item, "id") ?? $"anon-{anonymous++}";
            if (byId.TryGetValue(id, out var seen) && seen.Completed) continue;
            if (!byId.ContainsKey(id)) order.Add(id);
            byId[id] = type == "item.completed"
                ? (CompletedWebSearch(item), true)
                : (new WebRequest("WebSearch", (NonEmpty(Str(item, "query")) ?? NoTarget) + " (not completed)", false), false);
        }
        return order.Select(id => byId[id].Call with { Target = Flatten(byId[id].Call.Target) }).Take(MaxCommands).ToList();
    }

    private const string NoTarget = "(no target)";

    private static WebRequest CompletedWebSearch(JsonElement item)
    {
        var action = item.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.Object ? a : (JsonElement?)null;
        var kind = action is { } act ? NonEmpty(Str(act, "type")) : null;
        if (kind is null or "search")
            return new WebRequest("WebSearch", (action is { } s ? NonEmpty(Str(s, "query")) : null) ?? NonEmpty(Str(item, "query")) ?? NoTarget, false);
        var target = NonEmpty(Str(action!.Value, "url")) ?? NonEmpty(Str(action.Value, "pattern")) ?? NonEmpty(Str(action.Value, "query"));
        return new WebRequest(kind, target ?? NoTarget, false);
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>The model's final text from Claude stdout: the <c>result</c> string of a single JSON
    /// object (`--output-format json`) or of the last <c>type: result</c> line of a stream
    /// (`stream-json`). Null when neither is there.</summary>
    public static string? ClaudeFinalText(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            return ResultOf(doc.RootElement);
        }
        catch (JsonException) { }
        string? last = null;
        foreach (var root in Lines(stdout))
            if (Str(root, "type") == "result") last = ResultOf(root) ?? last;
        return last;
    }

    private static readonly HashSet<string> CodexToolItems = new(StringComparer.Ordinal) { "command_execution", "file_change", "mcp_tool_call", "web_search" };

    /// <summary>How many tool calls a spawn made, or null when its output does not list them.
    /// <paramref name="stream"/> is true only for the shapes that list calls: Claude `stream-json`
    /// (every <c>tool_use</c> block, denied ones and <c>post_message</c> included) and Codex `--json`
    /// (each command, file change, MCP call or web search item, once per item id; an item without a
    /// string id counts once per completed event). Plain Claude json, plain Codex text and an empty kept
    /// stream (an output overflow keeps none) give null, never 0. Shapes it does not expect are
    /// skipped.</summary>
    public static int? ToolCalls(string host, bool stream, string stdout)
    {
        if (!stream || string.IsNullOrWhiteSpace(stdout)) return null;
        var count = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in Lines(stdout))
        {
            if (host == "claude")
            {
                if (Str(root, "type") != "assistant" || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                foreach (var block in content.EnumerateArray())
                    if (Str(block, "type") == "tool_use") count++;
            }
            else if (host == "codex")
            {
                var type = Str(root, "type");
                if (type is not ("item.started" or "item.completed") || !root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) continue;
                if (Str(item, "type") is not { } kind || !CodexToolItems.Contains(kind)) continue;
                if (Str(item, "id") is { } id) { if (seen.Add(id)) count++; }
                else if (type == "item.completed") count++;
            }
        }
        return count;
    }

    public const int MaxUsageBytes = 16_384;

    /// <summary>The usage block a spawn reported, as compact JSON, or null when nothing was reported.
    /// Claude: <c>usage</c> and <c>modelUsage</c> from the result object (`json`) or the last result
    /// line (`stream-json`). Codex: <c>usage</c> from the last <c>turn.completed</c> line (`--json`).
    /// <paramref name="host"/> is the hub's record of the CLI it launched, not a reported value, and
    /// <c>modelUsage</c>'s keys are the only model names a reader may attribute. Plain Codex output
    /// (<paramref name="structured"/> false) is the model's own text and is never parsed. Every other
    /// result field (session, uuid, credential expiry, text) is dropped. Null over
    /// <see cref="MaxUsageBytes"/>; never throws.</summary>
    public static string? Usage(string host, bool structured, string stdout)
    {
        if (!structured || string.IsNullOrWhiteSpace(stdout)) return null;
        JsonElement? source = null;
        if (host == "claude")
        {
            try
            {
                using var doc = JsonDocument.Parse(stdout);
                if (doc.RootElement.ValueKind == JsonValueKind.Object) source = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                foreach (var root in Lines(stdout))
                    if (Str(root, "type") == "result") source = root;
            }
        }
        else if (host == "codex")
        {
            foreach (var root in Lines(stdout))
                if (Str(root, "type") == "turn.completed") source = root;
        }
        if (source is not { } s) return null;
        var fields = host == "claude" ? new[] { "usage", "modelUsage" } : new[] { "usage" };
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("host", host);
            var any = false;
            foreach (var name in fields)
            {
                if (!s.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) continue;
                writer.WritePropertyName(name);
                value.WriteTo(writer);
                any = true;
            }
            writer.WriteEndObject();
            writer.Flush();
            if (!any) return null;
        }
        return buffer.Length > MaxUsageBytes ? null : System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Codex: why a turn failed, from the last <c>turn.failed</c> event's <c>error.message</c>,
    /// or else the last top-level <c>{"type":"error","message":…}</c> line. Codex sometimes wraps the
    /// real cause as a JSON string holding another <c>error.message</c> (a re-serialized API error);
    /// when the message itself parses that way, the inner string is returned instead of the envelope.
    /// Null when neither shape appears.</summary>
    public static string? CodexFailure(string stdout)
    {
        string? failed = null;
        string? error = null;
        foreach (var root in Lines(stdout))
        {
            var type = Str(root, "type");
            if (type == "turn.failed" && root.TryGetProperty("error", out var e) && Str(e, "message") is { } m)
                failed = m;
            else if (type == "error" && Str(root, "message") is { } em)
                error = em;
        }
        var message = failed ?? error;
        return message is null ? null : Innermost(message);
    }

    private static string Innermost(string message)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(message); }
        catch (JsonException) { return message; }   // not JSON: the message stays literal
        using (doc)
        {
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var inner)
                && Str(inner, "message") is { } m
                ? m
                : message;
        }
    }

    /// <summary>One line, at most <see cref="MaxCommandChars"/> characters; a line break becomes ` ⏎ `.</summary>
    public static string Flatten(string command)
    {
        var one = command.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " ⏎ ").Trim();
        if (one.Length <= MaxCommandChars) return one;
        var cut = MaxCommandChars;
        if (char.IsHighSurrogate(one[cut - 1])) cut--;
        return one[..cut] + "…";
    }

    private static string? ResultOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()?.Trim() is { Length: > 0 } s ? s : null
            : null;

    private static IEnumerable<JsonElement> Lines(string stdout)
    {
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object) yield return doc.RootElement.Clone();
            }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
