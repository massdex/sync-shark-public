using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    // Display history is not effect evidence. Attempts, receipts and baselines
    // stay authoritative; only a verified closure may later collect those.
    private async Task AppendOperationLifecycleAsync(SqliteConnection connection,
        SqliteTransaction transaction, string operationId, string eventKind,
        string detailJson, string occurredUtc, string historyId, CancellationToken token)
    {
        string? pairId;
        string commandId, kind, status;
        long revision;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT o.pair_id,coalesce(o.execution_command_id,o.origin_command_id),o.kind,o.status,
                       o.configuration_revision
                FROM operation o JOIN command_request c
                  ON c.command_request_id=coalesce(o.execution_command_id,o.origin_command_id)
                WHERE o.operation_id=$id AND o.registration_sequence IS NOT NULL
                  AND c.admission_sequence IS NOT NULL;
                """;
            query.Parameters.AddWithValue("$id", operationId);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                // Legacy/unmapped authority stays exactly held, without a
                // retrospective conversion or inference about old effects.
                await reader.DisposeAsync();
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO history_event(retention_class,rowid,history_event_id,operation_id,
                        event_kind,detail_json,occurred_utc)
                    VALUES('safety',(SELECT next_history_sequence FROM installation),
                        $history,$operation,$kind,$detail,$utc);
                    """, token, ("$history", historyId), ("$operation", operationId),
                    ("$kind", eventKind), ("$detail", detailJson), ("$utc", occurredUtc));
                return;
            }
            pairId = reader.IsDBNull(0) ? null : reader.GetString(0);
            commandId = reader.GetString(1);
            kind = reader.GetString(2);
            status = reader.GetString(3);
            revision = reader.GetInt64(4);
        }

        string path = "<operation>", itemKind = "unknown";
        long? size = null;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT m.expected_path_key,lo.object_kind,
                    (SELECT v.size_bytes FROM operation_member sm
                     JOIN object_version v ON v.object_version_id=sm.object_version_id
                     WHERE sm.operation_id=$id ORDER BY
                        CASE sm.role WHEN 'source' THEN 0 WHEN 'affected' THEN 1 ELSE 2 END LIMIT 1)
                FROM operation_member m JOIN logical_object lo ON lo.object_id=m.object_id
                WHERE m.operation_id=$id
                ORDER BY CASE m.role WHEN 'affected' THEN 0 WHEN 'source' THEN 1 ELSE 2 END LIMIT 1;
                """;
            query.Parameters.AddWithValue("$id", operationId);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                path = reader.IsDBNull(0) ? "<operation>" : reader.GetString(0);
                itemKind = reader.GetString(1);
                size = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            }
        }
        async Task<string?> Root(string side) => await ScalarStringAsync(connection, transaction, """
            SELECT CASE WHEN count(DISTINCT r.display_path)=1 THEN min(r.display_path) END
            FROM operation_member m JOIN root_binding r ON r.root_binding_id=m.root_binding_id
            WHERE m.operation_id=$id AND r.side=$side;
            """, token, ("$id", operationId), ("$side", side));

        SyncEndpointContext? context = null;
        if (pairId is not null && await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$pair AND configuration_revision=$revision;",
                token, ("$pair", pairId), ("$revision", revision)) == 1 &&
            await LoadRetainedPairDefinitionAsync(connection, transaction, pairId, token) is { } pair)
        {
            var captured = SyncEndpointContext.Capture(pair);
            if (SyncEndpointContext.Validate(captured, pairId).Count == 0) context = captured;
        }
        var recoveryCheck = eventKind is "unissued_lease_recovered" or "attempt_reconciled";
        string[] categories = recoveryCheck ? [SyncActivityCategories.Sync, SyncActivityCategories.Diagnostics]
            : kind == "allocate_control" ? [SyncActivityCategories.Pairs]
            : kind is "cleanup" or "reconcile" ? [SyncActivityCategories.Diagnostics]
            : [SyncActivityCategories.Files];
        var projectedKind = eventKind == "attempt_reconciled" ? null : kind;
        var item = NormalizeActivityEvent(new SyncActivityEvent(historyId,
            DateTimeOffset.Parse(occurredUtc, System.Globalization.CultureInfo.InvariantCulture),
            categories, eventKind == "operation_completed" ? "Success" : "Information",
            eventKind == "attempt_reconciled" ? "Application" :
                kind == "allocate_control" ? "Folder pair" : ActivityItemKind(itemKind),
            ProjectionHistoryOutcome(eventKind, projectedKind),
            ProjectionHistoryEvent(eventKind, projectedKind),
            PairId: pairId, RunId: commandId, OperationId: operationId, RelativePath: path,
            EndpointContext: context,
            OperationKind: SyncEndpointActivityPresentation.KindForRetainedCode(projectedKind),
            Phase: SyncEndpointActivityPresentation.PhaseForHistoryEvent(eventKind))
        {
            OperationSnapshot = new(eventKind, status, size, await Root("local"), await Root("dropbox"))
        });
        var ordinal = await ScalarLongAsync(connection, transaction,
            "SELECT next_history_sequence FROM installation;", token);
        using var commands = new ObservationCommands(connection, transaction);
        await InsertWindowActivityAsync(commands, new Dictionary<string, string>(StringComparer.Ordinal),
            item, "ordinary", commandId, ordinal, token, producerKind: "lifecycle");
    }
}

