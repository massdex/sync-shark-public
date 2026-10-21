namespace Massdex.SyncShark.V2;

/// <summary>Reads a persisted provider job's status; never replays its mutation.</summary>
public interface IProviderEffectReconciler
{
    /// <summary>
    /// True means applied, false explicitly rejected, null still pending or unknown. <paramref name="jobChecks"/>,
    /// when given, shares one job-status read among every entry of the same job for the duration of one reconcile
    /// pass; the per-entry outcome interpretation is unaffected.
    /// </summary>
    Task<bool?> ResolveProviderEffectAsync(SyncAction action, ProviderJobCheckCache? jobChecks, CancellationToken ct);
}
