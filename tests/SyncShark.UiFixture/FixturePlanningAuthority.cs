using System.Threading;
using System.Threading.Tasks;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// Shared fixture seeding: a fresh action must carry exact current planning authority for its exact
// object. This mirrors the production planner: use the pair's current completed scan cut on both
// sides and bind that cut's observation-set identity to the object, using the same
// ObservationSetIdentity the planner publishes. The UI fixture seeds fresh actions directly rather
// than running the planner, so the caller passes the configuration revision and scan cut its own
// seeding already published (and an empty cut id seals one only when that side has none yet).
internal static class FixturePlanningAuthority
{
    internal static async Task<SyncAuthorityPlanningAuthority> CurrentAsync(SyncAuthorityDatabase database,
        string pairId, long configurationRevision, string localRootBindingId, string dropboxRootBindingId,
        string localScanId, string dropboxScanId, string objectId,
        CancellationToken cancellationToken = default)
    {
        if (localScanId.Length == 0)
        {
            var local = await database.BeginScanGenerationAsync(pairId, localRootBindingId, "local", "manual",
                "{\"scope\":\"complete\"}", cancellationToken);
            await database.CompleteScanGenerationAsync(local, cancellationToken);
            localScanId = local.ScanGenerationId;
        }
        if (dropboxScanId.Length == 0)
        {
            var remote = await database.BeginScanGenerationAsync(pairId, dropboxRootBindingId, "dropbox", "manual",
                "{\"scope\":\"complete\"}", cancellationToken);
            await database.CompleteScanGenerationAsync(remote, cancellationToken);
            dropboxScanId = remote.ScanGenerationId;
        }
        var set = await database.LoadPlanningObservationSetAsync(pairId, configurationRevision,
            localRootBindingId, dropboxRootBindingId, localScanId, dropboxScanId, cancellationToken);
        return set.Bind(SyncAuthorityReconciliationPlanner.ObservationSetIdentity(set), objectId);
    }
}
