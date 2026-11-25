using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static Massdex.SyncShark.Engine.Local.LocalErrors;
using System.Text;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Reads volume coverage and cancellable raw pages; proves publication on a held file only.</summary>
public class UsnJournal
{
    private const uint FsctlReadUsnJournal = 0x000900bb, FsctlReadUnprivilegedUsnJournal = 0x000903ab;
    private const uint FsctlWriteUsnCloseRecord = 0x000900ef;
    // fileapi.h VOLUME_NAME_GUID = 0x1.
    private const uint VolumeNameGuid = 0x1;
    // winioctl.h READ_USN_JOURNAL_DATA_V0 is 40 bytes.
    private const int ReadJournalDataBytes = 40;
    // winioctl.h USN_RECORD_V2 FileName offset 60.
    internal const int UsnRecordHeaderBytes = 60;
    private const ushort UsnRecordVersion2 = 2; // winioctl.h USN_RECORD_V2.MajorVersion
    // The observed pended-read transient; the adopted one-retry policy is in engine design section 8.7.
    // winerror.h ERROR_INVALID_USER_BUFFER = 1784.
    private const int ErrorInvalidUserBuffer = 1784;
    // winerror.h ERROR_JOURNAL_DELETE_IN_PROGRESS, ERROR_JOURNAL_NOT_ACTIVE, ERROR_JOURNAL_ENTRY_DELETED.
    private const int ErrorJournalDeleteInProgress = 1178;
    private const int ErrorJournalNotActive = 1179;
    private const int ErrorJournalEntryDeleted = 1181;
    public virtual JournalPosition Current(string root) => Retained(root).Tail;

    /// <summary>The retained inclusive continuation range from one volume query.</summary>
    public virtual (ulong First,JournalPosition Tail) Retained(string root) => JournalDevice.TryQueryJournalState(root,out var state,out var error,out _,out _)
        ? (Math.Max(state.FirstUsn,state.LowestValidUsn),new(state.JournalId,state.NextUsn)) : throw Failure(NtfsUsnPageStatus.Unavailable,error);

    internal static JournalSourceException Failure(NtfsUsnPageStatus status,int? error) =>
        new(status,error,Text.Get(TextKey.JournalSourceFailed,(TextArg.Reason,status.ToString()),(TextArg.Value,error?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)));

    /// <summary>Force the publishing handle's own close record, bracketed by queries to its volume GUID.</summary>
    public JournalPosition? Published(SafeFileHandle file)
    {
        var size=GetFinalPathNameByHandle(file,null,0,VolumeNameGuid);
        if (size==0) return null;
        var path=new StringBuilder(checked((int)size));
        var len=GetFinalPathNameByHandle(file,path,size,VolumeNameGuid);
        if (len==0 || len>=size) return null;
        var end=path.ToString().IndexOf('}',StringComparison.Ordinal);
        if (end<0) return null;
        var volume=path.ToString(0,end+1);
        if (!JournalDevice.TryQueryJournalState(volume,out var before,out _,out _,out _)) return null;
        var bytes=new byte[sizeof(ulong)];
        if (!JournalDevice.TryDeviceIoControlPage(file,FsctlWriteUsnCloseRecord,[],bytes,out var count,out _,CancellationToken.None) || count!=bytes.Length) return null;
        var usn=BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return usn>=before.NextUsn && JournalDevice.TryQueryJournalState(volume,out var after,out _,out _,out _) &&
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
        if (!JournalDevice.TryGetVolumePath(rootPath, out var volumePath))
            return new NtfsUsnPage(NtfsUsnPageStatus.Unavailable, fromNextUsn, null);
        using var handle = JournalDevice.OpenReadOnlyVolume(volumePath, overlapped: true);
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
            var failure=TryReadRecord(ref data,ref consumed,start,end,continuation,records);
            if (failure.HasValue) return Failed(failure.Value);
        }
        bool complete=end.HasValue ? consumed==end.Value : consumed==start;
        return new(complete ? NtfsUsnPageStatus.Complete : NtfsUsnPageStatus.More,consumed,null,records);
    }

    private static NtfsUsnPageStatus? TryReadRecord(ref ReadOnlySpan<byte> data,ref ulong consumed,ulong start,ulong? end,
        ulong continuation,List<UsnRecord> records)
    {
        // winioctl.h USN_RECORD_COMMON_HEADER: RecordLength, MajorVersion, MinorVersion.
        if (data.Length>=sizeof(uint)+sizeof(ushort)+sizeof(ushort) && BinaryPrimitives.ReadUInt16LittleEndian(data[4..])!=UsnRecordVersion2) return NtfsUsnPageStatus.UnsupportedRecord;
        if (data.Length < UsnRecordHeaderBytes) return NtfsUsnPageStatus.InvalidRecord;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (length < UsnRecordHeaderBytes || length > (uint)data.Length) return NtfsUsnPageStatus.InvalidRecord;
        var rest = data[(int)length..];
        if (!rest.IsEmpty && rest.Length < UsnRecordHeaderBytes) return NtfsUsnPageStatus.InvalidRecord;
        if (!rest.IsEmpty && BinaryPrimitives.ReadUInt16LittleEndian(rest[4..])!=UsnRecordVersion2) return NtfsUsnPageStatus.UnsupportedRecord;
        var boundary = rest.IsEmpty ? continuation : BinaryPrimitives.ReadUInt64LittleEndian(rest[24..]);
        if (boundary <= consumed) return NtfsUsnPageStatus.NoProgress;
        var usn = BinaryPrimitives.ReadUInt64LittleEndian(data[24..]);
        if (usn>=boundary) return NtfsUsnPageStatus.InvalidRecord;
        // A finite end taken from the native query can sit in the padding before the next journal page, so the
        // next record's USN may pass it. This record's own start and returned length decide; the returned V2 length
        // is never longer than the stored record, so a record before such an end is never left behind.
        if (end is not null && usn >= end.Value)
        {
            // No record starts before the finite end: the covered range is padding, so coverage reaches it.
            consumed = end.Value;
            data=default;
            return null;
        }
        if (end is not null && length > end.Value-usn) return NtfsUsnPageStatus.InvalidRecord;
        var nameLength=BinaryPrimitives.ReadUInt16LittleEndian(data[56..]);
        var nameOffset=BinaryPrimitives.ReadUInt16LittleEndian(data[58..]);
        if ((nameLength & 1)!=0 || (nameLength!=0 && (nameOffset<UsnRecordHeaderBytes || nameOffset+nameLength>length))) return NtfsUsnPageStatus.InvalidRecord;
        if (usn>=start) records.Add(new(BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[16..]),usn,BinaryPrimitives.ReadUInt32LittleEndian(data[40..]),
            (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(data[52..]),
            nameLength==0 ? null : Encoding.Unicode.GetString(data.Slice(nameOffset,nameLength))));
        consumed = end is not null && boundary > end.Value ? end.Value : boundary;
        data = rest;
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static int ReadPageNative(SafeFileHandle volume, ulong journalId, ulong startUsn, bool waitForChange,
        byte[] output, out int bytesReturned, CancellationToken ct)
    {
        var input = BuildReadJournalInput(startUsn, journalId, waitForChange);
        if (JournalDevice.TryDeviceIoControlPage(volume, FsctlReadUsnJournal, input, output, out bytesReturned, out var error, ct))
            return 0;
        if (error is ErrorAccessDenied or ErrorInvalidFunction &&
            JournalDevice.TryDeviceIoControlPage(volume, FsctlReadUnprivilegedUsnJournal, input, output, out bytesReturned, out error, ct))
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
        if (!JournalDevice.TryQueryJournalState(rootPath,out var state,out _,out _,out _))
            return NtfsUsnPageStatus.Unavailable;
        if (state.JournalId != journalId) return NtfsUsnPageStatus.JournalChanged;
        return fromNextUsn < Math.Max(state.FirstUsn,state.LowestValidUsn) || fromNextUsn>state.NextUsn ? NtfsUsnPageStatus.HistoryLost : NtfsUsnPageStatus.Unavailable;
    }

    private static byte[] BuildReadJournalInput(ulong startUsn, ulong journalId, bool waitForChange)
    {
        const ulong firstAvailableByte = 1; // winioctl.h READ_USN_JOURNAL_DATA_V0.BytesToWaitFor: wake on the first available byte.
        var input = new byte[ReadJournalDataBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(input, startUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);
        // Native Timeout is not a deadline; it only bounds the wait for BytesToWaitFor record bytes.
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(16), waitForChange ? ulong.MaxValue : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(24), waitForChange ? firstAvailableByte : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), journalId);
        return input;
    }
}
