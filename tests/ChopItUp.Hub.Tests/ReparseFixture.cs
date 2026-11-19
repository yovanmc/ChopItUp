using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ChopItUp.Hub.Tests;

/// <summary>Puts a third-party reparse tag on a file. Unlike a symbolic link this needs no privilege,
/// so a test can stand up a placeholder-like file or a link-like one on any NTFS volume.</summary>
[SupportedOSPlatform("windows")]
public static class ReparseFixture
{
    private const uint GenericWrite = 0x4000_0000;
    private const uint FileWriteAttributes = 0x100;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint SetReparsePoint = 0x0009_00A4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);

    /// <summary>Sets <paramref name="tag"/> with a four-byte payload. A tag without the Microsoft bit
    /// takes the GUID form of the buffer.</summary>
    public static void Set(string path, uint tag)
    {
        using var handle = CreateFileW(path, GenericWrite | FileWriteAttributes, 7, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"open failed: {Marshal.GetLastWin32Error()}");
        var buffer = new byte[24 + 4];
        BitConverter.GetBytes(tag).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)4).CopyTo(buffer, 4);
        Guid.NewGuid().ToByteArray().CopyTo(buffer, 8);
        if (!DeviceIoControl(handle, SetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException($"FSCTL_SET_REPARSE_POINT failed: {Marshal.GetLastWin32Error()}");
    }
}
