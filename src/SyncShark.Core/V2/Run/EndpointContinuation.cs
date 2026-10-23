namespace Massdex.SyncShark.V2;

/// <summary>Identifies the pair and roots of one preview; it grants cache reuse, never deletion authority.</summary>
public sealed record EndpointContinuation(string PairId, string RootA, string RootB, Guid PreviewId);
