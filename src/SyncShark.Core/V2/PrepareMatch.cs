namespace Massdex.SyncShark.V2;

/// <summary>
/// The prepare-time identity and content stamp of a path matched on both sides, as persisted in the prepare record
/// . A reviewed apply plans a later one-sided survivor as ordinary deletion propagation only when
/// it still equals this state; a changed survivor wins instead (RQ-043).
/// </summary>
public sealed record PrepareMatch(string A, string B)
{
    /// <summary>
    /// Whether the survivor on <paramref name="present"/> is still exactly the prepare-time state. The comparison
    /// follows the stamp KIND recorded at prepare , third-review finding 2): a metadata stamp is
    /// re-checked from size + modification ticks even when a hash is now available, and a hash stamp is re-checked
    /// against the hash (unavailable now = conservatively changed, so the change wins as a safe copy-back).
    /// </summary>
    public bool Unchanged(Side present, ItemState current)
    {
        var stored = present == Side.A ? A : B;
        var split = stored.IndexOf('|');
        if (split < 0) return false;
        if (!string.Equals(stored[..split], current.ObjectId, StringComparison.Ordinal)) return false;
        var kind = stored[(split + 1)..];
        if (kind == "d") return current.Kind == ItemKind.Folder;
        if (current.Kind != ItemKind.File) return false;
        if (kind.StartsWith("m:", StringComparison.Ordinal))
            return string.Equals(kind, Content.MetadataStamp(current), StringComparison.Ordinal);
        if (kind.StartsWith("h:", StringComparison.Ordinal))
            return Content.SameHash(kind[2..], current.Hash) == true;
        if (kind.StartsWith("v:", StringComparison.Ordinal))
            return current.VersionId is not null &&
                string.Equals(kind, "v:" + current.VersionId, StringComparison.Ordinal);
        return false;
    }
}
