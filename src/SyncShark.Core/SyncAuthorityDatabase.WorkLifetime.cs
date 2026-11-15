using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    // Called by the Host only after its operation workers reach their return /
    // teardown barrier. History drain is independent and may still be pending.
    internal Task CloseOrdinaryCommandIssuanceAsync(string commandId, CancellationToken token = default)
    {
        RequireIdentifier(commandId, nameof(commandId));
        return WriteAsync(async (connection, transaction) =>
        {
            if (await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM command_request WHERE command_request_id=$id;", token, ("$id", commandId)) != 1)
                throw new InvalidDataException("Ordinary issuance cannot close an absent command.");
            // A currently usable worker lease still owns its barrier. Expired
            // leases cannot dispatch and their uncertain receipts remain intact.
            if (await ScalarLongAsync(connection, transaction, """
                    SELECT count(*) FROM lease l JOIN operation o ON o.operation_id=l.scope_id
                    WHERE l.scope_kind='operation' AND l.status='active'
                      AND julianday(l.expires_utc)>julianday('now')
                      AND (l.command_request_id=$id OR o.execution_command_id=$id);
                    """, token, ("$id", commandId)) != 0)
                throw new InvalidOperationException("The command still has an active worker lease; ordinary issuance remains unclosed.");
            await ExecuteAsync(connection, transaction, """
                UPDATE command_request SET ordinary_issuance_closed_utc=coalesce(ordinary_issuance_closed_utc,$utc)
                WHERE command_request_id=$id;
                """, token, ("$id", commandId), ("$utc", Utc(DateTimeOffset.UtcNow)));
            await AdvanceOrdinaryCommandFloorCoreAsync(connection, transaction, token);
        }, token);
    }

    private static Task AdvanceOrdinaryCommandFloorCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken token) =>
        ExecuteAsync(connection, transaction, """
            UPDATE installation SET ordinary_closed_command_floor=max(ordinary_closed_command_floor,
                coalesce((SELECT min(c.admission_sequence)-1 FROM command_request c
                          WHERE c.admission_generation=installation.database_generation
                            AND c.admission_sequence IS NOT NULL
                            AND c.admission_purpose IN ('normal_effect','control_allocation')
                            AND c.status IN ('accepted','executing')
                            AND c.ordinary_issuance_closed_utc IS NULL),next_command_sequence-1));
            """, token);
}
