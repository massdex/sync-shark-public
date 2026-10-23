namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost
{
    // Prepared explicit maintenance entry points only. No automatic/default activation.
    // The same public run-admission lease excludes both preparation and an active dispatch.
    public async Task<SyncAuthorityHistoryRetentionResult> PlanAuthorityHistoryRetentionAsync(int maximumDeletedRows,
        int maximumInspectedRows, long maximumLoadedValueBytes,
        CancellationToken cancellationToken = default)
    {
        using var admission = await EnterPairConfigurationMutationAsync(cancellationToken);
        return await _authority.PlanHistoryRetentionAsync(maximumDeletedRows, maximumInspectedRows, maximumLoadedValueBytes, cancellationToken);
    }

    public async Task<SyncAuthorityHistoryRetentionResult> ExpireAuthorityHistoryAsync(int maximumDeletedRows,
        int maximumInspectedRows, long maximumLoadedValueBytes,
        CancellationToken cancellationToken = default)
    {
        using var admission = await EnterPairConfigurationMutationAsync(cancellationToken);
        return await _authority.ExpireHistoryAsync(maximumDeletedRows, maximumInspectedRows, maximumLoadedValueBytes, cancellationToken);
    }
}
