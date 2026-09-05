using System.Globalization;
using System.Text.Json;
using ChopItUp.Corpus;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// Three verbs, selected by flag (never a leading subcommand token, so the literal invocation the
// plan specifies — `--data <dir> --messages 10000 --rooms 3 --leave-in-wal 500 --fingerprint-out
// <path>` with no leading verb — keeps working as the default/build mode):
//   (default)     seed a fresh v1 corpus:            --data --messages --rooms --leave-in-wal --fingerprint-out --schema-version
//   --inspect     quick_check + user_version + fingerprint of an EXISTING db: --db --fingerprint-out
//   --mcp-check   post_message x2 + list_rooms over the real MCP transport:  --url --room --body --client-key --out
//                 (bearer token via the CHOPITUP_MCP_TOKEN env var, never a CLI arg — same reasoning
//                 HostConfigs.cs gives for putting mcp-remote's header value in env, not argv)
var parsed = ParseArgs(args);

if (parsed.ContainsKey("--inspect"))
    return RunInspect(parsed);
if (parsed.ContainsKey("--mcp-check"))
    return await RunMcpCheckAsync(parsed);
return RunBuild(parsed);

static int RunBuild(Dictionary<string, string> args)
{
    var data = Require(args, "--data");
    int messages = int.Parse(args.GetValueOrDefault("--messages", "10000"), CultureInfo.InvariantCulture);
    int rooms = int.Parse(args.GetValueOrDefault("--rooms", "3"), CultureInfo.InvariantCulture);
    int leaveInWal = int.Parse(args.GetValueOrDefault("--leave-in-wal", "500"), CultureInfo.InvariantCulture);
    var fingerprintOut = args.GetValueOrDefault("--fingerprint-out");
    int schemaVersion = int.Parse(args.GetValueOrDefault("--schema-version", "1"), CultureInfo.InvariantCulture);

    var handle = CorpusBuilder.Build(data, messages, rooms, leaveInWal, schemaVersion: schemaVersion);
    if (fingerprintOut is not null)
        File.WriteAllText(fingerprintOut, handle.Fingerprint.ToCanonicalJson());
    Console.WriteLine($"Wrote {messages} messages across {rooms} rooms to '{data}' ({leaveInWal} left un-checkpointed in the write-ahead log).");

    // Deliberately do NOT dispose `handle` and do NOT let this method return normally: SQLite runs
    // an automatic checkpoint when the last connection to a WAL database closes cleanly (verified
    // empirically before writing this — a plain Dispose() removes the -wal file entirely), which
    // would flush the WAL-only tail into the main file before any caller ever gets to run a
    // migration against it. Environment.Exit terminates the process without unwinding to that
    // Dispose, leaving the WAL exactly as written — the same shape a killed process would leave,
    // and the state Invoke-M2DryRun.ps1's migration step depends on.
    Environment.Exit(0);
    return 0; // unreachable
}

static int RunInspect(Dictionary<string, string> args)
{
    var dbPath = Require(args, "--db");
    var outPath = args.GetValueOrDefault("--fingerprint-out");
    try
    {
        var (quickCheck, userVersion, fingerprint) = CorpusBuilder.Inspect(dbPath);
        var summary = new { path = dbPath, quick_check = quickCheck, user_version = userVersion, fingerprint };
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        if (outPath is not null) File.WriteAllText(outPath, json);
        else Console.WriteLine(json);
        return 0;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Could not inspect '{dbPath}': {e.Message}");
        return 1;
    }
}

static async Task<int> RunMcpCheckAsync(Dictionary<string, string> args)
{
    var baseUrl = Require(args, "--url");
    var token = Environment.GetEnvironmentVariable("CHOPITUP_MCP_TOKEN");
    if (string.IsNullOrEmpty(token))
    {
        Console.Error.WriteLine("--mcp-check requires the CHOPITUP_MCP_TOKEN environment variable.");
        return 1;
    }
    var room = args.GetValueOrDefault("--room", "general");
    var body = args.GetValueOrDefault("--body", "Dry-run smoke test message.");
    var clientKey = Require(args, "--client-key");
    var outPath = Require(args, "--out");

    try
    {
        var endpoint = new Uri(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), "mcp");
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        });
        await using var client = await McpClient.CreateAsync(transport);

        var callArgs = new Dictionary<string, object?> { ["room_id"] = room, ["body"] = body, ["client_key"] = clientKey };
        var first = ParseJson(await client.CallToolAsync("post_message", callArgs));
        var second = ParseJson(await client.CallToolAsync("post_message", callArgs));
        var rooms = ParseJson(await client.CallToolAsync("list_rooms", new Dictionary<string, object?>()));

        // The corpus is multi-room (messages round-robin across rooms), so the room posted to holds
        // only its own share of the 10,000 seeded messages, not all of them. "list_rooms shows
        // 10,001" is a hub-wide claim: the sum of every room's message_count across the whole hub,
        // which is exactly the corpus total plus this one new post.
        long totalMessageCount = rooms.GetProperty("rooms").EnumerateArray()
            .Sum(r => r.GetProperty("message_count").GetInt64());

        var summary = new
        {
            first_id = first.GetProperty("id").GetInt64(),
            first_deduplicated = first.TryGetProperty("deduplicated", out var d1) && d1.ValueKind == JsonValueKind.True,
            second_id = second.GetProperty("id").GetInt64(),
            second_deduplicated = second.TryGetProperty("deduplicated", out var d2) && d2.ValueKind == JsonValueKind.True,
            total_message_count = totalMessageCount,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch (Exception e)
    {
        // Message-only: never let a caught exception's ToString() (which could echo request state)
        // reach the console, since this process runs with a live bearer token in its environment.
        Console.Error.WriteLine($"MCP check failed: {e.Message}");
        return 1;
    }
}

static JsonElement ParseJson(CallToolResult result)
{
    if (result.IsError == true)
        throw new InvalidOperationException("Tool call returned an error: " + string.Join(" | ", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
    var text = result.Content.OfType<TextContentBlock>().Select(t => t.Text).FirstOrDefault()
        ?? throw new InvalidOperationException("Tool call result had no text content.");
    return JsonDocument.Parse(text).RootElement;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
        var key = args[i];
        if (key is "--inspect" or "--mcp-check") { result[key] = "true"; continue; }
        if (i + 1 >= args.Length) throw new ArgumentException($"{key} requires a value.");
        result[key] = args[++i];
    }
    return result;
}

static string Require(Dictionary<string, string> args, string key) =>
    args.TryGetValue(key, out var v) ? v : throw new ArgumentException($"{key} is required.");
