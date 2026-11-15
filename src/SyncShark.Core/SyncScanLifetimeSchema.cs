namespace Massdex.SyncShark;

internal static class SyncScanLifetimeSchema
{
    internal const string Sql = """
        ALTER TABLE scan_generation ADD COLUMN ended_utc TEXT;
        CREATE INDEX IF NOT EXISTS scan_generation_ended ON scan_generation(status,ended_utc,scan_generation_id)
            WHERE ended_utc IS NOT NULL;
        CREATE TRIGGER IF NOT EXISTS failed_scan_cannot_reopen BEFORE UPDATE OF status ON scan_generation
        WHEN OLD.status IN ('failed','superseded') AND NEW.status IN ('incomplete','complete')
        BEGIN SELECT RAISE(ABORT,'An abandoned inventory cannot reopen or become complete'); END;
        """;
}
