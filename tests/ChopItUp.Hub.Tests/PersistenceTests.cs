using System.Text.Json;

namespace ChopItUp.Hub.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task Messages_and_tokens_survive_a_restart_and_a_corpus_round_trips()
    {
        string dir = Path.Combine(Path.GetTempPath(), "chopitup_persist_" + Guid.NewGuid().ToString("N"));
        string claudeToken, codexToken, ownerToken;
        long lastId;
        const int corpus = 230; // > MaxLimit: forces paging on the full read AND proves unread_count is not capped

        await using (var first = await HubTestHost.StartAsync(dir, deleteOnDispose: false))
        {
            claudeToken = first.TokenFor("claude");
            codexToken = first.TokenFor("codex");
            ownerToken = first.TokenFor("owner");
            await using var claude = await first.ClientFor("claude");
            await using var codex = await first.ClientFor("codex");
            for (int i = 1; i <= corpus; i++)
            {
                var author = i % 2 == 0 ? codex : claude;
                var r = await author.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = $"synthetic message {i}" });
                Assert.NotEqual(true, r.IsError);
            }
            lastId = HubTestHost.Json(await claude.CallToolAsync("list_rooms", new Dictionary<string, object?>()))
                .GetProperty("rooms")[0].GetProperty("last_message_id").GetInt64();
        }

        try
        {
            await using var second = await HubTestHost.StartAsync(dir, deleteOnDispose: true);
            // Row 28 AC3: a host-file token pasted before the restart still authenticates. It cannot
            // be looked back up through `second` (hashed at rest - the whole point of the fix), so
            // the plaintext captured before the restart is presented directly.
            await using (var codex = await second.ClientFor("codex", codexToken))
            {
                var r = await codex.CallToolAsync("list_rooms", new Dictionary<string, object?>());
                Assert.NotEqual(true, r.IsError);
            }
            await using var claude = await second.ClientFor("claude", claudeToken);

            // The owner never read anything: unread must be the true total, not the 200 page cap.
            await using var owner = await second.ClientFor("owner", ownerToken);
            var general = HubTestHost.Json(await owner.CallToolAsync("list_rooms", new Dictionary<string, object?>()))
                .GetProperty("rooms")[0];
            Assert.Equal(corpus, general.GetProperty("message_count").GetInt32());
            Assert.Equal(corpus, general.GetProperty("unread_count").GetInt32());

            var all = new List<JsonElement>();
            long after = 0;
            bool more;
            do
            {
                var page = HubTestHost.Json(await claude.CallToolAsync("read_messages",
                    new Dictionary<string, object?> { ["room_id"] = "general", ["after_id"] = after, ["limit"] = 50 }));
                all.AddRange(page.GetProperty("messages").EnumerateArray());
                after = page.GetProperty("next_after_id").GetInt64();
                more = page.GetProperty("has_more").GetBoolean();
            } while (more);

            Assert.Equal(corpus, all.Count);
            Assert.Equal(lastId, all[^1].GetProperty("id").GetInt64());
            Assert.Equal("synthetic message 1", all[0].GetProperty("body").GetString());
            Assert.Equal("codex", all[1].GetProperty("author_id").GetString());
            var ids = all.Select(m => m.GetProperty("id").GetInt64()).ToList();
            Assert.True(ids.SequenceEqual(ids.OrderBy(x => x)));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
