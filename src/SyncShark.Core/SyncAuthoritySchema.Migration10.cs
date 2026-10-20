namespace Massdex.SyncShark;

internal static class SyncAuthoritySchemaMigration10
{
    public const int PreviousVersion = 9;

    // Additive and narrow: exactly one new STRICT table, two read indexes and one nullable column on the
    // EXISTING history_event table. No retained table, column, row, index, trigger, view or constraint is
    // created, dropped, rewritten or relaxed, so every legacy observation, compact cut, interval, delta,
    // membership, version, scan, synchronization point, journal row and presentation event keeps its
    // identity and its exact bytes. Adding a column creates no new schema object, so the exact current
    // object inventory is the version 9 inventory plus activity_context and its two read indexes.
    //
    // activity_context holds ONE immutable detached snapshot of an activity endpoint context. It is
    // deliberately NOT an ordinary presentation event: a retained record whose context is referenced here
    // never depends on the lifetime of a neighbouring event, so an older anchor row can expire for its own
    // reasons without erasing a newer record's historical presentation context. A snapshot is addressed by
    // the lowercase-hex SHA-256 of its exact validated group text (pair id, the optional run id and the
    // complete canonical context serialization). The CHECK keeps that identity exactly sixty-four lowercase
    // hexadecimal characters, so no other spelling of the same bytes can ever be retained.
    //
    // The snapshot is descriptive presentation data only. It grants no effect, consent or authority, and it
    // carries only the contexts the existing append validator already accepts for the record it serves.
    //
    // history_event.activity_context_key is nullable: NULL is the retained meaning of every legacy row and
    // of a record that genuinely has no endpoint context. A non-NULL value names one retained snapshot
    // under ON DELETE RESTRICT, so the database itself refuses to remove a snapshot while any retained
    // event still references it. The partial index serves the direct reverse-reference question (which
    // retained events need this exact snapshot?) without scanning history, and it deliberately does not
    // cover the NULL majority.
    //
    // This migration creates the lifetime structure only. It enables no retirement, deletes no context and
    // rewrites no original row: removing a snapshot stays a separate bounded maintenance step that the
    // reverse reference can authorise only once zero retained events name it.
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS activity_context (
            context_key TEXT PRIMARY KEY
                CHECK(length(context_key) = 64 AND context_key NOT GLOB '*[^0-9a-f]*'),
            pair_id TEXT NOT NULL,
            run_id TEXT,
            context_json TEXT NOT NULL CHECK(json_valid(context_json)),
            encoded_bytes INTEGER NOT NULL CHECK(encoded_bytes >= 0),
            created_utc TEXT NOT NULL
        ) STRICT;

        CREATE INDEX IF NOT EXISTS activity_context_pair_run
            ON activity_context(pair_id, run_id);

        ALTER TABLE history_event ADD COLUMN activity_context_key TEXT
            REFERENCES activity_context(context_key) ON DELETE RESTRICT;

        CREATE INDEX IF NOT EXISTS history_event_activity_context
            ON history_event(activity_context_key) WHERE activity_context_key IS NOT NULL;
        """;
}
