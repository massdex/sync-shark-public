namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    public Task<SyncCliAvailabilityData> SetStorageIntentAsync(string pairId, string path, string mode,
        CancellationToken cancellationToken) => SetAvailabilityAsync(pairId, path, mode, cancellationToken);

    /// <inheritdoc />
    public Task<SyncCliAvailabilityData> SetAvailabilityAsync(string pairId, string path, string availability,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pair = RequirePair(pairId);
        if (pair.A.Provider != SyncProviders.Local || pair.B.Provider != SyncProviders.Dropbox)
            throw new SyncCliUsageException("Storage intent currently supports a local-to-Dropbox pair. No files changed.");
        if (availability == "online-only" && OperatingSystem.IsWindows() &&
            new DriveInfo(Path.GetPathRoot(Path.GetFullPath(pair.A.RootPath))!).DriveType == DriveType.Removable)
            return Task.FromResult(new SyncCliAvailabilityData(pairId, path, false,
                "Online-only is unavailable on removable volumes because Windows Cloud Files does not support them. " +
                "No policy or files changed. Keep this pair offline, or configure a pair on a fixed NTFS drive outside other sync providers' roots."));
        // Deliberately independent of Run.UpdatePairSettings: intent is accepted during an active run.
        var relative = StoragePolicyStore.SetStoragePolicyAtPath(pair.A, path, availability);
        return Task.FromResult(new SyncCliAvailabilityData(pairId, relative, availability == "online-only",
            "Saved " + availability + " intent. Physical availability is not confirmed by this command."));
    }

    private SqliteStateStore StoragePolicyStore => _store as SqliteStateStore ??
        throw new SyncCliUnsupportedException("Storage intent requires the durable SQLite store. No policy changed.");

    public Task<object> ListStorageAsync(string pairId, int limit, int offset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pair = RequirePair(pairId);
        return Task.FromResult<object>(new { pairId, limit, offset,
            folders = StoragePolicyStore.StorageFolderSummaries(pair.A, limit, offset),
            detail = "Explicit online-only folders; counts describe indexed descendant files, including nested overrides. Unknown means no confirmed physical observation." });
    }

}
