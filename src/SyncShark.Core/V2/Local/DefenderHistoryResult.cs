namespace Massdex.SyncShark.V2;

public sealed record DefenderHistoryResult(IReadOnlyList<DefenderDetection> Detections, bool Complete,
    DefenderAvailability Availability = DefenderAvailability.Unknown, bool LimitReached = false)
{
    public static DefenderHistoryResult Unknown { get; } = new([], false);
}
