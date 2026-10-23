namespace Massdex.SyncShark.V2;

/// <summary>Supplies the inventory reader and writer for one pair root; production wiring lives in the adapters.</summary>
public interface IEndpointProvider
{
    /// <summary>The inventory reader for one root.</summary>
    IInventorySource Inventory(RootDef root);

    /// <summary>The writer for one root.</summary>
    IEndpointWriter Writer(RootDef root);

    /// <summary>Independent readers for a read-only review; must not replace a running sync's endpoints.</summary>
    async Task<(IInventorySource A, IInventorySource B)> OpenReviewAsync(PairDef pair, CancellationToken ct)
    {
        await PrepareAsync(pair, ct);
        return (Inventory(pair.A), Inventory(pair.B));
    }

    /// <summary>Marks the endpoints opened by this preview for explicit, one-use continuation.</summary>
    internal EndpointContinuation? CaptureContinuation(PairDef pair) => null;

    /// <summary>Reopens endpoints, reusing classification only from the identified preview when still available.</summary>
    Task PrepareAsync(PairDef pair, EndpointContinuation? continuation, CancellationToken ct) => PrepareAsync(pair, ct);

    /// <summary>Opens read-only Recovery observers without allocating any writable endpoint namespace.</summary>
    Task PrepareRecoveryReadAsync(PairDef pair, CancellationToken ct) =>
        throw new NotSupportedException("Read-only Recovery preview is not available for this endpoint provider.");

    /// <summary>Opens this pair's endpoints before the run touches either side; a refusal here has no effect.</summary>
    Task PrepareAsync(PairDef pair, CancellationToken ct) => Task.CompletedTask;
}
