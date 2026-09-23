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
/// <see cref="ChatApi"/>'s shape (a <c>MapGroup("/api")</c>). <c>GET</c> stays unauthenticated; the
/// owner-bearer gate guards non-GET requests only. The composer's slash menu fetches this on mount.
///
/// Projects exactly the fields of <see cref="SkillSummary"/> and no others. An empty store answers an
/// empty list, and so does one whose only skill fails its fingerprint check:
/// <see cref="SkillStore.List"/> already skips both cases, so this endpoint needs no second filter.
///
/// The hub owner's side of a skill proposal (the same shape as <see cref="MemoryApi"/>):
/// <c>GET /skills/proposals</c> lists what still needs a decision, with every byte of text the skill
/// would install and a <c>sourceChanged</c>/<c>sourceMissing</c> flag that suppresses that text
/// rather than trust a second read of a source an owner already reviewed (a swap-back attack); the
/// two POSTs decide one. <see cref="BearerTokenMiddleware"/> refuses a non-owner credential (403)
/// on every non-GET <c>/api</c> route before either handler runs; the handlers' own
/// <see cref="IsOwner"/> check stays as defence in depth.</summary>
public static class SkillsApi
{
    public const string SpawnRunning = "A spawn is running; decide skill proposals when the exchange has finished.";

    // One decision at a time, the same shape MemoryApi.Decisions uses: two clicks on the same card must
    // not race the mark-then-install sequence. A separate semaphore from MemoryApi's — the two proposal
    // kinds never need to serialise against each other, only against themselves.
    private static readonly SemaphoreSlim Decisions = new(1, 1);

    /// <summary>What a card re-fetching on every hub note would otherwise re-hash and re-read from
    /// disk on every unauthenticated <c>GET</c>. Keyed on the proposal's (already room-confined,
    /// already normalised) <see cref="SkillProposal.SourceDir"/>; invalidated by a stat-only
    /// fingerprint of every file in the tree. A single tree-wide newest-write-time is too weak: a
    /// writer that preserves timestamps, such as <c>Copy-Item</c> or <c>robocopy</c> with their
    /// default flags, can change a file's content without moving that maximum, so a stale entry would
    /// answer <c>GET</c> with bytes that do not match disk. The fingerprint is still cheap (a directory
    /// walk reading only length and last-write-time) next to the hash-and-decode work it skips.
    /// Bounded at <see cref="MaxCacheEntries"/>. Entries for a decided proposal are dropped by
    /// <see cref="CleanupSource"/> alongside the source directory itself.</summary>
    private static readonly ConcurrentDictionary<string, TreeCache> TreeCacheByDir = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>No LRU bookkeeping, just a ceiling: <see cref="TreeFor"/> drops the whole cache and
    /// lets the next <c>GET</c> rebuild it lazily once a new key would push the count past this. 200
    /// is not derived from a hard limit; it is a round number well above what
    /// <see cref="SkillProposalStore.MaxUndecidedPerRoom"/> (20) times a realistic number of
    /// concurrently busy rooms would hold resident at once.</summary>
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

    private static IResult ListProposals(SkillProposalStore store, SkillStore skills, string? room = null, string? status = SkillProposalStore.Undecided)
    {
        if (status is "all") status = null;
        if (status is not (null or SkillProposalStore.Undecided or SkillProposalStore.Pending or SkillProposalStore.Approved or SkillProposalStore.Rejected))
            return Results.BadRequest(new { error = "status must be undecided, pending, approved, rejected or all." });
        return Results.Json(store.List(string.IsNullOrWhiteSpace(room) ? null : room, status).Select(p => MapForList(p, skills.Root)));
    }

    /// <summary>Every relative path, every declared gate and the full text of every file, unless
    /// <paramref name="p"/>'s source has vanished or no longer hashes to what was pinned at propose
    /// time; either way nothing derived from a live read of the source is shown, only the row's own
    /// recorded fields, and the flag that says why. Gates come from <c>SKILL.md</c> alone: a proposal
    /// carries no overlay, so there is no second frontmatter to union with.</summary>
    private static object MapForList(SkillProposal p, string skillsRoot)
    {
        if (!Directory.Exists(p.SourceDir))
            return Row(p, skillsRoot, sourceMissing: true, sourceChanged: false, entries: null, gates: null);

        string digest;
        IReadOnlyList<(string Path, string Text)> files;
        try { (digest, files) = TreeFor(p.SourceDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return Row(p, skillsRoot, sourceMissing: true, sourceChanged: false, entries: null, gates: null);
        }

        if (!string.Equals(digest, p.TreeSha256, StringComparison.Ordinal))
            return Row(p, skillsRoot, sourceMissing: false, sourceChanged: true, entries: null, gates: null);

        var skillMd = files.FirstOrDefault(f => f.Path == "SKILL.md").Text ?? "";
        var (_, _, _, _, gates) = SkillStore.StripFrontmatter(skillMd.Replace("\r\n", "\n"), p.Name);
        var entries = files.Select(f => (object)new { f.Path, f.Text }).ToList();
        var gateList = gates.Select(g => (object)new { g.Name, g.Arguments }).ToList();
        return Row(p, skillsRoot, sourceMissing: false, sourceChanged: false, entries, gateList);
    }

    /// <summary>The listing carries <see cref="SkillProposal.TreeSha256"/>: the pinned digest is the
    /// one value the hub owner's card, the request body and the staged copy must all agree on, and
    /// <see cref="Approve"/> refuses a first decision whose body does not carry it, so the SPA needs
    /// it from here. Disclosing it to an unauthenticated <c>GET</c> adds nothing: it is a digest of
    /// the file contents this same response already returns in full.</summary>
    private static object Row(SkillProposal p, string skillsRoot, bool sourceMissing, bool sourceChanged, IReadOnlyList<object>? entries, IReadOnlyList<object>? gates) => new
    {
        p.Id, p.RoomId, p.AuthorId, p.Name, p.TreeSha256, p.ReplacesInstalled, p.Force,
        FileCount = p.Files, p.Bytes, p.Status, p.CreatedAt, p.DecidedAt, p.InstalledAt,
        SourceMissing = sourceMissing, SourceChanged = sourceChanged,
        Approvable = IsApprovable(p, skillsRoot, sourceMissing, sourceChanged),
        Entries = entries ?? [], Gates = gates ?? [],
    };

    /// <summary>The same conditions <see cref="Approve"/> itself enforces before it will attempt an
    /// install. Source present and source unchanged are the two flags <paramref name="sourceMissing"/>/
    /// <paramref name="sourceChanged"/> carry; "text shown in full" is implied by both being false,
    /// since <see cref="MapForList"/> never reaches the per-file read that populates <c>Entries</c>
    /// otherwise. The status half mirrors <see cref="Approve"/>'s own first checks (already rejected,
    /// or already approved-and-installed, refuse immediately): only pending, or
    /// approved-but-not-yet-installed (the Retry state, still "undecided"; see
    /// <see cref="SkillProposalStore.Undecided"/>), can still be decided. Computed once here so the
    /// card reads a single flag instead of re-deriving the rule.
    ///
    /// The source flags do not disqualify a retry row whose install already completed.
    /// <see cref="Approve"/>'s Retry arm hashes the installed tree and short-circuits to
    /// <see cref="SkillProposalStore.MarkInstalled"/> before it reads the source at all, so the hub
    /// would finish such a row. Marking it un-approvable would strand it forever: the card disables
    /// Retry on <c>!approvable</c>, and <see cref="Reject"/> only acts from
    /// <see cref="SkillProposalStore.Pending"/>. (Approve, install completes, hub dies before the
    /// record, proposer cleans up its room directory: the row could never be decided again while
    /// counting against <see cref="SkillProposalStore.MaxUndecidedPerRoom"/>.) A first decision is
    /// untouched: a pending row with a missing or changed source stays un-approvable, whatever is
    /// installed under that name.</summary>
    private static bool IsApprovable(SkillProposal p, string skillsRoot, bool sourceMissing, bool sourceChanged)
    {
        if (p.Status == SkillProposalStore.Pending) return !sourceMissing && !sourceChanged;
        if (p.Status != SkillProposalStore.Approved || p.InstalledAt is not null) return false;
        // The retry row. The cheap answer first: with the source present and pinned, Approve's ordinary
        // path would run, and the installed tree does not need hashing to know that.
        return (!sourceMissing && !sourceChanged) || AlreadyInstalled(p, skillsRoot);
    }

    /// <summary>Detects a completed install by comparing the installed tree to the recorded manifest
    /// rather than by re-running the install. The one definition, read by both
    /// <see cref="IsApprovable"/> and <see cref="Approve"/>, so the listing's flag and the endpoint's
    /// behaviour cannot drift apart. Reuses <see cref="SkillImport.HashSourceTree"/>; an unreadable
    /// target answers false and falls through to the ordinary path, which refuses on its own terms
    /// rather than throwing out of a <c>GET</c>.</summary>
    private static bool AlreadyInstalled(SkillProposal p, string skillsRoot)
    {
        var target = Path.Combine(skillsRoot, p.Name);
        if (!Directory.Exists(target)) return false;
        try { return SkillImport.ManifestDigest(SkillImport.HashSourceTree(target)) == p.TreeSha256; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { return false; }
    }

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

    /// <summary>Every file under <paramref name="root"/> (relative path, length and last-write-time
    /// ticks) in ordinal path order, skipping a root <c>.git</c> directory exactly as
    /// <see cref="SkillImport.HashSourceTree"/> does, so a change the hash would notice is a change
    /// this notices too, and vice versa. No content read: only <see cref="FileInfo.Length"/> and
    /// <see cref="FileInfo.LastWriteTimeUtc"/>.
    ///
    /// Residual: a rewrite that preserves both a file's length and its last-write time still reads as
    /// unchanged here. <see cref="Approve"/>'s own re-hash of the source and the staged-copy pin both
    /// catch that before anything installs; this fingerprint only feeds <c>GET</c>, never
    /// <c>Approve</c>.</summary>
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

    /// <summary>Approve. A Retry call is still an approval, so the source and installed-state
    /// preconditions hold on it too. The installed-or-not-changed check
    /// (<see cref="SkillProposal.ReplacesInstalled"/> against a live re-read) is unconditional on both
    /// paths: it refuses a retry against a target that now holds something other than what the
    /// listing showed (an unrelated tree placed at the name between a failed first attempt and the
    /// retry, say), even when <c>p.Force</c> would otherwise let <see cref="SkillImport.Run"/>
    /// overwrite it. <paramref name="body"/>'s hash must match on the first decision; a retry enforces
    /// the check only when the body carries a hash. Every call re-hashes the source (not the recorded
    /// digest alone) and passes that manifest to <see cref="SkillImport.Run"/> as
    /// <c>expectedTree</c>, so the staged-copy pin is checked against bytes this call actually read,
    /// closing the propose-to-approve window as well as the copy-time one. The already-finished
    /// detection just below (installed tree hashes to the recorded manifest, so
    /// <see cref="SkillProposalStore.MarkInstalled"/> with no re-run) runs first.</summary>
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

            // The Retry arm: detect a completed install by comparing the installed tree to the
            // recorded manifest, never by re-running Run. Refusal 8 (target exists, no force) would
            // otherwise refuse a completed install forever. This runs before anything reads the
            // source, which is why the listing marks such a row approvable even when its source has
            // since vanished or changed (see IsApprovable). On a miss (no target, or installed content
            // that disagrees with the pin) this falls through to a fresh Run, which itself refuses
            // against a stale target when force is false.
            if (isRetry && AlreadyInstalled(p, skills.Root))
            {
                var finished = proposals.MarkInstalled(id) ?? proposals.Get(id)!;
                CleanupSource(finished);
                Note(store, signal, finished.RoomId, Installed(finished));
                return Results.Json(Map(finished));
            }

            // A retry is still an approval, so both of these hold on the retry path too.
            //
            // Body hash: the first decision always sends it from the fetched listing and it must match
            // exactly. A retry enforces the check only when the body carries a hash (the Retry button
            // may resend no body at all).
            if ((!isRetry || body?.TreeSha256 is not null) &&
                !string.Equals(body?.TreeSha256, p.TreeSha256, StringComparison.Ordinal))
                return Results.Conflict(new { error = $"Skill proposal #{id}: the tree hash sent does not match the one on record; re-fetch the listing and decide again." });

            // A card the hub owner read as "new" must never silently overwrite a skill installed since, and
            // a card read as "replaces" must not silently become a fresh install of a skill removed
            // since. Unconditional on retry as well as the first attempt: this refuses a retry against a
            // target that now holds something other than what the proposal recorded, even when p.Force
            // would otherwise let SkillImport.Run overwrite it (the "installed content disagrees with the
            // pin" fall-through just above).
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
                return Results.Conflict(new { error = result.Message });   // approved, not installed: still decidable

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

    /// <summary>The source tree lives in the room's own directory only long enough to be reviewed
    /// and, if approved, copied into the skill store: a decided proposal's copy is deleted, and its
    /// tree cache entry with it. Best-effort, the same stance the note itself takes: a decision stands
    /// even when this cleanup fails.</summary>
    private static void CleanupSource(SkillProposal p)
    {
        TreeCacheByDir.TryRemove(p.SourceDir, out _);
        try { if (Directory.Exists(p.SourceDir)) Directory.Delete(p.SourceDir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"skills: could not clean up source for proposal #{p.Id} ({e.GetType().Name}: {e.Message})");
        }
    }

    /// <summary>The authorization half (the middleware already did the authentication half): only the
    /// owner, from either hand, may decide a skill proposal. <see cref="BearerTokenMiddleware"/> stamps
    /// <see cref="BearerTokenMiddleware.ParticipantKey"/> in <see cref="HttpContext.Items"/> once a
    /// bearer token resolves; a resolvable-but-non-owner participant reaches here exactly as any other
    /// authenticated caller would, so this is where the two questions ("is there a credential" vs "is
    /// it the hub owner's") are answered separately.</summary>
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
