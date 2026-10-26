namespace Massdex.SyncShark;

/// <summary>
/// One Dropbox name-identity rule shared by the full listing, the delta collapse and prefix hydration.
/// <see cref="Key"/> is the storage path key, whose equality is exactly OrdinalIgnoreCase (StorageChecks
/// proves it); two spellings with equal keys name one occupant and must never both be published silently.
/// </summary>
internal static class DropboxPathIdentity
{
    public static string Key(string relativePath) => V2.PathKey.Of(relativePath);

    /// <summary>True when the path is a scoped failure or lies under one (never inferred absent).</summary>
    public static bool InScope(IReadOnlyDictionary<string, string> scoped, string relativePath)
    {
        if (scoped.Count == 0) return false;
        if (scoped.ContainsKey(relativePath)) return true;
        foreach (var ancestor in V2.PairPath.Ancestors(relativePath))
            if (scoped.ContainsKey(ancestor)) return true;
        return false;
    }

    /// <summary>Exact user-facing cause for two live objects at one case-insensitive path.</summary>
    public const string CollisionReason =
        "Dropbox reported two different items at the same case-insensitive path. " +
        "Neither is used and nothing at this path is synced or deleted. Rename one of them in Dropbox, then Retry.";

    /// <summary>Exact user-facing cause for one entry whose metadata cannot be read safely.</summary>
    public const string MalformedReason =
        "Dropbox returned metadata that could not be read safely for this item. Nothing at this path is synced or deleted. " +
        "Retry; if it persists, rename or re-upload the item in Dropbox.";
}
