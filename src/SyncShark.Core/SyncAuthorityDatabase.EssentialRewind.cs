using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private const string EssentialRewindSessions="SELECT session_id FROM rewind_session WHERE status<>'resumed'";
    private const string EssentialRewindOperations=
        "SELECT operation_id FROM rewind_path_progress WHERE session_id IN ("+EssentialRewindSessions+") AND operation_id IS NOT NULL "+
        "UNION SELECT operation_id FROM operation WHERE operation_group_id IN "+
        "(SELECT operation_group_id FROM rewind_session WHERE status<>'resumed' AND operation_group_id IS NOT NULL)";
    private const string EssentialRewindScans=
        "SELECT local_checkpoint_scan_id FROM rewind_session WHERE status<>'resumed' "+
        "UNION SELECT dropbox_checkpoint_scan_id FROM rewind_session WHERE status<>'resumed' "+
        "UNION SELECT local_preview_scan_id FROM rewind_session WHERE status<>'resumed' AND local_preview_scan_id IS NOT NULL "+
        "UNION SELECT dropbox_preview_scan_id FROM rewind_session WHERE status<>'resumed' AND dropbox_preview_scan_id IS NOT NULL";
    private const string EssentialRewindHeaderColumns=
        "session_id,installation_id,connection_id,pair_id,pair_name,dropbox_root,local_checkpoint_scan_id,"+
        "dropbox_checkpoint_scan_id,dropbox_checkpoint_cursor,checkpoint_utc,updated_utc,status,"+
        "local_preview_scan_id,dropbox_preview_scan_id,preview_generation,operation_group_id,detail";
    private const string EssentialRewindPreviewColumns=
        "session_id,relative_path,relative_path_key,group_label,proposed_effect,dropbox_changed,local_changed,reason";
    private const string EssentialRewindProgressColumns=
        "session_id,relative_path,relative_path_key,status,operation_id,detail,updated_utc";
    private static readonly EssentialConfigurationTable[] EssentialRewindTables=
    [
        EssentialJournalTable("rewind_session",EssentialRewindHeaderColumns,"session_id","session_id IN ("+EssentialRewindSessions+")"),
        EssentialJournalTable("rewind_preview_item",EssentialRewindPreviewColumns,"session_id,relative_path_key","session_id IN ("+EssentialRewindSessions+")"),
        EssentialJournalTable("rewind_path_progress",EssentialRewindProgressColumns,"session_id,relative_path_key","session_id IN ("+EssentialRewindSessions+")")
    ];
    private static async Task RequireEssentialRewindAsync(SqliteConnection source,SqliteTransaction snapshot,CancellationToken token)
    {
        if(!EssentialSourceHasTable(source,"rewind_session"))return;
        if(await ScalarLongAsync(source,snapshot,"""
            SELECT count(*) FROM rewind_session w
            WHERE w.status<>'resumed' AND
              (w.installation_id IS NOT (SELECT installation_id FROM installation)
               OR (w.operation_group_id IS NOT NULL AND NOT EXISTS
                   (SELECT 1 FROM operation_group g WHERE g.operation_group_id=w.operation_group_id))
               OR NOT EXISTS(SELECT 1 FROM scan_generation s WHERE s.scan_generation_id=w.local_checkpoint_scan_id
                   AND s.pair_id=w.pair_id AND s.side='local' AND s.status='complete')
               OR NOT EXISTS(SELECT 1 FROM scan_generation s WHERE s.scan_generation_id=w.dropbox_checkpoint_scan_id
                   AND s.pair_id=w.pair_id AND s.side='dropbox' AND s.status='complete')
               OR (w.local_preview_scan_id IS NOT NULL AND NOT EXISTS
                   (SELECT 1 FROM scan_generation s WHERE s.scan_generation_id=w.local_preview_scan_id
                    AND s.pair_id=w.pair_id AND s.side='local' AND s.status='complete'))
               OR (w.dropbox_preview_scan_id IS NOT NULL AND NOT EXISTS
                   (SELECT 1 FROM scan_generation s WHERE s.scan_generation_id=w.dropbox_preview_scan_id
                    AND s.pair_id=w.pair_id AND s.side='dropbox' AND s.status='complete')));
            """,token)!=0)
            throw new InvalidDataException("Active Rewind requires its exact installation, group and complete own-pair checkpoint/preview cuts.");
        if(await ScalarLongAsync(source,snapshot,"""
            SELECT count(*) FROM rewind_path_progress p JOIN rewind_session w ON w.session_id=p.session_id
            WHERE w.status<>'resumed' AND
              (NOT EXISTS(SELECT 1 FROM rewind_preview_item i WHERE i.session_id=p.session_id
                    AND i.relative_path_key=p.relative_path_key AND i.relative_path=p.relative_path)
               OR (p.operation_id IS NOT NULL AND NOT EXISTS
                   (SELECT 1 FROM operation o WHERE o.operation_id=p.operation_id AND o.pair_id=w.pair_id
                    AND o.operation_group_id IS w.operation_group_id)));
            """,token)!=0)
            throw new InvalidDataException("Active Rewind progress differs from its exact reviewed path or operation.");
    }
}
