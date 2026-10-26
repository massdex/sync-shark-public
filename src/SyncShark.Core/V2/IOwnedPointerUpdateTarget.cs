namespace Massdex.SyncShark.V2;

/// <summary>A metadata effect inside the ordinary Update intent/commit pipeline, never a transfer.</summary>
internal interface IOwnedPointerUpdateTarget
{
    Task<ItemState> UpdateOwnedPointerAsync(SyncAction action, IEndpointWriter source, CancellationToken ct);
}
