using Microsoft.Data.Sqlite;
using static Massdex.SyncShark.Engine.Storage.StoreImage;

namespace Massdex.SyncShark.Engine.Storage;

internal static class SchemaUpgrade
{
    private static readonly string[] Rebuilt =
        {
            "pair",
            "source_state",
            "issue",
            "issue_member"
        };
    private static readonly string[] Retired =
        {
            "account",
            "binding_repair",
            "change_work",
            "folder_removal",
            "folder_removal_submission",
            "folder_removal_item",
            "folder_move"
        };

    internal static void Upgrade(SqliteConnection db, CancellationToken ct)
    {
        // Keep the source intact. Only the private backup image is rebuilt, and generated columns are never copied as input.
        Execute(db, "PRAGMA foreign_keys=OFF");
        using var tx = db.BeginTransaction();
        // Renaming a rebuilt parent must not retarget the kept tables' foreign keys to its old name.
        Execute(db, "PRAGMA legacy_alter_table=ON", tx);
        var counts = new[]
        {
            "pair",
            "synced_entry",
            "discovery_deleted_block",
            "journal_position"
        }.ToDictionary(t => t, t => Scalar(db, "SELECT count(*) FROM " + t, tx));
        RebuildTables(db, tx);
        CopyTables(db, tx, ct);
        CopyIssues(db, tx);
        CopyJobs(db, tx);
        // Dropped v16 change work lies behind the saved journal position and cursor; owe a full Repair so it is compared.
        Execute(db, "INSERT INTO source_state(pair_id,side,repair_reason,repair_revision) SELECT id,s.side,'upgrade',1 FROM pair CROSS JOIN (SELECT 0 AS side UNION ALL SELECT 1) s WHERE true ON CONFLICT(pair_id,side) DO UPDATE SET repair_reason=coalesce(repair_reason,excluded.repair_reason),repair_revision=repair_revision+1", tx);
        // Which Dropbox file deletion a spend consumed; a spend made before this column recorded none and stays NULL.
        Execute(db, "ALTER TABLE discovery_deleted_delta ADD COLUMN deleted_ticks INTEGER", tx);
        Finish(db, tx);
        VerifyCounts(db, tx, counts);
        Execute(db, $"PRAGMA user_version={Schema.Version}", tx);
        Verify(db, Schema.Version, tx);
        tx.Commit();
        Execute(db, "PRAGMA foreign_keys=ON");
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static List<string> Names(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        using var rd = cmd.ExecuteReader();
        var names = new List<string>();
        while (rd.Read())
            names.Add(rd.GetString(0));
        return names;
    }

    private static void RebuildTables(SqliteConnection db, SqliteTransaction tx)
    {
        Execute(db, "DROP TRIGGER invalidate_deleted_history", tx);
        foreach (var table in Rebuilt)
        {
            foreach (var i in Names(db, tx, "SELECT name FROM sqlite_schema WHERE type='index' AND tbl_name='" + table + "' AND sql IS NOT NULL"))
                Execute(db, "DROP INDEX " + Quote(i), tx);
            Execute(db, "ALTER TABLE " + table + " RENAME TO old_" + table, tx);
        }

        // Read the target DDL from a fresh in-memory schema; unchanged million-row tables and their indexes stay put.
        using (var model = Open(":memory:", SqliteOpenMode.ReadWriteCreate))
        {
            Execute(model, Schema.Create);
            using var ddl = model.CreateCommand();
            ddl.CommandText = "SELECT sql FROM sqlite_schema WHERE tbl_name IN ('pair','source_state','issue','issue_member','accepted_job') AND sql IS NOT NULL ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'index' THEN 1 ELSE 2 END";
            using var rd = ddl.ExecuteReader();
            while (rd.Read())
                Execute(db, rd.GetString(0), tx);
        }
    }

    private static void CopyTables(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        foreach (var table in Rebuilt)
        {
            ct.ThrowIfCancellationRequested();
            var cols = string.Join(",", Names(db, tx, "SELECT name FROM pragma_table_xinfo('" + table + "') WHERE hidden=0").Select(Quote));
            var where = table == "issue_member" ? " WHERE issue_id IS NOT NULL" : "";
            Execute(db, "INSERT INTO " + table + "(" + cols + ") SELECT " + cols + " FROM old_" + table + where, tx);
            if (!Equals(Scalar(db, "SELECT count(*) FROM " + table, tx), Scalar(db, "SELECT count(*) FROM old_" + table + where, tx)))
                throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, table)));
        }
    }

    private static void CopyIssues(SqliteConnection db, SqliteTransaction tx)
    {
        Execute(db, """
            INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks,dismissed)
            SELECT m.pair_id,3,'InfoLinks','','{"Items":[],"Incoming":null}',max(i.at_ticks),0 FROM issue_member m JOIN issue i USING(issue_id) WHERE m.skip_cause='SkipLink' GROUP BY m.pair_id;
            UPDATE issue_member AS m SET issue_id=(SELECT issue_id FROM issue WHERE pair_id=m.pair_id AND path='' AND cause='InfoLinks' AND kind=3) WHERE m.skip_cause='SkipLink';
            DELETE FROM issue WHERE kind=4 AND NOT EXISTS(SELECT 1 FROM issue_member m WHERE m.issue_id=issue.issue_id) AND EXISTS(SELECT 1 FROM old_issue_member m WHERE m.issue_id=issue.issue_id AND m.skip_cause='SkipLink');
            UPDATE issue AS i SET
              cause=coalesce((SELECT skip_cause FROM issue_member m WHERE m.issue_id=i.issue_id AND m.skip_cause=i.cause LIMIT 1),(SELECT skip_cause FROM issue_member m WHERE m.issue_id=i.issue_id AND m.skip_cause IS NOT NULL ORDER BY m.member_id LIMIT 1)),
              subject_json=json_object('Incoming',(SELECT CASE json_extract(subject_json,'$.Value.Incoming') WHEN 'Local' THEN 0 WHEN 'Dropbox' THEN 1 END FROM issue_member m WHERE m.issue_id=i.issue_id AND m.skip_cause IS NOT NULL AND json_extract(subject_json,'$.Value.Incoming') IS NOT NULL ORDER BY member_id LIMIT 1),'Items',json((SELECT json_group_array(json_object('Side',m.side,'Path',coalesce(json_extract(j.value,'$.Path'),m.path_key),'Id',coalesce(json_extract(j.value,'$.Id'),cast(m.identity AS text),'')))
                FROM issue_member m LEFT JOIN json_each(m.subject_json,'$.Value.Items') j ON pathkey(json_extract(j.value,'$.Path'))=m.path_key AND json_extract(j.value,'$.Side')=CASE m.side WHEN 0 THEN 'Local' ELSE 'Dropbox' END
                WHERE m.issue_id=i.issue_id AND m.skip_cause IS NOT NULL)))
              WHERE i.kind=4 AND EXISTS(SELECT 1 FROM old_issue_member o WHERE o.issue_id=i.issue_id AND o.skip_cause='SkipLink')
              AND EXISTS(SELECT 1 FROM issue_member m WHERE m.issue_id=i.issue_id AND m.skip_cause IS NOT NULL);
            UPDATE sqlite_sequence SET seq=max(seq,coalesce((SELECT seq FROM sqlite_sequence WHERE name='old_issue'),0)) WHERE name='issue';
            """, tx);
        if (!Equals(Scalar(db, "SELECT count(*) FROM old_issue_member o LEFT JOIN issue_member m USING(member_id) LEFT JOIN issue i ON m.issue_id=i.issue_id WHERE o.issue_id IS NOT NULL AND (m.member_id IS NULL OR (o.skip_cause IS NOT 'SkipLink' AND m.issue_id!=o.issue_id) OR (o.skip_cause='SkipLink' AND (i.cause IS NOT 'InfoLinks' OR i.kind!=3 OR i.pair_id!=o.pair_id)))", tx), 0L))
            throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, nameof(NoticeItem))));
    }

    private static void CopyJobs(SqliteConnection db, SqliteTransaction tx)
    {
        Execute(db, """
            INSERT INTO accepted_job(pair_id,route,job_id,launch)
            SELECT f.pair_id,'files/move_batch/check_v2',f.job_id,(SELECT json_group_array(json_object('Id',json_extract(j.value,'$[0]'),'Path',CASE WHEN f.source_path='' THEN '' ELSE f.source_path||'/' END||json_extract(j.value,'$[1]'),'Destination',CASE WHEN f.destination_path='' THEN '' ELSE f.destination_path||'/' END||json_extract(j.value,'$[1]'))) FROM json_each(f.launch) j ORDER BY j.key) FROM folder_move f WHERE f.job_id IS NOT NULL;
            INSERT INTO accepted_job(pair_id,route,job_id,launch)
            SELECT s.pair_id,'files/delete_batch/check',s.job_id,(SELECT json_group_array(json_object('Id',g.id,'Path',g.path,'Destination',NULL)) FROM (SELECT json_extract(i.expected_json,'$.Value.Id') id,i.path FROM folder_removal_item i WHERE i.submission_id=s.submission_id ORDER BY i.submission_member) g) FROM folder_removal_submission s WHERE s.phase='Accepted';
            """, tx);
        if (!Equals(Scalar(db, "SELECT count(*) FROM accepted_job a WHERE json_array_length(launch)=0 OR EXISTS(SELECT 1 FROM json_each(a.launch) j WHERE length(coalesce(json_extract(j.value,'$.Id'),''))=0 OR length(coalesce(json_extract(j.value,'$.Path'),''))=0)", tx), 0L))
            throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, nameof(AcceptedJob))));
    }

    private static void Finish(SqliteConnection db, SqliteTransaction tx)
    {
        // Reparenting may change ownership, never the number of retained physical members.
        if(!Equals(Scalar(db,"SELECT count(*) FROM issue_member",tx),Scalar(db,"SELECT count(*) FROM old_issue_member WHERE issue_id IS NOT NULL",tx)))
            throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation,(TextArg.Path,nameof(NoticeItem))));
        Execute(db, "DROP TABLE old_issue_member", tx);
        foreach (var table in Rebuilt.Where(t => t != "issue_member"))
            Execute(db, "DROP TABLE old_" + table, tx);
        foreach (var table in Retired)
            Execute(db, "DROP TABLE " + table, tx);
    }

    private static void VerifyCounts(SqliteConnection db, SqliteTransaction tx, Dictionary<string,object?> counts)
    {
        foreach (var(table, count)in counts)
            if (!Equals(count, Scalar(db, "SELECT count(*) FROM " + table, tx)))
                throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, table)));
    }
}
