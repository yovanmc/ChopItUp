using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>Fixtures are the measured CLI output shapes, cut to the fields the parser reads; the
/// paths and text are fabricated.</summary>
public sealed class SpawnOutputTests
{
    private const string ClaudeStream = """
        {"type":"system","subtype":"init","session_id":"s"}
        {"type":"assistant","message":{"content":[{"type":"text","text":"I'll do it."}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01A","name":"Bash","input":{"command":"echo hello","description":"Say hi"}}]}}
        {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_01A","content":"hello"}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01B","name":"Bash","input":{"command":"git -c user.name=x commit --allow-empty -m probe"}}]}}
        {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_01B","is_error":true,"content":"Permission to use Bash with command git -c user.name=x commit --allow-empty -m probe has been denied."}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01C","name":"Write","input":{"file_path":"C:\\room\\probe.txt","content":"PROBE"}}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01D","name":"Bash","input":{"command":"dir\r\ntype probe.txt"}}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01E","name":"WebSearch","input":{"query":"IANA example domain","mode":"standard"}}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01F","name":"WebFetch","input":{"url":"https://example.com/","prompt":"What is the page title?"}}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01G","name":"WebFetch","input":{"url":"http://127.0.0.1:8790/api/rooms","prompt":"List it."}}]}}
        {"type":"result","subtype":"success","is_error":false,"num_turns":6,"result":"STEP 1: OK\nSTEP 2: DENIED","permission_denials":[{"tool_name":"Bash","tool_use_id":"toolu_01B","tool_input":{"command":"git -c user.name=x commit --allow-empty -m probe"}},{"tool_name":"WebFetch","tool_use_id":"toolu_01G","tool_input":{"url":"http://127.0.0.1:8790/api/rooms"}}],"total_cost_usd":0.01}
        """;

    private const string CodexStream = """
        {"type":"thread.started","thread_id":"t"}
        {"type":"turn.started"}
        {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Working."}}
        {"type":"item.started","item":{"id":"item_1","type":"command_execution","command":"\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -Command dir","aggregated_output":"","exit_code":null,"status":"in_progress"}}
        {"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -Command dir","aggregated_output":"listing","exit_code":0,"status":"completed"}}
        {"type":"item.started","item":{"id":"item_3","type":"command_execution","command":"\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -Command 'git commit --allow-empty -m probe'","aggregated_output":"","exit_code":null,"status":"in_progress"}}
        {"type":"turn.completed","usage":{"input_tokens":1}}
        """;

    [Fact]
    public void M9_A9_claude_stream_yields_bash_commands_in_order_with_denials_marked_and_other_tools_ignored()
    {
        var commands = SpawnOutput.ClaudeShellCommands(ClaudeStream);
        Assert.Equal(
            [new ShellCommand("echo hello", false), new ShellCommand("git -c user.name=x commit --allow-empty -m probe", true), new ShellCommand("dir ⏎ type probe.txt", false)],
            commands);
    }

    [Fact]
    public void Claude_stream_yields_web_searches_and_fetches_in_order_with_denials_marked()
    {
        Assert.Equal(
            [new WebRequest("WebSearch", "IANA example domain", false), new WebRequest("WebFetch", "https://example.com/", false),
             new WebRequest("WebFetch", "http://127.0.0.1:8790/api/rooms", true)],
            SpawnOutput.ClaudeWebRequests(ClaudeStream));
    }

    [Fact]
    public void M9_A9_claude_final_text_comes_from_the_last_result_line_or_from_a_single_object()
    {
        Assert.Equal("STEP 1: OK\nSTEP 2: DENIED", SpawnOutput.ClaudeFinalText(ClaudeStream));
        Assert.Equal("hello", SpawnOutput.ClaudeFinalText("""{"type":"result","subtype":"success","is_error":false,"result":"hello"}"""));
        Assert.Equal("hello", SpawnCommands.ClaudeFinalText("""{"type":"result","subtype":"success","is_error":false,"result":"hello"}"""));
        Assert.Null(SpawnOutput.ClaudeFinalText("not json"));
        Assert.Null(SpawnOutput.ClaudeFinalText(""));
        Assert.Null(SpawnOutput.ClaudeFinalText("{\"type\":\"assistant\"}\n{\"type\":\"result\",\"result\":\"   \"}"));
    }

    [Fact]
    public void M9_A9_codex_stream_yields_command_executions_once_each_in_first_seen_order_including_one_that_only_started()
    {
        var commands = SpawnOutput.CodexShellCommands(CodexStream);
        Assert.Equal(
            [new ShellCommand("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -Command dir", false),
             new ShellCommand("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -Command 'git commit --allow-empty -m probe'", false)],
            commands);
    }

    [Fact]
    public void M9_A9_garbage_and_foreign_shapes_yield_nothing_and_never_throw()
    {
        const string junk = "not json\n{\"type\":\"assistant\"}\n{\"item\":5}\n{\"type\":\"assistant\",\"message\":{\"content\":\"text\"}}\n[1,2]\n{\"item\":{\"type\":\"command_execution\"}}\n";
        Assert.Empty(SpawnOutput.ClaudeShellCommands(junk));
        Assert.Empty(SpawnOutput.ClaudeWebRequests(junk + "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"WebFetch\",\"input\":{\"prompt\":\"no url\"}}]}}\n"));
        Assert.Empty(SpawnOutput.CodexShellCommands(junk));
        Assert.Empty(SpawnOutput.ClaudeShellCommands(""));
        Assert.Empty(SpawnOutput.CodexShellCommands(""));
        Assert.Null(SpawnOutput.ClaudeFinalText(junk));
    }

    private const string CodexTurnFailedWrappedJson = """
        {"type":"thread.started","thread_id":"t"}
        {"type":"turn.started"}
        {"type":"turn.failed","error":{"message":"{\"type\":\"error\",\"status\":400,\"error\":{\"type\":\"invalid_request_error\",\"message\":\"The 'gpt-bogus-model' model is not supported for this request.\"}}"}}
        """;

    private const string CodexTurnFailedPlain = """
        {"type":"turn.failed","error":{"message":"context deadline exceeded"}}
        """;

    private const string CodexErrorLineOnly = """
        {"type":"thread.started","thread_id":"t"}
        {"type":"error","message":"stream disconnected"}
        """;

    private const string CodexTurnCompletedNoFailure = """
        {"type":"thread.started","thread_id":"t"}
        {"type":"turn.completed","usage":{"input_tokens":1}}
        """;

    [Theory]
    [InlineData(CodexTurnFailedWrappedJson, "The 'gpt-bogus-model' model is not supported for this request.")]   // a turn.failed message that is itself a json error envelope is unwrapped
    [InlineData(CodexTurnFailedPlain, "context deadline exceeded")]                                           // a plain turn.failed message is returned as is
    [InlineData(CodexErrorLineOnly, "stream disconnected")]                                                    // an error line is the fallback when no turn.failed event exists
    [InlineData(CodexTurnCompletedNoFailure, null)]                                                            // the turn completed without failing
    [InlineData("not json\n{\"type\":\"turn.failed\"}\n{\"error\":5}\n[1,2]\n", null)]                           // junk
    [InlineData("", null)]                                                                                     // empty stdout
    public void codex_failure_reads_the_failure_message_from_the_stream_and_is_null_without_one(string stdout, string? expected)
    {
        Assert.Equal(expected, SpawnOutput.CodexFailure(stdout));
    }

    // Trimmed from a measured `claude -p --output-format json` result: the session, uuid and credential
    // fields stay in the fixture so the test proves they are not copied.
    private const string ClaudeUsage = """{"input_tokens":2,"output_tokens":4,"cache_read_input_tokens":649}""";
    private const string ClaudeModelUsage = """{"claude-haiku-5-5":{"inputTokens":2,"outputTokens":4,"costUSD":0.0007}}""";
    private const string ClaudeJsonResult = """{"type":"result","subtype":"success","is_error":false,"session_id":"s-1","uuid":"u-1","credential_expires_at":"2026-12-13T20:00:36.036Z","total_cost_usd":0.0007,"usage":""" + ClaudeUsage + ""","modelUsage":""" + ClaudeModelUsage + ""","result":"ok"}""";
    private const string ClaudeBlock = """{"host":"claude","usage":""" + ClaudeUsage + ""","modelUsage":""" + ClaudeModelUsage + "}";
    private const string CodexUsage = """{"input_tokens":15789,"cached_input_tokens":11392,"output_tokens":5}""";

    public static TheoryData<string, string, bool, string, string?> UsageCases => new()
    {
        { "claude json result", "claude", true, ClaudeJsonResult, ClaudeBlock },
        { "claude stream-json final result line", "claude", true, "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\"}\n{\"type\":\"assistant\",\"message\":{\"usage\":{\"input_tokens\":9}}}\n" + ClaudeJsonResult + "\n", ClaudeBlock },
        { "claude modelUsage only", "claude", true, """{"type":"result","usage":null,"modelUsage":""" + ClaudeModelUsage + "}", """{"host":"claude","modelUsage":""" + ClaudeModelUsage + "}" },
        { "claude result without usage fields", "claude", true, """{"type":"result","result":"ok"}""", null },
        { "claude stdout that is not json", "claude", true, "Error: something broke", null },
        { "claude empty stdout", "claude", true, "", null },
        { "codex --json last turn.completed", "codex", true, "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1}}\n{\"type\":\"turn.completed\",\"usage\":" + CodexUsage + "}\n", """{"host":"codex","usage":""" + CodexUsage + "}" },
        { "codex --json without turn.completed", "codex", true, "{\"type\":\"turn.started\"}\n", null },
        { "plain codex reply quoting an event line", "codex", false, "Here is the event:\n{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1}}\n", null },
        { "block over 16 KB", "claude", true, """{"type":"result","modelUsage":{"m":{"note":" """ + new string('x', 16_400) + "\"}}}", null },
    };

    [Theory]
    [MemberData(nameof(UsageCases))]
    public void Usage_keeps_only_the_reported_usage_fields_and_is_null_when_nothing_was_reported(string _, string host, bool structured, string stdout, string? expected) =>
        Assert.Equal(expected, SpawnOutput.Usage(host, structured, stdout));

    public static TheoryData<string, string, bool, string, int?> ToolCallCases => new()
    {
        // Claude stream-json: every tool_use block, denied ones included.
        { "claude stream-json lists five calls", "claude", true, ClaudeStream, 7 },
        { "claude stream-json with no calls", "claude", true, "{\"type\":\"system\",\"subtype\":\"init\"}\n{\"type\":\"result\",\"result\":\"ok\"}\n", 0 },
        { "claude plain json lists no calls", "claude", false, """{"type":"result","num_turns":3,"result":"ok"}""", null },
        // Codex --json: allow-listed item types, once per id.
        { "codex items paired by id", "codex", true, CodexStream, 2 },
        { "codex mcp, file and web items count, messages do not", "codex", true,
          "{\"type\":\"item.completed\",\"item\":{\"id\":\"a\",\"type\":\"agent_message\",\"text\":\"x\"}}\n" +
          "{\"type\":\"item.started\",\"item\":{\"id\":\"b\",\"type\":\"mcp_tool_call\"}}\n{\"type\":\"item.completed\",\"item\":{\"id\":\"b\",\"type\":\"mcp_tool_call\"}}\n" +
          "{\"type\":\"item.completed\",\"item\":{\"id\":\"c\",\"type\":\"file_change\"}}\n{\"type\":\"item.completed\",\"item\":{\"id\":\"d\",\"type\":\"web_search\"}}\n" +
          "{\"type\":\"item.completed\",\"item\":{\"id\":\"e\",\"type\":\"reasoning\"}}\n{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\"}}\n", 4 },
        { "plain codex lists no calls", "codex", false, "done\n", null },
        { "empty kept stream (an overflow keeps none)", "claude", true, "", null },
        { "hostile shapes never throw", "codex", true, "{\"type\":\"item.completed\",\"item\":[1]}\n{\"type\":\"item.completed\",\"item\":{\"id\":7,\"type\":\"mcp_tool_call\"}}\n[1,2]\n{\"type\":\"assistant\",\"message\":{\"content\":\"text\"}}\n", 1 },
        { "hostile claude shapes never throw", "claude", true, "{\"type\":\"assistant\",\"message\":[1]}\n{\"type\":\"assistant\",\"message\":{\"content\":[1,\"x\",{\"type\":\"tool_use\"}]}}\n", 1 },
    };

    /// <summary>codex-cli 0.169.1 `exec --json`, one default web search (measured 2026-11-23): each item
    /// carries two <c>id</c> keys, and the started event has an empty query.</summary>
    private const string CodexWebSearch = """
        {"type":"thread.started","thread_id":"t"}
        {"type":"turn.started"}
        {"type":"item.started","item":{"id":"item_1","type":"web_search","id":"exec-d95b5fd1","query":"","action":{"type":"other"}}}
        {"type":"item.completed","item":{"id":"item_1","type":"web_search","id":"exec-d95b5fd1","query":"Model Context Protocol specification latest revision","action":{"type":"search","query":"Model Context Protocol specification latest revision"},"results":[{"type":"text_result","domain":"github.com"}]}}
        {"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":"2026-07-28"}}
        {"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":2}}
        """;

    [Fact]
    public void The_measured_codex_web_search_counts_once()
    {
        Assert.Equal(1, SpawnOutput.ToolCalls("codex", true, CodexWebSearch));
        Assert.Equal([new WebRequest("WebSearch", "Model Context Protocol specification latest revision", false)], SpawnOutput.CodexWebRequests(CodexWebSearch));
    }

    public static TheoryData<string, string, WebRequest[]> CodexWebCases => new()
    {
        { "search falls back to the item query", """{"type":"item.completed","item":{"id":"a","type":"web_search","query":"q1","action":{"type":"search"}}}""", [new("WebSearch", "q1", false)] },
        { "an opened page records its url", """{"type":"item.completed","item":{"id":"b","type":"web_search","action":{"type":"open_page","url":"https://example.com/x"}}}""", [new("open_page", "https://example.com/x", false)] },
        { "another action records its type and first target", """{"type":"item.completed","item":{"id":"c","type":"web_search","action":{"type":"find_in_page","pattern":"needle"}}}""", [new("find_in_page", "needle", false)] },
        { "an action with no target says so", """{"type":"item.completed","item":{"id":"d","type":"web_search","action":{"type":"mystery"}}}""", [new("mystery", "(no target)", false)] },
        { "a search that never completed is marked", """{"type":"item.started","item":{"id":"e","type":"web_search","query":"","action":{"type":"other"}}}""", [new("WebSearch", "(no target) (not completed)", false)] },
        { "other item types and hostile shapes are skipped", "{\"type\":\"item.completed\",\"item\":[1]}\n[1]\n{\"type\":\"item.completed\",\"item\":{\"id\":\"f\",\"type\":\"command_execution\",\"command\":\"ls\"}}\n{\"type\":\"item.completed\",\"item\":{\"id\":\"g\",\"type\":\"web_search\",\"action\":[1],\"query\":7}}\nnot json\n", [new("WebSearch", "(no target)", false)] },
        { "nothing listed", "", [] },
    };

    [Theory]
    [MemberData(nameof(CodexWebCases))]
    public void Codex_web_requests_name_each_search_or_page_in_order(string _, string stdout, WebRequest[] expected) =>
        Assert.Equal(expected, SpawnOutput.CodexWebRequests(stdout));

    [Theory]
    [MemberData(nameof(ToolCallCases))]
    public void Tool_calls_are_counted_only_from_streams_that_list_them(string _, string host, bool stream, string stdout, int? expected) =>
        Assert.Equal(expected, SpawnOutput.ToolCalls(host, stream, stdout));

    [Fact]
    public void M9_A9_flatten_folds_line_breaks_caps_at_400_characters_and_the_list_caps_at_50()
    {
        Assert.Equal("a ⏎ b ⏎ c", SpawnOutput.Flatten("a\r\nb\nc"));
        var flat = SpawnOutput.Flatten(new string('x', 500));
        Assert.Equal(401, flat.Length);
        Assert.EndsWith("…", flat);

        var many = string.Join('\n', Enumerable.Range(0, 60).Select(i =>
            $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t{{{i}}}","name":"Bash","input":{"command":"echo {{{i}}}"}}]}}"""));
        Assert.Equal(50, SpawnOutput.ClaudeShellCommands(many).Count);
        Assert.Equal("echo 0", SpawnOutput.ClaudeShellCommands(many)[0].Command);

        var fetches = string.Join('\n', Enumerable.Range(0, 60).Select(i =>
            $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"w{{{i}}}","name":"WebFetch","input":{"url":"https://example.com/{{{i}}}"}}]}}"""));
        Assert.Equal(50, SpawnOutput.ClaudeWebRequests(fetches).Count);
        Assert.Equal(401, SpawnOutput.ClaudeWebRequests(
            $$$$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"w","name":"WebSearch","input":{"query":"{{{{{new string('q', 500)}}}}}"}}]}}""")[0].Target.Length);
    }
}
