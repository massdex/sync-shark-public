namespace Massdex.SyncShark;

public static class SyncLocalRecovery
{
    internal const string RecoveryFolderPrefix = ".syncshark-recovery-";
    private const string RecoverySuffix = ".recovery";
    // Bounded stripes coordinate distinct leases for the same physical parent; only the
    // brief directory initialization is serialized, never Recovery copies or file deletes.
    private static readonly object[] RecoveryRootGates = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    internal static Action<string>? SourcePreservedForTest { get; set; }
    internal static Action<string>? SourceRemovedBeforeRecoveryCloseForTest { get; set; }
    internal static Action<string>? RecoveryWriteIntervalForTest { get; set; }
    internal static Action<string>? RecoveryCopyIntervalForTest { get; set; }
    internal static Action<string>? BeforeCreateForTest { get; set; }
    internal static Action? RecoveryCreatedBeforeBindingForTest { get; set; }

    public static bool IsRecoveryFolderName(string name) =>
        name.StartsWith(RecoveryFolderPrefix, StringComparison.OrdinalIgnoreCase) && name.Length > RecoveryFolderPrefix.Length;

    internal static string BuildRecoveryRootPath(PilotDirectoryLease rootLease, string pairId)
    {
        rootLease.VerifyBinding();
        return Path.Combine(rootLease.Path, RecoveryFolderPrefix + RequireSafeId(pairId, "pair"));
    }

    internal static string RestorePreboundWritableObjectAccess(PilotDirectoryLease rootLease, string pairId,
        SyncTransferIntent intent)
    {
        var exactRecoveryPath = BuildRecoveryPath(rootLease, pairId, intent.Id);
        if (string.IsNullOrWhiteSpace(intent.RecoveryPath) ||
            !string.Equals(Path.GetFullPath(intent.RecoveryPath), exactRecoveryPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The pending local recovery destination is not the prebound operation path.");
        if (intent.RecoveryFileId is not null)
        {
            var restored = RestoreInterruptedArchiveAccess(exactRecoveryPath);
            if (restored is not null && restored != ParseSourceIdentity(intent.RecoveryFileId))
                throw new IOException("The protected local recovery identity does not match its durable intent.");
        }
        rootLease.VerifyBinding();
        return exactRecoveryPath;
    }

    public static string BuildRecoveryPath(PilotDirectoryLease rootLease, string pairId, string operationId)
    {
        rootLease.VerifyBinding();
        var recoveryName = RecoveryFolderPrefix + RequireSafeId(pairId, "pair");
        var memberName = RequireSafeOperationId(operationId) + RecoverySuffix;
        return Path.Combine(rootLease.Path, recoveryName, memberName);
    }

    public static string FormatSourceIdentity(PilotLocalEntry entry)
    {
        if (entry.VolumeSerialNumber is null or 0 || entry.FileId is null or 0)
            throw new InvalidDataException("The inventoried local source identity is incomplete.");
        return $"{entry.VolumeSerialNumber.Value:x8}:{entry.FileId.Value:x16}";
    }

    public static PilotDirectoryIdentity ParseSourceIdentity(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 2 || parts[0].Length != 8 || parts[1].Length != 16 ||
            !uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var volume) ||
            !ulong.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var fileId) ||
            volume == 0 || fileId == 0)
            throw new InvalidDataException("The persisted local source identity is not accepted.");
        return new PilotDirectoryIdentity(volume, fileId);
    }

    public static async Task<SyncLocalArchiveResult> ArchiveAsync(PilotLocalEntry expected,
        PilotDirectoryLease rootLease, string pairId, string operationId, string recoveryPath,
        string? durableRecoveryIdentity,
        Func<SyncLocalArchiveResult, CancellationToken, Task> persistRecoveryIdentity,
        CancellationToken cancellationToken) => await ArchiveAsync(expected, rootLease, rootLease, pairId,
        operationId, recoveryPath, durableRecoveryIdentity, persistRecoveryIdentity, cancellationToken);

    public static async Task<SyncLocalArchiveResult> ArchiveAsync(PilotLocalEntry expected,
        PilotDirectoryLease rootLease, PilotDirectoryLease controlRoot, string pairId, string operationId,
        string recoveryPath, string? durableRecoveryIdentity,
        Func<SyncLocalArchiveResult, CancellationToken, Task> persistRecoveryIdentity,
        CancellationToken cancellationToken)
    {
        rootLease.VerifyBinding();
        controlRoot.VerifyBinding();
        var exactRecoveryPath = BuildRecoveryPath(controlRoot, pairId, operationId);
        if (!string.Equals(Path.GetFullPath(recoveryPath), exactRecoveryPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The local recovery destination is not the prebound operation path.");

        using var recoveryLease = OpenOrCreateRecoveryRoot(controlRoot, pairId);
        var restoredIdentity = RestoreInterruptedArchiveAccess(exactRecoveryPath);
        if (File.Exists(exactRecoveryPath))
        {
            var retained = await VerifyRecoveryAsync(exactRecoveryPath, expected, recoveryLease, cancellationToken);
            RequireOwnedRecovery(retained, durableRecoveryIdentity, restoredIdentity);
            await persistRecoveryIdentity(retained, cancellationToken);
            await RemoveExpectedSourceIfPresentAsync(expected, rootLease, cancellationToken);
            return retained;
        }

        BeforeCreateForTest?.Invoke(exactRecoveryPath);
        await using var source = SyncLocalFileAuthority.OpenExistingExclusive(expected.FullPath);
        await VerifySourceAsync(source, expected, rootLease, cancellationToken);
        var stagePath = ArchiveStagePath(exactRecoveryPath);
        // Only the encrypted access envelope authorizes removing an interrupted private copy.
        try
        {
            SyncLocalFileAuthority.RemoveInterruptedWritableObject(stagePath);
            if (File.Exists(stagePath) || Directory.Exists(stagePath))
                throw new SyncLocalFileAuthority.RecoveryEnvelopeException("The Recovery stage has no verified ownership envelope.");
        }
        catch (SyncLocalFileAuthority.RecoveryEnvelopeException error)
        {
            var detail = "Recovery staging file '" + stagePath + "' could not be verified as an owned interrupted copy. " +
                "The original file is unchanged. Move this staging file aside without deleting it, then run the pair again.";
            await SyncByteProgressStream.ReportProcessAsync("Recovery needs attention", expected.RelativePath,
                detail, ProcessMessageKind.Notice);
            throw new IOException(detail, error);
        }
        await using var recovery = SyncLocalFileAuthority.CreateNewMovableFile(stagePath);
        recovery.VerifySingleLink();
        if (!string.Equals(recovery.FinalPath, stagePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The recovery copy was not created at the prebound staging destination.");

        SyncLocalArchiveResult retainedResult;
        try
        {
            source.Stream.Position = 0;
            var recoveryWriter = recovery.CreateGuardedWriteStream(RecoveryWriteIntervalForTest);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.Stream.ReadAsync(buffer, cancellationToken)) != 0)
            {
                await recoveryWriter.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                RecoveryCopyIntervalForTest?.Invoke(stagePath);
            }
            await recovery.Stream.FlushAsync(cancellationToken);
            recovery.Stream.Flush(true);
            var identity = recovery.VerifySingleLink();
            var staged = await VerifyRecoveryAuthorityAsync(recovery, stagePath, expected, recoveryLease,
                identity, cancellationToken);
            recovery.RenameTo(recoveryLease, Path.GetFileName(exactRecoveryPath));
            retainedResult = staged with { RecoveryPath = exactRecoveryPath };
        }
        catch
        {
            recovery.DeleteOnClose();
            throw;
        }

        RecoveryCreatedBeforeBindingForTest?.Invoke();
        try { await persistRecoveryIdentity(retainedResult, cancellationToken); }
        catch
        {
            recovery.DeleteOnClose();
            throw;
        }

        rootLease.VerifyBinding();
        controlRoot.VerifyBinding();
        recoveryLease.VerifyBinding();
        source.DeleteOnClose();
        SourcePreservedForTest?.Invoke(exactRecoveryPath);
        // Close the deleted source before the copy: a crash here leaves Recovery as the only copy,
        // with its existing access envelope still available to the cold reopen.
        await source.DisposeAsync();
        SourceRemovedBeforeRecoveryCloseForTest?.Invoke(exactRecoveryPath);
        return retainedResult;
    }

    public static async Task<SyncLocalArchiveResult?> GetArchiveAsync(PilotLocalEntry expected,
        PilotDirectoryLease rootLease, string pairId, string operationId, string recoveryPath,
        string? durableRecoveryIdentity,
        CancellationToken cancellationToken)
    {
        var exact = BuildRecoveryPath(rootLease, pairId, operationId);
        if (!string.Equals(Path.GetFullPath(recoveryPath), exact, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The local recovery destination is not the prebound operation path.");
        var restoredIdentity = RestoreInterruptedArchiveAccess(exact);
        if (!File.Exists(exact)) return null;
        using var recoveryLease = PilotDirectoryLease.Open(Path.GetDirectoryName(exact)!);
        var retained = await VerifyRecoveryAsync(exact, expected, recoveryLease, cancellationToken);
        RequireOwnedRecovery(retained, durableRecoveryIdentity, restoredIdentity);
        return retained;
    }

    public static Task<SyncLocalArchiveResult?> GetArchiveAsync(PilotLocalEntry expected,
        PilotDirectoryLease rootLease, string pairId, string operationId, string recoveryPath,
        CancellationToken cancellationToken) => GetArchiveAsync(expected, rootLease, pairId, operationId,
        recoveryPath, null, cancellationToken);

    public static async Task<SyncLocalArchiveResult> ArchiveForManualRestoreAsync(PilotDirectoryLease rootLease,
        PilotDirectoryLease controlRoot, string pairId, string operationId, string relativePath, string provider,
        Func<SyncLocalArchiveResult, CancellationToken, Task> persistRecoveryIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistRecoveryIdentity);
        rootLease.VerifyBinding();
        controlRoot.VerifyBinding();
        var sourcePath = PilotPath.ResolveContainedPath(rootLease.Path, relativePath);
        if (!File.Exists(sourcePath) || Directory.Exists(sourcePath) ||
            PilotPath.IsUnsafePath(sourcePath))
            throw new IOException("The current restore destination is not an exact regular file.");
        var recoveryPath = BuildRecoveryPath(controlRoot, pairId, operationId);
        using var recoveryLease = OpenOrCreateRecoveryRoot(controlRoot, pairId);
        if (File.Exists(recoveryPath) || Directory.Exists(recoveryPath))
            throw new IOException("The manual-restore recovery destination is occupied.");
        await using var source = SyncLocalFileAuthority.OpenExistingExclusive(sourcePath);
        var sourceIdentity = source.VerifySingleLink();
        if (!string.Equals(source.FinalPath, sourcePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The current restore destination moved before its recovery copy was opened.");
        await using var recovery = SyncLocalFileAuthority.CreateNewFile(recoveryPath);
        source.Stream.Position = 0;
        await source.Stream.CopyToAsync(recovery.CreateGuardedWriteStream(), cancellationToken);
        await recovery.Stream.FlushAsync(cancellationToken);
        recovery.Stream.Flush(true);
        var recoveryIdentity = recovery.VerifySingleLink();
        recovery.Stream.Position = 0;
        var recoveryHash = await SyncContentIdentity.ComputeAsync(provider, recovery.Stream, cancellationToken);
        source.Stream.Position = 0;
        var sourceHash = await SyncContentIdentity.ComputeAsync(provider, source.Stream, cancellationToken);
        if (!string.Equals(recoveryHash, sourceHash, StringComparison.OrdinalIgnoreCase) ||
            recovery.Stream.Length != source.Stream.Length || source.VerifySingleLink() != sourceIdentity ||
            !string.Equals(recovery.FinalPath, recoveryPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The current destination was not retained exactly before restore.");
        recoveryLease.VerifyBinding();
        rootLease.VerifyBinding();
        controlRoot.VerifyBinding();
        var retained = new SyncLocalArchiveResult(recoveryPath, recoveryIdentity.VolumeSerialNumber,
            recoveryIdentity.FileId, recovery.Stream.Length, recoveryHash);
        // Hold BOTH exact objects across durable registration. Reopening the destination pathname
        // afterward could remove a newly saved replacement rather than the file we preserved.
        // On registration failure/uncertainty keep the source and protected copy; do not guess
        // whether the transaction committed and delete the only retained preimage.
        RecoveryCreatedBeforeBindingForTest?.Invoke();
        await persistRecoveryIdentity(retained, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        rootLease.VerifyBinding();
        controlRoot.VerifyBinding();
        recoveryLease.VerifyBinding();
        if (source.VerifySingleLink() != sourceIdentity ||
            !string.Equals(source.FinalPath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
            recovery.VerifySingleLink() != recoveryIdentity ||
            !string.Equals(recovery.FinalPath, recoveryPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The held manual-restore source or recovery identity changed before removal.");
        source.DeleteOnClose();
        return retained;
    }

    private static string ArchiveStagePath(string recoveryPath) => recoveryPath + ".part";

    internal static PilotDirectoryIdentity? RestoreInterruptedArchiveAccess(string recoveryPath)
    {
        var restored = SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(recoveryPath, recoveryPath);
        // Promotion leaves the envelope at the private staging name until normal disposal.
        if (File.Exists(recoveryPath) && File.Exists(SyncLocalFileAuthority.RecoverySidecarPath(ArchiveStagePath(recoveryPath))))
            restored = SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(ArchiveStagePath(recoveryPath),
                recoveryPath);
        return restored;
    }

    private static async Task RemoveExpectedSourceIfPresentAsync(PilotLocalEntry expected,
        PilotDirectoryLease rootLease, CancellationToken cancellationToken)
    {
        if (!File.Exists(expected.FullPath)) return;
        await using var source = SyncLocalFileAuthority.OpenExistingExclusive(expected.FullPath);
        await VerifySourceAsync(source, expected, rootLease, cancellationToken);
        source.DeleteOnClose();
    }

    private static async Task VerifySourceAsync(SyncLocalFileAuthority source, PilotLocalEntry expected,
        PilotDirectoryLease rootLease, CancellationToken cancellationToken)
    {
        rootLease.VerifyBinding();
        if (!string.Equals(source.FinalPath, expected.FullPath, StringComparison.OrdinalIgnoreCase) ||
            source.Stream.Length != expected.Size || File.GetLastWriteTimeUtc(expected.FullPath) != expected.LastWriteUtc ||
            PilotPath.IsUnsafePath(expected.FullPath))
            throw new IOException("The local archive source changed after inventory.");
        var expectedIdentity = new PilotDirectoryIdentity(
            expected.VolumeSerialNumber ?? throw new InvalidDataException("The expected local source volume is absent."),
            expected.FileId ?? throw new InvalidDataException("The expected local source file ID is absent."));
        if (source.ReadIdentity() != expectedIdentity)
            throw new IOException("The local archive source object identity changed after the durable intent was prepared.");
        source.Stream.Position = 0;
        var hash = await DropboxContentHash.ComputeAsync(source.Stream, cancellationToken);
        if (!string.Equals(hash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The local archive source bytes changed after inventory.");
        source.Stream.Position = 0;
        rootLease.VerifyBinding();
    }

    private static async Task<SyncLocalArchiveResult> VerifyRecoveryAsync(string recoveryPath,
        PilotLocalEntry expected, PilotDirectoryLease recoveryLease, CancellationToken cancellationToken)
    {
        await using var recovery = SyncLocalFileAuthority.OpenExistingExclusive(recoveryPath);
        var identity = recovery.VerifySingleLink();
        return await VerifyRecoveryAuthorityAsync(recovery, recoveryPath, expected, recoveryLease, identity,
            cancellationToken);
    }

    private static async Task<SyncLocalArchiveResult> VerifyRecoveryAuthorityAsync(SyncLocalFileAuthority recovery,
        string recoveryPath, PilotLocalEntry expected, PilotDirectoryLease recoveryLease,
        PilotDirectoryIdentity identity, CancellationToken cancellationToken)
    {
        recoveryLease.VerifyBinding();
        if (!string.Equals(recovery.FinalPath, recoveryPath, StringComparison.OrdinalIgnoreCase) ||
            recovery.Stream.Length != expected.Size)
            throw new IOException("The local recovery object does not match its prebound destination and size.");
        recovery.Stream.Position = 0;
        var hash = await DropboxContentHash.ComputeAsync(recovery.Stream, cancellationToken);
        recovery.Stream.Position = 0;
        if (!string.Equals(hash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The local recovery object differs from the inventoried source bytes.");
        var verifiedIdentity = recovery.VerifySingleLink();
        if (verifiedIdentity != identity)
            throw new IOException("The local recovery object identity changed during readback.");
        recoveryLease.VerifyBinding();
        return new SyncLocalArchiveResult(recoveryPath, identity.VolumeSerialNumber, identity.FileId,
            recovery.Stream.Length, hash);
    }

    private static void RequireOwnedRecovery(SyncLocalArchiveResult retained, string? durableRecoveryIdentity,
        PilotDirectoryIdentity? restoredIdentity)
    {
        var retainedIdentity = new PilotDirectoryIdentity(retained.VolumeSerialNumber, retained.FileId);
        if (durableRecoveryIdentity is not null)
        {
            if (ParseSourceIdentity(durableRecoveryIdentity) != retainedIdentity)
                throw new IOException("The local recovery pathname no longer names its durably bound object.");
            return;
        }
        if (restoredIdentity is null || restoredIdentity != retainedIdentity)
            throw new IOException("An unbound local recovery pathname exists without protected product-owner evidence and was preserved.");
    }

    private static string RequireSafeId(string value, string role) =>
        value.Length is >= 8 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? value
            : throw new InvalidDataException($"The {role} ID cannot be used for local recovery.");

    private static string RequireSafeOperationId(string value) =>
        value.Length == 32 && value.All(char.IsAsciiHexDigit)
            ? value.ToLowerInvariant()
            : throw new InvalidDataException("The operation ID cannot be used for local recovery.");

    private static PilotDirectoryLease OpenOrCreateRecoveryRoot(PilotDirectoryLease controlRoot, string pairId)
    {
        controlRoot.VerifyBinding();
        var childName = RecoveryFolderPrefix + RequireSafeId(pairId, "pair");
        var gate = RecoveryRootGates[(uint)controlRoot.Identity.GetHashCode() % (uint)RecoveryRootGates.Length];
        lock (gate)
        {
            controlRoot.VerifyBinding();
            // Shared lazy directory: atomically open-or-create, verifying the handle is
            // a real directory and not a junction. Never request create-new here.
            var lease = PilotDirectoryLease.OpenOrCreateChild(controlRoot, childName, hidden: true, pin: true);
            try
            {
                if (lease.Identity.VolumeSerialNumber != controlRoot.Identity.VolumeSerialNumber)
                    throw new IOException("The local recovery directory is not on the bound pair volume.");
                controlRoot.VerifyBinding();
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
    }
}
