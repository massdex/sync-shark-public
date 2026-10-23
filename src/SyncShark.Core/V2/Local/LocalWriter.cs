using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>Applies planned effects to one local NTFS folder through the conditional local endpoint store.</summary>
public sealed partial class LocalWriter : IEndpointWriter, ISyncTransferResourceEndpoint,
    ILocalFreeSpaceAdmission, ISyncDirectDownloadTarget, ILocalRetainedNoticeReader, IRecoveryAvailability, ISourceAbsenceProbe,
    IRecoveryRecordReader, ISyncDirectDownloadSource

{
    private readonly SyncLocalEndpointStore _store;
    private readonly string _rootPath;
    private readonly Side _side;
    private readonly string _pairId;
    private readonly string _recoveryRoot;
    private string? _recoveryUnavailable;
    public string? RecoveryUnavailable
    {
        get => _recoveryUnavailable ?? (Directory.Exists(_recoveryRoot) ? null : _recoveryRoot + " is unavailable");
        private init => _recoveryUnavailable = value;
    }
    private readonly PilotDirectoryIdentity _rootIdentity;
    private readonly LocalFreeSpaceGuard _freeSpace;
    private readonly IReadOnlyList<SyncIgnoreRule> _ignoreRules;
    private readonly Func<ItemState, bool>? _providerNameRefused;
    private readonly Func<string, ItemState, bool>? _verifyCarriedChoice;
    private readonly string _rootId;
    string ISyncTransferResourceEndpoint.TransferResourceKey => "local:" + Path.GetPathRoot(_rootPath)!.ToUpperInvariant();

    private LocalWriter(SyncLocalEndpointStore store, string rootPath, string rootId, Side side, string pairId,
        string recoveryRoot, PilotDirectoryIdentity rootIdentity, LocalFreeSpaceGuard freeSpace,
        IReadOnlyList<SyncIgnoreRule> ignoreRules, Func<ItemState, bool>? providerNameRefused,
        Func<string, ItemState, bool>? verifyCarriedChoice)
    {
        _store = store;
        _rootPath = rootPath;
        _rootId = rootId;
        _side = side;
        _pairId = pairId;
        _recoveryRoot = recoveryRoot;
        _rootIdentity = rootIdentity;
        _freeSpace = freeSpace;
        _ignoreRules = ignoreRules;
        _providerNameRefused = providerNameRefused;
        _verifyCarriedChoice = verifyCarriedChoice;
    }

    /// <summary>Binds the writer to one local root and allocates its private control namespace and recovery folder.</summary>
    /// <param name="settings">The run's settings; the free-space reserve defaults to <see cref="V2Settings.Default"/>.</param>
    /// <param name="volumeSpace">The volume probe; the production drive probe is used when null.</param>
    /// <param name="databasePath">
    /// The state database path; its volume keeps the larger of the floor and the database-plus-WAL size free.
    /// </param>
    /// <param name="time">The clock for the database-size cache; the system clock is used when null.</param>
    /// <param name="ignoreRules">The pair policy, used only to carry ignored remnants with a folder deletion.</param>
    public static async Task<LocalWriter> OpenAsync(RootDef root, Side side, string pairId, string installationId,
        string recoveryRoot, CancellationToken ct, V2Settings? settings = null, IVolumeSpaceProbe? volumeSpace = null,
        string? databasePath = null, TimeProvider? time = null,
        IReadOnlyList<SyncIgnoreRule>? ignoreRules = null, Func<ItemState, bool>? providerNameRefused = null,
        string? recoveryUnavailable = null,
        Func<string, ItemState, bool>? verifyCarriedChoice = null, bool readOnlyOpen = false)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(recoveryRoot);
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The local endpoint writer requires Windows NTFS identities.");
        var (volume, rootFileId) = LocalItems.ParseObjectId(root.RootObjectId);
        var identity = new PilotDirectoryIdentity(volume, rootFileId);
        var recovery = Path.GetFullPath(recoveryRoot);
        if (recoveryUnavailable is null && !readOnlyOpen)
        {
            try { Directory.CreateDirectory(recovery); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { recoveryUnavailable = recovery + ": " + error.Message; }
        }
        if (recoveryUnavailable is null && readOnlyOpen)
            recoveryUnavailable = ProbeRecoveryReadOnly(recovery);
        var namespaceId = LocalItems.NamespaceId(root.RootId);
        using var lease = PilotDirectoryLease.Open(root.RootPath, identity);
        var plan = SyncControlNamespace.PlanLocalAllocation(lease, installationId, namespaceId);
        var local = readOnlyOpen
            ? await SyncControlNamespace.InspectAuthorizedLocalAsync(lease, plan, sharedRead: true).ConfigureAwait(false)
                ?? throw new SyncRunRefusedException("Plan-only requires the existing local control namespace; no namespace was created.")
            : await SyncControlNamespace.BindAuthorizedLocalAsync(lease, plan, null, _ => Task.CompletedTask)
                .ConfigureAwait(false);
        var store = new SyncLocalEndpointStore(new SyncLocalEndpointBinding(root.RootPath, volume, rootFileId),
            new SyncControlNamespaceBinding(installationId, namespaceId, local), SyncProviders.Local);
        return new LocalWriter(store, root.RootPath, root.RootId, side, pairId, recovery, identity,
            new LocalFreeSpaceGuard(volumeSpace ?? new DriveVolumeSpaceProbe(), settings ?? V2Settings.Default,
                databasePath, time), SyncIgnorePolicy.Normalize(ignoreRules), providerNameRefused, verifyCarriedChoice)
            { RecoveryUnavailable = recoveryUnavailable };
    }

    // OPEN_EXISTING only: check directory creation rights without creating a probe file.
    internal static string? ProbeRecoveryReadOnly(string recovery)
    {
        var candidate = recovery;
        while (!Directory.Exists(candidate))
        {
            if (File.Exists(candidate)) return recovery + ": Recovery path is occupied by a file.";
            var parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(parent)) return recovery + ": Recovery volume is unavailable.";
            candidate = parent;
        }
        using var handle = PilotDirectoryLease.CreateFile(candidate, 0x00000002 | 0x00000004,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3,
            PilotDirectoryLease.FileFlagBackupSemantics, IntPtr.Zero);
        return handle.IsInvalid ? recovery + ": Recovery directory creation access is unavailable." : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A copy/update/keep-both reserves its source bytes on the target volume only: a Dropbox revision is downloaded
    /// straight into the target's own temporary file, so there is no second spool copy. A Recovery-first action
    /// reserves its victim's bytes on the recovery volume. The reservation is held until the action finishes, and a
    /// refusal leaves no intent, so nothing can be replayed.
    /// </remarks>
    internal NewPointerDownload? NewPointers { get; set; }

    /// <summary>
    /// N10: decided once when the writer is opened. True only for a root with an online-only policy or with
    /// already-owned pointer rows, so an ordinary pair pays no root/parent/attribute probes on every observe.
    /// </summary>
    internal bool ObserveOwnedPointers { get; set; }

    public Task<FreeSpaceAdmission> AdmitFreeSpaceAsync(SyncAction action, CancellationToken ct) =>
        OwnedPointerUpdates.IsMetadataUpdate(action) ||
        (NewPointers is not null && NewPointerDownload.IsNewFile(action))
            ? _freeSpace.AdmitAsync(0, _rootPath, true, 0, _recoveryRoot, ct)
            : _freeSpace.AdmitAsync(action, _rootPath, _recoveryRoot, ct);

    private void RequireAvailableRecovery(SyncAction action)
    {
        if (RecoverySafety.RequiresRecovery(action) && RecoveryUnavailable is { } unavailable)
            throw new IOException("Recovery location unavailable: " + unavailable);
    }

    /// <inheritdoc/>
    public async Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct)
    {
        RequireAvailableRecovery(a);
        try
        {
            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(content);
            if (a.ExpectedTarget?.Flags.HasFlag(ItemFlags.OwnedPointer) == true)
                throw new IOException("The online-only target needs a metadata-only refresh. Retry storage settlement or rescan; no local bytes were opened.");
            // New-file admission reserved metadata only because the pair has an online-only policy. This stream route
            // cannot create a pointer (the source is no ISyncDirectDownloadSource), so it writes the full bytes: reserve
            // them now, exactly as the direct download does, instead of relying on the metadata-only reservation. This
            // covers a recovery restore whose retained bytes arrive as a stream.
            var byteAdmission = NewPointers is not null && NewPointerDownload.IsNewFile(a)
                ? await _freeSpace.AdmitAsync(a, _rootPath, _recoveryRoot, ct).ConfigureAwait(false)
                : FreeSpaceAdmission.Granted;
            using var byteReservation = byteAdmission.Reservation;
            if (byteAdmission.Refusal is { } refusal) throw new FreeSpaceRefusedException(refusal);
            using var operation = RunOperations.Begin(RunOperationKind.LocalWrite, RunOperations.Short(a.Path));
            using var tracked = RunOperationStream.Touching(content, operation.Touch);
            content = tracked;
            var source = a.Source;
            var hash = source?.Hash;
            if (source is null || source.Kind != ItemKind.File || hash is null)
                throw new ArgumentException("A local Put requires a file source with a content hash.", nameof(a));
            await EnsureParentsAsync(a.Path, ct).ConfigureAwait(false);
            PilotUploadResult published;
            if (a.ExpectedTarget is { } stub && stub.Flags.HasFlag(ItemFlags.Placeholder))
            {
                // Rule 35 the one executor point for online-only stubs. The stub is replaced
                // without a Recovery copy (it holds no content) and never opened for data.
                published = await _store.ReplaceOnlineOnlyStubAsync(a.Path, content, hash, stub.ObjectId, ct, source.Mtime)
                    .ConfigureAwait(false);
            }
            else if (a.ExpectedTarget is null || await IsPreservedAsync(a, ct).ConfigureAwait(false))
            {
                published = await _store.UploadAsync(a.Path, content, hash, null, ct, source.Mtime).ConfigureAwait(false);
            }
            else
            {
                var current = await RequireTargetAsync(a.Path, a.ExpectedTarget, ct).ConfigureAwait(false);
                published = await _store.UploadAsync(a.Path, content, hash, current.Revision, ct, source.Mtime).ConfigureAwait(false);
            }
            return FromStore(published.FileId, published.Revision, published.Size, published.ContentHash, a.Path,
                PilotEntryKind.File);
        }
        catch (Exception error) when (LocalItems.MapWriteError(error) is var mapped && mapped != error) { throw mapped; }
    }

    /// <summary>
    /// Downloads one exact remote revision into a product-owned temporary file beside the destination, verifies
    /// it against the planned hash and size, and publishes it by the same conditional create-only rename as
    /// <see cref="PutAsync"/>. The target volume holds one copy, never a spool plus a staging copy.
    /// </summary>
    public async Task<ItemState> PutDownloadedAsync(SyncAction a, ISyncDirectDownloadSource source,
        Func<SyncDownloadStage, CancellationToken, Task>? preservedAfterVerify, CancellationToken ct)
    {
        RequireAvailableRecovery(a);
        try
        {
            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(source);
            if (a.ExpectedTarget?.Flags.HasFlag(ItemFlags.OwnedPointer) == true)
                throw new IOException("The online-only target needs a metadata-only refresh. Retry storage settlement or rescan; no local bytes were opened.");
            if (NewPointers is { } pointers &&
                await pointers.TryCreateAsync(a, source, ct).ConfigureAwait(false) is { } pointer)
                return pointer;
            // New-file admission reserved metadata only. If policy selected ordinary bytes (including a
            // concurrent policy change), reserve those bytes now rather than relying on stale policy.
            var byteAdmission = NewPointers is not null && NewPointerDownload.IsNewFile(a)
                ? await _freeSpace.AdmitAsync(a, _rootPath, _recoveryRoot, ct).ConfigureAwait(false)
                : FreeSpaceAdmission.Granted;
            using var byteReservation = byteAdmission.Reservation;
            if (byteAdmission.Refusal is { } refusal) throw new FreeSpaceRefusedException(refusal);
            var planned = a.Source;
            var hash = planned?.Hash;
            if (planned is null || planned.Kind != ItemKind.File || hash is null)
                throw new ArgumentException("A local download requires a file source with a content hash.", nameof(a));
            using var operation = RunOperations.Begin(RunOperationKind.LocalWrite, RunOperations.Short(a.Path));
            await EnsureParentsAsync(a.Path, ct).ConfigureAwait(false);
            string? expectedRevision = null;
            string? expectedStubId = null;
            if (a.ExpectedTarget is { } stub && stub.Flags.HasFlag(ItemFlags.Placeholder))
            {
                expectedStubId = stub.ObjectId;
            }
            else if (a.ExpectedTarget is not null && !await IsPreservedAsync(a, ct).ConfigureAwait(false))
            {
                expectedRevision = (await RequireTargetAsync(a.Path, a.ExpectedTarget, ct).ConfigureAwait(false))
                    .Revision;
            }
            var checkpointRecoveryPath = string.Empty;
            if (a.RecoveryFirst)
            {
                using var recoveryLease = PilotDirectoryLease.Open(_recoveryRoot);
                checkpointRecoveryPath = RecoveryPath(recoveryLease, a.ActionId);
            }
            var published = await _store.DownloadToTargetAsync(a.Path, planned.Size,
                (destination, token) => source.DownloadToAsync(planned,
                    RunOperationStream.Touching(destination, operation.Touch), token),
                hash, expectedRevision, expectedStubId, checkpointRecoveryPath, preservedAfterVerify, ct, planned.Mtime)
                .ConfigureAwait(false);
            return FromStore(published.FileId, published.Revision, published.Size, published.ContentHash, a.Path,
                PilotEntryKind.File);
        }
        catch (Exception error) when (LocalItems.MapWriteError(error) is var mapped && mapped != error) { throw mapped; }
    }

    /// <summary>
    /// Finishes or rolls back one interrupted direct download whose durable checkpoint is still open. It never
    /// overwrites a present divergent target. Superseded and excluded actions settle with actionable notices;
    /// publication and rollback remain create-only, and cleanup requires the checkpoint's native identity.
    /// </summary>
    public async Task<ActionResult> ResumeDownloadAsync(SyncAction action, ProviderEffectCheckpoint checkpoint,
        CancellationToken ct)
    {
        if (!string.Equals(checkpoint.Kind, SyncDirectDownloadCheckpoint.Kind, StringComparison.Ordinal))
            return ResumeUncertain(action, "not a local download checkpoint");
        SyncDownloadStage? stage;
        try { stage = JsonSerializer.Deserialize<SyncDownloadStage>(checkpoint.Id); }
        catch (JsonException) { stage = null; }
        if (stage is null || action.Source is not { Hash: { } sourceHash } source)
            return ResumeUncertain(action, "the download checkpoint is unreadable");
        var needsRecovery = stage.RecoveryPath.Length != 0;
        // Exclusion and relocated storage are not permission to touch either location.
        if (needsRecovery && !action.RecoveryFirst)
            return Settled(action, "Excluded download left untouched. Recovery/deletion candidates: " +
                stage.RecoveryPath + "; " + stage.Path);
        if (needsRecovery)
        {
            using var lease = PilotDirectoryLease.Open(_recoveryRoot);
            if (!string.Equals(stage.RecoveryPath, RecoveryPath(lease, action.ActionId), StringComparison.OrdinalIgnoreCase))
                return Settled(action, "Recovery location changed; files left untouched. Inspect/restore from " +
                    stage.RecoveryPath + "; partial deletion candidate: " + stage.Path);
        }
        var targetFull = LocalItems.FullPath(_rootPath, action.Path);
        try { SyncLocalEndpointStore.RequireDownloadStagePath(targetFull, stage.Path); }
        catch (SyncLocalEndpointStore.DownloadStageMismatchException error)
        {
            return Settled(action, error.Message + " Files left untouched; partial deletion candidate: " + stage.Path);
        }
        var (volume, fileId) = LocalItems.ParseObjectId(stage.ObjectId);
        try
        {
            SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(stage.Path,
                new PilotDirectoryIdentity(volume, fileId), stage.Path, targetFull);
        }
        catch (SyncLocalFileAuthority.RecoveryEnvelopeException)
        {
            return Settled(action, "Partial ownership cannot be proven; files left untouched. Deletion candidate: " +
                stage.Path + "; Recovery location: " + stage.RecoveryPath);
        }
        var current = await _store.FindEntryAtPathAsync(action.Path, ct).ConfigureAwait(false);
        var recovery = needsRecovery ? await TryRecoveryRefAsync(action, ct).ConfigureAwait(false) : null;
        if (current is { Kind: PilotEntryKind.File } && current.Id == stage.ObjectId &&
            Content.SameContent(current.Size, current.ContentHash, source.Size, sourceHash) == true)
        {
            var observed = LocalItems.FromStore(current, targetFull);
            string? dateFailure = null;
            try
            {
                current = await _store.StampPublishedDownloadAsync(action.Path, stage.ObjectId, source.Size,
                    sourceHash, source.Mtime, ct).ConfigureAwait(false);
                observed = LocalItems.FromStore(current, targetFull);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                dateFailure = "Date could not be set; will retry through dates repair. " + error.Message;
            }
            return ResumeDone(action, observed, recovery) with
            {
                Reason = dateFailure is not null ? SyncDirectDownloadCheckpoint.DateNoticePrefix + dateFailure +
                    (needsRecovery && recovery is null ? " Recovery copy unavailable at " + stage.RecoveryPath : "") :
                    needsRecovery && recovery is null ? SyncDirectDownloadCheckpoint.NoticePrefix +
                    "Download published, but its Recovery copy is unavailable at " + stage.RecoveryPath +
                    ". Keep the current file; inspect backups if the old version is needed." : null
            };
        }

        if (!stage.Verified || current is not null || recovery is null)
        {
            // Before verification no original was removed. A divergent destination wins even when the original
            // is archived. Native identity, never content equality, authorizes partial cleanup.
            var cleaned = TryDeleteOwnedStage(stage);
            var originalPresent = current is { Kind: PilotEntryKind.File } && action.ExpectedTarget is { } victim &&
                Content.SameContent(current.Size, current.ContentHash, victim.Size, victim.Hash) == true;
            if (cleaned && (originalPresent || (action.ExpectedTarget is null && current is null)))
                return new(action.ActionId, Outcome.Skipped, null, null, recovery, EffectResolver.NotApplied)
                { ForgetAbsentDownloadBaseline = current is null };
            return Settled(action, "Download superseded; current destination left unchanged. " +
                (recovery is null ? "No verified Recovery copy is available. " : "Original retained at " + recovery.Location + ". ") +
                (cleaned ? "Resync to plan the current version." : "Partial deletion candidate (ownership unproven): " + stage.Path), recovery);
        }
        if (File.Exists(stage.Path))
        {
            try
            {
                var published = await _store.PublishVerifiedPartialAsync(action.Path, stage.Path,
                    stage.ObjectId, source.Size, sourceHash, ct, source.Mtime).ConfigureAwait(false);
                return ResumeDone(action, FromStore(published.FileId, published.Revision, published.Size,
                    published.ContentHash, action.Path, PilotEntryKind.File), recovery);
            }
            catch (Exception error) when (error is SyncLocalEndpointStore.DownloadStageMismatchException or
                SyncLocalFileAuthority.RecoveryEnvelopeException)
            {
                return Settled(action, error.Message + " Files left untouched; partial deletion candidate: " +
                    stage.Path + "; original retained at " + recovery.Location, recovery);
            }
        }
        // The plain upload path already verifies its held stage; no fake provider download or duplicate hash pass.
        await using var archived = new FileStream(recovery.Location, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await _store.UploadAsync(action.Path, archived, recovery.Hash!, null, ct, action.ExpectedTarget?.Mtime)
            .ConfigureAwait(false);
        return new(action.ActionId, Outcome.Skipped, null, null, recovery, EffectResolver.NotApplied);
    }

    private static ActionResult ResumeUncertain(SyncAction action, string reason) =>
        new(action.ActionId, Outcome.Uncertain, null, null, null, reason);

    private ActionResult Settled(SyncAction action, string detail, RecoveryRef? recovery = null) =>
        new(action.ActionId, Outcome.Skipped, null, null, recovery,
            SyncDirectDownloadCheckpoint.NoticePrefix + EffectResolver.Superseded + ". " + detail)
        { ForgetAbsentDownloadBaseline = TargetAbsent(action.Path) };

    private bool TargetAbsent(string path)
    {
        try { _ = File.GetAttributes(LocalItems.FullPath(_rootPath, path)); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }

    private static ActionResult ResumeDone(SyncAction action, ItemState state, RecoveryRef? recovery) =>
        action.Target == Side.A
            ? new ActionResult(action.ActionId, Outcome.Done, state, action.Source, recovery, null)
            : new ActionResult(action.ActionId, Outcome.Done, action.Source, state, recovery, null);

    private async Task<RecoveryRef?> TryRecoveryRefAsync(SyncAction action, CancellationToken ct)
    {
        if (action.ExpectedTarget is not { Kind: ItemKind.File, Hash: { } hash } victim) return null;
        using var lease = PilotDirectoryLease.Open(_recoveryRoot);
        var path = RecoveryPath(lease, action.ActionId);
        SyncLocalRecovery.RestoreInterruptedArchiveAccess(path);
        if (!File.Exists(path) || new FileInfo(path).Length != victim.Size) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return string.Equals(await SyncContentIdentity.ComputeAsync(SyncProviders.Local, stream, ct).ConfigureAwait(false),
            hash, StringComparison.OrdinalIgnoreCase) ? new RecoveryRef(_side, path, null, hash, victim.Size) : null;
    }

    internal static bool DeleteMatchingStage(SyncDownloadStage checkpoint, string actualObjectId, Action deleteOwnedHandle)
    {
        if (actualObjectId != checkpoint.ObjectId) return false;
        deleteOwnedHandle();
        return true;
    }

    internal static bool TryDeleteOwnedStage(SyncDownloadStage checkpoint)
    {
        try
        {
            if (!File.Exists(checkpoint.Path)) return true;
            var (volume, fileId) = LocalItems.ParseObjectId(checkpoint.ObjectId);
            SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(checkpoint.Path,
                new PilotDirectoryIdentity(volume, fileId), checkpoint.Path);
            using var stage = SyncLocalFileAuthority.OpenExistingExclusive(checkpoint.Path);
            var identity = stage.ReadIdentity();
            return DeleteMatchingStage(checkpoint, SyncLocalEndpointStore.ObjectId(identity.VolumeSerialNumber, identity.FileId),
                () => stage.DeleteOnClose());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <inheritdoc/>
    public async Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct)
    {
        RequireAvailableRecovery(a);
        try
        {
            ArgumentNullException.ThrowIfNull(a);
            if (a.FromPath is null || a.ExpectedTarget is null)
                throw new ArgumentException("A local Move requires a source path and its expected target.", nameof(a));
            if (a.ExpectedTarget.Flags.HasFlag(ItemFlags.OwnedPointer))
                return await MoveOwnedPointerAsync(a, ct).ConfigureAwait(false);
            using var operation = RunOperations.Begin(RunOperationKind.LocalWrite, "move " + RunOperations.Short(a.Path));
            var current = await RequireTargetAsync(a.FromPath, a.ExpectedTarget, ct).ConfigureAwait(false);
            await EnsureParentsAsync(a.Path, ct).ConfigureAwait(false);
            var moved = await _store.MoveAsync(current, a.Path, ct).ConfigureAwait(false);
            return LocalItems.FromStore(moved, LocalItems.FullPath(_rootPath, moved.RelativePath));
        }
        catch (Exception error) when (LocalItems.MapWriteError(error) is var mapped && mapped != error) { throw mapped; }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(SyncAction a, CancellationToken ct)
    {
        RequireAvailableRecovery(a);
        try
        {
            ArgumentNullException.ThrowIfNull(a);
            var expected = a.ExpectedTarget ?? throw new ArgumentException("A local Delete requires its expected target.", nameof(a));
            if (expected.Flags.HasFlag(ItemFlags.OwnedPointer))
            {
                await DeleteSourceGuard.RevalidateCurrentAsync(a).ConfigureAwait(false);
                DeleteOwnedPointer(a, ct);
                return;
            }
            if (expected.Kind == ItemKind.File && await IsPreservedAsync(a, ct).ConfigureAwait(false)) return;
            var current = await RequireTargetAsync(a.Path, expected, ct).ConfigureAwait(false);
            var entry = expected.Kind == ItemKind.Folder ? LocalItems.ToStoreEntry(expected) : current;
            if (expected.Kind == ItemKind.Folder && !a.PreserveIgnoredChildren && (_ignoreRules.Count != 0 || _providerNameRefused is not null))
            {
                await DeleteSourceGuard.RevalidateCurrentAsync(a).ConfigureAwait(false);
                await CleanIgnoredChildrenAsync(a, ct).ConfigureAwait(false);
            }
            await DeleteSourceGuard.RevalidateCurrentAsync(a).ConfigureAwait(false);
            await _store.DeleteAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (LocalItems.MapWriteError(error) is var mapped && mapped != error) { throw mapped; }
    }

    /// <inheritdoc/>
    public async Task<ItemState> MkDirAsync(string path, CancellationToken ct)
    {
        try
        {
            var current = await _store.FindEntryAtPathAsync(path, ct).ConfigureAwait(false);
            if (current is not null)
            {
                if (current.Kind != PilotEntryKind.Directory)
                    throw new ExpectedTargetMismatchException("The local path '" + path + "' is a file, not a folder.");
                return LocalItems.FromStore(current, LocalItems.FullPath(_rootPath, current.RelativePath));
            }
            await EnsureParentsAsync(path, ct).ConfigureAwait(false);
            var created = await _store.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
            return LocalItems.FromStore(created, LocalItems.FullPath(_rootPath, created.RelativePath));
        }
        catch (Exception error) when (LocalItems.MapWriteError(error) is var mapped && mapped != error) { throw mapped; }
    }

    /// <inheritdoc/>
    public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
        PreserveCoreAsync(victim, actionId, ct, ProviderEffectContext.Current.Value?.Action is { } effect
            ? effect with { ActionId = actionId } : null);

    // Carried ignored children retain their own recovery receipt identity, but removal is authorized
    // by the enclosing folder action. Pass that exact authority explicitly, never infer it from an ID prefix.
    private async Task<RecoveryRef> PreserveCoreAsync(ItemState victim, string actionId, CancellationToken ct,
        SyncAction? removalAuthority)
    {
        if (RecoveryUnavailable is not null) throw new IOException("Recovery location unavailable: " + RecoveryUnavailable);
        try
        {
            ArgumentNullException.ThrowIfNull(victim);
            ArgumentNullException.ThrowIfNull(actionId);
            using var operation = RunOperations.Begin(RunOperationKind.LocalWrite,
                "recovery " + RunOperations.Short(victim.Path));
            if (victim.Kind == ItemKind.Folder)
                throw new ArgumentException("A folder has no bytes to preserve.", nameof(victim));
            if (victim.Hash is null)
                throw new ArgumentException("A preserved victim must carry its content hash.", nameof(victim));
            using var rootLease = PilotDirectoryLease.Open(_rootPath, _rootIdentity);
            using var recoveryLease = PilotDirectoryLease.Open(_recoveryRoot);
            var recoveryPath = RecoveryPath(recoveryLease, actionId);
            SyncLocalRecovery.RestoreInterruptedArchiveAccess(recoveryPath);
            var exists = File.Exists(recoveryPath);
            if (!exists) await RequireTargetAsync(victim.Path, victim, ct).ConfigureAwait(false);
            var durable = exists ? DurableIdentity(recoveryPath) : null;
            // The archive owner persists the verified copy through the Executor's durable sink here, before it
            // removes the original: a crash in the result window can no longer hide the only remaining copy.
            var archived = await SyncLocalRecovery.ArchiveAsync(LocalItems.ToScanEntry(victim, _rootPath), rootLease,
                recoveryLease, _pairId, OperationId(actionId), recoveryPath, durable,
                (archive, token) => ProviderEffectContext.Current.Value is { } context
                    ? context.RecordRecoveryAsync(actionId, victim.Path,
                        new RecoveryRef(_side, archive.RecoveryPath, null, archive.ContentHash, archive.Size), _pairId, token)
                    : Task.CompletedTask, ct, () => DeleteSourceGuard.RevalidateCurrentAsync(removalAuthority)).ConfigureAwait(false);
            return new RecoveryRef(_side, archived.RecoveryPath, null, archived.ContentHash, archived.Size);
        }
        catch (Exception error) when (LocalItems.MapWriteError(error) is var mapped && mapped != error) { throw mapped; }
    }

    async Task<Stream> ILocalRetainedNoticeReader.OpenRecoveryAsync(RecoveryRecord recovery, ItemState retained,
        CancellationToken ct)
    {
        using var lease = PilotDirectoryLease.Open(_recoveryRoot);
        var path = RecoveryPath(lease, recovery.ActionId);
        if (recovery.PairId != _pairId || recovery.Side != _side ||
            !string.Equals(Path.GetFullPath(recovery.Location), path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Recovery record is not bound to this operation.");
        // Repair both the final and promoted staging access envelopes before opening the archive.
        SyncLocalRecovery.RestoreInterruptedArchiveAccess(path);
        var archive = await SyncLocalRecovery.GetArchiveAsync(LocalItems.ToScanEntry(retained, _rootPath),
            lease, _pairId, OperationId(recovery.ActionId), path,
            File.Exists(path) ? DurableIdentity(path) : null, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The retained Recovery archive is unavailable.", path);
        return new FileStream(archive.RecoveryPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    /// <inheritdoc/>
    public async Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct)
    {
        var item = await _store.FindMetadataAtPathAsync(path, ct).ConfigureAwait(false);
        return item is null ? null : new ItemState(item.RelativePath,
            item.Kind == PilotEntryKind.Directory ? ItemKind.Folder : ItemKind.File,
            item.Id!, null, null, item.Size, DateTimeOffset.MinValue, ItemFlags.None);
    }

    /// <inheritdoc/>
    public Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var absence = new LocalFileAbsence(_rootPath, _rootIdentity);
        return Task.FromResult<bool?>(absence.Check(historical) switch
        {
            FileAbsence.Present => true,
            FileAbsence.Gone => false,
            _ => null,
        });
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AbsenceObservation>> IsAbsentBatchAsync(
        IReadOnlyList<(ItemState Historical, string Path)> requests, CancellationToken ct)
    {
        if (requests.Count > 128) throw new ArgumentOutOfRangeException(nameof(requests));
        var identities = new FileAbsence[requests.Count];
        var reasons = new string?[requests.Count];
        var results = new AbsenceObservation[requests.Count];
        // One root identity lease and native path buffer per admitted page, rather than one
        // 64-KiB buffer and root handle per file. Check uses that buffer, so do not share it in parallel.
        using var absence = new LocalFileAbsence(_rootPath, _rootIdentity);
        for (var i = 0; i < requests.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { identities[i] = absence.Check(requests[i].Historical); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct) == FailureRetryOwner.FailureScope.Item)
            { identities[i] = FileAbsence.Unknown; reasons[i] = AbsenceObservation.Cause(error); }
        }
        await Parallel.ForEachAsync(Enumerable.Range(0, requests.Count), new ParallelOptions
            { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (index, token) =>
        {
            if (identities[index] == FileAbsence.Present) { results[index] = new(false, null); return; }
            var request = requests[index];
            try
            {
                if (await ObserveSourceMetadataAsync(request.Path, token).ConfigureAwait(false) is not null ||
                    !PairPath.Same(request.Historical.Path, request.Path) &&
                    await ObserveSourceMetadataAsync(request.Historical.Path, token).ConfigureAwait(false) is not null)
                    results[index] = new(false, null);
                else results[index] = new(identities[index] == FileAbsence.Gone ? true : null, reasons[index]);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct) == FailureRetryOwner.FailureScope.Item)
            { results[index] = new(null, AbsenceObservation.Cause(error)); }
        }).ConfigureAwait(false);
        return results;
    }

    /// <inheritdoc/>
    public async Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct)
    {
        if (historical is null || string.IsNullOrWhiteSpace(historical.ObjectId)) return null;
        bool? present = await IsHistoricalSourcePresentAsync(historical, ct).ConfigureAwait(false);
        if (present == true) return false;
        // A replacement at the old path blocks even when the historical identity was retired.
        if (await ObserveSourceMetadataAsync(path, ct).ConfigureAwait(false) is not null) return false;
        if (historical is not null && !PairPath.Same(historical.Path, path) &&
            await ObserveSourceMetadataAsync(historical.Path, ct).ConfigureAwait(false) is not null) return false;
        return present == false ? true : null;
    }

    /// <inheritdoc/>
    public async Task<ItemState?> ObserveAsync(string path, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            // N10: an ordinary pair with no storage policy, owned pointers or recorded root pays no
            // root/parent/attribute probes. A recorded root keeps observing pointers after its policy returns to
            // offline/inherit, so intent recovery and OwnedPointer classification still work.
            if ((NewPointers is not null || ObserveOwnedPointers || PointerUpdateProvider is not null) &&
                ObserveOwnedPointer(path) is { } pointer)
                return pointer;
            var current = await _store.FindEntryAtPathAsync(path, ct).ConfigureAwait(false);
            return current is null ? null : LocalItems.FromStore(current, LocalItems.FullPath(_rootPath, current.RelativePath));
        }
        catch (Exception error) when (LocalItems.MapWriteError(error) is var mapped && mapped != error) { throw mapped; }
    }

    // Use the existing verified-stage/preserve/publish protocol for local-to-local transfers too.
    // OpenRead owns source validation; the target verifies the streamed bytes before archiving its victim.
    public async Task<SyncDownloadResult> DownloadToAsync(ItemState s, Stream destination, CancellationToken ct)
    {
        await using var content = await OpenReadAsync(s, ct).ConfigureAwait(false);
        await content.CopyToAsync(destination, ct).ConfigureAwait(false);
        return new SyncDownloadResult(s.ObjectId, s.VersionId ?? string.Empty, s.Size, s.Hash!);
    }

    /// <inheritdoc/>
    public async Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct)
    {
        using var measurement = RunInstrumentation.Current?.Measure(RunTimingStage.LocalRecheck);
        try
        {
            ArgumentNullException.ThrowIfNull(s);
            if (s.Flags.HasFlag(ItemFlags.OwnedPointer))
                throw new ExpectedTargetMismatchException("An online-only pointer is not an upload source.");
            if (s.Kind != ItemKind.File || s.Hash is null)
                throw new ArgumentException("A local OpenRead requires a file state with a content hash.", nameof(s));
            var info = new FileInfo(LocalItems.FullPath(_rootPath, s.Path));
            if (!info.Exists || info.Length != s.Size || info.LastWriteTimeUtc.Ticks != s.Mtime.UtcTicks)
                throw new ExpectedTargetMismatchException("The local source '" + s.Path + "' changed since it was planned.");
            var lease = PilotDirectoryLease.Open(_rootPath, _rootIdentity);
            try
            {
                return await PilotLocalScanner.OpenValidatedForUploadAsync(
                    LocalItems.ToScanEntry(s, _rootPath), lease, ct, SyncProviders.Local).ConfigureAwait(false);
            }
            catch (IOException error)
            {
                // The held-handle check found a different identity, size, mtime or bytes: a stale read source.
                throw new ExpectedTargetMismatchException(error.Message, error);
            }
            finally
            {
                lease.Dispose();
            }
        }
        catch (Exception error) when (LocalItems.MapWriteError(error) is var mapped && mapped != error) { throw mapped; }
    }

    // RQ-025, revised owner semantics: ignored entries have no standalone actions, but an enclosing
    // folder deletion carries them. Preflight the entire physical remainder before changing any of it; a
    // normal unexpected descendant (including an empty folder) must still leave the folder untouched.
    // Both passes stream: at 100k+ children in one folder and 1M items overall, no subtree is held in memory.
    private const int IgnoredChildPageSize = 256;

    private async Task CleanIgnoredChildrenAsync(SyncAction action, CancellationToken ct)
    {
        using var root = PilotDirectoryLease.Open(_rootPath, _rootIdentity);
        var parent = action.ExpectedTarget!;
        var folderRules = _ignoreRules.Where(rule => rule.Kind == SyncIgnoreKind.FolderName).ToArray();
        var fileNameRules = _ignoreRules.Where(rule => rule.Kind == SyncIgnoreKind.FileName).ToArray();
        await ValidateAsync(parent).ConfigureAwait(false);
        await RemoveAsync(parent).ConfigureAwait(false);

        bool IsCarried(ItemState entry) =>
            SyncIgnorePolicy.IsIgnored(entry.Path, entry.Kind == ItemKind.Folder, _ignoreRules) ||
            (_providerNameRefused?.Invoke(entry) ?? false);

        // Preflight pass: read every physical child (including one that appeared after the inventory, decision
        // 80(e)) before any effect. Directory enumeration is lazy, and the walk retains no per-folder list.
        async Task ValidateAsync(ItemState folder)
        {
            var (volume, fileId) = LocalItems.ParseObjectId(folder.ObjectId);
            using var held = PilotDirectoryLease.Open(LocalItems.FullPath(_rootPath, folder.Path),
                new PilotDirectoryIdentity(volume, fileId));
            foreach (var fullPath in Directory.EnumerateFileSystemEntries(held.Path))
            {
                ct.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(fullPath);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    throw new ExpectedTargetMismatchException("The local folder contains a redirected or offline child.");
                var path = folder.Path + "/" + Path.GetFileName(fullPath);
                var entry = await ObserveAsync(path, ct).ConfigureAwait(false)
                    ?? throw new ExpectedTargetMismatchException("The local child '" + path + "' changed.");
                // Re-evaluate using the observed real kind, not just a name or stale listing attributes.
                if (!IsCarried(entry))
                    throw new ExpectedTargetMismatchException((entry.Kind == ItemKind.Folder) == isDirectory
                        ? "The local folder contains an unexpected child '" + path + "'."
                        : "The local child '" + path + "' changed kind.");
                if (entry.Kind == ItemKind.Folder) await ValidateAsync(entry).ConfigureAwait(false);
            }
        }

        // Removal pass, deepest first. The authoritative physical sweep is lazy and bounded to 256 children;
        // no inventory is needed and no large directory is materialized.
        async Task RemoveAsync(ItemState folder)
        {
            // Keep the exact parent pinned across the physical sweep.
            var identity = LocalItems.ParseObjectId(folder.ObjectId);
            using var pinned = PilotDirectoryLease.Open(LocalItems.FullPath(_rootPath, folder.Path),
                new PilotDirectoryIdentity(identity.Volume, identity.FileId));
            while (true)
            {
                var batch = new List<ItemState>(IgnoredChildPageSize);
                var (volume, fileId) = LocalItems.ParseObjectId(folder.ObjectId);
                using (var held = PilotDirectoryLease.Open(LocalItems.FullPath(_rootPath, folder.Path),
                    new PilotDirectoryIdentity(volume, fileId)))
                {
                    foreach (var fullPath in Directory.EnumerateFileSystemEntries(held.Path))
                    {
                        ct.ThrowIfCancellationRequested();
                        var attributes = File.GetAttributes(fullPath);
                        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                            throw new ExpectedTargetMismatchException(
                                "The local folder contains a redirected or offline child.");
                        var path = folder.Path + "/" + Path.GetFileName(fullPath);
                        var entry = await ObserveAsync(path, ct).ConfigureAwait(false)
                            ?? throw new ExpectedTargetMismatchException("The local child '" + path + "' changed.");
                        if (!IsCarried(entry))
                            throw new ExpectedTargetMismatchException(
                                "The local folder contains an unexpected child '" + path + "'.");
                        batch.Add(entry);
                        if (batch.Count == IgnoredChildPageSize) break;
                    }
                }
                if (batch.Count == 0) return;
                foreach (var entry in batch) await RemoveEntryAsync(entry).ConfigureAwait(false);
            }
        }

        async Task RemoveEntryAsync(ItemState child)
        {
            // Inventory may be stale. Bind the physical child to the frozen version immediately before
            // any preservation/deletion, then let the conditional writer reject a subsequent race.
            if (action.FrozenChoiceId is { } frozen)
            {
                child = await ObserveAsync(child.Path, ct).ConfigureAwait(false)
                    ?? throw new ExpectedTargetMismatchException("A carried child disappeared before cleanup.");
                if (_verifyCarriedChoice?.Invoke(frozen, child) != true)
                    throw new ExpectedTargetMismatchException("A carried ignored child changed after the choice and was kept: " + child.Path);
            }
            if (child.Kind == ItemKind.Folder)
            {
                await RemoveAsync(child).ConfigureAwait(false);
                await DeleteSourceGuard.RevalidateCurrentAsync(action).ConfigureAwait(false);
                await _store.DeleteAsync(LocalItems.ToStoreEntry(child), ct).ConfigureAwait(false);
                return;
            }
            // Only small FileName matches may skip Recovery. FolderName ownership wins even
            // when the leaf also matches FileName; extension-only matches always keep their bytes.
            // A provider-refused child always keeps its bytes, including one refused through its parent.
            if (child.Size > 64 * 1024 ||
                (_providerNameRefused?.Invoke(child) == true) ||
                SyncIgnorePolicy.IsIgnored(child.Path, false, folderRules) ||
                !SyncIgnorePolicy.IsIgnored(child.Path, false, fileNameRules))
            {
                // Bind retry recovery to the exact child version, not merely its path in the parent action.
                var childId = action.ActionId + "\nignored-child\n" + child.Path + "\n" +
                    child.ObjectId + "\n" + child.Hash;
                var recoveryAction = action with { ActionId = childId, Path = child.Path,
                    ExpectedTarget = child, RecoveryFirst = true };
                var admission = await AdmitFreeSpaceAsync(recoveryAction, ct).ConfigureAwait(false);
                using var reservation = admission.Reservation;
                if (admission.Refusal is { } refusal) throw new IOException(refusal.Detail);
                await PreserveCoreAsync(child, childId, ct, action).ConfigureAwait(false);
            }
            else
            {
                var current = await RequireTargetAsync(child.Path, child, ct).ConfigureAwait(false);
                await DeleteSourceGuard.RevalidateCurrentAsync(action).ConfigureAwait(false);
                await _store.DeleteAsync(current, ct).ConfigureAwait(false);
            }
        }
    }

    private ItemState FromStore(string objectId, string revision, long size, string? hash, string path,
        PilotEntryKind kind) =>
        LocalItems.FromStore(new PilotRemoteEntry(path, kind, objectId, revision, size, hash),
            LocalItems.FullPath(_rootPath, path));

    // The item at the path must still be the exact planned object: same NTFS identity and kind, and for a
    // file the same size, write time and (when planned) content hash.
    private async Task<PilotRemoteEntry> RequireTargetAsync(string path, ItemState expected, CancellationToken ct)
    {
        using var measurement = RunInstrumentation.Current?.Measure(RunTimingStage.LocalRecheck);
        var current = await _store.FindEntryAtPathAsync(path, ct).ConfigureAwait(false);
        if (current is null)
            throw new ExpectedTargetMismatchException("The local target '" + path + "' is absent.");
        var kindMatches = expected.Kind == ItemKind.File
            ? current.Kind == PilotEntryKind.File
            : current.Kind == PilotEntryKind.Directory;
        if (!kindMatches || current.Id != expected.ObjectId)
            throw new ExpectedTargetMismatchException("The local target '" + path + "' is a different object.");
        if (expected.Kind == ItemKind.File &&
            (current.Size != expected.Size ||
             LocalItems.FileWriteTimeTicks(_rootPath, path) != expected.Mtime.UtcTicks ||
             expected.Hash is not null && Content.SameHash(current.ContentHash, expected.Hash) != true))
            throw new ExpectedTargetMismatchException("The local target '" + path + "' changed since it was planned.");
        return current;
    }

    private async Task EnsureParentsAsync(string path, CancellationToken ct)
    {
        var parts = PilotPath.NormalizeRelative(path).Split('/');
        for (var depth = 1; depth < parts.Length; depth++)
        {
            var prefix = string.Join('/', parts.Take(depth));
            var entry = await _store.FindEntryAtPathAsync(prefix, ct).ConfigureAwait(false);
            if (entry is null) await _store.CreateDirectoryAsync(prefix, ct).ConfigureAwait(false);
            else if (entry.Kind != PilotEntryKind.Directory)
                throw new ExpectedTargetMismatchException("The parent path '" + prefix + "' is not a folder.");
        }
    }

    // True when a prior PreserveAsync archived this exact victim for this action and removed it from the root.
    private async Task<bool> IsPreservedAsync(SyncAction action, CancellationToken ct)
    {
        if (!action.RecoveryFirst || action.ExpectedTarget is not { Kind: ItemKind.File } target) return false;
        if (target.Hash is null) return false;
        if (await _store.FindEntryAtPathAsync(action.Path, ct).ConfigureAwait(false) is not null) return false;
        return await TryRecoveryRefAsync(action, ct).ConfigureAwait(false) is not null;
    }

    private string RecoveryPath(PilotDirectoryLease recoveryLease, string actionId) =>
        SyncLocalRecovery.BuildRecoveryPath(recoveryLease, _pairId, OperationId(actionId));

    internal static string OperationId(string actionId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actionId))).ToLowerInvariant()[..32];

    internal static string DurableIdentity(string recoveryPath)
    {
        using var held = SyncLocalFileAuthority.OpenExistingReadGuard(recoveryPath);
        return SyncLocalTransactionDirectory.FormatIdentity(held.ReadIdentity());
    }
}
