using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Security;

namespace ChopItUp.Hub.Tests;

public sealed class TokenStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_tok_" + Guid.NewGuid().ToString("N"));
    private static readonly string[] Roster = ChopDb.SeedRoster.Select(p => p.Id).ToArray();

    [Fact]
    public void M8_A2_a_three_key_tokens_json_is_backfilled_with_the_original_values_byte_identical()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, TokenStore.FileName);
        var original = new Dictionary<string, string> { ["owner"] = "tok-owner", ["claude"] = "tok-claude", ["codex"] = "tok-codex" };
        File.WriteAllText(path, JsonSerializer.Serialize(original));

        var store = TokenStore.Load(_dir, Roster);

        Assert.Equal(Roster.Length, store.Count);
        foreach (var (id, token) in original) Assert.Equal(token, store.Tokens[id]);
        foreach (var id in Roster) Assert.False(string.IsNullOrWhiteSpace(store.Tokens[id]));
        foreach (var (id, token) in store.Tokens)
        {
            Assert.True(store.TryResolve(token, out var resolved));
            Assert.Equal(id, resolved);
        }
        var reread = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        Assert.Equal(store.Tokens.OrderBy(kv => kv.Key), reread.OrderBy(kv => kv.Key));
    }

    [Fact]
    public void M8_A5_rotate_accepts_any_roster_id_and_rejects_others_naming_the_roster()
    {
        TokenStore.Load(_dir, Roster);
        var before = TokenStore.ReadExisting(_dir, Roster);

        var minted = TokenStore.Rotate(_dir, Roster, "gpt-6-astra");
        var after = TokenStore.ReadExisting(_dir, Roster);
        Assert.Equal(minted, after["gpt-6-astra"]);
        foreach (var id in Roster.Where(id => id != "gpt-6-astra")) Assert.Equal(before[id], after[id]);

        var ex = Assert.Throws<ArgumentException>(() => TokenStore.Rotate(_dir, Roster, "mallory"));
        foreach (var id in Roster) Assert.Contains(id, ex.Message);
    }

    [Fact]
    public void ReadExisting_names_every_roster_id_missing_from_the_file()
    {
        TokenStore.Load(_dir, ["owner", "claude"]);
        var ex = Assert.Throws<InvalidOperationException>(() => TokenStore.ReadExisting(_dir, Roster));
        Assert.Contains("gpt-5.4-mini", ex.Message);
        Assert.DoesNotContain("owner", ex.Message.Split(':').Last());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
