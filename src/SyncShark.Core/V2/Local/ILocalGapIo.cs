namespace Massdex.SyncShark.V2;

internal interface ILocalGapIo
{
    TimeProvider Time => TimeProvider.System;
    bool Paused { get; }
    LocalGapProof? Probe(RootDef root, ItemState item);
    ILocalGapProbe OpenProbes(RootDef root) => new DelegatingGapProbe(this, root);
    string PauseReason => Paused ? "content verification paused for activity or power" : "content verification running";
    bool Excluded(RootDef root, ItemState item) => false;
    Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct);
    Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, bool urgent, CancellationToken ct) => HashAsync(root, item, ct);
}
