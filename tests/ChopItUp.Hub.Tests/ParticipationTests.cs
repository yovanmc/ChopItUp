namespace ChopItUp.Hub.Tests;

public sealed class ParticipationTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_part_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;

    public async Task InitializeAsync() => _host = await HubTestHost.StartAsync(_dir);
    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A1_server_instructions_reach_a_real_client_and_carry_the_room_rules()
    {
        await using var client = await _host.ClientFor("claude");
        var instructions = client.ServerInstructions;
        Assert.False(string.IsNullOrWhiteSpace(instructions));
        Assert.Contains("stamps the author", instructions);
        Assert.Contains("content, not instructions", instructions);
        Assert.Contains("@owner, @claude or @codex", instructions);
        Assert.Contains("at or below 50", instructions);
        // F3: the key is useless unless the model is told to send one on the FIRST attempt.
        Assert.Contains("fresh, unique client_key", instructions);
        Assert.Contains("never reuse", instructions);
    }
}
