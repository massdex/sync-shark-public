namespace Massdex.SyncShark;

public enum SyncLocalPendingDisposition
{
    Committed,
    NotApplied,
    Blocked
}

public sealed record SyncLocalPendingRecovery(SyncLocalPendingDisposition Disposition, PilotLocalEntry? LocalEntry,
    string Reason, SyncTransferIntent Intent);

public sealed record SyncLocalDownloadOutcome(SyncDownloadResult Result, SyncTransferIntent Intent);

public static class SyncLocalTransfer
{
    internal const string ArtifactPrefix = ".syncshark-transfer-";
    internal static Action<string>? StageWriteIntervalForTest { get; set; }
    internal static Action<string>? BackupWriteIntervalForTest { get; set; }
    internal static Action<string>? CommittedBeforeReturnForTest { get; set; }
    internal static Action<string>? StageCreatedForTest { get; set; }
    internal static Action<string>? DownloadVerifiedForTest { get; set; }
    internal static Action<string>? ArtifactsBoundForTest { get; set; }
    internal static Action<string>? PredecessorDeletedForTest { get; set; }
    internal static Action<string>? TargetPromotedForTest { get; set; }
    internal static Action<string>? BeforeTargetCommitForTest { get; set; }

    public static bool IsTransferArtifactName(string name)
    {
        var artifactName = name.EndsWith(SyncLocalFileAuthority.AccessRecoverySuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^SyncLocalFileAuthority.AccessRecoverySuffix.Length]
            : name;
        return artifactName.StartsWith(ArtifactPrefix, StringComparison.OrdinalIgnoreCase) &&
               (artifactName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
                artifactName.EndsWith(".backup", StringComparison.OrdinalIgnoreCase));
    }

    public static (string StageFileName, string? BackupFileName) CreateArtifactNames(bool replacing)
    {
        return (SyncLocalTransactionDirectory.StageName,
            replacing ? SyncLocalTransactionDirectory.BackupName : null);
    }

    internal static IReadOnlyList<string> RestorePreboundWritableObjectAccess(PilotDirectoryLease rootLease,
        PilotDirectoryLease controlRoot, SyncTransferIntent intent)
    {
        if (intent.Direction != SyncDirection.Download ||
            intent.Kind is not (SyncIntentKind.Transfer or SyncIntentKind.ResolveKeepDropbox) ||
            !string.Equals(intent.StageFileName, SyncLocalTransactionDirectory.StageName, StringComparison.Ordinal))
            throw new IOException(SyncCapabilityGate.ManualReviewReason("Private transaction recovery"));
        rootLease.VerifyBinding();
        if (intent.StageFileName is null)
        {
            if (intent.BackupFileName is not null)
                throw new InvalidDataException("A pending transfer backup has no prebound stage artifact.");
            return [];
        }

        var transactionPath = SyncLocalTransactionDirectory.PathFor(controlRoot, intent);
        if (intent.Phase == SyncIntentPhase.CommittedCleanupPending &&
            !Directory.Exists(transactionPath) && !File.Exists(transactionPath))
            return [transactionPath];
        using var transactionLease = SyncLocalTransactionDirectory.Open(controlRoot, intent);
        var stagePath = ArtifactPath(transactionLease, intent.StageFileName, SyncLocalTransactionDirectory.StageName);
        var backupPath = intent.BackupFileName is null
            ? null
            : ArtifactPath(transactionLease, intent.BackupFileName, SyncLocalTransactionDirectory.BackupName);
        var targetPath = PilotPath.ResolveContainedPath(rootLease.Path, intent.RelativePath);
        // An unbound writable object is still proved by its DPAPI recovery envelope. Leave that
        // envelope intact until BindPreparedArtifactsForRecoveryAsync can durably bind the exact
        // object identity, including an incomplete object that must only be cleaned and retried.
        if (intent.ArtifactsBound)
        {
            SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(stagePath, stagePath, targetPath);
            if (backupPath is not null)
                SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(backupPath, backupPath, targetPath);
        }
        rootLease.VerifyBinding();

        var paths = new List<string>
        {
            transactionLease.Path,
            stagePath,
            SyncLocalFileAuthority.RecoverySidecarPath(stagePath)
        };
        if (backupPath is not null)
        {
            paths.Add(backupPath);
            paths.Add(SyncLocalFileAuthority.RecoverySidecarPath(backupPath));
        }
        return paths;
    }

    public static async Task<SyncLocalDownloadOutcome> DownloadAsync(
        PilotRemoteEntry remote,
        PilotLocalEntry? expectedLocal,
        PilotDirectoryLease rootLease,
        PilotDirectoryLease controlRoot,
        ISyncRemoteStore remoteStore,
        SyncTransferIntent intent,
        Func<SyncTransferIntent, CancellationToken, Task> persistBoundIntent,
        CancellationToken cancellationToken)
    {
        if (!SyncCapabilityGate.ExistingFileReplacement)
            throw new IOException(SyncCapabilityGate.ManualReviewReason("Private transaction download"));
        rootLease.VerifyBinding();
        using var transactionLease = SyncLocalTransactionDirectory.Open(controlRoot, intent);
        var relativePath = PilotPath.NormalizeRelative(remote.RelativePath);
        var targetPath = PilotPath.ResolveContainedPath(rootLease.Path, relativePath);
        var stagePath = ArtifactPath(transactionLease, intent.StageFileName, SyncLocalTransactionDirectory.StageName);
        var backupPath = intent.BackupFileName is null ? null :
            ArtifactPath(transactionLease, intent.BackupFileName, SyncLocalTransactionDirectory.BackupName);
        if (File.Exists(stagePath) || File.Exists(SyncLocalFileAuthority.RecoverySidecarPath(stagePath)) ||
            backupPath is not null && (File.Exists(backupPath) ||
                                        File.Exists(SyncLocalFileAuthority.RecoverySidecarPath(backupPath))))
            throw new IOException("A transfer artifact already exists for the new download intent.");

        await using var stage = SyncLocalFileAuthority.CreateNewFile(stagePath);
        stage.VerifySingleLink();
        StageCreatedForTest?.Invoke(stagePath);
        if (!string.Equals(stage.FinalPath, stagePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The download staging object was not created in the authoritative local root.");
        rootLease.VerifyBinding();
        var stageWriter = stage.CreateGuardedWriteStream(StageWriteIntervalForTest);
        SyncDownloadResult downloaded;
        try
        {
            downloaded = await remoteStore.DownloadAsync(remote, stageWriter, cancellationToken);
        }
        catch
        {
            // The exact still-open object is product-created and has not yet crossed the durable artifact-binding
            // boundary. Remove it by handle so restart sees a legitimate no-artifact NotApplied state.
            stage.DeleteOnClose();
            throw;
        }
        await stage.Stream.FlushAsync(cancellationToken);
        stage.Stream.Flush(true);
        await VerifyWritablePayloadAsync(stage, stagePath, downloaded.Size, downloaded.ContentHash, rootLease,
            cancellationToken);
        DownloadVerifiedForTest?.Invoke(stagePath);

        rootLease.VerifyBinding();
        if (expectedLocal is null)
        {
            BeforeTargetCommitForTest?.Invoke(targetPath);
            if (File.Exists(targetPath)) throw new IOException("The local destination appeared after inventory; it was not overwritten.");
            intent = intent with
            {
                StageArtifactId = FormatIdentity(stage.VerifySingleLink()),
                BackupArtifactId = null,
                ArtifactsBound = true
            };
            await persistBoundIntent(intent, cancellationToken);
            ArtifactsBoundForTest?.Invoke(stagePath);
            stage.RenameTo(rootLease, relativePath);
            await VerifyCommittedTargetAsync(stage, targetPath, relativePath, downloaded, rootLease, cancellationToken);
            TargetPromotedForTest?.Invoke(targetPath);
            CommittedBeforeReturnForTest?.Invoke(targetPath);
            return new SyncLocalDownloadOutcome(downloaded, intent);
        }
        if (backupPath is null) throw new InvalidDataException("A replacement download has no recovery backup name.");
        await using var backup = SyncLocalFileAuthority.CreateNewFile(backupPath);
        backup.VerifySingleLink();
        await using (var held = await OpenExpectedForReplacementAsync(expectedLocal, rootLease, cancellationToken))
        {
            held.Stream.Position = 0;
            var backupWriter = backup.CreateGuardedWriteStream(BackupWriteIntervalForTest);
            await held.Stream.CopyToAsync(backupWriter, cancellationToken);
            await backup.Stream.FlushAsync(cancellationToken);
            backup.Stream.Flush(true);
            await VerifyHeldBackupAsync(backup, expectedLocal, backupPath, rootLease, cancellationToken);
            intent = intent with
            {
                StageArtifactId = FormatIdentity(stage.VerifySingleLink()),
                BackupArtifactId = FormatIdentity(backup.VerifySingleLink()),
                ArtifactsBound = true
            };
            await persistBoundIntent(intent, cancellationToken);
            ArtifactsBoundForTest?.Invoke(stagePath);
            held.DeleteOnClose();
            PredecessorDeletedForTest?.Invoke(targetPath);
        }
        stage.VerifySingleLink();
        if (File.Exists(targetPath))
            throw new IOException("The exact prior local object was not released after its recovery copy was proven.");
        stage.RenameTo(rootLease, relativePath);
        await VerifyCommittedTargetAsync(stage, targetPath, relativePath, downloaded, rootLease, cancellationToken);
        TargetPromotedForTest?.Invoke(targetPath);
        CommittedBeforeReturnForTest?.Invoke(targetPath);
        return new SyncLocalDownloadOutcome(downloaded, intent);
    }

    internal static Task<SyncLocalDownloadOutcome> DownloadAsync(PilotRemoteEntry remote,
        PilotLocalEntry? expectedLocal, PilotDirectoryLease rootLease, ISyncRemoteStore remoteStore,
        SyncTransferIntent intent, Func<SyncTransferIntent, CancellationToken, Task> persistBoundIntent,
        CancellationToken cancellationToken) => DownloadAsync(remote, expectedLocal, rootLease, rootLease,
        remoteStore, intent, persistBoundIntent, cancellationToken);

    public static async Task<SyncLocalPendingRecovery> RecoverInterruptedDownloadAsync(
        PilotRemoteEntry remote,
        PilotDirectoryLease rootLease,
        PilotDirectoryLease controlRoot,
        SyncTransferIntent intent,
        Func<SyncTransferIntent, CancellationToken, Task> persistBoundIntent,
        CancellationToken cancellationToken)
    {
        if (!SyncCapabilityGate.ExistingFileReplacement)
            throw new IOException(SyncCapabilityGate.ManualReviewReason("Private transaction recovery"));
        rootLease.VerifyBinding();
        var rebound = await BindPreparedArtifactsForRecoveryAsync(rootLease, controlRoot, intent, cancellationToken);
        if (rebound != intent)
        {
            await persistBoundIntent(rebound, cancellationToken);
            intent = rebound;
        }
        using var transactionLease = SyncLocalTransactionDirectory.Open(controlRoot, intent);
        var relativePath = PilotPath.NormalizeRelative(intent.RelativePath);
        var targetPath = PilotPath.ResolveContainedPath(rootLease.Path, relativePath);
        var stagePath = ArtifactPath(transactionLease, intent.StageFileName, SyncLocalTransactionDirectory.StageName);
        var backupPath = intent.BackupFileName is null ? null :
            ArtifactPath(transactionLease, intent.BackupFileName, SyncLocalTransactionDirectory.BackupName);

        SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(stagePath, stagePath, targetPath);
        if (backupPath is not null)
            SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(backupPath, backupPath, targetPath);

        if (File.Exists(targetPath))
        {
            var target = await ReadEntryAsync(targetPath, relativePath, rootLease, cancellationToken);
            if (string.Equals(target.ContentHash, intent.SourceContentHash, StringComparison.OrdinalIgnoreCase))
            {
                if (target.VolumeSerialNumber is null || target.FileId is null || intent.StageArtifactId is null ||
                    !string.Equals(FormatIdentity(new PilotDirectoryIdentity(target.VolumeSerialNumber.Value,
                        target.FileId.Value)), intent.StageArtifactId, StringComparison.OrdinalIgnoreCase))
                    return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Blocked, target,
                        "The destination bytes match the download, but its file identity is not the exact durable stage identity.", intent);
                return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Committed, target,
                    "The exact local destination contains the downloaded source bytes.", intent);
            }
            if (MatchesExpected(target.ContentHash, intent.ExpectedDestinationContentHash))
            {
                return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.NotApplied, target,
                    "The exact pre-transfer local destination remains unchanged.", intent);
            }
            return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Blocked, target,
                "The local destination contains neither the downloaded source nor the exact pre-transfer bytes.", intent);
        }

        if (File.Exists(stagePath))
        {
            await using var stage = SyncLocalFileAuthority.OpenExistingExclusive(stagePath);
            stage.VerifySingleLink();
            var stageHash = await HashExactAsync(stage, stagePath, rootLease, cancellationToken);
            if (stage.Stream.Length == remote.Size &&
                string.Equals(stageHash, intent.SourceContentHash, StringComparison.OrdinalIgnoreCase))
            {
                stage.VerifySingleLink();
                stage.RenameTo(rootLease, relativePath);
                var committed = await EntryFromAuthorityAsync(stage, relativePath, targetPath, rootLease, cancellationToken);
                return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Committed, committed,
                    "A complete held staging object was committed to the missing destination without repeating the download.", intent);
            }
            if (backupPath is null)
                return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.NotApplied, null,
                    "The exact bound staging object contains an interrupted download and is retained only for owned cleanup before retry.", intent);
        }

        if (backupPath is not null && File.Exists(backupPath))
        {
            await using var backup = SyncLocalFileAuthority.OpenExistingExclusive(backupPath);
            backup.VerifySingleLink();
            var backupHash = await HashExactAsync(backup, backupPath, rootLease, cancellationToken);
            if (!MatchesExpected(backupHash, intent.ExpectedDestinationContentHash))
                return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Blocked, null,
                    "The held replacement backup no longer matches the exact pre-transfer bytes.", intent);
            backup.RenameTo(rootLease, relativePath);
            var restored = await EntryFromAuthorityAsync(backup, relativePath, targetPath, rootLease, cancellationToken);
            return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.NotApplied, restored,
                "The exact pre-transfer local object was restored from its held backup before retry.", intent);
        }

        if (intent.ExpectedDestinationContentHash is null || intent.Kind == SyncIntentKind.ResolveKeepDropbox)
            return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.NotApplied, null,
                "No committed local destination or complete staging object exists; the download effect was not applied.", intent);
        return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Blocked, null,
            "The prior local destination and its held recovery backup are both absent.", intent);
    }

    internal static Task<SyncLocalPendingRecovery> RecoverInterruptedDownloadAsync(
        PilotRemoteEntry remote, PilotDirectoryLease rootLease, PilotDirectoryLease controlRoot,
        SyncTransferIntent intent,
        CancellationToken cancellationToken) => RecoverInterruptedDownloadAsync(remote, rootLease, controlRoot, intent,
        static (_, _) => Task.CompletedTask, cancellationToken);

    internal static Task<SyncLocalPendingRecovery> RecoverInterruptedDownloadAsync(
        PilotRemoteEntry remote, PilotDirectoryLease rootLease, SyncTransferIntent intent,
        CancellationToken cancellationToken) => RecoverInterruptedDownloadAsync(remote, rootLease, rootLease, intent,
        static (_, _) => Task.CompletedTask, cancellationToken);

    internal static Task<SyncLocalPendingRecovery> RecoverInterruptedDownloadAsync(
        PilotRemoteEntry remote, PilotDirectoryLease rootLease, SyncTransferIntent intent,
        Func<SyncTransferIntent, CancellationToken, Task> persistBoundIntent,
        CancellationToken cancellationToken) => RecoverInterruptedDownloadAsync(remote, rootLease, rootLease, intent,
        persistBoundIntent, cancellationToken);

    public static async Task<SyncTransferIntent> BindPreparedArtifactsForRecoveryAsync(
        PilotDirectoryLease rootLease, PilotDirectoryLease controlRoot, SyncTransferIntent intent,
        CancellationToken cancellationToken)
    {
        if (!SyncCapabilityGate.ExistingFileReplacement)
            throw new IOException(SyncCapabilityGate.ManualReviewReason("Private transaction artifact binding"));
        using var transactionLease = SyncLocalTransactionDirectory.Open(controlRoot, intent);
        var stagePath = ArtifactPath(transactionLease, intent.StageFileName, SyncLocalTransactionDirectory.StageName);
        var backupPath = intent.BackupFileName is null ? null :
            ArtifactPath(transactionLease, intent.BackupFileName, SyncLocalTransactionDirectory.BackupName);
        var targetPath = PilotPath.ResolveContainedPath(rootLease.Path, intent.RelativePath);

        var recoveredStageIdentity = SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(stagePath,
            stagePath, targetPath);
        var recoveredBackupIdentity = backupPath is null
            ? null
            : SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(backupPath, backupPath, targetPath);
        VerifyNoUnexpectedTransactionMembers(transactionLease.Path, stagePath, backupPath);

        var stageIdentity = await ValidateArtifactForBindingAsync(transactionLease, stagePath,
            intent.SourceContentHash, intent.StageArtifactId, recoveredStageIdentity, cancellationToken);
        var backupIdentity = backupPath is null ? null : await ValidateArtifactForBindingAsync(transactionLease,
            backupPath, intent.ExpectedDestinationContentHash, intent.BackupArtifactId, recoveredBackupIdentity,
            cancellationToken);

        if (intent.ArtifactsBound)
            return intent;
        if (stageIdentity is null && backupIdentity is null)
            return intent;
        return intent with
        {
            StageArtifactId = stageIdentity,
            BackupArtifactId = backupIdentity,
            ArtifactsBound = true
        };
    }

    internal static Task<SyncTransferIntent> BindPreparedArtifactsForRecoveryAsync(PilotDirectoryLease rootLease,
        SyncTransferIntent intent, CancellationToken cancellationToken) =>
        BindPreparedArtifactsForRecoveryAsync(rootLease, rootLease, intent, cancellationToken);

    public static void FinalizeArtifacts(PilotDirectoryLease controlRoot, SyncTransferIntent intent)
    {
        if (!SyncCapabilityGate.ExistingFileReplacement)
            throw new IOException(SyncCapabilityGate.ManualReviewReason("Private transaction cleanup"));
        var transactionPath = SyncLocalTransactionDirectory.PathFor(controlRoot, intent);
        controlRoot.VerifyBinding();
        if (!Directory.Exists(transactionPath) && !File.Exists(transactionPath)) return;
        if (intent.ArtifactsBound)
        {
            using var transactionLease = SyncLocalTransactionDirectory.Open(controlRoot, intent);
            DeleteOwnedArtifact(transactionLease, intent.StageFileName, intent.StageArtifactId,
                SyncLocalTransactionDirectory.StageName);
            DeleteOwnedArtifact(transactionLease, intent.BackupFileName, intent.BackupArtifactId,
                SyncLocalTransactionDirectory.BackupName);
        }
        VerifyArtifactsAbsent(controlRoot, intent);
        SyncLocalTransactionDirectory.DeleteIfEmpty(controlRoot, intent);
    }

    public static void VerifyArtifactsAbsent(PilotDirectoryLease controlRoot, SyncTransferIntent intent)
    {
        var transactionPath = SyncLocalTransactionDirectory.PathFor(controlRoot, intent);
        controlRoot.VerifyBinding();
        if (!Directory.Exists(transactionPath) && !File.Exists(transactionPath)) return;
        using var transactionLease = SyncLocalTransactionDirectory.Open(controlRoot, intent);
        VerifyOwnedArtifactAbsent(transactionLease, intent.StageFileName, SyncLocalTransactionDirectory.StageName);
        VerifyOwnedArtifactAbsent(transactionLease, intent.BackupFileName, SyncLocalTransactionDirectory.BackupName);
        VerifyNoUnexpectedTransactionMembers(transactionLease.Path,
            ArtifactPath(transactionLease, intent.StageFileName, SyncLocalTransactionDirectory.StageName),
            intent.BackupFileName is null ? null : ArtifactPath(transactionLease, intent.BackupFileName,
                SyncLocalTransactionDirectory.BackupName));
    }

    private static async Task<SyncLocalFileAuthority> OpenExpectedForReplacementAsync(PilotLocalEntry expected, PilotDirectoryLease rootLease,
        CancellationToken cancellationToken)
    {
        rootLease.VerifyBinding();
        var held = SyncLocalFileAuthority.OpenExistingExclusive(expected.FullPath);
        try
        {
            if (PilotPath.IsUnsafeAttributes(File.GetAttributes(expected.FullPath)) ||
                !string.Equals(held.FinalPath, expected.FullPath, StringComparison.OrdinalIgnoreCase) ||
                held.Stream.Length != expected.Size || File.GetLastWriteTimeUtc(expected.FullPath) != expected.LastWriteUtc)
                throw new IOException("The local destination changed after inventory.");
            var hash = await DropboxContentHash.ComputeAsync(held.Stream, cancellationToken);
            if (!string.Equals(hash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The local destination bytes changed after inventory.");
            held.Stream.Position = 0;
            return held;
        }
        catch
        {
            await held.DisposeAsync();
            throw;
        }
    }

    private static async Task VerifyHeldBackupAsync(SyncLocalFileAuthority held, PilotLocalEntry expected, string backupPath,
        PilotDirectoryLease rootLease, CancellationToken cancellationToken)
    {
        rootLease.VerifyBinding();
        held.VerifySingleLink();
        if (!string.Equals(held.FinalPath, backupPath, StringComparison.OrdinalIgnoreCase) ||
            held.Stream.Length != expected.Size)
            throw new IOException("The replaced local version was not retained in the exact recovery backup.");
        held.Stream.Position = 0;
        var hash = await DropboxContentHash.ComputeAsync(held.Stream, cancellationToken);
        if (!string.Equals(hash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The retained local recovery backup differs from inventory.");
        held.VerifySingleLink();
    }

    private static async Task VerifyCommittedTargetAsync(SyncLocalFileAuthority target, string targetPath, string relativePath, SyncDownloadResult downloaded,
        PilotDirectoryLease rootLease, CancellationToken cancellationToken)
    {
        rootLease.VerifyBinding();
        target.VerifySingleLink();
        if (!string.Equals(target.FinalPath, targetPath,
                StringComparison.OrdinalIgnoreCase) || target.Stream.Length != downloaded.Size)
            throw new IOException("The downloaded local target identity or length differs after commit.");
        target.Stream.Position = 0;
        var hash = await DropboxContentHash.ComputeAsync(target.Stream, cancellationToken);
        if (!string.Equals(hash, downloaded.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The downloaded local target bytes differ after commit.");
        target.VerifySingleLink();
        rootLease.VerifyBinding();
    }

    private static async Task VerifyWritablePayloadAsync(SyncLocalFileAuthority authority, string expectedPath,
        long expectedSize, string expectedHash, PilotDirectoryLease rootLease, CancellationToken cancellationToken)
    {
        rootLease.VerifyBinding();
        var identity = authority.VerifySingleLink();
        if (!string.Equals(authority.FinalPath, expectedPath, StringComparison.OrdinalIgnoreCase) ||
            authority.Stream.Length != expectedSize)
            throw new IOException("The held writable object does not match its prebound path and size.");
        authority.Stream.Position = 0;
        var hash = await DropboxContentHash.ComputeAsync(authority.Stream, cancellationToken);
        authority.Stream.Position = 0;
        if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The held writable object differs from the expected source bytes.");
        authority.VerifySameSingleLink(identity);
        rootLease.VerifyBinding();
    }

    private static string ArtifactPath(PilotDirectoryLease transactionLease, string? fileName, string expectedName)
    {
        if (fileName is null || !string.Equals(fileName, expectedName, StringComparison.Ordinal))
            throw new InvalidDataException("The transfer artifact name is not accepted.");
        return Path.Combine(transactionLease.Path, fileName);
    }

    private static void DeleteOwnedArtifact(PilotDirectoryLease transactionLease, string? fileName,
        string? expectedIdentity, string expectedName)
    {
        if (fileName is null) return;
        var path = ArtifactPath(transactionLease, fileName, expectedName);
        transactionLease.VerifyBinding();
        SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(path, path);
        if (!File.Exists(path)) return;
        if (expectedIdentity is null)
            throw new IOException("An unbound transfer artifact cannot be deleted.");
        if (PilotPath.IsUnsafeAttributes(File.GetAttributes(path)))
            throw new IOException("A transfer artifact became unsafe and was retained.");
        using var artifact = SyncLocalFileAuthority.OpenExistingExclusive(path);
        if (artifact.VerifySingleLink() != SyncLocalRecovery.ParseSourceIdentity(expectedIdentity))
            throw new IOException("A transfer artifact no longer matches its durable cleanup identity.");
        if (!string.Equals(artifact.FinalPath, path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A transfer artifact path no longer names the held product object.");
        artifact.DeleteOnClose();
        transactionLease.VerifyBinding();
    }

    private static string FormatIdentity(PilotDirectoryIdentity identity) =>
        $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}";

    private static void VerifyOwnedArtifactAbsent(PilotDirectoryLease transactionLease, string? fileName,
        string expectedName)
    {
        if (fileName is null) return;
        var path = ArtifactPath(transactionLease, fileName, expectedName);
        var sidecarPath = SyncLocalFileAuthority.RecoverySidecarPath(path);
        transactionLease.VerifyBinding();
        if (File.Exists(path) || Directory.Exists(path) || File.Exists(sidecarPath) || Directory.Exists(sidecarPath))
            throw new IOException("An exact owned transfer artifact remains after cleanup.");
        transactionLease.VerifyBinding();
    }

    private static async Task<string?> ValidateArtifactForBindingAsync(PilotDirectoryLease transactionLease,
        string path, string? expectedHash, string? persistedIdentity,
        PilotDirectoryIdentity? recoveredIdentity, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            if (recoveredIdentity is not null)
                throw new IOException("A recovered transaction identity has no corresponding local artifact.");
            return null;
        }
        if (expectedHash is null || PilotPath.IsUnsafeAttributes(File.GetAttributes(path)))
            throw new IOException("A transaction artifact cannot be safely bound.");
        await using var artifact = SyncLocalFileAuthority.OpenExistingExclusive(path);
        var identity = artifact.VerifySingleLink();
        if (!string.Equals(artifact.FinalPath, path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A transaction artifact escaped its private directory.");
        var formatted = FormatIdentity(identity);
        if (persistedIdentity is null && recoveredIdentity is null)
            throw new IOException("An unbound transaction artifact has no protected ownership proof and was retained.");
        if (recoveredIdentity is not null && identity != recoveredIdentity)
            throw new IOException("The recovered transaction artifact identity changed before durable binding.");
        if (persistedIdentity is not null && !string.Equals(formatted, persistedIdentity, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A transaction artifact no longer matches its durable identity.");
        // Hashing is deliberately still performed before binding so an unreadable artifact fails closed.
        // Its content need not yet be complete: a crash can interrupt the stage or backup write, and the
        // exact DPAPI-proved object must first be bound so cleanup can remove only that object on retry.
        _ = await HashExactAsync(artifact, path, transactionLease, cancellationToken);
        return formatted;
    }

    private static void VerifyNoUnexpectedTransactionMembers(string directoryPath, string stagePath, string? backupPath)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            stagePath,
            SyncLocalFileAuthority.RecoverySidecarPath(stagePath)
        };
        if (backupPath is not null)
        {
            allowed.Add(backupPath);
            allowed.Add(SyncLocalFileAuthority.RecoverySidecarPath(backupPath));
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath))
            if (!allowed.Contains(Path.GetFullPath(entry)))
                throw new IOException("The private transaction directory contains an unexpected object and was retained.");
    }

    private static async Task<PilotLocalEntry> ReadEntryAsync(string path, string relativePath, PilotDirectoryLease rootLease,
        CancellationToken cancellationToken)
    {
        await using var authority = SyncLocalFileAuthority.OpenExistingExclusive(path);
        return await EntryFromAuthorityAsync(authority, relativePath, path, rootLease, cancellationToken);
    }

    private static async Task<PilotLocalEntry> EntryFromAuthorityAsync(SyncLocalFileAuthority authority, string relativePath,
        string expectedPath, PilotDirectoryLease rootLease, CancellationToken cancellationToken)
    {
        var identity = authority.VerifySingleLink();
        var hash = await HashExactAsync(authority, expectedPath, rootLease, cancellationToken);
        authority.VerifySameSingleLink(identity);
        return new PilotLocalEntry(relativePath, PilotEntryKind.File, expectedPath, authority.Stream.Length,
            File.GetLastWriteTimeUtc(expectedPath), hash, identity.VolumeSerialNumber, identity.FileId);
    }

    private static async Task<string> HashExactAsync(SyncLocalFileAuthority authority, string expectedPath,
        PilotDirectoryLease rootLease, CancellationToken cancellationToken)
    {
        rootLease.VerifyBinding();
        if (!string.Equals(authority.FinalPath, expectedPath, StringComparison.OrdinalIgnoreCase) ||
            PilotPath.IsUnsafeAttributes(File.GetAttributes(expectedPath)))
            throw new IOException("The held local file object no longer matches the intended path.");
        authority.Stream.Position = 0;
        var hash = await DropboxContentHash.ComputeAsync(authority.Stream, cancellationToken);
        authority.Stream.Position = 0;
        rootLease.VerifyBinding();
        return hash;
    }

    private static bool MatchesExpected(string? current, string? expected) =>
        current is null && expected is null || current is not null && expected is not null &&
        string.Equals(current, expected, StringComparison.OrdinalIgnoreCase);
}
