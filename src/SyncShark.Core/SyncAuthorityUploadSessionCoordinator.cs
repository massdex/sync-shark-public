namespace Massdex.SyncShark;

internal sealed class SyncAuthorityUploadSessionCoordinator(
    SyncAuthorityDatabase authority,
    SyncAuthorityAllocation allocation,
    SyncAuthorityObjectVersion sourceVersion,
    SyncAuthorityLease lease)
{
    private SyncAuthorityAllocationStatus _status = allocation.Status;
    private bool _sourceAuthorityVerified;

    public async Task<SyncUploadSessionCheckpoint?> PrepareAsync(
        CancellationToken cancellationToken)
    {
        await RequireSourceAuthorityAsync(cancellationToken);
        if (_status == SyncAuthorityAllocationStatus.Authorized)
        {
            await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
            _status = SyncAuthorityAllocationStatus.Creating;
        }
        if (_status == SyncAuthorityAllocationStatus.Created)
        {
            await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Bound, boundIdentity: allocation.DeterministicName,
                cancellationToken: cancellationToken);
            _status = SyncAuthorityAllocationStatus.Bound;
        }
        if (_status is not (SyncAuthorityAllocationStatus.Creating or SyncAuthorityAllocationStatus.Bound))
            throw new InvalidOperationException(
                "The upload-session allocation is not available for a resumable transfer.");

        var retained = await authority.LoadUploadSessionCheckpointAsync(allocation.AllocationId,
            cancellationToken);
        if (retained is null) return null;
        RequireExactCheckpoint(retained);
        if (retained.Status != "active")
            throw new InvalidOperationException(
                "A completed upload-session checkpoint cannot authorize another upload attempt.");
        return new SyncUploadSessionCheckpoint(retained.SessionId, retained.ConfirmedOffset);
    }

    public async Task PersistAsync(SyncUploadSessionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await RequireSourceAuthorityAsync(cancellationToken);
        await authority.SaveUploadSessionCheckpointAsync(allocation.AllocationId, lease,
            checkpoint.SessionId, checkpoint.ConfirmedOffset, sourceVersion.ObjectVersionId,
            sourceVersion.Size!.Value, sourceVersion.ContentHash!, cancellationToken);
        await EnsureBoundAsync(cancellationToken);
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        await RequireSourceAuthorityAsync(cancellationToken);
        var retained = await authority.LoadUploadSessionCheckpointAsync(allocation.AllocationId,
            cancellationToken) ?? throw new InvalidDataException(
            "The completed large-file upload has no retained session checkpoint.");
        RequireExactCheckpoint(retained);
        if (retained.Status != "active")
            throw new InvalidDataException("The upload-session checkpoint is not active at commit.");
        await EnsureBoundAsync(cancellationToken);
        await authority.CompleteUploadSessionCheckpointAsync(allocation.AllocationId, lease,
            retained.SessionId, sourceVersion.Size!.Value, cancellationToken);
        if (_status == SyncAuthorityAllocationStatus.Bound)
        {
            await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Verified, boundIdentity: allocation.DeterministicName,
                contentHash: sourceVersion.ContentHash, size: sourceVersion.Size,
                cancellationToken: cancellationToken);
            _status = SyncAuthorityAllocationStatus.Verified;
        }
        if (_status == SyncAuthorityAllocationStatus.Verified)
        {
            await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Released, cancellationToken: cancellationToken);
            _status = SyncAuthorityAllocationStatus.Released;
        }
        if (_status != SyncAuthorityAllocationStatus.Released)
            throw new InvalidOperationException("The completed upload-session allocation was not released.");
    }

    public async Task CompleteFromReconciliationAsync(CancellationToken cancellationToken)
    {
        await RequireSourceAuthorityAsync(cancellationToken);
        var retained = await authority.LoadUploadSessionCheckpointAsync(allocation.AllocationId,
            cancellationToken) ?? throw new InvalidDataException(
            "The reconciled large-file upload has no retained session checkpoint.");
        RequireExactCheckpoint(retained);
        await EnsureBoundAsync(cancellationToken);
        if (retained.Status == "active")
            await authority.CompleteUploadSessionCheckpointAsync(allocation.AllocationId, lease,
                retained.SessionId, sourceVersion.Size!.Value, cancellationToken);
        else if (retained.Status != "completed")
            throw new InvalidDataException("The reconciled upload-session checkpoint is not completable.");
        if (_status == SyncAuthorityAllocationStatus.Bound)
        {
            await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Verified, boundIdentity: allocation.DeterministicName,
                contentHash: sourceVersion.ContentHash, size: sourceVersion.Size,
                cancellationToken: cancellationToken);
            _status = SyncAuthorityAllocationStatus.Verified;
        }
        if (_status == SyncAuthorityAllocationStatus.Verified)
        {
            await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Released, cancellationToken: cancellationToken);
            _status = SyncAuthorityAllocationStatus.Released;
        }
        if (_status != SyncAuthorityAllocationStatus.Released)
            throw new InvalidOperationException("The reconciled upload-session allocation was not released.");
    }

    private async Task EnsureBoundAsync(CancellationToken cancellationToken)
    {
        if (_status == SyncAuthorityAllocationStatus.Creating)
        {
            await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Created, boundIdentity: allocation.DeterministicName,
                contentHash: sourceVersion.ContentHash, size: sourceVersion.Size,
                cancellationToken: cancellationToken);
            _status = SyncAuthorityAllocationStatus.Created;
        }
        if (_status == SyncAuthorityAllocationStatus.Created)
        {
            await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Bound, boundIdentity: allocation.DeterministicName,
                cancellationToken: cancellationToken);
            _status = SyncAuthorityAllocationStatus.Bound;
        }
    }

    private async Task RequireSourceAuthorityAsync(CancellationToken cancellationToken)
    {
        if (allocation.Kind != SyncAuthorityAllocationKind.UploadSession ||
            sourceVersion.Side is not ("local" or "dropbox") || sourceVersion.Size is null ||
            sourceVersion.Size <= 0 ||
            string.IsNullOrWhiteSpace(sourceVersion.ContentHash))
            throw new InvalidDataException(
                "The resumable upload allocation is not bound to an exact content source.");
        if (_sourceAuthorityVerified) return;
        // The tags denote logical sides, not physical providers. Bind the immutable
        // source member and reserved target allocation instead of assuming NTFS or
        // importing Dropbox's request limit into another destination's mechanism.
        var operation = await authority.LoadOperationAsync(lease.OperationId, cancellationToken);
        var member = operation.Members.SingleOrDefault(item => item.Role == "source");
        var retainedAllocation = operation.Allocations.SingleOrDefault(item =>
            item.AllocationId == allocation.AllocationId);
        var retainedSource = await authority.LoadObjectVersionAsync(sourceVersion.ObjectVersionId,
            cancellationToken);
        if (member?.ObjectVersionId != sourceVersion.ObjectVersionId || retainedSource != sourceVersion ||
            retainedAllocation is null || retainedAllocation.Kind != allocation.Kind ||
            retainedAllocation.ControlNamespaceId != allocation.ControlNamespaceId ||
            retainedAllocation.IntendedParentIdentity != allocation.IntendedParentIdentity ||
            retainedAllocation.OwnerNonceHash != allocation.OwnerNonceHash ||
            retainedAllocation.IntendedParentIdentity != operation.Members.Single(item =>
                item.Role == "destination").RootBindingId ||
            retainedAllocation.DeterministicName != allocation.DeterministicName)
            throw new InvalidDataException(
                "The upload session differs from this operation's retained source or target allocation.");
        _sourceAuthorityVerified = true;
    }

    private void RequireExactCheckpoint(SyncAuthorityUploadSessionCheckpoint checkpoint)
    {
        if (checkpoint.OperationId != lease.OperationId ||
            checkpoint.SourceObjectVersionId != sourceVersion.ObjectVersionId ||
            checkpoint.SourceSize != sourceVersion.Size ||
            !string.Equals(checkpoint.SourceContentHash, sourceVersion.ContentHash,
                StringComparison.OrdinalIgnoreCase) || checkpoint.ConfirmedOffset < 0 ||
            checkpoint.ConfirmedOffset > sourceVersion.Size!.Value)
            throw new InvalidDataException(
                "The retained upload-session checkpoint differs from the immutable source authority.");
    }
}
