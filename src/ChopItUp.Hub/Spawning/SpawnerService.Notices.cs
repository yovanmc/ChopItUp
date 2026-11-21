using ChopItUp.Core.Model;
using ChopItUp.Hub.Realtime;

namespace ChopItUp.Hub.Spawning;

/// <summary>Run notices for the desktop shell's tray notifications (<see cref="RunNoticeBus"/>):
/// one when a run presents, parks or is held for a usage limit, raised beside the room note that says
/// the same thing.</summary>
public sealed partial class SpawnerService
{
    private readonly RunNoticeBus _notices;

    private void Notify(string kind, Run run, string text) =>
        _notices.Publish(new RunNotice(kind, run.RoomId, _store.GetRoom(run.RoomId)?.Name ?? run.RoomId, run.Id, text));

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
