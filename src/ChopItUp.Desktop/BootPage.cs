using System.Net;
using System.Text;

namespace ChopItUp.Desktop;

/// <summary>The two pages the WebView2 shows before (or instead of) the hub's own client.
///
/// They are HTML rather than native WPF for one reason: the window has no caption of its own, so
/// anything shown before the client loads still has to give the owner a way to move the window and a
/// way to get rid of it. A native strip would be a second chrome implementation to keep in step with
/// the page's; this one is the same 36 px row in the same palette, with the same
/// <c>app-region</c> declarations, so the switch to React is a repaint rather than a jump.
///
/// These pages are loaded with <c>NavigateToString</c>, which means they are NOT on the hub origin:
/// WebView2 reports their source as <c>about:blank</c> or a <c>data:</c> URI (see
/// <see cref="NavigationPolicy"/>). That is why every message they post carries a per-launch nonce:
/// it is the one widening of the bridge's origin rule, and the nonce is what bounds it
/// (<c>HostBridge.IsTrusted</c>).
///
/// Pure and tested: no window, no WebView2, no clock.</summary>
public static class BootPage
{
    /// <summary>Shown from the moment the window opens until <c>/health</c> answers. No log tail: there
    /// is nothing wrong yet, and a scrolling wall of startup lines reads like a failure.</summary>
    public static string Starting(string nonce) => Render(Validated(nonce), """
            <h1>Starting the hub…</h1>
            <div class="dots" aria-hidden="true"><i></i><i></i></div>
        """);

    /// <summary>Shown when the hub exited, never answered, or could not be started at all. The reason
    /// is <see cref="Hub.HubStatus.Reason"/>; the tail is the last lines the hub actually wrote, which
    /// is usually the whole diagnosis (a port already in use, a corrupt data dir).</summary>
    public static string Failed(string reason, IReadOnlyList<string> tail, string nonce)
    {
        ArgumentNullException.ThrowIfNull(tail);
        var body = new StringBuilder();
        body.Append("<h1>The hub did not start</h1>\n");
        body.Append("<p class=\"reason\">").Append(WebUtility.HtmlEncode(reason)).Append("</p>\n");
        if (tail.Count == 0)
        {
            body.Append("<p class=\"reason\">It wrote nothing to its log before stopping.</p>\n");
        }
        else
        {
            // Hub stdout: whatever a model, a stack trace or a crash dump wrote. Encoded, always.
            body.Append("<pre>");
            for (var i = 0; i < tail.Count; i++)
            {
                if (i > 0) body.Append('\n');
                body.Append(WebUtility.HtmlEncode(tail[i]));
            }
            body.Append("</pre>\n");
        }
        return Render(Validated(nonce), body.ToString());
    }

    /// <summary>The nonce is interpolated straight into the page's inline JavaScript, so it is checked
    /// here rather than trusted because <c>App.LaunchNonce</c> happens to be hex today.</summary>
    private static string Validated(string nonce)
    {
        var ok = !string.IsNullOrEmpty(nonce) && nonce.Length <= 64;
        if (ok)
        {
            foreach (var c in nonce)
            {
                if (char.IsAsciiLetterOrDigit(c)) continue;
                ok = false;
                break;
            }
        }
        if (!ok) throw new ArgumentException("A boot-page nonce must be 1-64 ASCII letters or digits; it is interpolated into the page's script.", nameof(nonce));
        return nonce;
    }

    /// <summary>Palette values are the literals from the client's <c>styles.css</c> dark theme
    /// (<c>--bg</c>, <c>--rail</c>, <c>--line-soft</c>, <c>--text</c>, <c>--dim</c>,
    /// <c>--accent-owner</c>) and the strip repeats <c>.chrome</c>'s geometry. They are copied rather
    /// than imported because this page must render with the hub down — there is nothing to fetch a
    /// stylesheet from.</summary>
    private static string Render(string nonce, string main) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="color-scheme" content="dark">
        <title>Chop It Up</title>
        <style>
          * { box-sizing: border-box; }
          html, body { height: 100%; }
          body {
            margin: 0;
            background: #0e1013;
            color: #e7eaf0;
            font: 14px/1.55 ui-sans-serif, system-ui, "Segoe UI", Inter, Roboto, Helvetica, Arial, sans-serif;
            -webkit-font-smoothing: antialiased;
            display: flex;
            flex-direction: column;
          }
          /* WebView2 hit-tests these declarations and reports the result to Windows as the window's
             caption, which is the only reason this strip can drag a window with no title bar. */
          .strip {
            flex: 0 0 36px;
            display: flex;
            align-items: center;
            height: 36px;
            padding-left: 16px;
            background: #121519;
            border-bottom: 1px solid #1b2028;
            user-select: none;
            app-region: drag;
            -webkit-app-region: drag;
          }
          .wordmark { font-size: 13px; font-weight: 650; letter-spacing: 0.04em; color: #e7eaf0; }
          /* Chromium resolves app-region geometrically, so the cluster AND each button opt out. */
          .cluster {
            margin-left: auto;
            display: flex;
            height: 36px;
            app-region: no-drag;
            -webkit-app-region: no-drag;
          }
          .btn {
            height: 36px;
            min-width: 46px;
            border: 0;
            padding: 0 12px;
            background: transparent;
            color: #8d95a5;
            font: inherit;
            font-size: 12px;
            display: grid;
            place-items: center;
            cursor: default;
            app-region: no-drag;
            -webkit-app-region: no-drag;
          }
          .btn:hover { background: rgba(255, 255, 255, 0.1); color: #e7eaf0; }
          .close { width: 46px; min-width: 46px; padding: 0; font-size: 15px; line-height: 1; }
          /* The Windows 11 close-hover red, same as the client's .chrome-close. */
          .close:hover { background: #c42b1c; color: #fff; }
          main {
            flex: 1 1 auto;
            min-height: 0;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            gap: 14px;
            padding: 24px 32px;
            text-align: center;
          }
          h1 { margin: 0; font-size: 15px; font-weight: 600; }
          .reason { margin: 0; max-width: 64ch; color: #8d95a5; font-size: 13px; }
          pre {
            margin: 0;
            width: 100%;
            max-width: 900px;
            max-height: 50vh;
            overflow: auto;
            text-align: left;
            white-space: pre-wrap;
            word-break: break-word;
            color: #8d95a5;
            font: 12px/1.5 ui-monospace, "Cascadia Mono", "JetBrains Mono", Consolas, "Courier New", monospace;
            background: #121519;
            border: 1px solid #1b2028;
            border-radius: 8px;
            padding: 12px 14px;
          }
          .dots { display: flex; gap: 6px; }
          .dots i { width: 6px; height: 6px; border-radius: 50%; background: #6fb2ff; animation: pulse 1.1s ease-in-out infinite; }
          .dots i:nth-child(2) { animation-delay: 0.35s; }
          @keyframes pulse { 0%, 100% { opacity: 0.2; } 50% { opacity: 1; } }
          @media (prefers-reduced-motion: reduce) { .dots i { animation: none; opacity: 0.6; } }
        </style>
        </head>
        <body>
        <div class="strip">
          <span class="wordmark">CHOP IT UP</span>
          <div class="cluster">
            <button type="button" class="btn" title="Quit Chop It Up" aria-label="Quit" onclick="chrome.webview.postMessage({cmd:'quit',nonce:'{{nonce}}'})">Quit</button>
            <button type="button" class="btn close" title="Close to the tray" aria-label="Close" onclick="chrome.webview.postMessage({cmd:'close',nonce:'{{nonce}}'})">&#215;</button>
          </div>
        </div>
        <main>
        {{main}}
        </main>
        </body>
        </html>
        """;
}
