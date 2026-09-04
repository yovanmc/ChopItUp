using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

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
            await using var claude = await Connect(first, claudeToken);
            await using var codex = await Connect(first, codexToken);
            for (int i = 1; i <= corpus; i++)
            {
                var author = i % 2 == 0 ? codex : claude;
                var r = await author.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = $"synthetic message {i}" });
                Assert.NotEqual(true, r.IsError);
            }
            lastId = JsonDocument.Parse(((TextContentBlock)(await claude.CallToolAsync("list_rooms", new Dictionary<string, object?>())).Content[0]).Text)
                .RootElement.GetProperty("rooms")[0].GetProperty("last_message_id").GetInt64();
        }

        try
        {
            await using var second = await HubTestHost.StartAsync(dir, deleteOnDispose: true);
            Assert.Equal(claudeToken, second.TokenFor("claude"));
            Assert.Equal(codexToken, second.TokenFor("codex"));
            await using var claude = await Connect(second, claudeToken);

            // The owner never read anything: unread must be the true total, not the 200 page cap.
            await using var owner = await Connect(second, ownerToken);
            var general = JsonDocument.Parse(((TextContentBlock)(await owner.CallToolAsync("list_rooms", new Dictionary<string, object?>())).Content[0]).Text)
                .RootElement.GetProperty("rooms")[0];
            Assert.Equal(corpus, general.GetProperty("message_count").GetInt32());
            Assert.Equal(corpus, general.GetProperty("unread_count").GetInt32());

            var all = new List<JsonElement>();
            long after = 0;
            bool more;
            do
            {
                var page = JsonDocument.Parse(((TextContentBlock)(await claude.CallToolAsync("read_messages",
                    new Dictionary<string, object?> { ["room_id"] = "general", ["after_id"] = after, ["limit"] = 50 })).Content[0]).Text).RootElement;
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

    private static async Task<McpClient> Connect(HubTestHost host, string token) =>
        await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(host.BaseAddress, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        }));
}
