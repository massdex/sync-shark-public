namespace Massdex.SyncShark.V2;

/// <summary>Explicit diagnostic/admission options for one run; absent options preserve ordinary execution.</summary>
public sealed record RunOptions
{
    internal ReconcileBatch? Batch { get; init; }
    internal bool AutoSyncFreshDelta { get; init; }
    public string? EffectsExportPath { get; init; }
    public long? MaxPlannedActions { get; init; }
}
