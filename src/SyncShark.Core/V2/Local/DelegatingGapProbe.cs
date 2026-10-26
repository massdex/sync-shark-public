namespace Massdex.SyncShark.V2;

internal sealed class DelegatingGapProbe(ILocalGapIo io, RootDef root) : ILocalGapProbe
{
    public LocalGapProof? Probe(ItemState item) => io.Probe(root, item);
    public void Dispose() { }
}
