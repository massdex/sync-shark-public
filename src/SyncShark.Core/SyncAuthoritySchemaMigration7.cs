namespace Massdex.SyncShark;

internal static class SyncAuthoritySchemaMigration7
{
    public const int PreviousVersion = 6;

    // Additive and narrow: exactly seven non-unique indexes and nothing else. No table, column, trigger,
    // constraint or provider row is created, dropped or rewritten, so every retained row keeps its identity,
    // its values and its foreign keys byte for byte and the migration is safe to run inside the existing
    // versioned transaction with its existing rollback backup.
    //
    // The indexes hold KEYS only. The three presentation indexes extract a PairId, OperationId, Severity or
    // RunId key from the retained detail_json; they never index the full EndpointContext or any other private
    // detail payload, and each is partial on the exact event_kind/predicate so unrelated rows stay uncovered.
    public const string Sql = """
        CREATE INDEX IF NOT EXISTS history_event_occurred_utc ON history_event(occurred_utc DESC);
        CREATE INDEX IF NOT EXISTS history_event_operation_id ON history_event(operation_id) WHERE operation_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS history_event_endpoint_context_operation ON history_event(json_extract(detail_json,'$.PairId'),json_extract(detail_json,'$.OperationId')) WHERE event_kind='presentation' AND json_extract(detail_json,'$.EndpointContext') IS NOT NULL AND coalesce(json_extract(detail_json,'$.OperationId'),'')<>'';
        CREATE INDEX IF NOT EXISTS history_event_presentation_severity ON history_event(json_extract(detail_json,'$.Severity')) WHERE event_kind='presentation';
        CREATE INDEX IF NOT EXISTS history_event_presentation_run ON history_event(json_extract(detail_json,'$.RunId')) WHERE event_kind='presentation';
        CREATE INDEX IF NOT EXISTS reconciliation_decision_decided_utc ON reconciliation_decision(decided_utc DESC,reconciliation_decision_id);
        CREATE INDEX IF NOT EXISTS operation_member_operation_id ON operation_member(operation_id);
        """;
}
