using System.Text.Json;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Skills;
using ChopItUp.Core.Storage;

namespace ChopItUp.Hub.Spawning;

public sealed record DispatchPreview(string Quote, string Mode, IReadOnlyList<string> Participants, int? Turns, string? Commit, string? Error = null);
public sealed class StaleDispatchException(string message) : Exception(message);

public sealed partial class SpawnerService
{
    private sealed record QuoteEntry(string Room, string Author, string Body, long? Reply, string State, string? Commit, DateTimeOffset Expires);
    private sealed record AdmissionCapture(string State, string? Directory, RoomModeSettings? Mode, bool Steer, PostResult? Retry = null);
    private readonly Dictionary<string, QuoteEntry> _quotes = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string?> _admittedCommits = new();

    private string AdmissionState(string roomId) => JsonSerializer.Serialize(new
    {
        Room = _store.GetRoom(roomId) is { } room ? new { room.Directory, room.ArchivedAt, Mode = room.EffectiveMode } : null,
        Run = _runs.Latest(roomId),
        Exchanges = ExchangesIn(roomId).Select(x => new { x.RootMessageId, x.Status, x.TurnsStarted, x.TurnsCommitted, InFlight = x.InFlight.ToArray(), Pending = x.Pending.Keys.ToArray() }),
    });

    private Exchange? ModeTarget(string roomId, string body, long? replyTo) => replyTo is { } reply ? JoinableFor(roomId, reply)
        : ExchangeCommands.IsContinue(body) ? ExchangesIn(roomId).LastOrDefault(x => x.Joinable)
            ?? (_joinable.TryGetValue(roomId, out var remembered) ? remembered.LastOrDefault() : null) : null;

    private static bool FinishingMode(Exchange? target) => target?.ModeLeg is not null
        && (target.Status == ExchangeStatus.Open || target.InFlight.Count != 0 || target.ModeLeg.Preparing);

    private RoomModeSettings? AdmissionMode(string roomId, string body, long? replyTo)
    {
        if (_runs.Active(roomId) is not null || _runs.Latest(roomId) is { Status: RunStatus.Parked }) return null;
        var mentions = new Mentions(_roster.Select(p => p.Id)).Leading(body);
        if (mentions.Recipients.Count != 0 || mentions.Unknown.Count != 0) return null;
        var continued = ExchangeCommands.IsContinue(body);
        if (body.TrimStart().StartsWith('/') && !continued) return null;
        if (replyTo is not null || continued)
        {
            var target = ModeTarget(roomId, body, replyTo);
            return FinishingMode(target) ? null : target?.ModeLeg?.Settings;
        }
        return _store.GetRoom(roomId)?.EffectiveMode;
    }

    private AdmissionCapture CaptureAdmission(string roomId, string body, long? replyTo)
    {
        var room = _store.GetRoom(roomId) ?? throw new ArgumentException("Unknown room.");
        var mode = AdmissionMode(roomId, body, replyTo);
        mode?.Validate(_roster);
        var turns = new Mentions(_roster.Select(p => p.Id)).Leading(body);
        if (mode is not null && turns.Turns != TurnsToken.None && turns.TurnsValue < mode.Turns)
            throw new ArgumentException($"{mode.Mode} requires {mode.Turns} turns.");
        return new(AdmissionState(roomId), room.Directory, mode, FinishingMode(ModeTarget(roomId, body, replyTo)));
    }

    public async Task<DispatchPreview> PreviewAsync(string roomId, string author, string body, long? replyTo, CancellationToken cancellation = default)
    {
        AdmissionCapture capture;
        try { capture = await InLoopAsync(() => CaptureAdmission(roomId, body, replyTo)); }
        catch (ArgumentException e) { return new("", "unavailable", [], null, null, e.Message); }
        string? commit = null, error = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _modeShutdown.Token);
        try
        {
            if (capture.Mode?.Mode == "panel" && capture.Directory is not null)
                commit = await PanelReadiness(capture.Directory, linked.Token);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OperationCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { error = e.Message; }
        return await InLoopAsync(() =>
        {
            if (capture.State != AdmissionState(roomId)) error = "Dispatch changed while checking. Refresh the preview.";
            var now = _clock.GetUtcNow();
            foreach (var key in _quotes.Where(q => q.Value.Expires <= now).Select(q => q.Key).ToArray()) _quotes.Remove(key);
            while (_quotes.Count >= 256) _quotes.Remove(_quotes.Keys.First());
            var quote = Guid.NewGuid().ToString("N");
            if (error is null) _quotes[quote] = new(roomId, author, body, replyTo, capture.State, commit, now.AddMinutes(2));
            return new DispatchPreview(quote, capture.Steer ? "steer (no new turn)" : capture.Mode?.Mode ?? "explicit/command",
                capture.Mode?.Participants ?? [], capture.Steer ? 0 : capture.Mode?.Turns, commit, error);
        });
    }

    private PostResult? ExistingOwnerRetry(string roomId, string author, string body, string? clientKey, long? replyTo)
    {
        if (clientKey is null || _store.FindClientMessage(author, clientKey) is not { } existing) return null;
        if (existing.RoomId != roomId || existing.Body != body || existing.ReplyToId != replyTo)
            throw new StaleDispatchException("This retry key belongs to a different message.");
        return new(existing, true);
    }

    private QuoteEntry? ValidateQuote(string? quote, string roomId, string author, string body, long? replyTo)
    {
        if (quote is null) return null;
        if (!_quotes.TryGetValue(quote, out var entry) || entry.Expires <= _clock.GetUtcNow()
            || entry.Room != roomId || entry.Author != author || entry.Body != body || entry.Reply != replyTo || entry.State != AdmissionState(roomId))
            throw new StaleDispatchException("Dispatch changed. Your draft is preserved. Refresh the preview and press Send again.");
        return entry;
    }

    /// <summary>Unquoted authenticated API/MCP callers retain compatibility but make no earlier-preview claim.</summary>
    public async Task<PostResult> AdmitOwnerAsync(string roomId, string author, string body, string? clientKey, long? replyTo, string? quote = null, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("body is empty.", nameof(body));
        clientKey = string.IsNullOrWhiteSpace(clientKey) ? null : clientKey.Trim();
        if (clientKey?.Length > MessageStore.MaxClientKeyChars) throw new ArgumentException("client_key is too long.");
        var capture = await InLoopAsync(() =>
        {
            if (ExistingOwnerRetry(roomId, author, body, clientKey, replyTo) is { } retry) return new AdmissionCapture("", null, null, false, retry);
            ValidateQuote(quote, roomId, author, body, replyTo);
            var captured = CaptureAdmission(roomId, body, replyTo);
            return captured.Mode?.Mode == "panel" && captured.Directory is not null ? captured : captured with { Retry = Commit(captured, null) };
        });
        if (capture.Retry is { } already) return already;
        string? commit = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _modeShutdown.Token);
        if (capture.Mode?.Mode == "panel" && capture.Directory is not null)
        {
            try { commit = await PanelReadiness(capture.Directory, linked.Token); }
            catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { throw new StaleDispatchException(e.Message); }
        }
        PostResult Commit(AdmissionCapture current, string? verifiedCommit)
        {
            if (ExistingOwnerRetry(roomId, author, body, clientKey, replyTo) is { } retry) return retry;
            var entry = ValidateQuote(quote, roomId, author, body, replyTo);
            if (current.State != AdmissionState(roomId)) throw new StaleDispatchException("Dispatch changed while checking. Send again after reviewing the preview.");
            if (entry is not null && verifiedCommit != entry.Commit) throw new StaleDispatchException("Repository changed since preview. Refresh and press Send again.");
            var result = _store.Post(roomId, author, body, clientKey, replyTo);
            if (quote is not null) _quotes.Remove(quote);
            if (!result.Deduplicated)
            {
                _admittedCommits[result.Message.Id] = verifiedCommit;
                _pendingAnnouncements.Add(result.Message.Id);
                // The queued signal cannot run until this serialized admission finishes.
                _signal.Publish(roomId, result.Message);
                try { OnMessage(result.Message); }
                finally { _admittedCommits.Remove(result.Message.Id); }
            }
            return result;
        }
        return await InLoopAsync(() => Commit(capture, commit));
    }
}
