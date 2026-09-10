using System.Text.RegularExpressions;
using ChopItUp.Core.Memory;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Web;

/// <summary>The owner's side of memory (D15: agents propose, the owner approves). Every non-GET
/// route here needs an owner-class bearer since row 28 (<c>BearerTokenMiddleware</c>), superseding
/// the old no-auth loopback boundary — and because a Codex spawn lives inside that boundary with a
/// shell (F3), decisions are refused while any spawn is in flight (plan decision 13). Approve =
/// mark + append + record + note, in that order (plan decision 15): the row is the arbiter, the file
/// write is idempotent on the proposal's key, the note is best-effort.</summary>
public static class MemoryApi
{
    // One decision at a time: two clicks on the same card must not race the mark-then-write sequence.
    private static readonly SemaphoreSlim Decisions = new(1, 1);
    public const string SpawnRunning = "A spawn is running; decide memory proposals when the exchange has finished.";
    // Row 18, decision 3: the refusal note is posted once per proposal per hub process, never per click
    // (keyed per store too, since the test process hosts many hubs whose ids all start at 1 - pass 2 P2-6).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Root, long Id), byte> RefusalNoted = new();

    public static void MapMemoryApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/memory");
        api.MapGet("/proposals", ListProposals);
        api.MapPost("/proposals/{id:long}/approve", Approve);
        api.MapPost("/proposals/{id:long}/reject", Reject);
        api.MapPost("/import", Import);
        api.MapDelete("/proposals", Discard);
    }

    private static IResult ListProposals(MemoryProposalStore proposals, MemoryStore memory, MemoryGit git, string? room = null, string? status = MemoryProposalStore.Undecided)
    {
        if (status is "all") status = null;
        if (status is not (null or MemoryProposalStore.Undecided or MemoryProposalStore.Pending or MemoryProposalStore.Approved or MemoryProposalStore.Rejected))
            return Results.BadRequest(new { error = "status must be undecided, pending, approved, rejected or all." });
        return Results.Json(proposals.List(string.IsNullOrWhiteSpace(room) ? null : room, status).Select(p => MapForList(p, memory, git)));
    }

    /// <summary>Row 23 (item 5, ticket 05): a <c>rewrite</c> proposal that is pending or approved-but-
    /// unwritten (the panel's default <c>undecided</c> filter shows both — the second is the Retry state,
    /// pass 2 finding H) carries a computed line diff against what approval would write, the entry titles
    /// it removes and adds, how many live entries would lose their provenance (renamed or removed alike —
    /// <see cref="MemoryStore.ProvenanceLost"/> matches by exact heading), and whether a commit
    /// can be made at all — all before the owner can approve it. Every other row gets the base shape with
    /// these fields present but empty/null (ticket 05: "always present", never missing), so the client
    /// never has to guess whether a field applies to a given kind. Cost is accepted, not optimised: one
    /// bounded <see cref="MemoryDiff"/> LCS per undecided rewrite per list call, for one local client.</summary>
    private static object MapForList(MemoryProposal p, MemoryStore memory, MemoryGit git)
    {
        var isRewrite = p.Kind == MemoryProposalStore.KindRewrite;
        var inScope = isRewrite && (p.Status == MemoryProposalStore.Pending || (p.Status == MemoryProposalStore.Approved && p.WrittenTo is null));

        IReadOnlyList<RelatedEntry> related = !isRewrite && p.Status == MemoryProposalStore.Pending ? memory.Related(p.Topic, p.Title, p.Replaces) : [];
        IReadOnlyList<object>? diff = null;
        IReadOnlyList<string> removedTitles = [];
        IReadOnlyList<string> addedTitles = [];
        var provenanceLost = 0;
        bool? gitAvailable = null;

        if (inScope)
        {
            var before = memory.ReadTopic(p.Topic, int.MaxValue)?.Text ?? "";
            var after = MemoryStore.PreviewRewrite(memory, p.Topic, p.Body);
            diff = MemoryDiff.Hunks(MemoryDiff.Compute(before, after)).Select(l => (object)new { op = l.Op.ToString().ToLowerInvariant(), text = l.Text }).ToList();
            var beforeTitles = memory.Titles(p.Topic);
            var afterTitles = HeadingTitles(after);
            removedTitles = beforeTitles.Except(afterTitles, StringComparer.Ordinal).ToList();
            addedTitles = afterTitles.Except(beforeTitles, StringComparer.Ordinal).ToList();
            provenanceLost = memory.ProvenanceLost(p.Topic, p.Body).Count;
            gitAvailable = git.IsAvailable();
        }

        return new
        {
            p.Id, p.RoomId, p.AuthorId, p.Topic, p.Title, p.Body, p.Status, p.Source, p.CreatedAt, p.DecidedAt, p.WrittenTo, p.CommitHash,
            p.Kind, p.Replaces, Flags = ProposalFlags.Parse(p.Flags), Related = related,
            Diff = diff, RemovedTitles = removedTitles, AddedTitles = addedTitles, ProvenanceLost = provenanceLost, GitAvailable = gitAvailable,
        };
    }

    /// <summary>The <c>## </c> heading titles of a composed (not-yet-written) rewrite body, in file order
    /// — mirrors <see cref="MemoryStore.ValidateRewrite"/>'s own heading extraction, but Core's parser
    /// (<c>ParseEntries</c>) is internal to Core and unreachable from the Hub (claim 15), and there is no
    /// on-disk file to call <see cref="MemoryStore.Titles"/> against for text that only exists as a
    /// preview.</summary>
    private static IReadOnlyList<string> HeadingTitles(string text) =>
        Regex.Matches(text.Replace("\r\n", "\n"), "(?m)^## (.*)$").Select(m => m.Groups[1].Value.Trim()).ToList();

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

            // Row 18, decision 3: refuse BEFORE marking, so a refused row stays pending rather than becoming
            // the replayable approved-but-unwritten state. A row that is ALREADY approved (a Retry after a crash
            // between mark and write) skips the check: it was committed to when it passed, and Retry must be able
            // to finish it (critique P1-5).
            var provenance = $"approved {Timestamps.Stamp(DateTimeOffset.UtcNow)} proposal {p.Id} by {p.AuthorId} in room {p.RoomId}";

            // Row 23, pass 2 finding H: a rewrite's pre-write checks run on BOTH the pending path AND the
            // Retry path (approved, written_to still null) - unlike the append/supersede checks below, which
            // stay pending-only under P1-5's ruling. Rewrite() itself throws KeyNotFoundException when the
            // topic vanished in the crash window, and unlike append/supersede that must never reach the
            // write uncaught: checking here turns it into a 409, not a 500.
            if (p.Kind == MemoryProposalStore.KindRewrite)
            {
                try { MemoryStore.ValidateRewrite(p.Topic, p.Body); }
                catch (ArgumentException e) { return Results.Conflict(new { error = $"Memory proposal #{p.Id} cannot be written: {e.Message} Reject it and propose it again." }); }
                if (memory.ReadTopic(p.Topic) is null)
                    return Results.Conflict(new { error = $"No topic '{p.Topic}' to rewrite." });
                var cap = p.Topic == MemoryStore.CoreTopic ? MemoryStore.CoreChars : MemoryStore.TopicChars;
                int chars;
                try { chars = memory.ProjectedRewriteChars(p.Topic, p.Body, provenance); }
                catch (KeyNotFoundException e) { return Results.Conflict(new { error = e.Message }); }
                if (chars > cap)
                {
                    var current = memory.ReadTopic(p.Topic)!.FullChars;
                    var refused = HubNotes.Refused(p, chars, current);   // the banner and the room note read the same text
                    if (RefusalNoted.TryAdd((memory.Root, p.Id), 0)) Note(store, signal, p.RoomId, refused);   // once per proposal per store per process, never per click
                    return Results.Conflict(new { error = refused, chars, current, cap });
                }
            }
            else if (p.Status == MemoryProposalStore.Pending)
            {
                // Pass 2 P2-3: a row that predates row 18's body rule (a "## " line) must be refused HERE, before
                // the mark - after it, Append/Supersede would throw on every Retry and the row could never be
                // rejected. The same check covers any future Validate rule.
                try { MemoryStore.Validate(p.Title, p.Body); }
                catch (ArgumentException e) { return Results.Conflict(new { error = $"Memory proposal #{p.Id} cannot be written: {e.Message} Reject it and propose it again." }); }
                if (p.Topic == MemoryStore.CoreTopic)
                {
                    int chars;
                    try { chars = memory.ProjectedCoreChars(p.Replaces, p.Title, p.Body, provenance); }
                    catch (KeyNotFoundException e) { return Results.Conflict(new { error = e.Message }); }
                    if (chars > MemoryStore.CoreChars)
                    {
                        var current = memory.ReadTopic(MemoryStore.CoreTopic)!.FullChars;
                        var refused = HubNotes.Refused(p, chars, current);   // the banner and the room note read the same text
                        if (RefusalNoted.TryAdd((memory.Root, p.Id), 0)) Note(store, signal, p.RoomId, refused);   // once per proposal per store per process, never per click
                        return Results.Conflict(new { error = refused, chars, current, cap = MemoryStore.CoreChars });
                    }
                }
                else if (p.Replaces is not null && !memory.Titles(p.Topic).Contains(p.Replaces, StringComparer.Ordinal))
                    return Results.Conflict(new { error = $"No entry titled '{p.Replaces}' to replace." });
            }

            // Row 23 (item 4): captured before the mark/write so the approval note can name what a
            // rewrite removed - the set difference of live titles before and after the replacement.
            var beforeTitles = p.Kind == MemoryProposalStore.KindRewrite ? memory.Titles(p.Topic) : null;

            // Mark first. An approved row with no written_to is the replayable state a crash below leaves.
            if (p.Status == MemoryProposalStore.Pending && proposals.Decide(id, MemoryProposalStore.Approved, null, null) is null)
                return Results.Conflict(new { error = $"Memory proposal #{id} was decided concurrently." });
            // A Supersede here can still throw KeyNotFoundException if the file changed between the check
            // above and this write - only the owner's editor can do that (same process, same semaphore, no
            // spawn in flight). Let it surface as a 500 with the row approved-but-unwritten, which the
            // panel's Retry then re-checks.
            var dedupKey = $"proposal {p.Id} by {p.AuthorId}";
            var written = p.Kind switch
            {
                MemoryProposalStore.KindRewrite => memory.Rewrite(p.Topic, p.Body, provenance, p.Id, dedupKey),
                _ when p.Replaces is null => memory.Append(p.Topic, p.Title, p.Body, provenance, dedupKey),
                _ => memory.Supersede(p.Topic, p.Replaces, p.Title, p.Body, provenance, dedupKey),
            };
            var removedTitles = beforeTitles is null ? null : beforeTitles.Except(memory.Titles(p.Topic), StringComparer.Ordinal).ToList();
            var hash = await git.CommitAsync($"Approve memory proposal #{p.Id} ({p.Topic}): {p.Title}");
            var decided = proposals.RecordWrite(id, written, hash) ?? proposals.Get(id)!;
            Note(store, signal, decided.RoomId, HubNotes.Approved(decided, removedTitles));
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
            try { added.Add(proposals.Create(body.RoomId, author, d.Topic, d.Title, d.Body, origin, flags: ProposalFlags.Compute(d.Body, fromDirectory: false))); imported++; }
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

    private static object Map(MemoryProposal p) => Map(p, null);

    private static object Map(MemoryProposal p, IReadOnlyList<RelatedEntry>? related) => new
    {
        p.Id, p.RoomId, p.AuthorId, p.Topic, p.Title, p.Body, p.Status, p.Source, p.CreatedAt, p.DecidedAt, p.WrittenTo, p.CommitHash,
        p.Kind, p.Replaces, Flags = ProposalFlags.Parse(p.Flags), Related = related ?? [],
    };

    internal sealed record ImportBody(string? Source, string? Path, string? RoomId);
}
