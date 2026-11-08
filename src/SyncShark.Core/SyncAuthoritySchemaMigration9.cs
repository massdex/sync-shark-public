namespace Massdex.SyncShark;

internal static class SyncAuthoritySchemaMigration9
{
    public const int PreviousVersion = 8;

    // Additive and narrow: exactly three new STRICT tables, three read indexes, two append-only
    // triggers and one read-only VIEW. No retained table, column, row, index, trigger or constraint
    // is created, dropped, rewritten or relaxed, so every legacy observation, membership, version,
    // scan, synchronization point and journal row keeps its identity and its exact bytes.
    //
    // observation_cut holds one bounded header per compact scan generation: the captured parent
    // complete scan, the sealed complete input count, the full cut digest and the publication state.
    // observation_interval holds append-only per-key generation ranges. observation_delta holds one
    // pending upsert or removal per candidate scan and key. The effective_observation view projects
    // exactly the nine retained observation column names for legacy scans, published compact cuts and
    // sealed compact candidates; the observation table keeps every legacy row and every legacy write.
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS observation_cut (
            cut_id TEXT PRIMARY KEY,
            scan_generation_id TEXT NOT NULL UNIQUE REFERENCES scan_generation(scan_generation_id) ON DELETE RESTRICT,
            root_binding_id TEXT NOT NULL REFERENCES root_binding(root_binding_id) ON DELETE RESTRICT,
            side TEXT NOT NULL CHECK(side IN ('local','dropbox')),
            generation INTEGER NOT NULL CHECK(generation > 0),
            captured_parent_scan_id TEXT REFERENCES scan_generation(scan_generation_id) ON DELETE RESTRICT,
            captured_parent_generation INTEGER CHECK(captured_parent_generation IS NULL OR captured_parent_generation > 0),
            state TEXT NOT NULL CHECK(state IN ('open','sealed','published')),
            sealed_input_count INTEGER CHECK(sealed_input_count IS NULL OR sealed_input_count >= 0),
            cut_digest TEXT NOT NULL CHECK(length(cut_digest) = 64),
            created_utc TEXT NOT NULL,
            sealed_utc TEXT,
            published_utc TEXT,
            CHECK((captured_parent_scan_id IS NULL) = (captured_parent_generation IS NULL)),
            CHECK(state <> 'published' OR published_utc IS NOT NULL),
            CHECK(state = 'open' OR (sealed_input_count IS NOT NULL AND sealed_utc IS NOT NULL))
        ) STRICT;

        CREATE TABLE IF NOT EXISTS observation_interval (
            interval_id TEXT PRIMARY KEY,
            root_binding_id TEXT NOT NULL REFERENCES root_binding(root_binding_id) ON DELETE RESTRICT,
            side TEXT NOT NULL CHECK(side IN ('local','dropbox')),
            semantic_key TEXT NOT NULL,
            semantic_digest TEXT NOT NULL CHECK(length(semantic_digest) = 64),
            from_generation INTEGER NOT NULL CHECK(from_generation > 0),
            to_generation INTEGER CHECK(to_generation IS NULL OR to_generation > 0),
            membership_id TEXT REFERENCES membership(membership_id) ON DELETE RESTRICT,
            object_version_id TEXT REFERENCES object_version(object_version_id) ON DELETE RESTRICT,
            evidence_json TEXT NOT NULL,
            observation_id TEXT NOT NULL,
            observed_utc TEXT NOT NULL,
            UNIQUE(root_binding_id, semantic_key, from_generation),
            CHECK(to_generation IS NULL OR to_generation > from_generation),
            CHECK((membership_id IS NULL) = (object_version_id IS NULL))
        ) STRICT;

        CREATE UNIQUE INDEX IF NOT EXISTS observation_interval_one_open
            ON observation_interval(root_binding_id, semantic_key) WHERE to_generation IS NULL;
        CREATE INDEX IF NOT EXISTS observation_interval_range
            ON observation_interval(root_binding_id, from_generation, to_generation);

        CREATE TRIGGER IF NOT EXISTS observation_interval_no_delete
        BEFORE DELETE ON observation_interval
        BEGIN
            SELECT RAISE(ABORT, 'Observation intervals are append-only; retention and pruning remain a separate bounded step.');
        END;

        CREATE TRIGGER IF NOT EXISTS observation_interval_close_only
        BEFORE UPDATE ON observation_interval
        WHEN NOT (OLD.to_generation IS NULL AND NEW.to_generation IS NOT NULL
                  AND NEW.interval_id IS OLD.interval_id
                  AND NEW.root_binding_id IS OLD.root_binding_id
                  AND NEW.side IS OLD.side
                  AND NEW.semantic_key IS OLD.semantic_key
                  AND NEW.semantic_digest IS OLD.semantic_digest
                  AND NEW.from_generation IS OLD.from_generation
                  AND NEW.membership_id IS OLD.membership_id
                  AND NEW.object_version_id IS OLD.object_version_id
                  AND NEW.evidence_json IS OLD.evidence_json
                  AND NEW.observation_id IS OLD.observation_id
                  AND NEW.observed_utc IS OLD.observed_utc)
        BEGIN
            SELECT RAISE(ABORT, 'Observation intervals are append-only except one checked null-to-exclusive-end close.');
        END;

        CREATE TABLE IF NOT EXISTS observation_delta (
            delta_id TEXT PRIMARY KEY,
            cut_id TEXT NOT NULL REFERENCES observation_cut(cut_id) ON DELETE RESTRICT,
            scan_generation_id TEXT NOT NULL REFERENCES scan_generation(scan_generation_id) ON DELETE RESTRICT,
            root_binding_id TEXT NOT NULL REFERENCES root_binding(root_binding_id) ON DELETE RESTRICT,
            side TEXT NOT NULL CHECK(side IN ('local','dropbox')),
            semantic_key TEXT NOT NULL,
            semantic_digest TEXT NOT NULL CHECK(length(semantic_digest) = 64),
            kind TEXT NOT NULL CHECK(kind IN ('upsert','remove')),
            interval_id TEXT,
            expected_prior_interval_id TEXT REFERENCES observation_interval(interval_id) ON DELETE RESTRICT,
            membership_id TEXT REFERENCES membership(membership_id) ON DELETE RESTRICT,
            object_version_id TEXT REFERENCES object_version(object_version_id) ON DELETE RESTRICT,
            evidence_json TEXT,
            UNIQUE(scan_generation_id, semantic_key),
            CHECK((kind = 'upsert' AND interval_id IS NOT NULL AND evidence_json IS NOT NULL
                   AND (membership_id IS NULL) = (object_version_id IS NULL))
               OR (kind = 'remove' AND interval_id IS NULL AND evidence_json IS NULL
                   AND membership_id IS NULL AND object_version_id IS NULL))
        ) STRICT;

        CREATE INDEX IF NOT EXISTS observation_delta_candidate
            ON observation_delta(scan_generation_id, kind, semantic_key);

        CREATE VIEW IF NOT EXISTS effective_observation(
            observation_id, scan_generation_id, root_binding_id, scan_generation, complete_generation,
            object_version_id, membership_id, evidence_json, observed_utc) AS
        SELECT o.observation_id, o.scan_generation_id, o.root_binding_id, o.scan_generation,
               o.complete_generation, o.object_version_id, o.membership_id, o.evidence_json, o.observed_utc
        FROM observation o
        UNION ALL
        SELECT 'observation-' || c.cut_digest || '-' || i.semantic_digest, c.scan_generation_id,
               c.root_binding_id, c.generation, 1, i.object_version_id, i.membership_id,
               i.evidence_json, c.published_utc
        FROM observation_cut c
        JOIN observation_interval i ON i.root_binding_id = c.root_binding_id
          AND i.from_generation <= c.generation
          AND (i.to_generation IS NULL OR i.to_generation > c.generation)
        WHERE c.state = 'published'
        UNION ALL
        SELECT 'observation-' || c.cut_digest || '-' || i.semantic_digest, c.scan_generation_id,
               c.root_binding_id, c.generation, 0, i.object_version_id, i.membership_id,
               i.evidence_json, c.sealed_utc
        FROM observation_cut c
        JOIN observation_interval i ON i.root_binding_id = c.root_binding_id
          AND i.from_generation <= c.captured_parent_generation
          AND (i.to_generation IS NULL OR i.to_generation > c.captured_parent_generation)
        WHERE c.state = 'sealed'
          AND NOT EXISTS (SELECT 1 FROM observation_delta d
                          WHERE d.cut_id = c.cut_id AND d.semantic_key = i.semantic_key)
        UNION ALL
        SELECT 'observation-' || c.cut_digest || '-' || d.semantic_digest, c.scan_generation_id,
               c.root_binding_id, c.generation, 0, d.object_version_id, d.membership_id,
               d.evidence_json, c.sealed_utc
        FROM observation_cut c
        JOIN observation_delta d ON d.cut_id = c.cut_id
        WHERE c.state = 'sealed' AND d.kind = 'upsert';
        """;
}
