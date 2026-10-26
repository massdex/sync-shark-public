namespace Massdex.SyncShark.V2;

public partial interface IStateStore
{
    /// <summary>Atomically publish validated content and retire only its exact entity's hash debt.</summary>
    bool CompleteRehash(string rootId, ItemState expected, string hash);
    /// <summary>Queue revision captured before content IO; completion must reject a newer obligation.</summary>
    long RehashRevision(string rootId, string objectId);
    bool CompleteIndexingRehash(string rootId, ItemState expected, string hash, long revision);
    IReadOnlyList<ItemState> RehashCandidates(string rootId, int limit, long through, bool? needsVerification);
    bool HasPendingRehashVerification(string rootId);
    long RehashPendingCount(string rootId);
    bool FailIndexingRehash(string rootId, ItemState item, long revision, string reason);
    /// <summary>Retire only scheduling for an excluded/classified item; never publish a hash or clear protection flags.</summary>
    bool RetireIndexingRehash(string rootId, ItemState item, long revision);
    void RecordIndexFailure(string rootId, string reason);
    /// <summary>CAS an exact review observation into protected indexing/planning debt; never advance coverage or cursor.</summary>
    bool DeferIndexObservation(string rootId, Side side, ItemState observed, ItemState? expectedPrior, string reason);

}
