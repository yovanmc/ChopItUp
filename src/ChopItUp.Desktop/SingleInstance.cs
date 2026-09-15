using System.Security.Cryptography;
using System.Text;

namespace ChopItUp.Desktop;

/// <summary>Row 12 T2: the piece Task 5's single-instance mutex/event names need. Extended there with
/// the actual mutex/event wiring (B6); this task only needs a stable, short key derived from the data
/// dir so the WebView2 profile path (ShellArgs.WebViewProfileDir) and the mutex/event names agree.</summary>
public static class SingleInstance
{
    /// <summary>First 16 hex chars of SHA-256 over the lower-invariant full path.</summary>
    public static string Hash16(string path)
    {
        var normalized = path.ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
