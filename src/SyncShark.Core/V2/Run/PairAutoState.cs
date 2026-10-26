namespace Massdex.SyncShark.V2;

/// <summary>One pair's continuous-sync state as the edge shows it (tray, monitor projection, run balloon).</summary>
public sealed record PairAutoState(string PairId, AutoStatus Status, NoticeReason? Reason, DateTimeOffset? RetryAt,
    RunReport? LastReport)
{
    /// <summary>The scheduled wake deadline; a provider signal can bring work forward.</summary>
    public DateTimeOffset? NextRunUtc { get; init; }
}
