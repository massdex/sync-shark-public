namespace Massdex.SyncShark.V2;

/// <summary>One joined identity: its baseline, the current sides and the evidence.</summary>
public sealed record PlanRow(Baseline? Base, ItemState? A, ItemState? B, Evidence Ev)
{
    /// <summary>Ambiguous Windows spelling; neither endpoint may be changed by an inferred match.</summary>
    public bool NameMappingCollision { get; init; }
}
