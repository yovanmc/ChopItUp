using System.Text.Json;

namespace ChopItUp.Desktop.Bridge;

/// <summary>How the launch-scoped owner bearer reaches the page.
///
/// The hub owner's real token is hashed at rest (<c>TokenStore</c>), so the shell cannot read it and
/// must not try. Instead <see cref="Hub.HubChild"/> mints 32 random bytes per launch, hands them to the
/// child hub in <c>CHOPITUP_SHELL_TOKEN</c> (which the hub deletes from its own environment before any
/// spawn can inherit it), and this script hands the same value to the page.
///
/// It is registered with <c>AddScriptToExecuteOnDocumentCreatedAsync</c>, so WebView2 runs it on every
/// document (including reloads and in-app navigations) before any page script. The value lands on a
/// non-enumerable, non-writable <c>window</c> property and nowhere else: not <c>localStorage</c>, not a
/// cookie, not the WebView2 profile on disk. That is the whole point: a LevelDB copy under the profile
/// is readable by any process running as this user, a spawn's scheduled-task escape included, whereas
/// this copy dies with the window. The client's <c>readOwnerToken()</c> reads the global first and
/// falls back to the paste flow, which is what attach mode and a browser tab get.
///
/// Attach mode registers nothing at all: the shell did not start that hub and holds no credential for it.</summary>
public static class OwnerTokenScript
{
    /// <summary>The page global the client's <c>readOwnerToken()</c> consults before storage.</summary>
    public const string GlobalName = "__chopitupShellToken";

    /// <summary>Builds the per-document script for <paramref name="origin"/>. The origin guard is
    /// belt-and-braces: WebView2 runs document-created scripts on every document the control loads,
    /// and only the hub origin should ever see this value — a page that somehow navigated elsewhere
    /// (the origin lock in <c>MainWindow</c> is the braces) must not be handed an owner credential.</summary>
    public static string For(Uri origin, string token)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrEmpty(token);

        // `location.origin` is scheme://host[:port] with no trailing slash — exactly UriPartial.Authority.
        var authority = origin.GetLeftPart(UriPartial.Authority);
        if (authority.Length == 0) throw new ArgumentException($"'{origin}' has no origin authority to guard on.", nameof(origin));
        foreach (var c in authority)
        {
            // The authority is interpolated into a JS string literal like the token, but unlike the
            // token it is not JSON-escaped (the test asserts the literal), so it is validated instead.
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or ':' or '/' or '-' or '_' or '[' or ']'))
                throw new ArgumentException($"'{authority}' is not an origin this script can guard on.", nameof(origin));
        }

        // JsonEncodedText's default encoder escapes the quote, the backslash and every HTML-sensitive
        // character (' -> '), which is what keeps a token from closing the literal it sits in.
        var escaped = JsonEncodedText.Encode(token).ToString();

        return $"(function(){{try{{if(location.origin!=='{authority}')return;"
             + $"Object.defineProperty(window,'{GlobalName}',{{value:'{escaped}',writable:false,configurable:false,enumerable:false}});"
             + "}catch(e){}})();";
    }
}
