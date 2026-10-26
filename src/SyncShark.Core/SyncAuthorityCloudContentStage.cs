using System.Security.Cryptography;
using System.Text.Json;

namespace Massdex.SyncShark;

// CC3A: the one provider-neutral verified cloud-content staging component.
//
// One shared primitive prepares a bounded private local stage for the exact retained source member and
// version of one cloud endpoint port. It is provider-neutral: the source provider is read only through the
// typed CC2C port, its native identity and native content algorithm come from the port's registered provider
// definition, and the download goes through the ordinary ISyncRemoteStore.DownloadAsync contract. A
// different provider supplies a definition, an adapter and its algorithm; no provider branch lives here.
//
// The stage is working storage, never a user endpoint. It is created only inside an already-held private
// control-root lease that the caller obtained from the existing product control-namespace ownership, through
// the existing SyncLocalTransactionDirectory / SyncLocalFileAuthority allocations - there is no second
// staging-directory scheme, no second journal and no new app-data root. Nothing in this file can reach a
// logical cloud endpoint, a selected NTFS pair root, the authority database's effect tables or a provider
// upload/delete/move; the only effects are one read-only source download and one private local stage file.
//
// This helper is deliberately NOT runtime or effect completion. It starts no target attempt, records no
// receipt and completes no operation, so after preparation the operation journal still shows that no target
// effect was ever issued. The caller owns the operation lease, the operation's later effect and this stage's
// cleanup; an expired lease cannot be renewed or bypassed here.

/// <summary>One safe refusal of the verified cloud content stage. It is never a partial or unverified stage.</summary>
public sealed class SyncAuthorityCloudContentStageException(string message, Exception? innerException = null)
    : IOException(message, innerException);

/// <summary>
/// The immutable evidence of the exact cloud source object this stage retained: the typed port's provider,
/// retained provider account and verified native namespace, the exact authority root binding, the retained
/// object and object version, the exact normalized relative path, the provider file id and revision, the
/// registered provider-native content algorithm with its native digest, and the reviewed byte size.
/// </summary>
public sealed record SyncAuthorityCloudStageSourceEvidence(
    string Provider,
    string ProviderAccountId,
    string NamespaceId,
    string RootBindingId,
    string ObjectId,
    string? ObjectVersionId,
    string RelativePath,
    string FileId,
    string? Revision,
    string NativeAlgorithm,
    string NativeHash,
    long Size,
    string? RecoveryArtifactId = null)
{
    /// <summary>
    /// CC4E-2: exactly one source-version identity must be present. An ordinary observed source carries its
    /// retained object version id. The one narrow Recovery-read source kind carries the exact retained
    /// recovery artifact id instead and leaves the object version id null, because a retained recovery copy is
    /// NOT an ordinary object version. An artifact id is never written into the object version member, and an
    /// evidence row that carries both or neither is refused rather than guessed.
    /// </summary>
    internal void RequireExactlyOneSourceIdentity()
    {
        var hasVersion = !string.IsNullOrWhiteSpace(ObjectVersionId);
        var hasRecovery = !string.IsNullOrWhiteSpace(RecoveryArtifactId);
        if (hasVersion == hasRecovery)
            throw new SyncAuthorityCloudContentStageException(
                "The staged source evidence does not carry exactly one source-version identity; a partially identified source is refused rather than guessed.");
    }
}

/// <summary>
/// One native digest computed over the stage's retained bytes for one target provider, carrying its own
/// registered algorithm so two different algorithms are never relabelled or compared as if they matched.
/// </summary>
public sealed record SyncAuthorityCloudStageDigest(string Provider, string Algorithm, string Digest, long Size);

/// <summary>
/// The outcome of the explicit stage cleanup entry: whether the owned stage and its empty private
/// transaction were removed and released, or retained for later reconciliation with the exact residual paths.
/// </summary>
public sealed record SyncAuthorityCloudStageCleanup(
    string OperationId,
    bool Cleaned,
    string JournalState,
    string Reason,
    string TransactionPath,
    string StagePath,
    bool StageRemoved,
    bool TransactionRemoved,
    IReadOnlyList<string> ResidualPaths);

/// <summary>
/// One verified private local stage over the exact retained bytes of one cloud source revision. The held
/// stage owns the open private transaction directory handle, the open exact stage-file handle, the durable
/// private-space admission and the exact identities of both. Disposing it closes only those handles: it never
/// deletes the stage, never changes an allocation and never removes retained evidence.
/// </summary>
public sealed class SyncAuthorityCloudContentStage : IAsyncDisposable
{
    /// <summary>
    /// The shipped product 64 MiB safety reserve used by the existing download and recovery spool code
    /// (DropboxProvider.RecoverySpoolSafetyReserve, SyncVerifiedRangeDownload.SafetyReserve and the inline
    /// reserve in GoogleDriveRevisions.WithRevisionBytesAsync). It is declared here with that exact value
    /// because those three constants are private to their own files and outside this packet's write
    /// boundary; the mechanism itself - SyncRecoverySpoolAdmission over a real observed volume free space -
    /// is the shipped one. A 20 GiB development scratch floor is deliberately NOT applied to production staging.
    /// </summary>
    internal const long StagingSafetyReserveBytes = 64L * 1024 * 1024;

    /// <summary>
    /// The narrow test-only device-space observation seam for the staging volume. The real production default
    /// is an actual DriveInfo observation of the staging volume; a fixture may supply one bounded synthetic
    /// observation so the reserve arithmetic can be exercised without filling a real disk.
    /// </summary>
    internal static Func<string, long>? AvailableDeviceBytesForTest { get; set; }

    // A scoped observation of completed real hash chunks, independent of UI progress throttling.
    // The fixture may cancel its own caller token; it never supplies bytes, hashes or decisions.
    private static readonly AsyncLocal<Action<long>?> HashChunkObserver = new();
    internal static IDisposable ObserveHashChunksForTest(Action<long> observer)
    {
        var previous = HashChunkObserver.Value;
        HashChunkObserver.Value = observer;
        return new HashChunkObservationScope(previous);
    }

    private sealed class HashChunkObservationScope(Action<long>? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            HashChunkObserver.Value = previous;
        }
    }

    private readonly SyncLocalFileAuthority _stage;
    private readonly PilotDirectoryLease _transaction;
    private readonly SyncRecoverySpoolAdmission.Reservation _admission;
    private int _disposed;

    private SyncAuthorityCloudContentStage(SyncLocalFileAuthority stage, PilotDirectoryLease transaction,
        SyncRecoverySpoolAdmission.Reservation admission, string operationId,
        SyncAuthorityCloudStageSourceEvidence source, string commonSha256, string transactionAllocationId,
        string stageAllocationId, string transactionIdentity, string stageIdentity, string stagePath)
    {
        _stage = stage;
        _transaction = transaction;
        _admission = admission;
        OperationId = operationId;
        Source = source;
        CommonSha256 = commonSha256;
        TransactionAllocationId = transactionAllocationId;
        StageAllocationId = stageAllocationId;
        TransactionIdentity = transactionIdentity;
        StageIdentity = stageIdentity;
        StagePath = stagePath;
    }

    /// <summary>The exact retained operation whose lease bound this stage.</summary>
    public string OperationId { get; }

    /// <summary>The immutable evidence of the exact cloud source revision this stage retained.</summary>
    public SyncAuthorityCloudStageSourceEvidence Source { get; }

    /// <summary>The common whole-file SHA-256 computed over exactly the bytes this stage retained.</summary>
    public string CommonSha256 { get; }

    /// <summary>The released transaction-directory allocation this stage belongs to.</summary>
    public string TransactionAllocationId { get; }

    /// <summary>The stage-file allocation this stage belongs to.</summary>
    public string StageAllocationId { get; }

    /// <summary>The held private transaction directory's retained identity.</summary>
    public string TransactionIdentity { get; }

    /// <summary>The held stage file's retained identity.</summary>
    public string StageIdentity { get; }

    /// <summary>The exact private transaction directory path this stage was created in.</summary>
    public string TransactionPath => _transaction.Path;

    /// <summary>The exact private stage file path holding the verified source bytes.</summary>
    public string StagePath { get; }

    /// <summary>The reviewed byte size of the retained source object.</summary>
    public long Size => Source.Size;

    /// <summary>The provider-native digest of the retained bytes under the source provider's own algorithm.</summary>
    public string NativeHash => Source.NativeHash;

    /// <summary>
    /// The retained bytes of exactly one verified cloud source revision, as the held private stage file. The
    /// caller owns the read position; <see cref="ResetPosition"/> returns it to the first byte.
    /// </summary>
    public Stream Content => _stage.Stream;

    /// <summary>The exact single-link identity of the held stage file.</summary>
    public PilotDirectoryIdentity HeldFileIdentity => _stage.VerifySingleLink();

    internal static async Task<SyncAuthorityCloudContentStage> PrepareAsync(SyncAuthorityDatabase authority,
        SyncAuthorityLease lease, string sourceMemberId, SyncAuthorityCloudEndpointPort port,
        SyncControlNamespaceBinding controlNamespace, PilotDirectoryLease controlRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMemberId);
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(controlNamespace);
        ArgumentNullException.ThrowIfNull(controlRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var namespaceId = RequireHeldControlRoot(controlNamespace, controlRoot);

        var operation = await authority.LoadOperationAsync(lease.OperationId, cancellationToken);
        RequireStagingOperation(operation, lease);
        var sourceMember = RequireSourceMember(operation, sourceMemberId, port);
        var version = await authority.LoadObjectVersionAsync(
            sourceMember.ObjectVersionId ??
            throw new SyncAuthorityCloudContentStageException(
                "The retained source member has no exact object version authority."), cancellationToken);
        var path = RequireExactRetainedPath(sourceMember);
        var identity = RequireSourceIdentity(version, sourceMember, port);
        var sourceHash = version.ContentHash!;
        var size = version.Size!.Value;
        var expected = new PilotRemoteEntry(path, PilotEntryKind.File, identity.FileId!, identity.Revision,
            size, sourceHash);

        var source = new SyncAuthorityCloudStageSourceEvidence(port.Provider, port.ProviderAccountId,
            port.NamespaceId, port.RootBindingId, sourceMember.ObjectId, version.ObjectVersionId, path,
            identity.FileId!, identity.Revision, identity.ContentAlgorithm!, sourceHash, size);
        return await PrepareBoundReadAsync(authority, lease, operation, namespaceId, controlRoot, source,
            (destination, token) => port.Store.DownloadAsync(expected, destination, token),
            token => FindExactSourceAsync(port, path, identity.FileId!, identity.Revision, size, sourceHash, token),
            cancellationToken);
    }

    /// <summary>
    /// CC4E-2: the one narrow bound RECOVERY-read overload. It prepares exactly the same bounded private local
    /// stage, through the same allocated private transaction and stage, the same lease/fencing and allocation
    /// checks and the same length and native-digest verification, over the exact bytes of one retained recovery
    /// copy read through the target's own bound recovery root. It is not a second staging scheme: the recovery
    /// copy is a retained recovery artifact, never an ordinary object version, so the object version member
    /// stays null and the exact recovery artifact id travels instead. The retained archive is revalidated
    /// before and after the read, and nothing here can reach a logical endpoint write.
    /// </summary>
    internal static async Task<SyncAuthorityCloudContentStage> PrepareRecoveryReadAsync(
        SyncAuthorityDatabase authority, SyncAuthorityLease lease, SyncAuthorityCloudEndpointPort port,
        SyncRemoteRecoveryRootBinding binding, SyncArchiveResult expected, string objectId,
        string recoveryArtifactId, string relativePath, string nativeAlgorithm,
        SyncControlNamespaceBinding controlNamespace, PilotDirectoryLease controlRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryArtifactId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeAlgorithm);
        ArgumentNullException.ThrowIfNull(controlNamespace);
        ArgumentNullException.ThrowIfNull(controlRoot);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(expected.FileId) || string.IsNullOrWhiteSpace(expected.Revision) ||
            string.IsNullOrWhiteSpace(expected.RecoveryPath) || string.IsNullOrWhiteSpace(expected.ContentHash) ||
            expected.Size < 0)
            throw new SyncAuthorityCloudContentStageException(
                "The retained recovery copy has no exact file identity, revision, recovery path, native digest and size; no verified stage is produced.");
        // A retained recovery copy lives under its own bound recovery root, so its retained path is the
        // provider's own recovery path and is NOT a pair-relative endpoint path: it is required to be exactly
        // the retained archive's recovery path rather than being normalized into some other path.
        if (!string.Equals(relativePath, expected.RecoveryPath, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The staged recovery source path is not the exact retained recovery path of the archive it reads; it is refused rather than normalized.");
        if (string.IsNullOrWhiteSpace(binding.RootFolderId) || string.IsNullOrWhiteSpace(binding.RootNamespaceId))
            throw new SyncAuthorityCloudContentStageException(
                "The bound recovery root has no exact folder and namespace identity; no recovery read is issued.");
        if (!SyncContentIdentity.SupportsIncrementalHashing(nativeAlgorithm) ||
            !SyncContentIdentity.IdentityMatchesAlgorithm(nativeAlgorithm, expected.ContentHash) ||
            !string.Equals(nativeAlgorithm, port.Definition.ContentHashAlgorithm, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The retained recovery digest was not produced with this port provider's registered content algorithm; it is refused rather than relabelled.");
        var namespaceId = RequireHeldControlRoot(controlNamespace, controlRoot);
        var operation = await authority.LoadOperationAsync(lease.OperationId, cancellationToken);
        RequireStagingOperation(operation, lease);
        RequireRecoverySourceBinding(operation, objectId, recoveryArtifactId, relativePath);
        var source = new SyncAuthorityCloudStageSourceEvidence(port.Provider, port.ProviderAccountId,
            port.NamespaceId, port.RootBindingId, objectId, ObjectVersionId: null, relativePath, expected.FileId,
            expected.Revision, nativeAlgorithm, expected.ContentHash!, expected.Size, recoveryArtifactId);
        source.RequireExactlyOneSourceIdentity();
        return await PrepareBoundReadAsync(authority, lease, operation, namespaceId, controlRoot, source,
            (destination, token) => port.Store.DownloadRecoveryAsync(binding, expected, destination, token),
            token => RequireExactRecoveryMemberAsync(port, binding, expected, token), cancellationToken);
    }

    /// <summary>
    /// The one exact recovery-membership revalidation used before AND after the bounded recovery read: this
    /// target adapter's own bound recovery lookup must still return the exact member whose native identity,
    /// revision, recovery path, size and digest are the retained archive's. A drift refuses the stage instead
    /// of accepting a changed copy, and a refusal produces no verified stage and mutates no target.
    /// </summary>
    private static async Task RequireExactRecoveryMemberAsync(SyncAuthorityCloudEndpointPort port,
        SyncRemoteRecoveryRootBinding binding, SyncArchiveResult expected, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(expected.RecoveryPath);
        if (string.IsNullOrWhiteSpace(name))
            throw new SyncAuthorityCloudContentStageException(
                "The retained recovery path carries no exact recovery member name.");
        var retained = await port.Store.GetRecoveryAsync(binding, name, cancellationToken);
        if (retained is null || string.IsNullOrWhiteSpace(retained.FileId) ||
            string.IsNullOrWhiteSpace(retained.Revision) || string.IsNullOrWhiteSpace(retained.ContentHash))
            throw new SyncAuthorityCloudContentStageException(
                "The bound recovery root no longer retains this exact recovery member; no verified stage is produced.");
        if (!string.Equals(retained.FileId, expected.FileId, StringComparison.Ordinal) ||
            !string.Equals(retained.Revision, expected.Revision, StringComparison.Ordinal) ||
            !string.Equals(retained.RecoveryPath, expected.RecoveryPath, StringComparison.Ordinal) ||
            retained.Size != expected.Size ||
            !string.Equals(retained.ContentHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudContentStageException(
                "The retained recovery member changed after it was recorded; the stage is refused and nothing was replaced.");
    }

    /// <summary>
    /// The one exact retained recovery source-member check: this operation must carry exactly one recovery
    /// source member, and it must name this exact object, this exact retained recovery path and this exact
    /// recovery artifact. A plan that names another artifact, another object or another path is refused
    /// before any private object or provider read.
    /// </summary>
    private static void RequireRecoverySourceBinding(SyncAuthorityOperationSnapshot operation, string objectId,
        string recoveryArtifactId, string relativePath)
    {
        var sources = operation.Members.Where(item => item.Role == "recovery").ToArray();
        if (sources.Length != 1)
            throw new SyncAuthorityCloudContentStageException(
                "The operation does not carry exactly one retained recovery source member.");
        var member = sources[0];
        if (!string.Equals(member.ObjectId, objectId, StringComparison.Ordinal) ||
            !string.Equals(member.ExpectedPathKey, relativePath, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The retained recovery source member does not name this exact object and retained recovery path.");
        if (!MemberNamesRecoveryArtifact(member.ExpectedStateJson, recoveryArtifactId))
            throw new SyncAuthorityCloudContentStageException(
                "The retained recovery source member does not name this exact recovery artifact; the stage is refused.");
    }

    private static bool MemberNamesRecoveryArtifact(string expectedStateJson, string recoveryArtifactId)
    {
        try
        {
            using var document = JsonDocument.Parse(expectedStateJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("recoveryArtifactId", out var value) &&
                   value.ValueKind == JsonValueKind.String &&
                   string.Equals(value.GetString(), recoveryArtifactId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<SyncAuthorityCloudContentStage> PrepareBoundReadAsync(SyncAuthorityDatabase authority,
        SyncAuthorityLease lease, SyncAuthorityOperationSnapshot operation, string namespaceId,
        PilotDirectoryLease controlRoot, SyncAuthorityCloudStageSourceEvidence source,
        Func<Stream, CancellationToken, Task<SyncDownloadResult>> download,
        Func<CancellationToken, Task> verifySource, CancellationToken cancellationToken)
    {
        var size = source.Size;
        var sourceHash = source.NativeHash;
        var transactionAllocation = RequireFreshAllocation(operation,
            SyncAuthorityAllocationKind.TransactionDirectory);
        var stageAllocation = RequireFreshAllocation(operation, SyncAuthorityAllocationKind.StageFile);
        RequireAllocationAuthority(transactionAllocation, stageAllocation, namespaceId);
        var transactionPath = Path.Combine(controlRoot.Path, transactionAllocation.DeterministicName);
        var stagePath = Path.Combine(transactionPath, stageAllocation.DeterministicName);
        if (Directory.Exists(transactionPath) || File.Exists(transactionPath))
            throw new SyncAuthorityCloudContentStageException(
                "The private transaction directory already exists; a pre-existing transaction is retained as evidence and is never adopted by a fresh allocation.");

        // Only the exact retained object is looked for. A directory, an unknown or cloud-native item, a
        // shortcut, a second entry at the same normalized path or any identity, revision, size or hash drift
        // refuses here, before any private object is created.
        await verifySource(cancellationToken);
        var admission = await AdmitPrivateSpaceAsync(controlRoot, size, reopeningRetained: false,
            cancellationToken);
        SyncLocalFileAuthority? stage = null;
        PilotDirectoryLease? transaction = null;
        try
        {
            await authority.TransitionAllocationAsync(transactionAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
            transaction = SyncLocalTransactionDirectory.CreateAuthorized(controlRoot,
                transactionAllocation.DeterministicName, out var securityHash);
            var transactionIdentity = SyncLocalTransactionDirectory.FormatIdentity(transaction.Identity);
            await authority.TransitionAllocationAsync(transactionAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Created, boundIdentity: transactionIdentity,
                cancellationToken: cancellationToken);
            await authority.TransitionAllocationAsync(transactionAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Bound, boundIdentity: transactionIdentity,
                contentHash: securityHash, cancellationToken: cancellationToken);
            await authority.TransitionAllocationAsync(transactionAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Verified, boundIdentity: transactionIdentity,
                contentHash: securityHash, cancellationToken: cancellationToken);

            await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
            stage = SyncLocalFileAuthority.CreateNewMovableFile(stagePath);
            var heldIdentity = stage.VerifySingleLink();
            var stageIdentity = SyncLocalTransactionDirectory.FormatIdentity(heldIdentity);
            await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Created, boundIdentity: stageIdentity,
                cancellationToken: cancellationToken);
            await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Bound, boundIdentity: stageIdentity,
                cancellationToken: cancellationToken);

            var capped = new ExactSizeWriteStream(stage.CreateGuardedWriteStream(), size);
            await using var downloadProgress = new SyncByteProgressStream(capped,
                "Downloading to verified staging", size,
                readBackPhase: "Verifying downloaded content");
            await downloadProgress.StartedAsync();
            var downloaded = await download(downloadProgress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (capped.ExactLength != size)
                throw new SyncAuthorityCloudContentStageException(
                    "The downloaded body is not exactly the reviewed object size; no verified stage is produced.");
            await stage.Stream.FlushAsync(cancellationToken);
            stage.Stream.Flush(true);
            stage.VerifySameSingleLink(heldIdentity);
            if (stage.Stream.Length != size)
                throw new SyncAuthorityCloudContentStageException(
                    "The private stage length differs from the exact reviewed source size; no verified stage is produced.");
            stage.Stream.Position = 0;
            var (nativeDigest, commonDigest) = await HashStageAsync(stage.Stream, source.NativeAlgorithm,
                cancellationToken);
            if (!string.Equals(nativeDigest, sourceHash, StringComparison.OrdinalIgnoreCase))
                throw new SyncAuthorityCloudContentStageException(
                    "The staged bytes do not match the retained provider-native source digest; no verified stage is produced.");
            if (downloaded is null || !string.Equals(downloaded.FileId, source.FileId, StringComparison.Ordinal) ||
                !string.Equals(downloaded.Revision, source.Revision, StringComparison.Ordinal) ||
                downloaded.Size != size ||
                !string.Equals(downloaded.ContentHash, sourceHash, StringComparison.OrdinalIgnoreCase))
                throw new SyncAuthorityCloudContentStageException(
                    "The download receipt does not describe the exact retained source identity, revision, size and registered native digest.");
            // The exact source is read back after the body was retained, so a root, membership, revision,
            // identity, digest or size change during the transfer can never return a verified stage and can
            // never leave the allocation durably recorded as verified either.
            await verifySource(cancellationToken);
            controlRoot.VerifyBinding();
            transaction.VerifyBinding();
            stage.VerifySameSingleLink(heldIdentity);
            await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Verified, boundIdentity: stageIdentity,
                contentHash: nativeDigest, size: size, cancellationToken: cancellationToken);

            var result = new SyncAuthorityCloudContentStage(stage, transaction, admission, lease.OperationId,
                source, commonDigest, transactionAllocation.AllocationId, stageAllocation.AllocationId,
                transactionIdentity, stageIdentity, stagePath);
            stage = null;
            transaction = null;
            return result;
        }
        catch
        {
            // Any failure retains the private object exactly as it was created: the stage is never deleted
            // here, the allocations keep their last durable state and the exact residual path is carried by
            // the refusal. Only the in-process handles and the space admission are released.
            if (stage is not null) await stage.DisposeAsync();
            transaction?.Dispose();
            admission.Dispose();
            throw;
        }
    }

    /// <summary>Returns the retained byte stream to its first byte after verifying the held identity.</summary>
    public void ResetPosition()
    {
        VerifyHeld();
        _stage.Stream.Position = 0;
    }

    /// <summary>
    /// Re-verifies that the held stage file is still the exact single-link object this stage retained and that
    /// its private transaction directory is unchanged. The stage's bytes are always read through this held
    /// handle, and the held-object DACL isolation refuses every other opener for the whole held interval, so a
    /// swapped, replaced or hardlinked path object can never be substituted for the verified output. The
    /// explicit cleanup entry re-establishes the exact path identity of the owned allocation before it deletes
    /// anything.
    /// </summary>
    public void VerifyHeld()
    {
        RequireNotDisposed();
        _transaction.VerifyBinding();
        var identity = _stage.VerifySameSingleLink(_stage.VerifySingleLink());
        if (!string.Equals(SyncLocalTransactionDirectory.FormatIdentity(identity), StageIdentity,
                StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The held stage file identity is no longer the identity this stage retained.");
    }

    /// <summary>
    /// Computes one native digest of the retained bytes under the named target provider's own registered
    /// content algorithm. The algorithm comes from the provider registry, so a cross-algorithm relabelling or
    /// comparison is impossible: an unregistered provider or algorithm is refused instead of substituted.
    /// </summary>
    public async Task<SyncAuthorityCloudStageDigest> ComputeNativeDigestAsync(string? targetProvider,
        CancellationToken cancellationToken = default)
    {
        RequireNotDisposed();
        var algorithm = SyncContentIdentity.AlgorithmFor(targetProvider);
        ResetPosition();
        var digest = await SyncContentIdentity.ComputeAsync(targetProvider, _stage.Stream, cancellationToken);
        if (!SyncContentIdentity.IdentityMatchesAlgorithm(algorithm, digest))
            throw new SyncAuthorityCloudContentStageException(
                "The target provider's native digest does not match its registered algorithm.");
        if (_stage.Stream.Length != Size)
            throw new SyncAuthorityCloudContentStageException(
                "The held stage length changed while its target digest was computed.");
        VerifyHeld();
        ResetPosition();
        return new SyncAuthorityCloudStageDigest(targetProvider ?? "", algorithm, digest, Size);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await _stage.DisposeAsync();
        }
        finally
        {
            try
            {
                _transaction.Dispose();
            }
            finally
            {
                _admission.Dispose();
            }
        }
    }

    /// <summary>
    /// The one explicit cleanup entry for a prepared cloud content stage. It deletes only this operation's
    /// exact owned stage file and its own then-empty private transaction directory, then releases each verified
    /// allocation and durably blocks any owned allocation that never reached Verified. It refuses to delete
    /// anything while the operation journal shows a target attempt that was issued but is not committed, and it
    /// never deletes a foreign, unidentified or non-empty artifact.
    /// </summary>
    internal static async Task<SyncAuthorityCloudStageCleanup> CleanupAsync(SyncAuthorityDatabase authority,
        string operationId, string workerId, SyncControlNamespaceBinding controlNamespace,
        PilotDirectoryLease controlRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(controlNamespace);
        ArgumentNullException.ThrowIfNull(controlRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var namespaceId = RequireHeldControlRoot(controlNamespace, controlRoot);
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.PairId is null)
            throw new SyncAuthorityCloudContentStageException(
                "The retained operation is not a pair operation with an owned private allocation.");
        var transactionAllocation = LoadAllocation(operation, SyncAuthorityAllocationKind.TransactionDirectory);
        var stageAllocation = LoadAllocation(operation, SyncAuthorityAllocationKind.StageFile);
        RequireAllocationAuthority(transactionAllocation, stageAllocation, namespaceId);
        var transactionPath = Path.Combine(controlRoot.Path, transactionAllocation.DeterministicName);
        var stagePath = Path.Combine(transactionPath, stageAllocation.DeterministicName);

        // Permitted journal states: the operation reached a committed cleanup boundary, or no target attempt
        // was ever issued at all. A request that was sent but is not committed retains the stage for later
        // reconciliation, because deleting it could destroy the only evidence of an unresolved effect.
        var journalState = operation.Status is SyncAuthorityOperationStatus.Committed or
            SyncAuthorityOperationStatus.CleanupPending
            ? "committed"
            : operation.Attempts.Count == 0
                ? "no-target-attempt-issued"
                : null;
        if (journalState is null)
        {
            var retained = Residuals(stagePath, transactionPath);
            return new SyncAuthorityCloudStageCleanup(operationId, false,
                SyncAuthorityNames.OperationStatus(operation.Status),
                "The operation journal shows target attempt(s) that are not committed; the stage and its private transaction are retained for later reconciliation.",
                transactionPath, stagePath, false, false, retained);
        }

        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            if (transactionAllocation.Status == SyncAuthorityAllocationStatus.Authorized &&
                stageAllocation.Status == SyncAuthorityAllocationStatus.Authorized &&
                !Directory.Exists(transactionPath) && !File.Exists(transactionPath))
            {
                // Nothing was ever created for this operation, so there is no owned object to remove and no
                // private object identity to release: both allocations stay exactly as fresh and Authorized as
                // they were, and the operation remains available for a later staged attempt.
                return new SyncAuthorityCloudStageCleanup(operationId, true, journalState,
                    "The operation created no private stage object, so both untouched fresh allocations were left Authorized and nothing was deleted.",
                    transactionPath, stagePath, false, false, Array.Empty<string>());
            }

            RequireOwned(stageAllocation);
            RequireOwned(transactionAllocation);
            if (File.Exists(transactionPath) && !Directory.Exists(transactionPath))
                throw new SyncAuthorityCloudContentStageException(
                    "The private transaction allocation path became a non-directory object and is retained as evidence.");
            var stageRemoved = false;
            var transactionRemoved = false;
            if (Directory.Exists(transactionPath))
            {
                if (transactionAllocation.BoundIdentity is null || transactionAllocation.ContentHash is null)
                    throw new SyncAuthorityCloudContentStageException(
                        "The private transaction allocation has no exact retained identity and security authority; it is retained as evidence.");
                using (var transaction = SyncLocalTransactionDirectory.OpenAuthorized(controlRoot,
                           transactionAllocation.DeterministicName, transactionAllocation.BoundIdentity,
                           transactionAllocation.ContentHash))
                {
                    if (File.Exists(stagePath))
                    {
                        if (stageAllocation.BoundIdentity is null)
                            throw new SyncAuthorityCloudContentStageException(
                                "The private stage file is present without an exact owned allocation identity; nothing is deleted.");
                        using (var held = SyncLocalFileAuthority.OpenExistingExclusive(stagePath))
                        {
                            var identity = held.VerifySingleLink();
                            if (identity != SyncLocalRecovery.ParseSourceIdentity(stageAllocation.BoundIdentity) ||
                                !string.Equals(SyncLocalTransactionDirectory.FormatIdentity(identity),
                                    stageAllocation.BoundIdentity, StringComparison.Ordinal))
                                throw new SyncAuthorityCloudContentStageException(
                                    "The private stage file identity differs from the exact owned allocation; nothing is deleted.");
                            held.DeleteOnClose();
                        }
                        stageRemoved = true;
                    }
                    if (Directory.EnumerateFileSystemEntries(transaction.Path).Any())
                        throw new SyncAuthorityCloudContentStageException(
                            "The private transaction directory retains an unexpected artifact; this operation's own empty transaction is never force-removed.");
                    transaction.DeleteOnClose();
                    transactionRemoved = true;
                }
            }
            await ReleaseOrBlockAllocationAsync(authority, stageAllocation, lease, cancellationToken);
            await ReleaseOrBlockAllocationAsync(authority, transactionAllocation, lease, cancellationToken);
            if (Directory.Exists(transactionPath) || File.Exists(transactionPath))
                throw new SyncAuthorityCloudContentStageException(
                    "The released private transaction still exists; the cleanup did not reach its exact terminal state.");
            return new SyncAuthorityCloudStageCleanup(operationId, true, journalState,
                "The owned stage and its own empty private transaction were removed; each verified allocation was released and every owned allocation that never reached Verified was durably blocked for reconciliation.",
                transactionPath, stagePath, stageRemoved, transactionRemoved, Array.Empty<string>());
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
    }

    private static IReadOnlyList<string> Residuals(string stagePath, string transactionPath)
    {
        var residuals = new List<string>();
        if (File.Exists(stagePath)) residuals.Add(stagePath);
        if (Directory.Exists(transactionPath)) residuals.Add(transactionPath);
        return residuals;
    }

    /// <summary>
    /// CC3B-3: the one narrow reopen path used by reconciliation, and only by reconciliation. It
    /// re-establishes this operation's already-created private stage from its durable allocation rows and
    /// retained object identities alone: the allocation shape, nesting, namespace authority and deterministic
    /// names must be exactly the policy's, the private transaction and stage must already exist, the stage
    /// must still be the exact single-link object its own allocation is bound to, and the bytes must still
    /// recompute the retained provider-native source digest at exactly the retained size. It never creates an
    /// artifact, never reads the cloud source (so reconciliation still works after the provider object has
    /// changed or disappeared) and never adopts a changed, unbound, released or freshly authorized stage.
    /// </summary>
    internal static async Task<SyncAuthorityCloudContentStage> ReopenVerifiedAsync(
        SyncAuthorityDatabase authority, string operationId, SyncControlNamespaceBinding controlNamespace,
        PilotDirectoryLease controlRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(controlNamespace);
        ArgumentNullException.ThrowIfNull(controlRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var namespaceId = RequireHeldControlRoot(controlNamespace, controlRoot);
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.PairId is null)
            throw new SyncAuthorityCloudContentStageException(
                "The retained operation is not a pair operation with an owned private stage.");
        var transactionAllocation = LoadAllocation(operation, SyncAuthorityAllocationKind.TransactionDirectory);
        var stageAllocation = LoadAllocation(operation, SyncAuthorityAllocationKind.StageFile);
        RequireAllocationAuthority(transactionAllocation, stageAllocation, namespaceId);
        if (stageAllocation.Status is SyncAuthorityAllocationStatus.Authorized or
            SyncAuthorityAllocationStatus.Released or SyncAuthorityAllocationStatus.Blocked)
            throw new SyncAuthorityCloudContentStageException(
                "The retained stage allocation is not an existing owned stage (" + stageAllocation.Status +
                "); a fresh, released or blocked allocation is never reopened.");
        var sourceMember = RequireSingleSourceMember(operation);
        var version = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId ??
            throw new SyncAuthorityCloudContentStageException(
                "The retained source member has no exact object version authority."), cancellationToken);
        if (version.Size is null || version.Size.Value < 0 || string.IsNullOrWhiteSpace(version.ContentHash))
            throw new SyncAuthorityCloudContentStageException(
                "The retained source version has no exact size and native content digest.");
        SyncAuthorityCloudObservationIdentity? identity;
        try
        {
            if (!SyncAuthorityCloudObservationIdentity.TryParse(version.IdentityJson, out identity) ||
                identity is null)
                throw new SyncAuthorityCloudContentStageException(
                    "The retained source version carries no accepted typed cloud observation identity; a legacy or malformed identity is refused rather than adopted.");
        }
        catch (InvalidDataException error)
        {
            throw new SyncAuthorityCloudContentStageException(
                "The retained typed cloud observation identity is refused without a read: " + error.Message);
        }
        if (string.IsNullOrWhiteSpace(identity.FileId))
            throw new SyncAuthorityCloudContentStageException(
                "The retained source identity has no stable provider file identity.");
        if (!SyncContentIdentity.SupportsIncrementalHashing(identity.ContentAlgorithm) ||
            !SyncContentIdentity.IdentityMatchesAlgorithm(identity.ContentAlgorithm, version.ContentHash))
            throw new SyncAuthorityCloudContentStageException(
                "The retained source digest does not match its registered content algorithm; it is refused rather than relabelled.");
        var size = version.Size.Value;
        var path = RequireExactRetainedPath(sourceMember);
        var transactionPath = Path.Combine(controlRoot.Path, transactionAllocation.DeterministicName);
        var stagePath = Path.Combine(transactionPath, stageAllocation.DeterministicName);
        if (!Directory.Exists(transactionPath) || File.Exists(transactionPath))
            throw new SyncAuthorityCloudContentStageException(
                "The operation's exact private transaction directory is not retained; reconciliation never creates one.");
        if (!File.Exists(stagePath))
            throw new SyncAuthorityCloudContentStageException(
                "The operation's exact private stage file is not retained; reconciliation never creates one.");
        if (transactionAllocation.BoundIdentity is null || transactionAllocation.ContentHash is null)
            throw new SyncAuthorityCloudContentStageException(
                "The private transaction allocation has no exact retained identity and security authority.");
        if (stageAllocation.BoundIdentity is null)
            throw new SyncAuthorityCloudContentStageException(
                "The private stage allocation has no exact retained identity.");
        var admission = await AdmitPrivateSpaceAsync(controlRoot, size, reopeningRetained: true,
            cancellationToken);
        SyncLocalFileAuthority? stage = null;
        PilotDirectoryLease? transaction = null;
        try
        {
            transaction = SyncLocalTransactionDirectory.OpenAuthorized(controlRoot,
                transactionAllocation.DeterministicName, transactionAllocation.BoundIdentity,
                transactionAllocation.ContentHash);
            stage = SyncLocalFileAuthority.OpenExistingExclusive(stagePath);
            var heldIdentity = stage.VerifySingleLink();
            var formatted = SyncLocalTransactionDirectory.FormatIdentity(heldIdentity);
            if (!string.Equals(formatted, stageAllocation.BoundIdentity, StringComparison.Ordinal))
                throw new SyncAuthorityCloudContentStageException(
                    "The retained private stage file identity differs from its exact owned allocation; the stage is refused rather than adopted.");
            if (stage.Stream.Length != size)
                throw new SyncAuthorityCloudContentStageException(
                    "The retained private stage length differs from the exact retained source size; the stage is refused rather than adopted.");
            if (stageAllocation.Size is not null && stageAllocation.Size.Value != size)
                throw new SyncAuthorityCloudContentStageException(
                    "The retained private stage size differs from the size recorded on its own allocation; the stage is refused rather than adopted.");
            stage.Stream.Position = 0;
            var (nativeDigest, commonDigest) = await HashStageAsync(stage.Stream, identity.ContentAlgorithm!,
                cancellationToken);
            if (!string.Equals(nativeDigest, version.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new SyncAuthorityCloudContentStageException(
                    "The retained private stage bytes no longer recompute the retained provider-native source digest; the stage is refused rather than adopted.");
            if (stageAllocation.ContentHash is not null &&
                !string.Equals(stageAllocation.ContentHash, nativeDigest, StringComparison.OrdinalIgnoreCase))
                throw new SyncAuthorityCloudContentStageException(
                    "The retained private stage bytes differ from the digest recorded on their own allocation; the stage is refused rather than adopted.");
            controlRoot.VerifyBinding();
            transaction.VerifyBinding();
            stage.VerifySameSingleLink(heldIdentity);
            stage.Stream.Position = 0;
            var source = new SyncAuthorityCloudStageSourceEvidence(identity.Provider, identity.ProviderAccountId,
                identity.NamespaceId, sourceMember.RootBindingId, version.ObjectId, version.ObjectVersionId, path,
                identity.FileId!, identity.Revision, identity.ContentAlgorithm!, nativeDigest, size);
            var result = new SyncAuthorityCloudContentStage(stage, transaction, admission, operationId, source,
                commonDigest, transactionAllocation.AllocationId, stageAllocation.AllocationId,
                transactionAllocation.BoundIdentity, formatted, stagePath);
            stage = null;
            transaction = null;
            return result;
        }
        catch
        {
            if (stage is not null) await stage.DisposeAsync();
            transaction?.Dispose();
            admission.Dispose();
            throw;
        }
    }

    private static SyncAuthorityMember RequireSingleSourceMember(SyncAuthorityOperationSnapshot operation)
    {
        var sources = operation.Members.Where(item => item.Role == "source").ToArray();
        if (sources.Length != 1)
            throw new SyncAuthorityCloudContentStageException(
                "The operation does not carry exactly one exact source member.");
        if (!string.Equals(sources[0].PairId, operation.PairId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The source member does not belong to this operation's pair.");
        return sources[0];
    }

    private static string RequireHeldControlRoot(SyncControlNamespaceBinding controlNamespace,
        PilotDirectoryLease controlRoot)
    {
        // The held root must be the exact private control namespace of this authority - never the user's
        // logical cloud endpoint and never a selected pair root.
        SyncControlNamespace.Validate(controlNamespace);
        controlRoot.VerifyBinding();
        var namespaceId = SyncControlNamespace.NormalizeNamespaceId(controlNamespace.NamespaceId);
        if (!string.Equals(Path.GetFileName(controlRoot.Path),
                SyncControlNamespace.LocalPrefix + namespaceId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The held lease is not the exact private control namespace this operation's allocations belong to.");
        return namespaceId;
    }

    private static void RequireStagingOperation(SyncAuthorityOperationSnapshot operation, SyncAuthorityLease lease)
    {
        if (!string.Equals(operation.OperationId, lease.OperationId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException("The supplied lease is not bound to this operation.");
        if (operation.Status is not (SyncAuthorityOperationStatus.Leased or
            SyncAuthorityOperationStatus.Validating or SyncAuthorityOperationStatus.AllocationAuthorized or
            SyncAuthorityOperationStatus.Staged))
            throw new SyncAuthorityCloudContentStageException(
                "The operation is not at a pre-effect private-staging boundary; its current state is " +
                operation.Status + ".");
        if (!string.Equals(operation.CurrentLeaseId, lease.LeaseId, StringComparison.Ordinal) ||
            operation.CurrentFencingToken != lease.FencingToken)
            throw new SyncAuthorityCloudContentStageException(
                "The caller does not hold the current fenced operation lease; an expired or superseded lease cannot be used or renewed here.");
    }

    private static SyncAuthorityMember RequireSourceMember(SyncAuthorityOperationSnapshot operation,
        string sourceMemberId, SyncAuthorityCloudEndpointPort port)
    {
        var sources = operation.Members.Where(item => item.Role == "source").ToArray();
        var member = sources.SingleOrDefault(item =>
            string.Equals(item.MemberId, sourceMemberId, StringComparison.Ordinal));
        if (member is null || sources.Length != 1)
            throw new SyncAuthorityCloudContentStageException(
                "The operation does not carry exactly one exact source member with the requested identity.");
        if (!string.Equals(member.PairId, operation.PairId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException("The source member does not belong to this operation's pair.");
        if (!string.Equals(member.RootBindingId, port.RootBindingId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The source member's retained root binding is not the typed cloud port's exact authority root.");
        return member;
    }

    private static string RequireExactRetainedPath(SyncAuthorityMember member)
    {
        var retained = member.ExpectedPathKey ??
                       throw new SyncAuthorityCloudContentStageException(
                           "The source member has no exact retained relative path.");
        string normalized;
        try
        {
            normalized = PilotPath.NormalizeRelative(retained);
        }
        catch (ArgumentException)
        {
            throw new SyncAuthorityCloudContentStageException(
                "The retained source path is not a safe exact relative path.");
        }
        if (!string.Equals(normalized, retained, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The retained source path is not already its exact normalized form.");
        return normalized;
    }

    private static SyncAuthorityCloudObservationIdentity RequireSourceIdentity(
        SyncAuthorityObjectVersion version, SyncAuthorityMember member, SyncAuthorityCloudEndpointPort port)
    {
        if (!string.Equals(version.ObjectId, member.ObjectId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The retained object version does not belong to the exact source member.");
        if (!string.Equals(version.Side, port.CompatibilitySide, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The retained source version belongs to another logical side than the typed cloud port.");
        if (version.Size is null || version.Size.Value < 0 || string.IsNullOrWhiteSpace(version.ContentHash))
            throw new SyncAuthorityCloudContentStageException(
                "The retained source version has no exact size and native content digest.");
        SyncAuthorityCloudObservationIdentity? identity;
        try
        {
            if (!SyncAuthorityCloudObservationIdentity.TryParse(version.IdentityJson, out identity) ||
                identity is null)
                throw new SyncAuthorityCloudContentStageException(
                    "The retained source version carries no accepted typed cloud observation identity; a legacy or " +
                    "malformed identity is refused rather than guessed or relabelled.");
        }
        catch (InvalidDataException error)
        {
            // A foreign identity kind, provider or content algorithm is refused by the one shared identity
            // reader; this stage surfaces that exact refusal instead of producing a partial stage.
            throw new SyncAuthorityCloudContentStageException(
                "The retained typed cloud observation identity is refused without a write: " + error.Message);
        }
        if (!string.Equals(identity.Provider, port.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.ProviderAccountId, port.ProviderAccountId, StringComparison.Ordinal) ||
            !string.Equals(identity.NamespaceId, port.NamespaceId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The retained source identity names another provider, provider account or native namespace than the typed cloud port.");
        if (string.IsNullOrWhiteSpace(identity.FileId))
            throw new SyncAuthorityCloudContentStageException(
                "The retained source identity has no stable provider file identity.");
        if (!string.Equals(identity.ContentAlgorithm, port.Definition.ContentHashAlgorithm,
                StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The retained source identity was not produced with the port provider's registered content algorithm.");
        if (!SyncContentIdentity.SupportsIncrementalHashing(identity.ContentAlgorithm) ||
            !SyncContentIdentity.IdentityMatchesAlgorithm(identity.ContentAlgorithm, version.ContentHash))
            throw new SyncAuthorityCloudContentStageException(
                "The retained source digest does not match its registered content algorithm; it is refused rather than relabelled.");
        if (version.Size.Value > 0 && string.IsNullOrWhiteSpace(identity.Revision))
            throw new SyncAuthorityCloudContentStageException(
                "The retained nonempty source revision is absent; there is nothing an exact download could be bound to.");
        return identity;
    }

    private static SyncAuthorityAllocation LoadAllocation(SyncAuthorityOperationSnapshot operation,
        SyncAuthorityAllocationKind kind)
    {
        var matches = operation.Allocations.Where(item => item.Kind == kind).ToArray();
        if (matches.Length != 1)
            throw new SyncAuthorityCloudContentStageException(
                "The operation does not carry exactly one " + kind + " allocation for this private stage.");
        return matches[0];
    }

    private static SyncAuthorityAllocation RequireFreshAllocation(SyncAuthorityOperationSnapshot operation,
        SyncAuthorityAllocationKind kind)
    {
        var allocation = LoadAllocation(operation, kind);
        if (allocation.Status != SyncAuthorityAllocationStatus.Authorized)
            throw new SyncAuthorityCloudContentStageException(
                "The " + kind + " allocation is not a fresh Authorized allocation; its retained state is " +
                allocation.Status + " and it fails closed for explicit reconciliation instead of being reused.");
        return allocation;
    }

    private static void RequireAllocationAuthority(SyncAuthorityAllocation transaction,
        SyncAuthorityAllocation stage, string namespaceId)
    {
        if (!string.Equals(transaction.ControlNamespaceId, namespaceId, StringComparison.Ordinal) ||
            !string.Equals(stage.ControlNamespaceId, namespaceId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "An allocation does not belong to this exact private control namespace authority.");
        if (!string.Equals(transaction.IntendedParentIdentity, namespaceId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The transaction allocation is not nested directly under this private control namespace.");
        if (!string.Equals(stage.IntendedParentIdentity, transaction.AllocationId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The stage allocation is not nested under the exact transaction allocation of this operation.");
        if (!string.Equals(stage.DeterministicName, SyncLocalTransactionDirectory.StageName,
                StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The stage allocation's deterministic name is not the existing private stage name.");
        if (!transaction.DeterministicName.StartsWith(SyncLocalTransactionDirectory.Prefix,
                StringComparison.Ordinal) ||
            transaction.DeterministicName.Length != SyncLocalTransactionDirectory.Prefix.Length + 32 ||
            transaction.DeterministicName[SyncLocalTransactionDirectory.Prefix.Length..]
                .Any(value => !char.IsAsciiHexDigit(value)) ||
            !string.Equals(Path.GetFileName(transaction.DeterministicName), transaction.DeterministicName,
                StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The transaction allocation's deterministic name is not an accepted private transaction name.");
    }

    private static void RequireOwned(SyncAuthorityAllocation allocation)
    {
        if (allocation.Status == SyncAuthorityAllocationStatus.Blocked)
            throw new SyncAuthorityCloudContentStageException(
                "The " + allocation.Kind + " allocation is blocked and is never deleted by this entry.");
    }

    private static async Task ReleaseOrBlockAllocationAsync(SyncAuthorityDatabase authority,
        SyncAuthorityAllocation allocation, SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        // The retained allocation allowlist is authoritative: a verified private object releases, an owned
        // object that never reached Verified is durably blocked for explicit reconciliation - it is never
        // reported as released - and a fresh or already terminal allocation is left exactly as it is.
        var next = allocation.Status switch
        {
            SyncAuthorityAllocationStatus.Verified => SyncAuthorityAllocationStatus.Released,
            SyncAuthorityAllocationStatus.Created or SyncAuthorityAllocationStatus.Bound =>
                SyncAuthorityAllocationStatus.Blocked,
            _ => (SyncAuthorityAllocationStatus?)null
        };
        if (next is null) return;
        await authority.TransitionAllocationAsync(allocation.AllocationId, lease, next.Value,
            cancellationToken: cancellationToken);
    }

    private static async Task FindExactSourceAsync(SyncAuthorityCloudEndpointPort port, string path,
        string fileId, string? revision, long size, string contentHash, CancellationToken cancellationToken)
    {
        await port.Store.VerifyRootIdentityAsync(cancellationToken);
        PilotRemoteEntry entry;
        try
        {
            entry = await port.Store.ReadEntryAsync(path, fileId, cancellationToken);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException)
        {
            // Native metadata and the legacy inventory fallback have the same staging
            // refusal boundary. Preserve the exact cause; never reinterpret an unavailable
            // or ambiguous lookup as absence, nor create a private stage after it fails.
            throw new SyncAuthorityCloudContentStageException(
                "The retained source could not be verified at " + path + ": " + error.Message, error);
        }
        await port.Store.VerifyRootIdentityAsync(cancellationToken);
        if (entry.Kind != PilotEntryKind.File)
            throw new SyncAuthorityCloudContentStageException(
                "The retained source path is a directory, not an ordinary supported file.");
        if (entry.TransferBlockedReason is not null)
            throw new SyncAuthorityCloudContentStageException(
                "The retained source is cloud-native, unsupported or otherwise blocked and is never staged as ordinary content: " +
                entry.TransferBlockedReason);
        if (string.IsNullOrWhiteSpace(entry.Id) ||
            !string.Equals(entry.Id, fileId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The listed source file identity differs from the exact retained native file identity.");
        if (!string.Equals(entry.Revision, revision, StringComparison.Ordinal))
            throw new SyncAuthorityCloudContentStageException(
                "The listed source revision differs from the exact retained native revision.");
        if (entry.Size != size)
            throw new SyncAuthorityCloudContentStageException(
                "The listed source size differs from the exact retained size.");
        if (entry.ContentHash is null ||
            !string.Equals(entry.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudContentStageException(
                "The listed source digest differs from the exact retained native digest.");
        await port.Store.VerifyRootIdentityAsync(cancellationToken);
    }

    private static bool MatchesPath(PilotRemoteEntry entry, string path)
    {
        try
        {
            return string.Equals(PilotPath.NormalizeRelative(entry.RelativePath), path, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<SyncRecoverySpoolAdmission.Reservation> AdmitPrivateSpaceAsync(
        PilotDirectoryLease controlRoot, long size, bool reopeningRetained, CancellationToken cancellationToken)
    {
        // The counted private-space admission for the staging volume, admitted against the actual observed free
        // space of that volume through the shipped product spool admission and its existing 64 MiB safety
        // reserve. Creating a new private stage asks for the checked twice-file-size headroom the guarded writer
        // needs to finish the reviewed object. Reopening an already-resident verified stage asks for zero
        // additional file-body bytes, because that path only opens and rehashes the exact object that already
        // exists; no branch may create, redownload or truncate a retained stage.
        long required;
        if (reopeningRetained)
        {
            if (size < 0)
                throw new SyncAuthorityCloudContentStageException(
                    "The retained private stage size is negative; no private staging space is admitted.");
            required = 0;
        }
        else
        {
            // A creation size that cannot be reserved refuses here, before any private object is created.
            if (size < 0 || size > long.MaxValue / 2)
                throw new SyncAuthorityCloudContentStageException(
                    "The reviewed source size exceeds the exact twice-size private staging bound; no stage is created.");
            try
            {
                required = checked(size * 2);
            }
            catch (OverflowException)
            {
                throw new SyncAuthorityCloudContentStageException(
                    "The reviewed source size overflows the exact twice-size private staging bound; no stage is created.");
            }
        }
        var volume = Path.GetPathRoot(controlRoot.Path) ??
                     throw new SyncAuthorityCloudContentStageException(
                         "The held private control root has no volume to observe for staging space.");
        var observed = AvailableDeviceBytesForTest is { } fixture
            ? fixture(volume)
            : new DriveInfo(volume).AvailableFreeSpace;
        if (observed < 0)
            throw new SyncAuthorityCloudContentStageException(
                "The observed staging volume free space is not a usable value.");
        if (reopeningRetained && observed < StagingSafetyReserveBytes)
            throw new SyncAuthorityCloudContentStageException(
                "The observed staging volume free space does not leave the shipped 64 MiB product safety " +
                "reserve free while reopening the retained private stage; the retained stage is refused " +
                "rather than adopted.");
        try
        {
            return await SyncRecoverySpoolAdmission.ReserveAsync(volume, required,
                Guid.NewGuid().ToString("N"), observed, StagingSafetyReserveBytes, cancellationToken);
        }
        catch (IOException error)
        {
            throw new SyncAuthorityCloudContentStageException(
                "The staging volume cannot admit the bounded private staging space: " + error.Message);
        }
    }

    private static async Task<(string Native, string Common)> HashStageAsync(Stream source, string nativeAlgorithm,
        CancellationToken cancellationToken)
    {
        // Both digests are computed by the registered seams over exactly the retained bytes in one bounded
        // pass, with no whole-file buffer, no full mirror and no provider read. The caller's own token governs
        // the whole rehash, so a cancelled rehash ends before any verified stage is produced and the retained
        // object is never rewritten, recreated or deleted.
        cancellationToken.ThrowIfCancellationRequested();
        var native = SyncContentIdentity.CreateIncrementalHasher(nativeAlgorithm);
        using var common = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        await using var progress = new SyncByteProgressStream(source, "Verifying staged file hashes");
        await progress.StartedAsync();
        long hashedBytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await progress.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            native.Append(buffer.AsSpan(0, read));
            common.AppendData(buffer, 0, read);
            hashedBytes += read;
            HashChunkObserver.Value?.Invoke(hashedBytes);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (native.FinishDigest(), Convert.ToHexString(common.GetHashAndReset()).ToLowerInvariant());
    }

    private void RequireNotDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SyncAuthorityCloudContentStage));
    }

    /// <summary>
    /// The additional byte cap over the guarded private stage writer: it refuses any byte beyond the exact
    /// reviewed object size BEFORE that byte reaches the stage, and it never allows the stage's length,
    /// position or seek target to exceed that size, so a provider that sends more than the reviewed object
    /// can never produce a verified stage.
    /// </summary>
    private sealed class ExactSizeWriteStream(Stream inner, long limit) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => RequireLength(inner.Length);

        public override long Position
        {
            get => inner.Position;
            set
            {
                RequirePosition(value);
                inner.Position = value;
            }
        }

        /// <summary>The exact number of bytes the stage now holds, refused if it exceeds the reviewed size.</summary>
        internal long ExactLength => RequireLength(inner.Length);

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin)
        {
            var start = origin switch
            {
                SeekOrigin.Begin => 0L,
                SeekOrigin.Current => inner.Position,
                SeekOrigin.End => inner.Length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            var position = checked(start + offset);
            RequirePosition(position);
            return inner.Seek(position, SeekOrigin.Begin);
        }

        public override void SetLength(long value)
        {
            RequirePosition(value);
            inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            RequireWrite(count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RequireWrite(buffer.Length);
            inner.Write(buffer);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            RequireWrite(count);
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            RequireWrite(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        private long RequireLength(long value)
        {
            if (value < 0 || value > limit)
                throw new SyncAuthorityCloudContentStageException(
                    "The private stage grew beyond the exact reviewed object size; no verified stage is produced.");
            return value;
        }

        private void RequirePosition(long value) => RequireLength(value);

        private void RequireWrite(int count)
        {
            var position = inner.Position;
            if (position < 0 || position > limit || count < 0 || count > limit - position)
                throw new SyncAuthorityCloudContentStageException(
                    "The provider body exceeds the exact reviewed object size; the excess bytes were refused before they reached the stage and no verified stage is produced.");
        }
    }
}
