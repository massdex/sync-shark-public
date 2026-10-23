using System.Text;

namespace Massdex.SyncShark;

// Exact scan consumers. Owned observation/cut/delta rows are retired with their scan;
// the remaining FKs and logical JSON/Rewind references protect the entire snapshot.
internal static class SyncScanRetentionSchema
{
    internal sealed record Reference(string Table,string Expression,string UpdateColumns,string Predicate="1");
    internal static readonly Reference[] References =
    [
        new("reconciliation_decision","local_scan_generation_id","local_scan_generation_id"),
        new("reconciliation_decision","dropbox_scan_generation_id","dropbox_scan_generation_id"),
        new("sync_path_index","local_scan_generation_id","local_scan_generation_id"),
        new("sync_path_index","dropbox_scan_generation_id","dropbox_scan_generation_id"),
        new("tombstone_observation","local_scan_generation_id","local_scan_generation_id"),
        new("tombstone_observation","dropbox_scan_generation_id","dropbox_scan_generation_id"),
        new("rewind_session","local_checkpoint_scan_id","local_checkpoint_scan_id,status","status<>'resumed'"),
        new("rewind_session","dropbox_checkpoint_scan_id","dropbox_checkpoint_scan_id,status","status<>'resumed'"),
        new("rewind_session","local_preview_scan_id","local_preview_scan_id,status","status<>'resumed'"),
        new("rewind_session","dropbox_preview_scan_id","dropbox_preview_scan_id,status","status<>'resumed'"),
        new("consent_header","json_extract(header_json,'$.plan.LocalScanGenerationId')","header_json"),
        new("consent_header","json_extract(header_json,'$.plan.DropboxScanGenerationId')","header_json"),
        new("command_request","json_extract(payload_json,'$.reviewedPlan.LocalScanGenerationId')","payload_json"),
        new("command_request","json_extract(payload_json,'$.reviewedPlan.DropboxScanGenerationId')","payload_json"),
        new("observation_cut","captured_parent_scan_id","captured_parent_scan_id,state","state IN ('open','sealed')")
    ];

    internal static readonly string Sql=Build();
    private static string Build()
    {
        var sql=new StringBuilder("""
            ALTER TABLE scan_generation ADD COLUMN lifetime_accounted INTEGER NOT NULL DEFAULT 0 CHECK(lifetime_accounted IN (0,1));
            ALTER TABLE scan_generation ADD COLUMN unreferenced_since_utc TEXT;
            ALTER TABLE scan_generation ADD COLUMN lifetime_retiring INTEGER NOT NULL DEFAULT 0 CHECK(lifetime_retiring IN (0,1));
            ALTER TABLE scan_generation ADD COLUMN planning_reference_count INTEGER NOT NULL DEFAULT 0 CHECK(planning_reference_count>=0);
            ALTER TABLE operation ADD COLUMN journal_scan_accounted INTEGER NOT NULL DEFAULT 0 CHECK(journal_scan_accounted IN (0,1));
            ALTER TABLE observation_interval ADD COLUMN unreferenced_since_utc TEXT;
            ALTER TABLE installation ADD COLUMN scan_lifetime_frontier TEXT NOT NULL DEFAULT '';
            ALTER TABLE installation ADD COLUMN scan_retirement_frontier TEXT NOT NULL DEFAULT '';
            ALTER TABLE installation ADD COLUMN interval_lifetime_frontier TEXT NOT NULL DEFAULT '';
            ALTER TABLE installation ADD COLUMN inventory_lifetime_last_hold TEXT;
            ALTER TABLE installation ADD COLUMN inventory_lifetime_slot INTEGER NOT NULL DEFAULT 0 CHECK(inventory_lifetime_slot IN (0,1));
            ALTER TABLE installation ADD COLUMN inventory_expired_rows INTEGER NOT NULL DEFAULT 0 CHECK(inventory_expired_rows>=0);
            ALTER TABLE installation ADD COLUMN inventory_expired_bytes INTEGER NOT NULL DEFAULT 0 CHECK(inventory_expired_bytes>=0);
            ALTER TABLE installation ADD COLUMN inventory_unreferenced_scans INTEGER NOT NULL DEFAULT 0 CHECK(inventory_unreferenced_scans>=0);
            ALTER TABLE installation ADD COLUMN inventory_unreferenced_intervals INTEGER NOT NULL DEFAULT 0 CHECK(inventory_unreferenced_intervals>=0);
            CREATE INDEX IF NOT EXISTS operation_scan_lifetime_pending ON operation(operation_id) WHERE journal_scan_accounted=0;
            CREATE INDEX IF NOT EXISTS scan_lifetime_pending ON scan_generation(scan_generation_id) WHERE lifetime_accounted=0;
            CREATE INDEX IF NOT EXISTS scan_lifetime_aged ON scan_generation(unreferenced_since_utc,scan_generation_id) WHERE unreferenced_since_utc IS NOT NULL;
            CREATE INDEX IF NOT EXISTS scan_lifetime_retiring ON scan_generation(scan_generation_id) WHERE lifetime_retiring=1;
            CREATE INDEX IF NOT EXISTS scan_root_complete_generation ON scan_generation(root_binding_id,generation DESC) WHERE status='complete';
            CREATE INDEX IF NOT EXISTS interval_lifetime_aged ON observation_interval(unreferenced_since_utc,interval_id) WHERE unreferenced_since_utc IS NOT NULL;
            CREATE INDEX IF NOT EXISTS interval_lifetime_closed ON observation_interval(interval_id) WHERE to_generation IS NOT NULL;
            CREATE INDEX IF NOT EXISTS cut_lifetime_range ON observation_cut(root_binding_id,state,generation);
            CREATE INDEX IF NOT EXISTS cut_lifetime_parent_range ON observation_cut(root_binding_id,state,captured_parent_generation);
            CREATE INDEX IF NOT EXISTS cut_lifetime_parent ON observation_cut(captured_parent_scan_id);
            CREATE INDEX IF NOT EXISTS delta_lifetime_prior ON observation_delta(expected_prior_interval_id);
            CREATE INDEX IF NOT EXISTS delta_lifetime_cut ON observation_delta(cut_id,delta_id);
            CREATE INDEX IF NOT EXISTS delta_lifetime_scan ON observation_delta(scan_generation_id,delta_id);
            CREATE INDEX IF NOT EXISTS observation_lifetime_scan ON observation(scan_generation_id,observation_id);
            CREATE TRIGGER IF NOT EXISTS scan_lifetime_account AFTER UPDATE OF unreferenced_since_utc ON scan_generation
            BEGIN UPDATE installation SET inventory_unreferenced_scans=inventory_unreferenced_scans
                +(NEW.unreferenced_since_utc IS NOT NULL)-(OLD.unreferenced_since_utc IS NOT NULL); END;
            CREATE TRIGGER IF NOT EXISTS scan_lifetime_removed AFTER DELETE ON scan_generation
            BEGIN UPDATE installation SET inventory_unreferenced_scans=inventory_unreferenced_scans
                -(OLD.unreferenced_since_utc IS NOT NULL); END;
            CREATE TRIGGER IF NOT EXISTS interval_lifetime_account AFTER UPDATE OF unreferenced_since_utc ON observation_interval
            BEGIN UPDATE installation SET inventory_unreferenced_intervals=inventory_unreferenced_intervals
                +(NEW.unreferenced_since_utc IS NOT NULL)-(OLD.unreferenced_since_utc IS NOT NULL); END;
            CREATE TRIGGER IF NOT EXISTS interval_lifetime_removed AFTER DELETE ON observation_interval
            BEGIN UPDATE installation SET inventory_unreferenced_intervals=inventory_unreferenced_intervals
                -(OLD.unreferenced_since_utc IS NOT NULL); END;
            CREATE TRIGGER IF NOT EXISTS scan_retirement_state_guard BEFORE UPDATE OF lifetime_retiring ON scan_generation
            WHEN OLD.lifetime_retiring<>NEW.lifetime_retiring
            BEGIN SELECT CASE WHEN OLD.lifetime_retiring<>0 OR NEW.lifetime_retiring<>1
                OR syncshark_object_retirement_allowed('scan_generation',OLD.scan_generation_id)<>1
                THEN RAISE(ABORT,'Inventory retirement requires exact private authority') END; END;
            """);
        // Expression indexes also cover retained predecessor JSON without parsing every row per scan.
        for(var i=0;i<References.Length;i++)
        {
            var reference=References[i];var table=reference.Table;var expression=reference.Expression;
            string Bound(string alias)=>expression.StartsWith("json_extract(",StringComparison.Ordinal)
                ?expression.Replace("json_extract(","json_extract("+alias+".",StringComparison.Ordinal)
                :alias+"."+expression;
            string Applies(string alias)=>reference.Predicate=="1"?"1":
                reference.Predicate.Replace("status",alias+".status",StringComparison.Ordinal)
                    .Replace("state",alias+".state",StringComparison.Ordinal);
            sql.Append($"""
                CREATE INDEX IF NOT EXISTS scan_reference_{i} ON {table}({expression}) WHERE {reference.Predicate};
                CREATE TRIGGER IF NOT EXISTS scan_reference_{i}_insert BEFORE INSERT ON {table}
                WHEN {Applies("NEW")} AND EXISTS(SELECT 1 FROM scan_generation
                    WHERE scan_generation_id={Bound("NEW")} AND lifetime_retiring=1)
                BEGIN SELECT RAISE(ABORT,'An expired inventory cannot acquire a new consumer'); END;
                CREATE TRIGGER IF NOT EXISTS scan_reference_{i}_update BEFORE UPDATE OF {reference.UpdateColumns} ON {table}
                WHEN {Applies("NEW")} AND EXISTS(SELECT 1 FROM scan_generation
                    WHERE scan_generation_id={Bound("NEW")} AND lifetime_retiring=1)
                BEGIN SELECT RAISE(ABORT,'An expired inventory cannot acquire a new consumer'); END;
                CREATE TRIGGER IF NOT EXISTS scan_reference_{i}_added AFTER INSERT ON {table}
                WHEN {Applies("NEW")}
                BEGIN UPDATE scan_generation SET unreferenced_since_utc=NULL,lifetime_accounted=0
                    WHERE scan_generation_id={Bound("NEW")}; END;
                CREATE TRIGGER IF NOT EXISTS scan_reference_{i}_released AFTER DELETE ON {table}
                WHEN {Applies("OLD")}
                BEGIN UPDATE scan_generation SET unreferenced_since_utc=NULL,lifetime_accounted=0
                    WHERE scan_generation_id={Bound("OLD")}; END;
                CREATE TRIGGER IF NOT EXISTS scan_reference_{i}_changed AFTER UPDATE OF {reference.UpdateColumns} ON {table}
                BEGIN UPDATE scan_generation SET unreferenced_since_utc=NULL,lifetime_accounted=0
                    WHERE scan_generation_id IN ({Bound("OLD")},{Bound("NEW")}); END;
                """);
        }
        // One counter per scan, not one permanent row per action/scan edge. Legacy actions are
        // incrementally accounted; collection waits while any predecessor is unaccounted.
        string OperationScans(string alias)=>$"""
            SELECT json_extract(value,'$.LocalScanGenerationId') FROM json_each({alias}.preconditions_json,'$.planningAuthorities')
            UNION SELECT json_extract(value,'$.DropboxScanGenerationId') FROM json_each({alias}.preconditions_json,'$.planningAuthorities')
            """;
        sql.Append($"""
            CREATE TRIGGER IF NOT EXISTS operation_scan_ref_insert AFTER INSERT ON operation
            BEGIN UPDATE operation SET journal_scan_accounted=1 WHERE operation_id=NEW.operation_id; END;
            CREATE TRIGGER IF NOT EXISTS operation_scan_ref_account AFTER UPDATE OF journal_scan_accounted ON operation
            WHEN OLD.journal_scan_accounted=0 AND NEW.journal_scan_accounted=1
            BEGIN
                SELECT CASE WHEN EXISTS(SELECT 1 FROM scan_generation WHERE lifetime_retiring=1
                    AND scan_generation_id IN ({OperationScans("NEW")}))
                    THEN RAISE(ABORT,'An expired inventory cannot acquire a planning consumer') END;
                UPDATE scan_generation SET planning_reference_count=planning_reference_count+1,
                    unreferenced_since_utc=NULL,lifetime_accounted=0
                    WHERE scan_generation_id IN ({OperationScans("NEW")});
            END;
            CREATE TRIGGER IF NOT EXISTS operation_scan_ref_account_monotonic BEFORE UPDATE OF journal_scan_accounted ON operation
            WHEN NEW.journal_scan_accounted<OLD.journal_scan_accounted
            BEGIN SELECT RAISE(ABORT,'Planning reference accounting cannot rewind'); END;
            CREATE TRIGGER IF NOT EXISTS operation_scan_ref_delete AFTER DELETE ON operation
            WHEN OLD.journal_scan_accounted=1
            BEGIN UPDATE scan_generation SET planning_reference_count=planning_reference_count-1,
                unreferenced_since_utc=NULL,lifetime_accounted=0
                WHERE scan_generation_id IN ({OperationScans("OLD")}); END;
            CREATE TRIGGER IF NOT EXISTS operation_scan_ref_changed AFTER UPDATE OF preconditions_json ON operation
            WHEN OLD.journal_scan_accounted=1
            BEGIN
                SELECT CASE WHEN EXISTS(SELECT 1 FROM scan_generation WHERE lifetime_retiring=1
                    AND scan_generation_id IN ({OperationScans("NEW")}))
                    THEN RAISE(ABORT,'An expired inventory cannot acquire a planning consumer') END;
                UPDATE scan_generation SET planning_reference_count=planning_reference_count-1,
                    unreferenced_since_utc=NULL,lifetime_accounted=0
                    WHERE scan_generation_id IN ({OperationScans("OLD")});
                UPDATE scan_generation SET planning_reference_count=planning_reference_count+1,
                    unreferenced_since_utc=NULL,lifetime_accounted=0
                    WHERE scan_generation_id IN ({OperationScans("NEW")});
            END;
            DROP TRIGGER observation_no_delete;
            DROP TRIGGER observation_interval_no_delete;
            DROP TRIGGER observation_interval_close_only;
            CREATE TRIGGER IF NOT EXISTS observation_interval_close_only BEFORE UPDATE ON observation_interval
            WHEN NOT (
                NEW.interval_id IS OLD.interval_id AND NEW.root_binding_id IS OLD.root_binding_id
                AND NEW.side IS OLD.side AND NEW.semantic_key IS OLD.semantic_key
                AND NEW.semantic_digest IS OLD.semantic_digest AND NEW.from_generation IS OLD.from_generation
                AND NEW.membership_id IS OLD.membership_id AND NEW.object_version_id IS OLD.object_version_id
                AND NEW.evidence_json IS OLD.evidence_json AND NEW.observation_id IS OLD.observation_id
                AND NEW.observed_utc IS OLD.observed_utc
                AND ((OLD.to_generation IS NULL AND NEW.to_generation IS NOT NULL
                        AND NEW.unreferenced_since_utc IS OLD.unreferenced_since_utc)
                    OR (NEW.to_generation IS OLD.to_generation
                        AND (NEW.unreferenced_since_utc IS NULL OR syncshark_object_retirement_allowed('observation_interval',OLD.interval_id)=1))))
            BEGIN SELECT RAISE(ABORT,'Observation intervals allow one close or exact private lifetime accounting'); END;
            """);
        foreach(var (table,key) in new[]{("scan_generation","scan_generation_id"),("observation_cut","cut_id"),
            ("observation_interval","interval_id"),("observation","observation_id")})
        {
            var guard=table=="observation"?"observation_no_delete":table=="observation_interval"?"observation_interval_no_delete":table+"_inventory_retirement_guard";
            sql.Append($"""
                CREATE TRIGGER IF NOT EXISTS {guard} BEFORE DELETE ON {table}
                BEGIN SELECT CASE WHEN syncshark_object_retirement_allowed('{table}',OLD.{key})<>1
                    THEN RAISE(ABORT,'Inventory detail retirement requires exact private authority') END; END;
                """);
        }
        sql.Append("""
            
            CREATE TRIGGER IF NOT EXISTS interval_new_delta_reference AFTER INSERT ON observation_delta
            BEGIN UPDATE observation_interval SET unreferenced_since_utc=NULL
                WHERE interval_id=NEW.expected_prior_interval_id AND unreferenced_since_utc IS NOT NULL; END;
            CREATE TRIGGER IF NOT EXISTS interval_changed_delta_reference AFTER UPDATE OF expected_prior_interval_id ON observation_delta
            BEGIN UPDATE observation_interval SET unreferenced_since_utc=NULL
                WHERE interval_id=NEW.expected_prior_interval_id AND unreferenced_since_utc IS NOT NULL; END;
            CREATE TRIGGER IF NOT EXISTS interval_new_cut_reference AFTER INSERT ON observation_cut
            BEGIN UPDATE observation_interval SET unreferenced_since_utc=NULL
                WHERE root_binding_id=NEW.root_binding_id AND to_generation IS NOT NULL AND unreferenced_since_utc IS NOT NULL
                    AND ((NEW.generation>=from_generation AND NEW.generation<to_generation)
                        OR (NEW.captured_parent_generation>=from_generation AND NEW.captured_parent_generation<to_generation)); END;
            CREATE TRIGGER IF NOT EXISTS interval_changed_cut_reference AFTER UPDATE OF state,captured_parent_generation ON observation_cut
            BEGIN UPDATE observation_interval SET unreferenced_since_utc=NULL
                WHERE root_binding_id=NEW.root_binding_id AND to_generation IS NOT NULL AND unreferenced_since_utc IS NOT NULL
                    AND ((NEW.generation>=from_generation AND NEW.generation<to_generation)
                        OR (NEW.captured_parent_generation>=from_generation AND NEW.captured_parent_generation<to_generation)); END;

            CREATE TRIGGER IF NOT EXISTS delta_failed_retirement_guard BEFORE DELETE ON observation_delta
            WHEN EXISTS(SELECT 1 FROM scan_generation WHERE scan_generation_id=OLD.scan_generation_id
                AND status IN ('failed','superseded'))
            BEGIN SELECT CASE WHEN syncshark_object_retirement_allowed('observation_delta',OLD.delta_id)<>1
                THEN RAISE(ABORT,'Abandoned overlay retirement requires exact private authority') END; END;
            """);
        // The original projection is reused byte-for-byte apart from status eligibility:
        // failed/retiring metadata is never projected as a smaller complete snapshot.
        var view=SyncAuthoritySchemaMigration9.Sql.Replace("\r\n","\n",StringComparison.Ordinal);
        view=view[view.IndexOf("CREATE VIEW IF NOT EXISTS effective_observation",StringComparison.Ordinal)..];
        view=view.Replace("FROM observation o\n","FROM observation o\nWHERE o.scan_generation_id IS NULL OR EXISTS(SELECT 1 FROM scan_generation s WHERE s.scan_generation_id=o.scan_generation_id AND s.status IN ('incomplete','complete'))\n",StringComparison.Ordinal)
            .Replace("WHERE c.state = 'published'","WHERE c.state = 'published' AND EXISTS(SELECT 1 FROM scan_generation s WHERE s.scan_generation_id=c.scan_generation_id AND s.status='complete')",StringComparison.Ordinal)
            .Replace("WHERE c.state = 'sealed'","WHERE c.state = 'sealed' AND EXISTS(SELECT 1 FROM scan_generation s WHERE s.scan_generation_id=c.scan_generation_id AND s.status='incomplete')",StringComparison.Ordinal);
        sql.Append("DROP VIEW effective_observation;\n").Append(view);
        return sql.ToString();
    }
}

