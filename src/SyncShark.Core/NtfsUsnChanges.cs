using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

public sealed record NtfsUsnChange(ulong FileId, ulong ParentFileId, long Usn, uint Reason,
    FileAttributes Attributes, string Name);

internal enum NtfsUsnReadFailure { Transient, OverLimit, Permanent }

public static partial class NtfsUsnJournal
{
    private sealed class RangeLimitException() : IOException(RangeLimitExceeded) { }
    private sealed class PermanentRecordException(string message) : IOException(message) { }
    internal static NtfsUsnReadFailure ClassifyReadFailure(Exception error) => error switch
    {
        RangeLimitException => NtfsUsnReadFailure.OverLimit,
        PermanentRecordException => NtfsUsnReadFailure.Permanent,
        _ => NtfsUsnReadFailure.Transient
    };
    private const uint FsctlReadUsnJournal = 0x000900bb;
    private const uint FsctlReadUnprivilegedUsnJournal = 0x000903ab;
    internal const string RangeLimitExceeded = "The bounded NTFS change range was exceeded.";

    /// <summary>Bounded journal reads without retaining earlier windows in a volume cache.
    /// Full 64-bit file references retain the NTFS sequence number; recycled MFT slots are not identities.
    /// No journal creation, elevation, hydration, or write access is requested.</summary>
    public static bool TryReadChanges(string rootPath, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
        out IReadOnlyList<NtfsUsnChange> changes, CancellationToken cancellationToken = default) =>
        TryReadChanges(rootPath, from, through, out changes, out _, cancellationToken);

    internal static bool TryReadChanges(string rootPath, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
        out IReadOnlyList<NtfsUsnChange> changes, out string? failure, CancellationToken cancellationToken) =>
        TryReadChanges(rootPath, from, through, out changes, out failure, out _, cancellationToken);

    internal static bool TryReadChanges(string rootPath, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
        out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken cancellationToken)
    {
        if (!TryReadChanges(rootPath, from, through, out changes, out failure, out kind, out var consumed, cancellationToken)) return false;
        if (consumed == through.NextUsn) return true;
        // Legacy callers cannot publish a partial boundary. Fail closed, never silently
        // authorize their requested checkpoint when the reader stopped short of it.
        changes = [];
        failure = RangeLimitExceeded;
        kind = NtfsUsnReadFailure.OverLimit;
        return false;
    }

    internal static bool TryReadChanges(string rootPath, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
        out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind,
        out long consumed, CancellationToken cancellationToken)
    {
        consumed = from.NextUsn;
        kind = NtfsUsnReadFailure.Transient;
        failure = null;
        changes = [];
        if (!OperatingSystem.IsWindows()) { failure = "USN requires Windows"; return false; }
        if (through.RequiresFullReconciliation(from.Cursor)) { failure = "journal checkpoint range invalid"; return false; }
        if (from.NextUsn == through.NextUsn) return true;
        var volume = Path.GetPathRoot(Path.GetFullPath(rootPath));
        if (volume is null || volume.Length < 2 || volume[1] != ':') { failure = "root is not a local drive"; return false; }
        try
        {
            using var handle = OpenReadOnlyVolume("\\\\.\\" + char.ToUpperInvariant(volume[0]) + ":");
            if (handle.IsInvalid) { failure = "volume open failed: Windows error " + Marshal.GetLastWin32Error(); return false; }
            var added = ReadChanges(handle, from.NextUsn, through.NextUsn, through.JournalId, out var end, cancellationToken);
            if (!TryReadCheckpoint(rootPath, out var verified) || verified!.JournalId != through.JournalId ||
                verified.RequiresFullReconciliation(from.Cursor)) { failure = "journal coverage changed during read"; return false; }
            changes = added;
            consumed = end;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          OverflowException or ArgumentException)
        {
            kind = ClassifyReadFailure(exception);
            failure = LastReadFailure = exception.Message;
            return false;
        }
    }

    private static IReadOnlyList<NtfsUsnChange> ReadChanges(SafeFileHandle volume, long start, long end,
        ulong journalId, out long consumed, CancellationToken cancellationToken)
    {
        consumed = start;
        var input = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), journalId);
        var output = new byte[1024 * 1024];
        var result = new List<NtfsUsnChange>();
        var cursor = start;
        while (cursor < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteInt64LittleEndian(input, cursor);
            var read = DeviceIoControlBytes(volume, FsctlReadUsnJournal, input, input.Length,
                output, output.Length, out var count, IntPtr.Zero);
            if (!read && Marshal.GetLastWin32Error() is 5 or 1)
                read = DeviceIoControlBytes(volume, FsctlReadUnprivilegedUsnJournal, input, input.Length,
                    output, output.Length, out count, IntPtr.Zero);
            if (!read || count < 8)
                throw new IOException("The complete NTFS change range is unavailable (Windows error " + Marshal.GetLastWin32Error() + ").");
            var next = BinaryPrimitives.ReadInt64LittleEndian(output);
            if (next <= cursor) throw new IOException("The NTFS cursor did not advance.");
            result.AddRange(ParseWindow(output.AsSpan(8, count - 8), cursor, end, next, out var recordEnd));
            if (result.Count > 250_000) throw new RangeLimitException();
            consumed = Math.Max(consumed, recordEnd);
            // The kernel continuation is authoritative only if this buffer was consumed in full.
            if (next > end) break;
            consumed = cursor = next;
        }
        return result;
    }

    // A requested window end can bisect a record. Leave that record for the next read,
    // reporting the actual consumed boundary rather than the requested byte offset.
    internal static IReadOnlyList<NtfsUsnChange> ParseWindow(ReadOnlySpan<byte> data, long start, long end,
        long continuation, out long consumed)
    {
        consumed = start;
        var result = new List<NtfsUsnChange>();
        while (!data.IsEmpty)
        {
            if (data.Length < 60) throw new PermanentRecordException("An NTFS change record is truncated.");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (length < 60 || length > data.Length)
                throw new PermanentRecordException("An NTFS change record is malformed.");
            var rest = data[(int)length..];
            if (!rest.IsEmpty && rest.Length < 60) throw new PermanentRecordException("An NTFS change record is truncated.");
            // Unprivileged journal reads may redact names, so returned RecordLength is
            // not an authoritative on-disk USN stride. Use the next record's USN or
            // the kernel continuation, never usn + returned RecordLength.
            var next = rest.IsEmpty ? continuation : BinaryPrimitives.ReadInt64LittleEndian(rest[24..]);
            if (next > end) break;
            if (next <= consumed) throw new IOException("The NTFS cursor did not advance.");
            result.AddRange(ParseChanges(data[..(int)length], start, end));
            consumed = next;
            data = rest;
        }
        return result;
    }

    internal static IReadOnlyList<NtfsUsnChange> ParseChanges(ReadOnlySpan<byte> data, long start, long end)
    {
        var result = new List<NtfsUsnChange>();
        while (!data.IsEmpty)
        {
            if (data.Length < 60) throw new PermanentRecordException("An NTFS change record is truncated.");
            var rawLength = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (rawLength < 60 || rawLength > (uint)data.Length || BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != 2)
                throw new PermanentRecordException("An unsupported NTFS change record requires a full scan.");
            var length = (int)rawLength;
            var record = data[..length];
            var usn = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
            var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
            if (nameOffset < 60 || (nameLength & 1) != 0 || nameOffset + nameLength > length)
                throw new PermanentRecordException("An NTFS change name is malformed.");
            if (usn >= start && usn < end)
                result.Add(new NtfsUsnChange(BinaryPrimitives.ReadUInt64LittleEndian(record[8..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(record[16..]), usn,
                    BinaryPrimitives.ReadUInt32LittleEndian(record[40..]),
                    (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[52..]),
                    Encoding.Unicode.GetString(record.Slice(nameOffset, nameLength))));
            data = data[length..];
        }
        return result;
    }

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlBytes(SafeFileHandle device, uint controlCode,
        byte[]? inputBuffer, int inputSize, byte[] outputBuffer, int outputSize,
        out int bytesReturned, IntPtr overlapped);
}
