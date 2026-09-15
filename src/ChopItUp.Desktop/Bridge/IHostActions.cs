using ChopItUp.Desktop.Hub;

namespace ChopItUp.Desktop.Bridge;

/// <summary>Row 12: everything the hosted page is allowed to ask the window for. <c>MainWindow</c>
/// implements it (Task 4); Task 5's <c>HostBridge</c> is a pure function from a JSON message onto these
/// six members, which is what lets the whole wire protocol be tested without a window.
///
/// Deliberately small. The page draws the chrome, so it needs the three window verbs plus the state to
/// label them with; it does NOT get file pickers, shell execution or a way to reach the data dir. Every
/// widening of this interface widens what a page served over loopback can do to the desktop.</summary>
public interface IHostActions
{
    /// <summary>Which glyph the middle button should show, and what <c>toggleMaximize</c> means.</summary>
    bool IsMaximized { get; }

    /// <summary>What the hub chip says. Read live from <c>HubChild</c>, never cached by the bridge: an
    /// attached hub may be on a port that is not the one the shell was asked for (B3).</summary>
    HubStatus Hub { get; }

    void Minimize();
    void Maximize();
    void Restore();

    /// <summary>Hides to the tray. The hub keeps running and the tray icon stays (AC4); this is not a
    /// quit, and the window comes back with its bounds unchanged.</summary>
    void Close();

    /// <summary>Stops the hub this shell started and exits (AC5). Kills, does not ask (B4).</summary>
    void Quit();
}
