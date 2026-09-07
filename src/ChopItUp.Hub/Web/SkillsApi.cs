using ChopItUp.Hub.Skills;

namespace ChopItUp.Hub.Web;

/// <summary>What the store can tell the outside world about itself: <c>GET /api/skills</c>, mirroring
/// <see cref="ChatApi"/>'s shape — a <c>MapGroup("/api")</c>, no auth (loopback is the boundary, per
/// <see cref="ChatApi"/>'s doc comment). The composer's slash menu (task 7) fetches this on mount.
///
/// Projects exactly the four fields of <see cref="SkillSummary"/> and no others (critique pass 2,
/// M-5: the first draft had four different shapes across three tasks, including a <c>HasOverlay</c>
/// no record defined). An empty store answers an empty list, and so does one whose only skill fails
/// its fingerprint check — <see cref="SkillStore.List"/> already skips both cases, so this endpoint
/// needs no second filter.</summary>
public static class SkillsApi
{
    public static void MapSkillsApi(this WebApplication app) =>
        app.MapGroup("/api").MapGet("/skills", (SkillStore skills) =>
            Results.Json(skills.List().Select(s => new { s.Name, s.Title, s.Description, s.Chars })));
}
