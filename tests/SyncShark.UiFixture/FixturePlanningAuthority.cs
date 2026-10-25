using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// Shared fixture seeding: a fresh action must carry exact current planning authority for its exact
// object. This mirrors the production planner: use the pair's current completed scan cut on both
// sides and bind that cut's observation-set identity to the object, using the same
// ObservationSetIdentity the planner publishes. The UI fixture seeds fresh actions directly rather
// than running the planner, so it reuses the cut its own seeding already published (and seals an
// empty cut only when that side has none yet).
internal static class FixturePlanningAuthority
{
    internal static async Task<SyncAuthorityPlanningAuthority> CurrentAsync(SyncAuthorityDatabase database,
        string pairId, string localRootBindingId, string dropboxRootBindingId, string objectId,
        CancellationToken cancellationToken = default)
    {
        var configurationRevision = await ReadCurrentConfigurationRevisionAsync(database, pairId, cancellationToken);
        var (localScanId, dropboxScanId) = await LatestCompleteScansAsync(database, pairId,
            localRootBindingId, dropboxRootBindingId, cancellationToken);
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

    // The pair's exact current configuration revision is the authority's revision; reading it here
    // keeps callers from having to know which revision their earlier seeding left current.
    private static async Task<long> ReadCurrentConfigurationRevisionAsync(SyncAuthorityDatabase database,
        string pairId, CancellationToken cancellationToken)
    {
        using var connection = OpenReadOnly(database);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT configuration_revision FROM sync_pair WHERE pair_id=$pair AND state<>'retired';";
        command.Parameters.AddWithValue("$pair", pairId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
            throw new InvalidDataException("The fixture pair has no current configuration revision: " + pairId);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<(string Local, string Dropbox)> LatestCompleteScansAsync(
        SyncAuthorityDatabase database, string pairId, string localRootBindingId, string dropboxRootBindingId,
        CancellationToken cancellationToken)
    {
        using var connection = OpenReadOnly(database);
        await connection.OpenAsync(cancellationToken);
        async Task<string> LatestAsync(string rootBindingId, string side)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT scan_generation_id FROM scan_generation WHERE pair_id=$pair AND root_binding_id=$root " +
                "AND side=$side AND status='complete' ORDER BY generation DESC LIMIT 1;";
            command.Parameters.AddWithValue("$pair", pairId);
            command.Parameters.AddWithValue("$root", rootBindingId);
            command.Parameters.AddWithValue("$side", side);
            return (await command.ExecuteScalarAsync(cancellationToken) as string) ?? string.Empty;
        }
        return (await LatestAsync(localRootBindingId, "local"),
            await LatestAsync(dropboxRootBindingId, "dropbox"));
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenReadOnly(SyncAuthorityDatabase database) =>
        new("Data Source=" + database.DatabasePath + ";Mode=ReadOnly;Pooling=False");
}
