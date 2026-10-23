using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

public sealed record NtfsUsnCheckpoint(
    ulong JournalId,
    long FirstUsn,
    long NextUsn,
    long LowestValidUsn)
{
    public string Cursor => $"{JournalId:x16}:{NextUsn}";

    public static bool TryParseCursor(string? value, out ulong journalId, out long nextUsn)
    {
        journalId = 0;
        nextUsn = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split(':');
        return parts.Length == 2 && ulong.TryParse(parts[0],
                   System.Globalization.NumberStyles.HexNumber,
                   System.Globalization.CultureInfo.InvariantCulture, out journalId) &&
               long.TryParse(parts[1], out nextUsn);
    }

    public bool RequiresFullReconciliation(string? retainedCursor)
    {
        if (!TryParseCursor(retainedCursor, out var retainedJournalId, out var retainedNextUsn)) return true;
        return retainedJournalId != JournalId || retainedNextUsn < Math.Max(FirstUsn, LowestValidUsn) ||
               retainedNextUsn > NextUsn;
    }
}

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
    internal static string? LastReadFailure { get; private set; }

    public static bool TryReadCheckpoint(string rootPath, out NtfsUsnCheckpoint? checkpoint) =>
        TryReadCheckpoint(rootPath, out checkpoint, out _);

    internal static bool TryReadCheckpoint(string rootPath, out NtfsUsnCheckpoint? checkpoint, out bool journalDeleted)
    {
        checkpoint = null;
        journalDeleted = false;
        if (!OperatingSystem.IsWindows()) return false;
        var full = Path.GetFullPath(rootPath);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':') return false;
        var volumePath = "\\\\.\\" + char.ToUpperInvariant(root[0]) + ":";
        using var handle = OpenReadOnlyVolume(volumePath);
        if (handle.IsInvalid) { LastReadFailure = "Open volume: " + Marshal.GetLastWin32Error(); return false; }
        var buffer = new byte[80];
        if (!DeviceIoControlBytes(handle, FsctlQueryUsnJournal, null, 0, buffer, buffer.Length,
                out var returned, IntPtr.Zero) || returned < 56)
        {
            var error = Marshal.GetLastWin32Error();
            journalDeleted = error is 1178 or 1179; // JOURNAL_DELETE_IN_PROGRESS / JOURNAL_NOT_ACTIVE
            LastReadFailure = "Query journal: " + error + "; bytes=" + returned;
            return false;
        }
        checkpoint = new NtfsUsnCheckpoint(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buffer),
            System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8)),
            System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16)),
            System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24)));
        if (checkpoint.JournalId == 0 || checkpoint.NextUsn < checkpoint.FirstUsn || checkpoint.LowestValidUsn > checkpoint.NextUsn)
        { checkpoint = null; return false; }
        LastReadFailure = null; return true;
    }

    public static NtfsUsnCheckpoint ReadCheckpoint(string rootPath) =>
        TryReadCheckpoint(rootPath, out var checkpoint)
            ? checkpoint!
            : throw new IOException("The NTFS USN journal is unavailable; directory notifications and periodic reconciliation remain active.");

    private static SafeFileHandle OpenReadOnlyVolume(string volumePath)
    {
        var handle = CreateFile(volumePath, GenericRead, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        // Standard users can query/read the unprivileged journal through the volume's root
        // directory handle. A zero-access raw device handle opens but returns INVALID_FUNCTION
        // on current Windows; do not mistake that for an unavailable NTFS journal.
        var directory = volumePath[4..] + "\\";
        return CreateFile(directory, 0, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, OpenExisting, 0x02000000, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

}
