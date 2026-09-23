using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Web;

/// <summary>Read and edit a room's persona, a participant's global role and a room's override of it.
/// Same guard as <see cref="ChatApi"/> and <see cref="RoomsApi"/>: every non-GET route here needs an
/// owner-class bearer (<c>BearerTokenMiddleware</c> guards by method, not by route, so nothing
/// is added here for that). Two traps this file exists to not fall into:
/// (1) every handler reads the roster via <see cref="ParticipantStore.List"/>, the live per-call
/// read, never the startup-static <c>IReadOnlyList&lt;Participant&gt;</c> singleton
/// <c>HubHost</c> registers for identity/peers: binding that singleton here would serve
/// startup-time role text and break "a POST is reflected in the next GET";
/// (2) the store's writers return <c>bool</c>, which cannot tell "unknown participant" (404) apart
/// from "not spawnable" (400), so every handler classifies from the roster BEFORE calling the store:
/// absent → 404, <see cref="ExchangePolicy.IsSpawnable"/> false → 400, otherwise call the store (whose
/// own WHERE clause stays as defence in depth, not as the classifier).</summary>
public static class RolesApi
{
    public static void MapRolesApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/rooms/{roomId}/roles", GetRoomRoles);
        api.MapPost("/rooms/{roomId}/persona", SetRoomPersona);
        api.MapPost("/participants/{id}/role", SetGlobalRole);
        api.MapPost("/rooms/{roomId}/roles/{participantId}", SetRoomRole);
    }

    private static IResult GetRoomRoles(string roomId, MessageStore store, ParticipantStore participants)
    {
        if (store.GetRoom(roomId) is not { } room) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        return Results.Json(BuildRoomRoles(roomId, room.Persona, participants));
    }

    private static IResult SetRoomPersona(string roomId, PersonaBody body, MessageStore store, ParticipantStore participants)
    {
        if (store.GetRoom(roomId) is null) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        try { store.SetPersona(roomId, body.Persona); }
        catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        return Results.Json(BuildRoomRoles(roomId, store.GetRoom(roomId)!.Persona, participants));
    }

    private static IResult SetGlobalRole(string id, RoleBody body, ParticipantStore participants)
    {
        var row = participants.List().FirstOrDefault(p => p.Id == id);
        if (row is null) return Results.NotFound(new { error = $"Unknown participant '{id}'." });
        if (!ExchangePolicy.IsSpawnable(row)) return Results.BadRequest(new { error = $"'{id}' is never spawned; a role can never render for it." });
        try { participants.SetRole(id, body.Role); }
        catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        var updated = participants.List().First(p => p.Id == id);
        return Results.Json(new { updated.Id, updated.DisplayName, role = updated.Role });
    }

    /// <summary>The room override. Two operations, told apart by the request body itself:
    /// <paramref name="body"/>'s <c>Role</c> absent or JSON <c>null</c> deserialize identically to a
    /// C# <c>null</c> and mean "clear the override" (<see cref="ParticipantStore.ClearRoomRole"/>,
    /// falling back to the global role); any other value, including the empty string, means "store
    /// this" (<see cref="ParticipantStore.SetRoomRole"/>): <c>""</c> is the stored sentinel for "no
    /// role in this room", never a delete. A DTO that defaulted a missing <c>role</c> to <c>""</c>
    /// would collapse both into one operation; <see cref="RoleBody"/> is a nullable reference for
    /// exactly this reason.</summary>
    private static IResult SetRoomRole(string roomId, string participantId, RoleBody body, MessageStore store, ParticipantStore participants)
    {
        if (store.GetRoom(roomId) is not { } room) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        var row = participants.List().FirstOrDefault(p => p.Id == participantId);
        if (row is null) return Results.NotFound(new { error = $"Unknown participant '{participantId}'." });
        if (!ExchangePolicy.IsSpawnable(row)) return Results.BadRequest(new { error = $"'{participantId}' is never spawned; a role can never render for it." });
        if (body.Role is null)
            participants.ClearRoomRole(roomId, participantId);
        else
        {
            try { participants.SetRoomRole(roomId, participantId, body.Role); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        }
        return Results.Json(BuildRoomRoles(roomId, room.Persona, participants));
    }

    /// <summary>The shape every read and every write here returns, built from the live roster
    /// (<see cref="ParticipantStore.List"/>), never the startup-static singleton, restricted to
    /// <see cref="ExchangePolicy.IsSpawnable"/> rows: <c>claude</c> and <c>codex</c> are kind 'model'
    /// with a NULL model and are never spawned, so listing them here would show role text that can
    /// never render. Each row also carries what the roster says about it, read-only: the
    /// <c>model</c> its host is launched with (never null on a spawnable row), its normalised
    /// <c>classes</c> (<see cref="ParticipantClasses.Parse"/>, so a mistyped token the dispatcher
    /// would drop is not shown as though it applied) and the <c>effort</c> those classes earn inside a
    /// run (<see cref="EffortPolicy.ForClasses"/>: <c>high</c> for a judge, null for "no flag, the
    /// CLI's default"). <c>conductorEffort</c> is the run conductor's, whatever its classes, so the
    /// dialog can say so without a literal of its own. Nothing here is a resolved runtime value: the
    /// dispatcher snapshots the roster at start, and <c>--set-classes</c> refuses to run under a live
    /// hub, so the live read and the snapshot agree unless the database was edited by hand.</summary>
    private static object BuildRoomRoles(string roomId, string? persona, ParticipantStore participants) => new
    {
        roomId,
        persona,
        conductorEffort = EffortPolicy.Raised,
        participants = participants.List().Where(ExchangePolicy.IsSpawnable).Select(p => new
        {
            p.Id,
            p.DisplayName,
            role = p.Role,
            roomRole = participants.RoomRole(roomId, p.Id),
            effectiveRole = participants.EffectiveRole(roomId, p.Id),
            model = p.Model,
            classes = ParticipantClasses.Parse(p.Classes),
            effort = EffortPolicy.ForClasses(p),
        }),
    };

    internal sealed record PersonaBody(string? Persona);
    internal sealed record RoleBody(string? Role);
}
