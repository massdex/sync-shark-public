namespace Massdex.SyncShark;

internal static class SyncAuthoritySchemaMigration5
{
    public const int PreviousVersion = 4;

    // Only provider_account and its sole referencing recovery_root_binding table are rebuilt, inside
    // the existing database transaction with foreign keys enabled throughout.
    //
    // provider_account's provider column was constrained to the single literal 'dropbox'. The
    // replacement accepts any non-empty registered provider literal, so a Google Drive row can finally
    // be truthful. Every row value and every primary key is copied unchanged; the only later change in
    // this version is the provider literal of a row the shared identity helper classifies as legacy
    // Google Drive.
    //
    // The replacement recovery table is created against the NEW provider table, so renaming the new
    // provider table into the canonical name rewrites that reference. The old child is dropped before
    // the old parent, so no RESTRICT child exists when the parent is dropped. Both recovery indices
    // are recreated with their existing definitions, which is why this version's object inventory is
    // exactly version 4's.
    public const string Sql = """
        CREATE TABLE provider_account_provider_qualified (
            provider_account_id TEXT PRIMARY KEY,
            installation_id TEXT NOT NULL REFERENCES installation(installation_id) ON DELETE RESTRICT,
            provider TEXT NOT NULL CHECK(length(provider) > 0),
            account_id TEXT NOT NULL,
            root_namespace_id TEXT NOT NULL,
            home_namespace_id TEXT NOT NULL,
            display_label TEXT NOT NULL,
            binding_revision INTEGER NOT NULL CHECK(binding_revision > 0),
            verified_utc TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('active','reauth_required','namespace_changed','revoked','retired')),
            UNIQUE(provider, account_id, root_namespace_id)
        ) STRICT;
        INSERT INTO provider_account_provider_qualified
            SELECT provider_account_id,installation_id,provider,account_id,root_namespace_id,home_namespace_id,
                   display_label,binding_revision,verified_utc,status FROM provider_account;

        CREATE TABLE recovery_root_binding_provider_qualified (
            recovery_root_binding_id TEXT PRIMARY KEY,
            installation_id TEXT NOT NULL REFERENCES installation(installation_id) ON DELETE RESTRICT,
            provider_account_id TEXT REFERENCES provider_account_provider_qualified(provider_account_id) ON DELETE RESTRICT,
            side TEXT NOT NULL CHECK(side IN ('local','dropbox')),
            display_path TEXT NOT NULL,
            normalized_path_key TEXT NOT NULL,
            identity_json TEXT NOT NULL CHECK(json_valid(identity_json)),
            generation INTEGER NOT NULL CHECK(generation > 0),
            status TEXT NOT NULL CHECK(status IN ('active','unavailable','identity_changed','retired','blocked')),
            verified_utc TEXT NOT NULL
        ) STRICT;
        INSERT INTO recovery_root_binding_provider_qualified
            SELECT recovery_root_binding_id,installation_id,provider_account_id,side,display_path,
                   normalized_path_key,identity_json,generation,status,verified_utc FROM recovery_root_binding;

        DROP TABLE recovery_root_binding;
        DROP TABLE provider_account;

        ALTER TABLE provider_account_provider_qualified RENAME TO provider_account;
        ALTER TABLE recovery_root_binding_provider_qualified RENAME TO recovery_root_binding;

        CREATE UNIQUE INDEX IF NOT EXISTS one_active_recovery_root_per_side
            ON recovery_root_binding(installation_id,side,coalesce(provider_account_id,'')) WHERE status='active';
        CREATE UNIQUE INDEX IF NOT EXISTS recovery_root_generation_per_account
            ON recovery_root_binding(installation_id,side,coalesce(provider_account_id,''),generation);
        """;
}
