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
        Assert.Contains("recall(query): check.", opus.StandardInput);
        Assert.Contains("propose_memory once, with room_id \"general\"", opus.StandardInput);
        var allowed = opus.Arguments[opus.Arguments.ToList().IndexOf("--allowedTools") + 1];
        Assert.Equal("mcp__chopitup__post_message,mcp__chopitup__recall,mcp__chopitup__propose_memory", allowed);
    }

    [Fact]
    public async Task R18_a_directory_room_spawn_carries_its_room_topic_and_a_plain_room_does_not()
    {
        var memory = _host.Services.GetRequiredService<MemoryStore>();
        await MakeRoom("proj");   // SpawnerServiceTests.Rooms.cs:21 — git-inits the directory, then CreateRoom (critique P1-20)
        memory.Append("room-proj", "Stack", ".NET 10.", "p");
        await PostAsOwner("@opus hi");
        var plain = await _runner.NextSpecAsync(Wait);
        Assert.DoesNotContain("Memory for this room only", plain.StandardInput);
        await PostAsOwnerIn("proj", "@opus hi");   // SpawnerServiceTests.Rooms.cs:29
        var scoped = await _runner.NextSpecAsync(Wait);
        // The fence key is the spawn id, minted per spawn: assert the section's shape around it, not the key.
        Assert.Contains("Memory for this room only (topic `room-proj`), same rule:\n--- begin memory ", scoped.StandardInput);
        Assert.Contains(" ---\n# room-proj\n\n## Stack\n<!-- p -->\n.NET 10.\n--- end memory ", scoped.StandardInput);
        Assert.Contains("go to topic \"room-proj\"", scoped.StandardInput);
    }
}
