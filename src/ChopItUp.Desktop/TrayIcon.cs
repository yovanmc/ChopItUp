using ChopItUp.Desktop.Hub;

namespace ChopItUp.Desktop;

/// <summary>The tray icon and its three-item menu (Open, a disabled status line, Quit) plus
/// double-click = Open. Untested beyond construction: a real <c>NotifyIcon</c> needs the notification
/// area, which the automated tests don't have; the UIA harness drives Open/Quit through
/// <see cref="SingleInstance"/>'s events instead and reads visibility from the shell log.
///
/// <see cref="Update"/> is called ONLY from App's <c>hub.StatusChanged</c> subscriber, already hopped
/// to the UI thread with <c>Dispatcher.BeginInvoke</c>: <c>NotifyIcon</c> and <c>ToolStripMenuItem</c>
/// are not thread-safe. App calls <c>System.Windows.Forms.Application.EnableVisualStyles()</c> once,
/// before this is constructed.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _status;
    private readonly System.Drawing.Icon _glyph;
    private Action? _onNoticeClick;

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

        // The shipped icon, read from this assembly's WPF resources: the same chopitup.ico the exe
        // carries as its Win32 icon and MainWindow shows. The size argument picks the .ico's
        // 16 px entry, so the notification area gets a layer drawn for that size instead of a
        // downscaled 256 one. GetResourceStream returns null only if the Resource item is missing from
        // the csproj, which is a build mistake, not a runtime condition.
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/chopitup.ico"))
            ?? throw new InvalidOperationException("chopitup.ico is not a Resource in ChopItUp.Desktop.");
        _glyph = new System.Drawing.Icon(resource.Stream, System.Windows.Forms.SystemInformation.SmallIconSize);

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _glyph,
            Text = "Chop It Up",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => onOpen();
        // A balloon renders as a Windows notification. Only the newest one is clickable, so the click
        // action is the newest notice's and a closed balloon forgets it.
        _icon.BalloonTipClicked += (_, _) => { var click = _onNoticeClick; _onNoticeClick = null; click?.Invoke(); };
        _icon.BalloonTipClosed += (_, _) => _onNoticeClick = null;
    }

    /// <summary>Shows one notification; a click on it runs <paramref name="onClick"/>.</summary>
    public void ShowNotice(string title, string text, Action onClick)
    {
        _onNoticeClick = onClick;
        _icon.ShowBalloonTip(10_000, title, text.Length == 0 ? " " : text, System.Windows.Forms.ToolTipIcon.Info);
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
        _glyph.Dispose();
    }
}
