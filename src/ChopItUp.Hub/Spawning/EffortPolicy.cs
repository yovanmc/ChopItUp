using ChopItUp.Core.Model;

namespace ChopItUp.Hub.Spawning;

/// <summary>The run-effort rule, in one place so the Roles dialog can show the effort a row's
/// classes earn without a second copy of the rule drifting from the launch site.
/// <see cref="ForClasses"/> is the configured half: a <c>judge</c>-class row is spawned at
/// <see cref="Raised"/> inside a run, any other row gets no flag at all (null), so its CLI's own
/// default applies. <see cref="AtLaunch"/> is what <c>SpawnerService.Launch</c> applies: the same,
/// gated on an active run, plus the run's conductor whatever its classes. Never <c>xhigh</c> or
/// <c>max</c>.</summary>
public static class EffortPolicy
{
    public const string Raised = "high";

    public static string? ForClasses(Participant p) =>
        ParticipantClasses.Has(p, ParticipantClasses.Judge) ? Raised : null;

    public static string? AtLaunch(Participant p, bool inRun, bool conductor) =>
        inRun && (conductor || ForClasses(p) is not null) ? Raised : null;
}
