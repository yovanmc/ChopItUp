using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ChopItUp.Hub.Rooms;

/// <summary>Opens a file or folder and asks Windows where the open handle really is. A path that
/// passes through a junction reports the junction's target, so a fence checked against this path
/// cannot be stepped around by a link on the way.</summary>
[SupportedOSPlatform("windows")]
public static partial class FinalPath
{
    public const int FileNotFound = 2;
    public const int PathNotFound = 3;
    public const int SharingViolation = 32;

    private const uint GenericRead = 0x80000000;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* buffer, uint size, uint flags);

    /// <summary>A read handle on the file itself. A link at the last step is opened as the link, not
    /// followed, so it can be refused before anything reads through it. Null with the Win32 error when
    /// the open fails.</summary>
    public static SafeFileHandle? OpenFile(string path, out int error) =>
        Open(path, GenericRead, OpenReparsePoint, out error);

    /// <summary>A handle on a folder that grants no access, only enough to ask where it is.</summary>
    public static SafeFileHandle? OpenDirectory(string path, out int error) =>
        Open(path, 0, BackupSemantics, out error);

    private static SafeFileHandle? Open(string path, uint access, uint flags, out int error)
    {
        var handle = CreateFile(Extended(path), access, ShareAll, 0, OpenExisting, flags, 0);
        error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        if (error == 0) return handle;
        handle.Dispose();
        return null;
    }

    /// <summary>The handle's full path with every link resolved and the <c>\\?\</c> prefix removed,
    /// or null when Windows cannot name it (a device, a volume with no drive letter).</summary>
    public static unsafe string? Of(SafeFileHandle handle)
    {
        var buffer = new char[512];
        while (true)
        {
            uint length;
            fixed (char* start = buffer) length = GetFinalPathNameByHandle(handle, start, (uint)buffer.Length, 0);
            if (length == 0) return null;
            if (length < buffer.Length) return Plain(new string(buffer, 0, (int)length));
            buffer = new char[length + 1];
        }
    }

    /// <summary>A full path in the <c>\\?\</c> form, which lifts the 260-character limit on a direct
    /// Win32 call. Forward slashes become backslashes first, because that form takes a path literally.</summary>
    private static string Extended(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private static string Plain(string final) =>
        final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + final[8..]
        : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..]
        : final;
}
