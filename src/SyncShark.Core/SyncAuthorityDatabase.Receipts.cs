namespace Massdex.SyncShark;

internal sealed record SyncAuthorityEffectReceipt(string Certainty, string SourceIdentityJson,
    string DestinationIdentityJson);

public sealed partial class SyncAuthorityDatabase
{
    // Read the latest receipt for the exact retained attempt, never a receipt from another operation.
    // Keeping this query with the journal avoids provider executors opening their own database handles.
    internal async Task<SyncAuthorityEffectReceipt?> LoadLatestEffectReceiptAsync(string operationId,
        string attemptId, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(operationId, nameof(operationId));
        RequireIdentifier(attemptId, nameof(attemptId));
        await using var connection = await OpenReadOnlyConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.certainty,r.source_identity_json,r.destination_identity_json
            FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id
            WHERE a.operation_id=$operation AND a.attempt_id=$attempt
            ORDER BY r.observed_utc DESC,r.rowid DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$operation", operationId);
        command.Parameters.AddWithValue("$attempt", attemptId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SyncAuthorityEffectReceipt(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }
}
