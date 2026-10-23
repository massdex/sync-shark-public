using System.Security.AccessControl;
using System.Security.Principal;

namespace Massdex.SyncShark;

internal sealed record SyncReplacementOutcome(SyncDownloadResult Result, SyncTransferIntent Intent);

internal static class SyncPrivateReplacement
{
    internal const string StageSuffix = ".partial";
    internal const string RollbackSuffix = ".rollback";
    internal static Action<string>? StageWriteIntervalForTest { get; set; }
    internal static Action? StageCreatedForTest { get; set; }
    internal static Action? StageBoundForTest { get; set; }
    internal static Action? PreimageMovedForTest { get; set; }
    internal static Action? TargetPromotedForTest { get; set; }

    public static string RelativeStage(string intentId) => ValidateId(intentId) + StageSuffix;
    public static string RelativeRollback(string intentId) => ValidateId(intentId) + RollbackSuffix;

    public static SyncTransferIntent BindStagingRoot(SyncTransferIntent intent)
    {
        EnsureRoot();
        using var lease = PilotDirectoryLease.OpenProductDirectory(SyncSharkRuntimePaths.ReplacementStagingRoot);
        return intent with
        {
            StagingRootVolumeSerialNumber = lease.Identity.VolumeSerialNumber,
            StagingRootFileId = lease.Identity.FileId
        };
    }

    public static async Task<SyncReplacementOutcome> DownloadAsync(PilotRemoteEntry remote, PilotLocalEntry expected,
        PilotDirectoryLease localRoot, ISyncRemoteStore remoteStore, SyncTransferIntent intent,
        Func<SyncTransferIntent, CancellationToken, Task> persist, CancellationToken cancellationToken)
    {
        using var stagingRoot = OpenBoundRoot(intent);
        var stagePath = Path.Combine(SyncSharkRuntimePaths.ReplacementStagingRoot, RelativeStage(intent.Id));
        var rollbackPath = Path.Combine(SyncSharkRuntimePaths.ReplacementStagingRoot, RelativeRollback(intent.Id));
        var targetPath = PilotPath.ResolveContainedPath(localRoot.Path, intent.RelativePath);
        RequireSameVolume(stagePath, targetPath);
        if (File.Exists(stagePath) || File.Exists(rollbackPath))
            throw new IOException("The private replacement paths are not empty.");

        await using var stage = SyncLocalFileAuthority.CreateNewMovableFile(stagePath);
        StageCreatedForTest?.Invoke();
        SyncDownloadResult result;
        var stageBound = false;
        try
        {
            var writer = stage.CreateGuardedWriteStream(StageWriteIntervalForTest);
            result = await remoteStore.DownloadAsync(remote, writer, cancellationToken);
            await stage.Stream.FlushAsync(cancellationToken);
            stage.Stream.Flush(true);
            await VerifyBytesAsync(stage, stagePath, result.Size, result.ContentHash, cancellationToken);
            intent = intent with { StageArtifactId = Format(stage.VerifySingleLink()) };
            await persist(intent, cancellationToken);
            stageBound = true;
            StageBoundForTest?.Invoke();
        }
        catch
        {
            if (!stageBound) stage.DeleteOnClose();
            throw;
        }

        await using var target = SyncLocalFileAuthority.OpenExistingExclusive(targetPath);
        var preimage = target.VerifySingleLink();
        if (preimage.VolumeSerialNumber != intent.ExpectedDestinationVolumeSerialNumber ||
            preimage.FileId != intent.ExpectedDestinationFileId || target.Stream.Length != expected.Size)
            throw new IOException("The local replacement preimage identity changed.");
        target.Stream.Position = 0;
        var preimageHash = await DropboxContentHash.ComputeAsync(target.Stream, cancellationToken);
        if (!string.Equals(preimageHash, intent.ExpectedDestinationContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The local replacement preimage bytes changed.");
        target.RenameTo(stagingRoot, RelativeRollback(intent.Id));
        PreimageMovedForTest?.Invoke();
        if (!string.Equals(target.FinalPath, rollbackPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The held local preimage did not reach the exact rollback path.");
        stage.RenameTo(localRoot, intent.RelativePath);
        TargetPromotedForTest?.Invoke();
        await VerifyBytesAsync(stage, targetPath, result.Size, result.ContentHash, cancellationToken);
        var committedIdentity = stage.VerifySingleLink();
        if (Format(committedIdentity) != intent.StageArtifactId)
            throw new IOException("The committed target is not the exact durably bound staged identity.");
        var rollbackIdentity = target.VerifySingleLink();
        if (rollbackIdentity != preimage)
            throw new IOException("The retained replacement rollback is not the exact local preimage.");
        intent = intent with { BackupArtifactId = Format(rollbackIdentity), Phase = SyncIntentPhase.CommittedCleanupPending };
        await persist(intent, cancellationToken);
        return new SyncReplacementOutcome(result, intent);
    }

    public static void CleanupCommitted(SyncTransferIntent intent)
    {
        if (intent.Phase != SyncIntentPhase.CleanupCompleted || intent.BackupArtifactId is null)
            throw new InvalidDataException("Replacement cleanup is forbidden until CleanupCompleted is durable.");
        var rollbackPath = Path.Combine(SyncSharkRuntimePaths.ReplacementStagingRoot, RelativeRollback(intent.Id));
        if (!File.Exists(rollbackPath) && !Directory.Exists(rollbackPath)) return;
        if (Directory.Exists(rollbackPath))
            throw new IOException("The exact retained rollback path became a directory and was preserved.");
        using (var rollback = SyncLocalFileAuthority.OpenExistingExclusive(rollbackPath))
        {
            if (Format(rollback.VerifySingleLink()) != intent.BackupArtifactId)
                throw new IOException("The retained rollback identity changed and was preserved.");
            if (!string.Equals(rollback.FinalPath, rollbackPath, StringComparison.OrdinalIgnoreCase) ||
                PilotPath.IsUnsafeAttributes(File.GetAttributes(rollbackPath)))
                throw new IOException("The retained rollback path became unsafe and was preserved.");
            rollback.DeleteOnClose();
        }
        if (File.Exists(rollbackPath) || Directory.Exists(rollbackPath))
            throw new IOException("The exact retained rollback remains after cleanup.");
    }

    public static void VerifyCleanupCompleted(SyncTransferIntent intent)
    {
        var rollbackPath = Path.Combine(SyncSharkRuntimePaths.ReplacementStagingRoot, RelativeRollback(intent.Id));
        if (File.Exists(rollbackPath) || Directory.Exists(rollbackPath))
            throw new IOException("The retained rollback still exists after durable cleanup completion.");
    }

    public static async Task<SyncLocalFileAuthority> OpenVerifiedCommittedTargetAsync(PilotDirectoryLease localRoot, SyncTransferIntent intent,
        CancellationToken cancellationToken)
    {
        var targetPath = PilotPath.ResolveContainedPath(localRoot.Path, intent.RelativePath);
        var target = await ReadEntryAsync(targetPath, intent.RelativePath, cancellationToken) ??
                     throw new IOException("The committed replacement target is absent.");
        if (!string.Equals(target.ContentHash, intent.SourceContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The committed replacement target bytes changed before cleanup.");
        var held = SyncLocalFileAuthority.OpenExistingExclusive(targetPath);
        try
        {
            if (intent.StageArtifactId is null || Format(held.VerifySingleLink()) != intent.StageArtifactId ||
                !string.Equals(held.FinalPath, targetPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The committed replacement target is not the exact staged identity.");
            return held;
        }
        catch
        {
            await held.DisposeAsync();
            throw;
        }
    }

    public static async Task<SyncLocalPendingRecovery> RecoverAsync(PilotRemoteEntry remote,
        PilotDirectoryLease localRoot, SyncTransferIntent intent,
        Func<SyncTransferIntent, CancellationToken, Task> persist, CancellationToken cancellationToken)
    {
        if (!SyncCapabilityGate.IsPrivateReplacementIntent(intent))
            throw new InvalidDataException("The pending operation is not a private replacement intent.");
        using var stagingRoot = OpenBoundRoot(intent);
        var stagePath = Path.Combine(SyncSharkRuntimePaths.ReplacementStagingRoot, RelativeStage(intent.Id));
        var rollbackPath = Path.Combine(SyncSharkRuntimePaths.ReplacementStagingRoot, RelativeRollback(intent.Id));
        var targetPath = PilotPath.ResolveContainedPath(localRoot.Path, intent.RelativePath);
        var restoredStageIdentity = SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(stagePath, stagePath, targetPath);
        var target = await ReadEntryAsync(targetPath, intent.RelativePath, cancellationToken);

        if (target is not null && string.Equals(target.ContentHash, intent.SourceContentHash,
                StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(rollbackPath) || Directory.Exists(rollbackPath))
                return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Blocked, target,
                    "The replacement target committed but its exact retained predecessor path is absent.", intent);
            await using var rollback = SyncLocalFileAuthority.OpenExistingExclusive(rollbackPath);
            var identity = rollback.VerifySingleLink();
            if (identity.VolumeSerialNumber != intent.ExpectedDestinationVolumeSerialNumber ||
                identity.FileId != intent.ExpectedDestinationFileId)
                return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Blocked, target,
                    "The retained predecessor does not match the durable local preimage identity.", intent);
            var bound = intent with { Phase = SyncIntentPhase.CommittedCleanupPending, BackupArtifactId = Format(identity) };
            await persist(bound, cancellationToken);
            return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Committed, target,
                "The atomic replacement and exact retained predecessor were reconciled.", bound);
        }

        var targetIsPreimage = target is not null && target.VolumeSerialNumber == intent.ExpectedDestinationVolumeSerialNumber &&
                               target.FileId == intent.ExpectedDestinationFileId &&
                               string.Equals(target.ContentHash, intent.ExpectedDestinationContentHash,
                                   StringComparison.OrdinalIgnoreCase);
        var rollbackIsPreimage = await ReadEntryAsync(rollbackPath, intent.RelativePath, cancellationToken);
        if (target is null && rollbackIsPreimage is not null &&
            rollbackIsPreimage.VolumeSerialNumber == intent.ExpectedDestinationVolumeSerialNumber &&
            rollbackIsPreimage.FileId == intent.ExpectedDestinationFileId &&
            string.Equals(rollbackIsPreimage.ContentHash, intent.ExpectedDestinationContentHash,
                StringComparison.OrdinalIgnoreCase) && intent.StageArtifactId is not null && File.Exists(stagePath))
        {
            await using var heldStage = SyncLocalFileAuthority.OpenExistingExclusive(stagePath);
            if (Format(heldStage.VerifySingleLink()) != intent.StageArtifactId)
                return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Blocked, null,
                    "The staged pathname no longer names the durable staged identity.", intent);
            await VerifyBytesAsync(heldStage, stagePath, remote.Size, intent.SourceContentHash, cancellationToken);
            heldStage.RenameTo(localRoot, intent.RelativePath);
            return await RecoverAsync(remote, localRoot, intent, persist, cancellationToken);
        }
        if (!targetIsPreimage)
            return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Blocked, target,
                "The local target matches neither the downloaded source nor the durable preimage identity.", intent);
        if (intent.StageArtifactId is null)
        {
            if (File.Exists(stagePath) || Directory.Exists(stagePath))
            {
                if (restoredStageIdentity is null)
                    return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Blocked, target,
                        "An unbound private staged pathname exists without its protected owner evidence and was not adopted.", intent);
                await using var restoredStage = SyncLocalFileAuthority.OpenExistingExclusive(stagePath);
                if (restoredStage.VerifySingleLink() != restoredStageIdentity)
                    return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Blocked, target,
                        "The recovered private stage changed identity and was not adopted.", intent);
                restoredStage.DeleteOnClose();
                return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.NotApplied, target,
                    "The exact product-owned but unbound private stage was removed; no replacement was applied.", intent);
            }
            return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.NotApplied, target,
                "No staged object was durably bound; the replacement was not applied.", intent);
        }
        if (!File.Exists(stagePath) || Directory.Exists(stagePath))
            return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Blocked, target,
                "The durably bound staged identity is absent and was not inferred from another pathname.", intent);
        await using (var stage = SyncLocalFileAuthority.OpenExistingExclusive(stagePath))
        {
            if (Format(stage.VerifySingleLink()) != intent.StageArtifactId)
                return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Blocked, target,
                    "The staged pathname no longer names the durable staged identity.", intent);
            await VerifyBytesAsync(stage, stagePath, remote.Size, intent.SourceContentHash, cancellationToken);
        }
        await using (var heldTarget = SyncLocalFileAuthority.OpenExistingExclusive(targetPath))
        await using (var heldStage = SyncLocalFileAuthority.OpenExistingExclusive(stagePath))
        {
            if (Format(heldStage.VerifySingleLink()) != intent.StageArtifactId)
                return new SyncLocalPendingRecovery(SyncLocalPendingDisposition.Blocked, target,
                    "The staged pathname no longer names the durable staged identity.", intent);
            heldTarget.RenameTo(stagingRoot, RelativeRollback(intent.Id));
            PreimageMovedForTest?.Invoke();
            heldStage.RenameTo(localRoot, intent.RelativePath);
        }
        return await RecoverAsync(remote, localRoot, intent, persist, cancellationToken);
    }

    private static async Task<PilotLocalEntry?> ReadEntryAsync(string path, string relativePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || Directory.Exists(path)) return null;
        await using var file = SyncLocalFileAuthority.OpenExistingExclusive(path);
        if (!string.Equals(file.FinalPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ||
            PilotPath.IsUnsafeAttributes(File.GetAttributes(path)))
            throw new IOException("The replacement path is not an exact regular file.");
        var identity = file.VerifySingleLink();
        file.Stream.Position = 0;
        var hash = await DropboxContentHash.ComputeAsync(file.Stream, cancellationToken);
        return new PilotLocalEntry(relativePath, PilotEntryKind.File, path, file.Stream.Length,
            File.GetLastWriteTimeUtc(path), hash, identity.VolumeSerialNumber, identity.FileId);
    }

    private static async Task VerifyBytesAsync(SyncLocalFileAuthority file, string path, long size, string hash,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(file.FinalPath, path, StringComparison.OrdinalIgnoreCase) || file.Stream.Length != size)
            throw new IOException("The private replacement object path or length changed.");
        file.Stream.Position = 0;
        var observed = await DropboxContentHash.ComputeAsync(file.Stream, cancellationToken);
        if (!string.Equals(observed, hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The private replacement object bytes changed.");
        file.VerifySingleLink();
    }

    private static void EnsureRoot()
    {
        if (!Directory.Exists(SyncSharkRuntimePaths.ProductRoot))
            throw new IOException("The registered product runtime root must exist before replacement staging is prepared.");
        using var productRoot = PilotDirectoryLease.OpenProductDirectory(SyncSharkRuntimePaths.ProductRoot);
        if (!Directory.Exists(SyncSharkRuntimePaths.ReplacementStagingRoot))
            Directory.CreateDirectory(SyncSharkRuntimePaths.ReplacementStagingRoot);
        using var stagingRoot = PilotDirectoryLease.OpenProductDirectory(SyncSharkRuntimePaths.ReplacementStagingRoot);
        productRoot.VerifyBinding();
#pragma warning disable CA1416
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new IOException("The current Windows identity has no SID.");
        var security = new DirectorySecurity();
        security.SetOwner(sid);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(SyncSharkRuntimePaths.ReplacementStagingRoot).SetAccessControl(security);
#pragma warning restore CA1416
        stagingRoot.VerifyBinding();
        productRoot.VerifyBinding();
    }

    private static PilotDirectoryLease OpenBoundRoot(SyncTransferIntent intent)
    {
        if (intent.StagingRootVolumeSerialNumber is null || intent.StagingRootFileId is null)
            throw new InvalidDataException("The replacement intent has no private staging-root identity.");
        return PilotDirectoryLease.OpenProductDirectory(SyncSharkRuntimePaths.ReplacementStagingRoot,
            new PilotDirectoryIdentity(intent.StagingRootVolumeSerialNumber.Value, intent.StagingRootFileId.Value));
    }

    private static void RequireSameVolume(string first, string second)
    {
        if (!string.Equals(Path.GetPathRoot(first), Path.GetPathRoot(second), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Atomic local replacement requires the registered private staging root on the same volume.");
    }

    private static string ValidateId(string value) => value.Length == 32 && value.All(char.IsAsciiHexDigit)
        ? value.ToLowerInvariant()
        : throw new InvalidDataException("The replacement intent ID is not accepted.");

    private static string Format(PilotDirectoryIdentity identity) =>
        $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}";
}
