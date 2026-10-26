namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private static bool JournalSelectionChanged(ItemState item, ItemState previous, long? usn,
        ILocalGapProbe? probes, ulong journal, long savedNext, List<ItemState> invalidated)
    {
        // Cursor stores NextUsn: the next UNPROCESSED record, so equality is not proof of unchanged content.
        // This supersedes the design's original <= saved-cursor shorthand.
        bool changed;
        if (usn is { } current) changed = current >= savedNext;
        else if (SelectionChanged(item, previous, false)) changed = true;
        else
        {
            var proof = probes?.Probe(item);
            changed = proof is null || !NtfsUsnCheckpoint.TryParseCursor(proof.JournalCursor, out var currentJournal, out _) ||
                currentJournal != journal || proof.File.Usn >= savedNext;
        }
        if (changed && previous.Kind == ItemKind.File && !previous.Flags.HasFlag(ItemFlags.NeedsRehash))
            invalidated.Add(previous);
        return changed;
    }
}
