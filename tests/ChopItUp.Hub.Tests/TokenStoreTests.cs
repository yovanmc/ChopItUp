using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Security;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests;

public sealed class TokenStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_tok_" + Guid.NewGuid().ToString("N"));

    private static string Sha256Hex(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();

    /// <summary>The required schema-evolution guard (row 28 Task 1, HIGH): a pre-row-28 plaintext
    /// tokens.json, INCLUDING a mixed file (one entry already migrated to the hashed shape, the rest
    /// still raw strings), migrates in place. Every original plaintext still resolves to its
    /// participant (AC3: no re-mint, no re-paste), no plaintext survives on disk (AC6), the
    /// already-hashed entry is carried over unchanged, and a second start touches the file not at
    /// all.</summary>
    [Fact]
    public void Schema_evolution_a_plaintext_and_mixed_tokens_json_migrates_with_no_plaintext_left()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, TokenStore.FileName);
        const string ownerPlain = "old-owner-plaintext-tok";
        const string claudePlain = "old-claude-plaintext-tok";
        const string codexPlain = "old-codex-plaintext-tok";
        var codexHash = Sha256Hex(codexPlain);   // codex already migrated by an earlier start
        File.WriteAllText(path, $$"""
            {
              "owner": "{{ownerPlain}}",
              "claude": "{{claudePlain}}",
              "codex": { "sha256": "{{codexHash}}" }
            }
            """);

        var store = TokenStore.Load(_dir, ChopDb.SeedRoster);

        Assert.True(store.TryResolve(ownerPlain, out var ownerId)); Assert.Equal("owner", ownerId);
        Assert.True(store.TryResolve(claudePlain, out var claudeId)); Assert.Equal("claude", claudeId);
        Assert.True(store.TryResolve(codexPlain, out var codexId)); Assert.Equal("codex", codexId);

        var onDisk = File.ReadAllText(path);
        Assert.DoesNotContain(ownerPlain, onDisk);
        Assert.DoesNotContain(claudePlain, onDisk);
        Assert.DoesNotContain(codexPlain, onDisk);

        var hostFile = ChopDb.SeedRoster.Where(p => p.Kind != "system" && !ExchangePolicy.IsSpawnable(p)).ToList();
        using (var doc = JsonDocument.Parse(onDisk))
        {
            foreach (var p in hostFile)
            {
                Assert.True(doc.RootElement.TryGetProperty(p.Id, out var entry), $"missing entry for '{p.Id}'");
                Assert.Equal(JsonValueKind.Object, entry.ValueKind);
                Assert.Equal(64, entry.GetProperty("sha256").GetString()!.Length);
            }
            Assert.Equal(codexHash, doc.RootElement.GetProperty("codex").GetProperty("sha256").GetString());
            foreach (var p in ChopDb.SeedRoster.Where(p => ExchangePolicy.IsSpawnable(p) || p.Kind == "system"))
                Assert.False(doc.RootElement.TryGetProperty(p.Id, out _), $"'{p.Id}' must not appear in {TokenStore.FileName}");
        }

        var before = File.ReadAllBytes(path);
        TokenStore.Load(_dir, ChopDb.SeedRoster);   // a second start: nothing left to migrate or mint
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void MintFor_replaces_one_host_file_token_and_refuses_a_spawnable_or_system_id()
    {
        var store = TokenStore.Load(_dir, ChopDb.SeedRoster);
        var before = TokenStore.ReadExisting(_dir, ChopDb.SeedRoster);

        var minted = store.MintFor("claude");
        var after = TokenStore.ReadExisting(_dir, ChopDb.SeedRoster);
        Assert.Equal(Sha256Hex(minted), after["claude"]);
        Assert.NotEqual(before["claude"], after["claude"]);
        Assert.True(store.TryResolve(minted, out var resolved)); Assert.Equal("claude", resolved);
        foreach (var id in before.Keys.Where(id => id != "claude"))
            Assert.Equal(before[id], after[id]);   // every other host-file entry is untouched

        // Row 28's classification, enforced: a spawnable row is ephemeral (nothing in the file to
        // mint over) and a system row never authenticates at all - neither is a rotation target.
        var spawnEx = Assert.Throws<ArgumentException>(() => store.MintFor("opus"));
        Assert.Contains("opus", spawnEx.Message);
        var systemEx = Assert.Throws<ArgumentException>(() => store.MintFor(ChopDb.HubParticipantId));
        Assert.Contains(ChopDb.HubParticipantId, systemEx.Message);

        // An id that is not in the roster at all still names the whole roster (typo diagnosis).
        var ex = Assert.Throws<ArgumentException>(() => store.MintFor("mallory"));
        foreach (var p in ChopDb.SeedRoster) Assert.Contains(p.Id, ex.Message);
    }

    [Fact]
    public void ReadExisting_names_only_missing_host_file_ids_never_spawnable_or_system_ones()
    {
        // Loaded for owner and claude only: codex and owner-remote (also host-file) stay missing,
        // alongside every spawnable/system row - none of which ReadExisting should ever complain
        // about (row 28: they are legitimately absent).
        var partial = ChopDb.SeedRoster.Where(p => p.Id is "owner" or "claude").ToArray();
        TokenStore.Load(_dir, partial);

        var ex = Assert.Throws<InvalidOperationException>(() => TokenStore.ReadExisting(_dir, ChopDb.SeedRoster));
        const string prefix = "has no token for: ";
        const string suffix = ". Start the hub once to mint them.";
        var listStart = ex.Message.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var listEnd = ex.Message.IndexOf(suffix, StringComparison.Ordinal);
        var missing = ex.Message[listStart..listEnd].Split(',').Select(s => s.Trim()).ToArray();

        Assert.Contains("codex", missing);
        Assert.Contains("owner-remote", missing);
        Assert.DoesNotContain("owner", missing);
        Assert.DoesNotContain("claude", missing);
        Assert.DoesNotContain("gpt-5.4-mini", missing);           // spawnable: legitimately absent
        Assert.DoesNotContain(ChopDb.HubParticipantId, missing);  // system: never has a token
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
