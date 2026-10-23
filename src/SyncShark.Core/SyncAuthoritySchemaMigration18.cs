namespace Massdex.SyncShark;

// The explicit offline successor carries its source binding and preparation
// progress in the existing installation row. No startup migration/import path
// is introduced; a null receipt keeps existing installations unchanged.
internal static class SyncAuthoritySchemaMigration18
{
    public const int PreviousVersion = 17;
    public const string Sql = """
        ALTER TABLE installation ADD COLUMN essential_successor_json TEXT
            CHECK(essential_successor_json IS NULL OR json_valid(essential_successor_json));
        CREATE TRIGGER IF NOT EXISTS essential_successor_receipt_guard
        BEFORE UPDATE OF essential_successor_json ON installation
        WHEN OLD.essential_successor_json IS NOT NEW.essential_successor_json
        BEGIN
            SELECT CASE WHEN syncshark_successor_receipt_allowed(
                OLD.essential_successor_json,NEW.essential_successor_json)<>1
                THEN RAISE(ABORT,'Essential successor receipt requires exact private preparation authority.') END;
        END;
        """;
}
