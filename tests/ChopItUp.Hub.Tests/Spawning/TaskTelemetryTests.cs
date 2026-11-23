using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>How a finished spawn is classified and how its reported usage block becomes token
/// numbers. Results are built in the exact shapes ProcessRunner returns.</summary>
public sealed class TaskTelemetryTests
{
    private static ProcessResult Result(int? exit, bool timedOut = false, bool cancelled = false, bool overflow = false) =>
        new(exit, timedOut, cancelled, "", "", TimeSpan.FromSeconds(1), OutputLimitExceeded: overflow);

    public static TheoryData<string, ProcessResult, bool, bool, string> TerminationCases => new()
    {
        { "worktree refused beats everything", Result(null), true, false, TaskTermination.WorktreeRefused },
        { "runner overflow (no exit, not timed out, not cancelled)", Result(null, overflow: true), false, false, TaskTermination.OutputLimit },
        { "runner throw", Result(null), false, false, TaskTermination.LaunchFailed },
        { "timed out", Result(null, timedOut: true), false, true, TaskTermination.TimedOut },
        { "cancelled", Result(null, cancelled: true), false, true, TaskTermination.Cancelled },
        { "usage limit on a nonzero exit", Result(1), false, true, TaskTermination.UsageLimit },
        { "nonzero exit", Result(2), false, false, TaskTermination.Failed },
        { "clean exit", Result(0), false, false, TaskTermination.Completed },
        { "a limit line on a clean exit is still a limit", Result(0), false, true, TaskTermination.UsageLimit },
    };

    [Theory]
    [MemberData(nameof(TerminationCases))]
    public void Termination_follows_the_stated_precedence(string _, ProcessResult result, bool worktreeRefused, bool limitHit, string expected) =>
        Assert.Equal(expected, TaskTermination.Of(result, worktreeRefused, limitHit));

    public static TheoryData<string, string?, long?, long?, long?, long?, string[]> MetricCases => new()
    {
        { "no block", null, null, null, null, null, [] },
        { "claude, one model",
          """{"host":"claude","usage":{"input_tokens":4,"output_tokens":82,"cache_read_input_tokens":8738,"cache_creation_input_tokens":3938},"modelUsage":{"claude-haiku-5-5":{"inputTokens":4,"outputTokens":82,"cacheReadInputTokens":8738,"cacheCreationInputTokens":3938}}}""",
          4, 82, 8738, 3938, ["claude-haiku-5-5"] },
        { "claude, two models summed",
          """{"host":"claude","usage":{"input_tokens":1,"output_tokens":1},"modelUsage":{"claude-opus-5-5":{"inputTokens":10,"outputTokens":20,"cacheReadInputTokens":30,"cacheCreationInputTokens":40},"claude-haiku-5-5":{"inputTokens":1,"outputTokens":2,"cacheReadInputTokens":3,"cacheCreationInputTokens":4}}}""",
          11, 22, 33, 44, ["claude-opus-5-5", "claude-haiku-5-5"] },
        { "claude usage only", """{"host":"claude","usage":{"input_tokens":5,"output_tokens":6}}""", 5, 6, null, null, [] },
        { "codex input includes its cached count",
          """{"host":"codex","usage":{"input_tokens":15789,"cached_input_tokens":11392,"cache_write_input_tokens":0,"output_tokens":5,"reasoning_output_tokens":0}}""",
          4397, 5, 11392, 0, [] },
        { "malformed block", "not json", null, null, null, null, [] },
    };

    [Theory]
    [MemberData(nameof(MetricCases))]
    public void Token_metrics_are_normalized_per_host_and_null_when_not_reported(string _, string? usage, long? input, long? output, long? cacheRead, long? cacheWrite, string[] models)
    {
        var m = TokenMetrics.From(usage);
        Assert.Equal((input, output, cacheRead, cacheWrite), (m.InputTokens, m.OutputTokens, m.CacheReadTokens, m.CacheWriteTokens));
        Assert.Equal(models, m.ReportedModels);
    }
}
