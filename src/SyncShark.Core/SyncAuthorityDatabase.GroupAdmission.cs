using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    // Half-written planning passes never admit new effects. Prior uncertain effects
    // must be reconciled before another pass can change the same group's admission.
    public async Task SuspendOperationGroupForPlanningAsync(string operationGroupId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(operationGroupId, nameof(operationGroupId));
        await WriteAsync(async (connection, transaction) =>
        {
            var unsafeMembers = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM operation o WHERE o.operation_group_id=$id AND
                  (o.status IN ('effect_issuing','effect_unknown','effect_observed','reconciling','verifying','committed','cleanup_pending')
                   OR EXISTS(SELECT 1 FROM lease l WHERE l.scope_id=o.operation_id AND l.scope_kind='operation'
                             AND l.status='active' AND julianday(l.expires_utc)>julianday('now')));
                """, cancellationToken, ("$id", operationGroupId));
            if (unsafeMembers != 0)
                throw new InvalidOperationException("This plan retains an active or uncertain effect. Verify its outcome before replanning.");
            var changed = await ExecuteCountAsync(connection, transaction,
                "UPDATE operation_group SET status='planned' WHERE operation_group_id=$id AND status IN ('planned','active');",
                cancellationToken, ("$id", operationGroupId));
            if (changed != 1) throw new InvalidOperationException("The operation group is absent or terminal; it cannot admit another planning pass.");
        }, cancellationToken);
    }

    private static async Task RequireSealedOperationGroupAsync(SqliteConnection connection,
        SqliteTransaction transaction, string operationId, CancellationToken token)
    {
        var blocked = await ScalarLongAsync(connection, transaction,
            """
            SELECT count(*) FROM operation o LEFT JOIN operation_group g ON g.operation_group_id=o.operation_group_id
            WHERE o.operation_id=$id AND o.operation_group_id IS NOT NULL
              AND (g.status IS NULL OR g.status NOT IN ('active','committed'));
            """, token, ("$id", operationId));
        if (blocked != 0)
            throw new InvalidOperationException("The complete plan has not been sealed for execution. No new effect is permitted.");
    }
}
