namespace Massdex.SyncShark.V2;

/// <summary>One pair's opened endpoints: an inventory reader and a writer per side.</summary>
public sealed record PairEndpoints(IInventorySource InventoryA, IInventorySource InventoryB,
    IEndpointWriter WriterA, IEndpointWriter WriterB);
