namespace Massdex.SyncShark.V2;

/// <summary>Read-through endpoints for isolated-store planning. No mutation reaches a provider, even if
/// a reconciliation path unexpectedly requests preservation. Inventory/hash reads retain normal semantics.</summary>
internal sealed class PlanOnlyEndpoints(IEndpointProvider inner) : IEndpointProvider
{
    internal static bool UnsupportedReconcile(SyncAction action) => action.ProviderEffect?.Kind is
        "whole-folder-group" or "whole-folder-member" or SyncDirectDownloadCheckpoint.Kind;
    public IInventorySource Inventory(RootDef root) => inner.Inventory(root);
    private int _denied;
    internal bool MutationRequested => Volatile.Read(ref _denied) != 0;
    public IEndpointWriter Writer(RootDef root) => new Reader(inner.Writer(root), () => Interlocked.Increment(ref _denied));
    internal Task<bool> HasUploadSpaceAsync(RootDef root, long need, CancellationToken ct) =>
        inner.Writer(root) is DropboxWriter writer ? writer.HasUploadSpaceAsync(need, ct) : Task.FromResult(false);
    public Task PrepareAsync(PairDef pair, CancellationToken ct) => inner is ProductionEndpoints production
        ? production.OpenPlanOnlyAsync(pair, ct) : inner.PrepareAsync(pair, ct);

    private sealed class Reader(IEndpointWriter inner, Action denied) : IEndpointWriter, IProviderEffectReconciler, ISourceAbsenceProbe, IRecoveryAvailability
    {
        public string? RecoveryUnavailable => (inner as IRecoveryAvailability)?.RecoveryUnavailable;
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
            inner is IProviderEffectReconciler reader ? reader.ResolveProviderEffectAsync(action, checks, ct) : Task.FromResult<bool?>(null);
        public Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct) =>
            inner is ISourceAbsenceProbe reader ? reader.IsSourceAbsentAsync(historical, path, ct) : Task.FromResult<bool?>(null);
        public Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct) =>
            inner is ISourceAbsenceProbe reader ? reader.ObserveSourceMetadataAsync(path, ct) : inner.ObserveAsync(path, ct);
        public Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct) =>
            inner is ISourceAbsenceProbe reader ? reader.IsHistoricalSourcePresentAsync(historical, ct) : Task.FromResult<bool?>(null);
    }
}
