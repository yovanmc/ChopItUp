using ChopItUp.Desktop.Bridge;

namespace ChopItUp.Desktop.Tests;

/// <summary>Row 12 T4 (B2): the launch-scoped owner bearer reaches the page as an in-memory global on
/// every document of the hub origin — never localStorage, never the WebView2 profile, so no copy
/// outlives Quit and no spawn running as this user can read one off disk.</summary>
public class OwnerTokenScriptTests
{
    [Fact]
    public void The_script_only_fires_on_the_exact_hub_origin_authority()
    {
        var js = OwnerTokenScript.For(new Uri("http://127.0.0.1:8795/"), "tok");

        // location.origin has no trailing slash; a guard that compared against the Uri's ToString()
        // would never match and the page would silently fall back to the paste flow.
        Assert.Contains("location.origin!=='http://127.0.0.1:8795'", js);
        Assert.DoesNotContain("http://127.0.0.1:8795/'", js);
    }

    [Fact]
    public void The_token_is_escaped_so_it_cannot_break_out_of_the_string_literal()
    {
        var js = OwnerTokenScript.For(new Uri("http://127.0.0.1:8790/"), "a'b\\c");

        Assert.DoesNotContain("'a'b", js);
        Assert.Contains("\\u0027", js);   // the quote
        Assert.Contains("\\\\", js);      // the backslash
    }

    [Fact]
    public void The_script_sets_an_in_memory_global_and_never_touches_storage()
    {
        var js = OwnerTokenScript.For(new Uri("http://127.0.0.1:8790/"), "tok");

        Assert.Contains("__chopitupShellToken", js);   // the client's readOwnerToken() reads this first
        Assert.DoesNotContain("localStorage", js);
        Assert.DoesNotContain("sessionStorage", js);
        Assert.DoesNotContain("document.cookie", js);
    }
}
