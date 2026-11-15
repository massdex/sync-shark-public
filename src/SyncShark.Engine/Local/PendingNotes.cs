namespace Massdex.SyncShark.Engine.Local;

/// <summary>
/// This pair's pending-note and folder-removal member port. The mutation gate is decided once here and
/// covers every durable write the local side can make, so a caller cannot hand the side an unguarded delegate.
/// </summary>
public sealed class PendingNotes(IStateStore store, string pairId, int pageSize, bool mutation)
{
    internal void Arm(PendingEffect value)
    {
        RequireMutation();
        store.ArmPending(pairId, value);
    }

    internal void Advance(PendingEffect expected, PendingEffect next)
    {
        RequireMutation();
        store.AdvancePending(pairId, expected, next);
    }

    internal PendingRead Read() => store.Pending(pairId);

    internal FolderRemovalItem? ReadMember(StageAllocation allocation) =>
        store.FolderRemovalItems(pairId, allocation.OperationId, allocation.Ordinal - 1, pageSize)
            .FirstOrDefault(item => item.Ordinal == allocation.Ordinal);

    internal void AdvanceMember(FolderRemovalItem expected, FolderRemovalItem next)
    {
        RequireMutation();
        store.SettleFolderRemovalItem(pairId, expected.OperationId, expected.Ordinal, expected.Generation,
            next, StateChange.Empty);
    }

    // A preview or observation registration must never persist pending state.
    private void RequireMutation()
    {
        if (!mutation) throw new InvalidOperationException();
    }
}
