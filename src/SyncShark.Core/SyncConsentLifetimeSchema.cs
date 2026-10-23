namespace Massdex.SyncShark;

// Compact terminal ordinal facts stay on the original consent header. They are
// not a second approval or a permanent row for every completed action.
internal static class SyncConsentLifetimeSchema
{
    internal const string Sql = """
        ALTER TABLE consent_header ADD COLUMN completed_prefix INTEGER NOT NULL DEFAULT -1
            CHECK(completed_prefix>=-1 AND completed_prefix<original_choice_count);
        ALTER TABLE consent_header ADD COLUMN terminal_ranges_json TEXT NOT NULL DEFAULT '[]'
            CHECK(json_valid(terminal_ranges_json) AND json_type(terminal_ranges_json)='array');
        ALTER TABLE consent_header ADD COLUMN retired_choice_count INTEGER NOT NULL DEFAULT 0
            CHECK(retired_choice_count>=0 AND retired_choice_count<=original_choice_count);
        ALTER TABLE consent_header ADD COLUMN retired_choice_bytes INTEGER NOT NULL DEFAULT 0
            CHECK(retired_choice_bytes>=0);
        ALTER TABLE installation ADD COLUMN consent_retire_frontier_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE installation ADD COLUMN consent_retire_frontier_ordinal INTEGER NOT NULL DEFAULT -1;
        CREATE TRIGGER IF NOT EXISTS consent_terminal_layout_guard
        BEFORE UPDATE OF completed_prefix,terminal_ranges_json,retired_choice_count,retired_choice_bytes ON consent_header
        WHEN NEW.completed_prefix<OLD.completed_prefix OR NEW.retired_choice_count<OLD.retired_choice_count
          OR NEW.retired_choice_bytes<OLD.retired_choice_bytes
          OR syncshark_consent_retirement_allowed(OLD.consent_id,-1)<>1
        BEGIN SELECT RAISE(ABORT,'Consent compaction requires exact private maintenance authority'); END;
        CREATE TRIGGER IF NOT EXISTS consent_item_retirement_guard BEFORE DELETE ON consent_item
        WHEN syncshark_consent_retirement_allowed(OLD.consent_id,OLD.ordinal)<>1
        BEGIN SELECT RAISE(ABORT,'Consent retirement requires exact private maintenance authority'); END;
        CREATE TRIGGER IF NOT EXISTS consent_item_no_retired_reinsertion BEFORE INSERT ON consent_item
        WHEN NOT EXISTS(SELECT 1 FROM consent_header h WHERE h.consent_id=NEW.consent_id
            AND h.lifecycle='building' AND h.completed_prefix=-1 AND h.retired_choice_count=0)
        BEGIN SELECT RAISE(ABORT,'Sealed original consent cannot receive a new or retired ordinal'); END;

        ALTER TABLE consent_header ADD COLUMN normalized_origin_accounted INTEGER NOT NULL DEFAULT 0 CHECK(normalized_origin_accounted IN (0,1));
        ALTER TABLE consent_item ADD COLUMN retained_operation_count INTEGER NOT NULL DEFAULT 0 CHECK(retained_operation_count>=0);
        ALTER TABLE consent_header ADD COLUMN eligible_payload_bytes INTEGER NOT NULL DEFAULT 0 CHECK(eligible_payload_bytes>=0);
        ALTER TABLE consent_header ADD COLUMN active_effect_commands INTEGER NOT NULL DEFAULT 0 CHECK(active_effect_commands>=0);
        ALTER TABLE consent_header ADD COLUMN commands_accounted INTEGER NOT NULL DEFAULT 0 CHECK(commands_accounted IN (0,1));
        ALTER TABLE consent_item ADD COLUMN eligible_bytes_accounted INTEGER NOT NULL DEFAULT 0 CHECK(eligible_bytes_accounted IN (0,1));
        ALTER TABLE installation ADD COLUMN consent_inactive_eligible_bytes INTEGER NOT NULL DEFAULT 0 CHECK(consent_inactive_eligible_bytes>=0);
        ALTER TABLE installation ADD COLUMN consent_retired_items INTEGER NOT NULL DEFAULT 0 CHECK(consent_retired_items>=0);
        ALTER TABLE installation ADD COLUMN consent_retired_bytes INTEGER NOT NULL DEFAULT 0 CHECK(consent_retired_bytes>=0);
        CREATE INDEX IF NOT EXISTS consent_pending_header_accounting ON consent_header(consent_id) WHERE commands_accounted=0;
        CREATE INDEX IF NOT EXISTS consent_pending_item_accounting ON consent_item(consent_id,ordinal) WHERE eligible_bytes_accounted=0;
        CREATE TRIGGER IF NOT EXISTS consent_new_header_accounted AFTER INSERT ON consent_header
        BEGIN UPDATE consent_header SET commands_accounted=1 WHERE consent_id=NEW.consent_id; END;
        CREATE TRIGGER IF NOT EXISTS consent_new_item_accounted AFTER INSERT ON consent_item
        BEGIN UPDATE consent_item SET eligible_bytes_accounted=1 WHERE consent_id=NEW.consent_id AND ordinal=NEW.ordinal; END;
        CREATE TRIGGER IF NOT EXISTS consent_item_payload_update AFTER UPDATE OF lifecycle,eligible_bytes_accounted,retained_operation_count ON consent_item
        BEGIN UPDATE consent_header SET eligible_payload_bytes=eligible_payload_bytes+
            (CASE WHEN NEW.eligible_bytes_accounted=1 AND NEW.lifecycle='eligible' AND NEW.retained_operation_count=0 THEN NEW.encoded_bytes ELSE 0 END)-(CASE WHEN OLD.eligible_bytes_accounted=1 AND OLD.lifecycle='eligible' AND OLD.retained_operation_count=0 THEN OLD.encoded_bytes ELSE 0 END) WHERE consent_id=NEW.consent_id; END;
        CREATE TRIGGER IF NOT EXISTS consent_item_payload_delete AFTER DELETE ON consent_item
        BEGIN UPDATE consent_header SET eligible_payload_bytes=eligible_payload_bytes-
            (CASE WHEN OLD.eligible_bytes_accounted=1 AND OLD.lifecycle='eligible' AND OLD.retained_operation_count=0 THEN OLD.encoded_bytes ELSE 0 END) WHERE consent_id=OLD.consent_id; END;
        CREATE TRIGGER IF NOT EXISTS consent_command_active_insert AFTER INSERT ON command_request
        WHEN NEW.consent_id IS NOT NULL
        BEGIN UPDATE consent_header SET active_effect_commands=active_effect_commands+(CASE WHEN NEW.status IN ('accepted','executing') AND NEW.ordinary_issuance_closed_utc IS NULL AND NEW.admission_purpose IN ('normal_effect','control_allocation') THEN 1 ELSE 0 END)
            WHERE consent_id=NEW.consent_id AND commands_accounted=1;
            UPDATE consent_header SET normalized_origin_accounted=1 WHERE consent_id=NEW.consent_id
              AND originating_command_id=NEW.command_request_id
              AND json_extract(NEW.payload_json,'$.consentId')=consent_id
              AND json_type(NEW.payload_json,'$.reviewedPlan') IS NULL; END;
        CREATE TRIGGER IF NOT EXISTS consent_command_active_update
        AFTER UPDATE OF status,ordinary_issuance_closed_utc,admission_purpose ON command_request
        WHEN NEW.consent_id IS NOT NULL
        BEGIN UPDATE consent_header SET active_effect_commands=active_effect_commands+
            (CASE WHEN NEW.status IN ('accepted','executing') AND NEW.ordinary_issuance_closed_utc IS NULL AND NEW.admission_purpose IN ('normal_effect','control_allocation') THEN 1 ELSE 0 END)-(CASE WHEN OLD.status IN ('accepted','executing') AND OLD.ordinary_issuance_closed_utc IS NULL AND OLD.admission_purpose IN ('normal_effect','control_allocation') THEN 1 ELSE 0 END)
            WHERE consent_id=NEW.consent_id AND commands_accounted=1; END;
        CREATE TRIGGER IF NOT EXISTS consent_command_active_delete AFTER DELETE ON command_request
        WHEN OLD.consent_id IS NOT NULL
        BEGIN UPDATE consent_header SET active_effect_commands=active_effect_commands-(CASE WHEN OLD.status IN ('accepted','executing') AND OLD.ordinary_issuance_closed_utc IS NULL AND OLD.admission_purpose IN ('normal_effect','control_allocation') THEN 1 ELSE 0 END)
            WHERE consent_id=OLD.consent_id AND commands_accounted=1;
            UPDATE consent_header SET normalized_origin_accounted=0 WHERE consent_id=OLD.consent_id
              AND originating_command_id=OLD.command_request_id; END;
        CREATE TRIGGER IF NOT EXISTS consent_inactive_payload_account
        AFTER UPDATE OF eligible_payload_bytes,active_effect_commands,commands_accounted,normalized_origin_accounted,lifecycle ON consent_header
        BEGIN UPDATE installation SET consent_inactive_eligible_bytes=consent_inactive_eligible_bytes+
            (CASE WHEN NEW.commands_accounted=1 AND NEW.normalized_origin_accounted=1 AND NEW.active_effect_commands=0 AND NEW.lifecycle IN ('active','inactive','partially_expired','closed') THEN NEW.eligible_payload_bytes ELSE 0 END)-(CASE WHEN OLD.commands_accounted=1 AND OLD.normalized_origin_accounted=1 AND OLD.active_effect_commands=0 AND OLD.lifecycle IN ('active','inactive','partially_expired','closed') THEN OLD.eligible_payload_bytes ELSE 0 END); END;
        CREATE TRIGGER IF NOT EXISTS consent_retirement_account
        AFTER UPDATE OF retired_choice_count,retired_choice_bytes ON consent_header
        BEGIN UPDATE installation SET consent_retired_items=consent_retired_items+NEW.retired_choice_count-OLD.retired_choice_count,
            consent_retired_bytes=consent_retired_bytes+NEW.retired_choice_bytes-OLD.retired_choice_bytes; END;

        CREATE TRIGGER IF NOT EXISTS consent_operation_reference_insert AFTER INSERT ON operation
        WHEN NEW.consent_id IS NOT NULL
        BEGIN UPDATE consent_item SET retained_operation_count=retained_operation_count+1
            WHERE consent_id=NEW.consent_id AND ordinal=NEW.consent_item_ordinal AND eligible_bytes_accounted=1; END;
        CREATE TRIGGER IF NOT EXISTS consent_operation_reference_stamp AFTER UPDATE OF consent_id,consent_item_ordinal ON operation
        WHEN OLD.consent_id IS NULL AND NEW.consent_id IS NOT NULL
        BEGIN UPDATE consent_item SET retained_operation_count=retained_operation_count+1
            WHERE consent_id=NEW.consent_id AND ordinal=NEW.consent_item_ordinal AND eligible_bytes_accounted=1; END;
        CREATE TRIGGER IF NOT EXISTS consent_operation_reference_delete AFTER DELETE ON operation
        WHEN OLD.consent_id IS NOT NULL
        BEGIN UPDATE consent_item SET retained_operation_count=retained_operation_count-1
            WHERE consent_id=OLD.consent_id AND ordinal=OLD.consent_item_ordinal AND eligible_bytes_accounted=1; END;
        """;
}
