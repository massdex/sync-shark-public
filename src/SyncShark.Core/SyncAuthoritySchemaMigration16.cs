namespace Massdex.SyncShark;

// The existing operation journal is the work window; no second queue or table.
internal static class SyncAuthoritySchemaMigration16
{
    public const int PreviousVersion = 15;
    public const string Sql = """
        CREATE INDEX IF NOT EXISTS operation_work_window
            ON operation(status,operation_id)
            WHERE status IN ('planned','ready','leased','validating','allocation_authorized',
                'staged','effect_issuing','effect_observed','reconciling','verifying','committed');
        """;
}
