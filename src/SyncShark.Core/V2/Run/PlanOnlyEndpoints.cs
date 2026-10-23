namespace Massdex.SyncShark.V2;

/// <summary>Read-through endpoints for isolated-store planning. No mutation reaches a provider, even if
/// a reconciliation path unexpectedly requests preservation. Inventory/hash reads retain normal semantics.</summary>
internal sealed class PlanOnlyEndpoints(IEndpointProvider inner) : IEndpointProvider
{
    internal static bool UnsupportedReconcile(SyncAction action) => action.ProviderEffect?.Kind is
        "whole-folder-group" or "whole-folder-member" or SyncDirectDownloadCheckpoint.Kind;
    public IInventorySource Inventory(RootDef root)
    {
        var inventory = inner.Inventory(root);
        if (inventory is LocalInventory local) local.ObserveOnly = true;
        return inventory;
    }
    private int _denied;
    internal bool MutationRequested => Volatile.Read(ref _denied) != 0;
    public IEndpointWriter Writer(RootDef root) => new Reader(inner.Writer(root), () => Interlocked.Increment(ref _denied));
    internal Task<bool> HasUploadSpaceAsync(RootDef root, long need, CancellationToken ct) =>
        inner.Writer(root) is DropboxWriter writer ? writer.HasUploadSpaceAsync(need, ct) : Task.FromResult(false);
    public Task PrepareAsync(PairDef pair, CancellationToken ct) => inner is ProductionEndpoints production
        ? production.OpenPlanOnlyAsync(pair, ct) : inner.PrepareAsync(pair, ct);

    private sealed class Reader(IEndpointWriter inner, Action denied) : IEndpointWriter, IProviderEffectReconciler, ISourceAbsenceProbe, IRecoveryAvailability, IRecoveryRecordReader, ILocalFreeSpaceAdmission
    {
        public string? RecoveryUnavailable => (inner as IRecoveryAvailability)?.RecoveryUnavailable;
        public Task<FreeSpaceAdmission> AdmitFreeSpaceAsync(SyncAction action, CancellationToken ct) =>
            inner is ILocalFreeSpaceAdmission admission ? admission.AdmitFreeSpaceAsync(action, ct) : Task.FromResult(FreeSpaceAdmission.Granted);
        public Task<ItemState> DescribeRecoveryAsync(RecoveryRecord recovery, CancellationToken ct) =>
            inner is LocalWriter local ? Task.FromResult(local.DescribeRecoveryReadOnly(recovery, ct)) :
            inner is DropboxWriter cloud ? ((IRecoveryRecordReader)cloud).DescribeRecoveryAsync(recovery, ct) :
            throw new NotSupportedException("This endpoint cannot describe retained recovery metadata.");
        public Task<Stream> OpenRecoveryRecordAsync(RecoveryRecord recovery, CancellationToken ct) => throw Denied();
        private InvalidOperationException Denied() { denied(); return new("Plan-only cannot modify an endpoint."); }
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct) => throw Denied();
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => throw Denied();
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => throw Denied();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw Denied();
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
            inner is DropboxWriter cloud ? cloud.ReadRevisionRecoveryAsync(victim, ct) : throw Denied();
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
        public Task<Stream> OpenReadAsync(ItemState item, CancellationToken ct) => inner.OpenReadAsync(item, ct);
        public Task<bool?> ResolveProviderEffectAsync(SyncAction action, ProviderJobCheckCache? checks, CancellationToken ct) =>
            inner is IProviderEffectReconciler reader && !reader.CanResumeProviderEffect(action)
                ? reader.ResolveProviderEffectAsync(action, checks, ct) : Task.FromResult<bool?>(null);
        public bool CanResumeProviderEffect(SyncAction action) =>
            inner is IProviderEffectReconciler reader && reader.CanResumeProviderEffect(action);
        public Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct) =>
            inner is ISourceAbsenceProbe reader ? reader.IsSourceAbsentAsync(historical, path, ct) : Task.FromResult<bool?>(null);
        public Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct) =>
            inner is ISourceAbsenceProbe reader ? reader.ObserveSourceMetadataAsync(path, ct) : inner.ObserveAsync(path, ct);
        public Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct) =>
            inner is ISourceAbsenceProbe reader ? reader.IsHistoricalSourcePresentAsync(historical, ct) : Task.FromResult<bool?>(null);
    }
}
