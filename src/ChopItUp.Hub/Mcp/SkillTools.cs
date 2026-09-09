using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Security;
using ChopItUp.Hub.Skills;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ChopItUp.Hub.Mcp;

/// <summary>M25 task 5: the propose half of the skill-import contract — <c>propose_memory</c>'s shape
/// (D1, D4, D6) repeated over <c>SkillImport</c>'s write path instead of <c>MemoryStore</c>'s. This
/// tool records a proposal and announces it; it never calls <see cref="SkillImport.Run"/> and writes
/// nothing under the skill store — the owner approves in the room (task 7, not this tool).
///
/// D4's confinement: <c>source_dir</c> must resolve inside the calling room's own bound directory
/// (<see cref="ChopItUp.Core.Model.Room.Directory"/>), checked with <see cref="RoomPaths"/>'s own
/// normalisation and containment test before anything on disk is even looked at — so an
/// out-of-room path is refused without the refusal ever disclosing whether that path exists (grill
/// D10: no reads outside the room). The root/ancestor link check inside a confined path is
/// <see cref="SkillImport.Validate"/>'s own refusal 1b, not duplicated here.
///
/// D6: this tool never passes an overlay to <see cref="SkillImport.Validate"/>, so both halves of D6
/// fall out of <c>Validate</c>'s existing refusals for free — a source carrying its own
/// <c>OVERLAY.md</c> (refusal 7b) and a forced re-import that would silently drop an installed
/// overlay (refusal 8b) are both refused there, with no new refusal logic needed here.</summary>
[McpServerToolType]
public sealed class SkillTools(SkillProposalStore proposals, SkillStore skills, MessageStore store, MessageSignal signal, IHttpContextAccessor http)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string Caller =>
        http.HttpContext?.Items[BearerTokenMiddleware.ParticipantKey] as string
        ?? throw new McpException("Unauthenticated request reached a tool; this is a hub bug.");

    [McpServerTool(Name = "propose_skill", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Offer a skill folder, from inside this room's own directory, for the owner to review and install into the hub's skill store. The hub validates it the same way --import-skill would, records you as the proposer, and announces it in the room; nothing is written under the skill store until the owner approves. Offering the same skill and tree twice returns the existing proposal instead of a new one.")]
    public string ProposeSkill(
        [Description("Room id the proposal belongs to, e.g. \"general\".")] string room_id,
        [Description("Absolute path to the skill folder, INSIDE this room's own bound directory, holding SKILL.md directly.")] string source_dir,
        [Description("Propose replacing an already-installed skill of the same name; the owner still approves before anything changes.")] bool force = false)
    {
        var me = Caller;
        var room = store.GetRoom(room_id) ?? throw new McpException($"Unknown room '{room_id}'. Call list_rooms.");
        var sourceFull = ConfineToRoom(source_dir, room_id, room.Directory);

        var validation = SkillImport.Validate(sourceFull, skills.Root, force);
        if (validation.Outcome != SkillImportOutcome.Ok)
            throw new McpException(validation.Message);

        var name = validation.Name!;
        var treeSha = SkillImport.ManifestDigest(SkillImport.HashSourceTree(sourceFull));

        // AC3's dedup: a repeat offer of the same tree returns the existing card rather than minting a
        // second one — no new row, no new note (the MemoryTools stance).
        if (proposals.FindPending(name, treeSha) is { } pending)
            return JsonSerializer.Serialize(new
            {
                pending.Id, pending.RoomId, pending.AuthorId, pending.Name, pending.SourceDir, pending.TreeSha256,
                pending.ReplacesInstalled, pending.Force, pending.Files, pending.Bytes, pending.Status,
                Duplicate = true,
            }, JsonOptions);

        SkillProposal proposal;
        try { proposal = proposals.Add(room_id, me, name, sourceFull, treeSha, validation.ReplacesInstalled, force, validation.Files, validation.Bytes); }
        catch (ArgumentException e) { throw new McpException(e.Message); }

        // The row is the proposal; the note is its announcement. A note that fails must not turn into a
        // tool error that invites a retry and a duplicate row (the same stance propose_memory takes).
        try { HubNotes.Post(store, signal, room_id, NoteText(proposal)); }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"skills: proposal #{proposal.Id} note not posted ({e.GetType().Name}: {e.Message})"); }

        return JsonSerializer.Serialize(new
        {
            proposal.Id, proposal.RoomId, proposal.AuthorId, proposal.Name, proposal.SourceDir, proposal.TreeSha256,
            proposal.ReplacesInstalled, proposal.Force, proposal.Files, proposal.Bytes, proposal.Status,
        }, JsonOptions);
    }

    private static string NoteText(SkillProposal p) =>
        $"Skill proposal #{p.Id} by {p.AuthorId}: '{p.Name}' ({p.Files} file{(p.Files == 1 ? "" : "s")}"
        + $"{(p.ReplacesInstalled ? ", replaces installed" : "")}). Approve or reject it in the skills panel.";

    /// <summary>D4: <paramref name="typed"/> must resolve inside <paramref name="roomDirectory"/> — the
    /// room's own bound directory — checked with <see cref="RoomPaths"/>'s own shape guards and
    /// normalisation (the same early checks <see cref="RoomPaths.Refusal"/> runs), so an out-of-room
    /// path is refused on its TEXT alone, before <c>Validate</c> or anything else ever asks the
    /// filesystem whether it exists (ticket 05: "the refusal does not reveal whether that path
    /// exists").</summary>
    private static string ConfineToRoom(string? typed, string roomId, string? roomDirectory)
    {
        if (roomDirectory is null)
            throw new McpException($"Room '{roomId}' has no bound directory; propose_skill needs one to confine the source to.");
        var t = (typed ?? "").Trim();
        if (t.Length == 0) throw new McpException("source_dir is empty.");
        if (t.StartsWith(@"\\", StringComparison.Ordinal) || t.StartsWith("//", StringComparison.Ordinal))
            throw new McpException(@"Network and device paths (\\server\share, \\?\...) cannot be a skill source.");
        if (t.Length < 3 || !char.IsAsciiLetter(t[0]) || t[1] != ':' || (t[2] != '\\' && t[2] != '/'))
            throw new McpException(@"source_dir must be an absolute local path such as C:\Projects\thing.");
        string full;
        try { full = RoomPaths.Normalize(t); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            throw new McpException("source_dir is not a valid Windows path.");
        }
        var roomFull = RoomPaths.Normalize(roomDirectory);
        if (!RoomPaths.IsUnderOrEqual(full, roomFull))
            throw new McpException($"'{full}' is outside the directory of room '{roomId}' ('{roomFull}'); propose_skill can only offer a skill from inside the room.");
        return full;
    }
}
