namespace Massdex.SyncShark.Engine;

/// <summary>How a retired folder-removal operation releases runtime responsibility: NoEffect only when
/// every member effect is proven unsent, otherwise Acknowledged.</summary>
public enum FolderRetirementDisposition { Acknowledged, NoEffect }

internal static class FolderRetirement
{
    private static readonly IReadOnlyList<RecoveryChoice> Abandon = Array.AsReadOnly(new[] { RecoveryChoice.AbandonIncoming });
    private static readonly IReadOnlyList<RecoveryChoice> Terminal = Array.AsReadOnly(new[]
        { RecoveryChoice.AcceptCurrent, RecoveryChoice.AbandonIncoming });

    internal static IReadOnlyList<RecoveryChoice> ItemChoices(FolderRemovalItem item) =>
        item.EffectState is FolderRemovalItem.EffectStatus.Armed or FolderRemovalItem.EffectStatus.Accepted ||
        item.Phase == FolderRemovalItem.RecoveryPhase.Settled ? [] :
        item.Stage is not null || item.StageAllocation is not null
            ? Abandon : Terminal;

    internal static bool CanAcknowledge(IEnumerable<FolderRemovalItem> items) =>
        !items.Any(BlocksAcknowledgement);

    internal static FolderRetirementDisposition For(IEnumerable<FolderRemovalItem> items) =>
        items.All(ProvenUnsent) ? FolderRetirementDisposition.NoEffect : FolderRetirementDisposition.Acknowledged;

    /// <summary>Both answers in one streaming pass, so a caller never walks the operation twice. Once the
    /// disposition is Acknowledged and acknowledgement is refused neither answer can change, so the walk stops.</summary>
    internal static (FolderRetirementDisposition Disposition, bool CanAcknowledge) Evaluate(
        IEnumerable<FolderRemovalItem> items)
    {
        var disposition = FolderRetirementDisposition.NoEffect;
        var acknowledge = true;
        foreach (var item in items)
        {
            if (!ProvenUnsent(item)) disposition = FolderRetirementDisposition.Acknowledged;
            acknowledge &= !BlocksAcknowledgement(item);
            if (disposition == FolderRetirementDisposition.Acknowledged && !acknowledge) break;
        }
        return (disposition, acknowledge);
    }

    private static bool ProvenUnsent(FolderRemovalItem item) =>
        item.EffectState is FolderRemovalItem.EffectStatus.None or
            FolderRemovalItem.EffectStatus.Ready or FolderRemovalItem.EffectStatus.NoEffect;

    private static bool BlocksAcknowledgement(FolderRemovalItem item) =>
        item.Stage is not null || item.StageAllocation is not null ||
        item.EffectState is FolderRemovalItem.EffectStatus.Armed or FolderRemovalItem.EffectStatus.Accepted ||
        item.Phase != FolderRemovalItem.RecoveryPhase.Settled &&
        item.EffectState is FolderRemovalItem.EffectStatus.Verified or FolderRemovalItem.EffectStatus.Unknown;
}
