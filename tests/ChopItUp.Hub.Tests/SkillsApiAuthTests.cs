using System.Net;
using System.Net.Http.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests;

/// <summary>The hub's owner-class gate on every non-GET <c>/api</c> route, as seen from the skill-proposal
/// decision endpoints. <see cref="SkillsApiProposalsTests"/> covers the decision logic once a caller
/// is let through (its helpers authenticate as the hub owner); this file covers the gate itself: no
/// credential or an unresolvable one is 401 and changes nothing, a credential resolving to a
/// non-owner participant is 403 and changes nothing, and <c>GET</c> here stays reachable with no
/// credential. The sibling <c>/api/memory</c> decision route is gated the same way, see
/// <see cref="Memory_proposal_approve_a_different_api_route_now_requires_the_owner_credential_too"/>.</summary>
public sealed class SkillsApiAuthTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_skillsauth_" + Guid.NewGuid().ToString("N"));
    private readonly string _roomDir = Path.Combine(Path.GetTempPath(), "chopitup_skillsauth_room_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_roomDir);
        _host = await HubTestHost.StartAsync(_dir);
        _host.Services.GetRequiredService<MessageStore>().CreateRoom("proj", "Proj", _roomDir);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        if (Directory.Exists(_roomDir)) Directory.Delete(_roomDir, recursive: true);
    }

    private SkillProposalStore Proposals => _host.Services.GetRequiredService<SkillProposalStore>();
    private SkillStore Skills => _host.Services.GetRequiredService<SkillStore>();

    private const string ValidSkillBody = "---\nname: demo\ndescription: A demo skill for tests.\n---\n# Demo Skill\n\nBody text here.\n";

    private async Task<long> ProposeAsync()
    {
        var dir = Path.Combine(_roomDir, "demo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), ValidSkillBody);
        await using var client = await _host.ClientFor("opus");
        var result = HubTestHost.Json(await client.CallToolAsync("propose_skill", new Dictionary<string, object?> { ["room_id"] = "proj", ["source_dir"] = dir }));
        return result.GetProperty("id").GetInt64();
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Add("Authorization", "Bearer " + token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _host.Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Approve(long id, string? token, string? tree = null) =>
        Send(HttpMethod.Post, $"api/skills/proposals/{id}/approve", token, new { treeSha256 = tree });

    private Task<HttpResponseMessage> Reject(long id, string? token) =>
        Send(HttpMethod.Post, $"api/skills/proposals/{id}/reject", token);

    [Fact]
    public async Task Approve_with_no_credential_is_unauthenticated_and_changes_nothing()
    {
        var id = await ProposeAsync();

        var r = await Approve(id, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("pending", Proposals.Get(id)!.Status);
        Assert.False(Directory.Exists(Path.Combine(Skills.Root, "demo")));
    }

    [Fact]
    public async Task Approve_with_an_unresolvable_credential_is_unauthenticated_and_changes_nothing()
    {
        var id = await ProposeAsync();

        var r = await Approve(id, token: "not-a-real-token");

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("pending", Proposals.Get(id)!.Status);
        Assert.False(Directory.Exists(Path.Combine(Skills.Root, "demo")));
    }

    /// <summary>Acceptance 5, last line: "the unauthenticated refusal's shape matches the one the
    /// authenticated channel already returns" — /mcp's own 401 body and header.</summary>
    [Fact]
    public async Task Approve_unauthenticated_refusal_shape_matches_the_mcp_channels_own()
    {
        var id = await ProposeAsync();

        var r = await Approve(id, token: null);

        Assert.Equal("{\"error\":\"unauthorized\"}", await r.Content.ReadAsStringAsync());
        Assert.Contains("Bearer realm=\"chopitup\"", r.Headers.WwwAuthenticate.Select(h => h.ToString()));
    }

    [Fact]
    public async Task Approve_with_a_non_owner_credential_is_forbidden_and_changes_nothing()
    {
        var id = await ProposeAsync();

        var r = await Approve(id, token: _host.TokenFor("opus"));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("pending", Proposals.Get(id)!.Status);
        Assert.False(Directory.Exists(Path.Combine(Skills.Root, "demo")));
    }

    [Fact]
    public async Task Approve_with_the_owner_token_succeeds()
    {
        var id = await ProposeAsync();
        var tree = Proposals.Get(id)!.TreeSha256;

        var r = await Approve(id, token: _host.TokenFor(ChopDb.OwnerParticipantId), tree: tree);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("approved", Proposals.Get(id)!.Status);
    }

    [Fact]
    public async Task Approve_with_the_owner_remote_token_succeeds()
    {
        var id = await ProposeAsync();
        var tree = Proposals.Get(id)!.TreeSha256;

        var r = await Approve(id, token: _host.TokenFor(ChopDb.OwnerRemoteParticipantId), tree: tree);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("approved", Proposals.Get(id)!.Status);
    }

    [Fact]
    public async Task Reject_with_no_credential_is_unauthenticated_and_changes_nothing()
    {
        var id = await ProposeAsync();

        var r = await Reject(id, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("pending", Proposals.Get(id)!.Status);
    }

    [Fact]
    public async Task Reject_with_a_non_owner_credential_is_forbidden_and_changes_nothing()
    {
        var id = await ProposeAsync();

        var r = await Reject(id, token: _host.TokenFor("opus"));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("pending", Proposals.Get(id)!.Status);
    }

    [Fact]
    public async Task Reject_with_the_owner_token_succeeds()
    {
        var id = await ProposeAsync();

        var r = await Reject(id, token: _host.TokenFor(ChopDb.OwnerParticipantId));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("rejected", Proposals.Get(id)!.Status);
    }

    [Fact]
    public async Task GET_the_listing_still_works_with_no_credential()
    {
        await ProposeAsync();

        var r = await Send(HttpMethod.Get, "api/skills/proposals", token: null);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    /// <summary>Auth is by method, not by route list: /api/memory writes the memory store, so leaving
    /// it unauthenticated would reopen the escalation the gate exists to close. No credential is 401
    /// and changes nothing, the hub's owner token succeeds.</summary>
    [Fact]
    public async Task Memory_proposal_approve_a_different_api_route_now_requires_the_owner_credential_too()
    {
        _host.Services.GetRequiredService<MemoryProposalStore>().Create("proj", "opus", "user", "Likes tests", "RED before GREEN.", null);

        var refused = await Send(HttpMethod.Post, "api/memory/proposals/1/approve", token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("pending", _host.Services.GetRequiredService<MemoryProposalStore>().Get(1)!.Status);

        var r = await Send(HttpMethod.Post, "api/memory/proposals/1/approve", token: _host.TokenFor(ChopDb.OwnerParticipantId));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}
