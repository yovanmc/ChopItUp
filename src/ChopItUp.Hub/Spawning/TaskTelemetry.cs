using System.Text.Json;

namespace ChopItUp.Hub.Spawning;

/// <summary>Why a spawn ended, as the string stored on its task row and returned by the tasks
/// route. Exactly one applies, in the precedence <see cref="Of"/> checks.</summary>
public static class TaskTermination
{
    public const string WorktreeRefused = "worktree_refused";
    public const string OutputLimit = "output_limit";
    public const string LaunchFailed = "launch_failed";
    public const string TimedOut = "timed_out";
    public const string Cancelled = "cancelled";
    public const string UsageLimit = "usage_limit";
    public const string Failed = "failed";
    public const string Completed = "completed";
    /// <summary>Read, never stored: a row with no end whose spawn is no longer in flight.</summary>
    public const string Interrupted = "interrupted";

    /// <summary><paramref name="worktreeRefused"/>: no CLI started because the exchange's worktree
    /// could not be made. <paramref name="limitHit"/>: the spawn posted nothing and its output names a
    /// usage limit. An overflow comes back from the runner with no exit code, so it is checked before
    /// the launch failure that shares that shape.</summary>
    public static string Of(ProcessResult result, bool worktreeRefused, bool limitHit) =>
        worktreeRefused ? WorktreeRefused
        : result.OutputLimitExceeded ? OutputLimit
        : result.ExitCode is null && !result.TimedOut && !result.Cancelled ? LaunchFailed
        : result.TimedOut ? TimedOut
        : result.Cancelled ? Cancelled
        : limitHit ? UsageLimit
        : result.ExitCode != 0 ? Failed
        : Completed;
}

/// <summary>Token numbers read from a stored usage block (<see cref="SpawnOutput.Usage"/>), with the
/// same meaning for both hosts: <see cref="InputTokens"/> is uncached input, the cache counts are
/// separate. Claude reports input without its cache; Codex reports input that includes its cached
/// count, so the cached count is taken out. Every number is null when the block did not report it;
/// <see cref="ReportedModels"/> are the block's <c>modelUsage</c> keys, the only model names a
/// reader may attribute.</summary>
public sealed record TokenMetrics(long? InputTokens, long? OutputTokens, long? CacheReadTokens, long? CacheWriteTokens, IReadOnlyList<string> ReportedModels)
{
    public static readonly TokenMetrics None = new(null, null, null, null, []);

    public static TokenMetrics From(string? usage)
    {
        if (string.IsNullOrWhiteSpace(usage)) return None;
        try
        {
            using var doc = JsonDocument.Parse(usage);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return None;
            if (root.TryGetProperty("modelUsage", out var models) && models.ValueKind == JsonValueKind.Object)
            {
                long? input = null, output = null, cacheRead = null, cacheWrite = null;
                var names = new List<string>();
                foreach (var model in models.EnumerateObject())
                {
                    names.Add(model.Name);
                    input = Add(input, Number(model.Value, "inputTokens"));
                    output = Add(output, Number(model.Value, "outputTokens"));
                    cacheRead = Add(cacheRead, Number(model.Value, "cacheReadInputTokens"));
                    cacheWrite = Add(cacheWrite, Number(model.Value, "cacheCreationInputTokens"));
                }
                return new(input, output, cacheRead, cacheWrite, names);
            }
            if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return None;
            if (root.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.String && host.GetString() == "codex")
            {
                var cached = Number(u, "cached_input_tokens");
                var all = Number(u, "input_tokens");
                return new(all is { } a ? a - (cached ?? 0) : null, Number(u, "output_tokens"), cached, Number(u, "cache_write_input_tokens"), []);
            }
            return new(Number(u, "input_tokens"), Number(u, "output_tokens"), Number(u, "cache_read_input_tokens"), Number(u, "cache_creation_input_tokens"), []);
        }
        catch (JsonException) { return None; }
    }

    private static long? Add(long? sum, long? value) => value is { } v ? (sum ?? 0) + v : sum;

    private static long? Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
