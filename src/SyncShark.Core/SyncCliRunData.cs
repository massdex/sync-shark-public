namespace Massdex.SyncShark;

public sealed record SyncCliRunData(string PairId, string PairName, int Uploaded, int Downloaded, int Unchanged,
    int Conflicts, int Blocked, bool Stopped)
{
    public string? RunId { get; init; }
    public string? Outcome { get; init; }
    public long UrgentPending { get; init; }
    public int Pending { get; init; }
    public IReadOnlyDictionary<string, long> IndexingRemainingByRoot { get; init; } = new Dictionary<string, long>();
    public int Deferred { get; init; }
    public int Failed { get; init; }
    public int NotAttempted { get; init; }
    public int NeedsVerification { get; init; }
    /// <summary>Files removed on a side (core v2 keeps the run's per-kind counts, .</summary>
    public int Deleted { get; init; }
    /// <summary>Files moved on a side.</summary>
    public int Moved { get; init; }
    /// <summary>Core v2 per-run throughput measurements; absent when collection is disabled.</summary>
    public V2.RunDiagnostics? Diagnostics { get; init; }
}
