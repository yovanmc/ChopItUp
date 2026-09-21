using System.Text.Json;
using ChopItUp.Core.Model;

namespace ChopItUp.Hub.Spawning;

public static class PanelExecution
{
    public const int OutputLimit = 1024 * 1024;
    public const int AnswerLimit = 20_000;

    public static ProcessSpec Command(ResolvedCli cli, Participant participant, string directory, string prompt, string label)
    {
        IReadOnlyList<string> args;
        if (participant.Host == "claude")
        {
            var config = Path.Combine(Path.GetDirectoryName(directory)!, Path.GetFileName(directory) + "-empty-mcp.json");
            // Keep generated configuration outside the tracked snapshot namespace.
            using (var output = new FileStream(config, FileMode.CreateNew, FileAccess.Write))
                output.Write("{\"mcpServers\":{}}"u8);
            args = [..cli.LeadingArguments, "-p", "--tools", "Read,Glob,Grep", "--allowedTools", "Read,Glob,Grep", "--permission-mode", "dontAsk",
                "--strict-mcp-config", "--mcp-config", config, "--no-session-persistence", "--model", participant.Model!, "--output-format", "json",
                "--disable-slash-commands", "--setting-sources", ""];
        }
        else if (participant.Host == "codex")
            args = [..cli.LeadingArguments, "exec", "--ephemeral", "--ignore-user-config", "--sandbox", "read-only",
                "-c", "windows.sandbox=\"unelevated\"", "-c", "approval_policy=\"never\"", "--json", "-C", directory,
                "--skip-git-repo-check", "-m", participant.Model!, "--color", "never", "-"];
        else throw new ArgumentException("Unsupported panel participant.");
        return new ProcessSpec(cli.FileName, args, new Dictionary<string,string>(), directory, prompt, label,
            RemoveEnvironmentPrefixes: ["CHOPITUP", "OPENAI_API_KEY", "ANTHROPIC_API_KEY"], OutputLimitBytes: OutputLimit);
    }

    public static string? Final(string host, ProcessResult result)
    {
        if (result.ExitCode != 0 || result.TimedOut || result.Cancelled || result.OutputLimitExceeded) return null;
        if (host == "claude") return Bound(SpawnOutput.ClaudeFinalText(result.StandardOutput));
        string? final = null;
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "turn.failed") return null;
                if (type == "item.completed" && root.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var kind) && kind.GetString() == "agent_message"
                    && item.TryGetProperty("text", out var text)) final = text.GetString();
            }
            catch (JsonException) { }
        }
        return Bound(final);
    }

    public static string? Bound(string? text) => string.IsNullOrWhiteSpace(text) ? null
        : text.Length <= AnswerLimit ? text.Trim() : text[..AnswerLimit] + "\n[Answer truncated at 20,000 characters.]";
}
