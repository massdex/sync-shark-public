namespace Massdex.SyncShark;

internal static class SyncAuthoritySchemaMigration6
{
    public const int PreviousVersion = 5;

    // Additive and narrow: recovery_root_binding gains exactly one nullable column and its two recovery
    // indices are recreated under their existing names. No table is rebuilt, no foreign key is dropped or
    // disabled, and every retained row keeps its identity and every other value byte for byte.
    //
    // The new column is NULL for every existing row, and NULL means "the actual corpus of this retained
    // recovery root has not been verified yet". It is never My Drive, never a namespace and never any
    // other inferred corpus, and the column can never be emptied again once a corpus is verified. The
    // column CHECK keeps a corpus opaque but non-empty when it is known; the provider identity of a row
    // continues to come only from its registered provider_account row.
    //
    // Both recovery indices keep their leading columns and gain coalesce(provider_corpus_id,'') after the
    // account discriminator, so active/generation uniqueness is now scoped to one exact installation, one
    // compatibility side, one cloud account AND one corpus. A retained NULL corpus coalesces to the empty
    // string, so the grandfathered unverified rows keep exactly their previous uniqueness and no existing
    // row becomes valid twice.
    public const string Sql = """
        ALTER TABLE recovery_root_binding ADD COLUMN provider_corpus_id TEXT
            CHECK(provider_corpus_id IS NULL OR length(provider_corpus_id) > 0);
        DROP INDEX IF EXISTS one_active_recovery_root_per_side;
        DROP INDEX IF EXISTS recovery_root_generation_per_account;
        CREATE UNIQUE INDEX IF NOT EXISTS one_active_recovery_root_per_side
            ON recovery_root_binding(installation_id,side,coalesce(provider_account_id,''),
                coalesce(provider_corpus_id,'')) WHERE status='active';
        CREATE UNIQUE INDEX IF NOT EXISTS recovery_root_generation_per_account
            ON recovery_root_binding(installation_id,side,coalesce(provider_account_id,''),
                coalesce(provider_corpus_id,''),generation);
        """;
}
