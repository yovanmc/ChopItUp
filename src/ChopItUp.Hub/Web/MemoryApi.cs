using ChopItUp.Core.Memory;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Web;

/// <summary>The owner's side of memory (D15: agents propose, the owner approves). No auth, like the
/// rest of <c>/api</c>: loopback is the boundary — and because a Codex spawn lives inside that boundary
/// with a shell (F3), decisions are refused while any spawn is in flight (plan decision 13). Approve =
/// mark + append + record + note, in that order (plan decision 15): the row is the arbiter, the file
/// write is idempotent on the proposal's key, the note is best-effort.</summary>
public static class MemoryApi
{
    // One decision at a time: two clicks on the same card must not race the mark-then-write sequence.
    private static readonly SemaphoreSlim Decisions = new(1, 1);
    public const string SpawnRunning = "A spawn is running; decide memory proposals when the exchange has finished.";

    public static void MapMemoryApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/memory");
        api.MapGet("/proposals", ListProposals);
        api.MapPost("/proposals/{id:long}/approve", Approve);
        api.MapPost("/proposals/{id:long}/reject", Reject);
        api.MapPost("/import", Import);
        api.MapDelete("/proposals", Discard);
    }

    private static IResult ListProposals(MemoryProposalStore proposals, string? room = null, string? status = MemoryProposalStore.Undecided)
    {
        if (status is "all") status = null;
        if (status is not (null or MemoryProposalStore.Undecided or MemoryProposalStore.Pending or MemoryProposalStore.Approved or MemoryProposalStore.Rejected))
            return Results.BadRequest(new { error = "status must be undecided, pending, approved, rejected or all." });
        return Results.Json(proposals.List(string.IsNullOrWhiteSpace(room) ? null : room, status).Select(Map));
    }

    private static async Task<IResult> Approve(long id, MemoryProposalStore proposals, MemoryStore memory, MemoryGit git, MessageStore store, MessageSignal signal, SpawnerService spawner)
    {
        await Decisions.WaitAsync();
        try
        {
            if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
            var p = proposals.Get(id);
            if (p is null) return Results.NotFound(new { error = $"No memory proposal #{id}." });
            if (p.Status == MemoryProposalStore.Rejected || (p.Status == MemoryProposalStore.Approved && p.WrittenTo is not null))
                return Results.Conflict(new { error = $"Memory proposal #{id} is already {p.Status}." });
            // Mark first. An approved row with no written_to is the replayable state a crash below leaves.
            if (p.Status == MemoryProposalStore.Pending && proposals.Decide(id, MemoryProposalStore.Approved, null, null) is null)
                return Results.Conflict(new { error = $"Memory proposal #{id} was decided concurrently." });
            var written = memory.Append(p.Topic, p.Title, p.Body,
                $"approved {Timestamps.Stamp(DateTimeOffset.UtcNow)} proposal {p.Id} by {p.AuthorId} in room {p.RoomId}",
                dedupKey: $"proposal {p.Id} by {p.AuthorId}");
            var hash = await git.CommitAsync($"Approve memory proposal #{p.Id} ({p.Topic}): {p.Title}");
            var decided = proposals.RecordWrite(id, written, hash) ?? proposals.Get(id)!;
            Note(store, signal, decided.RoomId, HubNotes.Approved(decided));
            return Results.Json(Map(decided));
        }
        finally { Decisions.Release(); }
    }

    private static async Task<IResult> Reject(long id, MemoryProposalStore proposals, MessageStore store, MessageSignal signal, SpawnerService spawner)
    {
        await Decisions.WaitAsync();
        try
        {
            if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
            var p = proposals.Get(id);
            if (p is null) return Results.NotFound(new { error = $"No memory proposal #{id}." });
            if (p.Status != MemoryProposalStore.Pending) return Results.Conflict(new { error = $"Memory proposal #{id} is already {p.Status}." });
            var decided = proposals.Decide(id, MemoryProposalStore.Rejected, null, null)!;
            Note(store, signal, decided.RoomId, HubNotes.Rejected(decided));
            return Results.Json(Map(decided));
        }
        finally { Decisions.Release(); }
    }

    /// <summary>Drafts become proposals authored as the vendor's app-backed roster row, source
    /// <c>&lt;vendor&gt;:&lt;path&gt;</c>; a draft already proposed by that author (pending or approved) is
    /// skipped, so re-importing is safe; a draft that fails validation is skipped, never fatal. A folder
    /// over <see cref="MemoryImport.MaxDrafts"/> is refused whole. One summary note, never one per draft
    /// (plan decisions 7, 16).</summary>
    private static IResult Import(ImportBody body, MemoryProposalStore proposals, ParticipantStore participants, MessageStore store, MessageSignal signal, SpawnerService spawner)
    {
        if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });   // a spawn could import its own file under a vendor's name (plan decision 17)
        if (string.IsNullOrWhiteSpace(body.RoomId) || !store.RoomExists(body.RoomId)) return Results.NotFound(new { error = $"Unknown room '{body.RoomId}'." });
        var source = (body.Source ?? "").Trim().ToLowerInvariant();
        IReadOnlyList<MemoryDraft> drafts;
        try { drafts = MemoryImport.Read(source, body.Path ?? ""); }
        catch (Exception e) when (e is ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return Results.BadRequest(new { error = e.Message });
        }
        if (drafts.Count > MemoryImport.MaxDrafts)
            return Results.BadRequest(new { error = $"'{body.Path}' yields {drafts.Count} proposals; the cap is {MemoryImport.MaxDrafts}. Point the import at a smaller folder." });
        var author = participants.List().FirstOrDefault(p => p.Kind == "model" && p.Host == source && p.Model is null)?.Id;
        if (author is null) return Results.BadRequest(new { error = $"No app-backed roster row for host '{source}'." });

        int imported = 0, skipped = 0;
        var added = new List<MemoryProposal>();
        var origin = $"{source}:{body.Path}";
        foreach (var d in drafts)
        {
            if (proposals.Exists(author, d.Topic, d.Title)) { skipped++; continue; }
            try { added.Add(proposals.Create(body.RoomId, author, d.Topic, d.Title, d.Body, origin)); imported++; }
            catch (ArgumentException) { skipped++; }
        }
        Note(store, signal, body.RoomId, HubNotes.Imported(source, body.Path!, imported, skipped));
        return Results.Json(new { imported, skipped, proposals = added.Select(Map) }, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>Undo for a mis-targeted import: every PENDING proposal with that source goes. Approved
    /// entries are memory now and stay; rejected ones are already inert.</summary>
    private static IResult Discard(MemoryProposalStore proposals, SpawnerService spawner, string? source, string? path)
    {
        if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
        var s = (source ?? "").Trim().ToLowerInvariant();
        if (!MemoryImport.Sources.Contains(s, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { error = "source (claude or codex) and path are required." });
        return Results.Json(new { discarded = proposals.DeletePending($"{s}:{path}") });
    }

    /// <summary>A note is the trail, not the mechanism: the decision stands even when the note fails
    /// (same stance as <c>SpawnerService.PostNote</c>).</summary>
    private static void Note(MessageStore store, MessageSignal signal, string roomId, string text)
    {
        try { HubNotes.Post(store, signal, roomId, text); }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"memory: note to '{roomId}' not posted ({e.GetType().Name}: {e.Message}): {text.Split('\n')[0]}"); }
    }

    private static object Map(MemoryProposal p) => new
    {
        p.Id, p.RoomId, p.AuthorId, p.Topic, p.Title, p.Body, p.Status, p.Source, p.CreatedAt, p.DecidedAt, p.WrittenTo, p.CommitHash,
    };

    internal sealed record ImportBody(string? Source, string? Path, string? RoomId);
}
