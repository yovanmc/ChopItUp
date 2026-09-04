using System.Security.Cryptography;
using System.Text;

namespace ChopItUp.Core.Storage;

/// <summary>Named cross-process mutex keyed on a normalized file path (ReserveDb's migration-mutex
/// rule): SHA256 of the full upper-cased path, hex, first 32 chars, behind a caller-chosen prefix.</summary>
public static class PathMutex
{
    public static string Name(string prefix, string path) =>
        prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..32];

    public static T Run<T>(string prefix, string path, TimeSpan timeout, Func<T> body)
    {
        using var mutex = new Mutex(initiallyOwned: false, Name(prefix, path));
        bool held;
        try { held = mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { held = true; }   // the holder died; every guarded write is atomic, so the file is whole
        if (!held) throw new TimeoutException($"Could not lock {path} within {timeout.TotalSeconds:0} s; is another hub running on this data dir?");
        try { return body(); }
        finally { mutex.ReleaseMutex(); }
    }
}
