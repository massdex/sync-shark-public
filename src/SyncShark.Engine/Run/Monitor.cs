namespace Massdex.SyncShark.Engine.Run;

/// <summary>Starts or records Repair for lost continuity; commits a fresh source base and its affected Repair debts together.</summary>
public static class Monitor
{
    /// <summary>Normal contiguous advancement does not request Repair; use the atomic source-reset operation only for lost continuity.</summary>
    public static Task ObserveAsync(PairConfig pair, ISide side, IStore store, CancellationToken ct) => throw new NotImplementedException();
}
