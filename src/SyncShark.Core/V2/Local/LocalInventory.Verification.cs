namespace Massdex.SyncShark.V2;

public sealed partial class LocalInventory
{
    private (ItemState? Item, bool Urgent) NextVerificationItem(ILocalGapIo io, long through, ref int urgentCount)
    {
        var urgent = urgentCount < VerificationPolicy.UrgentHashCap
            ? _known.UrgentRehashCandidates(_root.RootId, 1, through) : [];
        if (urgent.Count > 0)
        {
            urgentCount++;
            return (urgent[0], true);
        }
        return (io.Paused ? null : _known.RehashCandidates(_root.RootId, 1, through, needsVerification: true).FirstOrDefault(), false);
    }
}
