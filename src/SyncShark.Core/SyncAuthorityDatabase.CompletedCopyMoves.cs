using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

// Retained evidence is a candidate, never proof of current presence. The effect
// guard additionally binds both physical files, their bytes and fresh provider
// metadata. No historical operation, synchronization point or identity is erased.
internal sealed record SyncCompletedCopyMove(
    string ObjectId, string PriorPointId, string CurrentPointId,
    string OldPath, string NewPath, string OldVersionId, string NewVersionId,
    string OldRemoteVersionId, string NewRemoteVersionId,
    string OldMembershipId, long OldMembershipGeneration,
    string NewMembershipId, long NewMembershipGeneration, string CopyOperationId);

public sealed partial class SyncAuthorityDatabase
{
    internal static Action? CompletedCopyMoveReadProgressForTest { get; set; }
    internal async Task<bool> IsOperationRetainedAsync(string operationId, CancellationToken token)
    {
        await using var connection = await OpenReadOnlyConnectionAsync(token);
        return await ScalarLongAsync(connection, null, "SELECT count(*) FROM operation WHERE operation_id=$id;",
            token, ("$id", operationId)) == 1;
    }

    internal async Task<IReadOnlyList<SyncCompletedCopyMove>> LoadCompletedCopyMovesAsync(
        string pairId, CancellationToken token)
        => await ReadCompletedCopyMovesAsync(pairId, null, token);

    internal async Task<bool> CompletedCopyMoveMatchesAsync(string pairId,
        SyncCompletedCopyMove proof, CancellationToken token)
        => (await ReadCompletedCopyMovesAsync(pairId, proof, token)).Contains(proof);

    private async Task<IReadOnlyList<SyncCompletedCopyMove>> ReadCompletedCopyMovesAsync(
        string pairId, SyncCompletedCopyMove? expected, CancellationToken token)
    {
        await using var connection = await OpenReadOnlyConnectionAsync(token);
        var compact = await ReadCurrentCopyMovesCoreAsync(connection, null, pairId, expected, token);
        if (expected is not null && await ScalarLongAsync(connection, null,
                "SELECT count(*) FROM current_copy_completion WHERE pair_id=$pair AND object_id=$object AND coverage_verified=1;",
                token, ("$pair", pairId), ("$object", expected.ObjectId)) == 1)
            return compact;
        var legacy = await ReadLegacyCompletedCopyMovesCoreAsync(connection, null, pairId, expected, token);
        return compact.Concat(legacy).Distinct().ToArray();
    }

    private static async Task<IReadOnlyList<SyncCompletedCopyMove>> ReadLegacyCompletedCopyMovesCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string pairId,
        SyncCompletedCopyMove? expected, CancellationToken token, string? objectId = null,
        bool excludeCovered = true, int limit = -1)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText=LegacyCompletedCopyMoveSql(excludeCovered);
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$object", (object?)expected?.ObjectId ?? objectId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$prior", (object?)expected?.PriorPointId ?? DBNull.Value);
        command.Parameters.AddWithValue("$current", (object?)expected?.CurrentPointId ?? DBNull.Value);
        command.Parameters.AddWithValue("$copy", (object?)expected?.CopyOperationId ?? DBNull.Value);
        return await ReadWithNativeCancellationAsync(connection, async () =>
        {
            var result = new List<SyncCompletedCopyMove>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetInt64(10),
                    reader.GetString(11), reader.GetInt64(12), reader.GetString(13)));
            return (IReadOnlyList<SyncCompletedCopyMove>)result;
        }, token, CompletedCopyMoveReadProgressForTest);
    }

    // Shared exact legacy predicate. A null pair is reserved for the source-bound
    // offline successor projection; normal production readers still bind one pair.
    private static string LegacyCompletedCopyMoveSql(bool excludeCovered)
    {
        // Materialize each unindexed member subset once. Otherwise SQLite can
        // scan the entire retained member table for every prior/current point
        // combination. The scoped guard reuses these same predicates, but never
        // enumerates every object's lineage again for each individual effect.
        // A predecessor has no schema14 table. Select this exact fixed
        // predicate before preparing SQL; do not remove it by whitespace matching.
        var coverageClause=excludeCovered ? """
            AND NOT EXISTS(SELECT 1 FROM current_copy_completion n
                WHERE n.pair_id=synchronization_point.pair_id AND n.object_id=synchronization_point.object_id
                    AND n.coverage_verified=1)
            """ : "";
        return $"""
            WITH latest AS MATERIALIZED (
                SELECT pair_id,object_id,max(authority_generation) generation FROM synchronization_point
                WHERE ($pair IS NULL OR pair_id=$pair) AND ($object IS NULL OR object_id=$object)
                    {coverageClause}
                GROUP BY pair_id,object_id
            ), sources AS MATERIALIZED (
                SELECT operation_id,object_version_id FROM operation_member
                WHERE ($pair IS NULL OR pair_id=$pair) AND role='source' AND ($copy IS NULL OR operation_id=$copy)
            ), destinations AS MATERIALIZED (
                SELECT operation_id,expected_path_key FROM operation_member
                WHERE ($pair IS NULL OR pair_id=$pair) AND role='destination' AND ($copy IS NULL OR operation_id=$copy)
            )
            SELECT DISTINCT c.object_id AS object_id,p.synchronization_point_id AS prior_point_id,
                c.synchronization_point_id AS current_point_id,a.relative_path_key AS old_path,
                b.relative_path_key AS new_path,p.local_object_version_id AS old_version_id,
                c.local_object_version_id AS new_version_id,p.dropbox_object_version_id AS old_remote_version_id,
                c.dropbox_object_version_id AS new_remote_version_id,a.membership_id AS old_membership_id,
                a.generation AS old_membership_generation,b.membership_id AS new_membership_id,
                b.generation AS new_membership_generation,op.operation_id AS copy_operation_id,c.pair_id AS pair_id
            FROM latest
            JOIN synchronization_point c ON c.pair_id=latest.pair_id AND c.object_id=latest.object_id
                AND c.authority_generation=latest.generation
            JOIN synchronization_point p ON p.pair_id=c.pair_id AND p.object_id=c.object_id
                AND p.authority_generation<c.authority_generation
            JOIN membership a ON a.membership_id=p.local_membership_id
            JOIN membership b ON b.membership_id=c.local_membership_id
            JOIN membership ar ON ar.membership_id=p.dropbox_membership_id
            JOIN membership br ON br.membership_id=c.dropbox_membership_id
            JOIN object_version bv ON bv.object_version_id=c.local_object_version_id
            JOIN operation op ON op.pair_id=c.pair_id AND op.object_id=c.object_id
                AND op.kind='create_local' AND op.status='completed'
            JOIN sources s ON s.operation_id=op.operation_id
                AND s.object_version_id=c.dropbox_object_version_id
            JOIN destinations d ON d.operation_id=op.operation_id
                AND d.expected_path_key=b.relative_path_key
            WHERE ($pair IS NULL OR c.pair_id=$pair)
                AND ($prior IS NULL OR p.synchronization_point_id=$prior)
                AND ($current IS NULL OR c.synchronization_point_id=$current)
                AND a.relative_path_key=ar.relative_path_key AND b.relative_path_key=br.relative_path_key
                AND a.relative_path_key<>b.relative_path_key
                AND a.root_binding_id=b.root_binding_id AND ar.root_binding_id=br.root_binding_id
                AND p.local_object_version_id<>c.local_object_version_id
                AND EXISTS(SELECT 1 FROM operation_attempt t JOIN effect_receipt r ON r.attempt_id=t.attempt_id
                    WHERE t.operation_id=op.operation_id AND r.certainty='known_success'
                      AND json_extract(r.destination_identity_json,'$.identity')=bv.identity_json
                      AND json_extract(r.destination_identity_json,'$.path')=b.relative_path_key)
            ORDER BY a.relative_path_key,c.synchronization_point_id,p.authority_generation DESC LIMIT $limit;
            """;
    }

    internal async Task<bool> CompletedCopyReceiptMatchesAsync(string operationId,
        string expectedIdentityJson, string expectedPath, CancellationToken token)
    {
        await using var connection = await OpenReadOnlyConnectionAsync(token);
        await using var command = connection.CreateCommand();
        if (await ScalarLongAsync(connection, null, """
                SELECT count(*) FROM current_copy_completion
                WHERE copy_operation_id=$op AND coverage_verified=1 AND receipt_id IS NOT NULL
                    AND json_extract(destination_identity_json,'$.identity')=$identity
                    AND json_extract(destination_identity_json,'$.path')=$path;
                """, token, ("$op", operationId), ("$identity", expectedIdentityJson), ("$path", expectedPath)) == 1)
            return true;
        command.CommandText = """
            SELECT r.destination_identity_json FROM operation_attempt a
            JOIN effect_receipt r ON r.attempt_id=a.attempt_id
            JOIN operation o ON o.operation_id=a.operation_id
            WHERE a.operation_id=$operation AND o.status='completed' AND r.certainty='known_success';
            """;
        command.Parameters.AddWithValue("$operation", operationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            using var receipt = System.Text.Json.JsonDocument.Parse(reader.GetString(0));
            var value = receipt.RootElement;
            if (value.TryGetProperty("identity", out var identity) &&
                identity.GetString() == expectedIdentityJson &&
                value.TryGetProperty("path", out var path) && path.GetString() == expectedPath)
                return true;
        }
        return false;
    }
}
