namespace Massdex.SyncShark.Engine;

/// <summary>The result of one resolve request, including exact retry-all completion counts.</summary>
public sealed record ResolveSummary(SyncOutcome Outcome, bool Resolved, RepairSummary? Repair)
{
    public int Retried { get; init; }
    public int Succeeded { get; init; }
}
