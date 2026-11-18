namespace Massdex.SyncShark.Engine.Run;

/// <summary>L99 (rules 15, 22): the host's memory-only record of live refusals of exact selections (anchor and through),
/// per pair. No timer and no stored state; a host restart starts fresh from the notices and pending work.</summary>
internal sealed class RefusalRecords
{
    private readonly Dictionary<string, Dictionary<(long Anchor, long Through), (LiveAdmissionStaleReason Reason, bool Parked, bool Seen)>>
        _pairs = new(StringComparer.Ordinal);

    /// <summary>Records a refusal for reason, or with a null reason only asks; true while the selection is parked.</summary>
    internal bool Parks(string pairId, WorkSelection selection, LiveAdmissionStaleReason? reason)
    {
        var key = (selection.AnchorWorkId, selection.ThroughWorkId);
        lock (_pairs)
        {
            if (!_pairs.TryGetValue(pairId, out var refusals))
            {
                if (reason is null) return false;
                _pairs.Add(pairId, refusals = []);
            }
            var known = refusals.TryGetValue(key, out var seen);
            if (reason is not { } refused)
            {
                if (!known || !seen.Parked) return false;
                refusals[key] = seen with { Seen = true };
                return true;
            }
            // Seen marks this work pass: only a refusal kept from an earlier pass, whose fresh reads it has had, repeats.
            var parked = known && (seen.Parked || !seen.Seen && seen.Reason == refused);
            refusals[key] = (refused, parked, true);
            return parked;
        }
    }

    /// <summary>Drops the records no selection reached in this work pass: their work changed or ended.</summary>
    internal void EndPass(string pairId)
    {
        lock (_pairs)
        {
            if (!_pairs.TryGetValue(pairId, out var refusals)) return;
            foreach (var (key, value) in refusals.ToArray())
                if (value.Seen) refusals[key] = value with { Seen = false };
                else refusals.Remove(key);
            if (refusals.Count == 0) _pairs.Remove(pairId);
        }
    }

    /// <summary>Completed work, a user operation or a resume may have released what a refusal waited on: the pair
    /// starts over; a null pair (a whole-host operation) starts every pair over.</summary>
    internal void Forget(string? pairId)
    {
        lock (_pairs)
        {
            if (pairId is null) _pairs.Clear();
            else _pairs.Remove(pairId);
        }
    }
}
