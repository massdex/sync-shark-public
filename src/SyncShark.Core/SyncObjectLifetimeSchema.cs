using System.Text;

namespace Massdex.SyncShark;

// Exact existing reverse references only; no new object store or inferred path identity.
internal static class SyncObjectLifetimeSchema
{
    internal sealed record Target(string Table,string Key,string[] Scalars,(string Table,string Column)[] References);
    internal static readonly Target[] Targets =
    [
        new("object_version","object_version_id",
            ["object_version_id","object_id","side","identity_json","content_hash","size_bytes","observed_utc"],
            [("observation","object_version_id"),("synchronization_point","local_object_version_id"),
             ("synchronization_point","dropbox_object_version_id"),("operation_member","object_version_id"),
             ("conflict_version","object_version_id"),("tombstone","prior_object_version_id"),
             ("upload_session_checkpoint","source_object_version_id"),("observation_interval","object_version_id"),
             ("observation_delta","object_version_id"),("current_copy_completion","local_version_id"),
             ("current_copy_completion","remote_version_id"),("copy_predecessor","local_version_id"),
             ("copy_predecessor","remote_version_id")]),
        new("membership","membership_id",
            ["membership_id","object_id","root_binding_id","relative_path_key","generation","state","observed_utc"],
            [("observation","membership_id"),("synchronization_point","local_membership_id"),
             ("synchronization_point","dropbox_membership_id"),("operation_member","membership_id"),
             ("conflict_version","membership_id"),("tombstone","prior_membership_id"),
             ("observation_interval","membership_id"),("observation_delta","membership_id"),
             ("current_copy_completion","local_membership_id"),("current_copy_completion","remote_membership_id"),
             ("copy_predecessor","local_membership_id"),("copy_predecessor","remote_membership_id")])
    ];

    internal static readonly string Sql = Build();
    private static string Build()
    {
        var sql=new StringBuilder("""
            ALTER TABLE logical_object ADD COLUMN lifetime_reference_epoch INTEGER NOT NULL DEFAULT 0 CHECK(lifetime_reference_epoch>=0);
            ALTER TABLE installation ADD COLUMN object_lifetime_slot INTEGER NOT NULL DEFAULT 0 CHECK(object_lifetime_slot IN (0,1));
            CREATE INDEX IF NOT EXISTS object_version_lifetime_current ON object_version(object_id,side,observed_utc,object_version_id);
            """);
        foreach(var target in Targets)
        {
            var t=target.Table;var key=target.Key;
            sql.Append($"""
                ALTER TABLE {t} ADD COLUMN lifetime_accounted INTEGER NOT NULL DEFAULT 0 CHECK(lifetime_accounted IN (0,1));
                ALTER TABLE {t} ADD COLUMN unreferenced_since_utc TEXT;
                ALTER TABLE {t} ADD COLUMN lifetime_reference_epoch INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE {t} ADD COLUMN lifetime_encoded_bytes INTEGER NOT NULL DEFAULT 0 CHECK(lifetime_encoded_bytes>=0);
                ALTER TABLE installation ADD COLUMN {t}_lifetime_frontier TEXT NOT NULL DEFAULT '';
                ALTER TABLE installation ADD COLUMN {t}_unreferenced_count INTEGER NOT NULL DEFAULT 0 CHECK({t}_unreferenced_count>=0);
                ALTER TABLE installation ADD COLUMN {t}_unreferenced_bytes INTEGER NOT NULL DEFAULT 0 CHECK({t}_unreferenced_bytes>=0);
                ALTER TABLE installation ADD COLUMN {t}_expired_count INTEGER NOT NULL DEFAULT 0 CHECK({t}_expired_count>=0);
                CREATE INDEX IF NOT EXISTS {t}_lifetime_pending ON {t}({key}) WHERE lifetime_accounted=0;
                CREATE INDEX IF NOT EXISTS {t}_lifetime_grace ON {t}(unreferenced_since_utc,{key})
                    WHERE unreferenced_since_utc IS NOT NULL AND lifetime_encoded_bytes<=1048576;
                CREATE TRIGGER IF NOT EXISTS {t}_lifetime_account AFTER UPDATE OF unreferenced_since_utc,lifetime_encoded_bytes ON {t}
                BEGIN
                    UPDATE installation SET {t}_unreferenced_count={t}_unreferenced_count
                        +(NEW.unreferenced_since_utc IS NOT NULL)-(OLD.unreferenced_since_utc IS NOT NULL),
                        {t}_unreferenced_bytes={t}_unreferenced_bytes
                        +(CASE WHEN NEW.unreferenced_since_utc IS NOT NULL THEN NEW.lifetime_encoded_bytes ELSE 0 END)
                        -(CASE WHEN OLD.unreferenced_since_utc IS NOT NULL THEN OLD.lifetime_encoded_bytes ELSE 0 END);
                END;
                CREATE TRIGGER IF NOT EXISTS {t}_lifetime_delete_guard BEFORE DELETE ON {t}
                BEGIN SELECT CASE WHEN syncshark_object_retirement_allowed('{t}',OLD.{key})<>1
                    THEN RAISE(ABORT,'Object detail retirement requires exact private zero-reference authority') END; END;
                CREATE TRIGGER IF NOT EXISTS {t}_lifetime_deleted AFTER DELETE ON {t}
                BEGIN
                    UPDATE installation SET {t}_unreferenced_count={t}_unreferenced_count-(OLD.unreferenced_since_utc IS NOT NULL),
                        {t}_unreferenced_bytes={t}_unreferenced_bytes
                        -(CASE WHEN OLD.unreferenced_since_utc IS NOT NULL THEN OLD.lifetime_encoded_bytes ELSE 0 END),
                        {t}_expired_count={t}_expired_count+1;
                END;
                """);
            foreach(var reference in target.References)
            {
                var source=reference.Table;var column=reference.Column;var prefix=$"{t}_lifetime_{source}_{column}";
                // Schema14 already indexes each exact copy-proof reverse reference.
                if(source is not ("current_copy_completion" or "copy_predecessor"))
                    sql.Append($"CREATE INDEX IF NOT EXISTS {prefix}_ref ON {source}({column}) WHERE {column} IS NOT NULL;");
                sql.Append($"""
                    CREATE TRIGGER IF NOT EXISTS {prefix}_insert AFTER INSERT ON {source} WHEN NEW.{column} IS NOT NULL
                    BEGIN UPDATE {t} SET lifetime_accounted=0,unreferenced_since_utc=NULL WHERE {key}=NEW.{column}; END;
                    CREATE TRIGGER IF NOT EXISTS {prefix}_delete AFTER DELETE ON {source} WHEN OLD.{column} IS NOT NULL
                    BEGIN UPDATE {t} SET lifetime_accounted=0,unreferenced_since_utc=NULL WHERE {key}=OLD.{column}; END;
                    CREATE TRIGGER IF NOT EXISTS {prefix}_update AFTER UPDATE OF {column} ON {source}
                    WHEN NEW.{column} IS NOT OLD.{column}
                    BEGIN UPDATE {t} SET lifetime_accounted=0,unreferenced_since_utc=NULL
                        WHERE {key}=OLD.{column} OR {key}=NEW.{column}; END;
                    """);
            }
        }
        // Object-scoped effects and recovery may retain references inside their typed payload.
        // One scalar per logical object avoids updating every historical version on each effect.
        foreach(var source in new[]{"operation","conflict","recovery_artifact","membership"})
        {
            // The operation_copy_object_state index already starts with object_id.
            if(source!="operation")
                sql.Append($"CREATE INDEX IF NOT EXISTS {source}_object_lifetime_ref ON {source}(object_id) WHERE object_id IS NOT NULL;");
            sql.Append($"""
                CREATE TRIGGER IF NOT EXISTS {source}_object_lifetime_insert AFTER INSERT ON {source} WHEN NEW.object_id IS NOT NULL
                BEGIN UPDATE logical_object SET lifetime_reference_epoch=lifetime_reference_epoch+1 WHERE object_id=NEW.object_id; END;
                CREATE TRIGGER IF NOT EXISTS {source}_object_lifetime_delete AFTER DELETE ON {source} WHEN OLD.object_id IS NOT NULL
                BEGIN UPDATE logical_object SET lifetime_reference_epoch=lifetime_reference_epoch+1 WHERE object_id=OLD.object_id; END;
                CREATE TRIGGER IF NOT EXISTS {source}_object_lifetime_update AFTER UPDATE OF object_id{(source=="membership"?",state":",status")} ON {source}
                BEGIN UPDATE logical_object SET lifetime_reference_epoch=lifetime_reference_epoch+1
                    WHERE object_id=OLD.object_id OR object_id=NEW.object_id; END;
                """);
        }
        return sql.ToString();
    }
}
