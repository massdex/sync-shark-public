namespace Massdex.SyncShark.V2;

/// <summary>A normal checkpoint-safe exit, not a provider failure or a retry request.</summary>
public sealed class PairPausedException(string pairId) : OperationCanceledException("Pair paused; resume to continue.")
{
    public string PairId { get; } = pairId;
    internal RunReport? Report { get; set; }
}
