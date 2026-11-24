using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Owns the volume handles and native journal control requests.</summary>
internal static class JournalDevice
{
    private const uint FsctlQueryUsnJournal = 0x000900f4; // winioctl.h FSCTL_QUERY_USN_JOURNAL
    // winioctl.h USN_JOURNAL_DATA_V2 is 80 bytes.
    private const int JournalStateBufferBytes = 80;
    // winioctl.h USN_JOURNAL_DATA_V0 is 56 bytes.
    private const int JournalStateBytes = 56;
    private const int ErrorOperationAborted = 995; // winerror.h ERROR_OPERATION_ABORTED
    private const int ErrorIoPending = 997; // winerror.h ERROR_IO_PENDING

    internal static bool TryGetVolumePath(string rootPath, out string volumePath)
    {
        volumePath = string.Empty;
        if (rootPath.StartsWith(@"\\?\Volume{",StringComparison.OrdinalIgnoreCase)) { volumePath=rootPath.TrimEnd('\\'); return true; }
        var full = Path.GetFullPath(rootPath);
        var root = Path.GetPathRoot(full);
        if (root is not [_, ':', ..]) return false;
        volumePath = "\\\\.\\" + char.ToUpperInvariant(root[0]) + ":";
        return true;
    }

    internal static bool TryQueryJournalState(string rootPath,
        out (ulong JournalId, ulong FirstUsn, ulong NextUsn, ulong LowestValidUsn, ulong MaximumSize,
            ulong AllocationDelta) state,
        out int error, out int bytesReturned, out bool openFailed)
    {
        state = default;
        error = 0;
        bytesReturned = 0;
        openFailed = false;
        if (!OperatingSystem.IsWindows()) return false;
        if (!TryGetVolumePath(rootPath, out var volumePath)) return false;
        using var handle = OpenReadOnlyVolume(volumePath);
        if (handle.IsInvalid) { error = Marshal.GetLastWin32Error(); openFailed = true; return false; }
        var buffer = new byte[JournalStateBufferBytes];
        if (!DeviceIoControlBytes(handle, FsctlQueryUsnJournal, null, 0, buffer, buffer.Length,
                out bytesReturned, IntPtr.Zero) || bytesReturned < JournalStateBytes)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }
        // winioctl.h USN_JOURNAL_DATA_V0: seven 64-bit fields, including MaxUsn between LowestValidUsn and MaximumSize.
        var journalId = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        var firstUsn = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8));
        var nextUsn = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(16));
        var lowestValidUsn = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(24));
        if (journalId == 0 || nextUsn < firstUsn || lowestValidUsn > nextUsn) return false;
        state = (journalId, firstUsn, nextUsn, lowestValidUsn,
            BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(40)),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(48)));
        return true;
    }

    // One overlapped request: the pinned buffers, pending wait and finally cleanup share one overlapped-structure lifetime.
    [SupportedOSPlatform("windows")]
    internal static bool TryDeviceIoControlPage(SafeFileHandle device, uint controlCode, byte[] input, byte[] output,
        out int bytesReturned, out int error, CancellationToken ct)
    {
        error = 0;
        var inputBuffer = Marshal.AllocHGlobal(input.Length);
        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        var pinned = default(GCHandle);
        using var completed = new ManualResetEvent(false);
        try
        {
            Marshal.Copy(input, 0, inputBuffer, input.Length);
            Marshal.StructureToPtr(new NativeOverlapped
            {
                // Win32's low event bit keeps our manual completion off a FileStream-bound IO completion port.
                EventHandle = (IntPtr)(completed.SafeWaitHandle.DangerousGetHandle().ToInt64() | 1)
            }, overlapped, false);
            // The kernel writes the page directly into this pinned buffer for the pending I/O's lifetime.
            pinned = GCHandle.Alloc(output, GCHandleType.Pinned);
            if (!DeviceIoControlOverlapped(device, controlCode, inputBuffer, input.Length,
                    pinned.AddrOfPinnedObject(), output.Length, out bytesReturned, overlapped))
            {
                error = Marshal.GetLastWin32Error();
                if (error == ErrorOperationAborted && ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
                if (error != ErrorIoPending) return false;
                if (WaitHandle.WaitAny(new WaitHandle[] { completed, ct.WaitHandle }) != 0)
                {
                    _ = CancelIoEx(device, overlapped);
                    completed.WaitOne();
                    _ = GetOverlappedResult(device, overlapped, out _, false);
                    throw new OperationCanceledException(ct);
                }
            }
            if (GetOverlappedResult(device, overlapped, out bytesReturned, false)) return true;
            error = Marshal.GetLastWin32Error();
            if (error == ErrorOperationAborted && ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            return false;
        }
        finally
        {
            if (pinned.IsAllocated) pinned.Free();
            Marshal.FreeHGlobal(overlapped);
            Marshal.FreeHGlobal(inputBuffer);
        }
    }

    internal static SafeFileHandle OpenReadOnlyVolume(string volumePath, bool overlapped = false)
    {
        var overlappedFlag = overlapped ? (uint)FileOptions.Asynchronous : 0;
        var handle = CreateFile(volumePath, LocalNative.ReadData, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, (uint)FileMode.Open, overlappedFlag, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        // Standard users can query/read the unprivileged journal through the volume's root
        // directory handle. A zero-access raw device handle opens but returns INVALID_FUNCTION
        // on current Windows; do not mistake that for an unavailable NTFS journal.
        var directory = (volumePath.StartsWith(@"\\.\",StringComparison.Ordinal) ? volumePath[4..] : volumePath) + "\\";
        return CreateFile(directory, 0, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, (uint)FileMode.Open, LocalNative.BackupSemantics | overlappedFlag, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlOverlapped(SafeFileHandle device, uint controlCode,
        IntPtr inputBuffer, int inputSize, IntPtr outputBuffer, int outputSize, out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped,
        out int bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint="DeviceIoControl", SetLastError=true)]
    private static extern bool DeviceIoControlBytes(SafeFileHandle device,uint code,byte[]? input,int inputSize,byte[] output,int outputSize,out int count,IntPtr overlapped);
}
