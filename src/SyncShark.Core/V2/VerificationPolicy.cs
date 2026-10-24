namespace Massdex.SyncShark.V2;

internal static class VerificationPolicy
{
    internal const int UrgentHashCap = 64;
    internal const ItemFlags BlockedFlags = ItemFlags.Ignored | ItemFlags.Link | ItemFlags.Placeholder | ItemFlags.Quarantined;
    // Recording authoritative writes, attempting a read, and acknowledging settled work have different
    // authority. Keep those explicit policies together rather than accidentally sharing the wrong mask.
    internal const ItemFlags WriteEvidenceBlocked = ItemFlags.Locked | ItemFlags.Unreadable | ItemFlags.Ignored |
        ItemFlags.Link | ItemFlags.Placeholder | ItemFlags.OwnedPointer;
    internal const ItemFlags SettlementBlocked = BlockedFlags | ItemFlags.NeedsRehash | ItemFlags.Locked | ItemFlags.Unreadable;
    internal static bool CanAcknowledge(string? hash, ItemFlags flags) => hash is not null && (flags & SettlementBlocked) == 0;
    internal static bool NeedsWriteProof(ItemState state, ItemState? prior, bool written) =>
        CouldNeedWriteProof(state) && prior is not null &&
        ((prior.Flags & ItemFlags.NeedsRehash) != 0 || written && prior.Size == state.Size && prior.Mtime == state.Mtime);
    // The cheap, store-free half of the rule. Only an ordinary file can ever need a proof, so the
    // lazy overload below can skip the per-item store lookup for folders and blocked files.
    internal static bool CouldNeedWriteProof(ItemState state) =>
        state.Kind == ItemKind.File && (state.Flags & WriteEvidenceBlocked) == 0;
    internal static bool NeedsWriteProof(ItemState state, Func<ItemState?> prior, bool written) =>
        CouldNeedWriteProof(state) && NeedsWriteProof(state, prior(), written);
    internal static bool NeedsVerification(ItemState item) => item.Hash is null || (item.Flags & ItemFlags.NeedsRehash) != 0;
    internal static bool RequiresHash(ItemState item, NtfsFileUsn proof) => proof.RequiresHash || NeedsVerification(item);
    // Idempotent readback only: a concurrent owner already accepted this exact content proof.
    internal static bool AlreadyVerified(ItemState current, ItemState expected, string hash) =>
        current.Path == expected.Path && current.ObjectId == expected.ObjectId && current.Kind == ItemKind.File &&
        current.Size == expected.Size && current.Mtime == expected.Mtime && current.Flags == ItemFlags.None &&
        current.Hash is not null && Content.SameContent(current, expected with { Hash = hash }) == true;
    internal static bool Eligible(ItemState item) => item.Kind == ItemKind.File &&
        NeedsVerification(item) && (item.Flags & BlockedFlags) == 0;
}
