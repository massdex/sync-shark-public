namespace Massdex.SyncShark.V2;

public sealed partial class LocalInventory
{
    private IReadOnlyList<(ItemState Item, bool Urgent)> VerificationPage(ILocalGapIo io, long through)
    {
        var urgent = _known.UrgentRehashCandidates(_root.RootId, VerificationPolicy.UrgentHashCap, through)
            .Select(item => (Item: item, Urgent: true));
        var bulk = io.Paused ? Enumerable.Empty<(ItemState Item, bool Urgent)>() :
            _known.RehashCandidates(_root.RootId, VerificationBudget.MaximumItems, through, needsVerification: true)
                .Select(item => (Item: item, Urgent: false));
        // One bounded snapshot visits held rows once, without backoff or failure mutation.
        return urgent.Concat(bulk).DistinctBy(row => row.Item.ObjectId)
            .Take(VerificationBudget.MaximumItems).ToArray();
    }
}
