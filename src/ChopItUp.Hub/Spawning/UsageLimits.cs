using System.Text.RegularExpressions;

namespace ChopItUp.Hub.Spawning;

/// <summary>A usage limit a CLI reported: its own text, and the instant the CLI said it resets
/// (null when it named none or the time could not be read).</summary>
public sealed record UsageLimitHit(string Text, DateTimeOffset? ResetAt);

/// <summary>Reads a usage limit out of a spawn that ended without posting. Only the CLI's own
/// failure channel is read, never model text: Claude's final result text when it starts with the
/// CLI's phrase and is short (whatever its <c>is_error</c> says, which no capture has confirmed yet),
/// and stderr lines that start with it; Codex's <c>error</c> and <c>turn.failed</c> events, and stderr
/// lines that start with <c>ERROR: </c>. Reset times are read in the hub's own time zone, because
/// the CLI is a child on the same machine; the zone name in parentheses is ignored, so no time zone
/// lookup runs (the hub ships with invariant globalization).
///
/// Sources of the phrases:
/// <list type="bullet">
/// <item>Claude Code 2.1.328, bundled JS: <c>You've hit your ${limitName}${suffix}</c> with names such
/// as <c>5-hour limit</c>, <c>weekly limit</c>, <c>Opus limit</c>, <c>monthly spend limit</c>; suffix
/// <c> · resets ${time}</c>, time <c>3pm (America/Chicago)</c>, <c>3:30pm (…)</c> or past 24 h
/// <c>Nov 21, 3pm (…)</c> with the year when it differs; and <c>You're out of usage credits</c>.</item>
/// <item>openai/codex <c>main</c> (read 2026-11-20; installed codex-cli 0.169.1),
/// <c>codex-rs/protocol/src/error.rs</c>, <c>UsageLimitReachedError</c>: <c>You’ve hit your usage
/// limit</c> (optionally <c> for {name}</c>) ending <c>Try again at 3:15 PM.</c>, <c>try again at Nov
/// 21st, 2026 3:15 PM.</c> or <c>Try again later.</c>; and the no-reset forms <c>Your workspace is out
/// of credits</c>, <c>You hit your spend cap</c>, <c>Quota exceeded. Check your plan</c>.</item>
/// </list></summary>
public static class UsageLimits
{
    /// <summary>Longer than any limit line either CLI prints: a reply that long is a model's text.</summary>
    public const int MaxTextChars = 400;

    /// <summary>A time-only reset this far in the past means the limit has not cleared yet.</summary>
    public static readonly TimeSpan NotClearedWindow = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan NotClearedRetry = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan FarthestReset = TimeSpan.FromDays(8);

    private const string Apostrophe = "['’]";
    private static readonly Regex ClaudeHit = new($@"^You{Apostrophe}ve hit your (?<limit>.+?)(?:\s+\S\s+resets\s+(?<reset>[^·]+?))?(?:\s+\S\s+.*)?\.?$", RegexOptions.CultureInvariant);
    private static readonly Regex ClaudeNoReset = new($@"^You{Apostrophe}re out of usage credits", RegexOptions.CultureInvariant);
    private static readonly Regex CodexHit = new($@"^You{Apostrophe}ve hit your usage limit\b.*?(?:try again at (?<reset>[^.]+)\.|try again later\.)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex CodexNoReset = new(@"^(?:Your workspace is out of credits|You hit your spend cap|Quota exceeded\. Check your plan)", RegexOptions.CultureInvariant);
    private static readonly Regex Clock = new(@"^(?:(?<mon>[A-Z][a-z]{2})\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s+(?:(?<year>\d{4}),?\s+)?)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>[AaPp][Mm])(?:\s*\(.*\))?$", RegexOptions.CultureInvariant);
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    public static UsageLimitHit? Detect(string host, string stdout, string stderr, TimeZoneInfo zone, DateTimeOffset now)
    {
        foreach (var text in Candidates(host, stdout, stderr))
        {
            if (host == "claude" && ClaudeHit.Match(text) is { Success: true } c)
                return new(text, c.Groups["reset"].Success ? ParseReset(c.Groups["reset"].Value, zone, now) : null);
            if (host == "claude" && ClaudeNoReset.IsMatch(text)) return new(text, null);
            if (host == "codex" && CodexHit.Match(text) is { Success: true } x)
                return new(text, x.Groups["reset"].Success ? ParseReset(x.Groups["reset"].Value.Trim(), zone, now) : null);
            if (host == "codex" && CodexNoReset.IsMatch(text)) return new(text, null);
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string host, string stdout, string stderr)
    {
        if (host == "claude")
        {
            if (SpawnOutput.ClaudeFinalText(stdout)?.Trim() is { Length: > 0 and <= MaxTextChars } final) yield return final;
            foreach (var line in Lines(stderr))
                if (line.Length <= MaxTextChars) yield return line;
        }
        else if (host == "codex")
        {
            if (SpawnOutput.CodexFailure(stdout)?.Trim() is { Length: > 0 and <= MaxTextChars } failure) yield return failure;
            foreach (var line in Lines(stderr))
                if (line.StartsWith("ERROR: ", StringComparison.Ordinal) && line.Length <= MaxTextChars + 7) yield return line[7..].Trim();
        }
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    /// <summary>The instant a displayed reset names, in <paramref name="zone"/>; null when it cannot be
    /// read, falls in a clock gap, or lies more than <see cref="FarthestReset"/> ahead. A time-only
    /// value earlier than now is tomorrow, except within <see cref="NotClearedWindow"/>, where the
    /// limit has not cleared yet and the answer is <see cref="NotClearedRetry"/> from now.</summary>
    public static DateTimeOffset? ParseReset(string text, TimeZoneInfo zone, DateTimeOffset now)
    {
        var m = Clock.Match(text.Trim());
        if (!m.Success) return null;
        var hour = int.Parse(m.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minute = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        if (hour is < 1 or > 12 || minute > 59) return null;
        hour = hour % 12 + (m.Groups["ap"].Value.ToUpperInvariant() == "PM" ? 12 : 0);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        DateTimeOffset? reset;
        if (m.Groups["mon"].Success)
        {
            var month = Array.IndexOf(Months, m.Groups["mon"].Value) + 1;
            var day = int.Parse(m.Groups["day"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var year = m.Groups["year"].Success ? int.Parse(m.Groups["year"].Value, System.Globalization.CultureInfo.InvariantCulture) : localNow.Year;
            if (month == 0 || day > DateTime.DaysInMonth(year, month)) return null;
            reset = ToInstant(new DateTime(year, month, day, hour, minute, 0), zone);
            if (reset is { } r && !m.Groups["year"].Success && r < now - TimeSpan.FromDays(180) && day <= DateTime.DaysInMonth(year + 1, month))
                reset = ToInstant(new DateTime(year + 1, month, day, hour, minute, 0), zone);
        }
        else
        {
            reset = ToInstant(localNow.Date.AddHours(hour).AddMinutes(minute), zone);
            if (reset is { } r && r < now)
                reset = now - r <= NotClearedWindow ? now + NotClearedRetry : ToInstant(localNow.Date.AddDays(1).AddHours(hour).AddMinutes(minute), zone);
        }
        return reset is { } at && at >= now && at - now <= FarthestReset ? at : null;
    }

    private static DateTimeOffset? ToInstant(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified)) return null;
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), TimeSpan.Zero);
    }
}
