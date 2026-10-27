namespace Massdex.SyncShark.V2;

internal sealed record PrepareHeader(string PairId, string Generation, string MetadataJson, string Digest);
