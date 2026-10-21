namespace Massdex.SyncShark;

public sealed record SyncStorageOverview(SyncPair Pair, SyncFolderSelectionPage? Folders,
    long KnownSyncedBytes, long KnownSyncedFiles, DateTimeOffset? IndexedUtc);

public sealed partial class SyncAuthorityDatabase
{
    public async Task<(long Bytes, long Files, DateTimeOffset? IndexedUtc)> LoadStorageSizeSummaryAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT coalesce(sum(v.size_bytes),0),count(v.size_bytes),
                (SELECT max(updated_utc) FROM sync_path_index WHERE pair_id=$pair)
            FROM current_synchronization_point s
            JOIN object_version v ON v.object_version_id=s.dropbox_object_version_id
            JOIN logical_object o ON o.object_id=s.object_id AND o.object_kind='file'
            WHERE s.pair_id=$pair AND s.authority_generation=(
                SELECT max(n.authority_generation) FROM current_synchronization_point n WHERE n.pair_id=s.pair_id AND n.object_id=s.object_id);
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (0, 0, null);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)));
    }
}

public sealed partial class SyncAuthorityRuntimeHost
{
    // Metadata only: opening Storage must not start a sync, hash user content, or hydrate a placeholder.
    public async Task<SyncStorageOverview> LoadStorageOverviewAsync(string pairId, string connectionId,
        int offset = 0, string? search = null, string? filter = null, CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        var pair = (await LoadPairsAsync(cancellationToken)).Pairs.SingleOrDefault(item => item.Id == pairId)
            ?? throw new InvalidOperationException("This folder pair is no longer available.");
        await RequirePairAccountAsync(pair, connectionId, cancellationToken);
        var page = await _authority.LoadFolderSelectionPageAsync(pairId, 100, offset, search, filter, cancellationToken);
        var size = await _authority.LoadStorageSizeSummaryAsync(pairId, cancellationToken);
        return new(pair, page, size.Bytes, size.Files, size.IndexedUtc);
    }

    public Task<SyncCloudAvailabilityResult> ChangeManagedStorageAsync(string pairId, string connectionId,
        string? folderPath, SyncDirectoryStorage? storage, long expectedPolicyRevision, CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
        => ChangeManagedStorageBatchAsync(pairId, connectionId, folderPath is null ? null : [folderPath], storage, expectedPolicyRevision, cancellationToken,
            onProgress: onProgress);

    public Task<SyncCloudAvailabilityResult> ChangeManagedRootStorageAsync(string pairId, string connectionId,
        SyncDirectoryStorage storage, long expectedPolicyRevision, CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
        => ChangeManagedStorageBatchAsync(pairId, connectionId, null, storage, expectedPolicyRevision, cancellationToken,
            applyExistingRootItems: true, onProgress: onProgress);

    public async Task<SyncCloudAvailabilityResult> ChangeManagedStorageBatchAsync(string pairId, string connectionId,
        IReadOnlyList<string>? folderPaths, SyncDirectoryStorage? storage, long expectedPolicyRevision, CancellationToken cancellationToken = default,
        bool applyExistingRootItems = false, Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        stateAuthority.VerifyAuthority();
        if (storage is not null && !Enum.IsDefined(storage.Value)) throw new InvalidDataException("Choose Online Only or Available Offline.");
        if (folderPaths is null && storage is null) throw new InvalidDataException("A pair default cannot inherit from a parent.");
        if (folderPaths is { Count: < 1 or > 100 }) throw new InvalidDataException("Choose between 1 and 100 folders in one batch.");
        if (!await _runGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Wait for the current sync to finish before changing storage.");
        try
        {
            await EnsurePairConfigurationMutableAsync(pairId, cancellationToken);
            var set = await LoadPairsAsync(cancellationToken); var prior = set.Pairs.Single(item => item.Id == pairId);
            await ReportStorageProgressAsync(prior, folderPaths?.FirstOrDefault() ?? string.Empty,
                "Verifying the selected account and retained storage-policy revision before saving any choice.", onProgress);
            await RequirePairAccountAsync(prior, connectionId, cancellationToken);
            var account = IsGooglePair(prior) ? null : await RequireConnectionAsync(cancellationToken, connectionId);
            if (prior.PolicyRevision != expectedPolicyRevision) throw new InvalidOperationException("Folder settings changed. Refresh before applying your choice.");
            using var root = PilotDirectoryLease.Open(prior.LocalRoot, new PilotDirectoryIdentity(prior.LocalVolumeSerialNumber, prior.LocalRootFileId));
            if (WindowsCloudFilesProvider.IsCloudFilesPlaceholder(root.Path) && !WindowsCloudFilesProvider.IsOwnedSyncRoot(root.Path, prior.Id))
                throw new InvalidOperationException("Another provider owns this location. Resolve its ownership before changing storage.");
            var rules = SyncDirectoryStoragePolicy.Normalize(prior.DirectoryStorageRules).ToList();
            var paths = (prior.DirectoryStorageApplyPaths ?? []).ToList();
            if (applyExistingRootItems)
            {
                if (folderPaths is not null) throw new InvalidDataException("A whole-pair availability choice cannot contain child selections.");
                var children = await Task.Run(() => Directory.EnumerateFileSystemEntries(root.Path)
                    .Select(path => PilotPath.NormalizeRelative(Path.GetRelativePath(root.Path, path))).Take(10_001).ToArray(), cancellationToken);
                if (children.Length > 10_000) throw new InvalidOperationException("This root contains over 10,000 direct items. Apply availability to its child folders in Storage instead.");
                paths.AddRange(children); root.VerifyBinding();
            }
            foreach (var requested in folderPaths ?? [])
            {
                var folder = PilotPath.NormalizeRelative(requested);
                if (!SyncSelectionPolicy.IsIncluded(folder, prior, true))
                    throw new InvalidOperationException("Choose a folder that participates in sync. Storage preferences do not change sync selection.");
                rules.RemoveAll(rule => string.Equals(rule.FolderPath, folder, StringComparison.OrdinalIgnoreCase));
                if (storage is not null) rules.Add(new(folder, storage.Value)); paths.Add(folder);
            }
            var updated = prior with { DefaultStorage = folderPaths is null ? storage : SyncDirectoryStoragePolicy.Default(prior),
                FilesOnDemandEnabled = prior.FilesOnDemandEnabled || storage is not null,
                DirectoryStorageRules = SyncDirectoryStoragePolicy.Normalize(rules), DirectoryStorageApplyPaths = paths,
                PolicyRevision = checked(prior.PolicyRevision + 1) };
            updated = await BindDirectoryStorageRulesAsync(updated, prior, updated.DirectoryStorageRules, account, cancellationToken);
            await ReportStorageProgressAsync(prior, folderPaths?.FirstOrDefault() ?? string.Empty,
                "Saving the verified storage preference and exact pending paths before any native availability changes.", onProgress);
            await RegisterPairPolicyAsync(updated, cancellationToken);
            WindowsCloudFilesProvider? activated = null; var started = false;
            if (updated.FilesOnDemandEnabled) activated = GetOrStartCloudProvider(updated, CreateRemoteStoreForPair(updated, account), out started);
            try { await _authority.RetainPairDefinitionAsync(updated, set.SelectedPairId == pairId, cancellationToken); }
            catch { if (started && activated is not null) RollBackCloudProviderStart(pairId, activated); throw; }
            root.VerifyBinding();
            return updated.FilesOnDemandEnabled
                ? await ApplyDirectoryStoragePathsAsync(updated, updated.DirectoryStorageApplyPaths, cancellationToken, onProgress)
                : new SyncCloudAvailabilityResult(folderPaths?.FirstOrDefault() ?? "", true, 0, 0, []);
        }
        finally { _runGate.Release(); }
    }
}
