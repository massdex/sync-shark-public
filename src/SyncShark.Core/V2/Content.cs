namespace Massdex.SyncShark.V2;

/// <summary>The only content and version comparisons; shared by the Planner and the Executor's EffectResolver.</summary>
public static class Content
{
    /// <summary>
    /// one compact side identity for the prepare record — the object id plus the strongest
    /// content marker the endpoint offers (folder kind, version, hash, or size + modification ticks). Two equal
    /// stamps mean the same object with unchanged content; a mismatch conservatively means "changed".
    /// </summary>
    public static string SideStamp(ItemState item) =>
        item.ObjectId + "|" + (item.Kind == ItemKind.Folder
            ? "d"
            : item.VersionId is not null ? "v:" + item.VersionId
            : item.Hash is not null ? "h:" + item.Hash
            : MetadataStamp(item));

    /// <summary>
    /// The metadata-only content marker (size + modification ticks). Used both as the weakest prepare stamp and to
    /// re-check a prepare that recorded this kind months before a hash became available finding 2
    /// of the third review): the comparison follows the KIND the prepare saved, never upgrades it.
    /// </summary>
    public static string MetadataStamp(ItemState item) =>
        "m:" + item.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
        item.Mtime.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="cur"/> still matches the baseline state on the same side; null needs a hash.</summary>
    public static bool? SameVersion(ItemState cur, ItemState baseSameSide)
    {
        if (cur.Kind != baseSameSide.Kind) return false;
        if (cur.Kind == ItemKind.Folder) return true;
        if (cur.Hash is not null && baseSameSide.Hash is not null)
            return string.Equals(cur.Hash, baseSameSide.Hash, StringComparison.OrdinalIgnoreCase);
        if (cur.Size != baseSameSide.Size) return false;
        if (cur.VersionId is not null && baseSameSide.VersionId is not null)
            return string.Equals(cur.VersionId, baseSameSide.VersionId, StringComparison.Ordinal);
        if (cur.Mtime == baseSameSide.Mtime) return true;
        return null;
    }

    /// <summary>Cross-side equality of two items' content; null needs a hash (both sides share one hash algorithm).</summary>
    public static bool? SameContent(ItemState x, ItemState y)
    {
        if (x.Kind != y.Kind) return false;
        if (x.Kind == ItemKind.Folder) return true;
        if (x.Hash is not null && y.Hash is not null)
            return string.Equals(x.Hash, y.Hash, StringComparison.OrdinalIgnoreCase);
        if (x.Size != y.Size) return false;
        return null;
    }
}
