namespace Massdex.SyncShark.Engine;

/// <summary>One finite, immutable selection of acquired change work: a pair at a generation, one side's source
/// epoch, and the work IDs from AnchorWorkId through ThroughWorkId that the anchor owns. It names a selection,
/// not a materialized receipt collection; membership and its predicates are solely ChangeTable's responsibility.</summary>
public sealed record WorkSelection(string PairId, long PairGeneration, long SourceEpoch, Side Side,
    string Domain, string? Identity, string Key, long AnchorWorkId, long ThroughWorkId)
{
    /// <summary>The observed peer travels with the selection's commit.</summary>
    public SelectionPeer? Peer { get; init; }

    /// <summary>The anchor's canonical obligation (StateEncoding.ObligationKey): a residual's as Storage stored it, an
    /// acquired anchor's as Storage would register its notice. Set only by Storage; it tells same-path
    /// responsibilities apart.</summary>
    internal string? Obligation { get; init; }

    /// <summary>Refuses a selection that is not this pair at this generation with the existing stale refusal.</summary>
    internal void RequireFor(string pairId, long generation)
    {
        if (PairId != pairId || PairGeneration != generation)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
    }
}
