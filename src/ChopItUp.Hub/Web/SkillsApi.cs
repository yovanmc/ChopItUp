using System.Collections.Concurrent;
using System.Text;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Memory;
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
/// <c>/api</c> — D1 gates only the two decision POSTs, and that gate is task 6's, not this one's: these
/// handlers are written so task 6 can wrap them without reshaping them.</summary>
public static class SkillsApi
{
    public const string SpawnRunning = "A spawn is running; decide skill proposals when the exchange has finished.";

    // One decision at a time, the same shape MemoryApi.Decisions uses: two clicks on the same card must
    // not race the mark-then-install sequence. A separate semaphore from MemoryApi's — the two proposal
    // kinds never need to serialise against each other, only against themselves.
    private static readonly SemaphoreSlim Decisions = new(1, 1);

    /// <summary>Task 7, pass 2 finding 10: what a card re-fetching on every hub note would otherwise
    /// re-hash and re-read from disk on every unauthenticated <c>GET</c>. Keyed on the proposal's
    /// (already room-confined, already normalised) <see cref="SkillProposal.SourceDir"/>; invalidated by
    /// the tree's own newest file write time, which is cheap to recompute (a directory walk with no
    /// content read) next to the hash-and-decode work it lets a request skip. Entries for a decided
    /// proposal are dropped by <see cref="CleanupSource"/> alongside the source directory itself.</summary>
    private static readonly ConcurrentDictionary<string, TreeCache> TreeCacheByDir = new(StringComparer.OrdinalIgnoreCase);

    private sealed record TreeCache(DateTime NewestWriteUtc, string Digest, IReadOnlyList<(string Path, string Text)> Files);

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

    private static object Row(SkillProposal p, bool sourceMissing, bool sourceChanged, IReadOnlyList<object>? entries, IReadOnlyList<object>? gates) => new
    {
        p.Id, p.RoomId, p.AuthorId, p.Name, p.ReplacesInstalled, p.Force,
        FileCount = p.Files, p.Bytes, p.Status, p.CreatedAt, p.DecidedAt, p.InstalledAt,
        SourceMissing = sourceMissing, SourceChanged = sourceChanged,
        Entries = entries ?? [], Gates = gates ?? [],
    };

    /// <summary>The cached (digest, per-file text) pair for <paramref name="sourceFull"/>, recomputed
    /// only when the tree's newest write time has moved since the last call — the check itself never
    /// reads a file's content, only its <see cref="File.GetLastWriteTimeUtc(string)"/>.</summary>
    private static (string Digest, IReadOnlyList<(string Path, string Text)> Files) TreeFor(string sourceFull)
    {
        var newest = NewestWriteTimeUtc(sourceFull);
        if (TreeCacheByDir.TryGetValue(sourceFull, out var cached) && cached.NewestWriteUtc == newest)
            return (cached.Digest, cached.Files);

        var manifest = SkillImport.HashSourceTree(sourceFull);
        var digest = SkillImport.ManifestDigest(manifest);
        var files = manifest.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(relative => (Path: relative, Text: ReadText(sourceFull, relative)))
            .ToList();
        TreeCacheByDir[sourceFull] = new TreeCache(newest, digest, files);
        return (digest, files);
    }

    private static string ReadText(string sourceFull, string relativePath) =>
        new UTF8Encoding(false).GetString(File.ReadAllBytes(Path.Combine(sourceFull, relativePath.Replace('/', Path.DirectorySeparatorChar))));

    /// <summary>The latest write time of any file under <paramref name="root"/>, skipping a root
    /// <c>.git</c> directory exactly as <see cref="SkillImport.HashSourceTree"/> does — so a change the
    /// hash would notice is a change this notices too, and vice versa.</summary>
    private static DateTime NewestWriteTimeUtc(string root)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var newest = DateTime.MinValue;
        Walk(rootFull);
        return newest;

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
                var t = File.GetLastWriteTimeUtc(file);
                if (t > newest) newest = t;
            }
        }
    }

    /// <summary>Task 7: approve. Ordered so a Retry call — the ordinary outcome of a swap that lost to a
    /// live reader (<c>SkillImport.cs:318-323</c>), not a rare crash — never repeats a check that can
    /// only be true once (the body hash, the replaces-installed snapshot) and never re-runs an install
    /// that already finished. <paramref name="body"/>'s hash and <see cref="SkillProposal.ReplacesInstalled"/>
    /// are checked only on the FIRST decision; every call, first or retried, re-hashes the SOURCE (not
    /// the recorded digest alone) and passes that manifest to <see cref="SkillImport.Run"/> as
    /// <c>expectedTree</c>, so task 2's staged-copy pin is checked against bytes this call actually
    /// read, closing the propose-to-approve window as well as the copy-time one (D5).</summary>
    private static async Task<IResult> Approve(long id, ApproveBody? body, SkillProposalStore proposals, SkillStore skills, ChopDb db, MessageStore store, MessageSignal signal, SpawnerService spawner)
    {
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

            if (!isRetry)
            {
                // AC6: the hash the caller sends must agree with the hash on record — a card that went
                // stale between fetch and click must not silently approve a different tree.
                if (!string.Equals(body?.TreeSha256, p.TreeSha256, StringComparison.Ordinal))
                    return Results.Conflict(new { error = $"Skill proposal #{id}: the tree hash sent does not match the one on record; re-fetch the listing and decide again." });

                // The force blocker (task 7): a card the owner read as "new" must never silently
                // overwrite a skill installed since, and a card read as "replaces" must not silently
                // become a fresh install of a skill removed since.
                var currentlyInstalled = IsInstalled(skills.Root, p.Name);
                if (currentlyInstalled != p.ReplacesInstalled)
                    return Results.Conflict(new { error = $"Skill proposal #{id}: '{p.Name}' {(currentlyInstalled ? "now exists" : "no longer exists")} in the skill store, which does not match what the listing showed; re-fetch it and decide again." });
            }

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

    private static async Task<IResult> Reject(long id, SkillProposalStore proposals, MessageStore store, MessageSignal signal, SpawnerService spawner)
    {
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
