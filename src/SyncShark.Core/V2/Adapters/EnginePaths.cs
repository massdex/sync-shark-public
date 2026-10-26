namespace Massdex.SyncShark.V2;

/// <summary>The folders the engine writes outside the pair roots: the database, the Dropbox spool and local Recovery.</summary>
/// <remarks>
/// Recovery is <see cref="SyncTransferSettings.LocalRecoveryRoot"/> from the product settings store (decision 55),
/// never a code default.
/// </remarks>
public sealed record EnginePaths(string ProductRoot, string? RecoveryRoot)
{
    /// <summary>The one database file the store opens.</summary>
    public string DatabasePath => SqliteStateStore.DatabasePath(ProductRoot);

    /// <summary>The spool folder for Dropbox downloads before they are placed.</summary>
    public string Spool => Path.Combine(ProductRoot, "v2", "spool");

    /// <summary>The full local Recovery root, or a refusal (before any effect) when it is missing or unusable.</summary>
    public string RequireRecoveryRoot()
    {
        if (string.IsNullOrWhiteSpace(RecoveryRoot))
            throw new SyncRunRefusedException(
                "No local Recovery location is set in the Sync Shark settings, so local changes cannot be protected.");
        var configured = RecoveryRoot;
        if (!Path.IsPathFullyQualified(configured) || configured.Length < 3 || !char.IsAsciiLetter(configured[0]) ||
            configured[1] != ':' || configured[2] is not ('\\' or '/'))
            throw new SyncRunRefusedException("The Recovery location '" + configured +
                                              "' must be an absolute folder on a local drive.");
        var full = Path.GetFullPath(configured);
        if (!Directory.Exists(Path.GetPathRoot(full)))
            throw new SyncRunRefusedException("The drive of the Recovery location '" + full + "' is not available.");
        if (File.Exists(full))
            throw new SyncRunRefusedException("The Recovery location '" + full + "' is a file, not a folder.");
        return full;
    }

    /// <summary>
    /// The absolute folders a local scan or watch never inventories: the data folder, the spool and Recovery (when
    /// one is given).
    /// </summary>
    public IReadOnlySet<string> ProductOwnedPaths(string? recoveryRoot)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(ProductRoot), Path.GetFullPath(Spool)
        };
        if (!string.IsNullOrWhiteSpace(recoveryRoot) && Path.IsPathFullyQualified(recoveryRoot))
            owned.Add(Path.GetFullPath(recoveryRoot));
        return owned;
    }
}
