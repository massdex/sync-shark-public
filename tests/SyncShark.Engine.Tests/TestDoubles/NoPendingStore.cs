namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>A store seam for a local side that must see no pending note and no folder-removal member.</summary>
internal sealed class NoPendingStore() : ForwardingStateStore(null!)
{
    public override PendingRead Pending(string pairId) => new NoPending();
    public override void ArmPending(string pairId, PendingEffect value) { }
    public override void AdvancePending(string pairId, PendingEffect expected, PendingEffect next) { }
    public override IEnumerable<FolderRemovalItem> FolderRemovalItems(string pairId, string operationId,
        long afterOrdinal, int pageSize) => [];
    public override void SettleFolderRemovalItem(string pairId, string operationId, long ordinal,
        long expectedGeneration, FolderRemovalItem next, StateChange verifiedChange) { }
}
