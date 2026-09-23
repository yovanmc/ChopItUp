using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ChopItUp.Desktop.Bridge;
using ChopItUp.Desktop.Hub;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ChopItUp.Desktop;

/// <summary>The chromeless shell window. WindowChrome contributes only the resize border and the
/// maximize clamp; the WebView2 is the single child and the page inside it draws the only visible
/// chrome (the client's <c>ChromeBar</c>, or <see cref="BootPage"/> before the client loads).
///
/// Three things happen here that nothing else can do:
/// navigation is locked to the hub origin, and everything else opens in the default browser;
/// the launch-scoped owner bearer is registered as a per-document script on that origin, and only
/// when this shell started the hub; and the window hides instead of closing.
///
/// Untestable by construction (it needs an STA thread, a real WebView2 and a real hub), which is why
/// every piece that can be pure is pure: <see cref="BootPage"/>, <see cref="OwnerTokenScript"/> and
/// <c>HostBridge</c> are all tested without a window.</summary>
public partial class MainWindow : System.Windows.Window, IHostActions
{
    private readonly ShellArgs _args;
    private readonly HubChild _hub;
    private readonly ShellLog _log;

    /// <summary>WPF can raise Loaded again (a hidden-then-shown window re-entering the tree), and
    /// initialising WebView2 twice throws. Set before the first await, so a re-entrant call returns.</summary>
    private bool _initialized;

    /// <summary>Set once the window has left the boot page for the hub's own client. Terminal statuses
    /// can arrive more than once (HubChild raises Ready, then the exit watcher raises Failed, and
    /// App replays the current status when the window is built), and navigating twice would reload the
    /// client out from under the owner. Failed clears it, so a hub that comes up later still navigates.</summary>
    private bool _navigated;

    /// <summary>Raised just before each <c>NavigateToString</c> and spent by the first boot page that
    /// comes back through <see cref="OnNavigationStarting"/>. It is what lets the navigation lock tell
    /// the page THIS window wrote from a <c>data:</c> URI arriving in a message (see
    /// <see cref="NavigationPolicy"/>).</summary>
    private bool _bootPagePending;

    public MainWindow(ShellArgs args, HubChild hub, ShellLog log)
    {
        _args = args;
        _hub = hub;
        _log = log;
        InitializeComponent();

        // B11: the profile is regenerable and hundreds of MB. It lives under %LOCALAPPDATA%, keyed by
        // the data dir, NOT inside data\ beside chopitup.db and tokens.json.
        Web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = _args.WebViewProfileDir };

        // Measured: the OS resize edges do not respond over the WebView2's HWND child. This
        // margin is the WPF-owned strip they hit-test against. Kept in step by OnStateChanged.
        Web.Margin = new Thickness(6);

        Loaded += OnLoaded;
    }

    // ===== WebView2 bring-up =================================================================

    /// <summary>async void on purpose (it is an event handler), so its whole body is one try/catch:
    /// an exception escaping here is an unhandled exception on the UI thread, which is a process kill
    /// with no message. A WebView2 that cannot initialise is fatal — there is no UI without it — so
    /// this is the one path that exits non-zero rather than degrading.</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            await Web.EnsureCoreWebView2Async();
            var core = Web.CoreWebView2;

            // This is what turns the page's `app-region: drag` row into OS
            // caption behaviour. It takes effect on the NEXT navigation, so it must be set before the
            // boot page loads. Its own try/catch: losing drag is a degraded window, not a dead one —
            // the page's buttons still work and the owner can still resize and quit.
            try
            {
                core.Settings.IsNonClientRegionSupportEnabled = true;
            }
            catch (Exception ex)
            {
                _log.Append($"NONCLIENT unsupported {ex.GetType().Name} {ex.Message}");
            }

            // This is an application window, not a browser: no page context menu, no status bar.
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += (_, a) => _log.Append($"NAV {a.IsSuccess} {core.Source}");
            core.NewWindowRequested += (_, a) =>
            {
                // A link in a message that asks for a new window is a link, not a second shell.
                a.Handled = true;
                OpenExternal(a.Uri);
            };
            core.WebMessageReceived += OnWebMessage;

            ShowBoot();

            // The hub can reach Ready before WebView2 finishes initialising, in which case the status
            // change already fired into a window that could not act on it. Replay the current one.
            await ApplyHubStatusAsync(_hub.Status);
        }
        catch (Exception ex)
        {
            WebViewFailed(ex);
        }
    }

    private void ShowBoot()
    {
        _bootPagePending = true;
        Web.CoreWebView2?.NavigateToString(BootPage.Starting(App.LaunchNonce));
        _log.Append("BOOT SHOWN");
    }

    /// <summary>Called by App's <c>hub.StatusChanged</c> subscriber after it hops to the UI thread
    /// (StatusChanged fires on a pool thread), and once by <see cref="OnLoaded"/>. Same whole-body
    /// guard as OnLoaded: a COMException from a WebView2 whose browser process died would otherwise
    /// take the shell down from inside an event handler.</summary>
    public async void OnHubStatus(HubStatus status)
    {
        try
        {
            await ApplyHubStatusAsync(status);
        }
        catch (Exception ex)
        {
            WebViewFailed(ex);
        }
    }

    private async Task ApplyHubStatusAsync(HubStatus status)
    {
        var core = Web.CoreWebView2;
        if (core is null) return;   // not initialised yet; OnLoaded replays the status when it is.

        switch (status.State)
        {
            case HubState.Ready:
                if (_navigated) return;
                _navigated = true;
                // Ready means this shell started the hub, so the token exists and the origin is ours.
                // Registered BEFORE the navigation: WebView2 runs document-created scripts on every
                // document from then on, so a reload keeps the credential without anything storing it.
                if (_hub.ShellToken is { Length: > 0 } token)
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(OwnerTokenScript.For(_hub.ResolvedOrigin, token));
                _log.Append($"NAVIGATE {_hub.ResolvedOrigin} started token={(_hub.ShellToken is null ? "no" : "yes")}");
                core.Navigate(_hub.ResolvedOrigin.ToString());
                return;

            case HubState.Attached:
                if (_navigated) return;
                _navigated = true;
                // The shell did not start this hub and holds no credential for it. Nothing is injected
                // and the page shows its paste flow.
                _log.Append($"NAVIGATE {_hub.ResolvedOrigin} attached token=no");
                core.Navigate(_hub.ResolvedOrigin.ToString());
                return;

            case HubState.Failed:
                // Back to a page that can still be moved and dismissed, with the reason and the tail.
                _navigated = false;
                _bootPagePending = true;
                _log.Append("BOOT FAILED");
                core.NavigateToString(BootPage.Failed(status.Reason ?? "the hub did not start", _hub.Tail.Snapshot(), App.LaunchNonce));
                return;

            default:
                return;   // Starting and Stopped: the boot page already says what there is to say.
        }
    }

    private void WebViewFailed(Exception ex)
    {
        _log.Append($"WEBVIEW FAILED {ex.GetType().Name} {ex.Message}");
        System.Windows.MessageBox.Show(
            $"Chop It Up could not start its embedded browser.{Environment.NewLine}{Environment.NewLine}"
            + $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}{Environment.NewLine}"
            + $"WebView2 profile: {_args.WebViewProfileDir}{Environment.NewLine}"
            + $"Log: {_log.Path}",
            "Chop It Up",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
        App.Quitting = true;
        System.Windows.Application.Current?.Shutdown(4);
    }

    // ===== navigation lock ===================================================================

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        switch (NavigationPolicy.Decide(e.Uri, _hub.ResolvedOrigin, _bootPagePending))
        {
            case NavDecision.Allow:
                // One shot: the boot page this window just wrote has arrived, so the next data: URI —
                // one that could only have come from the page — is judged with the flag down again.
                if (_bootPagePending && NavigationPolicy.IsBootPageUri(e.Uri))
                {
                    _bootPagePending = false;
                    // The URI carries the whole page base64-encoded; log the shape, not the payload.
                    _log.Append($"NAV BOOT {Describe(e.Uri)}");
                }
                return;

            case NavDecision.OpenExternally:
                e.Cancel = true;
                _log.Append($"NAV EXTERNAL {e.Uri}");
                OpenExternal(e.Uri);
                return;

            default:
                // data: outside the boot window, file:, ms-appx:, anything else a message could carry.
                e.Cancel = true;
                _log.Append($"NAV BLOCKED {e.Uri}");
                return;
        }
    }

    private static string Describe(string uri) =>
        uri.Length <= 48 ? uri : string.Concat(uri.AsSpan(0, 48), "...");

    // ===== page <-> host bridge ==============================================================

    /// <summary>Trust-checks with <see cref="HostBridge.IsTrusted"/> before touching the message at
    /// all (only the hub origin, or the boot page carrying this launch's nonce), then dispatches
    /// through the pure <see cref="HostBridge.Handle"/> and posts its reply back, followed by a fresh
    /// state event so a toggleMaximize's own glyph update does not have to wait for OnStateChanged.</summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = e.WebMessageAsJson;
        if (!HostBridge.IsTrusted(e.Source, _hub.ResolvedOrigin, HostBridge.ExtractNonce(json), App.LaunchNonce))
        {
            _log.Append($"BRIDGE UNTRUSTED source={e.Source}");
            return;
        }

        var core = Web.CoreWebView2;
        if (core is null) return;
        var dispatched = HostBridge.HandleDetailed(json, this);
        core.PostWebMessageAsJson(dispatched.Reply);
        _log.Append($"BRIDGE cmd={dispatched.Cmd} ok={dispatched.Ok}");
        core.PostWebMessageAsJson(HostBridge.StateEvent(this));
    }

    private void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _log.Append($"EXTERNAL {url}");
        }
        catch (Exception ex)
        {
            _log.Append($"EXTERNAL FAILED {url} {ex.GetType().Name} {ex.Message}");
        }
    }

    // ===== window lifetime ===================================================================

    /// <summary>Close hides. The hub, the tray icon and this window's bounds all survive; only Quit
    /// (which sets <see cref="App.Quitting"/>) lets the window actually close.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (App.Quitting) return;
        e.Cancel = true;
        Hide();
        _log.Append("HIDE");
    }

    /// <summary>Open, from the tray menu, a tray double-click, or a second launch.</summary>
    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // An activated WPF window does not hand keyboard focus to the WebView2's HWND by itself, so
        // the composer would not take a keystroke until the owner clicked it.
        Web.Focus();
        _log.Append("SHOW");
    }

    // ===== IHostActions ======================================================================
    // Close and Quit go through Dispatcher.BeginInvoke so the bridge's reply reaches the page before
    // the window goes away: nothing tears the WebView2 down from inside its own event handler.

    public bool IsMaximized => WindowState == WindowState.Maximized;

    public HubStatus Hub => _hub.Status;

    public void Minimize() => SystemCommands.MinimizeWindow(this);

    public void Maximize() => SystemCommands.MaximizeWindow(this);

    public void Restore() => SystemCommands.RestoreWindow(this);

    void IHostActions.Close() => Dispatcher.BeginInvoke(() =>
    {
        Hide();
        _log.Append("HIDE");
    });

    public void Quit() => Dispatcher.BeginInvoke(() =>
    {
        App.Quitting = true;
        _ = ((App)System.Windows.Application.Current).QuitAsync();
    });

    // ===== frameless-chrome plumbing =========================================================
    // Same shape as Curio.Shell/MainWindow.xaml.cs, proven on this machine.

    /// <summary>Clamp the maximized frameless window to the monitor WORK area. A
    /// <c>WindowStyle=None</c> window maximizes to the full MONITOR rect and then overhangs by the
    /// invisible resize border, so it covers the taskbar and clips its own edges; answering
    /// <c>WM_GETMINMAXINFO</c> with the work-area origin and size is the standard fix. Chosen over
    /// <c>SystemParameters.WorkArea</c> because that describes the PRIMARY monitor only — this hook
    /// asks the monitor the window is actually on.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(ChromeHook);
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref NcMonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NcRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NcPoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NcMonitorInfo { public int cbSize; public NcRect rcMonitor; public NcRect rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NcMinMaxInfo { public NcPoint ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

    private IntPtr ChromeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;
        var info = new NcMonitorInfo { cbSize = Marshal.SizeOf<NcMonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<NcMinMaxInfo>(lParam);
        var work = info.rcWork;
        var mon = info.rcMonitor;
        // ptMaxPosition is relative to the monitor rect, not the desktop.
        mmi.ptMaxPosition = new NcPoint { X = work.Left - mon.Left, Y = work.Top - mon.Top };
        mmi.ptMaxSize = new NcPoint { X = work.Right - work.Left, Y = work.Bottom - work.Top };
        // Keep the OS min-track size in step with the declared minimum (DIP -> physical px).
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            mmi.ptMinTrackSize = new NcPoint
            {
                X = (int)(MinWidth * dpi.DpiScaleX),
                Y = (int)(MinHeight * dpi.DpiScaleY),
            };
        }
        catch (InvalidOperationException)
        {
            // No presentation source yet: leave the OS default.
        }

        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>6 px of WPF-owned border while not maximized so the OS resize-border logic has
    /// something to hit-test, 0 while maximized (there is nothing to resize into, and a margin there
    /// would show the desktop through the window's own edges). Also pushes a state event to the page
    /// so the maximize button flips its glyph.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        Web.Margin = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(6);
        // A maximize/restore that did not originate from the page's own button (a taskbar action, a
        // double-click on the drag strip) still needs the chrome row's glyph to flip.
        Web.CoreWebView2?.PostWebMessageAsJson(HostBridge.StateEvent(this));
    }
}
