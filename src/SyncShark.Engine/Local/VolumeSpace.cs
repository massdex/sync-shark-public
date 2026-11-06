using System.ComponentModel;
using System.Runtime.InteropServices;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Reads the destination parent's native volume serial and caller-available bytes for download admission.</summary>
internal static class VolumeSpace
{
    /// <summary>Returns the destination directory's volume serial; zero is a valid grouping key.</summary>
    internal static uint Serial(string destinationDirectory)
    {
        // The raw shared open helper, not PilotDirectoryLease.Open: that class's NTFS and exact-final-path policy
        // rejects the subst, mapped and UNC parents this admission admits. The helper applies the shared extended spelling.
        using var handle = PilotDirectoryLease.CreateFile(destinationDirectory, PilotDirectoryLease.FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero, PilotDirectoryLease.OpenExisting,
            PilotDirectoryLease.FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((information.FileAttributes & FileAttributes.Directory) == 0) throw new IOException();
        return information.VolumeSerialNumber;
    }

    /// <summary>Returns the bytes available to the caller on the destination directory's volume.</summary>
    internal static long Available(string destinationDirectory)
    {
        if (!GetDiskFreeSpaceExW(PathRules.ToExtendedWindowsPath(destinationDirectory),
                out var availableToCaller, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (availableToCaller > long.MaxValue) throw new OverflowException();
        return (long)availableToCaller;
    }

    private const string KernelLibrary = "kernel32.dll";
    [DllImport(KernelLibrary, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(string directoryName, out ulong availableToCaller,
        out ulong totalBytes, out ulong totalFreeBytes);
}
