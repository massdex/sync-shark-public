namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost
{
    public async Task<SyncCloudAvailabilityResult> ResetManagedFileStorageAsync(string pairId, string connectionId,
        SyncFileStorageRule expectedRule, long expectedPolicyRevision, CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        stateAuthority.VerifyAuthority();
        if (!await _runGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Wait for the current sync before changing availability.");
        try
        {
            await EnsurePairConfigurationMutableAsync(pairId, cancellationToken);
            var set = await LoadPairsAsync(cancellationToken); var prior = set.Pairs.Single(item => item.Id == pairId);
            await ReportStorageProgressAsync(prior, expectedRule.RelativePath,
                "Verifying the retained file identity, account and policy before removing its storage override.", onProgress);
            await RequirePairAccountAsync(prior, connectionId, cancellationToken);
            if (prior.PolicyRevision != expectedPolicyRevision || !(prior.FileStorageRules ?? []).Contains(expectedRule))
                throw new InvalidOperationException("The account or file preference changed. Refresh before applying availability.");
            using var root = PilotDirectoryLease.Open(prior.LocalRoot, new(prior.LocalVolumeSerialNumber, prior.LocalRootFileId));
            // Resolve the retained identity through the existing index, never by searching the disk or
            // assuming a different file at its old name is the one whose preference was selected.
            var rebound = SyncFileStorageBinding.Rebind([expectedRule],
                await _authority.LoadStorageFileBindingsAsync(prior, cancellationToken)).Single();
            var path = PilotPath.ResolveContainedPath(root.Path, rebound.RelativePath);
            var apply = false;
            if (File.Exists(path) && !Directory.Exists(path))
            {
                var parent = Path.GetDirectoryName(path);
                while (parent is not null && !string.Equals(parent, root.Path, StringComparison.OrdinalIgnoreCase))
                {
                    if (PilotPath.IsUnsafeAttributes(File.GetAttributes(parent)) &&
                        (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(parent, out var info) || info?.Identity.IsBoundTo(pairId, prior.Provider) != true))
                        throw new IOException("A parent directory belongs to another provider or is a link.");
                    parent = Path.GetDirectoryName(parent);
                }
                if (parent is null) throw new IOException("The selected file lost its folder binding.");
                var identity = WindowsCloudFilesProvider.ReadStorageFileIdentity(path, pairId, prior.Provider);
                apply = identity.LocalId != 0 && identity.LocalId == rebound.LocalFileId;
            }
            var pending = (prior.DirectoryStorageApplyPaths ?? []).Where(item =>
                !string.Equals(item, expectedRule.RelativePath, StringComparison.OrdinalIgnoreCase)).ToList();
            if (apply) pending.Add(rebound.RelativePath);
            var updated = prior with {
                FileStorageRules = SyncDirectoryStoragePolicy.NormalizeFiles((prior.FileStorageRules ?? []).Where(rule => rule != expectedRule).ToArray()),
                DirectoryStorageApplyPaths = pending.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                PolicyRevision = checked(prior.PolicyRevision + 1)
            };
            await RegisterPairPolicyAsync(updated, cancellationToken);
            await _authority.RetainPairDefinitionAsync(updated, set.SelectedPairId == pairId, cancellationToken);
            root.VerifyBinding();
            return apply ? await ApplyDirectoryStoragePathsAsync(updated, updated.DirectoryStorageApplyPaths, cancellationToken, onProgress) :
                new SyncCloudAvailabilityResult(expectedRule.RelativePath, false, 0, 0, []);
        }
        finally { _runGate.Release(); }
    }

    public async Task<SyncCloudAvailabilityResult> ChangeManagedFileStorageAsync(string pairId, string connectionId,
        string relativePath, SyncDirectoryStorage storage, long expectedPolicyRevision, CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        stateAuthority.VerifyAuthority();
        if (!Enum.IsDefined(storage)) throw new InvalidDataException("Choose Available Offline or Online Only.");
        if (!await _runGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Wait for the current sync before changing availability.");
        try
        {
            await EnsurePairConfigurationMutableAsync(pairId, cancellationToken);
            var set = await LoadPairsAsync(cancellationToken); var prior = set.Pairs.Single(item => item.Id == pairId);
            await ReportStorageProgressAsync(prior, relativePath,
                "Verifying the selected file's identity, account and storage-policy revision before saving its preference.", onProgress);
            await RequirePairAccountAsync(prior, connectionId, cancellationToken);
            if (prior.PolicyRevision != expectedPolicyRevision)
                throw new InvalidOperationException("The account or folder settings changed. Refresh before applying availability.");
            var relative = PilotPath.NormalizeRelative(relativePath);
            if (!SyncSelectionPolicy.IsIncluded(relative, prior)) throw new InvalidOperationException("This file does not participate in sync; availability does not change folder selection or ignore rules.");
            using var root = PilotDirectoryLease.Open(prior.LocalRoot, new(prior.LocalVolumeSerialNumber, prior.LocalRootFileId));
            var path = PilotPath.ResolveContainedPath(root.Path, relative);
            if (!File.Exists(path) || Directory.Exists(path)) throw new IOException("The selected file moved or is unavailable.");
            var parent = Path.GetDirectoryName(path);
            while (parent is not null && !string.Equals(parent, root.Path, StringComparison.OrdinalIgnoreCase))
            {
                if (PilotPath.IsUnsafeAttributes(File.GetAttributes(parent)) &&
                    (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(parent, out var info) || info?.Identity.IsBoundTo(pairId, prior.Provider) != true))
                    throw new IOException("A parent directory belongs to another provider or is a link.");
                parent = Path.GetDirectoryName(parent);
            }
            if (parent is null) throw new IOException("The selected file lost its folder binding.");
            var identity = WindowsCloudFilesProvider.ReadStorageFileIdentity(path, pairId, prior.Provider);
            if (identity.LocalId == 0) throw new IOException("Select a file, not a directory.");
            if (identity.RemoteId is null)
            {
                var known = await _authority.LoadStorageFileIdentityAsync(pairId, relative, cancellationToken);
                if (known?.LocalId == identity.LocalId) identity = (identity.LocalId, known.RemoteId);
            }
            var choices = (prior.FileStorageRules ?? []).Where(rule => rule.LocalFileId != identity.LocalId &&
                !string.Equals(rule.RelativePath, relative, StringComparison.OrdinalIgnoreCase) &&
                (identity.RemoteId is null || rule.RemoteFileId != identity.RemoteId)).ToList();
            if (choices.Count >= 10000) throw new InvalidOperationException("Use a folder storage preference for large selections.");
            choices.Add(new(relative, storage, identity.LocalId, identity.RemoteId));
            var updated = prior with { FilesOnDemandEnabled = true, DefaultStorage = SyncDirectoryStoragePolicy.Default(prior),
                FileStorageRules = SyncDirectoryStoragePolicy.NormalizeFiles(choices), DirectoryStorageApplyPaths = (prior.DirectoryStorageApplyPaths ?? []).Append(relative).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                PolicyRevision = checked(prior.PolicyRevision + 1) };
            await RegisterPairPolicyAsync(updated, cancellationToken);
            var provider = GetOrStartCloudProvider(updated, await CreateRemoteStoreForPairAsync(updated, cancellationToken), out var started);
            try { await _authority.RetainPairDefinitionAsync(updated, set.SelectedPairId == pairId, cancellationToken); }
            catch { if (started) RollBackCloudProviderStart(pairId, provider); throw; }
            root.VerifyBinding();
            return await ApplyDirectoryStoragePathsAsync(updated, updated.DirectoryStorageApplyPaths, cancellationToken, onProgress);
        }
        finally { _runGate.Release(); }
    }
}
