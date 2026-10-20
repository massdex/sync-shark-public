namespace Massdex.SyncShark;

// Ordinary presentation lifetime only. Legacy rows and effect evidence remain held.
// No retention is enabled by this migration; the installation opt-in remains zero.
internal static class SyncAuthoritySchemaMigration13
{
    public const int PreviousVersion = 12;

    public const string Sql = """
        ALTER TABLE installation ADD COLUMN next_history_sequence INTEGER NOT NULL DEFAULT 1
            CHECK(next_history_sequence > 0);
        ALTER TABLE installation ADD COLUMN history_retention_enabled INTEGER NOT NULL DEFAULT 0
            CHECK(history_retention_enabled IN (0,1));
        ALTER TABLE installation ADD COLUMN history_ordinary_count INTEGER NOT NULL DEFAULT 0
            CHECK(history_ordinary_count >= 0);
        ALTER TABLE installation ADD COLUMN history_ordinary_bytes INTEGER NOT NULL DEFAULT 0
            CHECK(history_ordinary_bytes >= 0);
        ALTER TABLE installation ADD COLUMN history_diagnostic_count INTEGER NOT NULL DEFAULT 0
            CHECK(history_diagnostic_count >= 0);
        ALTER TABLE installation ADD COLUMN history_diagnostic_bytes INTEGER NOT NULL DEFAULT 0
            CHECK(history_diagnostic_bytes >= 0);
        ALTER TABLE installation ADD COLUMN history_expired_count INTEGER NOT NULL DEFAULT 0
            CHECK(history_expired_count >= 0);
        ALTER TABLE installation ADD COLUMN history_expired_through_sequence INTEGER NOT NULL DEFAULT 0
            CHECK(history_expired_through_sequence >= 0);

        UPDATE installation SET next_history_sequence=(
            SELECT CASE WHEN coalesce(max(rowid),0)=9223372036854775807 THEN 0
                        ELSE coalesce(max(rowid),0)+1 END FROM history_event);

        ALTER TABLE command_request ADD COLUMN next_activity_ordinal INTEGER NOT NULL DEFAULT 1
            CHECK(next_activity_ordinal > 0);
        ALTER TABLE command_request ADD COLUMN activity_opened INTEGER NOT NULL DEFAULT 0
            CHECK(activity_opened IN (0,1));
        ALTER TABLE command_request ADD COLUMN activity_closed INTEGER NOT NULL DEFAULT 0
            CHECK(activity_closed IN (0,1));
        ALTER TABLE command_request ADD COLUMN activity_incomplete INTEGER NOT NULL DEFAULT 0
            CHECK(activity_incomplete IN (0,1));

        ALTER TABLE history_event ADD COLUMN producer_command_id TEXT;
        ALTER TABLE history_event ADD COLUMN producer_ordinal INTEGER CHECK(producer_ordinal > 0);
        ALTER TABLE history_event ADD COLUMN retention_class TEXT NOT NULL DEFAULT 'legacy'
            CHECK(retention_class IN ('legacy','safety','ordinary','diagnostic'));
        ALTER TABLE history_event ADD COLUMN encoded_bytes INTEGER NOT NULL DEFAULT 0 CHECK(encoded_bytes >= 0);
        ALTER TABLE history_event ADD COLUMN expires_utc TEXT;

        CREATE UNIQUE INDEX IF NOT EXISTS history_event_producer_ordinal
            ON history_event(producer_command_id,producer_ordinal) WHERE producer_command_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS history_event_retention_class ON history_event(retention_class);
        CREATE INDEX IF NOT EXISTS history_event_expiry ON history_event(expires_utc)
            WHERE retention_class IN ('ordinary','diagnostic');
        CREATE INDEX IF NOT EXISTS command_activity_open ON command_request(status)
            WHERE activity_closed=0 AND activity_opened=1 AND admission_sequence IS NOT NULL;

        CREATE INDEX IF NOT EXISTS command_activity_incomplete ON command_request(command_request_id)
            WHERE activity_incomplete=1;
        CREATE INDEX IF NOT EXISTS history_event_related_run ON history_event(
            coalesce(json_extract(detail_json,'$.RunId'),json_extract(detail_json,'$.RelatedRunId')))
            WHERE event_kind='presentation';

        ALTER TABLE activity_context ADD COLUMN ordinary_references INTEGER NOT NULL DEFAULT 0
            CHECK(ordinary_references >= 0);
        ALTER TABLE activity_context ADD COLUMN diagnostic_references INTEGER NOT NULL DEFAULT 0
            CHECK(diagnostic_references >= 0 AND diagnostic_references <= ordinary_references);

        CREATE TRIGGER IF NOT EXISTS history_sequence_guard BEFORE INSERT ON history_event
        BEGIN
            SELECT CASE WHEN (SELECT count(*) FROM installation)<>1
                OR NEW.rowid<>(SELECT next_history_sequence FROM installation)
                OR NEW.rowid=9223372036854775807
                THEN RAISE(ABORT,'history requires the exact unspent sequence') END;
            SELECT CASE WHEN NEW.retention_class='ordinary' AND
                (NEW.producer_command_id IS NULL OR NEW.producer_ordinal IS NULL
                 OR NEW.event_kind<>'presentation' OR NEW.expires_utc IS NULL OR NEW.encoded_bytes<=0)
                THEN RAISE(ABORT,'ordinary history requires a committed command ordinal') END;
            SELECT CASE WHEN NEW.retention_class='diagnostic' AND
                (NEW.producer_command_id IS NOT NULL OR NEW.producer_ordinal IS NOT NULL
                 OR NEW.event_kind<>'presentation' OR NEW.expires_utc IS NULL OR NEW.encoded_bytes<=0)
                THEN RAISE(ABORT,'runless diagnostic history has no command ordinal') END;
        END;

        CREATE TRIGGER IF NOT EXISTS history_sequence_advance AFTER INSERT ON history_event
        BEGIN
            UPDATE installation SET next_history_sequence=next_history_sequence+1;
        END;

        CREATE TRIGGER IF NOT EXISTS history_ordinary_account_insert AFTER INSERT ON history_event
        WHEN NEW.retention_class IN ('ordinary','diagnostic')
        BEGIN
            UPDATE installation SET
                history_ordinary_count=history_ordinary_count+1,
                history_ordinary_bytes=history_ordinary_bytes+NEW.encoded_bytes+
                    coalesce((SELECT encoded_bytes FROM activity_context
                        WHERE context_key=NEW.activity_context_key AND ordinary_references=0),0),
                history_diagnostic_count=history_diagnostic_count+(NEW.retention_class='diagnostic'),
                history_diagnostic_bytes=history_diagnostic_bytes+
                    CASE WHEN NEW.retention_class='diagnostic' THEN NEW.encoded_bytes+
                        coalesce((SELECT encoded_bytes FROM activity_context
                            WHERE context_key=NEW.activity_context_key AND diagnostic_references=0),0)
                    ELSE 0 END;
            UPDATE activity_context SET ordinary_references=ordinary_references+1,
                diagnostic_references=diagnostic_references+(NEW.retention_class='diagnostic')
                WHERE context_key=NEW.activity_context_key;
        END;

        DROP TRIGGER history_no_delete;
        CREATE TRIGGER history_no_delete BEFORE DELETE ON history_event
        BEGIN
            SELECT CASE WHEN NOT(
                OLD.retention_class='diagnostic' OR
                (OLD.retention_class='ordinary' AND OLD.producer_command_id IS NOT NULL AND OLD.producer_ordinal IS NOT NULL))
                OR OLD.event_kind<>'presentation'
                OR syncshark_history_retirement_allowed(OLD.rowid,OLD.history_event_id)<>1
                THEN RAISE(ABORT,'history retirement is not authorized for this exact ordinary row') END;
        END;

        CREATE TRIGGER IF NOT EXISTS history_ordinary_account_delete AFTER DELETE ON history_event
        BEGIN
            UPDATE installation SET
                history_ordinary_count=history_ordinary_count-1,
                history_ordinary_bytes=history_ordinary_bytes-OLD.encoded_bytes-
                    coalesce((SELECT encoded_bytes FROM activity_context
                        WHERE context_key=OLD.activity_context_key AND ordinary_references=1),0),
                history_diagnostic_count=history_diagnostic_count-(OLD.retention_class='diagnostic'),
                history_diagnostic_bytes=history_diagnostic_bytes-
                    CASE WHEN OLD.retention_class='diagnostic' THEN OLD.encoded_bytes+
                        coalesce((SELECT encoded_bytes FROM activity_context
                            WHERE context_key=OLD.activity_context_key AND diagnostic_references=1),0)
                    ELSE 0 END,
                history_expired_count=history_expired_count+1,
                history_expired_through_sequence=max(history_expired_through_sequence,OLD.rowid);
            UPDATE activity_context SET ordinary_references=ordinary_references-1,
                diagnostic_references=diagnostic_references-(OLD.retention_class='diagnostic')
                WHERE context_key=OLD.activity_context_key;
        END;

        CREATE TRIGGER IF NOT EXISTS activity_context_content_immutable
        BEFORE UPDATE OF context_key,pair_id,run_id,context_json,encoded_bytes,created_utc ON activity_context
        BEGIN
            SELECT RAISE(ABORT,'retained activity context content is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS activity_context_retirement_guard BEFORE DELETE ON activity_context
        BEGIN
            SELECT CASE WHEN OLD.ordinary_references<>0 OR OLD.diagnostic_references<>0
                OR EXISTS(SELECT 1 FROM history_event WHERE activity_context_key=OLD.context_key)
                OR syncshark_history_context_retirement_allowed(OLD.context_key)<>1
                THEN RAISE(ABORT,'activity context retirement is not authorized') END;
        END;
        """;
}

