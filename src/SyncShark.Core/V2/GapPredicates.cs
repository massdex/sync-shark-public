namespace Massdex.SyncShark.V2;

internal static class GapPredicates
{
    internal static bool SameJournal(string a, string b) =>
        NtfsUsnCheckpoint.TryParseCursor(a, out var first, out _) &&
        NtfsUsnCheckpoint.TryParseCursor(b, out var second, out _) && first == second;

    internal static bool MetadataOnly(ItemState item) => item.Kind != ItemKind.File ||
        (item.Flags & (ItemFlags.Ignored | ItemFlags.Link | ItemFlags.Placeholder | (ItemFlags)128)) != 0;
}
