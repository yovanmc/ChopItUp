using ChopItUp.Desktop.Hub;

namespace ChopItUp.Desktop;

/// <summary>Row 12 T5 (B9): the tray icon and its three-item menu — Open, a disabled status line, Quit
/// — plus double-click = Open. Untested beyond construction: a real <c>NotifyIcon</c> needs the
/// notification area, which the automated tests don't have; the harness (Task 9) drives Open/Quit
/// through <see cref="SingleInstance"/>'s events instead and reads visibility from the shell log.
///
/// <see cref="Update"/> is called ONLY from App's <c>hub.StatusChanged</c> subscriber, already hopped
/// to the UI thread with <c>Dispatcher.BeginInvoke</c> — <c>NotifyIcon</c> and <c>ToolStripMenuItem</c>
/// are not thread-safe (pass 1, finding 19). App calls
/// <c>System.Windows.Forms.Application.EnableVisualStyles()</c> once, before this is constructed.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _status;

    public TrayIcon(Action onOpen, Action onQuit)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        var open = new System.Windows.Forms.ToolStripMenuItem("Open");
        open.Click += (_, _) => onOpen();
        menu.Items.Add(open);

        _status = new System.Windows.Forms.ToolStripMenuItem("Starting…") { Enabled = false };
        menu.Items.Add(_status);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var quit = new System.Windows.Forms.ToolStripMenuItem("Quit");
        quit.Click += (_, _) => onQuit();
        menu.Items.Add(quit);

        _icon = new System.Windows.Forms.NotifyIcon
        {
            // Task 6 replaces this with the shipped chopitup.ico resource.
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Chop It Up",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => onOpen();
    }

    public void Update(HubStatus status)
    {
        _status.Text = status.State switch
        {
            HubState.Ready => $"Hub running on :{status.Port}",
            HubState.Attached => $"Attached to :{status.Port}",
            HubState.Failed => "Hub failed",
            _ => "Starting…",
        };
    }

    /// <summary>Visible = false BEFORE Dispose: a NotifyIcon disposed while still visible leaves a
    /// ghost icon in the tray until the mouse happens to pass over it.</summary>
    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
