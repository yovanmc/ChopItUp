using System.Collections.Concurrent;
using System.Text;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Security;
using ChopItUp.Hub.Skills;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Web;

/// <summary>What the store can tell the outside world about itself: <c>GET /api/skills</c>, mirroring
/// <see cref="ChatApi"/>'s shape — a <c>MapGroup("/api")</c>, no auth (loopback is the boundary, per
/// <see cref="ChatApi"/>'s doc comment). The composer's slash menu (task 7) fetches this on mount.
///
/// Projects exactly the fields of <see cref="SkillSummary"/> and no others (critique pass 2, M-5: the
/// first draft had four different shapes across three tasks, including a <c>HasOverlay</c> no record
/// defined; row 19 adds <c>isRun</c> as a fifth). An empty store answers an empty list, and so does
/// one whose only skill fails its fingerprint check — <see cref="SkillStore.List"/> already skips
/// both cases, so this endpoint needs no second filter.
///
/// M25 task 7 adds the owner's side of a skill proposal (D1/D15's shape, repeated from
/// <see cref="MemoryApi"/>): <c>GET /skills/proposals</c> lists what still needs a decision, with every
/// byte of text the skill would install and a <c>sourceChanged</c>/<c>sourceMissing</c> flag that
/// suppresses that text rather than trust a second read of a source an owner already reviewed (pass 1's
/// swap-back attack); the two POSTs decide one. <c>GET</c> stays unauthenticated like the rest of
/// <c>/api</c>. Task 6 gates the two decision POSTs: <see cref="BearerTokenMiddleware"/> now also
/// guards them (401 on a missing or unresolvable credential), and <see cref="Approve"/>/
/// <see cref="Reject"/> additionally require the resolved participant to be
/// <see cref="ChopDb.OwnerParticipantId"/> or <see cref="ChopDb.OwnerRemoteParticipantId"/> (403
/// otherwise) — checked first, before either handler does anything else, so a non-owner credential
/// changes nothing.</summary>
public static class SkillsApi
{
    public const string SpawnRunning = "A spawn is running; decide skill proposals when the exchange has finished.";

    // One decision at a time, the same shape MemoryApi.Decisions uses: two clicks on the same card must
    // not race the mark-then-install sequence. A separate semaphore from MemoryApi's — the two proposal
    // kinds never need to serialise against each other, only against themselves.
    private static readonly SemaphoreSlim Decisions = new(1, 1);

    /// <summary>Task 7, pass 2 finding 10: what a card re-fetching on every hub note would otherwise
    /// re-hash and re-read from disk on every unauthenticated <c>GET</c>. Keyed on the proposal's
    /// (already room-confined, already normalised) <see cref="SkillProposal.SourceDir"/>; invalidated
    /// by a stat-only fingerprint of every file in the tree (task 7 correction, item B — a single
    /// tree-wide newest-write-time was too weak: a writer that preserves timestamps, such as
    /// <c>Copy-Item</c> or <c>robocopy</c> with their default flags, can change a file's content
    /// without moving that maximum, so a stale cache entry would answer <c>GET</c> with bytes that no
    /// longer match disk), which is still cheap to recompute (a directory walk reading only length and
    /// last-write-time, no content) next to the hash-and-decode work it lets a request skip. Bounded at
    /// <see cref="MaxCacheEntries"/> so it cannot grow without limit. Entries for a decided proposal are
    /// dropped by <see cref="CleanupSource"/> alongside the source directory itself.</summary>
    private static readonly ConcurrentDictionary<string, TreeCache> TreeCacheByDir = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Task 7 correction, item B: no LRU bookkeeping, just a ceiling — <see cref="TreeFor"/>
    /// drops the whole cache and lets the next <c>GET</c> rebuild it lazily once a NEW key would push
    /// the count past this. 200 is not derived from a hard limit; it is a round number well above what
    /// <see cref="SkillProposalStore.MaxUndecidedPerRoom"/> (20) times a realistic number of
    /// concurrently busy rooms would hold resident at once, cheap to raise later if that guess is
    /// wrong.</summary>
    private const int MaxCacheEntries = 200;

    private sealed record FileStat(string Path, long Length, long WriteTicks);

    private sealed record TreeCache(IReadOnlyList<FileStat> Fingerprint, string Digest, IReadOnlyList<(string Path, string Text)> Files);

    public static void MapSkillsApi(this WebApplication app)
    {
        app.MapGroup("/api").MapGet("/skills", (SkillStore skills) =>
            Results.Json(skills.List().Select(s => new { s.Name, s.Title, s.Description, s.Chars, s.IsRun })));

        var proposals = app.MapGroup("/api/skills/proposals");
        proposals.MapGet("", ListProposals);
        proposals.MapPost("/{id:long}/approve", Approve);
        proposals.MapPost("/{id:long}/reject", Reject);
    }

    private static IResult ListProposals(SkillProposalStore store, string? room = null, string? status = SkillProposalStore.Undecided)
    {
        if (status is "all") status = null;
        if (status is not (null or SkillProposalStore.Undecided or SkillProposalStore.Pending or SkillProposalStore.Approved or SkillProposalStore.Rejected))
            return Results.BadRequest(new { error = "status must be undecided, pending, approved, rejected or all." });
        return Results.Json(store.List(string.IsNullOrWhiteSpace(room) ? null : room, status).Select(MapForList));
    }

    /// <summary>AC4: every relative path, every declared gate and the full text of every file, unless
    /// <paramref name="p"/>'s source has vanished or no longer hashes to what was pinned at propose time
    /// — either way nothing derived from a live read of the source is shown, only the row's own recorded
    /// fields, and the flag that says why. Gates come from <c>SKILL.md</c> alone: v1 proposals carry no
    /// overlay (D6), so there is no second frontmatter to union with.</summary>
    private static object MapForList(SkillProposal p)
    {
        if (!Directory.Exists(p.SourceDir))
            return Row(p, sourceMissing: true, sourceChanged: false, entries: null, gates: null);

        string digest;
        IReadOnlyList<(string Path, string Text)> files;
        try { (digest, files) = TreeFor(p.SourceDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return Row(p, sourceMissing: true, sourceChanged: false, entries: null, gates: null);
        }

        if (!string.Equals(digest, p.TreeSha256, StringComparison.Ordinal))
            return Row(p, sourceMissing: false, sourceChanged: true, entries: null, gates: null);

        var skillMd = files.FirstOrDefault(f => f.Path == "SKILL.md").Text ?? "";
        var (_, _, _, _, gates) = SkillStore.StripFrontmatter(skillMd.Replace("\r\n", "\n"), p.Name);
        var entries = files.Select(f => (object)new { f.Path, f.Text }).ToList();
        var gateList = gates.Select(g => (object)new { g.Name, g.Arguments }).ToList();
        return Row(p, sourceMissing: false, sourceChanged: false, entries, gateList);
    }

    /// <summary>Task 8 correction: the listing carries <see cref="SkillProposal.TreeSha256"/>. D5 makes
    /// the pinned digest "the one value the owner's card, the request body and the staged copy must all
    /// three agree on", and <see cref="Approve"/> refuses a first decision whose body does not carry it
    /// — but the row this shape returns omitted it, so the SPA had no way to obtain the hash it is
    /// required to send and every approval from the card would have been refused. Disclosing it to an
    /// unauthenticated <c>GET</c> adds nothing: it is a digest of the file contents this same response
    /// already returns in full.</summary>
    private static object Row(SkillProposal p, bool sourceMissing, bool sourceChanged, IReadOnlyList<object>? entries, IReadOnlyList<object>? gates) => new
    {
        p.Id, p.RoomId, p.AuthorId, p.Name, p.TreeSha256, p.ReplacesInstalled, p.Force,
        FileCount = p.Files, p.Bytes, p.Status, p.CreatedAt, p.DecidedAt, p.InstalledAt,
        SourceMissing = sourceMissing, SourceChanged = sourceChanged,
        Approvable = IsApprovable(p, sourceMissing, sourceChanged),
        Entries = entries ?? [], Gates = gates ?? [],
    };

    /// <summary>Task 7 correction, item C (AC4's "marked ⇒ not approvable" made explicit): the same
    /// conditions <see cref="Approve"/> itself enforces before it will even attempt an install. Source
    /// present and source unchanged are the two flags <paramref name="sourceMissing"/>/
    /// <paramref name="sourceChanged"/> already carry; "text shown in full" is implied by both being
    /// false, since <see cref="MapForList"/> never reaches the per-file read that populates
    /// <c>Entries</c> otherwise. The status half mirrors <see cref="Approve"/>'s own first checks
    /// (already rejected, or already approved-and-installed, refuse immediately): only pending, or
    /// approved-but-not-yet-installed (the Retry state, still "undecided" — see
    /// <see cref="SkillProposalStore.Undecided"/>), can still be decided. Computed once here so task
    /// 8's card reads a single flag instead of re-deriving the rule itself.</summary>
    private static bool IsApprovable(SkillProposal p, bool sourceMissing, bool sourceChanged) =>
        !sourceMissing && !sourceChanged &&
        (p.Status == SkillProposalStore.Pending || (p.Status == SkillProposalStore.Approved && p.InstalledAt is null));

    /// <summary>The cached (digest, per-file text) pair for <paramref name="sourceFull"/>, recomputed
    /// only when the tree's per-file fingerprint (item B) has moved since the last call — the check
    /// itself never reads a file's content, only its length and last-write-time.</summary>
    private static (string Digest, IReadOnlyList<(string Path, string Text)> Files) TreeFor(string sourceFull)
    {
        var fingerprint = Fingerprint(sourceFull);
        if (TreeCacheByDir.TryGetValue(sourceFull, out var cached) && cached.Fingerprint.SequenceEqual(fingerprint))
            return (cached.Digest, cached.Files);

        var manifest = SkillImport.HashSourceTree(sourceFull);
        var digest = SkillImport.ManifestDigest(manifest);
        var files = manifest.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(relative => (Path: relative, Text: ReadText(sourceFull, relative)))
            .ToList();

        // Item B's bound: only a NEW key risks growing the cache past the ceiling — updating an
        // already-cached key never changes the count.
        if (!TreeCacheByDir.ContainsKey(sourceFull) && TreeCacheByDir.Count >= MaxCacheEntries)
            TreeCacheByDir.Clear();

        TreeCacheByDir[sourceFull] = new TreeCache(fingerprint, digest, files);
        return (digest, files);
    }

    private static string ReadText(string sourceFull, string relativePath) =>
        new UTF8Encoding(false).GetString(File.ReadAllBytes(Path.Combine(sourceFull, relativePath.Replace('/', Path.DirectorySeparatorChar))));

    /// <summary>Item B: every file under <paramref name="root"/> — relative path, length and
    /// last-write-time ticks — in ordinal path order, skipping a root <c>.git</c> directory exactly as
    /// <see cref="SkillImport.HashSourceTree"/> does, so a change the hash would notice is a change
    /// this notices too, and vice versa. Still no content read: only <see cref="FileInfo.Length"/> and
    /// <see cref="FileInfo.LastWriteTimeUtc"/>.
    ///
    /// Residual, documented rather than hidden: a rewrite that preserves BOTH a file's length and its
    /// last-write time still reads as unchanged here. <see cref="Approve"/>'s own re-hash of the source
    /// (AC6) and task 2's staged-copy pin (D5) both catch that before anything installs; this
    /// fingerprint only feeds <c>GET</c>, never <c>Approve</c>.</summary>
    private static IReadOnlyList<FileStat> Fingerprint(string root)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var stats = new List<FileStat>();
        Walk(rootFull);
        return stats.OrderBy(s => s.Path, StringComparer.Ordinal).ToList();

        void Walk(string dir)
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (string.Equals(Path.GetFileName(sub), ".git", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.TrimEndingDirectorySeparator(dir), rootFull, StringComparison.OrdinalIgnoreCase))
                    continue;
                Walk(sub);
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var relative = Path.GetRelativePath(rootFull, file).Replace(Path.DirectorySeparatorChar, '/');
                var info = new FileInfo(file);
                stats.Add(new FileStat(relative, info.Length, info.LastWriteTimeUtc.Ticks));
            }
        }
    }

    /// <summary>Task 7: approve. Task 7 correction, item A: a Retry call is still an approval, so
    /// AC6/AC7's preconditions hold on it too, not only on the first decision. The
    /// installed-or-not-changed check (<see cref="SkillProposal.ReplacesInstalled"/> against a live
    /// re-read) is unconditional on both paths — it is what refuses a retry against a target that now
    /// holds something other than what the listing showed (an unrelated tree placed at the name
    /// between a failed first attempt and the retry, say), even when <c>p.Force</c> would otherwise let
    /// <see cref="SkillImport.Run"/> overwrite it outright. <paramref name="body"/>'s hash is required
    /// to match on the first decision, same as always; the plan does not settle whether a Retry must
    /// resend it, so a retry enforces the check only when the body actually carries a hash. Either way,
    /// every call, first or retried, re-hashes the SOURCE (not the recorded digest alone) and passes
    /// that manifest to <see cref="SkillImport.Run"/> as <c>expectedTree</c>, so task 2's staged-copy
    /// pin is checked against bytes this call actually read, closing the propose-to-approve window as
    /// well as the copy-time one (D5). The already-finished detection just below (installed tree hashes
    /// to the recorded manifest → <see cref="SkillProposalStore.MarkInstalled"/>, no re-run) still runs
    /// first and is unchanged (AC8).</summary>
    private static async Task<IResult> Approve(long id, ApproveBody? body, HttpContext httpContext, SkillProposalStore proposals, SkillStore skills, ChopDb db, MessageStore store, MessageSignal signal, SpawnerService spawner)
    {
        if (!IsOwner(httpContext)) return Forbidden();

        await Decisions.WaitAsync();
        try
        {
            if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
            var p = proposals.Get(id);
            if (p is null) return Results.NotFound(new { error = $"No skill proposal #{id}." });
            if (p.Status == SkillProposalStore.Rejected)
                return Results.Conflict(new { error = $"Skill proposal #{id} is already rejected." });
            if (p.Status == SkillProposalStore.Approved && p.InstalledAt is not null)
                return Results.Conflict(new { error = $"Skill proposal #{id} is already approved and installed." });

            var isRetry = p.Status == SkillProposalStore.Approved;   // InstalledAt is null here, or the check above would have returned
            var target = Path.Combine(skills.Root, p.Name);

            // The Retry arm (plan pass 2 blocker 1): detect a completed install by comparing the
            // INSTALLED tree to the recorded manifest, never by re-running Run — refusal 8 (target
            // exists, no force) would otherwise refuse a completed install forever (AC8).
            if (isRetry && Directory.Exists(target))
            {
                var installedDigest = SkillImport.ManifestDigest(SkillImport.HashSourceTree(target));
                if (installedDigest == p.TreeSha256)
                {
                    var finished = proposals.MarkInstalled(id) ?? proposals.Get(id)!;
                    CleanupSource(finished);
                    Note(store, signal, finished.RoomId, Installed(finished));
                    return Results.Json(Map(finished));
                }
                // Installed content disagrees with the pin (not the ordinary retry case) — fall through
                // to a fresh Run, which will itself refuse against a stale target when force is false.
            }

            // AC6/AC7 (task 7 correction, item A): a retry is still an approval, so both of these must
            // hold on the retry path too, not just on the first decision.
            //
            // Body hash: the first decision always sends it from the fetched listing and it must match
            // exactly. The plan does not settle whether a Retry must resend it, so a retry enforces the
            // check only when the body actually carries a hash, and does not require one when it does
            // not (existing tests exercise the Retry button resending no body at all).
            if ((!isRetry || body?.TreeSha256 is not null) &&
                !string.Equals(body?.TreeSha256, p.TreeSha256, StringComparison.Ordinal))
                return Results.Conflict(new { error = $"Skill proposal #{id}: the tree hash sent does not match the one on record; re-fetch the listing and decide again." });

            // The force blocker (task 7): a card the owner read as "new" must never silently overwrite
            // a skill installed since, and a card read as "replaces" must not silently become a fresh
            // install of a skill removed since. Unconditional on retry as well as the first attempt —
            // this is what refuses a retry against a target that now holds something other than what
            // the proposal recorded, even when p.Force would otherwise let SkillImport.Run overwrite it
            // outright (the "installed content disagrees with the pin" fall-through just above).
            var currentlyInstalled = IsInstalled(skills.Root, p.Name);
            if (currentlyInstalled != p.ReplacesInstalled)
                return Results.Conflict(new { error = $"Skill proposal #{id}: '{p.Name}' {(currentlyInstalled ? "now exists" : "no longer exists")} in the skill store, which does not match what the listing showed; re-fetch it and decide again." });

            Dictionary<string, string> sourceTree;
            try { sourceTree = SkillImport.HashSourceTree(p.SourceDir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                return Results.Conflict(new { error = $"Skill proposal #{id}: its source could not be read ({e.Message})." });
            }
            if (SkillImport.ManifestDigest(sourceTree) != p.TreeSha256)
                return Results.Conflict(new { error = $"Skill proposal #{id}: the source has changed since it was proposed; reject it and propose it again." });

            if (!isRetry && proposals.MarkApproved(id) is null)
                return Results.Conflict(new { error = $"Skill proposal #{id} was decided concurrently." });

            var result = SkillImport.Run(p.SourceDir, skills.Root, p.Force, new SkillHashes(db), expectedTree: sourceTree);
            if (result.Outcome != SkillImportOutcome.Ok)
                return Results.Conflict(new { error = result.Message });   // approved, not installed — still decidable (AC7)

            var installed = proposals.MarkInstalled(id) ?? proposals.Get(id)!;
            CleanupSource(installed);
            Note(store, signal, installed.RoomId, Installed(installed));
            return Results.Json(Map(installed));
        }
        finally { Decisions.Release(); }
    }

    private static async Task<IResult> Reject(long id, HttpContext httpContext, SkillProposalStore proposals, MessageStore store, MessageSignal signal, SpawnerService spawner)
    {
        if (!IsOwner(httpContext)) return Forbidden();

        await Decisions.WaitAsync();
        try
        {
            if (spawner.AnySpawnInFlight) return Results.Conflict(new { error = SpawnRunning });
            var p = proposals.Get(id);
            if (p is null) return Results.NotFound(new { error = $"No skill proposal #{id}." });
            if (p.Status != SkillProposalStore.Pending) return Results.Conflict(new { error = $"Skill proposal #{id} is already {p.Status}." });
            var decided = proposals.MarkRejected(id)!;
            CleanupSource(decided);
            Note(store, signal, decided.RoomId, $"Skill proposal #{decided.Id} rejected.");
            return Results.Json(Map(decided));
        }
        finally { Decisions.Release(); }
    }

    /// <summary>Task 1 / D9 (pass 1 finding 9): the source tree lives in the room's own directory only
    /// long enough to be reviewed and, if approved, copied into the skill store — a decided proposal's
    /// copy is deleted so it is not kept forever, and its tree cache entry with it. Best-effort, the same
    /// stance the note itself takes: a decision stands even when this cleanup fails.</summary>
    private static void CleanupSource(SkillProposal p)
    {
        TreeCacheByDir.TryRemove(p.SourceDir, out _);
        try { if (Directory.Exists(p.SourceDir)) Directory.Delete(p.SourceDir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"skills: could not clean up source for proposal #{p.Id} ({e.GetType().Name}: {e.Message})");
        }
    }

    /// <summary>D1's authorization half (the middleware already did the authentication half): only the
    /// owner, from either hand, may decide a skill proposal. <see cref="BearerTokenMiddleware"/> stamps
    /// <see cref="BearerTokenMiddleware.ParticipantKey"/> in <see cref="HttpContext.Items"/> once a
    /// bearer token resolves; a resolvable-but-non-owner participant reaches here exactly as any other
    /// authenticated caller would, so this is where the two questions ("is there a credential" vs "is it
    /// the owner's") are answered separately, per acceptance 5.</summary>
    private static bool IsOwner(HttpContext context) =>
        context.Items.TryGetValue(BearerTokenMiddleware.ParticipantKey, out var raw) &&
        raw is string participant &&
        (participant == ChopDb.OwnerParticipantId || participant == ChopDb.OwnerRemoteParticipantId);

    private static IResult Forbidden() => Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);

    private static bool IsInstalled(string skillsRoot, string name) =>
        Directory.Exists(Path.Combine(skillsRoot, name)) || Directory.Exists(Path.Combine(skillsRoot, name + ".replaced"));

    private static string Installed(SkillProposal p) =>
        $"Skill proposal #{p.Id} approved: '{p.Name}' installed" + (p.ReplacesInstalled ? ", replacing the existing skill." : ".");

    /// <summary>A note is the trail, not the mechanism: the decision stands even when the note fails
    /// (same stance as <c>SpawnerService.PostNote</c> and <c>MemoryApi.Note</c>).</summary>
    private static void Note(MessageStore store, MessageSignal signal, string roomId, string text)
    {
        try { HubNotes.Post(store, signal, roomId, text); }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"skills: note to '{roomId}' not posted ({e.GetType().Name}: {e.Message}): {text.Split('\n')[0]}"); }
    }

    private static object Map(SkillProposal p) => new
    {
        p.Id, p.RoomId, p.AuthorId, p.Name, p.ReplacesInstalled, p.Force,
        FileCount = p.Files, p.Bytes, p.Status, p.CreatedAt, p.DecidedAt, p.InstalledAt,
    };

    internal sealed record ApproveBody(string? TreeSha256);
}
