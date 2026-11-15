namespace Massdex.SyncShark.V2;

/// <summary>Owns provider checkpoint reconciliation: observes jobs, or resumes explicitly supported identity-checked effects.</summary>
public interface IProviderEffectReconciler
{
    /// <summary>Whether this saved effect supports identity-checked forward recovery through this seam.</summary>
    bool CanResumeProviderEffect(SyncAction action) => false;

    /// <summary>
    /// True means applied, false explicitly rejected or superseded, null still pending or unknown.
    /// <paramref name="jobChecks"/> shares job-status reads for this reconciliation pass.
    /// </summary>
    Task<bool?> ResolveProviderEffectAsync(SyncAction action, ProviderJobCheckCache? jobChecks, CancellationToken ct);
}
