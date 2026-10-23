using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed record SyncActionJournalStatus(long RetainedActions, long EncodedBytes,
    long SafetyHeldActions, long SafetyHeldEncodedBytes, bool AccountingPending,
    bool Warning, long RetiredActions, string? RetainedFromUtc)
{
    public string Describe() =>
        $"Action detail: {RetainedActions:N0} measured actions, {EncodedBytes:N0} encoded bytes; " +
        $"{SafetyHeldActions:N0} unclosed actions hold {SafetyHeldEncodedBytes:N0} bytes. " +
        (AccountingPending ? "Older action accounting is pending. " : "") +
        (Warning ? "Retained safety evidence exceeds the warning level. Measurement and size alone do not pause synchronization." : "");
}

public sealed partial class SyncAuthorityDatabase
{
    internal long ActionJournalWarningBytesForTest { get; set; } = 67_108_864;

    public async Task<SyncActionJournalStatus> LoadActionJournalStatusAsync(string? pairId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await LoadActionJournalStatusCoreAsync(connection, null, pairId, cancellationToken);
    }

    private async Task<SyncActionJournalStatus> LoadActionJournalStatusCoreAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string? pairId, CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        // Aggregates are updated in the same SQLite transaction as every journal mutation.
        // The indexed pending flag reports incomplete measurement; it grants or
        // refuses no effect authority and is never a fixed storage-policy gate.
        query.CommandText = pairId is null ? """
            SELECT journal_count,journal_bytes,journal_held_count,journal_held_bytes,
                EXISTS(SELECT 1 FROM operation WHERE journal_accounted=0),
                action_retired_count,(SELECT min(created_utc) FROM operation)
            FROM installation;
            """ : """
            SELECT p.journal_count,p.journal_bytes,p.journal_held_count,
                p.journal_held_bytes+i.journal_global_held_bytes,
                EXISTS(SELECT 1 FROM operation WHERE journal_accounted=0 AND (pair_id=$pair OR pair_id IS NULL)),
                i.action_retired_count,(SELECT min(created_utc) FROM operation WHERE pair_id=$pair)
            FROM sync_pair p,installation i WHERE p.pair_id=$pair;
            """;
        query.Parameters.AddWithValue("$pair", (object?)pairId ?? DBNull.Value);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException("The journal scope is not retained.");
        var bytes = reader.GetInt64(3);
        var pending = reader.GetInt64(4) != 0;
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), bytes, pending,
            bytes >= ActionJournalWarningBytesForTest, reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    // Pre-v15 rows stay explicitly unmeasured until visited. New and changed accounted
    // graphs use fixed delta triggers. No full journal scan on startup or effect admission.
    private static async Task MeasurePredecessorActionJournalCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, Stopwatch timer, CancellationToken token)
    {
        for (var count = 0; count < 200 && timer.ElapsedMilliseconds < 50; count++)
        {
            var id = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM operation WHERE journal_accounted=0 LIMIT 1;", token);
            if (id is null) return;
            long bytes = 0;
            foreach (var spec in SyncActionJournalSchema.Tables)
            {
                bytes = checked(bytes + await ScalarLongAsync(connection, transaction,
                    $"SELECT coalesce(sum({SyncActionJournalSchema.Charge(spec.Columns, "")}),0) " +
                    $"FROM {spec.Table} WHERE {ActionGraphTables.Single(x => x.Table == spec.Table).Predicate};",
                    token, ("$id", id)));
            }
            await ExecuteAsync(connection, transaction, """
                UPDATE operation SET journal_encoded_bytes=$bytes,journal_accounted=1
                WHERE operation_id=$id AND journal_accounted=0;
                """, token, ("$bytes", bytes), ("$id", id));
        }
    }
}
