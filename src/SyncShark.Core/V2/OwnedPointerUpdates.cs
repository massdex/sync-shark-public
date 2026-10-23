namespace Massdex.SyncShark.V2;

internal static class OwnedPointerUpdates
{
    // Only unchanged-target propagation. Conflict choices and Recovery-first replacements must
    // never borrow this path: their destination can contain independent user bytes.
    internal static bool IsMetadataUpdate(SyncAction action) => action is
    {
        Kind: ActionKind.Update, Rule: 2 or 28, Target: Side.A, RecoveryFirst: false,
        Source: { Kind: ItemKind.File, Flags: ItemFlags.None, Hash: not null, VersionId: not null },
        ExpectedTarget: { Kind: ItemKind.File, Flags: ItemFlags.OwnedPointer, Hash: not null }
    };

    /// <summary>Finds a pair cause anywhere in an exception chain. A native callback may wrap the
    /// <see cref="StoragePairException"/> thrown by the provider recheck (for example in an
    /// <see cref="AggregateException"/> or another wrapper), so the deferral must not depend on the
    /// outermost type.</summary>
    internal static StoragePairException? PairCause(Exception? error)
    {
        if (error is null) return null;
        if (error is StoragePairException pair) return pair;
        if (error is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
                if (PairCause(inner) is { } found) return found;
            return null;
        }
        return PairCause(error.InnerException);
    }
}
