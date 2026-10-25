using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

// Capacity is normal backpressure. It neither marks an item failed nor grants
// permission to discard retained work, uncertain outcomes or recovery authority.
public sealed class SyncAuthorityWorkWindowFullException : InvalidOperationException
{
    internal SyncAuthorityWorkWindowFullException() : base(
        "Waiting for retained work to drain before planning more files.") { }
}

public sealed partial class SyncAuthorityDatabase
{
    internal const int MaximumOrdinaryWorkWindow = 256;
    private const string OccupiedWorkWindowSql = """
        SELECT count(*) FROM (
            SELECT operation_id FROM operation INDEXED BY operation_work_window
            WHERE status IN ('planned','ready','leased','validating','allocation_authorized',
                'staged','effect_issuing','effect_observed','reconciling','verifying','committed')
            LIMIT 256
        );
        """;

    internal async Task<int> AvailableWorkWindowAsync(CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token);
        return MaximumOrdinaryWorkWindow - checked((int)await ScalarLongAsync(
            connection, null, OccupiedWorkWindowSql, token));
    }

    private static async Task RequireWorkWindowSlotAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncAuthorityCommandAdmission admission, CancellationToken token)
    {
        // Recovery/control allocation must still be able to resolve already-retained
        // effects. All existing active records, including legacy ones, conservatively
        // consume ordinary capacity; uncertain/cleanup-only outcomes stay separately held.
        if (admission.Purpose != SyncAuthorityCommandPurpose.NormalEffect) return;
        if (await ScalarLongAsync(connection, transaction, OccupiedWorkWindowSql, token) >=
            MaximumOrdinaryWorkWindow)
            throw new SyncAuthorityWorkWindowFullException();
    }
}
