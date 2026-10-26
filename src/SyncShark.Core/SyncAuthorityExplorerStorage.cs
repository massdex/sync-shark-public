namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost
{
    public async Task RefreshExplorerStorageChoicesAsync(CancellationToken cancellationToken = default,
        IReadOnlySet<string>? requestedPairs = null, Action<string>? startupProgress = null)
    {
        stateAuthority.VerifyAuthority();
        if (!await _runGate.WaitAsync(0, cancellationToken)) return;
        try { await RefreshExplorerStorageChoicesCoreAsync(cancellationToken, requestedPairs, startupProgress); }
        finally { _runGate.Release(); }
    }

    // The batch preparation caller already owns run admission; do not reacquire
    // the non-reentrant gate or silently skip Explorer policy refresh.
    private async Task RefreshExplorerStorageChoicesCoreAsync(CancellationToken cancellationToken,
        IReadOnlySet<string>? requestedPairs, Action<string>? startupProgress)
    {
        stateAuthority.VerifyAuthority();
        var set = await LoadPairsAsync(cancellationToken, startupProgress);
        foreach (var pair in set.Pairs.Where(item => item.FilesOnDemandEnabled &&
                     (requestedPairs is null || requestedPairs.Contains(item.Id))))
        {
            startupProgress?.Invoke("Admission: checking owned Explorer placeholder choices for pair " + pair.Id + ".");
            if (!WindowsCloudFilesProvider.IsOwnedSyncRoot(pair.LocalRoot, pair.Id)) continue;
            using var root = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            var observed = new List<SyncNativeDirectoryChoice>();
            foreach (var item in await _authority.LoadStorageDirectoryIdentitiesAsync(pair.Id, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = PilotPath.ResolveContainedPath(root.Path, item.RelativePath);
                var ancestor = Path.GetDirectoryName(path);
                var safe = true;
                while (ancestor is not null && !string.Equals(ancestor, root.Path, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (PilotPath.IsUnsafeAttributes(File.GetAttributes(ancestor)) &&
                            (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(ancestor, out var parent) ||
                             parent?.Identity.PairId != pair.Id)) { safe = false; break; }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { safe = false; break; }
                    ancestor = Path.GetDirectoryName(ancestor);
                }
                if (!safe) continue;
                if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out var info) || info is null ||
                    !info.IsDirectory || info.Identity.PairId != pair.Id || info.FileId != item.LocalId) continue;
                uint flags;
                try { flags = (uint)File.GetAttributes(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
                if ((flags & (0x80000 | 0x100000)) == 0) continue;
                observed.Add(new(item.RelativePath, info.FileId, item.RemoteId, (flags & 0x80000) != 0));
            }
            root.VerifyBinding();
            var updated = SyncDirectoryStoragePolicy.AdoptExplorerChoices(pair, observed);
            if (updated == pair) continue;
            startupProgress?.Invoke("Admission: retaining observed Explorer storage-policy changes for pair " + pair.Id + "; no transfer is running.");
            var connection = await RequireConnectionAsync(cancellationToken, pair.ConnectionId);
            await RegisterPairAuthorityAsync(updated, connection, await EnsureAuthorityInitializedAsync(cancellationToken), root, cancellationToken);
            await _authority.RetainPairDefinitionAsync(updated, set.SelectedPairId == pair.Id, cancellationToken);
            lock (_cloudProviderGate)
                if (_cloudProviders.TryGetValue(pair.Id, out var provider)) provider.UpdatePairPolicy(updated);
        }
    }
}
