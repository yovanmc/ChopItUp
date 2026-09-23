using System.Security.Cryptography;
using ChopItUp.Desktop.Hub;
using Microsoft.Web.WebView2.Core;

namespace ChopItUp.Desktop;

/// <summary>The startup order (args, then the single-instance verbs, then the runtime check, then
/// primary, then log/hub/window/tray, then start-or-attach) and the matching shutdown path
/// (<see cref="QuitAsync"/>). Untestable by construction (STA thread, a real WebView2, a real mutex):
/// every piece it wires (<see cref="ShellArgs"/>, <see cref="SingleInstance"/>,
/// <see cref="HubChild"/>, <c>MainWindow</c>, <see cref="TrayIcon"/>, <see cref="Bridge.HostBridge"/>)
/// is tested on its own; this file is exercised by a manual smoke test.</summary>
public partial class App : System.Windows.Application
{
    /// <summary>False for every close the hub owner makes with the Close button or Alt+F4: the window
    /// hides and the hub keeps running. Only Quit (tray menu, the page's Quit, or a second launch with
    /// <c>--quit</c>) sets it, and it is what tells <c>OnClosing</c> to let the window actually
    /// close.</summary>
    public static bool Quitting { get; set; }

    /// <summary>Minted once per process. The boot pages are loaded with <c>NavigateToString</c>; the
    /// documented source WebView2 reports for that is <c>about:blank</c>, but on runtime
    /// 152.0.4191.66 it was measured as a <c>data:text/html;charset=utf-8;base64,...</c> URI (see
    /// <see cref="NavigationPolicy.IsBootPageUri"/>) rather than the hub origin. Every message the boot
    /// page posts carries this value, and the bridge's origin rule
    /// (<see cref="Bridge.HostBridge.IsTrusted"/>) is widened for either shape of non-hub source only
    /// when the nonce matches. 16 hex chars from the CSPRNG: it never leaves this process except into
    /// a page this process itself wrote.</summary>
    public static string LaunchNonce { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    private SingleInstance? _singleInstance;
    private HubChild? _hub;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private ShellLog? _log;
    private readonly CancellationTokenSource _startCts = new();

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Args.
        ShellArgs args;
        try
        {
            args = ShellArgs.Parse(Environment.GetCommandLineArgs()[1..], baseDir: AppContext.BaseDirectory);
        }
        catch (ArgumentException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Chop It Up", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        // 2. Single-instance verbs: --show/--quit only ever address an ALREADY-running instance.
        if (args.Command is ShellCommand.Show or ShellCommand.Quit)
        {
            var signaled = SingleInstance.Signal(SingleInstance.Key(args.DataDir), args.Command);
            if (!signaled)
                Console.Error.WriteLine($"No running Chop It Up instance for {args.DataDir}.");
            Shutdown(signaled ? 0 : 1);
            return;
        }

        // 3. Runtime check: no page, not even the boot page, exists without a WebView2 Evergreen
        // install. Fatal before anything else is built.
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            System.Windows.MessageBox.Show(
                "Chop It Up needs the WebView2 runtime, which is not installed on this machine."
                + Environment.NewLine + Environment.NewLine
                + "Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and try again.",
                "Chop It Up",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(3);
            return;
        }

        // 4. Primary: one shell per data dir. Not primary → show the one that is, and exit.
        var key = SingleInstance.Key(args.DataDir);
        _singleInstance = SingleInstance.TryBecomePrimary(key);
        if (_singleInstance is null)
        {
            SingleInstance.Signal(key, ShellCommand.Show);
            Shutdown(0);
            return;
        }

        // 5. Log, hub, window, tray, single-instance listener.
        _log = new ShellLog(args);
        _log.Append($"SESSION START args=--data {args.DataDir} --port {args.Port} --hub {args.HubExe}");

        _hub = new HubChild(args, new ProcessHubFactory(), new HttpHealthProbe(), TimeProvider.System, _log.Append);
        _hub.StatusChanged += OnHubStatusChanged;

        _window = new MainWindow(args, _hub, _log);
        _window.Show();

        System.Windows.Forms.Application.EnableVisualStyles();
        _tray = new TrayIcon(onOpen: () => _window?.ShowAndActivate(), onQuit: () => _ = QuitAsync());

        _singleInstance.Listen(
            onShow: () => Dispatcher.BeginInvoke(() => _window?.ShowAndActivate()),
            onQuit: () => Dispatcher.BeginInvoke(() => _ = QuitAsync()));

        // 6. Start or attach. Never throws (HubChild guards its whole body); the window and tray react
        // to StatusChanged as it moves through Starting -> Ready/Attached/Failed.
        _ = _hub.StartOrAttachAsync(_startCts.Token);
    }

    /// <summary>HubChild.StatusChanged fires on whatever thread set the status (a poll loop, an
    /// Exited callback), never the UI thread. This is the one subscriber, and it hops before touching
    /// either the window or the tray.</summary>
    private void OnHubStatusChanged(HubStatus status) =>
        Dispatcher.BeginInvoke(() =>
        {
            _window?.OnHubStatus(status);
            _tray?.Update(status);
        });

    /// <summary>Kills the hub this shell started (idempotent; a no-op when attached) and does not
    /// wait for it, tears the tray down, closes the window and exits 0. Cancels the readiness poll
    /// FIRST, so a Quit that lands during Starting cannot keep logging HUB STATE lines after EXIT.</summary>
    public Task QuitAsync()
    {
        Quitting = true;
        _startCts.Cancel();
        _tray?.Dispose();
        _hub?.Stop();
        _window?.Close();
        Shutdown(0);
        return Task.CompletedTask;
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _hub?.Dispose();
        _log?.Append("EXIT");
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
