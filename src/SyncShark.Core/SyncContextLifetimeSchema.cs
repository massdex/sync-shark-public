namespace Massdex.SyncShark;

// Descriptive snapshots keep a grace period after their last exact history reference.
// Existing rows are classified incrementally; there is no migration-wide data rewrite.
internal static class SyncContextLifetimeSchema
{
    internal const string Sql = """
        ALTER TABLE activity_context ADD COLUMN lifetime_accounted INTEGER NOT NULL DEFAULT 0
            CHECK(lifetime_accounted IN (0,1));
        ALTER TABLE activity_context ADD COLUMN unreferenced_since_utc TEXT;
        ALTER TABLE installation ADD COLUMN unreferenced_context_count INTEGER NOT NULL DEFAULT 0 CHECK(unreferenced_context_count>=0);
        ALTER TABLE installation ADD COLUMN unreferenced_context_bytes INTEGER NOT NULL DEFAULT 0 CHECK(unreferenced_context_bytes>=0);
        ALTER TABLE installation ADD COLUMN expired_context_count INTEGER NOT NULL DEFAULT 0 CHECK(expired_context_count>=0);
        CREATE INDEX IF NOT EXISTS activity_context_pending_lifetime ON activity_context(context_key)
            WHERE lifetime_accounted=0;
        CREATE INDEX IF NOT EXISTS activity_context_grace ON activity_context(unreferenced_since_utc,context_key)
            WHERE unreferenced_since_utc IS NOT NULL AND encoded_bytes<=1048576;
        CREATE TRIGGER IF NOT EXISTS activity_context_reference_acquired AFTER INSERT ON history_event
        WHEN NEW.activity_context_key IS NOT NULL
        BEGIN
            UPDATE activity_context SET unreferenced_since_utc=NULL,lifetime_accounted=1
                WHERE context_key=NEW.activity_context_key;
        END;
        CREATE TRIGGER IF NOT EXISTS activity_context_reference_released AFTER DELETE ON history_event
        WHEN OLD.activity_context_key IS NOT NULL
        BEGIN
            UPDATE activity_context SET unreferenced_since_utc=NULL,lifetime_accounted=0
                WHERE context_key=OLD.activity_context_key
                  AND NOT EXISTS(SELECT 1 FROM history_event WHERE activity_context_key=OLD.activity_context_key);
        END;
        CREATE TRIGGER IF NOT EXISTS activity_context_grace_account
        AFTER UPDATE OF unreferenced_since_utc ON activity_context
        BEGIN
            UPDATE installation SET
                unreferenced_context_count=unreferenced_context_count
                    +(NEW.unreferenced_since_utc IS NOT NULL)-(OLD.unreferenced_since_utc IS NOT NULL),
                unreferenced_context_bytes=unreferenced_context_bytes
                    +(CASE WHEN NEW.unreferenced_since_utc IS NOT NULL THEN NEW.encoded_bytes ELSE 0 END)
                    -(CASE WHEN OLD.unreferenced_since_utc IS NOT NULL THEN OLD.encoded_bytes ELSE 0 END);
        END;
        CREATE TRIGGER IF NOT EXISTS activity_context_grace_deleted AFTER DELETE ON activity_context
        BEGIN
            UPDATE installation SET
                unreferenced_context_count=unreferenced_context_count-(OLD.unreferenced_since_utc IS NOT NULL),
                unreferenced_context_bytes=unreferenced_context_bytes
                    -(CASE WHEN OLD.unreferenced_since_utc IS NOT NULL THEN OLD.encoded_bytes ELSE 0 END),
                expired_context_count=expired_context_count+1;
        END;
        """;
}
