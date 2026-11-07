using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;


/// <summary>
/// Reads the NTFS USN journal checkpoint without creating or changing the journal. A caller can use
/// the monotonically increasing NextUsn as a durable missed-notification detector and safely fall
/// back to a full subtree reconciliation after a journal reset or gap.
/// </summary>
public static partial class NtfsUsnJournal
{
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FsctlQueryUsnJournal = 0x000900f4;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int JournalStateBufferBytes = 80;
    private const int JournalStateBytes = 56;
    private const int ReadJournalDataBytes = 40;
    private const int UsnRecordHeaderBytes = 60;
    private const ushort UsnRecordVersion2 = 2;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorOperationAborted = 995;
    private const int ErrorIoPending = 997;
    // The observed pended-read transient; the adopted one-retry policy is in engine design section 8.7.
    private const int ErrorInvalidUserBuffer = 1784;
    private const int ErrorJournalDeleteInProgress = 1178;
    private const int ErrorJournalNotActive = 1179;
    private const int ErrorJournalEntryDeleted = 1181;
    internal static string? LastReadFailure { get; private set; }

    /// <summary>Hands one native USN record and its native continuation boundary to a page consumer.</summary>
    internal delegate void UsnPageRecord(ReadOnlySpan<byte> record, ulong boundaryAfter);

    public static bool TryReadCheckpoint(string rootPath, out NtfsUsnCheckpoint? checkpoint) =>
        TryReadCheckpoint(rootPath, out checkpoint, out _);

    internal static bool TryReadCheckpoint(string rootPath, out NtfsUsnCheckpoint? checkpoint, out bool journalDeleted)
    {
        checkpoint = null;
        journalDeleted = false;
        if (!TryQueryJournalState(rootPath, out var state, out var error, out var returned, out var openFailed))
        {
            if (error == 0) return false;
            if (!openFailed) journalDeleted = error is ErrorJournalDeleteInProgress or ErrorJournalNotActive;
            LastReadFailure = openFailed
                ? "Open volume: " + error
                : "Query journal: " + error + "; bytes=" + returned;
            return false;
        }
        checkpoint = new NtfsUsnCheckpoint(state.JournalId, unchecked((long)state.FirstUsn),
            unchecked((long)state.NextUsn), unchecked((long)state.LowestValidUsn));
        LastReadFailure = null;
        return true;
    }

    public static NtfsUsnCheckpoint ReadCheckpoint(string rootPath) =>
        TryReadCheckpoint(rootPath, out var checkpoint)
            ? checkpoint!
            : throw new IOException("The NTFS USN journal is unavailable; directory notifications and periodic reconciliation remain active.");

    /// <summary>
    /// Reads the current journal identity, coverage bounds and retention parameters from one native
    /// query. A false result is not evidence of an empty journal or of a rollback.
    /// </summary>
    /// <param name="rootPath">A path on the volume whose journal is queried.</param>
    /// <param name="journalId">The current journal identifier.</param>
    /// <param name="firstUsn">The first USN the journal can hold.</param>
    /// <param name="nextUsn">The next USN to be assigned.</param>
    /// <param name="lowestValidUsn">The lowest USN still retained.</param>
    /// <param name="maximumSize">The journal's maximum size in bytes.</param>
    /// <param name="allocationDelta">The journal's allocation delta in bytes.</param>
    /// <param name="nativeError">The native failure code where the platform supplied one; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> only when a coherent journal state was read.</returns>
    public static bool TryReadState(string rootPath, out ulong journalId, out ulong firstUsn, out ulong nextUsn,
        out ulong lowestValidUsn, out ulong maximumSize, out ulong allocationDelta, out int? nativeError)
    {
        nativeError = null;
        journalId = 0;
        firstUsn = 0;
        nextUsn = 0;
        lowestValidUsn = 0;
        maximumSize = 0;
        allocationDelta = 0;
        if (!TryQueryJournalState(rootPath, out var state, out var error, out _, out _))
        {
            if (error != 0) nativeError = error;
            return false;
        }
        journalId = state.JournalId;
        firstUsn = state.FirstUsn;
        nextUsn = state.NextUsn;
        lowestValidUsn = state.LowestValidUsn;
        maximumSize = state.MaximumSize;
        allocationDelta = state.AllocationDelta;
        return true;
    }

    /// <summary>
    /// Processes one native journal response, handing each record to <paramref name="record"/> without
    /// materializing it. A callback exception leaves the partially assembled page uncommittable.
    /// </summary>
    /// <param name="rootPath">A path on the volume whose journal is read.</param>
    /// <param name="journalId">The journal identifier to read; a different journal is classified, not read.</param>
    /// <param name="fromNextUsn">The first USN to return.</param>
    /// <param name="throughNextUsn">A finite end that no returned record may cross, or <see langword="null"/> for one open response.</param>
    /// <param name="waitForChange">Waits for any new record when the journal is at the start position; invalid with a finite end.</param>
    /// <param name="record">Receives each record's full file reference, parent reference, USN, native timestamp, reason flags and attributes.</param>
    /// <param name="ct">Cancels the pending native read; cancellation throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The classified page. A nonempty response is <see cref="NtfsUsnPageStatus.More"/> so the caller continues from the consumed boundary; an empty immediate response is <see cref="NtfsUsnPageStatus.Complete"/> so the caller waits instead of re-reading.</returns>
    public static Task<NtfsUsnPage> ReadChangePageAsync(string rootPath, ulong journalId, ulong fromNextUsn,
        ulong? throughNextUsn, bool waitForChange,
        Action<ulong, ulong, ulong, long, uint, FileAttributes> record, int pageBufferBytes, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(record);
        if (throughNextUsn is not null && throughNextUsn.Value < fromNextUsn)
            throw new ArgumentOutOfRangeException(nameof(throughNextUsn), "A finite end must not precede the start position.");
        if (waitForChange && throughNextUsn is not null)
            throw new ArgumentException("A finite page does not wait for new records.", nameof(waitForChange));
        if (ct.IsCancellationRequested) return Task.FromCanceled<NtfsUsnPage>(ct);
        return Task.Run(() => ReadChangePage(rootPath, journalId, fromNextUsn, throughNextUsn, waitForChange, record, pageBufferBytes, ct), ct);
    }

    private static NtfsUsnPage ReadChangePage(string rootPath, ulong journalId, ulong fromNextUsn,
        ulong? throughNextUsn, bool waitForChange,
        Action<ulong, ulong, ulong, long, uint, FileAttributes> record, int pageBufferBytes, CancellationToken ct)
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
            if (returned < sizeof(ulong)) return new NtfsUsnPage(NtfsUsnPageStatus.Unavailable, fromNextUsn, null);
            var continuation = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
            var status = WalkUsnRecords(buffer.AsSpan(sizeof(ulong), returned - sizeof(ulong)), fromNextUsn,
                throughNextUsn, continuation, out var consumed, (span, _) =>
                {
                    ReadUsnRecordFields(span, out var fileId, out var parentFileId, out var usn,
                        out var timestamp, out var reason, out var attributes);
                    record(fileId, parentFileId, usn, timestamp, reason, attributes);
                });
            return new NtfsUsnPage(status, consumed, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
        if (!TryReadState(rootPath, out var currentJournalId, out _, out _, out var lowestValidUsn, out _, out _, out _))
            return NtfsUsnPageStatus.Unavailable;
        if (currentJournalId != journalId) return NtfsUsnPageStatus.JournalChanged;
        return fromNextUsn < lowestValidUsn ? NtfsUsnPageStatus.HistoryLost : NtfsUsnPageStatus.Unavailable;
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

    /// <summary>
    /// Walks one native response, consuming no record whose end boundary crosses a finite end. The
    /// next record's USN, never USN plus record length, determines coverage.
    /// </summary>
    internal static NtfsUsnPageStatus WalkUsnRecords(ReadOnlySpan<byte> data, ulong start, ulong? end,
        ulong continuation, out ulong consumed, UsnPageRecord record)
    {
        consumed = start;
        if (data.IsEmpty) return continuation < start ? NtfsUsnPageStatus.NoProgress : NtfsUsnPageStatus.Complete;
        while (!data.IsEmpty)
        {
            if (data.Length < UsnRecordHeaderBytes) return NtfsUsnPageStatus.InvalidRecord;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (length < UsnRecordHeaderBytes || length > (uint)data.Length) return NtfsUsnPageStatus.InvalidRecord;
            var rest = data[(int)length..];
            if (!rest.IsEmpty && rest.Length < UsnRecordHeaderBytes) return NtfsUsnPageStatus.InvalidRecord;
            var boundary = rest.IsEmpty ? continuation : BinaryPrimitives.ReadUInt64LittleEndian(rest[24..]);
            if (boundary <= consumed) return NtfsUsnPageStatus.NoProgress;
            var usn = BinaryPrimitives.ReadUInt64LittleEndian(data[24..]);
            // A finite end taken from the native query can sit in the padding before the next journal page, so the
            // next record's USN may pass it. This record's own start and returned length decide; the returned V2 length
            // is never longer than the stored record, so a record before such an end is never left behind.
            if (end is not null && usn >= end.Value)
            {
                // No record starts before the finite end: the covered range is padding, so coverage reaches it.
                consumed = end.Value;
                break;
            }
            if (end is not null && usn + length > end.Value) break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != UsnRecordVersion2)
                return NtfsUsnPageStatus.UnsupportedRecord;
            if (usn >= start) record(data[..(int)length], boundary);
            consumed = end is not null && boundary > end.Value ? end.Value : boundary;
            data = rest;
        }
        if (end is not null) return consumed == end.Value ? NtfsUsnPageStatus.Complete : NtfsUsnPageStatus.More;
        return consumed > start ? NtfsUsnPageStatus.More : NtfsUsnPageStatus.Complete;
    }

    internal static void ReadUsnRecordFields(ReadOnlySpan<byte> record, out ulong fileId, out ulong parentFileId,
        out ulong usn, out long timestamp, out uint reason, out FileAttributes attributes)
    {
        fileId = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
        parentFileId = BinaryPrimitives.ReadUInt64LittleEndian(record[16..]);
        usn = BinaryPrimitives.ReadUInt64LittleEndian(record[24..]);
        timestamp = BinaryPrimitives.ReadInt64LittleEndian(record[32..]);
        reason = BinaryPrimitives.ReadUInt32LittleEndian(record[40..]);
        attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[52..]);
    }

    private static bool TryGetVolumePath(string rootPath, out string volumePath)
    {
        volumePath = string.Empty;
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
                EventHandle = completed.SafeWaitHandle.DangerousGetHandle()
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
        var directory = volumePath[4..] + "\\";
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
}
