#if NET8_0_OR_GREATER
#nullable disable
#endif
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Shared by the already-elevated installer and the attended action. Never creates a missing journal.
public static class SyncSharkJournalNative
{
    public const long GiB = 1073741824L;
    public const long Delta = 536870912L;
    public static string Volume(string value)
    {
        if (value == null || value.Length != 2 || value[1] != ':' || value[0] < 'A' || value[0] > 'Z')
            throw new ArgumentException("An exact uppercase drive letter is required.");
        return value;
    }
    public static long[] Query(string volume)
    {
        Volume(volume);
        var drive = new DriveInfo(volume + "\\");
        if (!drive.IsReady || drive.DriveFormat != "NTFS") return null;
        using (var handle = Open(volume, false))
        {
            var bytes = new byte[80]; int returned;
            if (handle.IsInvalid || !Control(handle, 0x000900f4, null, 0, bytes, bytes.Length, out returned, IntPtr.Zero) || returned < 56)
                return null;
            uint serial, maxComponent, flags;
            if (!GetVolumeInformation(volume + "\\", IntPtr.Zero, 0, out serial, out maxComponent, out flags, IntPtr.Zero, 0))
                return null;
            return new[] { BitConverter.ToInt64(bytes, 0), BitConverter.ToInt64(bytes, 8), BitConverter.ToInt64(bytes, 16),
                BitConverter.ToInt64(bytes, 24), BitConverter.ToInt64(bytes, 40), BitConverter.ToInt64(bytes, 48),
                drive.AvailableFreeSpace, (long)serial };
        }
    }
    public static long Target(long current, long free, long requested, bool install, long currentDelta = Delta)
    {
        if (requested <= 0 || requested > 8 * GiB) throw new ArgumentOutOfRangeException("requested");
        var cap = Math.Min(8 * GiB, Math.Max(0, free) / 20);
        var delta = Math.Max(Delta, currentDelta);
        return Math.Max(current, install ? Math.Max(GiB, requested) : Math.Min(requested, cap > delta ? cap - delta : 0));
    }
    public static void Grow(string volume, long expectedSerial, long requested, bool install)
    {
        if (requested <= 0 || requested > 8 * GiB) throw new ArgumentOutOfRangeException("requested");
        var current = Query(volume);
        if (current == null) { if (install) return; throw new IOException("The NTFS journal is unavailable. Refresh the notice."); }
        if (current[7] != expectedSerial) throw new IOException("The volume changed. Refresh the notice.");
        var target = Target(current[4], current[6], requested, install, current[5]);
        if (target <= current[4] && current[5] >= Delta) return;
        if (!install && current[5] < Delta && current[4] > Math.Min(8 * GiB, Math.Max(0, current[6]) / 20) - Delta)
            throw new IOException("Not enough free space for the journal allocation floor.");
        using (var handle = Open(volume, true))
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Query again on the same write handle, so a larger journal is never shrunk.
            var bytes = new byte[80]; int returned;
            if (!Control(handle, 0x000900f4, null, 0, bytes, bytes.Length, out returned, IntPtr.Zero) || returned < 56)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (BitConverter.ToInt64(bytes, 0) != current[0]) throw new IOException("The journal changed. Refresh the notice.");
            var free = new DriveInfo(volume + "\\").AvailableFreeSpace;
            target = Target(BitConverter.ToInt64(bytes, 40), free,
                requested, install, BitConverter.ToInt64(bytes, 48));
            if (!install && BitConverter.ToInt64(bytes, 48) < Delta &&
                BitConverter.ToInt64(bytes, 40) > Math.Min(8 * GiB, Math.Max(0, free) / 20) - Delta)
                throw new IOException("Not enough free space for the journal allocation floor.");
            if (target <= BitConverter.ToInt64(bytes, 40) && BitConverter.ToInt64(bytes, 48) >= Delta) return;
            var input = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(target), 0, input, 0, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(Math.Max(Delta, BitConverter.ToInt64(bytes, 48))), 0, input, 8, 8);
            if (!Control(handle, 0x000900e7, input, input.Length, null, 0, out returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
    private static SafeFileHandle Open(string volume, bool write)
    {
        var handle = CreateFile("\\\\.\\" + volume, write ? 0xc0000000u : 0x80000000u, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (write || !handle.IsInvalid) return handle;
        handle.Dispose();
        return CreateFile(volume + "\\", 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool Control(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlap);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string root, IntPtr name, uint nameSize, out uint serial, out uint component, out uint flags, IntPtr fs, uint fsSize);
}
