namespace Massdex.SyncShark.V2;

/// <summary>One entity-wide release review; bindings are a frozen description, not per-pair consent.</summary>
public sealed record DefenderSharedRelease(string ObjectId, string Bindings, string OtherPairNames);
