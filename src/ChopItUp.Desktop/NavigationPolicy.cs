namespace ChopItUp.Desktop;

/// <summary>What the window does with a navigation the WebView2 is about to start.</summary>
public enum NavDecision
{
    /// <summary>Let it run in this window.</summary>
    Allow,

    /// <summary>Cancel it here and hand the URL to the default browser (a link in a message is a
    /// link, not a second shell).</summary>
    OpenExternally,

    /// <summary>Cancel it and go nowhere.</summary>
    Block,
}

/// <summary>The navigation lock, as a pure decision so it can be tested without a window, a WebView2
/// or a hub.
///
/// <c>NavigateToString</c> is documented to report <c>about:blank</c>, but WebView2 runtime
/// 152.0.4191.66 raises <c>NavigationStarting</c> for it with a
/// <c>data:text/html;charset=utf-8;base64,...</c> URI instead. A guard that exempted only the literal
/// <c>about:blank</c> cancelled the shell's own boot page, so "Starting the hub…" and the failure page
/// with the reason and the log tail never rendered; a slow or failed hub just left a blank window.
///
/// The exemption is bounded rather than blanket: a <c>data:</c> URI is allowed only while the window
/// has just asked for a boot page (<c>bootPagePending</c>), and the window spends that flag on the
/// first one through. A <c>data:text/html</c> link arriving later in a message is still refused, which
/// is what the rule is there for.</summary>
public static class NavigationPolicy
{
    /// <summary>The two shapes a <c>NavigateToString</c> is known to report: the documented one, and the
    /// one this machine's runtime actually raises.</summary>
    private const string AboutBlank = "about:blank";

    private const string DataHtmlPrefix = "data:text/html";

    /// <summary>True for a URI that could be the boot page this window just wrote. The window uses it to
    /// spend its one-shot <c>bootPagePending</c> flag on the first boot page through, so a second
    /// <c>data:</c> navigation in the same launch is judged with the flag already down.</summary>
    public static bool IsBootPageUri(string uri) =>
        !string.IsNullOrEmpty(uri)
        && (string.Equals(uri, AboutBlank, StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith(DataHtmlPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Decides one navigation. <paramref name="hubOrigin"/> is
    /// <see cref="Hub.HubChild.ResolvedOrigin"/>: the origin the shell actually reached, never the
    /// one <c>--port</c> asked for.</summary>
    public static NavDecision Decide(string uri, Uri hubOrigin, bool bootPagePending)
    {
        ArgumentNullException.ThrowIfNull(hubOrigin);
        if (string.IsNullOrEmpty(uri)) return NavDecision.Block;

        // Checked before parsing: a data: URI is absolute and well-formed, so the http/https test below
        // would not catch it, and the allowance has to stay narrower than "any data: URI".
        if (uri.StartsWith(DataHtmlPrefix, StringComparison.OrdinalIgnoreCase))
            return bootPagePending ? NavDecision.Allow : NavDecision.Block;

        if (string.Equals(uri, AboutBlank, StringComparison.OrdinalIgnoreCase)) return NavDecision.Allow;

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return NavDecision.Block;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return NavDecision.Block;

        // Authority, not the whole URI: the client navigates within its own origin all the time.
        return string.Equals(
            parsed.GetLeftPart(UriPartial.Authority),
            hubOrigin.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase)
            ? NavDecision.Allow
            : NavDecision.OpenExternally;
    }
}
