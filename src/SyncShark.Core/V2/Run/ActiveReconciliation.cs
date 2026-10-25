namespace Massdex.SyncShark.V2;

/// <summary>Run-owned reconciliation lifetime; durable markers remain the restart authority.</summary>
internal sealed class ActiveReconciliation(StateMonitorDecision decision, string session)
{
    internal StateMonitorDecision Decision { get; } = decision;
    internal string Session { get; } = session;
    internal bool Selecting { get; set; } = true;
    internal RunProgress? LiveProgress { get; set; }
}
