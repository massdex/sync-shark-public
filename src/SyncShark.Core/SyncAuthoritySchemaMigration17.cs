namespace Massdex.SyncShark;

internal static class SyncAuthoritySchemaMigration17
{
    public const int PreviousVersion=16;
    public const string Sql="""
        ALTER TABLE monitor_cursor ADD COLUMN writer_epoch TEXT;
        ALTER TABLE monitor_cursor ADD COLUMN committed_batch_sequence INTEGER NOT NULL DEFAULT 0 CHECK(committed_batch_sequence>=0);
        ALTER TABLE monitor_cursor ADD COLUMN last_batch_digest TEXT;
        ALTER TABLE monitor_cursor ADD COLUMN last_batch_prior_cursor TEXT;
        ALTER TABLE installation ADD COLUMN next_monitor_hint_sequence INTEGER NOT NULL DEFAULT 1 CHECK(next_monitor_hint_sequence>0);
        ALTER TABLE installation ADD COLUMN monitor_retained_count INTEGER NOT NULL DEFAULT 0 CHECK(monitor_retained_count>=0);
        ALTER TABLE installation ADD COLUMN monitor_retained_bytes INTEGER NOT NULL DEFAULT 0 CHECK(monitor_retained_bytes>=0);
        ALTER TABLE installation ADD COLUMN monitor_expired_count INTEGER NOT NULL DEFAULT 0 CHECK(monitor_expired_count>=0);
        ALTER TABLE installation ADD COLUMN monitor_lifetime_frontier TEXT NOT NULL DEFAULT '';
        ALTER TABLE monitor_event ADD COLUMN batch_epoch TEXT;
        ALTER TABLE monitor_event ADD COLUMN batch_sequence INTEGER CHECK(batch_sequence IS NULL OR batch_sequence>0);
        ALTER TABLE monitor_event ADD COLUMN hint_sequence INTEGER CHECK(hint_sequence IS NULL OR hint_sequence>0);
        ALTER TABLE monitor_event ADD COLUMN lifetime_class TEXT NOT NULL DEFAULT 'legacy' CHECK(lifetime_class IN ('legacy','eligible'));
        ALTER TABLE monitor_event ADD COLUMN encoded_bytes INTEGER NOT NULL DEFAULT 0 CHECK(encoded_bytes>=0);
        ALTER TABLE dirty_item ADD COLUMN latest_hint_sequence INTEGER NOT NULL DEFAULT 0 CHECK(latest_hint_sequence>=0);
        ALTER TABLE dirty_item ADD COLUMN closed_utc TEXT;
        CREATE INDEX IF NOT EXISTS monitor_lifetime_candidates ON monitor_event(lifetime_class,received_utc,monitor_event_id);
        CREATE INDEX IF NOT EXISTS monitor_cursor_legacy ON monitor_event(monitor_cursor_id,lifetime_class) WHERE consumed_utc IS NULL;
        CREATE INDEX IF NOT EXISTS monitor_legacy_frontier ON monitor_event(lifetime_class,monitor_event_id);
        CREATE INDEX IF NOT EXISTS scan_pair_status ON scan_generation(pair_id,status);
        CREATE INDEX IF NOT EXISTS monitor_pair_hint_sequence ON monitor_event(pair_id,hint_sequence) WHERE consumed_utc IS NULL;
        CREATE INDEX IF NOT EXISTS dirty_pair_hint_sequence ON dirty_item(pair_id,status,latest_hint_sequence);
        CREATE INDEX IF NOT EXISTS dirty_closed_retention ON dirty_item(closed_utc,dirty_item_id) WHERE status='closed';
        CREATE INDEX IF NOT EXISTS dirty_decision_reference ON reconciliation_decision(dirty_item_id) WHERE dirty_item_id IS NOT NULL;
        CREATE TRIGGER IF NOT EXISTS monitor_lifetime_insert AFTER INSERT ON monitor_event WHEN NEW.lifetime_class='eligible'
        BEGIN UPDATE installation SET monitor_retained_count=monitor_retained_count+1,
            monitor_retained_bytes=monitor_retained_bytes+NEW.encoded_bytes; END;
        CREATE TRIGGER IF NOT EXISTS monitor_lifetime_adopt AFTER UPDATE OF lifetime_class ON monitor_event
        WHEN OLD.lifetime_class='legacy' AND NEW.lifetime_class='eligible'
        BEGIN UPDATE installation SET monitor_retained_count=monitor_retained_count+1,
            monitor_retained_bytes=monitor_retained_bytes+NEW.encoded_bytes; END;
        CREATE TRIGGER IF NOT EXISTS monitor_lifetime_delete BEFORE DELETE ON monitor_event
        BEGIN SELECT CASE WHEN OLD.lifetime_class<>'eligible'
            OR syncshark_object_retirement_allowed('monitor_event',OLD.monitor_event_id)<>1
            THEN RAISE(ABORT,'Monitor diagnostics require exact finite retirement authority.') END; END;
        CREATE TRIGGER IF NOT EXISTS monitor_lifetime_deleted AFTER DELETE ON monitor_event
        BEGIN UPDATE installation SET monitor_retained_count=monitor_retained_count-1,
            monitor_retained_bytes=monitor_retained_bytes-OLD.encoded_bytes,monitor_expired_count=monitor_expired_count+1; END;
        CREATE TRIGGER IF NOT EXISTS monitor_immutable_evidence BEFORE UPDATE OF
            monitor_cursor_id,pair_id,source_event_key,normalized_path_key,object_identity_json,event_kind,event_json,
            received_utc,batch_epoch,batch_sequence,hint_sequence ON monitor_event
        BEGIN SELECT RAISE(ABORT,'Monitor batch evidence is immutable.'); END;
        CREATE TRIGGER IF NOT EXISTS dirty_lifetime_delete BEFORE DELETE ON dirty_item
        BEGIN SELECT CASE WHEN OLD.status<>'closed'
            OR syncshark_object_retirement_allowed('dirty_item',OLD.dirty_item_id)<>1
            THEN RAISE(ABORT,'Dirty state requires exact closed retirement authority.') END; END;
        """;
}

