using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Reads volume coverage and cancellable raw pages; proves publication on a held file only.</summary>
public class UsnJournal
{
    private const uint FsctlReadUsnJournal = 0x000900bb, FsctlReadUnprivilegedUsnJournal = 0x000903ab;
    private const uint FsctlWriteUsnCloseRecord = 0x000900ef;
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FsctlQueryUsnJournal = 0x000900f4;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    // winioctl.h USN_JOURNAL_DATA_V2 is 80 bytes.
    private const int JournalStateBufferBytes = 80;
    // winioctl.h USN_JOURNAL_DATA_V0 is 56 bytes.
    private const int JournalStateBytes = 56;
    // winioctl.h READ_USN_JOURNAL_DATA_V0 is 40 bytes.
    private const int ReadJournalDataBytes = 40;
    // winioctl.h USN_RECORD_V2 FileName offset 60.
    private const int UsnRecordHeaderBytes = 60;
    private const ushort UsnRecordVersion2 = 2;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorOperationAborted = 995;
    private const int ErrorIoPending = 997;
    // The observed pended-read transient; the adopted one-retry policy is in engine design section 8.7.
    // winerror.h ERROR_INVALID_USER_BUFFER = 1784.
    private const int ErrorInvalidUserBuffer = 1784;
    private const int ErrorJournalDeleteInProgress = 1178;
    private const int ErrorJournalNotActive = 1179;
    private const int ErrorJournalEntryDeleted = 1181;
    public virtual JournalPosition Current(string root) => Retained(root).Tail;

    /// <summary>The retained inclusive continuation range from one volume query.</summary>
    public virtual (ulong First,JournalPosition Tail) Retained(string root) => TryQueryJournalState(root,out var state,out var error,out _,out _)
        ? (Math.Max(state.FirstUsn,state.LowestValidUsn),new(state.JournalId,state.NextUsn)) : throw Failure(NtfsUsnPageStatus.Unavailable,error);

    internal static JournalSourceException Failure(NtfsUsnPageStatus status,int? error) =>
        new(status,error,Text.Get(TextKey.JournalSourceFailed,(TextArg.Reason,status.ToString()),(TextArg.Value,error?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)));

    /// <summary>Force the publishing handle's own close record, bracketed by queries to its volume GUID.</summary>
    public JournalPosition? Published(SafeFileHandle file)
    {
        var size=GetFinalPathNameByHandle(file,null,0,1);
        if (size==0) return null;
        var path=new StringBuilder(checked((int)size));
        var len=GetFinalPathNameByHandle(file,path,size,1);
        if (len==0 || len>=size) return null;
        var end=path.ToString().IndexOf('}',StringComparison.Ordinal);
        if (end<0) return null;
        var volume=path.ToString(0,end+1);
        if (!TryQueryJournalState(volume,out var before,out _,out _,out _)) return null;
        var bytes=new byte[sizeof(ulong)];
        if (!TryDeviceIoControlPage(file,FsctlWriteUsnCloseRecord,[],bytes,out var count,out _,CancellationToken.None) || count!=bytes.Length) return null;
        var usn=BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return usn>=before.NextUsn && TryQueryJournalState(volume,out var after,out _,out _,out _) &&
            after.JournalId==before.JournalId && usn<after.NextUsn ? new(after.JournalId,usn) : null;
    }

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder? path,uint size,uint flags);

    /// <summary>A null end waits for changes; a finite end never waits for a later arrival.</summary>
    public virtual Task<NtfsUsnPage> ReadAsync(string root, JournalPosition from, JournalPosition? end, int bytes, CancellationToken ct)
    {
        if (end!=null && end.JournalId!=from.JournalId)
        {
            var current=Current(root);
            if (current.JournalId==from.JournalId) throw Failure(NtfsUsnPageStatus.Unavailable,null);
            return Task.FromResult(new NtfsUsnPage(NtfsUsnPageStatus.JournalChanged,from.NextUsn,null));
        }
        return Task.Run(()=>ReadChangePage(root,from.JournalId,from.NextUsn,end?.NextUsn,end is null,bytes,ct),ct);
    }

    private static NtfsUsnPage ReadChangePage(string rootPath, ulong journalId, ulong fromNextUsn,
        ulong? throughNextUsn, bool waitForChange,
        int pageBufferBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return new NtfsUsnPage(NtfsUsnPageStatus.Unavailable, fromNextUsn, null);
        if (!TryGetVolumePath(rootPath, out var volumePath))
            return new NtfsUsnPage(NtfsUsnPageStatus.Unavailable, fromNextUsn, null);
        using var handle = OpenReadOnlyVolume(volumePath, overlapped: true);
        if (handle.IsInvalid)
            return new NtfsUsnPage(NtfsUsnPageStatus.Unavailable, fromNextUsn, Marshal.GetLastWin32Error());
        var buffer = ArrayPool<byte>.Shared.Rent(pageBufferBytes);
        try
        {
            var error = ReadPageNative(handle, journalId, fromNextUsn, waitForChange, buffer, out var returned, ct);
            if (error == ErrorInvalidUserBuffer)
                error = ReadPageNative(handle, journalId, fromNextUsn, waitForChange, buffer, out returned, ct);
            if (error != 0) return ClassifyPageFailure(rootPath, journalId, fromNextUsn, error);
            return Decode(buffer.AsSpan(0,returned),fromNextUsn,throughNextUsn);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Decode exactly the returned bytes; failed pages expose no records or progress.</summary>
    public static NtfsUsnPage Decode(ReadOnlySpan<byte> buffer,ulong start,ulong? end)
    {
        NtfsUsnPage Failed(NtfsUsnPageStatus status) => new(status,start,null);
        if (buffer.Length<sizeof(ulong)) return Failed(NtfsUsnPageStatus.Unavailable);
        var continuation=BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        var data=buffer[sizeof(ulong)..];
        var records=new List<UsnRecord>();
        var consumed=start;
        if (data.IsEmpty)
        {
            if (continuation<start) return Failed(NtfsUsnPageStatus.NoProgress);
            consumed=Math.Min(continuation,end ?? continuation);
            if (end!=null && consumed<end && consumed==start) return Failed(NtfsUsnPageStatus.NoProgress);
            return new(end==null || consumed==end ? NtfsUsnPageStatus.Complete : NtfsUsnPageStatus.More,consumed,null,records);
        }
        while (!data.IsEmpty)
        {
            if (data.Length>=8 && BinaryPrimitives.ReadUInt16LittleEndian(data[4..])!=UsnRecordVersion2) return Failed(NtfsUsnPageStatus.UnsupportedRecord);
            if (data.Length < UsnRecordHeaderBytes) return Failed(NtfsUsnPageStatus.InvalidRecord);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (length < UsnRecordHeaderBytes || length > (uint)data.Length) return Failed(NtfsUsnPageStatus.InvalidRecord);
            var rest = data[(int)length..];
            if (!rest.IsEmpty && rest.Length < UsnRecordHeaderBytes) return Failed(NtfsUsnPageStatus.InvalidRecord);
            if (!rest.IsEmpty && BinaryPrimitives.ReadUInt16LittleEndian(rest[4..])!=UsnRecordVersion2) return Failed(NtfsUsnPageStatus.UnsupportedRecord);
            var boundary = rest.IsEmpty ? continuation : BinaryPrimitives.ReadUInt64LittleEndian(rest[24..]);
            if (boundary <= consumed) return Failed(NtfsUsnPageStatus.NoProgress);
            var usn = BinaryPrimitives.ReadUInt64LittleEndian(data[24..]);
            if (usn>=boundary) return Failed(NtfsUsnPageStatus.InvalidRecord);
            // A finite end taken from the native query can sit in the padding before the next journal page, so the
            // next record's USN may pass it. This record's own start and returned length decide; the returned V2 length
            // is never longer than the stored record, so a record before such an end is never left behind.
            if (end is not null && usn >= end.Value)
            {
                // No record starts before the finite end: the covered range is padding, so coverage reaches it.
                consumed = end.Value;
                break;
            }
            if (end is not null && length > end.Value-usn) return Failed(NtfsUsnPageStatus.InvalidRecord);
            var nameLength=BinaryPrimitives.ReadUInt16LittleEndian(data[56..]);
            var nameOffset=BinaryPrimitives.ReadUInt16LittleEndian(data[58..]);
            if ((nameLength & 1)!=0 || (nameLength!=0 && (nameOffset<UsnRecordHeaderBytes || nameOffset+nameLength>length))) return Failed(NtfsUsnPageStatus.InvalidRecord);
            if (usn>=start) records.Add(new(BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(data[16..]),usn,BinaryPrimitives.ReadUInt32LittleEndian(data[40..]),
                (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(data[52..]),
                nameLength==0 ? null : Encoding.Unicode.GetString(data.Slice(nameOffset,nameLength))));
            consumed = end is not null && boundary > end.Value ? end.Value : boundary;
            data = rest;
        }
        bool complete=end.HasValue ? consumed==end.Value : consumed==start;
        return new(complete ? NtfsUsnPageStatus.Complete : NtfsUsnPageStatus.More,consumed,null,records);
    }

    [SupportedOSPlatform("windows")]
    private static int ReadPageNative(SafeFileHandle volume, ulong journalId, ulong startUsn, bool waitForChange,
        byte[] output, out int bytesReturned, CancellationToken ct)
    {
        var input = BuildReadJournalInput(startUsn, journalId, waitForChange);
        if (TryDeviceIoControlPage(volume, FsctlReadUsnJournal, input, output, out bytesReturned, out var error, ct))
            return 0;
        if (error is ErrorAccessDenied or ErrorInvalidFunction &&
            TryDeviceIoControlPage(volume, FsctlReadUnprivilegedUsnJournal, input, output, out bytesReturned, out error, ct))
            return 0;
        return error;
    }

    private static NtfsUsnPage ClassifyPageFailure(string rootPath, ulong journalId, ulong fromNextUsn, int error)
    {
        var status = error switch
        {
            ErrorJournalEntryDeleted => NtfsUsnPageStatus.HistoryLost,
            ErrorInvalidParameter or ErrorJournalDeleteInProgress or ErrorJournalNotActive =>
                ClassifyJournalState(rootPath, journalId, fromNextUsn),
            _ => NtfsUsnPageStatus.Unavailable
        };
        return new NtfsUsnPage(status, fromNextUsn, error);
    }

    private static NtfsUsnPageStatus ClassifyJournalState(string rootPath, ulong journalId, ulong fromNextUsn)
    {
        if (!TryQueryJournalState(rootPath,out var state,out _,out _,out _))
            return NtfsUsnPageStatus.Unavailable;
        if (state.JournalId != journalId) return NtfsUsnPageStatus.JournalChanged;
        return fromNextUsn < Math.Max(state.FirstUsn,state.LowestValidUsn) || fromNextUsn>state.NextUsn ? NtfsUsnPageStatus.HistoryLost : NtfsUsnPageStatus.Unavailable;
    }

    private static byte[] BuildReadJournalInput(ulong startUsn, ulong journalId, bool waitForChange)
    {
        var input = new byte[ReadJournalDataBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(input, startUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);
        // Native Timeout is not a deadline; it only bounds the wait for BytesToWaitFor record bytes.
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(16), waitForChange ? ulong.MaxValue : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(24), waitForChange ? 1UL : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), journalId);
        return input;
    }

    private static bool TryGetVolumePath(string rootPath, out string volumePath)
    {
        volumePath = string.Empty;
        if (rootPath.StartsWith(@"\\?\Volume{",StringComparison.OrdinalIgnoreCase)) { volumePath=rootPath.TrimEnd('\\'); return true; }
        var full = Path.GetFullPath(rootPath);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':') return false;
        volumePath = "\\\\.\\" + char.ToUpperInvariant(root[0]) + ":";
        return true;
    }

    private static bool TryQueryJournalState(string rootPath,
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
    private static bool TryDeviceIoControlPage(SafeFileHandle device, uint controlCode, byte[] input, byte[] output,
        out int bytesReturned, out int error, CancellationToken ct)
    {
        bytesReturned = 0;
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

    private static SafeFileHandle OpenReadOnlyVolume(string volumePath, bool overlapped = false)
    {
        var overlappedFlag = overlapped ? FileFlagOverlapped : 0;
        var handle = CreateFile(volumePath, GenericRead, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, OpenExisting, overlappedFlag, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        // Standard users can query/read the unprivileged journal through the volume's root
        // directory handle. A zero-access raw device handle opens but returns INVALID_FUNCTION
        // on current Windows; do not mistake that for an unavailable NTFS journal.
        var directory = (volumePath.StartsWith(@"\\.\",StringComparison.Ordinal) ? volumePath[4..] : volumePath) + "\\";
        return CreateFile(directory, 0, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | overlappedFlag, IntPtr.Zero);
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
