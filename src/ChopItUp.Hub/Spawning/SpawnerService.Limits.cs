using System.Globalization;
using ChopItUp.Core.Model;

namespace ChopItUp.Hub.Spawning;

/// <summary>Usage limits. A spawn that ended without posting is read for a limit line
/// (<see cref="UsageLimits"/>). In a run, the run parks like any park (its open exchange stops and
/// the room's spawns are cancelled; a run room is exclusive) with the spawn refunded and a resume
/// instant two minutes past the reset, and <see cref="ResumeDue"/> reopens it then. Outside a run,
/// the participant goes back into its exchange's pending set, gated until that instant, with its
/// turn refunded, once per participant per exchange. A limit with no reset time, or a run held three
/// times on one stated reset, waits for the owner.</summary>
public sealed partial class SpawnerService
{
    internal static readonly TimeSpan ResumeGrace = TimeSpan.FromMinutes(2);
    private const int MaxHoldsOnOneReset = 3;
    // Keyed on the limit line itself: a CLI that still names the same reset after it passed gives a
    // new parsed instant each time (the not-cleared retry), but the same line.
    private readonly Dictionary<long, (string Line, int Count)> _holds = new();

    private UsageLimitHit? DetectLimit(SpawnHandle h, ProcessResult r) =>
        UsageLimits.Detect(h.Participant.Host, r.StandardOutput, r.StandardError, _clock.LocalTimeZone, _clock.GetUtcNow());

    private string? KeepRawOutput(SpawnHandle h, ProcessResult r) =>
        SpawnFailureLog.Write(_options.DataDir, h.SpawnId, h.Participant.Id, h.Participant.Host, r.ExitCode,
            r.StandardOutput, r.StandardError, text => h.Token.Length > 0 ? Scrub(text, h.Token) : text, _clock.GetUtcNow());

    private static string RawNote(string? raw) => raw is null ? "" : $" Raw output: {raw} in the hub's data folder.";

    private static string LimitText(SpawnHandle h, UsageLimitHit hit) =>
        Truncate(StripAnsi(h.Token.Length > 0 ? Scrub(hit.Text, h.Token) : hit.Text), 300);

    /// <summary>The note for a limit that is not retried outside a run: it has no reset time, its
    /// exchange has ended, or the participant was already asked again once in this exchange.</summary>
    private static string LimitNotRetriedNote(string id, string text, UsageLimitHit hit, Exchange exchange, string? raw) =>
        hit.ResetAt is null ? $"@{id} hit a usage limit (\"{text}\") with no reset time given, so it is not asked again.{RawNote(raw)}"
        : exchange.Status != ExchangeStatus.Open ? $"@{id} hit a usage limit (\"{text}\") after its exchange ended, so it is not asked again.{RawNote(raw)}"
        : $"@{id} hit a usage limit again (\"{text}\"), so it is not asked again.{RawNote(raw)}";

    private string LimitRetryNote(string id, string text, DateTimeOffset notBefore, string? raw) =>
        $"@{id} hit a usage limit (\"{text}\"). Asking it again at {LocalTime(notBefore)}, two minutes after the reset.{RawNote(raw)}";

    /// <summary>Holds what <paramref name="hit"/> stopped. True when the hold took the spawn's place
    /// entirely, so the caller posts no failed-spawn note and does not finish it.</summary>
    private bool HoldForLimit(SpawnHandle h, UsageLimitHit hit, string? raw)
    {
        var room = h.Request.RoomId;
        var id = h.Participant.Id;
        var now = _clock.GetUtcNow();
        var text = LimitText(h, hit);
        if (!h.Exchange.Joinable && _runs.Active(room) is { } run)
        {
            var count = _holds.TryGetValue(run.Id, out var last) && last.Line == hit.Text ? last.Count + 1 : 1;
            _holds[run.Id] = (hit.Text, count);
            DateTimeOffset? resumeAt = hit.ResetAt is { } reset && count <= MaxHoldsOnOneReset ? reset + ResumeGrace : null;
            var limit = $"@{id} hit a usage limit (\"{Truncate(text, 120)}\")";
            _runs.Hold(run.Id, resumeAt is null ? limit : $"{limit}; resumes at {LocalTime(resumeAt.Value)}", resumeAt, now);
            h.Exchange.InFlight.Remove(id);
            if (Newest(room) is { Status: ExchangeStatus.Open } x) PostNote(room, ExchangePolicy.Stop(x, ExchangeStopCause.Run));
            foreach (var other in _inFlight.Values.Where(o => o.Request.RoomId == room).ToList()) other.Cancel.Cancel();
            var why = hit.ResetAt is null ? "with no reset time given" : $"{count} times on the same reset";
            PostNote(room, resumeAt is { } at
                ? $"Run #{run.Id} held: @{id} hit a usage limit (\"{text}\"). It resumes at {LocalTime(at)}, two minutes after the reset; a post resumes it sooner.{RawNote(raw)}"
                : $"Run #{run.Id} parked: @{id} hit a usage limit (\"{text}\") {why}. Post to resume. @{_owner.Id}{RawNote(raw)}");
            return true;
        }
        var exchange = h.Exchange;
        if (hit.ResetAt is not { } due || exchange.Status != ExchangeStatus.Open || !exchange.LimitRetried.Add(id))
        {
            PostNote(room, LimitNotRetriedNote(id, text, hit, exchange, raw));
            return false;
        }
        Requeue(exchange, id, h.Request, due + ResumeGrace, now);
        PostNote(room, LimitRetryNote(id, text, due + ResumeGrace, raw));
        return true;
    }

    /// <summary>Puts <paramref name="participantId"/> back into <paramref name="exchange"/>'s pending
    /// set, launchable no sooner than <paramref name="notBefore"/>, and refunds the turn its launch
    /// took. It leaves the in-flight set first, or a worktree exchange would wait on it forever.</summary>
    private static void Requeue(Exchange exchange, string participantId, SpawnRequest request, DateTimeOffset notBefore, DateTimeOffset now)
    {
        exchange.InFlight.Remove(participantId);
        exchange.TurnsStarted = Math.Max(0, exchange.TurnsStarted - 1);
        var pending = new PendingSpawn { LastTriggerAt = now, Reason = request.Reason, RefusedAt = request.RefusedAt, NotBefore = notBefore };
        pending.TriggerIds.AddRange(request.TriggerIds);
        exchange.Pending[participantId] = pending;
    }

    private string LocalTime(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, _clock.LocalTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " hub time";

    /// <summary>Every held run whose resume instant has passed is decided like an owner post on a
    /// parked run: it reopens its conductor, or, over budget, gets the cap-spent line and stays parked.
    /// The instant is cleared whatever the decision, so a past instant never spins the wake.</summary>
    private void ResumeDue()
    {
        var now = _clock.GetUtcNow();
        while (_runs.NextResume() is { ResumeAt: { } at } held && at <= now)
        {
            _runs.ClearResume(held.Id);
            // A note that failed to post still resumes the run, triggered by its root message.
            var trigger = PostNote(held.RoomId, $"Run #{held.Id}: the usage-limit reset passed.") ?? held.RootMessageId;
            DriveRun(_runs.ById(held.Id)!, new RunEvent.HumanPosted(trigger));
        }
    }
}
