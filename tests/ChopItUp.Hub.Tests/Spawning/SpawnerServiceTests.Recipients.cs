using ChopItUp.Core.Storage;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>End to end through the real MCP client and the fake runner: only a
/// leading mention hands a turn on or opens an exchange; an inline id is a reference the hub notes
/// and spawns nothing for; an unknown leading word gets its own note beside whatever else the post
/// addressed.</summary>
public sealed class SpawnerServiceRecipientsTests : SpawnerServiceTestBase
{
    [Fact]
    public async Task Row43_AC4_a_leading_hand_on_spawns_a_second_turn()
    {
        _runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "opus")
                await PostAs("opus", "@sonnet over to you");
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwner("@opus go");
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        var second = await _runner.NextSpecAsync(Wait);
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(second));
    }

    [Fact]
    public async Task Row43_AC4_an_inline_only_reply_concludes_with_one_spawn()
    {
        _runner.Handler = async (spec, _, _) =>
        {
            if (FakeProcessRunner.ParticipantOf(spec) == "opus")
                await PostAs("opus", "thanks — @sonnet may know");
            return FakeProcessRunner.Ok("""{"result":"done"}""");
        };

        await PostAsOwner("@opus go");
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.StartsWith("Exchange concluded: 1 of"));
        Assert.Equal(1, _runner.Count);
    }

    [Fact]
    public async Task M49_inline_reference_uses_the_configured_oncall_not_the_referenced_model()
    {
        Mode("primary", "sonnet", null);
        await PostAsOwner("please ask @opus about X");
        Assert.Equal("sonnet", FakeProcessRunner.ParticipantOf(await _runner.NextSpecAsync(Wait)));
        await WaitForStatus("concluded");
        Assert.Equal(1, _runner.Count);
    }

    [Fact]
    public async Task Row43_AC3_an_unknown_leading_word_posts_its_note_and_still_spawns()
    {
        await PostAsOwner("@nobody @opus hi");
        var spec = await _runner.NextSpecAsync(Wait);
        Assert.Equal("opus", FakeProcessRunner.ParticipantOf(spec));
        await WaitForMessage(m => m.Author == ChopDb.HubParticipantId && m.Body.StartsWith("No participant named @nobody"));
    }
}
