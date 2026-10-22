namespace Massdex.SyncShark.V2;

public interface IRecoveryAvailability
{
    string? RecoveryUnavailable { get; }
}

internal static class RecoverySafety
{
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
