using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Skills;

namespace ChopItUp.Core.Tests.Messaging;

public sealed class MentionsTests
{
    private static readonly string[] Ids = ["owner", "claude", "codex", "opus", "sonnet", "gpt-6-astra", "gpt-5.6-sol", "gpt-5.5", "hub"];

    /// <summary>Row 44: what a case's fixture `"turns"` field expects of `LeadingMentions.Turns`/
    /// `TurnsValue`; absent on a case that asserts nothing about turns.</summary>
    private sealed record TurnsCase(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("value")] int Value);

    private sealed record MentionCase(
        string Name,
        string Body,
        [property: JsonPropertyName("recipients")] string[] Recipients,
        [property: JsonPropertyName("unknown")] string[] Unknown,
        [property: JsonPropertyName("references")] string[] References,
        [property: JsonPropertyName("turns")] TurnsCase? Turns = null);

    private sealed record MentionCasesFile(string[] Roster, MentionCase[] Cases);

    /// <summary>The fixture lives in <c>tests/mention-cases.json</c> at the repo root, shared with the
    /// TypeScript twin (Task 4). Found the way <c>SpawnPromptTests.GoldenPath</c> finds the repo root:
    /// walk up from the test assembly to <c>ChopItUp.slnx</c>.</summary>
    private static string FixturePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChopItUp.slnx")))
                return Path.Combine(dir.FullName, "tests", "mention-cases.json");
        }
        throw new InvalidOperationException("Could not locate the repo root (ChopItUp.slnx) above " + AppContext.BaseDirectory);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEnumerable<object[]> Cases()
    {
        var file = JsonSerializer.Deserialize<MentionCasesFile>(File.ReadAllText(FixturePath()), JsonOptions)!;
        foreach (var c in file.Cases)
            yield return [c.Name, file.Roster, c.Body, c.Recipients, c.Unknown, c.References, c.Turns?.Token!, c.Turns?.Value ?? 0];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Leading_and_the_reference_remainder_match_the_shared_fixture(
        string name, string[] roster, string body, string[] recipients, string[] unknown, string[] references, string? turnsToken, int turnsValue)
    {
        Assert.False(string.IsNullOrWhiteSpace(name));
        var m = new Mentions(roster);
        // Standards S1 (hub F10): the timing bound guards the catastrophic-backtracking canary only
        // (B1) - applying it to every case made the whole theory flaky under load for no reason the
        // other 47 cases need.
        var watch = Stopwatch.StartNew();
        var leading = m.Leading(body);
        watch.Stop();
        if (name.StartsWith("perf canary", StringComparison.Ordinal))
            Assert.True(watch.ElapsedMilliseconds < 200, $"'{name}' took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(recipients, leading.Recipients);
        Assert.Equal(unknown, leading.Unknown);
        Assert.Equal(references, m.Find(body).Except(leading.Recipients));
        if (turnsToken is not null)
        {
            var expectedToken = turnsToken switch
            {
                "valid" => TurnsToken.Valid,
                "out-of-range" => TurnsToken.OutOfRange,
                "none" => TurnsToken.None,
                _ => throw new InvalidOperationException($"Unknown turns token '{turnsToken}' in case '{name}'."),
            };
            Assert.Equal(expectedToken, leading.Turns);
            Assert.Equal(turnsValue, leading.TurnsValue);
        }
    }

    [Fact]
    public void Finds_ids_in_first_appearance_order_without_duplicates()
    {
        var m = new Mentions(Ids);
        Assert.Equal(["opus", "gpt-5.6-sol", "owner"], m.Find("@opus then @gpt-5.6-sol, and @opus again; back to @owner."));
    }

    [Fact]
    public void Is_case_insensitive_and_reports_the_canonical_id()
    {
        var m = new Mentions(Ids);
        Assert.Equal(["opus", "gpt-6-astra"], m.Find("@Opus @GPT-6-ASTRA"));
    }

    [Fact]
    public void Sentence_final_punctuation_still_matches_but_a_longer_word_does_not()
    {
        var m = new Mentions(Ids);
        Assert.Equal(["claude"], m.Find("ask @claude."));
        Assert.Empty(m.Find("ask @claude-2 or @claude.x or @claudette"));
    }

    [Fact]
    public void Dotted_ids_win_over_their_prefixes()
    {
        var m = new Mentions(Ids);
        Assert.Equal(["gpt-5.6-sol"], m.Find("@gpt-5.6-sol please"));
        Assert.Equal(["gpt-5.5"], m.Find("@gpt-5.5 please"));
    }

    [Fact]
    public void An_email_address_is_not_a_mention()
    {
        var m = new Mentions(Ids);
        Assert.Empty(m.Find("mail me at me@opus.com"));
        Assert.Empty(m.Find("x-@sonnet"));
    }

    [Fact]
    public void Unknown_ids_and_empty_bodies_yield_nothing()
    {
        var m = new Mentions(Ids);
        Assert.Empty(m.Find("@nobody @ owner"));
        Assert.Empty(m.Find(""));
        Assert.Empty(new Mentions([]).Find("@opus"));
    }
}
