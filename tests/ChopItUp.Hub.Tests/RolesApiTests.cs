using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChopItUp.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ChopItUp.Hub.Tests;

/// <summary>Row 14 task 5: <c>/api/rooms/{roomId}/roles</c> (read), <c>/api/rooms/{roomId}/persona</c>,
/// <c>/api/participants/{id}/role</c> and <c>/api/rooms/{roomId}/roles/{participantId}</c> (writes).
/// Every write here is a non-GET <c>/api</c> route, so <c>BearerTokenMiddleware</c> guards it by
/// method already (ledger 8); this file proves that gate holds here too, that the GET reads the live
/// roster rather than the startup-static singleton, and that D-b's "clear the override" and "no role
/// in this room" requests are told apart.</summary>
public sealed class RolesApiTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_roles_" + Guid.NewGuid().ToString("N"));
    private HubTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await HubTestHost.StartAsync(_dir);
        _host.AuthorizeAs(ChopDb.OwnerParticipantId);   // row 28: every non-GET /api call here needs a credential
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private ParticipantStore Participants => _host.Services.GetRequiredService<ParticipantStore>();

    private async Task<JsonElement> GetRoles(HttpClient? client = null) =>
        await (client ?? _host.Client).GetFromJsonAsync<JsonElement>("api/rooms/general/roles");

    private static JsonElement Row(JsonElement roles, string id) =>
        roles.GetProperty("participants").EnumerateArray().Single(p => p.GetProperty("id").GetString() == id);

    private static string? Str(JsonElement e, string prop) => e.GetProperty(prop).GetString();

    private Task<HttpResponseMessage> SetPersona(string roomId, string? persona, HttpClient? client = null) =>
        (client ?? _host.Client).PostAsJsonAsync($"api/rooms/{roomId}/persona", new { persona });

    private Task<HttpResponseMessage> SetGlobalRole(string id, string? role, HttpClient? client = null) =>
        (client ?? _host.Client).PostAsJsonAsync($"api/participants/{id}/role", new { role });

    private Task<HttpResponseMessage> SetRoomRole(string roomId, string id, string role, HttpClient? client = null) =>
        (client ?? _host.Client).PostAsJsonAsync($"api/rooms/{roomId}/roles/{id}", new { role });

    /// <summary>Sends a body with the "role" property entirely absent, distinct from an explicit JSON
    /// null (which System.Text.Json deserializes identically to the missing case, but this exercises
    /// the literally-omitted wire shape a browser's "clear" button would send).</summary>
    private Task<HttpResponseMessage> ClearRoomRole(string roomId, string id, HttpClient? client = null) =>
        (client ?? _host.Client).PostAsync($"api/rooms/{roomId}/roles/{id}", new StringContent("{}", Encoding.UTF8, "application/json"));

    private HttpClient Anon() => new() { BaseAddress = _host.BaseAddress };

    [Fact]
    public async Task GET_lists_only_spawnable_participants()
    {
        var roles = await GetRoles();
        var ids = roles.GetProperty("participants").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();

        Assert.DoesNotContain("claude", ids);
        Assert.DoesNotContain("codex", ids);
        Assert.Contains("opus", ids);
    }

    [Fact]
    public async Task GET_reports_effectiveRole_in_all_three_states()
    {
        var neither = Row(await GetRoles(), "opus");
        Assert.Null(Str(neither, "role"));
        Assert.Null(Str(neither, "roomRole"));
        Assert.Null(Str(neither, "effectiveRole"));

        Assert.Equal(HttpStatusCode.OK, (await SetGlobalRole("opus", "Reviewer")).StatusCode);
        var globalOnly = Row(await GetRoles(), "opus");
        Assert.Equal("Reviewer", Str(globalOnly, "role"));
        Assert.Null(Str(globalOnly, "roomRole"));
        Assert.Equal("Reviewer", Str(globalOnly, "effectiveRole"));

        Assert.Equal(HttpStatusCode.OK, (await SetRoomRole("general", "opus", "Local reviewer")).StatusCode);
        var overridden = Row(await GetRoles(), "opus");
        Assert.Equal("Reviewer", Str(overridden, "role"));
        Assert.Equal("Local reviewer", Str(overridden, "roomRole"));
        Assert.Equal("Local reviewer", Str(overridden, "effectiveRole"));
        Assert.NotEqual(Str(overridden, "role"), Str(overridden, "effectiveRole"));
    }

    /// <summary>No existing route carries a participant id in its path, so a dotted id like
    /// <c>gpt-5.6-sol</c> routing through <c>/roles/{participantId}</c> is unproven without this.</summary>
    [Fact]
    public async Task Round_trip_for_a_participant_id_containing_dots()
    {
        Assert.Equal(HttpStatusCode.OK, (await SetGlobalRole("gpt-5.6-sol", "Solar reviewer")).StatusCode);
        Assert.Equal("Solar reviewer", Str(Row(await GetRoles(), "gpt-5.6-sol"), "role"));

        Assert.Equal(HttpStatusCode.OK, (await SetRoomRole("general", "gpt-5.6-sol", "Local solar reviewer")).StatusCode);
        var row = Row(await GetRoles(), "gpt-5.6-sol");
        Assert.Equal("Local solar reviewer", Str(row, "roomRole"));
        Assert.Equal("Local solar reviewer", Str(row, "effectiveRole"));
    }

    [Fact]
    public async Task A_read_taken_immediately_after_a_write_reflects_it()
    {
        Assert.Equal(HttpStatusCode.OK, (await SetGlobalRole("opus", "Fresh")).StatusCode);
        Assert.Equal("Fresh", Str(Row(await GetRoles(), "opus"), "role"));

        Assert.Equal(HttpStatusCode.OK, (await SetGlobalRole("opus", "Fresher")).StatusCode);
        Assert.Equal("Fresher", Str(Row(await GetRoles(), "opus"), "role"));
    }

    [Fact]
    public async Task Global_role_for_claude_is_refused_and_stores_nothing()
    {
        var r = await SetGlobalRole("claude", "Should not land");

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(Participants.GlobalRole("claude"));
    }

    [Fact]
    public async Task Global_role_for_owner_is_refused_with_400()
    {
        var r = await SetGlobalRole("owner", "Should not land");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Room_role_for_codex_is_400_a_non_spawnable_row()
    {
        var r = await SetRoomRole("general", "codex", "Should not land");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Global_role_for_an_unknown_participant_is_404()
    {
        var r = await SetGlobalRole("nope", "text");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Room_role_for_an_unknown_room_is_404_and_an_unknown_participant_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await SetRoomRole("nope", "opus", "text")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetRoomRole("general", "nope", "text")).StatusCode);
    }

    [Fact]
    public async Task Persona_for_an_unknown_room_is_404()
    {
        var r = await SetPersona("nope", "text");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task GET_roles_for_an_unknown_room_is_404()
    {
        var r = await _host.Client.GetAsync("api/rooms/nope/roles");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task A_2001_character_global_role_is_refused_and_stores_nothing()
    {
        var tooLong = new string('a', ParticipantStore.MaxRoleChars + 1);

        var r = await SetGlobalRole("opus", tooLong);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(Participants.GlobalRole("opus"));
    }

    [Fact]
    public async Task A_2001_character_room_override_is_refused_and_stores_nothing()
    {
        var tooLong = new string('a', ParticipantStore.MaxRoleChars + 1);

        var r = await SetRoomRole("general", "opus", tooLong);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(Participants.RoomRole("general", "opus"));
    }

    [Fact]
    public async Task A_2001_character_persona_is_refused_and_stores_nothing()
    {
        var tooLong = new string('a', MessageStore.MaxPersonaChars + 1);

        var r = await SetPersona("general", tooLong);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(_host.Services.GetRequiredService<MessageStore>().GetRoom("general")!.Persona);
    }

    [Fact]
    public async Task Clearing_an_override_that_does_not_exist_succeeds()
    {
        var r = await ClearRoomRole("general", "opus");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    /// <summary>D-b's fourth state: a stored empty string is not the global role, and a cleared
    /// override falls all the way back to it. The two requests differ only in whether "role" is
    /// present in the body, so this is the test a blank-means-delete implementation fails.</summary>
    [Fact]
    public async Task Suppress_sentinel_and_clear_are_different_requests_with_different_outcomes()
    {
        await SetGlobalRole("opus", "Global reviewer");

        var suppressed = await SetRoomRole("general", "opus", "");
        Assert.Equal(HttpStatusCode.OK, suppressed.StatusCode);
        var afterSuppress = Row(await GetRoles(), "opus");
        Assert.Equal("", Str(afterSuppress, "roomRole"));
        Assert.Equal("", Str(afterSuppress, "effectiveRole"));

        var cleared = await ClearRoomRole("general", "opus");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var afterClear = Row(await GetRoles(), "opus");
        Assert.Null(Str(afterClear, "roomRole"));
        Assert.Equal("Global reviewer", Str(afterClear, "effectiveRole"));
    }

    [Fact]
    public async Task Global_role_can_be_set_and_then_cleared()
    {
        Assert.Equal(HttpStatusCode.OK, (await SetGlobalRole("opus", "Reviewer")).StatusCode);
        Assert.Equal("Reviewer", Participants.GlobalRole("opus"));

        Assert.Equal(HttpStatusCode.OK, (await SetGlobalRole("opus", null)).StatusCode);
        Assert.Null(Participants.GlobalRole("opus"));
    }

    [Fact]
    public async Task Persona_can_be_set_and_then_cleared()
    {
        Assert.Equal(HttpStatusCode.OK, (await SetPersona("general", "A persona.")).StatusCode);
        Assert.Equal("A persona.", _host.Services.GetRequiredService<MessageStore>().GetRoom("general")!.Persona);

        Assert.Equal(HttpStatusCode.OK, (await SetPersona("general", null)).StatusCode);
        Assert.Null(_host.Services.GetRequiredService<MessageStore>().GetRoom("general")!.Persona);
    }

    [Fact]
    public async Task Persona_round_trips_and_is_reflected_in_the_room_listing()
    {
        Assert.Equal(HttpStatusCode.OK, (await SetPersona("general", "A room for reviewing PRs.")).StatusCode);

        var roles = await GetRoles();
        Assert.Equal("A room for reviewing PRs.", roles.GetProperty("persona").GetString());

        var rooms = await _host.Client.GetFromJsonAsync<JsonElement>("api/rooms");
        var general = rooms.EnumerateArray().Single(r => r.GetProperty("id").GetString() == "general");
        Assert.Equal("A room for reviewing PRs.", general.GetProperty("persona").GetString());
    }

    // -- Unauthenticated: refused AND the stored value is unchanged afterwards (AC8), not merely the status code. --

    [Fact]
    public async Task Persona_write_with_no_credential_is_401_and_the_stored_value_is_unchanged()
    {
        using var anon = Anon();
        var r = await SetPersona("general", "Should not land", anon);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Null(_host.Services.GetRequiredService<MessageStore>().GetRoom("general")!.Persona);
    }

    [Fact]
    public async Task Global_role_write_with_no_credential_is_401_and_the_stored_value_is_unchanged()
    {
        using var anon = Anon();
        var r = await SetGlobalRole("opus", "Should not land", anon);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Null(Participants.GlobalRole("opus"));
    }

    [Fact]
    public async Task Room_role_write_with_no_credential_is_401_and_the_stored_value_is_unchanged()
    {
        using var anon = Anon();
        var r = await SetRoomRole("general", "opus", "Should not land", anon);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Null(Participants.RoomRole("general", "opus"));
    }

    [Fact]
    public async Task GET_roles_still_works_with_no_credential()
    {
        using var anon = Anon();
        var r = await anon.GetAsync("api/rooms/general/roles");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}
