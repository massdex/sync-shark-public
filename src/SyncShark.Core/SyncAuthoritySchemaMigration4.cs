namespace Massdex.SyncShark;

internal static class SyncAuthoritySchemaMigration4
{
    public const int PreviousVersion = 3;
    // Only this small authority table is rebuilt, inside the existing database transaction.
    // No table has an incoming foreign key to it; all row identities and values are copied.
    // Outgoing installation/account foreign keys stay enabled throughout the migration.
    public const string Sql = """
        CREATE TABLE recovery_root_binding_account_scope (
            recovery_root_binding_id TEXT PRIMARY KEY,
            installation_id TEXT NOT NULL REFERENCES installation(installation_id) ON DELETE RESTRICT,
            provider_account_id TEXT REFERENCES provider_account(provider_account_id) ON DELETE RESTRICT,
            side TEXT NOT NULL CHECK(side IN ('local','dropbox')),
            display_path TEXT NOT NULL,
            normalized_path_key TEXT NOT NULL,
            identity_json TEXT NOT NULL CHECK(json_valid(identity_json)),
            generation INTEGER NOT NULL CHECK(generation > 0),
            status TEXT NOT NULL CHECK(status IN ('active','unavailable','identity_changed','retired','blocked')),
            verified_utc TEXT NOT NULL
        ) STRICT;
        INSERT INTO recovery_root_binding_account_scope
            SELECT recovery_root_binding_id,installation_id,provider_account_id,side,display_path,
                   normalized_path_key,identity_json,generation,status,verified_utc FROM recovery_root_binding;
        DROP TABLE recovery_root_binding;
        ALTER TABLE recovery_root_binding_account_scope RENAME TO recovery_root_binding;
        CREATE UNIQUE INDEX IF NOT EXISTS one_active_recovery_root_per_side
            ON recovery_root_binding(installation_id,side,coalesce(provider_account_id,'')) WHERE status='active';
        CREATE UNIQUE INDEX IF NOT EXISTS recovery_root_generation_per_account
            ON recovery_root_binding(installation_id,side,coalesce(provider_account_id,''),generation);
        """;
}
