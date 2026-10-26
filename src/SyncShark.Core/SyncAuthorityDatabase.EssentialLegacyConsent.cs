using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    // Retained reviewed work can predate command/admission columns. Its original
    // typed group detail still names the exact run, and retries still name their
    // parent and root approval. Preserve that closure before normalizing bodies.
    private const string EssentialReviewedLineage="""
        WITH RECURSIVE reviewed(command_id) AS (
            SELECT command_request_id FROM command_request WHERE command_kind='run_pair'
                AND command_request_id IN (
        """+EssentialDirectCommands+"""
                )
            UNION
            SELECT json_extract(g.detail_json,'$.runId') FROM operation_group g
            WHERE json_extract(g.detail_json,'$.purpose')='reviewed_first_sync'
                AND g.operation_group_id IN (SELECT operation_group_id FROM operation WHERE operation_id IN (
        """+EssentialOperations+"""
                ))
            UNION
            SELECT json_extract(c.payload_json,'$.retryOfRunId') FROM command_request c
            JOIN reviewed r ON r.command_id=c.command_request_id
            WHERE json_type(c.payload_json,'$.retryOfRunId')='text'
            UNION
            SELECT json_extract(c.payload_json,'$.reviewedConsent.commandId') FROM command_request c
            JOIN reviewed r ON r.command_id=c.command_request_id
            WHERE json_type(c.payload_json,'$.reviewedConsent.commandId')='text'
        ) SELECT command_id FROM reviewed WHERE command_id IS NOT NULL
        """;
    private static string ExpandEssentialReviewedLineageSql(string sql)
    {
        if(!sql.Contains("_essential_reviewed_lineage",StringComparison.Ordinal))return sql;
        var prefix="WITH _essential_reviewed_lineage AS MATERIALIZED ("+EssentialReviewedLineage+")";
        // The copy adapter may already have its fixed WITH prefix. Compose once
        // instead of expanding the recursive ancestry into every scalar consumer.
        return sql.StartsWith("WITH ",StringComparison.Ordinal)
            ?prefix+","+sql[5..]:prefix+" "+sql;
    }
    private static async Task RequireEssentialLegacyConsentLineageAsync(SqliteConnection source,
        SqliteTransaction snapshot,CancellationToken token)
    {
        if(await ScalarLongAsync(source,snapshot,EssentialSourceSql(source,
            "SELECT count(*) FROM ("+EssentialReviewedLineage+") r LEFT JOIN command_request c "+
            "ON c.command_request_id=r.command_id WHERE c.command_request_id IS NULL OR c.command_kind<>'run_pair';"),token)!=0)
            throw new InvalidDataException("Retained reviewed work has a missing or non-run approval ancestor.");
        if(await ScalarLongAsync(source,snapshot,EssentialSourceSql(source,"""
            SELECT count(*) FROM operation_group g JOIN command_request c
                ON c.command_request_id=json_extract(g.detail_json,'$.runId')
            WHERE json_extract(g.detail_json,'$.purpose')='reviewed_first_sync'
                AND g.operation_group_id IN (SELECT operation_group_id FROM operation WHERE operation_id IN (
            """+EssentialOperations+"""
                )) AND (json_extract(g.detail_json,'$.pairId') IS NOT c.pair_id OR EXISTS(
                    SELECT 1 FROM operation o WHERE o.operation_group_id=g.operation_group_id AND o.pair_id IS NOT c.pair_id));
            """),token)!=0)
            throw new InvalidDataException("Legacy reviewed group and its original command differ in pair identity.");
    }
}
