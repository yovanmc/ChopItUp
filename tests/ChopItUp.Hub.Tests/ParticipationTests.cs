using ChopItUp.Core.Storage;

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
        foreach (var p in ChopDb.SeedRoster) Assert.Contains("@" + p.Id, instructions);
        Assert.DoesNotContain("@owner, @claude or @codex", instructions);
        // Exactly one blank line between the roster paragraph and the rules: fails if the header is
        // written as one raw string with a trailing blank line (a raw string drops its final newline).
        Assert.Contains("host and model.\n\nTaking part", instructions);
        Assert.Contains("at or below 50", instructions);
        // The key is useless unless the model is told to send one on the FIRST attempt - but it is
        // an OPTIONAL parameter, and saying so is not a nicety. Codex read the old imperative
        // ("Give every post_message call a fresh, unique client_key - a UUID, ...") as a hard
        // requirement to mint a UUID, called into a crypto API its runtime does not have, and died
        // with ReferenceError before post_message was ever reached (2026-09-05). The schema always
        // said optional; only the prose lied, and the model believed the prose.
        Assert.Contains("client_key is optional", instructions);
        Assert.Contains("never reuse", instructions);
        Assert.DoesNotContain("Give every post_message call a fresh, unique client_key", instructions);
    }
}
