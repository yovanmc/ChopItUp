using ChopItUp.Core.Memory;
using ChopItUp.Hub.Spawning;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed partial class SpawnerServiceTests
{
    [Fact]
    public async Task A2_a_spawn_prompt_carries_the_core_written_on_disk_and_the_widened_claude_allow_list()
    {
        var memory = _host.Services.GetRequiredService<MemoryStore>();
        File.WriteAllText(memory.CorePath, "# Memory\n\nCodeword: PELICAN-42.\n");
        memory.Append("check", "Seeded", "A topic.", "test");

        await PostAsOwner("@opus what is the codeword?");
        var opus = await _runner.NextSpecAsync(Wait);
        Assert.Contains("Codeword: PELICAN-42.", opus.StandardInput);
        Assert.Contains("recall(topic): check.", opus.StandardInput);
        Assert.Contains("propose_memory once, with room_id \"general\"", opus.StandardInput);
        var allowed = opus.Arguments[opus.Arguments.ToList().IndexOf("--allowedTools") + 1];
        Assert.Equal("mcp__chopitup__post_message,mcp__chopitup__recall,mcp__chopitup__propose_memory", allowed);
    }
}
