namespace Massdex.SyncShark.V2;

/// <summary>Run-owned reconciliation lifetime; durable markers remain the restart authority.</summary>
internal sealed class ActiveReconciliation(StateMonitorDecision decision, string session)
{
    internal StateMonitorDecision Decision { get; } = decision;
    internal string Session { get; } = session;
    internal Dictionary<string, string> GapEpochs { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> ListingBases { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> CompletedSelectionRoots { get; } = new(StringComparer.Ordinal);
    internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Selecting { get; set; } = true;
    internal RunProgress? LiveProgress { get; set; }
}
