using System.Text;

namespace Massdex.SyncShark;

// Fixed schema contract, shared by delta triggers and bounded predecessor accounting.
// Encoded size is the sum of UTF-8 SQLite scalar values, excluding accounting counters.
// It is not the physical database/WAL size or the JSON closure's separate retention charge.
internal static class SyncActionJournalSchema
{
    internal static readonly (string Table, string Columns, string Owner)[] Tables =
    [
        ("operation", "operation_id,pair_id,object_id,kind,status,priority,configuration_revision,capability_profile_id,idempotency_key,preconditions_json,current_lease_id,current_fencing_token,last_transition_lease_id,last_transition_fencing_token,created_utc,ready_utc,terminal_utc,block_code,block_detail,operation_group_id,origin_command_id,execution_command_id,consent_id,consent_item_ordinal,registration_generation,registration_sequence,registration_intent_sha256,closure_utc,closure_json,closure_sha256,closure_encoded_bytes,completion_state_json,closure_kind", "{0}.operation_id"),
        ("operation_member", "operation_member_id,operation_id,pair_id,object_id,role,root_binding_id,membership_id,object_version_id,expected_membership_generation,expected_path_key,expected_state_json", "{0}.operation_id"),
        ("operation_dependency", "operation_id,depends_on_operation_id,dependency_kind,retired_dependency_id,terminal_proof_json,terminal_proof_sha256", "{0}.operation_id"),
        ("operation_step", "operation_step_id,operation_id,ordinal,step_kind,status,preconditions_json,created_utc,terminal_utc", "{0}.operation_id"),
        ("resource_allocation", "allocation_id,operation_id,control_namespace_id,resource_kind,deterministic_name,intended_parent_identity,owner_nonce_hash,status,bound_identity,content_hash,size_bytes,transition_lease_id,transition_fencing_token,created_utc", "{0}.operation_id"),
        ("namespace_allocation", "namespace_allocation_id,operation_id,side,parent_identity,deterministic_name,owner_nonce_hash,status,bound_identity,transition_lease_id,transition_fencing_token,created_utc", "{0}.operation_id"),
        ("operation_attempt", "attempt_id,operation_id,attempt_no,lease_id,fencing_token,transition_lease_id,transition_fencing_token,request_fingerprint,effect_kind,status,started_utc,ended_utc", "{0}.operation_id"),
        ("effect_receipt", "effect_receipt_id,attempt_id,recorded_lease_id,recorded_fencing_token,certainty,source_identity_json,destination_identity_json,provider_metadata_json,evidence_digest,observed_utc", "(SELECT operation_id FROM operation_attempt WHERE attempt_id={0}.attempt_id)"),
        ("upload_session_checkpoint", "allocation_id,operation_id,session_id,confirmed_offset_bytes,source_object_version_id,source_size_bytes,source_content_hash,status,transition_lease_id,transition_fencing_token,updated_utc", "{0}.operation_id"),
        ("lease", "lease_id,scope_kind,scope_id,worker_id,fencing_token,acquired_utc,expires_utc,status,command_request_id,dispatch_allowed", "CASE WHEN {0}.scope_kind='operation' THEN {0}.scope_id END"),
        ("lease_fence", "scope_kind,scope_id,last_token", "CASE WHEN {0}.scope_kind='operation' THEN {0}.scope_id END"),
    ];

    internal static string Charge(string columns, string prefix) =>
        string.Join("+", columns.Split(',').Select(name =>
            $"coalesce(length(CAST({prefix}{name} AS BLOB)),0)"));

    internal static string Owner(string template, string alias) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, template, alias);

    internal static readonly string Sql = Build();

    private static string Build()
    {
        var sql = new StringBuilder("""
            ALTER TABLE operation ADD COLUMN journal_encoded_bytes INTEGER NOT NULL DEFAULT 0 CHECK(journal_encoded_bytes>=0);
            ALTER TABLE operation ADD COLUMN journal_accounted INTEGER NOT NULL DEFAULT 0 CHECK(journal_accounted IN (0,1));
            CREATE INDEX IF NOT EXISTS action_journal_unmeasured
                ON operation(journal_accounted,pair_id,operation_id);
            CREATE INDEX IF NOT EXISTS action_journal_retained_from ON operation(created_utc);
            CREATE INDEX IF NOT EXISTS action_journal_pair_retained_from ON operation(pair_id,created_utc);
            """);
        foreach (var table in new[] { "installation", "sync_pair" })
            foreach (var field in new[] { "count", "bytes", "held_count", "held_bytes" })
                sql.Append($"ALTER TABLE {table} ADD COLUMN journal_{field} INTEGER NOT NULL DEFAULT 0 CHECK(journal_{field}>=0);");
        sql.Append("ALTER TABLE installation ADD COLUMN journal_global_held_bytes INTEGER NOT NULL DEFAULT 0 CHECK(journal_global_held_bytes>=0);");
        foreach (var spec in Tables)
        {
            var before = Charge(spec.Columns, "OLD.");
            var after = Charge(spec.Columns, "NEW.");
            if (spec.Table == "operation")
            {
                sql.Append($"""
                    CREATE TRIGGER IF NOT EXISTS journal_operation_insert AFTER INSERT ON operation
                    BEGIN UPDATE operation SET journal_encoded_bytes={after},journal_accounted=1
                        WHERE operation_id=NEW.operation_id; END;
                    CREATE TRIGGER IF NOT EXISTS journal_operation_update AFTER UPDATE ON operation
                    WHEN NEW.journal_accounted=1 AND ({after})<>({before})
                    BEGIN UPDATE operation SET journal_encoded_bytes=journal_encoded_bytes+({after})-({before})
                        WHERE operation_id=NEW.operation_id; END;
                    """);
                continue;
            }
            var oldOwner = Owner(spec.Owner, "OLD");
            var newOwner = Owner(spec.Owner, "NEW");
            sql.Append($"""
                CREATE TRIGGER IF NOT EXISTS journal_{spec.Table}_insert AFTER INSERT ON {spec.Table}
                BEGIN UPDATE operation SET journal_encoded_bytes=journal_encoded_bytes+({after})
                    WHERE operation_id=({newOwner}) AND journal_accounted=1; END;
                CREATE TRIGGER IF NOT EXISTS journal_{spec.Table}_delete AFTER DELETE ON {spec.Table}
                BEGIN UPDATE operation SET journal_encoded_bytes=journal_encoded_bytes-({before})
                    WHERE operation_id=({oldOwner}) AND journal_accounted=1; END;
                CREATE TRIGGER IF NOT EXISTS journal_{spec.Table}_update AFTER UPDATE ON {spec.Table}
                WHEN ({newOwner}) IS NOT ({oldOwner}) OR ({after})<>({before})
                BEGIN
                    UPDATE operation SET journal_encoded_bytes=journal_encoded_bytes-({before})
                        WHERE operation_id=({oldOwner}) AND journal_accounted=1;
                    UPDATE operation SET journal_encoded_bytes=journal_encoded_bytes+({after})
                        WHERE operation_id=({newOwner}) AND journal_accounted=1;
                END;
                """);
        }
        // Changes to these derived fields do not affect an action's immutable graph.
        foreach (var kind in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            sql.Append($"CREATE TRIGGER IF NOT EXISTS journal_totals_{kind.ToLowerInvariant()} AFTER {kind} ON operation ");
            if (kind == "UPDATE") sql.Append("WHEN NEW.journal_accounted<>OLD.journal_accounted OR " +
                "NEW.journal_encoded_bytes<>OLD.journal_encoded_bytes OR NEW.closure_utc IS NOT OLD.closure_utc OR NEW.pair_id IS NOT OLD.pair_id ");
            sql.Append("BEGIN ");
            if (kind != "INSERT") Totals(sql, "OLD", "-");
            if (kind != "DELETE") Totals(sql, "NEW", "+");
            sql.Append(" END;");
        }
        return sql.ToString();
    }

    private static void Totals(StringBuilder sql, string row, string sign)
    {
        var held = $"({row}.journal_accounted=1 AND {row}.closure_utc IS NULL)";
        foreach (var table in new[] { "installation", "sync_pair" })
        {
            sql.Append($"UPDATE {table} SET journal_count=journal_count{sign}{row}.journal_accounted," +
                $"journal_bytes=journal_bytes{sign}({row}.journal_accounted*{row}.journal_encoded_bytes)," +
                $"journal_held_count=journal_held_count{sign}{held}," +
                $"journal_held_bytes=journal_held_bytes{sign}({held}*{row}.journal_encoded_bytes)");
            if (table == "installation")
                sql.Append($",journal_global_held_bytes=journal_global_held_bytes{sign}({held}*({row}.pair_id IS NULL)*{row}.journal_encoded_bytes);");
            else sql.Append($" WHERE pair_id={row}.pair_id;");
        }
    }
}
