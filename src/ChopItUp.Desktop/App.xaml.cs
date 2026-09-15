using System.Security.Cryptography;

namespace ChopItUp.Desktop;

public partial class App : System.Windows.Application
{
    /// <summary>Row 12 T4: false for every close the owner makes with the Close button or Alt+F4 — the
    /// window hides and the hub keeps running (AC4). Only Quit (tray menu, the page's Quit, or a second
    /// launch with <c>--quit</c>) sets it, and it is what tells <c>OnClosing</c> to let the window
    /// actually close. Task 5 sets it from <c>QuitAsync</c>.</summary>
    public static bool Quitting { get; set; }

    /// <summary>Row 12 B8: minted once per process. The boot pages are loaded with
    /// <c>NavigateToString</c>, so WebView2 reports their source as <c>about:blank</c> rather than the
    /// hub origin; every message they post carries this value, and the bridge's origin rule is widened
    /// for a non-hub source only when the nonce matches (pass 1, finding 12). 16 hex chars from the
    /// CSPRNG: it never leaves this process except into a page this process itself wrote.</summary>
    public static string LaunchNonce { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // Row 12 T2: parse only, for now. Task 5 replaces this body with the hub
            // start-or-attach flow and the window that follows it.
            ShellArgs.Parse(Environment.GetCommandLineArgs()[1..], baseDir: AppContext.BaseDirectory);
        }
        catch (ArgumentException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "ChopItUp", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown(2);
        }
    }
}
