namespace ChopItUp.Desktop.Tests;

/// <summary>Row 12 T4 (B8): the boot page is the only thing the owner sees for the first seconds of a
/// launch and the only thing they see when the hub never comes up, and it is the one page in the shell
/// that is NOT served from the hub origin — so its markup is worth asserting. The window itself cannot
/// be exercised here (no WebView2 on CI); the pure HTML is the gate.</summary>
public class BootPageTests
{
    private const string Nonce = "a1b2c3d4e5f60718";

    public static TheoryData<string> BothPages() => new(
        BootPage.Starting(Nonce),
        BootPage.Failed("the hub exited", ["hub line one"], Nonce));

    [Theory]
    [MemberData(nameof(BothPages))]
    public void Every_boot_page_can_be_dragged_and_dismissed(string html)
    {
        // The window has no native caption: without a drag strip the boot page cannot be moved, and
        // without the buttons it cannot be dismissed before React ever loads.
        Assert.Contains("app-region: drag", html);
        Assert.Contains("-webkit-app-region: drag", html);
        Assert.Contains("app-region: no-drag", html);
        Assert.Contains("-webkit-app-region: no-drag", html);
        Assert.Contains("aria-label=\"Close\"", html);
        Assert.Contains("aria-label=\"Quit\"", html);
    }

    [Theory]
    [MemberData(nameof(BothPages))]
    public void Every_posted_message_carries_the_launch_nonce(string html)
    {
        // B8: this page is not on the hub origin, so the bridge trusts it only by nonce (Task 5).
        Assert.Contains($"chrome.webview.postMessage({{cmd:'close',nonce:'{Nonce}'}})", html);
        Assert.Contains($"chrome.webview.postMessage({{cmd:'quit',nonce:'{Nonce}'}})", html);
        Assert.DoesNotContain("postMessage({cmd:'close'}", html);
        Assert.DoesNotContain("postMessage({cmd:'quit'}", html);
    }

    [Theory]
    [MemberData(nameof(BothPages))]
    public void Every_boot_page_is_a_dark_document_in_the_client_palette(string html)
    {
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<meta name=\"color-scheme\" content=\"dark\">", html);
        Assert.Contains("#0e1013", html);   // styles.css --bg
        Assert.Contains("#e7eaf0", html);   // styles.css --text
    }

    [Fact]
    public void Failed_encodes_the_reason_and_every_tail_line()
    {
        // The tail is hub stdout: whatever a model or a crash dump wrote goes in here verbatim.
        var html = BootPage.Failed("<b>port 8790 in use</b>", ["<script>alert(1)</script>", "plain line"], Nonce);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<b>port 8790 in use</b>", html);
        Assert.Contains("&lt;b&gt;port 8790 in use&lt;/b&gt;", html);
        Assert.Contains("plain line", html);
    }

    [Fact]
    public void Failed_shows_the_tail_in_a_pre_block_and_Starting_has_no_tail_at_all()
    {
        Assert.Contains("<pre", BootPage.Failed("boom", ["one"], Nonce));
        Assert.DoesNotContain("<pre", BootPage.Starting(Nonce));
        // A hub that died before writing a line still gets a page, just without an empty box.
        Assert.DoesNotContain("<pre", BootPage.Failed("boom", [], Nonce));
    }

    [Theory]
    [InlineData("has'quote")]
    [InlineData("has\\backslash")]
    [InlineData("has space")]
    [InlineData("")]
    public void A_nonce_that_could_break_out_of_the_inline_script_is_refused(string nonce)
    {
        // The nonce is interpolated straight into the page's JavaScript, so it is validated at the
        // one place that builds the page rather than trusted because App happens to mint hex.
        Assert.Throws<ArgumentException>(() => BootPage.Starting(nonce));
        Assert.Throws<ArgumentException>(() => BootPage.Failed("reason", [], nonce));
    }
}
