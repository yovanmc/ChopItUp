using System.Text.Json;

namespace ChopItUp.Hub.Spawning;

public sealed record ShellCommand(string Command, bool Denied);

/// <summary>Reads the two CLIs' structured stdout for the one thing the trail needs (M9 decision 6):
/// which shell commands the model ran. Shapes measured 2026-09-06 (plan claims 23, 24): Claude
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
    public static IReadOnlyList<ShellCommand> ClaudeShellCommands(string stdout)
    {
        var found = new List<(string Id, string Command)>();
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
                    if (Str(block, "type") != "tool_use" || Str(block, "name") != "Bash") continue;
                    if (!block.TryGetProperty("input", out var input) || Str(input, "command") is not { } command) continue;
                    found.Add((Str(block, "id") ?? "", command));
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
        return found
            .Select(f => new ShellCommand(Flatten(f.Command), f.Id.Length > 0 && denied.Contains(f.Id)))
            .Take(MaxCommands)
            .ToList();
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

    /// <summary>The model's final text from Claude stdout: the <c>result</c> string of a single JSON
    /// object (`--output-format json`, M5) or of the last <c>type: result</c> line of a stream
    /// (`stream-json`, M9). Null when neither is there.</summary>
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
