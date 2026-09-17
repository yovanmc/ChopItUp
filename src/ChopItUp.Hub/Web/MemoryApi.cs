using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ChopItUp.Core.Memory;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Security;
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
        api.MapGet("/topics", ListTopics);
        api.MapGet("/topics/{slug}", GetTopic);
        api.MapPost("/topics/{slug}/preview", PreviewTopic);
        api.MapPut("/topics/{slug}", PutTopic);
    }

    public const string StaleEdit = "The file changed since you opened it. Reload it and apply your edit again.";
    private const string BadSlug = "topic must be a slug: lowercase letters, digits and hyphens.";

    private static string PathOf(string slug) => slug == MemoryStore.CoreTopic ? MemoryStore.CoreFileName : $"{MemoryStore.TopicsDirName}/{slug}.md";
    private static int CapOf(string slug) => slug == MemoryStore.CoreTopic ? MemoryStore.CoreChars : MemoryStore.TopicChars;
    /// <summary>CRLF and lone CR both become LF: the store writes LF and a browser textarea holds LF.</summary>
    private static string Lf(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    /// <summary>Row 40: the stale-edit token, over LF-normalised text so a CRLF file on disk, the hub's
    /// reply and the browser's textarea all hash alike. Never over what a browser echoed back.</summary>
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Lf(text)))).ToLowerInvariant();
    /// <summary>The provenance <see cref="ApproveCore"/> will compose, with the one unknown — the row id —
    /// as twelve nines: never fewer digits than a real id, so a size that passes on this string passes
    /// on the real one. <c>MemoryTools.ProposeRewrite</c> asks the same question with <c>proposal 0</c>,
    /// which is looser and relies on the approval re-check; this one must be strict because a refused
    /// save must leave no row.</summary>
    private static string Provisional(string author, string roomId) =>
        $"approved {Timestamps.Stamp(DateTimeOffset.UtcNow)} proposal {new string('9', 12)} by {author} in room {roomId}";
    private static object FileRow(MemoryStore memory, string slug) =>
        new { slug, path = PathOf(slug), chars = memory.ReadTopic(slug, int.MaxValue) is { } t ? Lf(t.Text).Length : 0, cap = CapOf(slug) };
    private static object FileBody(string slug, string text)
    {
        var lf = Lf(text);
        return new { slug, path = PathOf(slug), text = lf, chars = lf.Length, cap = CapOf(slug), hash = Hash(lf) };
    }

    /// <summary>Row 40: the editor's file list — the core first, then every topic in slug order, each
    /// with its live character count and the cap a save must stay under.</summary>
    private static IResult ListTopics(MemoryStore memory)
    {
        var rows = new List<object> { FileRow(memory, MemoryStore.CoreTopic) };
        rows.AddRange(memory.ListTopics().Select(t => FileRow(memory, t.Slug)));
        return Results.Json(rows);
    }

    /// <summary>Row 40: the whole file, uncut — the editor is the one reader that must see past a cap,
    /// because shrinking an over-cap topic is the only thing propose_rewrite cannot do (it refuses a
    /// truncated read).</summary>
    private static IResult GetTopic(string slug, MemoryStore memory)
    {
        if (!MemoryStore.TopicSlug.IsMatch(slug)) return Results.BadRequest(new { error = BadSlug });
        var current = memory.ReadTopic(slug, int.MaxValue);
        return current is null ? Results.NotFound(new { error = $"No topic '{slug}'." }) : Results.Json(FileBody(slug, current.Text));
    }

    /// <summary>Row 40: the size the cap is enforced on — the composed file, marker line and carried
    /// provenance included — so the dialog's count is the hub's count, not the textarea's. Reads
    /// nothing but the topic file; writes nothing.</summary>
    private static IResult PreviewTopic(string slug, PreviewBody body, HttpContext http, MemoryStore memory)
    {
        if (!MemoryStore.TopicSlug.IsMatch(slug)) return Results.BadRequest(new { error = BadSlug });
        var author = http.Items[BearerTokenMiddleware.ParticipantKey] as string ?? ChopDb.OwnerParticipantId;
        var chars = memory.ProjectedRewriteChars(slug, Lf(body.Text ?? "").Trim(), Provisional(author, body.RoomId ?? ""));
        var cap = CapOf(slug);
        return Results.Json(new { slug, chars, cap, over = chars > cap });
    }

    /// <summary>Row 40: a hand edit, saved as an approved <c>rewrite</c> authored by the bearer's own
    /// participant (the middleware set it) with <see cref="MemoryProposalStore.SourceEditor"/>. Every
    /// refusal runs BEFORE the row is created — spawn in flight, stale hash, empty body, cap, body rules —
    /// so a refused save leaves no row; the cap is decided on the composed size before ValidateRewrite
    /// runs, so an over-cap text is always a 409 and never the floor's 400. Then <see cref="ApproveCore"/>
    /// does exactly what the panel's Approve does. One action, one commit, under the same semaphore.</summary>
    private static async Task<IResult> PutTopic(string slug, EditBody body, HttpContext http, MemoryProposalStore proposals, MemoryStore memory, MemoryGit git, MessageStore store, MessageSignal signal, SpawnerService spawner)
    {
        if (!MemoryStore.TopicSlug.IsMatch(slug)) return Results.BadRequest(new { error = BadSlug });
        if (string.IsNullOrWhiteSpace(body.RoomId) || !store.RoomExists(body.RoomId)) return Results.NotFound(new { error = $"Unknown room '{body.RoomId}'." });
        // The middleware sets this on every guarded write; the check stays so a route mapped outside the
        // guard could never author a row as nobody.
        if (http.Items[BearerTokenMiddleware.ParticipantKey] is not string author) return Results.Unauthorized();
        // Exactly the string the row will hold: Create stores body.Trim(), and ApproveCore re-checks the
        // stored text, so every check here runs on the trimmed text or a leading-space heading could pass
        // the pre-checks unrecognised and then be refused after the INSERT.
        var text = Lf(body.Text ?? "").Trim();
        await Decisions.WaitAsync();
        try
        {
            if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
            var current = memory.ReadTopic(slug, int.MaxValue);
            if (current is null) return Results.NotFound(new { error = $"No topic '{slug}' to edit." });
            var currentHash = Hash(current.Text);
            if (!string.Equals(currentHash, body.BaseHash?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = StaleEdit, hash = currentHash });
            if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { error = "body is empty." });
            var cap = CapOf(slug);
            var projected = memory.ProjectedRewriteChars(slug, text, Provisional(author, body.RoomId));
            if (projected > cap)
            {
                var where = slug == MemoryStore.CoreTopic ? "the core" : $"topic '{slug}'";
                return Results.Conflict(new { error = $"The edit of {where} would be {projected} characters, over the {cap} cap. Trim it and save again.", chars = projected, current = Lf(current.Text).Length, cap });
            }
            try { MemoryStore.ValidateRewrite(slug, text); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            MemoryProposal proposal;
            try { proposal = proposals.Create(body.RoomId, author, slug, $"Edit {slug}", text, MemoryProposalStore.SourceEditor, null, null, MemoryProposalStore.KindRewrite); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            var (refusal, decided) = await ApproveCore(proposal, proposals, memory, git, store, signal);
            if (refusal is not null) return refusal;
            var written = memory.ReadTopic(slug, int.MaxValue)!.Text;
            return Results.Json(new
            {
                proposal = Map(decided!),
                slug, path = PathOf(slug), text = Lf(written), chars = Lf(written).Length, cap, hash = Hash(written),
                backup = $"{PathOf(slug)}.rewrite-{decided!.Id}.bak",
            });
        }
        finally { Decisions.Release(); }
    }

    internal sealed record EditBody(string? RoomId, string? Text, string? BaseHash);
    internal sealed record PreviewBody(string? RoomId, string? Text);

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

            var (refusal, decided) = await ApproveCore(p, proposals, memory, git, store, signal);
            return refusal ?? Results.Json(Map(decided!));
        }
        finally { Decisions.Release(); }
    }

    /// <summary>Everything an approval does for a row that is pending or approved-but-unwritten —
    /// pre-write checks, mark, write, commit, record, note — shared by the panel's Approve and the
    /// editor's save (row 40, which hands it the row it just created) so the two doors cannot drift.
    /// Runs under <see cref="Decisions"/>, which the caller holds. Returns the refusal, or null and the
    /// decided row.</summary>
    private static async Task<(IResult? Refusal, MemoryProposal? Decided)> ApproveCore(MemoryProposal p, MemoryProposalStore proposals, MemoryStore memory, MemoryGit git, MessageStore store, MessageSignal signal)
    {
        // Row 18, decision 3: refuse BEFORE marking, so a refused row stays pending rather than becoming
        // the replayable approved-but-unwritten state. A row that is ALREADY approved (a Retry after a crash
        // between mark and write) skips the check: it was committed to when it passed, and Retry must be able
        // to finish it (critique P1-5).
        var provenance = $"approved {Timestamps.Stamp(DateTimeOffset.UtcNow)} proposal {p.Id} by {p.AuthorId} in room {p.RoomId}";

        // Row 23, pass 2 finding H: a rewrite's pre-write checks run on BOTH the pending path AND the
        // Retry path (approved, written_to still null) - unlike the append/supersede checks below, which
        // stay pending-only per P1-5. Rewrite() itself throws KeyNotFoundException when the
        // topic vanished in the crash window, and unlike append/supersede that must never reach the
        // write uncaught: checking here turns it into a 409, not a 500.
        if (p.Kind == MemoryProposalStore.KindRewrite)
        {
            try { MemoryStore.ValidateRewrite(p.Topic, p.Body); }
            catch (ArgumentException e) { return (Results.Conflict(new { error = $"Memory proposal #{p.Id} cannot be written: {e.Message} Reject it and propose it again." }), null); }
            if (memory.ReadTopic(p.Topic) is null)
                return (Results.Conflict(new { error = $"No topic '{p.Topic}' to rewrite." }), null);
            var cap = p.Topic == MemoryStore.CoreTopic ? MemoryStore.CoreChars : MemoryStore.TopicChars;
            int chars;
            try { chars = memory.ProjectedRewriteChars(p.Topic, p.Body, provenance); }
            catch (KeyNotFoundException e) { return (Results.Conflict(new { error = e.Message }), null); }
            if (chars > cap)
            {
                var current = memory.ReadTopic(p.Topic)!.FullChars;
                var refused = HubNotes.Refused(p, chars, current);   // the banner and the room note read the same text
                if (RefusalNoted.TryAdd((memory.Root, p.Id), 0)) Note(store, signal, p.RoomId, refused);   // once per proposal per store per process, never per click
                return (Results.Conflict(new { error = refused, chars, current, cap }), null);
            }
        }
        else if (p.Status == MemoryProposalStore.Pending)
        {
            // Pass 2 P2-3: a row that predates row 18's body rule (a "## " line) must be refused HERE, before
            // the mark - after it, Append/Supersede would throw on every Retry and the row could never be
            // rejected. The same check covers any future Validate rule.
            try { MemoryStore.Validate(p.Title, p.Body); }
            catch (ArgumentException e) { return (Results.Conflict(new { error = $"Memory proposal #{p.Id} cannot be written: {e.Message} Reject it and propose it again." }), null); }
            if (p.Topic == MemoryStore.CoreTopic)
            {
                int chars;
                try { chars = memory.ProjectedCoreChars(p.Replaces, p.Title, p.Body, provenance); }
                catch (KeyNotFoundException e) { return (Results.Conflict(new { error = e.Message }), null); }
                if (chars > MemoryStore.CoreChars)
                {
                    var current = memory.ReadTopic(MemoryStore.CoreTopic)!.FullChars;
                    var refused = HubNotes.Refused(p, chars, current);   // the banner and the room note read the same text
                    if (RefusalNoted.TryAdd((memory.Root, p.Id), 0)) Note(store, signal, p.RoomId, refused);   // once per proposal per store per process, never per click
                    return (Results.Conflict(new { error = refused, chars, current, cap = MemoryStore.CoreChars }), null);
                }
            }
            else if (p.Replaces is not null && !memory.Titles(p.Topic).Contains(p.Replaces, StringComparer.Ordinal))
                return (Results.Conflict(new { error = $"No entry titled '{p.Replaces}' to replace." }), null);
        }

        // Row 23 (item 4): captured before the mark/write so the approval note can name what a
        // rewrite removed - the set difference of live titles before and after the replacement.
        var beforeTitles = p.Kind == MemoryProposalStore.KindRewrite ? memory.Titles(p.Topic) : null;

        // Mark first. An approved row with no written_to is the replayable state a crash below leaves.
        if (p.Status == MemoryProposalStore.Pending && proposals.Decide(p.Id, MemoryProposalStore.Approved, null, null) is null)
            return (Results.Conflict(new { error = $"Memory proposal #{p.Id} was decided concurrently." }), null);
        // A Supersede here can still throw KeyNotFoundException if the file changed between the check
        // above and this write - only a hand edit to the file in between can do that (same process, same semaphore, no
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
        var decided = proposals.RecordWrite(p.Id, written, hash) ?? proposals.Get(p.Id)!;
        Note(store, signal, decided.RoomId, HubNotes.Approved(decided, removedTitles));
        return (null, decided);
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
