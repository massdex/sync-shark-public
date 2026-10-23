namespace Massdex.SyncShark;

internal static class SyncCreateNewLocalTransfer
{
    internal const string StagePrefix = ".syncshark-create-";
    internal static Action? StageCreatedForTest { get; set; }
    internal static Action? StageBoundForTest { get; set; }
    internal static Action? DownloadVerifiedForTest { get; set; }
    internal static Action<string>? BeforeCommitForTest { get; set; }
    internal static Action? CommittedBeforeDisposeForTest { get; set; }
    public static string StageName(string intentId)
    {
        if (intentId.Length != 32 || intentId.Any(value => !char.IsAsciiHexDigit(value)))
            throw new InvalidDataException("The create-new intent ID is not accepted.");
        return StagePrefix + intentId.ToLowerInvariant() + ".tmp";
    }

    public static async Task<SyncDownloadResult> DownloadAsync(PilotRemoteEntry remote,
        PilotDirectoryLease rootLease, ISyncRemoteStore remoteStore, SyncTransferIntent intent,
        Func<SyncTransferIntent, CancellationToken, Task> persist, CancellationToken cancellationToken)
    {
        rootLease.VerifyBinding();
        var relativePath = PilotPath.NormalizeRelative(remote.RelativePath);
        var targetPath = Path.Combine(rootLease.Path, relativePath);
        var stageName = StageName(intent.Id);
        if (!string.Equals(intent.StageFileName, stageName, StringComparison.Ordinal))
            throw new InvalidDataException("The create-new stage name is not bound to its durable intent.");
        var stagePath = Path.Combine(rootLease.Path, stageName);
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
            throw new IOException("Create-new download requires an absent local destination.");

        await using var target = SyncLocalFileAuthority.CreateNewMovableFile(stagePath);
        StageCreatedForTest?.Invoke();
        try
        {
            var identity = target.VerifySingleLink();
            intent = intent with { StageArtifactId = $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}" };
            await persist(intent, cancellationToken);
            StageBoundForTest?.Invoke();
            var writer = target.CreateGuardedWriteStream(SyncLocalTransfer.StageWriteIntervalForTest);
            var result = await remoteStore.DownloadAsync(remote, writer, cancellationToken);
            await target.Stream.FlushAsync(cancellationToken);
            target.Stream.Flush(true);
            target.VerifySingleLink();
            if (!string.Equals(target.FinalPath, stagePath, StringComparison.OrdinalIgnoreCase) ||
                target.Stream.Length != result.Size)
                throw new IOException("The create-new local stage identity or length differs after download.");
            target.Stream.Position = 0;
            var hash = await DropboxContentHash.ComputeAsync(target.Stream, cancellationToken);
            if (!string.Equals(hash, result.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The create-new local target bytes differ after download.");
            target.VerifySingleLink();
            DownloadVerifiedForTest?.Invoke();
            rootLease.VerifyBinding();
            BeforeCommitForTest?.Invoke(targetPath);
            if (File.Exists(targetPath) || Directory.Exists(targetPath))
                throw new IOException("The create-new local destination appeared before commit and was preserved.");
            target.RenameTo(rootLease, relativePath);
            CommittedBeforeDisposeForTest?.Invoke();
            return result;
        }
        catch
        {
            target.DeleteOnClose();
            throw;
        }
    }

    public static void RestoreAccess(PilotDirectoryLease rootLease, SyncTransferIntent intent)
    {
        if (intent.StageArtifactId is null) return;
        var stagePath = Path.Combine(rootLease.Path, StageName(intent.Id));
        var targetPath = PilotPath.ResolveContainedPath(rootLease.Path, intent.RelativePath);
        var restored = SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(stagePath, stagePath, targetPath);
        if (restored is not null && !string.Equals(Format(restored), intent.StageArtifactId, StringComparison.Ordinal))
            throw new IOException("The protected create-new object identity does not match its durable intent.");
        rootLease.VerifyBinding();
    }

    public static void CleanupUncommittedStage(PilotDirectoryLease rootLease, SyncTransferIntent intent)
    {
        var stagePath = Path.Combine(rootLease.Path, StageName(intent.Id));
        if (!File.Exists(stagePath)) return;
        var expected = intent.StageArtifactId;
        if (expected is null)
        {
            var restored = SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(stagePath, stagePath);
            if (restored is null)
                throw new IOException("An unbound create-new stage exists without protected owner evidence and was preserved.");
            expected = Format(restored);
        }
        using var stage = SyncLocalFileAuthority.OpenExistingExclusive(stagePath);
        var identity = stage.VerifySingleLink();
        if (!string.Equals(Format(identity), expected, StringComparison.Ordinal) ||
            !string.Equals(stage.FinalPath, stagePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The create-new stage identity changed and was preserved.");
        stage.DeleteOnClose();
        rootLease.VerifyBinding();
    }

    private static string Format(PilotDirectoryIdentity identity) =>
        $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}";
}
