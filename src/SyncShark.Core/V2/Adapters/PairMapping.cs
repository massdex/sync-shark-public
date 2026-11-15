namespace Massdex.SyncShark.V2;

/// <summary>Projects one stored pair definition onto the window's SyncPair view.</summary>
internal static class PairMapping
{
    internal static WindowsCloudFilesBinding CloudFilesBinding(PairDef pair)
    {
        if (string.IsNullOrWhiteSpace(pair.PairId) || pair.A.Provider != SyncProviders.Local ||
            pair.B.Provider != SyncProviders.Dropbox || string.IsNullOrWhiteSpace(pair.B.AccountId) ||
            string.IsNullOrWhiteSpace(pair.B.RootObjectId))
            throw new InvalidDataException("Cloud Files requires a stable local–Dropbox pair and account/root identity.");
        var (volume, fileId) = LocalItems.ParseObjectId(pair.A.RootObjectId);
        return new(pair.PairId, pair.B.Provider, pair.B.AccountId, Path.GetFullPath(pair.A.RootPath),
            volume, fileId, pair.B.RootPath, pair.B.RootObjectId);
    }

    /// <summary>Maps a local–cloud pair for display; any other root shape is not shown in core v2.</summary>
    internal static SyncPair ToSyncPair(PairDef pair, bool firstSyncDone, V2Settings settings)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (!string.Equals(pair.A.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pair.B.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only local–cloud pairs are shown in core v2.");
        var (volume, fileId) = LocalItems.ParseObjectId(pair.A.RootObjectId);
        return new SyncPair(
            Id: pair.PairId,
            Name: pair.Name,
            LocalRoot: pair.A.RootPath,
            LocalVolumeSerialNumber: volume,
            LocalRootFileId: fileId,
            DropboxRoot: pair.B.RootPath,
            DropboxFolderId: pair.B.RootObjectId,
            Enabled: pair.Enabled,
            PropagateDeletions: true,
            SelectionRules: pair.Excluded.Select(path => new SyncSelectionRule(path, Included: false)).ToArray(),
            ConnectionId: pair.B.AccountId ?? "",
            Provider: pair.B.Provider,
            InitialReviewApproved: firstSyncDone,
            RecoveryRetentionDays: (int)settings.RecoveryRetention.TotalDays,
            IgnoreRules: PairInclude.IgnoreRules(pair));
    }
}
