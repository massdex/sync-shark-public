namespace Massdex.SyncShark;

public static class SyncCapabilityGate
{
    public static bool CreateNewUpload => true;
    public static bool CreateNewDownload => true;
    public static bool ExistingFileReplacement => true;
    public static bool ConflictResolution => true;
    public static bool DeletionPropagation => true;

    public static string ManualReviewReason(string change) =>
        $"{change} is detected and preserved because the exact safe transition preconditions were not satisfied.";

    public static bool IsSafeCreateNewIntent(SyncTransferIntent intent) =>
        intent.Kind == SyncIntentKind.Transfer && intent.ExpectedDestinationContentHash is null &&
        ((intent.Direction == SyncDirection.Upload && intent.StageFileName is null && intent.StageArtifactId is null) ||
         (intent.Direction == SyncDirection.Download && string.Equals(intent.StageFileName,
             SyncCreateNewLocalTransfer.StageName(intent.Id), StringComparison.Ordinal))) && intent.BackupFileName is null &&
        intent.RecoveryPath is null && intent.RecoveryOriginalPath is null &&
        intent.TransactionDirectoryName is null && intent.TransactionDirectoryId is null &&
        intent.TransactionDirectorySecurityHash is null && !intent.ArtifactsBound &&
        intent.BackupArtifactId is null;

    public static bool IsPrivateReplacementIntent(SyncTransferIntent intent) =>
        intent.Kind == SyncIntentKind.Transfer && intent.Direction == SyncDirection.Download &&
        intent.ExpectedDestinationContentHash is not null && intent.ExpectedDestinationVolumeSerialNumber is not null &&
        intent.ExpectedDestinationFileId is not null &&
        intent.StagingRootVolumeSerialNumber is not null && intent.StagingRootFileId is not null &&
        string.Equals(intent.StagingRelativePath, SyncPrivateReplacement.RelativeStage(intent.Id), StringComparison.Ordinal);

    public static bool IsControlTransactionIntent(SyncTransferIntent intent) =>
        intent.Direction == SyncDirection.Download &&
        intent.Kind is SyncIntentKind.Transfer or SyncIntentKind.ResolveKeepDropbox &&
        string.Equals(intent.StageFileName, SyncLocalTransactionDirectory.StageName, StringComparison.Ordinal) &&
        intent.BackupFileName is null or SyncLocalTransactionDirectory.BackupName &&
        intent.TransactionDirectoryName is not null && intent.TransactionDirectoryId is not null &&
        intent.TransactionDirectorySecurityHash is { Length: 64 };

    public static bool IsIdentityBoundUploadIntent(SyncTransferIntent intent) =>
        intent.Kind == SyncIntentKind.Transfer && intent.Direction == SyncDirection.Upload &&
        intent.ExpectedSourceVolumeSerialNumber is not null && intent.ExpectedSourceFileId is not null &&
        intent.ExpectedSourceSize is not null && intent.ExpectedSourceLastWriteUtc is not null &&
        intent.StageFileName is null && intent.BackupFileName is null &&
        intent.TransactionDirectoryName is null && intent.TransactionDirectoryId is null &&
        intent.TransactionDirectorySecurityHash is null;

    public static bool IsExecutableIntent(SyncTransferIntent intent) =>
        IsSafeCreateNewIntent(intent) || IsPrivateReplacementIntent(intent) ||
        (ExistingFileReplacement && IsControlTransactionIntent(intent)) ||
        (CreateNewUpload && IsIdentityBoundUploadIntent(intent)) ||
        (DeletionPropagation && intent.Kind is (SyncIntentKind.ArchiveRemoteAfterLocalDeletion or SyncIntentKind.ArchiveLocalAfterDropboxDeletion)) ||
        (ConflictResolution && intent.Kind is (SyncIntentKind.ResolveKeepLocal or SyncIntentKind.ResolveKeepDropbox));
}
