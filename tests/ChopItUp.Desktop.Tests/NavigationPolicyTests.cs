namespace ChopItUp.Desktop.Tests;

/// <summary>Row 12 T4 fix (B5): the navigation lock, pulled out of the window so it can be tested.
///
/// The defect this covers: <c>NavigateToString</c> is documented as reporting <c>about:blank</c>, but on
/// this machine's WebView2 runtime (152.0.4191.66) it raises <c>NavigationStarting</c> with a
/// <c>data:text/html;charset=utf-8;base64,...</c> URI. The guard cancelled it, so the shell's own boot
/// page — the Starting page AND the Failed page carrying the reason and the log tail — never rendered
/// and a slow or failed hub left a blank window.
///
/// The widening is bounded: a <c>data:</c> URI is allowed only while the window has just asked for a
/// boot page, and the window clears that flag on the first one through, so a <c>data:</c> link arriving
/// in a message is still refused.</summary>
public class NavigationPolicyTests
{
    private static readonly Uri HubOrigin = new("http://127.0.0.1:8795/");

    private const string BootPageUri = "data:text/html;charset=utf-8;base64,PGh0bWw+PC9odG1sPg==";

    [Theory]
    // The hub's own client: allowed with or without a boot page pending, whatever the path or case.
    [InlineData("http://127.0.0.1:8795/", true)]
    [InlineData("http://127.0.0.1:8795/", false)]
    [InlineData("http://127.0.0.1:8795/rooms/general?x=1#y", false)]
    [InlineData("HTTP://127.0.0.1:8795/Rooms", false)]
    public void The_hub_origin_is_allowed_whatever_the_path_or_case(string uri, bool pending) =>
        Assert.Equal(NavDecision.Allow, NavigationPolicy.Decide(uri, HubOrigin, pending));

    [Theory]
    // Documented NavigateToString behaviour; allowed unconditionally, as it was before the fix.
    [InlineData(true)]
    [InlineData(false)]
    public void About_blank_is_allowed(bool pending) =>
        Assert.Equal(NavDecision.Allow, NavigationPolicy.Decide("about:blank", HubOrigin, pending));

    [Theory]
    [InlineData(BootPageUri)]
    [InlineData("DATA:TEXT/HTML;charset=utf-8;base64,PGh0bWw+")]
    [InlineData("data:text/html,<h1>hi</h1>")]
    public void A_data_html_uri_is_the_shells_own_boot_page_while_one_is_pending(string uri) =>
        Assert.Equal(NavDecision.Allow, NavigationPolicy.Decide(uri, HubOrigin, bootPagePending: true));

    [Theory]
    [InlineData(BootPageUri)]
    [InlineData("DATA:TEXT/HTML;charset=utf-8;base64,PGh0bWw+")]
    [InlineData("data:text/html,<h1>hi</h1>")]
    public void The_same_data_uri_is_blocked_when_no_boot_page_is_pending(string uri) =>
        Assert.Equal(NavDecision.Block, NavigationPolicy.Decide(uri, HubOrigin, bootPagePending: false));

    [Theory]
    // Only text/html is the boot page's shape; anything else a message could carry stays refused even
    // in the window between asking for a boot page and getting it.
    [InlineData("data:application/json;base64,e30=")]
    [InlineData("data:text/plain,hello")]
    [InlineData("data:image/svg+xml,<svg/>")]
    public void Other_data_uris_are_blocked_even_while_a_boot_page_is_pending(string uri) =>
        Assert.Equal(NavDecision.Block, NavigationPolicy.Decide(uri, HubOrigin, bootPagePending: true));

    [Theory]
    // B5: a link in a message is a link. The default browser gets it; this window does not.
    [InlineData("https://example.com/thing")]
    [InlineData("http://127.0.0.1:8796/")]          // another hub on this machine is still not ours
    [InlineData("http://localhost:8795/")]          // same port, different host string: not the authority
    public void Any_other_http_origin_opens_externally(string uri) =>
        Assert.Equal(NavDecision.OpenExternally, NavigationPolicy.Decide(uri, HubOrigin, bootPagePending: true));

    [Theory]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("ms-appx:///index.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("about:srcdoc")]
    [InlineData("not a uri at all")]
    [InlineData("")]
    public void Everything_else_is_blocked(string uri) =>
        Assert.Equal(NavDecision.Block, NavigationPolicy.Decide(uri, HubOrigin, bootPagePending: true));

    [Theory]
    // The window uses this to spend the one-shot flag on the first boot page through, so the second
    // data: navigation of a launch is blocked again.
    [InlineData("about:blank", true)]
    [InlineData(BootPageUri, true)]
    [InlineData("DATA:TEXT/HTML,x", true)]
    [InlineData("http://127.0.0.1:8795/", false)]
    [InlineData("data:text/plain,hello", false)]
    [InlineData("", false)]
    public void IsBootPageUri_recognises_what_NavigateToString_reports(string uri, bool expected) =>
        Assert.Equal(expected, NavigationPolicy.IsBootPageUri(uri));
}
