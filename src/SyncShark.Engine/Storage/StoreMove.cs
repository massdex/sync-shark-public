using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;
/// <summary>Chooses the sole eligible release image and switches closed, verified stores without consuming the predecessor.</summary>
public sealed class StoreMove
{
    private const string Backup = "state.v16.db";
    private static string StoreFile(string root) => Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.DatabaseFile);
    private static bool Occupied(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try
        {
            db.Open();
            db.CreateFunction<string, string>("pathkey", PathNames.Key, isDeterministic: true);
            db.CreateFunction<string, int>("plandepth", PathNames.Depth, isDeterministic: true);
            db.CreateFunction<string, int>("localaccessretrycause", cause => cause is nameof(TextKey.FailLocalInUse) or nameof(TextKey.FailLocalInUseOrAccessDenied) ? 1 : 0, isDeterministic: true);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static object? Scalar(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd.ExecuteScalar();
    }

    private static void Execute(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    private static void Checkpoint(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var rd = cmd.ExecuteReader();
        if (!rd.Read() || rd.GetInt32(0) != 0)
            throw new IOException(Text.Get(TextKey.FailLocalInUse, (TextArg.Path, db.DataSource)));
    }

    private static void Verify(SqliteConnection db, int version, SqliteTransaction? tx = null)
    {
        if (!Equals(Scalar(db, "PRAGMA user_version", tx), (long)version) || !Equals(Scalar(db, "PRAGMA integrity_check", tx), "ok") || Scalar(db, "PRAGMA foreign_key_check", tx) != null)
            throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, db.DataSource)));
        if (version == Schema.Version)
            ValidateShape(db, tx);
    }

    private static void ValidateShape(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT p.def_json,s.local_published_usn,h.last_key,j.next_usn,m.skip_cause,a.launch FROM pair p,synced_entry s,discovery_deleted_block h,journal_position j,issue_member m,accepted_job a LIMIT 0";
        using var reader = cmd.ExecuteReader();
    }

    private static int Version(string path)
    {
        using var db = Open(path, SqliteOpenMode.ReadOnly);
        var v = Convert.ToInt32(Scalar(db, "PRAGMA user_version"));
        if (v != 16 && v != Schema.Version)
            throw new InvalidDataException(Text.Get(v < 16 ? TextKey.StateSchemaTooOld : TextKey.StateSchemaTooNew, (TextArg.Path, path)));
        return v;
    }

    private static List<string> Required(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT json_extract(def_json,'$.ConnectionId') FROM pair";
        using var rd = cmd.ExecuteReader();
        var ids = new List<string>();
        while (rd.Read())
        {
            if (rd.IsDBNull(0) || rd.GetString(0).Length == 0)
                throw new InvalidDataException(Text.Get(TextKey.StateRootBindingInvalid));
            ids.Add(rd.GetString(0));
        }

        return ids;
    }

    private static string Stage(string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var path = target + "." + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N") + ".new";
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return path;
    }

    private static void Closed(string path)
    {
        if (Occupied(path + "-wal") || Occupied(path + "-shm") || Occupied(path + "-journal"))
            throw new IOException(Text.Get(TextKey.FailLocalInUse, (TextArg.Path, path)));
    }

    private static void RemoveStage(string path)
    {
        File.Delete(path);
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
        File.Delete(path + "-journal");
    }

    /// <summary>Publish the closed verified v17 stage, upgrading v16 in one transaction; Schema.Create only builds an empty schema.</summary>
    public Task MoveAsync(string predecessorRoot, string productRoot, Func<string, string, IReadOnlyList<string>, Action?> verifyConnections, CancellationToken ct) => MoveWithOutcomeAsync(predecessorRoot, productRoot, verifyConnections, ct);
    /// <summary>Refusals before publication throw; after publication a pointer-cleanup warning allows pairs to keep running.</summary>
    public Task<string?> MoveWithOutcomeAsync(string predecessorRoot, string productRoot, Func<string, string, IReadOnlyList<string>, Action?> verifyConnections, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(productRoot));
        var target = StoreFile(root);
        var backup = Path.Combine(Path.GetDirectoryName(target)!, Backup);
        var pointer = Path.Combine(predecessorRoot, "product-root.txt");
        var sourceRoot = root;
        bool hasPointer = Occupied(pointer);
        if (hasPointer)
        {
            var lines = File.ReadAllLines(pointer);
            if (lines.Length != 1 || !Path.IsPathFullyQualified(lines[0]))
                throw new InvalidDataException(Text.Get(TextKey.StateRootBindingInvalid));
            sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(lines[0]));
        }

        bool external = !string.Equals(root, sourceRoot, StringComparison.OrdinalIgnoreCase);
        var externalPath = StoreFile(sourceRoot);
        var hasTarget = Occupied(target);
        var hasBackup = Occupied(backup);
        var version = hasTarget ? Version(target) : 0;
        if (hasBackup && Version(backup) != 16 || external && (!Occupied(externalPath) || Version(externalPath) != 16 || hasBackup))
            throw new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked, (TextArg.Place, externalPath)));
        if (version == Schema.Version)
        {
            using var published = Open(target, SqliteOpenMode.ReadOnly);
            ValidateShape(published);
            if (hasPointer)
            {
                Verify(published, Schema.Version);
                // Only this invocation knows it published an external source. After restart, two
                // independently occupied stores can have identical connections, even identical data.
                if(external)throw new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked,(TextArg.Place,externalPath)));
                verifyConnections(sourceRoot, root, Required(published));
            }

            return Task.FromResult(RemovePointer());
        }

        if (hasTarget && (external || hasBackup))
            throw new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked, (TextArg.Place, target)));
        if (!hasTarget && !hasBackup && !external)
        {
            if (hasPointer)
                throw new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked, (TextArg.Place, pointer)));
            Closed(target);
            return Task.FromResult<string?>(null);
        }

        var source = external ? externalPath : hasBackup ? backup : target;
        if (!hasTarget)
            Closed(target);
        var stage = Stage(target);
        Action? cleanup = null;
        bool publishedStage = false, replacing = false;
        try
        {
            List<string> ids;
            using (var original = Open(source, SqliteOpenMode.ReadWrite))
            using (var next = Open(stage, SqliteOpenMode.ReadWrite))
            {
                Checkpoint(original);
                Verify(original, 16);
                ids = Required(original);
                ct.ThrowIfCancellationRequested();
                original.BackupDatabase(next);
                Upgrade(next, ct);
                Checkpoint(next);
                Checkpoint(original);
            }

            Closed(stage);
            Closed(source);
            ct.ThrowIfCancellationRequested();
            cleanup = verifyConnections(sourceRoot, root, ids);
            ct.ThrowIfCancellationRequested();
            if (source == target)
            {
                replacing = true;
                File.Replace(stage, target, backup);
            }
            else
            {
                Closed(target);
                File.Move(stage, target, false);
            }

            publishedStage = true;
        }
        catch
        {
            if (replacing && !Occupied(target) && Occupied(backup))
                File.Move(backup, target, false);
            cleanup?.Invoke();
            throw;
        }
        finally
        {
            if (!publishedStage && (!replacing || Occupied(target)))
                RemoveStage(stage);
        }

        return Task.FromResult(RemovePointer());
        string? RemovePointer()
        {
            if (hasPointer)
                try
                {
                    File.Delete(pointer);
                }
                catch (Exception e)when (e is IOException or UnauthorizedAccessException)
                {
                    return e.Message;
                }

            return null;
        }
    }

    /// <summary>Create a verified empty database for the first explicit pair add, without replacing an existing file.</summary>
    public Task CreateAsync(string productRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = StoreFile(productRoot);
        if (Occupied(target) || Occupied(Path.Combine(Path.GetDirectoryName(target)!, Backup)) || Occupied(Path.Combine(productRoot, "product-root.txt")))
            throw new IOException(Text.Get(TextKey.ClashNameTaken, (TextArg.Path, target)));
        Closed(target);
        var stage = Stage(target);
        try
        {
            using (var db = Open(stage, SqliteOpenMode.ReadWrite))
            {
                Execute(db, Schema.Pragmas);
                using (var tx = db.BeginTransaction())
                {
                    Execute(db, Schema.Create, tx);
                    Execute(db, $"PRAGMA user_version={Schema.Version}", tx);
                    Verify(db, Schema.Version, tx);
                    tx.Commit();
                }

                Checkpoint(db);
            }

            Closed(stage);
            ct.ThrowIfCancellationRequested();
            Closed(target);
            File.Move(stage, target, false);
        }
        finally
        {
            RemoveStage(stage);
        }

        return Task.CompletedTask;
    }

    private static void Upgrade(SqliteConnection db, CancellationToken ct)
    {
        // Keep the source intact. Only the private backup image is rebuilt, and generated columns are never copied as input.
        Execute(db, "PRAGMA foreign_keys=OFF");
        using var tx = db.BeginTransaction();
        // Renaming a rebuilt parent must not retarget the kept tables' foreign keys to its old name.
        Execute(db, "PRAGMA legacy_alter_table=ON", tx);
        var rebuilt = new[]
        {
            "pair",
            "source_state",
            "issue",
            "issue_member"
        };
        var retired = new[]
        {
            "account",
            "binding_repair",
            "change_work",
            "folder_removal",
            "folder_removal_submission",
            "folder_removal_item",
            "folder_move"
        };
        var counts = new[]
        {
            "pair",
            "synced_entry",
            "discovery_deleted_block",
            "journal_position"
        }.ToDictionary(t => t, t => Scalar(db, "SELECT count(*) FROM " + t, tx));
        static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        Execute(db, "DROP TRIGGER invalidate_deleted_history", tx);
        foreach (var table in rebuilt)
        {
            foreach (var i in Names("SELECT name FROM sqlite_schema WHERE type='index' AND tbl_name='" + table + "' AND sql IS NOT NULL"))
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

        foreach (var table in rebuilt)
        {
            ct.ThrowIfCancellationRequested();
            var cols = string.Join(",", Names("SELECT name FROM pragma_table_xinfo('" + table + "') WHERE hidden=0").Select(Quote));
            var where = table == "issue_member" ? " WHERE issue_id IS NOT NULL" : "";
            Execute(db, "INSERT INTO " + table + "(" + cols + ") SELECT " + cols + " FROM old_" + table + where, tx);
            if (!Equals(Scalar(db, "SELECT count(*) FROM " + table, tx), Scalar(db, "SELECT count(*) FROM old_" + table + where, tx)))
                throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, table)));
        }

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
        Execute(db, """
            INSERT INTO accepted_job(pair_id,route,job_id,launch)
            SELECT f.pair_id,'files/move_batch/check_v2',f.job_id,(SELECT json_group_array(json_object('Id',json_extract(j.value,'$[0]'),'Path',CASE WHEN f.source_path='' THEN '' ELSE f.source_path||'/' END||json_extract(j.value,'$[1]'),'Destination',CASE WHEN f.destination_path='' THEN '' ELSE f.destination_path||'/' END||json_extract(j.value,'$[1]'))) FROM json_each(f.launch) j ORDER BY j.key) FROM folder_move f WHERE f.job_id IS NOT NULL;
            INSERT INTO accepted_job(pair_id,route,job_id,launch)
            SELECT s.pair_id,'files/delete_batch/check',s.job_id,(SELECT json_group_array(json_object('Id',g.id,'Path',g.path,'Destination',NULL)) FROM (SELECT json_extract(i.expected_json,'$.Value.Id') id,i.path FROM folder_removal_item i WHERE i.submission_id=s.submission_id ORDER BY i.submission_member) g) FROM folder_removal_submission s WHERE s.phase='Accepted';
            """, tx);
        if (!Equals(Scalar(db, "SELECT count(*) FROM accepted_job a WHERE json_array_length(launch)=0 OR EXISTS(SELECT 1 FROM json_each(a.launch) j WHERE length(coalesce(json_extract(j.value,'$.Id'),''))=0 OR length(coalesce(json_extract(j.value,'$.Path'),''))=0)", tx), 0L))
            throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, nameof(AcceptedJob))));
        // Reparenting may change ownership, never the number of retained physical members.
        if(!Equals(Scalar(db,"SELECT count(*) FROM issue_member",tx),Scalar(db,"SELECT count(*) FROM old_issue_member WHERE issue_id IS NOT NULL",tx)))
            throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation,(TextArg.Path,nameof(NoticeItem))));
        Execute(db, "DROP TABLE old_issue_member", tx);
        foreach (var table in rebuilt.Where(t => t != "issue_member"))
            Execute(db, "DROP TABLE old_" + table, tx);
        foreach (var table in retired)
            Execute(db, "DROP TABLE " + table, tx);
        foreach (var(table, count)in counts)
            if (!Equals(count, Scalar(db, "SELECT count(*) FROM " + table, tx)))
                throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, table)));
        Execute(db, $"PRAGMA user_version={Schema.Version}", tx);
        Verify(db, Schema.Version, tx);
        tx.Commit();
        Execute(db, "PRAGMA foreign_keys=ON");
        List<string> Names(string sql)
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
    }
}
