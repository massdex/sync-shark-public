namespace Massdex.SyncShark.V2;

// Test seam at the shared reader contract, never a second production content reader.
internal sealed class VerificationTestIo(Func<RootDef, ItemState, CancellationToken, Task<LocalGapHashResult>> hash) : ILocalGapIo
{
    public bool Paused => false;
    public LocalGapProof? Probe(RootDef root, ItemState item) => null;
    public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct) => hash(root, item, ct);
}
