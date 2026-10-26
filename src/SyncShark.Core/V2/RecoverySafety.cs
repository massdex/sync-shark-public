namespace Massdex.SyncShark.V2;

internal static class RecoverySafety
{
    // Local/local has no provider history. Reuse RecoveryFirst, including its durable receipt and restart rules.
    internal static SyncAction RetainLocalReplacement(SyncAction action, string? providerA, string? providerB) =>
        !action.RecoveryFirst && action.Kind is ActionKind.Update or ActionKind.CopyNew or ActionKind.KeepBoth &&
        action.ExpectedTarget is { Kind: ItemKind.File } victim &&
        (victim.Flags & (ItemFlags.Placeholder | ItemFlags.OwnedPointer)) == 0 &&
        string.Equals(providerA, ProviderIds.Local, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(providerB, ProviderIds.Local, StringComparison.OrdinalIgnoreCase)
            ? action with { RecoveryFirst = true } : action;

    // Provenance, not content equality: a historical victim may predate hashing. The archive reader still
    // verifies bytes against the receipt's hash; callers populate Source.Hash only after this binding succeeds.
    internal static bool MatchesRemovalProvenance([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] RecoveryRecord? record,
        string pairId, string removalId,
        Side side, ItemState victim) =>
        record is not null && string.Equals(record.PairId, pairId, StringComparison.Ordinal) &&
        string.Equals(record.ActionId, removalId, StringComparison.Ordinal) && record.Side == side &&
        PairPath.Same(record.Path, victim.Path) && record.Size == victim.Size &&
        !string.IsNullOrWhiteSpace(record.Hash) &&
        (victim.Hash is null || string.Equals(record.Hash, victim.Hash, StringComparison.OrdinalIgnoreCase));

    // Historical arrival authority is distinct from equality. Never write the newly learned hash into history.
    internal static bool MatchesHistoricalArrival(ItemState verified, ItemState frozen, string provider)
    {
        if (!PairPath.Same(verified.Path, frozen.Path) || verified.Kind != frozen.Kind ||
            verified.ObjectId != frozen.ObjectId) return false;
        if (frozen.Kind == ItemKind.Folder) return verified.VersionId == frozen.VersionId;
        if (string.IsNullOrWhiteSpace(verified.Hash) || verified.Size != frozen.Size) return false;
        if (frozen.Hash is not null) return Content.SameContent(frozen, verified) == true;
        if (string.Equals(provider, ProviderIds.Dropbox, StringComparison.OrdinalIgnoreCase))
            return frozen.VersionId is not null && verified.VersionId == frozen.VersionId;
        return string.Equals(provider, ProviderIds.Local, StringComparison.OrdinalIgnoreCase) &&
            verified.Mtime.UtcTicks == frozen.Mtime.UtcTicks;
    }

    internal static bool RequiresRecovery(SyncAction action) => action.RecoveryFirst ||
        action.Kind is ActionKind.Delete or ActionKind.Update or ActionKind.Move or ActionKind.KeepBoth ||
        action.ExpectedTarget is not null && action.Kind is not (ActionKind.Adopt or ActionKind.Forget or ActionKind.Hold);

    internal static string? Unavailable(IEndpointWriter a, IEndpointWriter b) =>
        (a as IRecoveryAvailability)?.RecoveryUnavailable ?? (b as IRecoveryAvailability)?.RecoveryUnavailable;

    /// <summary>
    /// True only when this action actually had bytes to archive. Folder victims are intentionally never preserved
    /// (they carry no bytes), so a RecoveryFirst folder action must never require a Recovery receipt.
    /// </summary>
    internal static bool HasPreservedFileVictim(SyncAction action) =>
        action.RecoveryFirst && action.ExpectedTarget is { Kind: ItemKind.File };

    /// <summary>
    /// The durable archive receipt a RecoveryFirst file action recorded before its effect, or null. Read-only: it
    /// never asks a writer to preserve again, so an original that reappeared remains untouched.
    /// </summary>
    internal static RecoveryRef? PreservedReceipt(IStateStore store, string pairId, SyncAction action)
    {
        if (!HasPreservedFileVictim(action)) return null;
        return store.RecoveryForAction(pairId, action.ActionId) is { } row
            ? new RecoveryRef(row.Side, row.Location, row.VersionId, row.Hash, row.Size)
            : null;
    }
}
