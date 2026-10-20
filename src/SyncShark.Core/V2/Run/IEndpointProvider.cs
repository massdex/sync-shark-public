namespace Massdex.SyncShark.V2;

/// <summary>Supplies the inventory reader and writer for one pair root; production wiring lives in the adapters.</summary>
public interface IEndpointProvider
{
    /// <summary>The inventory reader for one root.</summary>
    IInventorySource Inventory(RootDef root);

    /// <summary>The writer for one root.</summary>
    IEndpointWriter Writer(RootDef root);

    /// <summary>Opens this pair's endpoints before the run touches either side; a refusal here has no effect.</summary>
    Task PrepareAsync(PairDef pair, CancellationToken ct) => Task.CompletedTask;
}
