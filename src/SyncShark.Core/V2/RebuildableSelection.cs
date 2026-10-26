namespace Massdex.SyncShark.V2;

/// <summary>
/// The rebuildable folders a first-sync preview stops syncing, and the cloud side whose copies are removed. The cloud
/// side is derived from the pair's endpoint kinds, never assumed to be side B.
/// </summary>
public sealed record RebuildableSelection(Side Cloud, IReadOnlyList<string> Folders)
{
    /// <summary>
    /// A pair's one local side and its one Dropbox side, or null when the pair is not exactly one local side and one
    /// Dropbox side. The rebuildables choice is only offered for such a pair.
    /// </summary>
    public static (Side Local, Side Cloud)? Sides(PairDef pair)
    {
        Side? local = null, cloud = null;
        foreach (var side in new[] { Side.A, Side.B })
        {
            var provider = side == Side.A ? pair.A.Provider : pair.B.Provider;
            if (string.Equals(provider, ProviderIds.Local, StringComparison.OrdinalIgnoreCase))
            {
                if (local is not null) return null;
                local = side;
            }
            else if (string.Equals(provider, ProviderIds.Dropbox, StringComparison.OrdinalIgnoreCase))
            {
                if (cloud is not null) return null;
                cloud = side;
            }
        }
        return local is not null && cloud is not null ? (local.Value, cloud.Value) : null;
    }
}
