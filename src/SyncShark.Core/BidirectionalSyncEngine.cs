namespace Massdex.SyncShark;

public sealed class BidirectionalSyncEngine(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncRuntimeStateStore stateStore,
    PilotRunControl control)
{
    private string? _runGeneration;
    internal static Action<SyncIntentKind, string>? CleanupStatePersistedForTest { get; set; }
    internal static Action<SyncIntentKind, string>? CleanupArtifactsVerifiedForTest { get; set; }
    internal static Action? PrivateRollbackCleanedForTest { get; set; }
    internal static Action? PrivateCommittedPersistedForTest { get; set; }
    internal static Action? PrivateCleanupCompletedPersistedForTest { get; set; }
    internal static Action<string>? PrivateTargetFencedForTest { get; set; }

    public async Task<SyncRunResult> ResolveConflictAsync(string relativePath, SyncConflictResolution resolution,
        CancellationToken cancellationToken = default)
    {
        var path = PilotPath.NormalizeRelative(relativePath);
        if (!SyncCapabilityGate.ConflictResolution)
            return new SyncRunResult(pair.Id, [new SyncDecision(path, SyncDecisionKind.Blocked,
                SyncCapabilityGate.ManualReviewReason("Conflict resolution"))]);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, control.StopToken);
        var token = linked.Token;
        var decisions = new List<SyncDecision>();
        var lease = await stateStore.BeginRunAsync(pair.Id, pair.PolicyRevision, token);
        _runGeneration = lease.Generation;
        try
        {
        using var rootLease = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        var controlBinding = pair.ControlNamespace ??
                             throw new IOException("This pair has no verified private control namespace binding.");
        using var controlRoot = SyncControlNamespace.OpenLocal(controlBinding, rootLease);
        var state = stateStore.LoadPair(pair.Id);
        var localExclusions = RestorePersistedLocalAccessBeforeScan(rootLease, controlRoot, state);
        var cleanupPending = state.PendingTransfer?.Phase == SyncIntentPhase.CommittedCleanupPending;
        state = await CompletePendingCleanupBeforeScanAsync(state, rootLease, controlRoot, decisions, token);
        if (cleanupPending)
            return new SyncRunResult(pair.Id, decisions);
        await remoteStore.VerifyRootIdentityAsync(token);
        await remoteStore.VerifyControlNamespaceAsync(controlBinding.Remote ??
            throw new IOException("The legacy runtime requires its existing remote control binding."), controlBinding.InstallationId,
            controlBinding.NamespaceId, token);
        var local = (await new PilotLocalScanner().ScanAsync(rootLease, localExclusions, token,
            contentProvider: pair.Provider, expectedPairId: pair.Id)).Entries;
        var remote = BuildRemoteIndex(await remoteStore.ListEntriesAsync(token));
        if (!local.TryGetValue(path, out var localEntry) || !remote.TryGetValue(path, out var remoteEntry))
            return new SyncRunResult(pair.Id, [new SyncDecision(path, SyncDecisionKind.Blocked,
                "Conflict resolution requires both current versions; neither side was changed.")]);
        if (SameBytes(localEntry, remoteEntry))
            return new SyncRunResult(pair.Id, [new SyncDecision(path, SyncDecisionKind.Identical,
                "Both sides already match; run normal sync to record the converged baseline.")]);

        var baselines = state.Files.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (state.PendingTransfer is not null)
        {
            var pending = await ResolvePendingAsync(state, baselines, local, remote, rootLease, controlRoot, decisions, token);
            if (pending.SafeToContinue)
                decisions.Add(new SyncDecision(path, SyncDecisionKind.Recovered,
                    "The earlier pending operation was reconciled first; fresh inventory is required before another conflict choice."));
            return new SyncRunResult(pair.Id, decisions);
        }
        try
        {
            SyncTransferIntent intent;
            switch (resolution)
            {
                case SyncConflictResolution.KeepLocalArchiveDropbox:
                    var keepLocalId = Guid.NewGuid().ToString("N");
                    intent = new SyncTransferIntent(keepLocalId, pair.Id, path, SyncDirection.Upload,
                        localEntry.ContentHash!, remoteEntry.ContentHash, remoteEntry.Revision, null, null,
                        DateTimeOffset.UtcNow, SyncIntentKind.ResolveKeepLocal,
                        RecoveryPath: BuildRemoteRecoveryPath(pair.Id, keepLocalId), RecoveryOriginalPath: path,
                        ExpectedSourceId: SyncLocalRecovery.FormatSourceIdentity(localEntry),
                        ExpectedSourceSize: localEntry.Size, CreationPolicyRevision: pair.PolicyRevision,
                        ExpectedSourceVolumeSerialNumber: localEntry.VolumeSerialNumber,
                        ExpectedSourceFileId: localEntry.FileId,
                        ExpectedSourceLastWriteUtc: localEntry.LastWriteUtc,
                        ExpectedDestinationRemoteId: remoteEntry.Id,
                        ExpectedDestinationSize: remoteEntry.Size);
                    break;
                case SyncConflictResolution.KeepDropboxArchiveLocal:
                    var artifacts = SyncLocalTransfer.CreateArtifactNames(false);
                    var keepDropboxId = Guid.NewGuid().ToString("N");
                    intent = new SyncTransferIntent(keepDropboxId, pair.Id, path, SyncDirection.Download,
                        remoteEntry.ContentHash!, localEntry.ContentHash, remoteEntry.Revision,
                        artifacts.StageFileName, null, DateTimeOffset.UtcNow, SyncIntentKind.ResolveKeepDropbox,
                        RecoveryPath: SyncLocalRecovery.BuildRecoveryPath(controlRoot, pair.Id, keepDropboxId),
                        RecoveryOriginalPath: path, ExpectedSourceId: remoteEntry.Id,
                        ExpectedSourceRevision: remoteEntry.Revision,
                        ExpectedSourceSize: remoteEntry.Size, CreationPolicyRevision: pair.PolicyRevision,
                        ExpectedDestinationVolumeSerialNumber: localEntry.VolumeSerialNumber,
                        ExpectedDestinationFileId: localEntry.FileId,
                        ExpectedDestinationSize: localEntry.Size);
                    intent = SyncLocalTransactionDirectory.Prepare(controlRoot, intent);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(resolution));
            }
            state = await SaveAsync(baselines, true, intent, token);
            await ResolvePendingAsync(state, baselines, local, remote, rootLease, controlRoot, decisions, token);
        }
        catch (OperationCanceledException) when (control.IsStopRequested)
        {
            decisions.Add(new SyncDecision(path, SyncDecisionKind.Stopped,
                "Conflict resolution stopped; its persisted composite intent will be reconciled before another effect."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DropboxApiException or DropboxEffectUncertainException)
        {
            decisions.Add(new SyncDecision(path, SyncDecisionKind.Blocked,
                "Conflict resolution retained its composite intent and recovery evidence for inventory reconciliation: " + SafeMessage(exception)));
        }
        return new SyncRunResult(pair.Id, decisions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SyncRunResult(pair.Id, [new SyncDecision("<run>", SyncDecisionKind.Blocked,
                "Owned transfer cleanup remains durably pending: " + SafeMessage(exception))]);
        }
        finally
        {
            _runGeneration = null;
            await stateStore.EndRunAsync(lease, CancellationToken.None);
        }
    }

    public async Task<SyncRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, control.StopToken);
        var token = linked.Token;
        var decisions = new List<SyncDecision>();
        var lease = await stateStore.BeginRunAsync(pair.Id, pair.PolicyRevision, token);
        _runGeneration = lease.Generation;

        try
        {
            using var rootLease = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            var controlBinding = pair.ControlNamespace ??
                                 throw new IOException("This pair has no verified private control namespace binding.");
            using var controlRoot = SyncControlNamespace.OpenLocal(controlBinding, rootLease);
            var state = stateStore.LoadPair(pair.Id);
            var localExclusions = RestorePersistedLocalAccessBeforeScan(rootLease, controlRoot, state);
            state = await CompletePendingCleanupBeforeScanAsync(state, rootLease, controlRoot, decisions, token);
            await remoteStore.VerifyRootIdentityAsync(token);
            await remoteStore.VerifyControlNamespaceAsync(controlBinding.Remote ??
                throw new IOException("The legacy runtime requires its existing remote control binding."), controlBinding.InstallationId,
                controlBinding.NamespaceId, token);
            var localScan = await new PilotLocalScanner().ScanAsync(rootLease, localExclusions, token,
                contentProvider: pair.Provider, expectedPairId: pair.Id);
            decisions.AddRange(localScan.Decisions.Select(item => new SyncDecision(item.RelativePath,
                item.Kind == PilotDecisionKind.OnlineOnly ? SyncDecisionKind.OnlineOnly : SyncDecisionKind.Blocked, item.Reason)));
            var unavailableLocalPaths = localScan.Decisions.Select(item => item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var local = localScan.Entries;
            var remote = BuildRemoteIndex(await remoteStore.ListEntriesAsync(token));
            await remoteStore.VerifyRootIdentityAsync(token);

            var baselines = state.Files.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
            if (state.PendingTransfer is not null)
            {
                await ResolvePendingAsync(state, baselines, local, remote, rootLease, controlRoot, decisions, token);
                return new SyncRunResult(pair.Id, decisions);
            }

            if (!state.BaselineEstablished)
            {
                var observation = local.Keys.Concat(remote.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Where(path => !unavailableLocalPaths.Contains(path))
                    .Select(path =>
                    {
                        local.TryGetValue(path, out var localEntry);
                        remote.TryGetValue(path, out var remoteEntry);
                        return Capture(path, localEntry, remoteEntry);
                    }).ToArray();
                if (state.InitialObservationCount != 1 ||
                    !ObservationsMatch(state.InitialObservation ?? [], observation))
                {
                    state = await SaveCoreAsync(new SyncPairRuntimeState(pair.Id, false, [], null,
                        observation, 1), token);
                    foreach (var item in observation)
                        decisions.Add(new SyncDecision(item.RelativePath, SyncDecisionKind.Baseline,
                            "First identity-bound discovery observation recorded without transfer; one unchanged rescan is required before adoption."));
                    return new SyncRunResult(pair.Id, decisions);
                }

                foreach (var path in local.Keys.Concat(remote.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                             .Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (unavailableLocalPaths.Contains(path)) continue;
                    local.TryGetValue(path, out var localEntry);
                    remote.TryGetValue(path, out var remoteEntry);
                    if (localEntry is not null && remoteEntry is not null && !SameBytes(localEntry, remoteEntry))
                    {
                        decisions.Add(new SyncDecision(path, SyncDecisionKind.Conflict,
                            "Initial discovery found different local and Dropbox bytes; both versions were preserved and no baseline was accepted for this path."));
                        continue;
                    }
                    baselines[path] = Capture(path, localEntry, remoteEntry);
                        decisions.Add(new SyncDecision(path, SyncDecisionKind.Baseline,
                        localEntry is not null && remoteEntry is not null
                            ? "Two stable identity-bound observations proved same-path byte equivalence; the initial-equivalence baseline was durably accepted without a transfer."
                            : "Two stable identity-bound observations accepted this unmatched initial state without copying, overwriting or deleting it."));
                }
                state = await SaveAsync(baselines, true, null, token);
                return new SyncRunResult(pair.Id, decisions);
            }

            var paths = OrderPathsForRecoverableEffects(local, remote, baselines);
            foreach (var path in paths)
            {
                if (unavailableLocalPaths.Contains(path)) continue;
                token.ThrowIfCancellationRequested();
                local.TryGetValue(path, out var localEntry);
                remote.TryGetValue(path, out var remoteEntry);
                baselines.TryGetValue(path, out var baseline);

                if (baseline is null)
                {
                    if (localEntry is not null && remoteEntry is not null)
                    {
                        if (SameBytes(localEntry, remoteEntry))
                        {
                            baselines[path] = Capture(path, localEntry, remoteEntry);
                            state = await SaveAsync(baselines, true, null, token);
                            decisions.Add(new SyncDecision(path, SyncDecisionKind.Identical,
                                "The independently created files are byte-identical; a shared baseline was recorded."));
                        }
                        else
                        {
                            decisions.Add(new SyncDecision(path, SyncDecisionKind.Conflict,
                                "A new path appeared on both sides with different bytes; both versions were preserved."));
                        }
                    }
                    else if (localEntry is not null)
                    {
                        state = await UploadAsync(path, localEntry, null, baselines, rootLease, decisions, token);
                    }
                    else if (remoteEntry is not null)
                    {
                        state = await DownloadAsync(path, remoteEntry, null, baselines, rootLease, controlRoot, decisions, token);
                    }
                    continue;
                }

                var localChanged = !MatchesLocal(baseline, localEntry);
                var remoteChanged = !MatchesRemote(baseline, remoteEntry);
                if (SameLocalBytesDifferentIdentity(baseline, localEntry) ||
                    SameRemoteBytesDifferentIdentity(baseline, remoteEntry))
                {
                    decisions.Add(new SyncDecision(path, SyncDecisionKind.Conflict,
                        "The bytes match the retained baseline but the underlying local or Dropbox object identity changed; no same-byte replacement was adopted."));
                    continue;
                }
                if (!localChanged && !remoteChanged)
                {
                    if (localEntry is not null && remoteEntry is not null)
                    {
                        if (SameBytes(localEntry, remoteEntry))
                        {
                            if (!string.Equals(baseline.RemoteRevision, remoteEntry.Revision, StringComparison.Ordinal))
                            {
                                baselines[path] = Capture(path, localEntry, remoteEntry);
                                state = await SaveAsync(baselines, true, null, token);
                            }
                            decisions.Add(new SyncDecision(path, SyncDecisionKind.Identical, "Local and Dropbox bytes match the retained baseline."));
                        }
                        else
                        {
                            decisions.Add(new SyncDecision(path, SyncDecisionKind.Conflict,
                                "The unresolved initial versions still differ; both remain preserved."));
                        }
                    }
                    continue;
                }

                if (!SyncCapabilityGate.ExistingFileReplacement)
                {
                    decisions.Add(new SyncDecision(path, SyncDecisionKind.Conflict,
                        SyncCapabilityGate.ManualReviewReason(localChanged && remoteChanged
                            ? "Concurrent or conflicting change"
                            : localEntry is null || remoteEntry is null
                                ? "Rename or one-sided deletion"
                                : "Existing-file modification")));
                    continue;
                }

                if (localChanged && remoteChanged)
                {
                    if (localEntry is null && remoteEntry is null)
                    {
                        baselines.Remove(path);
                        state = await SaveAsync(baselines, true, null, token);
                        decisions.Add(new SyncDecision(path, SyncDecisionKind.Baseline,
                            "Both sides independently removed the path; the obsolete baseline was retired without a delete effect."));
                    }
                    else if (localEntry is not null && remoteEntry is not null && SameBytes(localEntry, remoteEntry))
                    {
                        baselines[path] = Capture(path, localEntry, remoteEntry);
                        state = await SaveAsync(baselines, true, null, token);
                        decisions.Add(new SyncDecision(path, SyncDecisionKind.Identical,
                            "Both sides changed to identical bytes; the converged baseline was recorded."));
                    }
                    else
                    {
                        decisions.Add(new SyncDecision(path, SyncDecisionKind.Conflict,
                            "Both sides changed from the retained baseline; neither version was overwritten or deleted."));
                    }
                    continue;
                }

                if (localChanged)
                {
                    if (localEntry is null)
                    {
                        if (pair.PropagateDeletions && SyncCapabilityGate.DeletionPropagation && remoteEntry is not null)
                            state = await ArchiveRemoteAsync(path, remoteEntry, baselines, decisions, token);
                        else
                            decisions.Add(new SyncDecision(path, SyncDecisionKind.Conflict,
                                SyncCapabilityGate.ManualReviewReason(
                                    "The local file was removed and the Dropbox version was preserved")));
                    }
                    else if (remoteEntry is null || baseline.RemoteContentHash is not null)
                    {
                        state = await UploadAsync(path, localEntry, remoteEntry, baselines, rootLease, decisions, token);
                    }
                    else
                    {
                        decisions.Add(new SyncDecision(path, SyncDecisionKind.Conflict,
                            "Dropbox content appeared while the local version changed; both versions were preserved."));
                    }
                    continue;
                }

                if (remoteEntry is null)
                {
                    if (pair.PropagateDeletions && SyncCapabilityGate.DeletionPropagation && localEntry is not null)
                        state = await ArchiveLocalAsync(path, localEntry, baselines, rootLease, controlRoot, decisions, token);
                    else
                        decisions.Add(new SyncDecision(path, SyncDecisionKind.Conflict,
                            SyncCapabilityGate.ManualReviewReason(
                                "The Dropbox file was removed and the local version was preserved")));
                }
                else if (localEntry is null || baseline.LocalContentHash is not null)
                {
                    state = await DownloadAsync(path, remoteEntry, localEntry, baselines, rootLease, controlRoot, decisions, token);
                }
                else
                {
                    decisions.Add(new SyncDecision(path, SyncDecisionKind.Conflict,
                        "Local content appeared while the Dropbox version changed; both versions were preserved."));
                }
            }
        }
        catch (OperationCanceledException) when (control.IsStopRequested)
        {
            decisions.Add(new SyncDecision("<run>", SyncDecisionKind.Stopped,
                "Run stopped; any persisted transfer intent will be reconciled by inventory before retry."));
        }
        catch (PilotLimitException exception)
        {
            decisions.Clear();
            decisions.Add(new SyncDecision("<run>", SyncDecisionKind.Blocked, exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            decisions.Clear();
            decisions.Add(new SyncDecision("<run>", SyncDecisionKind.Blocked,
                "Owned transfer cleanup remains durably pending: " + SafeMessage(exception)));
        }
        finally
        {
            _runGeneration = null;
            await stateStore.EndRunAsync(lease, CancellationToken.None);
        }

        return new SyncRunResult(pair.Id, decisions);
    }

    private async Task<SyncPairRuntimeState> UploadAsync(string path, PilotLocalEntry local, PilotRemoteEntry? remote,
        IDictionary<string, SyncFileBaseline> baselines, PilotDirectoryLease rootLease, ICollection<SyncDecision> decisions,
        CancellationToken token)
    {
        if (!SyncCapabilityGate.CreateNewUpload && remote is null)
            throw new IOException(SyncCapabilityGate.ManualReviewReason("Existing-file upload replacement"));
        await control.WaitBeforeNextEffectAsync(token);
        var operationId = Guid.NewGuid().ToString("N");
        var intent = new SyncTransferIntent(operationId, pair.Id, path, SyncDirection.Upload,
            local.ContentHash!, remote?.ContentHash, remote?.Revision, null, null, DateTimeOffset.UtcNow,
            CreationPolicyRevision: pair.PolicyRevision,
            RecoveryPath: remote is null ? null : BuildRemoteRecoveryPath(pair.Id, operationId),
            RecoveryOriginalPath: remote is null ? null : path,
            ExpectedSourceId: SyncLocalRecovery.FormatSourceIdentity(local),
            ExpectedSourceSize: local.Size,
            ExpectedSourceVolumeSerialNumber: local.VolumeSerialNumber,
            ExpectedSourceFileId: local.FileId,
            ExpectedSourceLastWriteUtc: local.LastWriteUtc,
            ExpectedDestinationRemoteId: remote?.Id,
            ExpectedDestinationSize: remote?.Size);
        var state = await SaveAsync(baselines, true, intent, token);
        try
        {
            rootLease.VerifyBinding();
            await remoteStore.VerifyRootIdentityAsync(token);
            if (remote is not null)
            {
                var recovery = await remoteStore.CopyToRecoveryAsync(remote, intent.RecoveryPath!, token);
                ValidateArchiveResult(recovery, intent);
                intent = WithArchiveResult(intent, recovery);
                state = await SaveAsync(baselines, true, intent, token);
            }
            await using var source = await PilotLocalScanner.OpenValidatedForUploadAsync(local, rootLease, token,
                contentProvider: pair.Provider, expectedPairId: pair.Id);
            var result = await remoteStore.UploadAsync(path, source, local.ContentHash!, remote?.Revision, token);
            await PilotLocalScanner.VerifyHeldSourceAfterUploadAsync(source, local, rootLease, token,
                contentProvider: pair.Provider);
            ValidateUploadResult(result, local);
            intent = intent with
            {
                Phase = SyncIntentPhase.SuccessorCreated,
                SuccessorFileId = result.FileId,
                SuccessorRevision = result.Revision,
                SuccessorContentHash = result.ContentHash,
                SuccessorSize = result.Size
            };
            state = await SaveAsync(baselines, true, intent, token);
            var acceptedRemote = new PilotRemoteEntry(path, PilotEntryKind.File, result.FileId, result.Revision, result.Size, result.ContentHash);
            baselines[path] = Capture(path, local, acceptedRemote);
            state = await SaveAsync(baselines, true, null, token);
            decisions.Add(new SyncDecision(path, SyncDecisionKind.Uploaded,
                remote is null ? "Created in Dropbox after revalidating both folder identities and the held local source."
                    : "Updated the exact expected Dropbox revision after revalidating both folder identities and the held local source."));
        }
        catch (DropboxEffectUncertainException exception)
        {
            decisions.Add(new SyncDecision(path, SyncDecisionKind.Blocked,
                "Dropbox acknowledgement is uncertain; the persisted intent will be resolved by inventory before retry: " + exception.Message));
        }
        catch (OperationCanceledException) when (control.IsStopRequested)
        {
            decisions.Add(new SyncDecision(path, SyncDecisionKind.Stopped,
                "Stop interrupted an upload; the persisted intent will be resolved by inventory before retry."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DropboxApiException)
        {
            decisions.Add(new SyncDecision(path, SyncDecisionKind.Blocked,
                "Upload was not confirmed; its persisted intent remains authoritative until inventory proves the destination outcome: " +
                SafeMessage(exception)));
        }
        return state;
    }

    private async Task<SyncPairRuntimeState> DownloadAsync(string path, PilotRemoteEntry remote, PilotLocalEntry? local,
        IDictionary<string, SyncFileBaseline> baselines, PilotDirectoryLease rootLease,
        PilotDirectoryLease controlRoot, ICollection<SyncDecision> decisions, CancellationToken token)
    {
        await control.WaitBeforeNextEffectAsync(token);
        if (!SyncCapabilityGate.CreateNewDownload && local is null)
            throw new IOException(SyncCapabilityGate.ManualReviewReason("Existing-file download replacement"));
        var intentId = Guid.NewGuid().ToString("N");
        var artifacts = SyncLocalTransfer.CreateArtifactNames(local is not null);
        var intent = new SyncTransferIntent(intentId, pair.Id, path, SyncDirection.Download,
            remote.ContentHash!, local?.ContentHash, remote.Revision,
            artifacts.StageFileName, artifacts.BackupFileName,
            DateTimeOffset.UtcNow, CreationPolicyRevision: pair.PolicyRevision,
            ExpectedDestinationVolumeSerialNumber: local?.VolumeSerialNumber,
            ExpectedDestinationFileId: local?.FileId,
            ExpectedSourceId: remote.Id, ExpectedSourceRevision: remote.Revision,
            ExpectedSourceSize: remote.Size,
            ExpectedSourceVolumeSerialNumber: local?.VolumeSerialNumber,
            ExpectedSourceFileId: local?.FileId,
            ExpectedSourceLastWriteUtc: local?.LastWriteUtc);
        intent = SyncLocalTransactionDirectory.Prepare(controlRoot, intent);
        var state = await SaveAsync(baselines, true, intent, token);
        try
        {
            await remoteStore.VerifyRootIdentityAsync(token);
            var outcome = await SyncLocalTransfer.DownloadAsync(remote, local, rootLease, controlRoot, remoteStore, intent,
                async (bound, cancellation) =>
                {
                    intent = bound;
                    state = await SaveAsync(baselines, true, bound, cancellation);
                }, token);
            intent = outcome.Intent;
            var result = outcome.Result;
            var acceptedPath = PilotPath.ResolveContainedPath(rootLease.Path, path);
            PilotDirectoryIdentity acceptedIdentity;
            await using (var acceptedAuthority = SyncLocalFileAuthority.OpenExistingExclusive(acceptedPath))
            {
                acceptedIdentity = acceptedAuthority.ReadIdentity();
                if (intent.StageArtifactId is null ||
                !string.Equals($"{acceptedIdentity.VolumeSerialNumber:x8}:{acceptedIdentity.FileId:x16}",
                    intent.StageArtifactId, StringComparison.Ordinal))
                    throw new IOException("The committed local target is not the exact durably bound staged artifact.");
                var acceptedLocal = new PilotLocalEntry(path, PilotEntryKind.File, acceptedPath, result.Size,
                    File.GetLastWriteTimeUtc(acceptedPath), result.ContentHash, acceptedIdentity.VolumeSerialNumber,
                    acceptedIdentity.FileId);
                var acceptedRemote = remote with { Id = result.FileId, Revision = result.Revision, Size = result.Size, ContentHash = result.ContentHash };
                baselines[path] = Capture(path, acceptedLocal, acceptedRemote);
            }
            state = await CompleteCommittedLocalTransferAsync(state, baselines, intent, controlRoot, token);
            decisions.Add(new SyncDecision(path, SyncDecisionKind.Downloaded,
                local is null ? "Created locally from the exact inventoried Dropbox file and verified its content hash."
                    : "Replaced only the unchanged local baseline; the prior bytes were retained until baseline commit and then released."));
        }
        catch (OperationCanceledException) when (control.IsStopRequested)
        {
            decisions.Add(new SyncDecision(path, SyncDecisionKind.Stopped,
                "Stop interrupted a download; the persisted intent and any owned artifact will be reconciled before retry."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DropboxApiException)
        {
            decisions.Add(new SyncDecision(path, SyncDecisionKind.Blocked,
                "Download outcome needs inventory reconciliation before retry: " + SafeMessage(exception)));
        }
        return state;
    }

    private async Task<SyncPairRuntimeState> ArchiveRemoteAsync(string path, PilotRemoteEntry remote,
        IDictionary<string, SyncFileBaseline> baselines, ICollection<SyncDecision> decisions, CancellationToken token)
    {
        if (!SyncCapabilityGate.DeletionPropagation)
            throw new IOException(SyncCapabilityGate.ManualReviewReason("Dropbox deletion propagation"));
        await control.WaitBeforeNextEffectAsync(token);
        var operationId = Guid.NewGuid().ToString("N");
        var intent = new SyncTransferIntent(operationId, pair.Id, path, SyncDirection.Upload,
            remote.ContentHash!, remote.ContentHash, remote.Revision, null, null, DateTimeOffset.UtcNow,
            SyncIntentKind.ArchiveRemoteAfterLocalDeletion,
            RecoveryPath: BuildRemoteRecoveryPath(pair.Id, operationId), RecoveryOriginalPath: path,
            ExpectedSourceId: remote.Id, ExpectedSourceRevision: remote.Revision, ExpectedSourceSize: remote.Size,
            CreationPolicyRevision: pair.PolicyRevision);
        var state = await SaveAsync(baselines, true, intent, token);
        try
        {
            await remoteStore.VerifyRootIdentityAsync(token);
            var result = await remoteStore.CopyToRecoveryAsync(remote, intent.RecoveryPath!, token);
            ValidateArchiveResult(result, intent);
            intent = WithArchiveResult(intent, result);
            state = await SaveAsync(baselines, true, intent, token);
            await remoteStore.DeleteAsync(remote, token);
            baselines.Remove(path);
            state = await SaveAsync(baselines, true, null, token);
            decisions.Add(new SyncDecision(path, SyncDecisionKind.Archived,
                "The unchanged Dropbox peer was copied and verified in Sync Shark Recovery before its exact revision was conditionally deleted."));
        }
        catch (DropboxEffectUncertainException exception)
        {
            decisions.Add(new SyncDecision(path, SyncDecisionKind.Blocked,
                "Dropbox archive acknowledgement is uncertain; inventory will determine whether the main path remains: " + exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DropboxApiException)
        {
            decisions.Add(new SyncDecision(path, SyncDecisionKind.Blocked,
                "Dropbox did not confirm the recoverable archive; its persisted intent remains until inventory proves the main-path outcome: " +
                SafeMessage(exception)));
        }
        return state;
    }

    private async Task<SyncPairRuntimeState> ArchiveLocalAsync(string path, PilotLocalEntry local,
        IDictionary<string, SyncFileBaseline> baselines, PilotDirectoryLease rootLease,
        PilotDirectoryLease controlRoot,
        ICollection<SyncDecision> decisions, CancellationToken token)
    {
        if (!SyncCapabilityGate.DeletionPropagation)
            throw new IOException(SyncCapabilityGate.ManualReviewReason("Local deletion propagation"));
        await control.WaitBeforeNextEffectAsync(token);
        var operationId = Guid.NewGuid().ToString("N");
        var intent = new SyncTransferIntent(operationId, pair.Id, path, SyncDirection.Download,
            local.ContentHash!, local.ContentHash, null, null, null, DateTimeOffset.UtcNow,
            SyncIntentKind.ArchiveLocalAfterDropboxDeletion,
            RecoveryPath: SyncLocalRecovery.BuildRecoveryPath(controlRoot, pair.Id, operationId),
            RecoveryOriginalPath: path, ExpectedSourceId: SyncLocalRecovery.FormatSourceIdentity(local),
            ExpectedSourceSize: local.Size, CreationPolicyRevision: pair.PolicyRevision);
        var state = await SaveAsync(baselines, true, intent, token);
        try
        {
            var retained = await SyncLocalRecovery.ArchiveAsync(local, rootLease, controlRoot, pair.Id, intent.Id,
                intent.RecoveryPath!, intent.RecoveryFileId,
                async (bound, cancellation) =>
                {
                    intent = WithPreparedRecoveryResult(intent, bound);
                    state = await SaveAsync(baselines, true, intent, cancellation);
                }, token);
            ValidateLocalArchiveResult(retained, intent, intent.SourceContentHash);
            baselines.Remove(path);
            state = await SaveAsync(baselines, true, null, token);
            decisions.Add(new SyncDecision(path, SyncDecisionKind.Archived,
                "The unchanged local peer was moved into the pair recovery folder after the Dropbox deletion; no permanent delete was issued."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            decisions.Add(new SyncDecision(path, SyncDecisionKind.Blocked,
                "The local archive was not confirmed; its persisted intent remains until inventory proves the main-path outcome: " +
                exception.Message));
        }
        return state;
    }

    private async Task<(SyncPairRuntimeState State, bool SafeToContinue)> ResolvePendingAsync(
        SyncPairRuntimeState state,
        IDictionary<string, SyncFileBaseline> baselines,
        IReadOnlyDictionary<string, PilotLocalEntry> local,
        IReadOnlyDictionary<string, PilotRemoteEntry> remote,
        PilotDirectoryLease rootLease,
        PilotDirectoryLease controlRoot,
        ICollection<SyncDecision> decisions,
        CancellationToken token)
    {
        var pending = state.PendingTransfer!;
        if (!SyncCapabilityGate.IsExecutableIntent(pending))
            return RetainPending(state, pending, decisions,
                SyncCapabilityGate.ManualReviewReason("Persisted advanced transfer recovery"));
        local.TryGetValue(pending.RelativePath, out var localEntry);
        remote.TryGetValue(pending.RelativePath, out var remoteEntry);
        return pending.Kind switch
        {
            SyncIntentKind.Transfer => await ResolveTransferPendingAsync(state, baselines, pending, localEntry, remoteEntry,
                rootLease, controlRoot, decisions, token),
            SyncIntentKind.ArchiveRemoteAfterLocalDeletion => await ResolveRemoteArchivePendingAsync(state, baselines,
                pending, localEntry, remoteEntry, decisions, token),
            SyncIntentKind.ArchiveLocalAfterDropboxDeletion => await ResolveLocalArchivePendingAsync(state, baselines,
                pending, localEntry, remoteEntry, rootLease, controlRoot, decisions, token),
            SyncIntentKind.ResolveKeepLocal => await ResolveKeepLocalPendingAsync(state, baselines, pending, localEntry,
                remoteEntry, rootLease, decisions, token),
            SyncIntentKind.ResolveKeepDropbox => await ResolveKeepDropboxPendingAsync(state, baselines, pending, localEntry,
                remoteEntry, rootLease, controlRoot, decisions, token),
            _ => throw new InvalidDataException("The pending operation kind is not accepted.")
        };
    }

    private async Task<(SyncPairRuntimeState State, bool SafeToContinue)> ResolveTransferPendingAsync(
        SyncPairRuntimeState state,
        IDictionary<string, SyncFileBaseline> baselines,
        SyncTransferIntent pending,
        PilotLocalEntry? localEntry,
        PilotRemoteEntry? remoteEntry,
        PilotDirectoryLease rootLease,
        PilotDirectoryLease controlRoot,
        ICollection<SyncDecision> decisions,
        CancellationToken token)
    {
        if (pending.Direction == SyncDirection.Download)
        {
            if (!HasHash(remoteEntry, pending.SourceContentHash))
                return RetainPending(state, pending, decisions,
                    "The interrupted download source no longer matches its exact Dropbox content hash.");
            if (SyncCapabilityGate.IsSafeCreateNewIntent(pending))
            {
                if (HasHash(localEntry, pending.SourceContentHash))
                {
                    var observedIdentity = localEntry is null
                        ? null
                        : $"{localEntry.VolumeSerialNumber:x8}:{localEntry.FileId:x16}";
                    if (pending.StageArtifactId is null ||
                        !string.Equals(observedIdentity, pending.StageArtifactId, StringComparison.Ordinal))
                        return RetainPending(state, pending, decisions,
                            "The create-new destination has matching bytes but is not the exact durably staged object; it was preserved.");
                    baselines[pending.RelativePath] = Capture(pending.RelativePath, localEntry, remoteEntry);
                    state = await SaveAsync(baselines, true, null, token);
                    decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
                        "Inventory confirmed the interrupted create-new download exactly once."));
                    return (state, true);
                }
                if (localEntry is null)
                {
                    SyncCreateNewLocalTransfer.CleanupUncommittedStage(rootLease, pending);
                    state = await SaveAsync(baselines, true, null, token);
                    decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
                        "Inventory proved the interrupted create-new download was not applied; a later run may retry."));
                    return (state, true);
                }
                return RetainPending(state, pending, decisions,
                    "The create-new local destination contains unexpected bytes and was preserved for manual review.");
            }
            if (SyncCapabilityGate.IsPrivateReplacementIntent(pending))
            {
                var replacement = await SyncPrivateReplacement.RecoverAsync(remoteEntry!, rootLease, pending,
                    async (bound, cancellation) => state = await SaveAsync(baselines, true, bound, cancellation), token);
                pending = replacement.Intent;
                if (replacement.Disposition == SyncLocalPendingDisposition.Blocked)
                    return RetainPending(state, pending, decisions, replacement.Reason);
                if (replacement.Disposition == SyncLocalPendingDisposition.Committed)
                {
                    baselines[pending.RelativePath] = Capture(pending.RelativePath, replacement.LocalEntry, remoteEntry);
                    state = await SaveAsync(baselines, true, pending, token);
                    await using var committedTarget = await SyncPrivateReplacement.OpenVerifiedCommittedTargetAsync(rootLease, pending, token);
                    pending = pending with { Phase = SyncIntentPhase.CleanupCompleted };
                    state = await SaveAsync(baselines, true, pending, token);
                    SyncPrivateReplacement.CleanupCommitted(pending);
                    PrivateRollbackCleanedForTest?.Invoke();
                }
                state = await SaveAsync(baselines, true, null, token);
                decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered, replacement.Reason));
                return (state, true);
            }
            var recovery = await SyncLocalTransfer.RecoverInterruptedDownloadAsync(remoteEntry!, rootLease, controlRoot, pending,
                async (bound, cancellation) => state = await SaveAsync(baselines, true, bound, cancellation), token);
            pending = recovery.Intent;
            if (recovery.Disposition == SyncLocalPendingDisposition.Blocked)
                return RetainPending(state, pending, decisions, recovery.Reason);
            if (recovery.Disposition == SyncLocalPendingDisposition.Committed)
            {
                baselines[pending.RelativePath] = Capture(pending.RelativePath, recovery.LocalEntry, remoteEntry);
                state = await CompleteCommittedLocalTransferAsync(state, baselines, pending, controlRoot, token);
                decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
                    "The held local objects prove the interrupted download committed exactly once; its baseline is now durable."));
                return (state, true);
            }
            state = await CompleteCommittedLocalTransferAsync(state, baselines, pending, controlRoot, token);
            decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
                "Held-object recovery proved that the download was not applied; fresh inventory may plan one retry."));
            return (state, true);
        }

        var sourceStillPresent = MatchesLocalIntentSource(localEntry, pending);
        if (pending.RecoveryPath is not null)
        {
            try
            {
                var retained = await remoteStore.GetRecoveryAsync(pending.RecoveryPath, token);
                if (retained is not null)
                {
                    ValidateArchiveResult(retained, pending);
                    if (pending.Phase == SyncIntentPhase.Prepared)
                    {
                        pending = WithArchiveResult(pending, retained);
                        state = await SaveAsync(baselines, true, pending, token);
                    }
                }
                if (!sourceStillPresent)
                    return RetainPending(state, pending, decisions,
                        "The exact local upload source identity changed while replacement recovery was pending.");
                if (IsExpectedReplacementSuccessor(remoteEntry, pending))
                {
                    if (retained is null)
                        return RetainPending(state, pending, decisions,
                            "Dropbox contains the replacement bytes but the exact predecessor recovery object is absent.");
                    baselines[pending.RelativePath] = Capture(pending.RelativePath, localEntry, remoteEntry);
                    state = await SaveAsync(baselines, true, null, token);
                    decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
                        "Inventory confirmed the exact replacement successor and its predecessor recovery object."));
                    return (state, true);
                }
                if (retained is null)
                {
                    if (pending.Phase != SyncIntentPhase.Prepared || !MatchesRemoteReplacementPredecessor(remoteEntry, pending))
                        return RetainPending(state, pending, decisions,
                            "The exact Dropbox predecessor cannot be proven before recovery copy.");
                    await control.WaitBeforeNextEffectAsync(token);
                    retained = await remoteStore.CopyToRecoveryAsync(remoteEntry!, pending.RecoveryPath!, token);
                    ValidateArchiveResult(retained, pending);
                    pending = WithArchiveResult(pending, retained);
                    state = await SaveAsync(baselines, true, pending, token);
                }
                if (!MatchesRemoteReplacementPredecessor(remoteEntry, pending))
                    return RetainPending(state, pending, decisions,
                        "Dropbox no longer contains the exact predecessor revision after recovery was secured.");
                await control.WaitBeforeNextEffectAsync(token);
                rootLease.VerifyBinding();
                await using var source = await PilotLocalScanner.OpenValidatedForUploadAsync(localEntry!, rootLease, token,
                    contentProvider: pair.Provider, expectedPairId: pair.Id);
                var uploaded = await remoteStore.UploadAsync(pending.RelativePath, source, pending.SourceContentHash,
                    pending.ExpectedDestinationRevision, token);
                await PilotLocalScanner.VerifyHeldSourceAfterUploadAsync(source, localEntry!, rootLease, token,
                    contentProvider: pair.Provider);
                ValidateUploadResult(uploaded, localEntry!);
                pending = pending with
                {
                    Phase = SyncIntentPhase.SuccessorCreated,
                    SuccessorFileId = uploaded.FileId,
                    SuccessorRevision = uploaded.Revision,
                    SuccessorContentHash = uploaded.ContentHash,
                    SuccessorSize = uploaded.Size
                };
                state = await SaveAsync(baselines, true, pending, token);
                var accepted = new PilotRemoteEntry(pending.RelativePath, PilotEntryKind.File, uploaded.FileId,
                    uploaded.Revision, uploaded.Size, uploaded.ContentHash);
                baselines[pending.RelativePath] = Capture(pending.RelativePath, localEntry, accepted);
                state = await SaveAsync(baselines, true, null, token);
                decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
                    "The exact Dropbox predecessor was recovered before one revision-conditional replacement."));
                return (state, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DropboxApiException or DropboxEffectUncertainException)
            {
                return RetainPending(state, pending, decisions,
                    "Replacement upload recovery remains pending: " + SafeMessage(exception));
            }
        }
        if (sourceStillPresent && HasHash(remoteEntry, pending.SourceContentHash))
        {
            baselines[pending.RelativePath] = Capture(pending.RelativePath, localEntry, remoteEntry);
            state = await SaveAsync(baselines, true, null, token);
            decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
                "Inventory confirmed the interrupted upload exactly once; the baseline was committed without repeating it."));
            return (state, true);
        }
        if (sourceStillPresent && MatchesExpectedRemote(remoteEntry, pending.ExpectedDestinationContentHash,
                pending.ExpectedDestinationRevision, pending.ExpectedDestinationRemoteId))
        {
            state = await SaveAsync(baselines, true, null, token);
            decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
                "Exact Dropbox inventory proved that the interrupted upload was not applied; fresh inventory may plan one retry."));
            return (state, true);
        }
        return RetainPending(state, pending, decisions,
            "The interrupted upload no longer matches either its held source or exact expected Dropbox destination.");
    }

    private async Task<(SyncPairRuntimeState State, bool SafeToContinue)> ResolveRemoteArchivePendingAsync(
        SyncPairRuntimeState state,
        IDictionary<string, SyncFileBaseline> baselines,
        SyncTransferIntent pending,
        PilotLocalEntry? localEntry,
        PilotRemoteEntry? remoteEntry,
        ICollection<SyncDecision> decisions,
        CancellationToken token)
    {
        if (localEntry is not null)
            return RetainPending(state, pending, decisions,
                "The local path reappeared while its peer-archive intent was pending; no Dropbox effect was repeated.");
        try
        {
            await remoteStore.VerifyRootIdentityAsync(token);
            var retained = await remoteStore.GetRecoveryAsync(RequireRecoveryPath(pending), token);
            if (retained is not null)
            {
                ValidateArchiveResult(retained, pending);
                if (pending.Phase == SyncIntentPhase.Prepared)
                {
                    pending = WithArchiveResult(pending, retained);
                    state = await SaveAsync(baselines, true, pending, token);
                }
            }

            if (retained is null)
            {
                if (pending.Phase != SyncIntentPhase.Prepared)
                    return RetainPending(state, pending, decisions,
                        "The durable recovery phase has no exact Dropbox recovery object.");
                if (!MatchesRemoteIntentSource(remoteEntry, pending))
                    return RetainPending(state, pending, decisions,
                        "The Dropbox peer changed before its exact revision could be copied to recovery.");
                await control.WaitBeforeNextEffectAsync(token);
                retained = await remoteStore.CopyToRecoveryAsync(remoteEntry!, RequireRecoveryPath(pending), token);
                ValidateArchiveResult(retained, pending);
                pending = WithArchiveResult(pending, retained);
                state = await SaveAsync(baselines, true, pending, token);
            }

            if (remoteEntry is not null)
            {
                if (!MatchesRemoteIntentSource(remoteEntry, pending))
                    return RetainPending(state, pending, decisions,
                        "A different Dropbox object occupies the main path after recovery was secured.");
                await control.WaitBeforeNextEffectAsync(token);
                await remoteStore.DeleteAsync(remoteEntry, token);
            }
            baselines.Remove(pending.RelativePath);
            state = await SaveAsync(baselines, true, null, token);
            decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
                "The exact Dropbox recovery object was verified before the expected main-path revision was conditionally deleted."));
            return (state, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DropboxApiException or DropboxEffectUncertainException)
        {
            return RetainPending(state, pending, decisions,
                "Dropbox still has not confirmed the recoverable archive; inventory must reconcile it again: " + SafeMessage(exception));
        }
    }

    private async Task<(SyncPairRuntimeState State, bool SafeToContinue)> ResolveLocalArchivePendingAsync(
        SyncPairRuntimeState state,
        IDictionary<string, SyncFileBaseline> baselines,
        SyncTransferIntent pending,
        PilotLocalEntry? localEntry,
        PilotRemoteEntry? remoteEntry,
        PilotDirectoryLease rootLease,
        PilotDirectoryLease controlRoot,
        ICollection<SyncDecision> decisions,
        CancellationToken token)
    {
        if (remoteEntry is not null)
            return RetainPending(state, pending, decisions,
                "The Dropbox path reappeared while its local peer-archive intent was pending; no local effect was repeated.");
        try
        {
            var expected = ExpectedLocalArchiveEntry(pending, rootLease, localEntry,
                pending.SourceContentHash);
            var retained = await SyncLocalRecovery.GetArchiveAsync(expected, controlRoot, pair.Id, pending.Id,
                RequireRecoveryPath(pending), pending.RecoveryFileId, token);
            if (retained is not null)
            {
                if (pending.RecoveryFileId is null)
                {
                    pending = WithPreparedRecoveryResult(pending, retained);
                    state = await SaveAsync(baselines, true, pending, token);
                }
                ValidateLocalArchiveResult(retained, pending, pending.SourceContentHash);
                if (localEntry is not null)
                {
                    if (!HasHash(localEntry, pending.SourceContentHash))
                        return RetainPending(state, pending, decisions,
                            "The exact local recovery object exists, but a changed main-path object also exists.");
                    await control.WaitBeforeNextEffectAsync(token);
                    retained = await SyncLocalRecovery.ArchiveAsync(localEntry, rootLease, controlRoot, pair.Id, pending.Id,
                        RequireRecoveryPath(pending), pending.RecoveryFileId,
                        async (bound, cancellation) =>
                        {
                            pending = WithPreparedRecoveryResult(pending, bound);
                            state = await SaveAsync(baselines, true, pending, cancellation);
                        }, token);
                    ValidateLocalArchiveResult(retained, pending, pending.SourceContentHash);
                }
                baselines.Remove(pending.RelativePath);
                state = await SaveAsync(baselines, true, null, token);
                decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
                    "The exact prebound local recovery object proves the peer archive completed."));
                return (state, true);
            }
            if (localEntry is null)
                return RetainPending(state, pending, decisions,
                    "The local main path is absent, but the exact prebound recovery object is also absent; source absence is not archive proof.");
            if (!HasHash(localEntry, pending.SourceContentHash))
                return RetainPending(state, pending, decisions,
                    "The local peer changed while its recoverable archive intent was pending.");
            await control.WaitBeforeNextEffectAsync(token);
            retained = await SyncLocalRecovery.ArchiveAsync(localEntry, rootLease, controlRoot, pair.Id, pending.Id,
                RequireRecoveryPath(pending), pending.RecoveryFileId,
                async (bound, cancellation) =>
                {
                    pending = WithPreparedRecoveryResult(pending, bound);
                    state = await SaveAsync(baselines, true, pending, cancellation);
                }, token);
            ValidateLocalArchiveResult(retained, pending, pending.SourceContentHash);
            baselines.Remove(pending.RelativePath);
            state = await SaveAsync(baselines, true, null, token);
            decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
                "The exact unchanged local peer was recoverably archived after inventory proved the prior effect absent."));
            return (state, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return RetainPending(state, pending, decisions,
                "Local held-object archive recovery remains incomplete: " + exception.Message);
        }
    }

    private async Task<(SyncPairRuntimeState State, bool SafeToContinue)> ResolveKeepLocalPendingAsync(
        SyncPairRuntimeState state,
        IDictionary<string, SyncFileBaseline> baselines,
        SyncTransferIntent pending,
        PilotLocalEntry? localEntry,
        PilotRemoteEntry? remoteEntry,
        PilotDirectoryLease rootLease,
        ICollection<SyncDecision> decisions,
        CancellationToken token)
    {
        if (!MatchesLocalIntentSource(localEntry, pending))
            return RetainPending(state, pending, decisions,
                "The chosen local source changed while conflict resolution was pending.");
        try
        {
            await remoteStore.VerifyRootIdentityAsync(token);
            var retained = await remoteStore.GetRecoveryAsync(RequireRecoveryPath(pending), token);
            if (retained is not null) ValidateArchiveResult(retained, pending);

            if (IsExpectedReplacementSuccessor(remoteEntry, pending))
            {
                if (retained is null)
                    return RetainPending(state, pending, decisions,
                        "The chosen local bytes reached Dropbox, but the exact prebound conflict recovery object is absent.");
                baselines[pending.RelativePath] = Capture(pending.RelativePath, localEntry, remoteEntry);
                state = await SaveAsync(baselines, true, null, token);
                decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
                    "Exact Dropbox recovery proof and main-path inventory confirm the conflict resolution completed."));
                return (state, true);
            }

            if (pending.Phase == SyncIntentPhase.Prepared)
            {
                if (retained is null)
                {
                    if (remoteEntry is null)
                        return RetainPending(state, pending, decisions,
                            "The conflicting Dropbox main path is absent, but its exact prebound recovery object is also absent.");
                    if (!MatchesExpectedRemote(remoteEntry, pending.ExpectedDestinationContentHash,
                            pending.ExpectedDestinationRevision, pending.ExpectedDestinationRemoteId))
                        return RetainPending(state, pending, decisions,
                            "The Dropbox conflict version changed before its recoverable archive could be proven.");
                    await control.WaitBeforeNextEffectAsync(token);
                    retained = await remoteStore.CopyToRecoveryAsync(remoteEntry, RequireRecoveryPath(pending), token);
                    ValidateArchiveResult(retained, pending);
                }
                pending = WithArchiveResult(pending, retained);
                state = await SaveAsync(baselines, true, pending, token);
                decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Archived,
                    "The exact Dropbox conflict version is preserved at its prebound recovery destination."));
            }
            else if (retained is null)
            {
                return RetainPending(state, pending, decisions,
                    "The composite intent claims archive completion, but the exact prebound Dropbox recovery object is absent.");
            }

            if (!MatchesRemoteIntentSource(remoteEntry, pending))
                return RetainPending(state, pending, decisions,
                    "The Dropbox main path is no longer the exact predecessor revision after the recovery phase.");
            await control.WaitBeforeNextEffectAsync(token);
            rootLease.VerifyBinding();
            await using var source = await PilotLocalScanner.OpenValidatedForUploadAsync(localEntry!, rootLease, token,
                contentProvider: pair.Provider, expectedPairId: pair.Id);
            var result = await remoteStore.UploadAsync(pending.RelativePath, source, pending.SourceContentHash,
                pending.ExpectedDestinationRevision, token);
            await PilotLocalScanner.VerifyHeldSourceAfterUploadAsync(source, localEntry!, rootLease, token,
                contentProvider: pair.Provider);
            ValidateUploadResult(result, localEntry!);
            pending = pending with
            {
                Phase = SyncIntentPhase.SuccessorCreated,
                SuccessorFileId = result.FileId,
                SuccessorRevision = result.Revision,
                SuccessorContentHash = result.ContentHash,
                SuccessorSize = result.Size
            };
            state = await SaveAsync(baselines, true, pending, token);
            var acceptedRemote = new PilotRemoteEntry(pending.RelativePath, PilotEntryKind.File, result.FileId,
                result.Revision, result.Size, result.ContentHash);
            baselines[pending.RelativePath] = Capture(pending.RelativePath, localEntry, acceptedRemote);
            state = await SaveAsync(baselines, true, null, token);
            decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Uploaded,
                "The chosen local version conditionally replaced the exact Dropbox predecessor after its recovery copy was durable."));
            return (state, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DropboxApiException or DropboxEffectUncertainException)
        {
            return RetainPending(state, pending, decisions,
                "The composite intent remains archive-complete while Dropbox upload outcome is reconciled: " + SafeMessage(exception));
        }
    }

    private async Task<(SyncPairRuntimeState State, bool SafeToContinue)> ResolveKeepDropboxPendingAsync(
        SyncPairRuntimeState state,
        IDictionary<string, SyncFileBaseline> baselines,
        SyncTransferIntent pending,
        PilotLocalEntry? localEntry,
        PilotRemoteEntry? remoteEntry,
        PilotDirectoryLease rootLease,
        PilotDirectoryLease controlRoot,
        ICollection<SyncDecision> decisions,
        CancellationToken token)
    {
        if (!MatchesRemoteSource(remoteEntry, pending))
            return RetainPending(state, pending, decisions,
                "The chosen Dropbox source changed while conflict resolution was pending.");
        try
        {
            var recoveryHash = ExpectedRecoveryHash(pending);
            var expectedRecovery = ExpectedLocalArchiveEntry(pending, rootLease,
                MatchesExpected(localEntry?.ContentHash, recoveryHash) ? localEntry : null, recoveryHash);
            var retained = await SyncLocalRecovery.GetArchiveAsync(expectedRecovery, controlRoot, pair.Id, pending.Id,
                RequireRecoveryPath(pending), pending.RecoveryFileId, token);
            if (retained is not null)
            {
                if (pending.RecoveryFileId is null)
                {
                    pending = WithPreparedRecoveryResult(pending, retained);
                    state = await SaveAsync(baselines, true, pending, token);
                }
                ValidateLocalArchiveResult(retained, pending, recoveryHash);
            }

            if (HasHash(localEntry, pending.SourceContentHash))
            {
                if (retained is null)
                    return RetainPending(state, pending, decisions,
                        "The chosen Dropbox bytes reached the local main path, but the exact prebound conflict recovery object is absent.");
                if (!MatchesArtifactIdentity(localEntry, pending.StageArtifactId))
                    return RetainPending(state, pending, decisions,
                        "The chosen Dropbox bytes are present, but the local target is not the exact durably staged object.");
                baselines[pending.RelativePath] = Capture(pending.RelativePath, localEntry, remoteEntry);
                state = await CompleteCommittedLocalTransferAsync(state, baselines, pending, rootLease, token);
                decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
                    "Exact local recovery proof and main-path inventory confirm the conflict resolution completed."));
                return (state, true);
            }

            if (pending.Phase == SyncIntentPhase.Prepared)
            {
                if (retained is null)
                {
                    if (localEntry is null)
                        return RetainPending(state, pending, decisions,
                            "The conflicting local main path is absent, but its exact prebound recovery object is also absent.");
                    if (!MatchesExpected(localEntry.ContentHash, recoveryHash))
                        return RetainPending(state, pending, decisions,
                            "The local conflict version changed before its recoverable archive could be proven.");
                    await control.WaitBeforeNextEffectAsync(token);
                    retained = await SyncLocalRecovery.ArchiveAsync(localEntry, rootLease, controlRoot, pair.Id, pending.Id,
                        RequireRecoveryPath(pending), pending.RecoveryFileId,
                        async (bound, cancellation) =>
                        {
                            pending = WithPreparedRecoveryResult(pending, bound);
                            state = await SaveAsync(baselines, true, pending, cancellation);
                        }, token);
                    ValidateLocalArchiveResult(retained, pending, recoveryHash);
                    localEntry = null;
                }
                else if (localEntry is not null)
                {
                    if (!MatchesExpected(localEntry.ContentHash, recoveryHash))
                        return RetainPending(state, pending, decisions,
                            "The exact local recovery object exists, but a changed main-path object also exists.");
                    await control.WaitBeforeNextEffectAsync(token);
                    retained = await SyncLocalRecovery.ArchiveAsync(localEntry, rootLease, controlRoot, pair.Id, pending.Id,
                        RequireRecoveryPath(pending), pending.RecoveryFileId,
                        async (bound, cancellation) =>
                        {
                            pending = WithPreparedRecoveryResult(pending, bound);
                            state = await SaveAsync(baselines, true, pending, cancellation);
                        }, token);
                    ValidateLocalArchiveResult(retained, pending, recoveryHash);
                    localEntry = null;
                }
                pending = WithArchiveResult(pending, retained);
                state = await SaveAsync(baselines, true, pending, token);
                decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Archived,
                    "The exact local conflict version is preserved at its prebound recovery destination."));
            }
            else if (retained is null)
            {
                return RetainPending(state, pending, decisions,
                    "The composite intent claims archive completion, but the exact prebound local recovery object is absent.");
            }

            var recovery = await SyncLocalTransfer.RecoverInterruptedDownloadAsync(remoteEntry!, rootLease, controlRoot, pending,
                async (bound, cancellation) => state = await SaveAsync(baselines, true, bound, cancellation), token);
            pending = recovery.Intent;
            if (recovery.Disposition == SyncLocalPendingDisposition.Blocked)
                return RetainPending(state, pending, decisions, recovery.Reason);
            PilotLocalEntry acceptedLocal;
            if (recovery.Disposition == SyncLocalPendingDisposition.Committed)
            {
                acceptedLocal = recovery.LocalEntry!;
            }
            else
            {
                await control.WaitBeforeNextEffectAsync(token);
                await remoteStore.VerifyRootIdentityAsync(token);
                var outcome = await SyncLocalTransfer.DownloadAsync(remoteEntry!, null, rootLease, controlRoot, remoteStore, pending,
                    async (bound, cancellation) => state = await SaveAsync(baselines, true, bound, cancellation), token);
                pending = outcome.Intent;
                var result = outcome.Result;
                var target = PilotPath.ResolveContainedPath(rootLease.Path, pending.RelativePath);
                acceptedLocal = new PilotLocalEntry(pending.RelativePath, PilotEntryKind.File, target, result.Size,
                    File.GetLastWriteTimeUtc(target), result.ContentHash);
            }
            baselines[pending.RelativePath] = Capture(pending.RelativePath, acceptedLocal, remoteEntry);
            state = await CompleteCommittedLocalTransferAsync(state, baselines, pending, controlRoot, token);
            decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Downloaded,
                "The chosen Dropbox version was committed to the empty local main path after the archive phase was durable."));
            return (state, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DropboxApiException)
        {
            return RetainPending(state, pending, decisions,
                "The composite intent remains archive-complete while held local download recovery is reconciled: " + SafeMessage(exception));
        }
    }

    private static (SyncPairRuntimeState State, bool SafeToContinue) RetainPending(SyncPairRuntimeState state,
        SyncTransferIntent pending, ICollection<SyncDecision> decisions, string reason)
    {
        decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Blocked, reason));
        return (state, false);
    }

    private static void ValidateUploadResult(PilotUploadResult result, PilotLocalEntry expected)
    {
        if (result.Size != expected.Size ||
            !string.Equals(result.ContentHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(result.FileId) || string.IsNullOrWhiteSpace(result.Revision))
            throw new DropboxEffectUncertainException("Dropbox returned incomplete or mismatched upload metadata.");
    }

    private static void ValidateArchiveResult(SyncArchiveResult result, SyncTransferIntent intent)
    {
        if (string.IsNullOrWhiteSpace(result.FileId) || string.IsNullOrWhiteSpace(result.Revision))
            throw new DropboxEffectUncertainException("Dropbox recovery metadata has no exact file ID and revision.");
        var expectedSize = ExpectedRecoverySize(intent);
        if (result.Size != expectedSize)
            throw new DropboxEffectUncertainException(
                $"Dropbox recovery metadata has an unexpected predecessor size ({result.Size} instead of {expectedSize}).");
        if (!string.Equals(result.ContentHash, ExpectedRecoveryHash(intent), StringComparison.OrdinalIgnoreCase))
            throw new DropboxEffectUncertainException("Dropbox recovery metadata has an unexpected predecessor content hash.");
        if (!string.Equals(result.RecoveryPath, RequireRecoveryPath(intent), StringComparison.OrdinalIgnoreCase))
            throw new DropboxEffectUncertainException("Dropbox recovery metadata names a different recovery destination.");
        if (intent.RecoveryFileId is not null &&
            (!string.Equals(result.FileId, intent.RecoveryFileId, StringComparison.Ordinal) ||
             !string.Equals(result.Revision, intent.RecoveryRevision, StringComparison.Ordinal)))
            throw new DropboxEffectUncertainException("The Dropbox recovery object no longer matches its durable identity.");
    }

    private static void ValidateLocalArchiveResult(SyncLocalArchiveResult result, SyncTransferIntent intent,
        string expectedHash)
    {
        if (intent.RecoveryFileId is not null)
        {
            var expectedIdentity = SyncLocalRecovery.ParseSourceIdentity(intent.RecoveryFileId);
            if (result.VolumeSerialNumber != expectedIdentity.VolumeSerialNumber || result.FileId != expectedIdentity.FileId)
                throw new IOException("The reopened local recovery object does not match the exact persisted recovery file identity.");
        }
        else if (intent.Phase == SyncIntentPhase.ArchiveCompleted)
            throw new IOException("The completed local recovery intent has no durable file identity.");
        if (result.VolumeSerialNumber == 0 || result.FileId == 0 || result.Size != ExpectedRecoverySize(intent) ||
            !string.Equals(result.ContentHash, expectedHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.RecoveryPath, RequireRecoveryPath(intent), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The local recovery readback did not match the exact prebound source identity.");
    }

    private static SyncTransferIntent WithArchiveResult(SyncTransferIntent intent, SyncArchiveResult result) =>
        intent with
        {
            Phase = SyncIntentPhase.ArchiveCompleted,
            RecoveryFileId = result.FileId,
            RecoveryRevision = result.Revision,
            RecoveryContentHash = result.ContentHash,
            RecoverySize = result.Size
        };

    private static SyncTransferIntent WithArchiveResult(SyncTransferIntent intent, SyncLocalArchiveResult result) =>
        intent with
        {
            Phase = SyncIntentPhase.ArchiveCompleted,
            RecoveryFileId = $"{result.VolumeSerialNumber:x8}:{result.FileId:x16}",
            RecoveryContentHash = result.ContentHash,
            RecoverySize = result.Size
        };

    private static SyncTransferIntent WithPreparedRecoveryResult(SyncTransferIntent intent,
        SyncLocalArchiveResult result) => intent with
    {
        RecoveryFileId = $"{result.VolumeSerialNumber:x8}:{result.FileId:x16}",
        RecoveryContentHash = result.ContentHash,
        RecoverySize = result.Size
    };

    private static PilotLocalEntry ExpectedLocalArchiveEntry(SyncTransferIntent intent, PilotDirectoryLease rootLease,
        PilotLocalEntry? current, string expectedHash)
    {
        var replacingLocal = intent.Kind == SyncIntentKind.ResolveKeepDropbox;
        var identity = replacingLocal
            ? new PilotDirectoryIdentity(
                intent.ExpectedDestinationVolumeSerialNumber ??
                throw new InvalidDataException("The local predecessor volume identity is absent."),
                intent.ExpectedDestinationFileId ??
                throw new InvalidDataException("The local predecessor file identity is absent."))
            : SyncLocalRecovery.ParseSourceIdentity(intent.ExpectedSourceId ?? string.Empty);
        var size = replacingLocal
            ? intent.ExpectedDestinationSize ?? throw new InvalidDataException("The local predecessor size is absent.")
            : intent.ExpectedSourceSize ?? throw new InvalidDataException("The pending local recovery source size is absent.");
        var path = PilotPath.ResolveContainedPath(rootLease.Path, intent.RelativePath);
        return new PilotLocalEntry(intent.RelativePath, PilotEntryKind.File, path, size,
            current?.LastWriteUtc ?? DateTime.MinValue, expectedHash, identity.VolumeSerialNumber, identity.FileId);
    }

    private static string ExpectedRecoveryHash(SyncTransferIntent intent) =>
        intent.Kind is SyncIntentKind.ResolveKeepLocal or SyncIntentKind.ResolveKeepDropbox ||
        intent.Kind == SyncIntentKind.Transfer && intent.Direction == SyncDirection.Upload && intent.RecoveryPath is not null
            ? intent.ExpectedDestinationContentHash ?? throw new InvalidDataException("The recovery predecessor hash is absent.")
            : intent.SourceContentHash;

    private static long ExpectedRecoverySize(SyncTransferIntent intent) =>
        intent.Kind is SyncIntentKind.ResolveKeepLocal or SyncIntentKind.ResolveKeepDropbox ||
        intent.Kind == SyncIntentKind.Transfer && intent.Direction == SyncDirection.Upload && intent.RecoveryPath is not null
            ? intent.ExpectedDestinationSize ?? throw new InvalidDataException("The recovery predecessor size is absent.")
            : intent.ExpectedSourceSize ?? throw new InvalidDataException("The recovery source size is absent.");

    private static string RequireRecoveryPath(SyncTransferIntent intent) =>
        string.IsNullOrWhiteSpace(intent.RecoveryPath)
            ? throw new InvalidDataException("The pending recovery destination is absent.")
            : intent.RecoveryPath;

    private static string BuildRemoteRecoveryPath(string pairId, string operationId)
    {
        return SyncDropboxArtifactPaths.BuildRecoveryOperationPath(pairId, operationId);
    }

    private IReadOnlySet<string> RestorePersistedLocalAccessBeforeScan(PilotDirectoryLease rootLease,
        PilotDirectoryLease controlRoot, SyncPairRuntimeState state)
    {
        if (!string.Equals(state.PairId, pair.Id, StringComparison.Ordinal))
            throw new InvalidDataException("The checksummed runtime aggregate does not match the selected sync pair.");

        var exclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            controlRoot.Path
        };
        var pending = state.PendingTransfer;
        if (pending is null) return exclusions;

        if (!string.Equals(pending.PairId, pair.Id, StringComparison.Ordinal))
            throw new InvalidDataException("The persisted pending operation does not match the selected sync pair.");
        if (!SyncCapabilityGate.IsExecutableIntent(pending))
            throw new IOException(SyncCapabilityGate.ManualReviewReason("Persisted advanced transfer recovery"));
        if (SyncCapabilityGate.IsPrivateReplacementIntent(pending)) return exclusions;
        if (pending.Direction == SyncDirection.Download && pending.Kind == SyncIntentKind.Transfer &&
            string.Equals(pending.StageFileName, SyncCreateNewLocalTransfer.StageName(pending.Id), StringComparison.Ordinal))
        {
            SyncCreateNewLocalTransfer.RestoreAccess(rootLease, pending);
            var stagePath = Path.Combine(rootLease.Path, pending.StageFileName!);
            exclusions.Add(stagePath);
            exclusions.Add(SyncLocalFileAuthority.RecoverySidecarPath(stagePath));
        }
        else if (pending.Direction == SyncDirection.Download &&
                 pending.Kind is SyncIntentKind.Transfer or SyncIntentKind.ResolveKeepDropbox)
            foreach (var artifactPath in SyncLocalTransfer.RestorePreboundWritableObjectAccess(rootLease, controlRoot, pending))
                exclusions.Add(Path.GetFullPath(artifactPath));

        if (pending.Kind is SyncIntentKind.ArchiveLocalAfterDropboxDeletion or SyncIntentKind.ResolveKeepDropbox)
        {
            var recoveryPath = SyncLocalRecovery.RestorePreboundWritableObjectAccess(controlRoot, pair.Id, pending);
            exclusions.Add(Path.GetDirectoryName(recoveryPath)!);
        }
        rootLease.VerifyBinding();
        return exclusions;
    }

    private async Task<SyncPairRuntimeState> CompletePendingCleanupBeforeScanAsync(SyncPairRuntimeState state,
        PilotDirectoryLease rootLease, PilotDirectoryLease controlRoot,
        ICollection<SyncDecision> decisions, CancellationToken token)
    {
        var pending = state.PendingTransfer;
        if (pending?.Phase is not (SyncIntentPhase.CommittedCleanupPending or SyncIntentPhase.CleanupCompleted)) return state;
        if (SyncCapabilityGate.IsPrivateReplacementIntent(pending))
        {
            var replacementBaselines = state.Files.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
            if (pending.Phase == SyncIntentPhase.CommittedCleanupPending)
            {
                await using var committedTarget = await SyncPrivateReplacement.OpenVerifiedCommittedTargetAsync(rootLease, pending, token);
                pending = pending with { Phase = SyncIntentPhase.CleanupCompleted };
                state = await SaveAsync(replacementBaselines, true, pending, token);
                SyncPrivateReplacement.CleanupCommitted(pending);
                PrivateRollbackCleanedForTest?.Invoke();
            }
            else
            {
                await using var committedTarget = await SyncPrivateReplacement.OpenVerifiedCommittedTargetAsync(rootLease, pending, token);
                SyncPrivateReplacement.CleanupCommitted(pending);
                PrivateRollbackCleanedForTest?.Invoke();
            }
            SyncPrivateReplacement.VerifyCleanupCompleted(pending);
            state = await SaveAsync(replacementBaselines, true, null, token);
            decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
                "The exact retained local predecessor was cleaned before ordinary inventory."));
            return state;
        }
        var baselines = state.Files.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        state = await CompleteCommittedLocalTransferAsync(state, baselines, pending, controlRoot, token);
        decisions.Add(new SyncDecision(pending.RelativePath, SyncDecisionKind.Recovered,
            "The exact committed transfer artifacts were cleaned before ordinary user-entry enumeration."));
        return state;
    }

    private async Task<SyncPairRuntimeState> CompleteCommittedLocalTransferAsync(SyncPairRuntimeState state,
        IDictionary<string, SyncFileBaseline> baselines, SyncTransferIntent intent, PilotDirectoryLease controlRoot,
        CancellationToken token)
    {
        var cleanupPending = intent.Phase == SyncIntentPhase.CommittedCleanupPending
            ? intent
            : intent with { Phase = SyncIntentPhase.CommittedCleanupPending };
        if (intent.Phase != SyncIntentPhase.CommittedCleanupPending)
        {
            state = await SaveAsync(baselines, true, cleanupPending, token);
            CleanupStatePersistedForTest?.Invoke(cleanupPending.Kind, cleanupPending.RelativePath);
        }

        SyncLocalTransfer.FinalizeArtifacts(controlRoot, cleanupPending);
        CleanupArtifactsVerifiedForTest?.Invoke(cleanupPending.Kind, cleanupPending.RelativePath);
        return await SaveAsync(baselines, true, null, token);
    }

    private Task<SyncPairRuntimeState> SaveAsync(IDictionary<string, SyncFileBaseline> baselines, bool established,
        SyncTransferIntent? pending, CancellationToken token)
    {
        var state = new SyncPairRuntimeState(pair.Id, established,
            baselines.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), pending);
        return SaveCoreAsync(state, token);
    }

    private async Task<SyncPairRuntimeState> SaveCoreAsync(SyncPairRuntimeState state, CancellationToken token)
    {
        await stateStore.SavePairAsync(state,
            _runGeneration ?? throw new InvalidOperationException("Runtime state cannot be saved outside the persisted run lease."), token);
        return state;
    }

    private static string[] OrderPathsForRecoverableEffects(
        IReadOnlyDictionary<string, PilotLocalEntry> local,
        IReadOnlyDictionary<string, PilotRemoteEntry> remote,
        IReadOnlyDictionary<string, SyncFileBaseline> baselines)
    {
        // A rename is represented as a create/verify effect at the new path followed by the
        // independently recoverable archive/delete effect at the old path. Ordering every new
        // path before every missing-sided baseline guarantees that sequence even when a test
        // provider cannot expose Dropbox's stable file ID across the rename.
        return local.Keys.Concat(remote.Keys).Concat(baselines.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => !baselines.ContainsKey(path)
                ? 0
                : !local.ContainsKey(path) || !remote.ContainsKey(path) ? 2 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, PilotRemoteEntry> BuildRemoteIndex(IEnumerable<PilotRemoteEntry> entries)
    {
        var result = new Dictionary<string, PilotRemoteEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!Enum.IsDefined(entry.Kind) || entry.Size < 0 ||
                entry.Kind == PilotEntryKind.File &&
                (string.IsNullOrWhiteSpace(entry.ContentHash) || string.IsNullOrWhiteSpace(entry.Revision) ||
                 string.IsNullOrWhiteSpace(entry.Id)))
                throw new PilotLimitException("Dropbox inventory contains an unsupported or incomplete entry.");
            var path = PilotPath.NormalizeRelative(entry.RelativePath);
            if (result.ContainsKey(path)) throw new PilotLimitException("Dropbox inventory contains a duplicate or case-colliding path.");
            result[path] = entry with { RelativePath = path };
        }
        return result;
    }

    private static SyncFileBaseline Capture(string path, PilotLocalEntry? local, PilotRemoteEntry? remote) =>
        new(path, local?.ContentHash, local?.Size, remote?.ContentHash, remote?.Size, remote?.Id, remote?.Revision,
            local?.VolumeSerialNumber, local?.FileId, local?.LastWriteUtc);

    private static bool MatchesLocal(SyncFileBaseline baseline, PilotLocalEntry? current)
    {
        if (baseline.LocalContentHash is null) return current is null;
        return current is not null && baseline.LocalVolumeSerialNumber is not null && baseline.LocalFileId is not null &&
               baseline.LocalLastWriteUtc is not null &&
               string.Equals(current.ContentHash, baseline.LocalContentHash, StringComparison.OrdinalIgnoreCase) &&
               current.Size == baseline.LocalSize && current.VolumeSerialNumber == baseline.LocalVolumeSerialNumber &&
               current.FileId == baseline.LocalFileId && current.LastWriteUtc == baseline.LocalLastWriteUtc;
    }

    private static bool MatchesRemote(SyncFileBaseline baseline, PilotRemoteEntry? current)
    {
        if (baseline.RemoteContentHash is null) return current is null;
        return current is not null && baseline.RemoteId is not null && baseline.RemoteRevision is not null &&
               string.Equals(current.ContentHash, baseline.RemoteContentHash, StringComparison.OrdinalIgnoreCase) &&
               current.Size == baseline.RemoteSize && string.Equals(current.Id, baseline.RemoteId, StringComparison.Ordinal) &&
               string.Equals(current.Revision, baseline.RemoteRevision, StringComparison.Ordinal);
    }

    private static bool ObservationsMatch(IReadOnlyList<SyncFileBaseline> expected,
        IReadOnlyList<SyncFileBaseline> current)
    {
        if (expected.Count != current.Count) return false;
        for (var index = 0; index < expected.Count; index++)
        {
            var left = expected[index];
            var right = current[index];
            if (!string.Equals(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(left.LocalContentHash, right.LocalContentHash, StringComparison.OrdinalIgnoreCase) ||
                left.LocalSize != right.LocalSize || left.LocalVolumeSerialNumber != right.LocalVolumeSerialNumber ||
                left.LocalFileId != right.LocalFileId || left.LocalLastWriteUtc != right.LocalLastWriteUtc ||
                !string.Equals(left.RemoteContentHash, right.RemoteContentHash, StringComparison.OrdinalIgnoreCase) ||
                left.RemoteSize != right.RemoteSize || !string.Equals(left.RemoteId, right.RemoteId, StringComparison.Ordinal) ||
                !string.Equals(left.RemoteRevision, right.RemoteRevision, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool SameLocalBytesDifferentIdentity(SyncFileBaseline baseline, PilotLocalEntry? current) =>
        baseline.LocalContentHash is not null && current is not null &&
        string.Equals(current.ContentHash, baseline.LocalContentHash, StringComparison.OrdinalIgnoreCase) &&
        current.Size == baseline.LocalSize &&
        (baseline.LocalVolumeSerialNumber is null || baseline.LocalFileId is null || baseline.LocalLastWriteUtc is null ||
         current.VolumeSerialNumber != baseline.LocalVolumeSerialNumber || current.FileId != baseline.LocalFileId ||
         current.LastWriteUtc != baseline.LocalLastWriteUtc);

    private static bool SameRemoteBytesDifferentIdentity(SyncFileBaseline baseline, PilotRemoteEntry? current) =>
        baseline.RemoteContentHash is not null && current is not null &&
        string.Equals(current.ContentHash, baseline.RemoteContentHash, StringComparison.OrdinalIgnoreCase) &&
        current.Size == baseline.RemoteSize &&
        (baseline.RemoteId is null || baseline.RemoteRevision is null ||
         !string.Equals(current.Id, baseline.RemoteId, StringComparison.Ordinal) ||
         !string.Equals(current.Revision, baseline.RemoteRevision, StringComparison.Ordinal));

    private static bool MatchesExpected(string? current, string? expected) =>
        current is null && expected is null || current is not null && expected is not null &&
        string.Equals(current, expected, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesExpectedRemote(PilotRemoteEntry? current, string? expectedHash, string? expectedRevision,
        string? expectedId = null)
    {
        if (expectedHash is null && expectedRevision is null) return current is null;
        return current is not null && string.Equals(current.ContentHash, expectedHash, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal) &&
               (expectedId is null || string.Equals(current.Id, expectedId, StringComparison.Ordinal));
    }

    private static bool MatchesLocalIntentSource(PilotLocalEntry? current, SyncTransferIntent pending) =>
        current is not null && pending.ExpectedSourceVolumeSerialNumber is not null &&
        pending.ExpectedSourceFileId is not null && pending.ExpectedSourceLastWriteUtc is not null &&
        current.VolumeSerialNumber == pending.ExpectedSourceVolumeSerialNumber &&
        current.FileId == pending.ExpectedSourceFileId && current.LastWriteUtc == pending.ExpectedSourceLastWriteUtc &&
        current.Size == pending.ExpectedSourceSize &&
        string.Equals(current.ContentHash, pending.SourceContentHash, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(SyncLocalRecovery.FormatSourceIdentity(current), pending.ExpectedSourceId,
            StringComparison.OrdinalIgnoreCase);

    private static bool MatchesRemoteSource(PilotRemoteEntry? current, SyncTransferIntent pending) =>
        current is not null && string.Equals(current.Id, pending.ExpectedSourceId, StringComparison.Ordinal) &&
        string.Equals(current.ContentHash, pending.SourceContentHash, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(current.Revision, pending.ExpectedSourceRevision, StringComparison.Ordinal) &&
        current.Size == pending.ExpectedSourceSize;

    private static bool MatchesRemoteIntentSource(PilotRemoteEntry? current, SyncTransferIntent pending) =>
        pending.Kind == SyncIntentKind.ResolveKeepLocal
            ? MatchesRemoteReplacementPredecessor(current, pending)
            : current is not null && string.Equals(current.Id, pending.ExpectedSourceId, StringComparison.Ordinal) &&
              string.Equals(current.Revision, pending.ExpectedSourceRevision, StringComparison.Ordinal) &&
              current.Size == pending.ExpectedSourceSize &&
              string.Equals(current.ContentHash, ExpectedRecoveryHash(pending), StringComparison.OrdinalIgnoreCase);

    private static bool IsExpectedReplacementSuccessor(PilotRemoteEntry? current, SyncTransferIntent pending)
    {
        var predecessorId = pending.Direction == SyncDirection.Upload
            ? pending.ExpectedDestinationRemoteId
            : pending.ExpectedSourceId;
        if (current is null || !string.Equals(current.Id, pending.SuccessorFileId ?? predecessorId,
                StringComparison.Ordinal) ||
            !string.Equals(current.ContentHash, pending.SuccessorContentHash ?? pending.SourceContentHash,
                StringComparison.OrdinalIgnoreCase) || current.Size != (pending.SuccessorSize ?? pending.ExpectedSourceSize))
            return false;
        if (pending.SuccessorRevision is not null)
            return string.Equals(current.Revision, pending.SuccessorRevision, StringComparison.Ordinal);
        var predecessorRevision = pending.Direction == SyncDirection.Upload
            ? pending.ExpectedDestinationRevision
            : pending.ExpectedSourceRevision;
        return !string.Equals(current.Revision, predecessorRevision, StringComparison.Ordinal);
    }

    private static bool MatchesRemoteReplacementPredecessor(PilotRemoteEntry? current, SyncTransferIntent pending) =>
        current is not null && string.Equals(current.Id, pending.ExpectedDestinationRemoteId, StringComparison.Ordinal) &&
        string.Equals(current.Revision, pending.ExpectedDestinationRevision, StringComparison.Ordinal) &&
        string.Equals(current.ContentHash, pending.ExpectedDestinationContentHash, StringComparison.OrdinalIgnoreCase) &&
        current.Size == pending.ExpectedDestinationSize;

    private static bool SameBytes(PilotLocalEntry local, PilotRemoteEntry remote) =>
        local.Size == remote.Size && string.Equals(local.ContentHash, remote.ContentHash, StringComparison.OrdinalIgnoreCase);

    private static bool HasHash(PilotLocalEntry? entry, string expected) =>
        entry is not null && string.Equals(entry.ContentHash, expected, StringComparison.OrdinalIgnoreCase);

    private static bool HasHash(PilotRemoteEntry? entry, string expected) =>
        entry is not null && string.Equals(entry.ContentHash, expected, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesArtifactIdentity(PilotLocalEntry? entry, string? artifactId) =>
        entry?.VolumeSerialNumber is not null && entry.FileId is not null && artifactId is not null &&
        string.Equals($"{entry.VolumeSerialNumber.Value:x8}:{entry.FileId.Value:x16}", artifactId,
            StringComparison.OrdinalIgnoreCase);

    private static string SafeMessage(Exception exception) => exception is DropboxApiException api ? api.SafeReason : exception.Message;
}
