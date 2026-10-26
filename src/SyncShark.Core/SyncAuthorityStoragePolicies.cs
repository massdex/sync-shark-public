namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost
{
    private async Task<SyncPair> BindDirectoryStorageRulesAsync(SyncPair pair, SyncPair? prior,
        IReadOnlyList<SyncDirectoryStorageRule>? requested, DropboxConnectionBinding? connection,
        CancellationToken cancellationToken)
    {
        if (pair.DefaultStorage is not null && !Enum.IsDefined(pair.DefaultStorage.Value))
            throw new InvalidDataException("Unknown default storage choice.");
        var rules = SyncDirectoryStoragePolicy.Normalize(requested ?? pair.DirectoryStorageRules);
        if (!pair.FilesOnDemandEnabled && rules.Any(rule => rule.Storage == SyncDirectoryStorage.CloudOnly))
            throw new InvalidOperationException("Enable Sync Shark storage management explicitly before selecting cloud-only folders.");
        var bound = new List<SyncDirectoryStorageRule>();
        var knownDirectories = rules.Count == 0 ? [] : await _authority.LoadStorageDirectoryIdentitiesAsync(pair.Id, cancellationToken);
        foreach (var rule in rules)
        {
            var previous = prior?.DirectoryStorageRules?.FirstOrDefault(item => item == rule);
            if (previous is not null && (rule.LocalDirectoryId is not null || rule.RemoteDirectoryId is not null))
            { bound.Add(previous); continue; }
            var localPath = PilotPath.ResolveContainedPath(pair.LocalRoot, rule.FolderPath);
            ulong? localId = null; string? remoteId = null;
            if (File.Exists(localPath)) throw new InvalidOperationException("Storage rules apply to directories, not individual files.");
            if (Directory.Exists(localPath))
            {
                using var held = PilotDirectoryLease.Open(localPath);
                if (held.Identity.VolumeSerialNumber != pair.LocalVolumeSerialNumber)
                    throw new IOException("The directory storage choice crossed the saved volume identity.");
                localId = held.Identity.FileId;
            }
            var known = knownDirectories
                .SingleOrDefault(item => string.Equals(item.RelativePath, rule.FolderPath, StringComparison.OrdinalIgnoreCase));
            remoteId = known?.RemoteId;
            if (localId is null && remoteId is null)
            {
                if (IsGooglePair(pair))
                {
                    var resolved = await CreateGoogleDriveClient(pair).ResolveWebLocationAsync(pair.ConnectionId!, rule.FolderPath, cancellationToken);
                    if (resolved.Kind != GoogleDriveEntryKind.Folder)
                        throw new InvalidOperationException("The storage preference no longer names a cloud folder.");
                    remoteId = resolved.FileId;
                }
                else
                    remoteId = (await CreateDropboxClient(pair, connection ?? throw new InvalidOperationException("Reconnect this account first."))
                        .GetDirectoryIdentityAsync(rule.FolderPath, cancellationToken)).FolderId;
            }
            bound.Add(rule with { LocalDirectoryId = localId, RemoteDirectoryId = remoteId });
        }
        var changed = (prior?.DirectoryStorageRules ?? []).Concat(bound).Select(rule => rule.FolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(path =>
                prior?.DirectoryStorageRules?.SingleOrDefault(rule => string.Equals(rule.FolderPath, path, StringComparison.OrdinalIgnoreCase)) !=
                bound.SingleOrDefault(rule => string.Equals(rule.FolderPath, path, StringComparison.OrdinalIgnoreCase))).ToList();
        if (pair.FilesOnDemandEnabled && (prior is null || !prior.FilesOnDemandEnabled || SyncDirectoryStoragePolicy.Default(prior) != SyncDirectoryStoragePolicy.Default(pair)))
            changed.AddRange(Directory.EnumerateFileSystemEntries(pair.LocalRoot).Select(path =>
                PilotPath.NormalizeRelative(Path.GetRelativePath(pair.LocalRoot, path))));
        return pair with { DirectoryStorageRules = bound,
            DirectoryStorageApplyPaths = (pair.DirectoryStorageApplyPaths ?? []).Concat(changed).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
    }

    public async Task<SyncCloudAvailabilityResult> ApplyPendingDirectoryStorageAsync(string pairId,
        CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        stateAuthority.VerifyAuthority();
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Folder storage will be applied after the active sync.");
        try
        {
            var pair = (await LoadPairsAsync(cancellationToken)).Pairs.Single(item => item.Id == pairId);
            return await ApplyDirectoryStoragePathsAsync(pair, pair.DirectoryStorageApplyPaths, cancellationToken, onProgress);
        }
        finally { _runGate.Release(); }
    }

    // Called only by an explicit directory choice. Saving a cloud rule never excludes the directory.
    public async Task<SyncCloudAvailabilityResult> SetDirectoryStoragePolicyAsync(string pairId,
        string folderPath, SyncDirectoryStorage? storage, CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        stateAuthority.VerifyAuthority();
        var folder = PilotPath.NormalizeRelative(folderPath);
        if (storage is not null && !Enum.IsDefined(storage.Value)) throw new InvalidDataException("Unknown storage choice.");
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Wait for the active sync to finish before changing folder storage.");
        try
        {
            await EnsurePairConfigurationMutableAsync(pairId, cancellationToken);
            var pairs = await LoadPairsAsync(cancellationToken);
            var pair = pairs.Pairs.Single(item => item.Id == pairId);
            if (!pair.FilesOnDemandEnabled)
                throw new InvalidOperationException("Enable Sync Shark storage management for this pair before choosing child-folder storage.");
            var localPath = PilotPath.ResolveContainedPath(pair.LocalRoot, folder);
            if (File.Exists(localPath)) throw new InvalidOperationException("Persistent storage choices apply to directories, not individual files.");
            var rules = SyncDirectoryStoragePolicy.Normalize(pair.DirectoryStorageRules)
                .Where(rule => !string.Equals(rule.FolderPath, folder, StringComparison.OrdinalIgnoreCase)).ToList();
            if (storage is not null) rules.Add(new(folder, storage.Value));
            var updated = pair with
            {
                DirectoryStorageRules = SyncDirectoryStoragePolicy.Normalize(rules),
                DirectoryStorageApplyPaths = (pair.DirectoryStorageApplyPaths ?? []).Append(folder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                PolicyRevision = checked(pair.PolicyRevision + 1)
            };
            var connection = IsGooglePair(pair) ? null : await RequireConnectionAsync(cancellationToken, pair.ConnectionId);
            updated = await BindDirectoryStorageRulesAsync(updated, pair, updated.DirectoryStorageRules, connection, cancellationToken);
            using var root = PilotDirectoryLease.Open(updated.LocalRoot,
                new PilotDirectoryIdentity(updated.LocalVolumeSerialNumber, updated.LocalRootFileId));
            await RegisterPairPolicyAsync(updated, cancellationToken);
            await _authority.RetainPairDefinitionAsync(updated, pairs.SelectedPairId == pairId, cancellationToken);
            // Retain the choice before native effects. A busy/changed file remains local and the exact
            // subtree stays pending across restart; already successful native effects are not undone.
            return await ApplyDirectoryStoragePathsAsync(updated, updated.DirectoryStorageApplyPaths, cancellationToken, onProgress);
        }
        finally { _runGate.Release(); }
    }

    private async Task<SyncCloudAvailabilityResult> ApplyDirectoryStoragePathsAsync(SyncPair pair,
        IEnumerable<string>? paths, CancellationToken cancellationToken,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        var roots = new List<string>();
        foreach (var path in (paths ?? []).Select(PilotPath.NormalizeRelative)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length))
            if (!roots.Any(root => SyncDirectoryStoragePolicy.Contains(root, path))) roots.Add(path);
        var resolve = SyncDirectoryStoragePolicy.Compile(pair);
        var failures = new List<string>(); var pending = new List<string>(); var files = 0; var directories = 0;
        await ReportStorageProgressAsync(pair, roots.FirstOrDefault() ?? string.Empty,
            $"Applying saved storage choices to {roots.Count} pending paths; sync transfers are already complete or were not started.", onProgress);
        foreach (var folder in roots)
        {
            var path = PilotPath.ResolveContainedPath(pair.LocalRoot, folder);
            // A cloud-only directory not yet materialized is covered when its future transfers finish.
            if (!Directory.Exists(path) && !File.Exists(path)) continue;
            var failed = false;
            foreach (var keep in new[] { true, false })
            {
                bool Include(string item) => (resolve(item) == SyncDirectoryStorage.AlwaysAvailable) == keep &&
                    SyncSelectionPolicy.IsIncluded(item, pair, Directory.Exists(PilotPath.ResolveContainedPath(pair.LocalRoot, item)));
                try
                {
                    if (File.Exists(path) && !Include(folder)) continue;
                    var result = await SetPathAvailabilityCoreAsync(pair, folder, keep, cancellationToken, Include,
                        onProgress: onProgress);
                    files += result.FilesChanged; directories += result.DirectoriesChanged;
                    failures.AddRange(result.Failures); failed |= result.Failures.Count != 0;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                { failures.Add(folder + ": " + error.Message); failed = true; }
            }
            if (failed) pending.Add(folder);
        }
        foreach (var failure in failures)
            await ReportStorageProgressAsync(pair, string.Empty,
                "Storage result remains pending: " + SyncActivityText.Safe(failure), onProgress);
        await ReportStorageProgressAsync(pair, roots.FirstOrDefault() ?? string.Empty,
            "Saving the exact remaining storage paths; successful native changes are retained and failed paths remain pending.", onProgress);
        await _authority.RetainPairDefinitionAsync(pair with { DirectoryStorageApplyPaths = pending },
            string.Equals(_selectedPairId, pair.Id, StringComparison.Ordinal), cancellationToken);
        await ReportStorageProgressAsync(pair, roots.FirstOrDefault() ?? string.Empty,
            $"Storage application finished: {files} files and {directories} directories changed; {failures.Count} failures and {pending.Count} paths remain pending.", onProgress);
        return new SyncCloudAvailabilityResult(roots.FirstOrDefault() ?? "", false, files, directories, failures);
    }

    private async Task<SyncRunResult> ApplyStorageAfterRunAsync(SyncPair pair, SyncRunResult result,
        CancellationToken cancellationToken, Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        if (!pair.FilesOnDemandEnabled || result.Stopped || result.NeedsVerification > 0 ||
            NewSyncAdmissionReason(pair) is not null) return result;
        if (pair.FileStorageRules is { Count: > 0 } && (result.Decisions.Any(item => item.Kind is not SyncDecisionKind.Identical and not SyncDecisionKind.Skipped) ||
                pair.DirectoryStorageApplyPaths is { Count: > 0 }))
        {
            var files = SyncFileStorageBinding.Rebind(pair.FileStorageRules,
                await _authority.LoadStorageFileBindingsAsync(pair, cancellationToken));
            if (!files.SequenceEqual(SyncDirectoryStoragePolicy.NormalizeFiles(pair.FileStorageRules)))
            {
                pair = pair with { FileStorageRules = files,
                    DirectoryStorageApplyPaths = (pair.DirectoryStorageApplyPaths ?? []).Concat(files.Select(rule => rule.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
                await _authority.RetainPairDefinitionAsync(pair, _selectedPairId == pair.Id, cancellationToken);
            }
        }
        // Work only on pending subtrees and changed paths, never scan an unchanged pair to refresh icons.
        var rules = SyncDirectoryStoragePolicy.Normalize(pair.DirectoryStorageRules);
        if (rules.Count != 0)
        {
            var identities = await _authority.LoadStorageDirectoryIdentitiesAsync(pair.Id, cancellationToken);
            var movedRules = new List<SyncDirectoryStorageRule>();
            foreach (var rule in rules)
            {
                var matches = identities.Where(item => rule.RemoteDirectoryId is not null && item.RemoteId == rule.RemoteDirectoryId ||
                    rule.LocalDirectoryId is not null && item.LocalId == rule.LocalDirectoryId).ToArray();
                var pathsNow = matches.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                // If the two sides are temporarily at different paths, preserve the rule and retry
                // after convergence rather than assigning it to a different directory identity.
                if (pathsNow.Length == 1) movedRules.Add(rule with { FolderPath = pathsNow[0],
                    LocalDirectoryId = matches.Select(item => item.LocalId).FirstOrDefault(id => id is not null) ?? rule.LocalDirectoryId,
                    RemoteDirectoryId = matches.Select(item => item.RemoteId).FirstOrDefault(id => id is not null) ?? rule.RemoteDirectoryId });
                else if (pathsNow.Length > 1 || identities.All(item => !string.Equals(item.RelativePath, rule.FolderPath, StringComparison.OrdinalIgnoreCase))) movedRules.Add(rule);
                // A new directory at the old path does not inherit the deleted folder's explicit identity-bound override.
            }
            if (!rules.SequenceEqual(movedRules))
            {
                pair = pair with { DirectoryStorageRules = SyncDirectoryStoragePolicy.Normalize(movedRules),
                    DirectoryStorageApplyPaths = (pair.DirectoryStorageApplyPaths ?? []).Concat(movedRules.Select(rule => rule.FolderPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
                await _authority.RetainPairDefinitionAsync(pair, _selectedPairId == pair.Id, cancellationToken);
                rules = SyncDirectoryStoragePolicy.Normalize(pair.DirectoryStorageRules);
            }
        }
        var changed = result.Decisions.Where(item => item.Kind is SyncDecisionKind.Uploaded or
                SyncDecisionKind.Downloaded or SyncDecisionKind.OnlineOnly or SyncDecisionKind.Recovered)
            .Select(item => item.RelativePath).Where(path => !path.StartsWith('<'));
        var paths = (pair.DirectoryStorageApplyPaths ?? []).Concat(changed).ToArray();
        if (paths.Length == 0) return result;
        // Retain intent BEFORE applying it. A crash or cancelled conversion after a completed
        // upload must retry storage, not reupload or silently forget the pair's online-only default.
        pair = pair with { DirectoryStorageApplyPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        await _authority.RetainPairDefinitionAsync(pair, _selectedPairId == pair.Id, cancellationToken);
        var applied = await ApplyDirectoryStoragePathsAsync(pair, paths, cancellationToken, onProgress);
        return applied.Failures.Count == 0 ? result : result with
        {
            Decisions = result.Decisions.Concat(applied.Failures.Select(failure => new SyncDecision(
                "<storage>", SyncDecisionKind.Blocked, "Folder storage remains pending: " + failure))).ToArray()
        };
    }

    private Task ReportStorageProgressAsync(SyncPair pair, string path, string detail,
        Func<SyncAuthorityBatchProgress, Task>? onProgress, int completed = 0, int total = 0,
        string unit = "paths inspected") => NotifyProgressAsync(onProgress,
        new SyncAuthorityBatchProgress(pair, "Applying storage choices", null, null, null,
            Comparison: new SyncComparisonProgress("Applying storage choices", path, completed, total,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, detail, unit)));
}
