using ChopItUp.Core.Storage;
using ChopItUp.Hub.Hosting;
using ChopItUp.Hub.Security;

namespace ChopItUp.Hub.Tests;

/// <summary>Row 28 Task 2: <see cref="TokenScan.Candidates"/> is the one routine Task 3's start-time
/// sweep and Task 6's escalation proof both call. Exercised here against the REAL shapes
/// <see cref="HostConfigs.Write"/> produces (not hand-typed approximations of them), plus formats it
/// must tolerate without parsing: malformed JSON, Markdown prose, and a file with nothing to find.</summary>
public sealed class TokenScanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_tokscan_" + Guid.NewGuid().ToString("N"));

    // A synthetic value with the exact shape TokenStore.NewToken mints: 43 characters drawn from the
    // base64url alphabet (A-Z a-z 0-9 - _), which is what 32 random bytes become once padding is
    // trimmed. Not a real credential; nothing this token would resolve against is ever created.
    private const string FakeToken = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopq";
    private const string OtherFakeToken = "9876543210ZYXWVUTSRQPONMLKJIHGFEDCBAzyxwvut";

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Fake_token_fixture_has_the_exact_minted_length()
    {
        Assert.Equal(TokenScan.TokenLength, FakeToken.Length);
        Assert.Equal(43, FakeToken.Length);
    }

    /// <summary>The real claude-desktop.json shape: a JSON string value, nested under
    /// <c>mcpServers.chopitup.env.CHOPITUP_TOKEN</c>, holding <c>"Bearer &lt;token&gt;"</c>. The
    /// candidate must be the token alone, never the whole header string.</summary>
    [Fact]
    public void Finds_the_token_inside_the_real_claude_desktop_host_config()
    {
        Directory.CreateDirectory(_dir);
        var tokens = new Dictionary<string, string> { ["claude"] = FakeToken };
        var roster = ChopDb.SeedRoster.Where(p => p.Id == "claude").ToList();
        var folder = HostConfigs.Write(_dir, 5177, tokens, roster);
        var text = File.ReadAllText(Path.Combine(folder, "claude-desktop.json"));

        var candidates = TokenScan.Candidates(text).ToList();

        Assert.Contains(FakeToken, candidates);
        Assert.DoesNotContain("Bearer " + FakeToken, candidates);
        Assert.DoesNotContain(candidates, c => c.Contains("Bearer"));
    }

    /// <summary>The real codex-config.toml shape: <c>http_headers = { Authorization = "Bearer
    /// &lt;token&gt;" }</c>, an inline TOML table, not JSON at all.</summary>
    [Fact]
    public void Finds_the_token_inside_the_real_codex_toml_host_config()
    {
        Directory.CreateDirectory(_dir);
        var tokens = new Dictionary<string, string> { ["codex"] = FakeToken };
        var roster = ChopDb.SeedRoster.Where(p => p.Id == "codex").ToList();
        var folder = HostConfigs.Write(_dir, 5177, tokens, roster);
        var text = File.ReadAllText(Path.Combine(folder, "codex-config.toml"));

        var candidates = TokenScan.Candidates(text).ToList();

        Assert.Contains(FakeToken, candidates);
        Assert.DoesNotContain(candidates, c => c.Contains("Bearer"));
    }

    /// <summary>The real claude-code-owner-remote.json shape (SpawnCommands.ClaudeMcpConfigJson): the
    /// token sits nested three levels deep at <c>mcpServers.chopitup.headers.Authorization</c>, not
    /// under an env var like the Desktop file. Proves nesting depth is not special-cased.</summary>
    [Fact]
    public void Finds_the_token_nested_three_levels_deep_in_the_owner_remote_config()
    {
        Directory.CreateDirectory(_dir);
        var tokens = new Dictionary<string, string> { [ChopDb.OwnerRemoteParticipantId] = FakeToken };
        var roster = ChopDb.SeedRoster.Where(p => p.Id == ChopDb.OwnerRemoteParticipantId).ToList();
        var folder = HostConfigs.Write(_dir, 5177, tokens, roster);
        var text = File.ReadAllText(Path.Combine(folder, "claude-code-owner-remote.json"));

        var candidates = TokenScan.Candidates(text).ToList();

        Assert.Contains(FakeToken, candidates);
    }

    /// <summary>Must not require the text to parse: truncated/hand-edited JSON still yields its
    /// candidate.</summary>
    [Fact]
    public void Finds_the_token_in_malformed_json_that_does_not_parse()
    {
        var malformed = $$"""
            {
              "mcpServers": {
                "chopitup": {
                  "headers": { "Authorization": "Bearer {{FakeToken}}"
            """; // truncated: missing closing braces/quote

        var candidates = TokenScan.Candidates(malformed).ToList();

        Assert.Contains(FakeToken, candidates);
    }

    [Fact]
    public void Finds_multiple_distinct_candidates_in_one_document()
    {
        var text = $$"""{"a": "Bearer {{FakeToken}}", "b": "Bearer {{OtherFakeToken}}"}""";

        var candidates = TokenScan.Candidates(text).ToList();

        Assert.Contains(FakeToken, candidates);
        Assert.Contains(OtherFakeToken, candidates);
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void Finds_nothing_in_a_markdown_file_with_no_token_shaped_text()
    {
        var readme = """
            # Host configs for Chop It Up

            Generated by `ChopItUp.Hub --print-config`. Merge the `mcpServers` entry into
            `%APPDATA%\Claude\claude_desktop_config.json`, then restart Claude Desktop.

            | File | Where it goes |
            |------|---------------|
            | `claude-desktop.json` | Claude Desktop config |
            """;

        var candidates = TokenScan.Candidates(readme).ToList();

        Assert.Empty(candidates);
    }

    /// <summary>A run one character short or one character long is not the minted shape and must
    /// not be reported, even though it shares the alphabet.</summary>
    [Fact]
    public void Ignores_runs_that_are_not_exactly_the_minted_length()
    {
        var tooShort = FakeToken[..42];
        var tooLong = FakeToken + "X";
        var text = $"\"{tooShort}\" \"{tooLong}\"";

        var candidates = TokenScan.Candidates(text).ToList();

        Assert.Empty(candidates);
    }
}
