using System.Diagnostics;

namespace Massdex.SyncShark.V2;

/// <summary>Admission budget for one independent indexing pass, checked between whole-file proofs.</summary>
internal sealed class VerificationBudget
{
    internal const int MaximumItems = 512;
    internal const ItemFlags BlockedFlags = VerificationPolicy.BlockedFlags;
    internal const ItemFlags WriteEvidenceBlocked = VerificationPolicy.WriteEvidenceBlocked;
    internal const ItemFlags SettlementBlocked = VerificationPolicy.SettlementBlocked;
    internal static bool CanAcknowledge(string? hash, ItemFlags flags) => VerificationPolicy.CanAcknowledge(hash, flags);
    internal static bool NeedsWriteProof(ItemState state, ItemState? prior, bool written) => VerificationPolicy.NeedsWriteProof(state, prior, written);
    internal static bool NeedsWriteProof(ItemState state, Func<ItemState?> prior, bool written) => VerificationPolicy.NeedsWriteProof(state, prior, written);
    internal static bool NeedsVerification(ItemState item) => VerificationPolicy.NeedsVerification(item);
    internal static bool RequiresHash(ItemState item, NtfsFileUsn proof) => VerificationPolicy.RequiresHash(item, proof);
    internal static bool AlreadyVerified(ItemState current, ItemState expected, string hash) => VerificationPolicy.AlreadyVerified(current, expected, hash);
    internal static bool Eligible(ItemState item) => VerificationPolicy.Eligible(item);
    internal const long MaximumBytes = 64L * 1024 * 1024;
    internal static readonly TimeSpan MaximumTime = TimeSpan.FromSeconds(2);
    private readonly Func<TimeSpan> _elapsed;
    private long _bytes;
    internal int Items { get; private set; }
    private int _urgentItems;
    internal bool AdmitUrgent(bool userChange)
    {
        if (!userChange || _urgentItems >= VerificationPolicy.UrgentHashCap) return false;
        _urgentItems++;
        return true;
    }

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
