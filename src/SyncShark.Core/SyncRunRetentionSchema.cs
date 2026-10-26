namespace Massdex.SyncShark;

// Derived accounting stays on existing commands and installation. It is never
// effect authority; exact closure and reverse references are rechecked on delete.
internal static class SyncRunRetentionSchema
{
    internal const string EncodedBytes = """
        length(CAST(payload_json AS BLOB))+coalesce(length(CAST(result_json AS BLOB)),0)
        +length(CAST(command_request_id AS BLOB))+length(CAST(command_kind AS BLOB))
        +coalesce(length(CAST(pair_id AS BLOB)),0)+coalesce(length(CAST(target_id AS BLOB)),0)
        +length(CAST(requested_utc AS BLOB))+length(CAST(status AS BLOB))
        +coalesce(length(CAST(terminal_utc AS BLOB)),0)
        """;
    internal static readonly string Sql = """
        ALTER TABLE command_request ADD COLUMN summary_accounted INTEGER NOT NULL DEFAULT 0 CHECK(summary_accounted IN (0,1));
        ALTER TABLE command_request ADD COLUMN summary_bytes INTEGER NOT NULL DEFAULT 0 CHECK(summary_bytes>=0);
        ALTER TABLE command_request ADD COLUMN summary_eligible INTEGER NOT NULL DEFAULT 0 CHECK(summary_eligible IN (0,1));
        ALTER TABLE command_request ADD COLUMN summary_noop INTEGER NOT NULL DEFAULT 0 CHECK(summary_noop IN (0,1));
        ALTER TABLE installation ADD COLUMN run_summary_count INTEGER NOT NULL DEFAULT 0 CHECK(run_summary_count>=0);
        ALTER TABLE installation ADD COLUMN run_summary_bytes INTEGER NOT NULL DEFAULT 0 CHECK(run_summary_bytes>=0);
        ALTER TABLE installation ADD COLUMN run_noop_count INTEGER NOT NULL DEFAULT 0 CHECK(run_noop_count>=0);
        ALTER TABLE installation ADD COLUMN run_summary_expired INTEGER NOT NULL DEFAULT 0 CHECK(run_summary_expired>=0);
        ALTER TABLE installation ADD COLUMN run_summary_frontier_utc TEXT NOT NULL DEFAULT '';
        ALTER TABLE installation ADD COLUMN run_summary_frontier_id TEXT NOT NULL DEFAULT '';
        CREATE INDEX IF NOT EXISTS run_summary_pending ON command_request(command_request_id)
            WHERE summary_accounted=0 AND command_kind IN ('run_pair','run_all');
        CREATE INDEX IF NOT EXISTS run_summary_scan ON command_request(terminal_utc,command_request_id)
            WHERE terminal_utc IS NOT NULL AND command_kind IN ('run_pair','run_all');
        CREATE INDEX IF NOT EXISTS run_summary_eligible ON command_request(terminal_utc,command_request_id)
            WHERE summary_eligible=1;
        CREATE INDEX IF NOT EXISTS run_summary_held ON command_request(command_request_id)
            WHERE command_kind IN ('run_pair','run_all') AND terminal_utc IS NOT NULL AND summary_eligible=0;
        CREATE INDEX IF NOT EXISTS command_retry_origin ON command_request(json_extract(payload_json,'$.retryOfRunId'));
        CREATE INDEX IF NOT EXISTS command_reviewed_origin ON command_request(json_extract(payload_json,'$.reviewedConsent.commandId'));
        CREATE INDEX IF NOT EXISTS lease_command_identity ON lease(command_request_id) WHERE command_request_id IS NOT NULL;
        CREATE TRIGGER IF NOT EXISTS run_summary_insert AFTER INSERT ON command_request
        WHEN NEW.command_kind IN ('run_pair','run_all')
        BEGIN UPDATE command_request SET summary_bytes=
        """ + EncodedBytes + """
          ,summary_accounted=1 WHERE command_request_id=NEW.command_request_id;
          UPDATE command_request SET summary_eligible=0
            WHERE command_request_id=json_extract(NEW.payload_json,'$.retryOfRunId')
               OR command_request_id=json_extract(NEW.payload_json,'$.reviewedConsent.commandId');
        END;
        CREATE TRIGGER IF NOT EXISTS run_summary_change
        AFTER UPDATE OF payload_json,result_json,status,terminal_utc,ordinary_issuance_closed_utc ON command_request
        WHEN NEW.command_kind IN ('run_pair','run_all')
        BEGIN UPDATE command_request SET summary_bytes=
        """ + EncodedBytes + """
          ,summary_accounted=1,summary_eligible=0 WHERE command_request_id=NEW.command_request_id; END;
        CREATE TRIGGER IF NOT EXISTS run_summary_counter_change
        AFTER UPDATE OF summary_eligible,summary_bytes,summary_noop ON command_request
        BEGIN UPDATE installation SET
            run_summary_count=run_summary_count+NEW.summary_eligible-OLD.summary_eligible,
            run_summary_bytes=run_summary_bytes+NEW.summary_eligible*NEW.summary_bytes-OLD.summary_eligible*OLD.summary_bytes,
            run_noop_count=run_noop_count+NEW.summary_eligible*NEW.summary_noop-OLD.summary_eligible*OLD.summary_noop;
        END;
        CREATE TRIGGER IF NOT EXISTS run_summary_counter_delete AFTER DELETE ON command_request
        BEGIN UPDATE installation SET run_summary_count=run_summary_count-OLD.summary_eligible,
            run_summary_bytes=run_summary_bytes-OLD.summary_eligible*OLD.summary_bytes,
            run_noop_count=run_noop_count-OLD.summary_eligible*OLD.summary_noop,
            run_summary_expired=run_summary_expired+CASE WHEN OLD.command_kind IN ('run_pair','run_all') THEN 1 ELSE 0 END; END;
        CREATE TRIGGER IF NOT EXISTS run_summary_operation_pin AFTER INSERT ON operation
        BEGIN UPDATE command_request SET summary_eligible=0
            WHERE command_request_id IN (NEW.origin_command_id,NEW.execution_command_id); END;
        CREATE TRIGGER IF NOT EXISTS run_summary_adoption_pin AFTER UPDATE OF execution_command_id ON operation
        BEGIN UPDATE command_request SET summary_eligible=0 WHERE command_request_id=NEW.execution_command_id; END;
        CREATE TRIGGER IF NOT EXISTS run_summary_lease_pin AFTER INSERT ON lease
        BEGIN UPDATE command_request SET summary_eligible=0 WHERE command_request_id=NEW.command_request_id; END;
        CREATE TRIGGER IF NOT EXISTS run_summary_consent_pin AFTER INSERT ON consent_header
        BEGIN UPDATE command_request SET summary_eligible=0
            WHERE command_request_id IN (NEW.originating_command_id,NEW.prepared_command_id); END;
        CREATE TRIGGER IF NOT EXISTS command_retirement_guard BEFORE DELETE ON command_request
        BEGIN SELECT CASE WHEN syncshark_run_retirement_allowed(OLD.command_request_id)<>1
          THEN RAISE(ABORT,'Command retirement requires exact closed authority and reference proof') END; END;

        ALTER TABLE consent_header ADD COLUMN retirement_started_utc TEXT;
        ALTER TABLE command_request ADD COLUMN consent_detail_expired_utc TEXT;
        ALTER TABLE installation ADD COLUMN closed_consent_frontier_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE installation ADD COLUMN consent_expired_headers INTEGER NOT NULL DEFAULT 0 CHECK(consent_expired_headers>=0);
        CREATE INDEX IF NOT EXISTS consent_complete_detail ON consent_header(consent_id)
          WHERE original_choice_count=retired_choice_count AND lifecycle NOT IN ('building','sealed');
        CREATE INDEX IF NOT EXISTS command_consent_closure ON command_request(consent_id,ordinary_issuance_closed_utc)
          WHERE consent_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS command_consent_unclosed ON command_request(consent_id)
          WHERE consent_id IS NOT NULL AND (ordinary_issuance_closed_utc IS NULL OR status IN ('requested','accepted','executing')
            OR (activity_opened=1 AND activity_closed=0));
        DROP TRIGGER command_request_consent_immutable;
        CREATE TRIGGER IF NOT EXISTS command_request_consent_immutable BEFORE UPDATE OF consent_id ON command_request
        WHEN NOT (NEW.consent_id IS OLD.consent_id) AND NOT (
          OLD.consent_id IS NOT NULL AND NEW.consent_id IS NULL AND NEW.consent_detail_expired_utc IS NOT NULL
          AND syncshark_closed_consent_retirement_allowed('command',OLD.command_request_id)=1)
        BEGIN SELECT RAISE(ABORT,'The consent authority of a retained command is write-once'); END;
        CREATE TRIGGER IF NOT EXISTS command_consent_expiry_guard BEFORE UPDATE OF consent_detail_expired_utc ON command_request
        WHEN NEW.consent_detail_expired_utc IS NOT OLD.consent_detail_expired_utc AND (
          OLD.consent_detail_expired_utc IS NOT NULL OR NEW.consent_detail_expired_utc IS NULL
          OR syncshark_closed_consent_retirement_allowed('command',OLD.command_request_id)<>1)
        BEGIN SELECT RAISE(ABORT,'Consent expiry requires exact closed selection authority'); END;
        CREATE TRIGGER IF NOT EXISTS consent_retirement_start_guard BEFORE UPDATE OF retirement_started_utc ON consent_header
        WHEN NEW.retirement_started_utc IS NOT OLD.retirement_started_utc AND (
          OLD.retirement_started_utc IS NOT NULL OR NEW.retirement_started_utc IS NULL
          OR syncshark_closed_consent_retirement_allowed('header',OLD.consent_id)<>1)
        BEGIN SELECT RAISE(ABORT,'Closed selection retirement requires exact proof'); END;
        CREATE TRIGGER IF NOT EXISTS closed_consent_new_reference BEFORE INSERT ON command_request
        WHEN NEW.consent_id IS NOT NULL AND EXISTS(SELECT 1 FROM consent_header
          WHERE consent_id=NEW.consent_id AND retirement_started_utc IS NOT NULL)
        BEGIN SELECT RAISE(ABORT,'The original selection has expired; review a new selection'); END;
        CREATE TRIGGER IF NOT EXISTS closed_consent_delete_guard BEFORE DELETE ON consent_header
        BEGIN SELECT CASE WHEN syncshark_closed_consent_retirement_allowed('header',OLD.consent_id)<>1
          THEN RAISE(ABORT,'Closed selection deletion requires exact retirement proof') END; END;
        CREATE TRIGGER IF NOT EXISTS closed_consent_detach AFTER UPDATE OF consent_id ON command_request
        WHEN OLD.consent_id IS NOT NULL AND NEW.consent_id IS NULL
        BEGIN UPDATE consent_header SET normalized_origin_accounted=0
          WHERE consent_id=OLD.consent_id AND originating_command_id=OLD.command_request_id; END;
        """;
}
