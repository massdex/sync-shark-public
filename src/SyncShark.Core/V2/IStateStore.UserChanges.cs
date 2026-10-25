namespace Massdex.SyncShark.V2;

public partial interface IStateStore
{
    /// <summary>Existing deferred user-change classification, keyed by entity rather than path.</summary>
    bool IsUserChange(string rootId, string objectId) => false;
    /// <summary>Bounded urgent selection from existing rehash debt; no new verification authority.</summary>
    IReadOnlyList<ItemState> UrgentRehashCandidates(string rootId, int limit, long through) => [];
}
