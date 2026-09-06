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
     Description("Read the shared memory every participant uses. With no topic: the core (what every model should know) and the list of topics. With a topic: that topic's text. Read it before answering anything about the owner or their work, and before proposing a memory.")]
    public string Recall(
        [Description("A topic slug from the list, e.g. \"user\". Omit for the core and the topic list; \"core\" returns the whole core file uncut; a topic is cut at 24000 characters and says so.")] string? topic = null)
    {
        _ = Caller;
        if (string.IsNullOrWhiteSpace(topic))
        {
            var core = memory.ReadCore();
            return JsonSerializer.Serialize(new
            {
                Core = core.Text,
                Truncated = core.Truncated ? true : (bool?)null,
                Topics = memory.ListTopics().Select(t => new { t.Slug, t.Bytes }),
            }, JsonOptions);
        }
        var slug = topic.Trim().ToLowerInvariant();
        if (!MemoryStore.TopicSlug.IsMatch(slug))
            throw new McpException("topic must be a slug: lowercase letters, digits and hyphens.");
        var text = memory.ReadTopic(slug)
            ?? throw new McpException($"No topic '{slug}'. Topics: {Names()}.");
        return JsonSerializer.Serialize(new { Topic = slug, text.Text, Truncated = text.Truncated ? true : (bool?)null, Chars = text.FullChars }, JsonOptions);
    }

    [McpServerTool(Name = "propose_memory", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Propose one durable fact for the shared memory: something about the owner or the work that memory does not already say and that every future model should know. The hub records you as the proposer and announces it in the room; the owner approves or rejects it there. Nothing is remembered until approved. Propose once per fact, never per message.")]
    public string ProposeMemory(
        [Description("Room id the proposal belongs to, e.g. \"general\".")] string room_id,
        [Description("Topic slug: lowercase letters, digits, hyphens (e.g. \"user\", \"project\"); \"core\" for the always-injected core. New topics are fine.")] string topic,
        [Description("One line, up to 120 characters: the fact as a heading.")] string title,
        [Description("The fact itself, markdown, up to 4000 characters.")] string body)
    {
        var me = Caller;
        if (!store.RoomExists(room_id)) throw new McpException($"Unknown room '{room_id}'. Call list_rooms.");
        var slug = (topic ?? "").Trim().ToLowerInvariant();
        MemoryProposal proposal;
        try { proposal = proposals.Create(room_id, me, slug, title, body, null); }
        catch (ArgumentException e) { throw new McpException(e.Message); }
        // The row is the proposal; the note is its announcement. A note that fails must not turn into a
        // tool error that invites a retry and a duplicate row (critique pass 2, P2-8).
        try { HubNotes.Post(store, signal, room_id, HubNotes.Proposed(proposal)); }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"memory: proposal #{proposal.Id} note not posted ({e.GetType().Name}: {e.Message})"); }
        return JsonSerializer.Serialize(new { proposal.Id, proposal.RoomId, proposal.AuthorId, proposal.Topic, proposal.Title, proposal.Status }, JsonOptions);
    }

    private string Names()
    {
        var names = memory.ListTopics().Select(t => t.Slug).ToList();
        return names.Count == 0 ? "(none yet)" : string.Join(", ", names);
    }
}
