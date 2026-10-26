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
