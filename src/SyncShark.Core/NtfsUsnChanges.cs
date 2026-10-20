using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

public sealed record NtfsUsnChange(ulong FileId, ulong ParentFileId, long Usn, uint Reason,
    FileAttributes Attributes, string Name);

public static partial class NtfsUsnJournal
{
    private const uint FsctlReadUsnJournal = 0x000900bb;
    private const uint FsctlReadUnprivilegedUsnJournal = 0x000903ab;
    private const int MaximumRetainedChanges = 100_000;
    private static readonly ConcurrentDictionary<string, VolumeChanges> Volumes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One bounded journal reader/cache per volume, shared by every pair on that volume.
    /// Full 64-bit file references retain the NTFS sequence number; recycled MFT slots are not identities.
    /// No journal creation, elevation, hydration, or write access is requested.</summary>
    public static bool TryReadChanges(string rootPath, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
        out IReadOnlyList<NtfsUsnChange> changes, CancellationToken cancellationToken = default) =>
        TryReadChanges(rootPath, from, through, out changes, out _, cancellationToken);

    internal static bool TryReadChanges(string rootPath, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
        out IReadOnlyList<NtfsUsnChange> changes, out string? failure, CancellationToken cancellationToken)
    {
        failure = null;
        changes = [];
        if (!OperatingSystem.IsWindows()) { failure = "USN requires Windows"; return false; }
        if (through.RequiresFullReconciliation(from.Cursor)) { failure = "journal checkpoint range invalid"; return false; }
        if (from.NextUsn == through.NextUsn) return true;
        var volume = Path.GetPathRoot(Path.GetFullPath(rootPath));
        if (volume is null || volume.Length < 2 || volume[1] != ':') { failure = "root is not a local drive"; return false; }
        var cache = Volumes.GetOrAdd(volume, _ => new VolumeChanges());
        lock (cache)
        {
            try
            {
                var reuse = cache.JournalId == through.JournalId && from.NextUsn >= cache.Start &&
                            from.NextUsn <= cache.End && through.NextUsn >= cache.End;
                var start = reuse ? cache.End : from.NextUsn;
                using var handle = OpenReadOnlyVolume("\\\\.\\" + char.ToUpperInvariant(volume[0]) + ":");
                if (handle.IsInvalid) { failure = "volume open failed: Windows error " + Marshal.GetLastWin32Error(); return false; }
                var added = ReadChanges(handle, start, through.NextUsn, through.JournalId, cancellationToken);
                if (!TryReadCheckpoint(rootPath, out var verified) || verified!.JournalId != through.JournalId ||
                    verified.RequiresFullReconciliation(from.Cursor)) { failure = "journal coverage changed during read"; return false; }
                if (!reuse) { cache.Changes.Clear(); cache.Start = from.NextUsn; }
                cache.Changes.AddRange(added);
                cache.End = through.NextUsn;
                cache.JournalId = through.JournalId;
                // Return the caller's complete range before bounding the reusable tail.
                changes = cache.Changes.Where(change => change.Usn >= from.NextUsn &&
                    change.Usn < through.NextUsn).ToArray();
                if (cache.Changes.Count > MaximumRetainedChanges)
                {
                    cache.Changes.RemoveRange(0, cache.Changes.Count - MaximumRetainedChanges);
                    cache.Start = cache.Changes[0].Usn;
                }
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              OverflowException or ArgumentException)
            {
                failure = LastReadFailure = exception.Message;
                cache.Changes.Clear(); cache.JournalId = 0;
                return false;
            }
        }
    }

    private static IReadOnlyList<NtfsUsnChange> ReadChanges(SafeFileHandle volume, long start, long end,
        ulong journalId, CancellationToken cancellationToken)
    {
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
            result.AddRange(ParseChanges(output.AsSpan(8, count - 8), start, end));
            if (result.Count > 250_000) throw new IOException("The bounded NTFS change range was exceeded.");
            cursor = next;
        }
        return result;
    }

    internal static IReadOnlyList<NtfsUsnChange> ParseChanges(ReadOnlySpan<byte> data, long start, long end)
    {
        var result = new List<NtfsUsnChange>();
        while (!data.IsEmpty)
        {
            if (data.Length < 60) throw new IOException("An NTFS change record is truncated.");
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data));
            if (length < 60 || length > data.Length || BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != 2)
                throw new IOException("An unsupported NTFS change record requires a full scan.");
            var record = data[..length];
            var usn = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
            var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
            if (nameOffset < 60 || (nameLength & 1) != 0 || nameOffset + nameLength > length)
                throw new IOException("An NTFS change name is malformed.");
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

    private sealed class VolumeChanges
    {
        public ulong JournalId;
        public long Start;
        public long End;
        public List<NtfsUsnChange> Changes { get; } = [];
    }

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlBytes(SafeFileHandle device, uint controlCode,
        byte[]? inputBuffer, int inputSize, byte[] outputBuffer, int outputSize,
        out int bytesReturned, IntPtr overlapped);
}
