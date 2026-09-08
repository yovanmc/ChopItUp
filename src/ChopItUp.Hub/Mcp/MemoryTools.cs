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
/// participant, stamped like a message author.</summary>
[McpServerToolType]
public sealed class MemoryTools(MemoryStore memory, MemoryProposalStore proposals, MessageStore store, MessageSignal signal, IHttpContextAccessor http)
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

    private string Names()
    {
        var names = memory.ListTopics().Select(t => t.Slug).ToList();
        return names.Count == 0 ? "(none yet)" : string.Join(", ", names);
    }
}
