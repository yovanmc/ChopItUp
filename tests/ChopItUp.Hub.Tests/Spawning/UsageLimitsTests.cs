using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>One row per cited limit phrase (sources in <see cref="UsageLimits"/>), read in a fixed
/// zone six hours behind UTC with no daylight saving, at 10:00 local on 2026-11-20.</summary>
public sealed class UsageLimitsTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("hub-local", TimeSpan.FromHours(-6), "hub-local", "hub-local");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T16:00:00Z");

    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0, int year = 2026) =>
        new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(-6)).ToUniversalTime();

    private static string ClaudeResult(string text, bool isError) =>
        $$"""{"type":"result","subtype":"success","is_error":{{(isError ? "true" : "false")}},"result":{{System.Text.Json.JsonSerializer.Serialize(text)}}}""";

    private static string CodexEvent(string type, string message) => type == "turn.failed"
        ? """{"type":"turn.failed","error":{"message":""" + System.Text.Json.JsonSerializer.Serialize(message) + "}}"
        : """{"type":"error","message":""" + System.Text.Json.JsonSerializer.Serialize(message) + "}";

    public static TheoryData<string, string, string, DateTimeOffset?> Hits() => new()
    {
        // Claude Code 2.1.328: `You've hit your ${limitName} · resets ${time}`.
        { "claude", ClaudeResult("You've hit your 5-hour limit · resets 3pm (America/Chicago)", true), "", Local(11, 20, 15) },
        { "claude", ClaudeResult("You've hit your 5-hour limit · resets 3pm (America/Chicago)", false), "", Local(11, 20, 15) },
        { "claude", ClaudeResult("You've hit your Opus limit · resets 3:30pm (America/Chicago) · progress saved", true), "", Local(11, 20, 15, 30) },
        { "claude", ClaudeResult("You've hit your weekly limit · resets Nov 23, 9am (America/Chicago)", true), "", Local(11, 23, 9) },
        { "claude", "", "You've hit your session limit · resets 11pm (America/Chicago)\n", Local(11, 20, 23) },
        { "claude", ClaudeResult("You've hit your monthly spend limit.", true), "", null },
        { "claude", ClaudeResult("You're out of usage credits. Run /usage-credits to keep using Claude Code", true), "", null },
        // Codex, codex-rs/protocol/src/error.rs UsageLimitReachedError (U+2019 apostrophe).
        { "codex", CodexEvent("turn.failed", "You’ve hit your usage limit. Visit https://chatgpt.com/settings/usage to purchase more credits Try again at 3:15 PM."), "", Local(11, 20, 15, 15) },
        { "codex", CodexEvent("error", "You’ve hit your usage limit. Upgrade to Plus to continue using Codex (https://chatgpt.com/explore/plus), or try again at Nov 21st, 2026 3:15 PM."), "", Local(11, 21, 15, 15) },
        { "codex", CodexEvent("error", "You’ve hit your usage limit for gpt-6-astra. Switch to another model now, or try again at 1:05 PM."), "", Local(11, 20, 13, 5) },
        { "codex", CodexEvent("turn.failed", "You’ve hit your usage limit. Try again later."), "", null },
        { "codex", "", "ERROR: You've hit your usage limit. Try again at 4:00 PM.\n", Local(11, 20, 16) },
        { "codex", CodexEvent("error", "Your workspace is out of credits. Add credits to continue."), "", null },
        { "codex", CodexEvent("error", "You hit your spend cap set in your workspace. Increase your spend cap to continue."), "", null },
        { "codex", CodexEvent("error", "Quota exceeded. Check your plan and billing details."), "", null },
    };

    [Theory]
    [MemberData(nameof(Hits))]
    public void A_cited_limit_phrase_is_a_hit_with_its_reset_in_the_hub_zone(string host, string stdout, string stderr, DateTimeOffset? reset)
    {
        var hit = UsageLimits.Detect(host, stdout, stderr, Zone, Now);

        Assert.NotNull(hit);
        Assert.Equal(reset, hit.ResetAt);
    }

    public static TheoryData<string, string, string> NotHits() => new()
    {
        // A model reply that quotes the phrase: it does not start with it, or runs past the limit length.
        { "claude", ClaudeResult("The CLI prints \"You've hit your 5-hour limit · resets 3pm\" when the plan runs out.", false), "" },
        { "claude", ClaudeResult("You've hit your 5-hour limit · resets 3pm. " + new string('x', UsageLimits.MaxTextChars), false), "" },
        // Codex in human mode writes its transcript to stderr: only its ERROR lines are its own.
        { "codex", "", "codex\nYou’ve hit your usage limit. Try again at 3:15 PM.\n" },
        { "codex", CodexEvent("turn.failed", "stream disconnected before completion"), "exited 1" },
        { "claude", ClaudeResult("Error: ENOENT", true), "spawn failed" },
        // A phrase from the other CLI is not this one's.
        { "gemini", ClaudeResult("You've hit your 5-hour limit · resets 3pm", true), "" },
    };

    [Theory]
    [MemberData(nameof(NotHits))]
    public void Model_text_and_other_failures_are_not_hits(string host, string stdout, string stderr) =>
        Assert.Null(UsageLimits.Detect(host, stdout, stderr, Zone, Now));

    [Theory]
    [InlineData("9:30am", 10, 15)]          // 30 min past: not cleared yet, so 15 min from now
    [InlineData("8am", -1, -1)]             // 2 h past: tomorrow at 8
    [InlineData("Dec 31, 3pm", -2, -2)]     // more than 8 days out: no usable reset
    [InlineData("13pm", -3, -3)]            // not a clock time
    public void A_reset_in_the_past_or_far_away_reads_as_designed(string text, int hour, int minute)
    {
        var reset = UsageLimits.ParseReset(text, Zone, Now);

        var expected = hour switch
        {
            -1 => Local(11, 21, 8),
            -2 or -3 => (DateTimeOffset?)null,
            _ => Now + UsageLimits.NotClearedRetry,
        };
        Assert.Equal(expected, reset);
    }
}
