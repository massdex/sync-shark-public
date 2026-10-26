namespace Massdex.SyncShark.V2;

/// <summary>
/// What one first-sync preview resolved for a rebuildable choice, in a single object: every rebuildable folder the
/// local inventory holds (what the prepare report describes), the selection the plan actually used (outermost roots,
/// cloud-folder counterparts, and at apply the frozen paths whose markers still qualify) and the pair's cloud side.
/// <see cref="Detected"/> is populated only by a prepare; an apply never detects.
/// </summary>
internal sealed record RebuildableResolution(IReadOnlyList<RebuildableFolder> Detected,
    IReadOnlyList<RebuildableFolder> Selected, Side? Cloud)
{
    /// <summary>Nothing rebuilt: no rebuildable sides, or no rebuildable key in the choices document.</summary>
    internal static readonly RebuildableResolution None = new([], [], null);
}
