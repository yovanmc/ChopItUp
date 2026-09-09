using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChopItUp.Core.Memory;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Memory;
using ChopItUp.Hub.Security;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ChopItUp.Hub.Mcp;

/// <summary>The memory half of the contract (M10, D15). <c>recall</c> is how a spawn gets what its
/// prompt did not carry and how an interactive host gets memory at all; <c>propose_memory</c> writes a
/// proposal, never the store — the owner approves in the room. The proposer is the authenticated
/// participant, stamped like a message author. Row 23 (item 3): <paramref name="participants"/> is the
/// roster <c>propose_rewrite</c>'s caller boundary reads — a singleton already registered in
/// <c>HubHost</c>, but a dependency this tool did not have before. The roster it returns is loaded once
/// at hub startup, so a class or host change to a participant row needs a hub restart before the
/// boundary sees it.</summary>
[McpServerToolType]
public sealed class MemoryTools(MemoryStore memory, MemoryProposalStore proposals, MessageStore store, MessageSignal signal, IHttpContextAccessor http, ParticipantStore participants)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string Caller =>
        http.HttpContext?.Items[BearerTokenMiddleware.ParticipantKey] as string
        ?? throw new McpException("Unauthenticated request reached a tool; this is a hub bug.");

    [McpServerTool(Name = "recall", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Read the shared memory every participant uses. With nothing: the core (what every model should know) and every topic with its entry titles. With a topic: that topic's text. With a query: every entry whose title or body contains it, across all topics or within the given one. Read it before answering anything about the owner or their work, and before proposing a memory.")]
    public string Recall(
        [Description("A topic slug from the list, e.g. \"user\". Omit for the core and the topic list; \"core\" returns the whole core file uncut; a topic is cut at 24000 characters and says so.")] string? topic = null,
        [Description("2 to 200 characters to search for, case-insensitive, in entry titles and bodies. Returns matching entries as topic, title and a snippet; combine with topic to search one topic.")] string? query = null)
    {
        _ = Caller;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var scope = string.IsNullOrWhiteSpace(topic) ? null : topic.Trim();
            if (scope is not null && !MemoryStore.TopicSlug.IsMatch(scope))
                throw new McpException("topic must be a slug: lowercase letters, digits and hyphens.");
            IReadOnlyList<MemoryHit> hits;
            try { hits = memory.Search(query, scope); }
            catch (ArgumentException e) { throw new McpException(e.Message); }
            return JsonSerializer.Serialize(new { Query = query.Trim(), Hits = hits, Capped = hits.Count == MemoryStore.MaxHits }, JsonOptions);
        }
        if (string.IsNullOrWhiteSpace(topic))
        {
            var core = memory.ReadCore();
            return JsonSerializer.Serialize(new
            {
                Core = core.Text,
                Truncated = core.Truncated,
                CoreTitles = memory.Titles(MemoryStore.CoreTopic),
                Topics = memory.ListTopics().Select(t => new { t.Slug, t.Bytes, Titles = memory.Titles(t.Slug) }),
            }, JsonOptions);
        }
        var slug = topic.Trim();
        if (!MemoryStore.TopicSlug.IsMatch(slug))
            throw new McpException("topic must be a slug: lowercase letters, digits and hyphens.");
        var text = memory.ReadTopic(slug)
            ?? throw new McpException($"No topic '{slug}'. Topics: {Names()}.");
        return JsonSerializer.Serialize(new { Topic = slug, text.Text, Truncated = text.Truncated, Chars = text.FullChars }, JsonOptions);
    }

    [McpServerTool(Name = "propose_memory", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Propose one durable fact for the shared memory: something about the owner or the work that memory does not already say and that every future model should know. The hub records you as the proposer and announces it in the room; the owner approves or rejects it there. Nothing is remembered until approved. Propose once per fact, never per message.")]
    public string ProposeMemory(
        [Description("Room id the proposal belongs to, e.g. \"general\".")] string room_id,
        [Description("Topic slug: lowercase letters, digits, hyphens (e.g. \"user\", \"project\"); \"core\" for the always-injected core. New topics are fine.")] string topic,
        [Description("One line, up to 120 characters: the fact as a heading.")] string title,
        [Description("The fact itself, markdown, up to 4000 characters.")] string body,
        [Description("The exact title of an entry this topic already holds that this proposal corrects or updates. On approval that entry is retired and this one takes its place. Omit to add a new entry.")] string? replaces = null)
    {
        var me = Caller;
        // Normalized once here; every use below reads these locals rather than re-guarding the
        // (non-nullable) parameters, so the flow state passed to proposals.Create stays definitely
        // non-null (was CS8604 under -warnaserror: repeated `title ?? ""`/`body ?? ""` narrowed the
        // parameters' flow state to maybe-null for the rest of the method).
        var entryTitle = title ?? "";
        var entryBody = body ?? "";
        if (!store.RoomExists(room_id)) throw new McpException($"Unknown room '{room_id}'. Call list_rooms.");
        var slug = (topic ?? "").Trim();
        if (!MemoryStore.TopicSlug.IsMatch(slug))
            throw new McpException("topic must be a slug: lowercase letters, digits and hyphens.");
        var target = string.IsNullOrWhiteSpace(replaces) ? null : replaces.Trim();
        var titles = memory.Titles(slug);
        if (target is not null && !titles.Contains(target, StringComparer.Ordinal))
            throw new McpException($"No entry titled '{target}' in topic '{slug}'. Titles: {(titles.Count == 0 ? "(none)" : string.Join(", ", titles.Take(20)))}.");
        // Order (critique P1-17a): the "memory already holds it" refusal first, so a repeat of a pending
        // proposal for a title the file already has is told about replaces rather than handed a duplicate.
        if (target is null && titles.Contains(entryTitle.Trim(), StringComparer.Ordinal))
            throw new McpException($"Memory already holds '{entryTitle.Trim()}' in topic '{slug}'. To change it, propose again with replaces set to that title.");
        if (proposals.FindPending(slug, entryTitle) is { } pending)
            return JsonSerializer.Serialize(new { pending.Id, pending.RoomId, pending.AuthorId, pending.Topic, pending.Title, pending.Status, Duplicate = true }, JsonOptions);
        var flags = ProposalFlags.Compute(entryBody, store.GetRoom(room_id)?.Directory is not null);
        MemoryProposal proposal;
        try { proposal = proposals.Create(room_id, me, slug, entryTitle, entryBody, null, target, flags); }
        catch (ArgumentException e) { throw new McpException(e.Message); }
        // The row is the proposal; the note is its announcement. A note that fails must not turn into a
        // tool error that invites a retry and a duplicate row (critique pass 2, P2-8).
        try { HubNotes.Post(store, signal, room_id, HubNotes.Proposed(proposal)); }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"memory: proposal #{proposal.Id} note not posted ({e.GetType().Name}: {e.Message})"); }
        return JsonSerializer.Serialize(new { proposal.Id, proposal.RoomId, proposal.AuthorId, proposal.Topic, proposal.Title, proposal.Status, proposal.Kind, proposal.Replaces, Flags = flags is null ? null : ProposalFlags.Parse(flags) }, JsonOptions);
    }

    [McpServerTool(Name = "propose_rewrite", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Propose a consolidated version of ONE memory topic: the whole file, rewritten. Read the topic with recall first. Fold duplicates and contradictions, keep every distinct fact, invent nothing, keep one '## ' heading per entry, and keep a surviving entry's heading exactly as it was. The owner sees it as a diff against the current file, with the entries it would remove named, and approves or rejects it; nothing changes until then. Use propose_memory for a single new fact — this replaces everything in the topic.")]
    public string ProposeRewrite(
        [Description("Room id the proposal belongs to, e.g. \"general\".")] string room_id,
        [Description("Topic slug: lowercase letters, digits, hyphens (e.g. \"user\"); the topic must already have a file.")] string topic,
        [Description("The whole replacement file text: an H1, then one '## ' heading per surviving entry, markdown, up to the topic's cap.")] string body)
    {
        var me = Caller;   // 1. Caller required — same McpException as its siblings.

        // 2. Caller boundary, server-side. Permitted set, named explicitly: model rows hosted by
        // claude, plus every human row (owner and owner-remote hold bearer tokens and call tools like
        // anyone else; a naive Host == "claude" predicate would silently exclude the owner's own
        // ability to file a consolidation). This is defence in depth over an already-approval-gated
        // write (claim 14: --allowedTools shapes a spawn's prompt, it is not a server-side boundary —
        // every roster bearer can already reach every memory tool), not the boundary the argv allowlist
        // appears to promise.
        var caller = participants.List().FirstOrDefault(p => p.Id == me);
        var permitted = caller is not null && (caller.Kind == "human" || (caller.Kind == "model" && caller.Host == "claude"));
        if (!permitted)
            throw new McpException($"propose_rewrite is not available to {caller?.Host ?? "unknown"} participants.");

        // 3. Room must exist — reuse the existing literal.
        if (!store.RoomExists(room_id)) throw new McpException($"Unknown room '{room_id}'. Call list_rooms.");

        // 4. RequireSlug; the topic must have a file — reuse recall's literal.
        var slug = (topic ?? "").Trim();
        if (!MemoryStore.TopicSlug.IsMatch(slug))
            throw new McpException("topic must be a slug: lowercase letters, digits and hyphens.");
        var entryBody = body ?? "";
        var current = memory.ReadTopic(slug)
            ?? throw new McpException($"No topic '{slug}'. Topics: {Names()}.");

        // 5. Refuse a truncated read: a proposer that folded only what it saw would propose deleting
        // the tail it never read.
        if (current.Truncated)
            throw new McpException($"Topic '{slug}' is {current.FullChars} characters, past the {MemoryStore.TopicChars} a proposer can read. Split it by hand before consolidating.");

        // 6. ValidateRewrite is a cheap floor only; ProjectedRewriteChars is the authoritative cap check
        // (pass 2 finding A) — it is the only one that can see the carried-forward provenance. The
        // provenance string here mirrors the shape MemoryApi.Approve composes at approval time; the
        // real proposal id is not known until Create returns, but approval re-checks the real string
        // regardless (T4), so this is a preliminary refusal, not the final word.
        try { MemoryStore.ValidateRewrite(slug, entryBody); }
        catch (ArgumentException e) { throw new McpException(e.Message); }
        var provisionalProvenance = $"approved {Timestamps.Stamp(DateTimeOffset.UtcNow)} proposal 0 by {me} in room {room_id}";
        var cap = slug == MemoryStore.CoreTopic ? MemoryStore.CoreChars : MemoryStore.TopicChars;
        var projected = memory.ProjectedRewriteChars(slug, entryBody, provisionalProvenance);
        if (projected > cap)
            throw new McpException($"body would produce a {projected}-character file for topic '{slug}', over the {cap}-character cap.");

        // 7. Refuse a second pending rewrite rather than deduplicating it (pass 2 finding C): the
        // generated title is invariant for this kind, so title-dedup would hand back proposal #1's body
        // as a "duplicate" of a second, better one, and the owner would approve the wrong text.
        var title = $"Consolidate {slug}";
        if (proposals.FindPending(slug, title) is { } pending)
            throw new McpException($"A rewrite of '{slug}' is already pending (#{pending.Id}); reject it before proposing another.");

        // 8. Flags, create, announce, return the shape propose_memory returns.
        var flags = ProposalFlags.Compute(entryBody, store.GetRoom(room_id)?.Directory is not null);
        MemoryProposal proposal;
        try { proposal = proposals.Create(room_id, me, slug, title, entryBody, null, null, flags, MemoryProposalStore.KindRewrite); }
        catch (ArgumentException e) { throw new McpException(e.Message); }
        try { HubNotes.Post(store, signal, room_id, HubNotes.Proposed(proposal)); }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"memory: proposal #{proposal.Id} note not posted ({e.GetType().Name}: {e.Message})"); }
        return JsonSerializer.Serialize(new { proposal.Id, proposal.RoomId, proposal.AuthorId, proposal.Topic, proposal.Title, proposal.Status, proposal.Kind, proposal.Replaces, Flags = flags is null ? null : ProposalFlags.Parse(flags) }, JsonOptions);
    }

    private string Names()
    {
        var names = memory.ListTopics().Select(t => t.Slug).ToList();
        return names.Count == 0 ? "(none yet)" : string.Join(", ", names);
    }
}
