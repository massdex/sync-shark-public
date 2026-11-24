using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Reads caller-available bytes on the destination volume.</summary>
public static class VolumeSpace
{
    /// <summary>Returns the bytes available to the caller on the destination directory's volume.</summary>
    public static long Available(string destinationDirectory)
    {
        if (!GetDiskFreeSpaceExW(DriveIdentity.ToExtendedWindowsPath(destinationDirectory),
                out var availableToCaller, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (availableToCaller > long.MaxValue) throw new OverflowException();
        return (long)availableToCaller;
    }

    // The file-system format name DriveInfo.DriveFormat reports for an NTFS root; a format fact, not user text.
    internal const string NtfsFormat = "NTFS";

    private const string KernelLibrary = "kernel32.dll";
    [DllImport(KernelLibrary, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(string directoryName, out ulong availableToCaller,
        out ulong totalBytes, out ulong totalFreeBytes);
}
