namespace Massdex.SyncShark.V2;

internal sealed record LocalGapResult(string Epoch, ItemState Expected, string Hash, LocalGapProof Proof, long Sequence);
