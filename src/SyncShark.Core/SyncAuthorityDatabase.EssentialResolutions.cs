using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private const string EssentialResolutionLegacyColumns=
        "conflict_resolution_id,conflict_id,requested_generation,resolution_kind,command_request_id,"+
        "operation_group_id,status,requested_utc,terminal_utc";
    private const string EssentialResolutionColumns=EssentialResolutionLegacyColumns+",preserve_action_id,resolution_action_id";
    private static readonly EssentialConfigurationTable[] EssentialResolutionTables=
    [
        EssentialJournalTable("conflict_resolution",EssentialResolutionColumns,"conflict_resolution_id",
            "status NOT IN ('completed','stale','cancelled')")
    ];
    private static async Task RequireEssentialResolutionsAsync(SqliteConnection source,SqliteTransaction snapshot,CancellationToken token)
    {
        if(await ScalarLongAsync(source,snapshot,EssentialSourceSql(source,"""
            SELECT count(*) FROM conflict_resolution r
            LEFT JOIN conflict c ON c.conflict_id=r.conflict_id
            WHERE r.status NOT IN ('completed','stale','cancelled') AND
              (c.conflict_id IS NULL OR c.conflict_generation IS NOT r.requested_generation
               OR (r.operation_group_id IS NOT NULL AND NOT EXISTS
                    (SELECT 1 FROM operation_group g WHERE g.operation_group_id=r.operation_group_id AND g.kind='conflict_resolution'))
               OR (r.command_request_id IS NOT NULL AND NOT EXISTS
                    (SELECT 1 FROM command_request q WHERE q.command_request_id=r.command_request_id
                     AND (q.pair_id IS NULL OR q.pair_id=c.pair_id)))
               OR (r.preserve_action_id IS NOT NULL AND NOT EXISTS
                    (SELECT 1 FROM operation o WHERE o.operation_id=r.preserve_action_id
                     AND o.operation_group_id IS r.operation_group_id AND o.pair_id=c.pair_id))
               OR (r.resolution_action_id IS NOT NULL AND NOT EXISTS
                    (SELECT 1 FROM operation o WHERE o.operation_id=r.resolution_action_id
                     AND o.operation_group_id IS r.operation_group_id AND o.pair_id=c.pair_id)));
            """),token)!=0)
            throw new InvalidDataException("Unresolved conflict resolution requires its exact conflict generation, group, command and action bindings.");
    }
}
