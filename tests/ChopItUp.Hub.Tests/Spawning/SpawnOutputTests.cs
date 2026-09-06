using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>Fixtures are the shapes measured on 2026-09-06 (plan claims 23, 24), cut to the fields the
/// parser reads; the paths and text are fabricated.</summary>
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
        {"type":"result","subtype":"success","is_error":false,"num_turns":6,"result":"STEP 1: OK\nSTEP 2: DENIED","permission_denials":[{"tool_name":"Bash","tool_use_id":"toolu_01B","tool_input":{"command":"git -c user.name=x commit --allow-empty -m probe"}}],"total_cost_usd":0.01}
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
        Assert.Empty(SpawnOutput.CodexShellCommands(junk));
        Assert.Empty(SpawnOutput.ClaudeShellCommands(""));
        Assert.Empty(SpawnOutput.CodexShellCommands(""));
        Assert.Null(SpawnOutput.ClaudeFinalText(junk));
    }

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
    }
}
