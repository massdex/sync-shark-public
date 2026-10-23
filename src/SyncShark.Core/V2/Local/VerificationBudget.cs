using System.Diagnostics;

namespace Massdex.SyncShark.V2;

/// <summary>Admission budget for one independent indexing pass, checked between whole-file proofs.</summary>
internal sealed class VerificationBudget
{
    internal const int MaximumItems = 512;
    internal const ItemFlags BlockedFlags = ItemFlags.Ignored | ItemFlags.Link | ItemFlags.Placeholder | ItemFlags.Quarantined;
    // Recording authoritative writes, attempting a read, and acknowledging settled work have different
    // authority. Keep those explicit policies together rather than accidentally sharing the wrong mask.
    internal const ItemFlags WriteEvidenceBlocked = ItemFlags.Locked | ItemFlags.Unreadable | ItemFlags.Ignored |
        ItemFlags.Link | ItemFlags.Placeholder | ItemFlags.OwnedPointer;
    internal const ItemFlags SettlementBlocked = BlockedFlags | ItemFlags.NeedsRehash | ItemFlags.Locked | ItemFlags.Unreadable;
    internal static bool CanAcknowledge(string? hash, ItemFlags flags) => hash is not null && (flags & SettlementBlocked) == 0;
    internal static bool NeedsWriteProof(ItemState state, ItemState? prior, bool written) =>
        state.Kind == ItemKind.File && (state.Flags & WriteEvidenceBlocked) == 0 && prior is not null &&
        ((prior.Flags & ItemFlags.NeedsRehash) != 0 || written && prior.Size == state.Size && prior.Mtime == state.Mtime);
    internal static string NeededSql(string alias) => $"({alias}.hash IS NULL OR ({alias}.flags & {(int)ItemFlags.NeedsRehash})!=0)";
    internal static string EligibleSql(string alias) => $"{alias}.kind={(int)ItemKind.File} AND ({alias}.flags & {(int)BlockedFlags})=0";
    internal static bool NeedsVerification(ItemState item) => item.Hash is null || (item.Flags & ItemFlags.NeedsRehash) != 0;
    internal static bool RequiresHash(ItemState item, NtfsFileUsn proof) => proof.RequiresHash || NeedsVerification(item);
    // Idempotent readback only: a concurrent owner already accepted this exact content proof.
    internal static bool AlreadyVerified(ItemState current, ItemState expected, string hash) =>
        current.Path == expected.Path && current.ObjectId == expected.ObjectId && current.Kind == ItemKind.File &&
        current.Size == expected.Size && current.Mtime == expected.Mtime && current.Flags == ItemFlags.None &&
        current.Hash is not null && Content.SameContent(current, expected with { Hash = hash }) == true;
    internal static bool Eligible(ItemState item) => item.Kind == ItemKind.File &&
        NeedsVerification(item) && (item.Flags & BlockedFlags) == 0;
    internal const long MaximumBytes = 64L * 1024 * 1024;
    internal static readonly TimeSpan MaximumTime = TimeSpan.FromSeconds(2);
    private readonly Func<TimeSpan> _elapsed;
    private long _bytes;
    internal int Items { get; private set; }

    internal VerificationBudget(Func<TimeSpan>? elapsed = null)
    {
        var start = Stopwatch.GetTimestamp();
        _elapsed = elapsed ?? (() => Stopwatch.GetElapsedTime(start));
    }

    internal bool CanStart => Items < MaximumItems &&
        (Items == 0 || _bytes < MaximumBytes && _elapsed() < MaximumTime);

    internal bool Admit(long size)
    {
        if (!CanStart || Items > 0 && size > MaximumBytes - _bytes) return false;
        Items++;
        _bytes = size > long.MaxValue - _bytes ? long.MaxValue : _bytes + Math.Max(0, size);
        return true;
    }
}
