namespace Massdex.SyncShark;

internal static class SyncAuthoritySchemaMigration12
{
    public const int PreviousVersion = 11;

    // Additive and narrow: exactly two new STRICT tables (consent_header, consent_item), five read or
    // identity indexes, two nullable columns on the EXISTING operation table, two nullable/checked
    // linkage columns on the EXISTING command_request table, one reverse-reference index and the
    // enforcement triggers that keep the new relation exact. No retained table, column, row, index,
    // trigger, view or constraint is created, dropped, rewritten or relaxed, so every legacy command
    // request, installation identity, operation, attempt, receipt, allocation, consent-free lease and
    // history row keeps its identity and its exact bytes. Adding a column creates no new schema object,
    // so the exact current object inventory is the version 11 inventory plus the consent objects, the
    // three new operation/command indexes and the consent enforcement triggers.
    //
    // The normalized ORIGINAL reviewed selection lives here exactly once. consent_header holds one
    // bounded header per original approval: the exact UTF-8 request fingerprint of the approved
    // original serialization, the original choice count, the ordered-choice digest and every original
    // non-Choices plan field/root/cut/direction/approval datum in one immutable header_json. The
    // original count and digest are the retained truth and are never shrunk when an item expires.
    // consent_item holds one complete immutable original choice_json per original ordinal, with the
    // exact original relative path, action and expected group kept as lean indexed columns so retry
    // eligibility, list and grouping read short rows instead of decoding every original choice body.
    // relative_path_key carries the existing ordinal-ignore-case/PilotPath identity computed in managed
    // code, and the unique (consent_id, relative_path_key) index is the original duplicate-path refusal.
    //
    // Lifecycle. consent_header is 'building' while chunks are being retained, which is NEVER approval:
    // no operation and no retained command may name a building header, and the triggers below refuse it.
    // One atomic seal verifies the exact total, count and ordered digest and marks the header 'sealed';
    // the command-acceptance transaction then requires that exact sealed header, binds command_request.
    // consent_id to it and moves it to 'active'. An incomplete candidate is only a non-authorizing
    // building row that a later bounded retirement step may treat as unissued; it is never resumed by a
    // different reservation. Later maintenance marks 'partially_expired' and then 'closed' after
    // removing only provably unneeded item bodies; this migration performs no expiry and no deletion.
    //
    // Per-item lifecycle is marked at the EXISTING durable boundaries by trigger, never from terminal
    // command status: the first retained operation attempt of the exact operation that carries this
    // consent item marks the item 'issued', and that operation reaching the retained 'completed' status
    // marks it 'completed'. An 'issued' item whose operation is still uncertain stays 'issued' and pins
    // only its own item, header and journal dependencies.
    //
    // operation.consent_id/consent_item_ordinal carry the exact original item authority of a reviewed
    // operation. SQLite cannot add a table-level composite FOREIGN KEY with ALTER TABLE, and a rebuild
    // of the referenced operation table is impossible inside this migration's transaction because
    // PRAGMA foreign_keys is a no-op there, so the composite reference is expressed as its exact
    // enforced equivalent: a RESTRICT foreign key on the header identity, a checked non-negative
    // ordinal, and the triggers below that refuse a non-existent (consent_id, ordinal) pair, a building
    // header, a half-set pair and any later change, plus a delete-side trigger that refuses removing an
    // item any retained operation still references. Legacy operations keep both columns NULL.
    public const string Sql = """
        ALTER TABLE observation_cut ADD COLUMN sealed_input_digest TEXT
            CHECK(sealed_input_digest IS NULL OR length(sealed_input_digest) = 64);

        CREATE TABLE IF NOT EXISTS consent_header (
            consent_id TEXT PRIMARY KEY,
            originating_command_id TEXT,
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            original_request_sha256 TEXT NOT NULL
                CHECK(length(original_request_sha256) = 64 AND original_request_sha256 NOT GLOB '*[^0-9a-f]*'),
            original_choice_count INTEGER NOT NULL CHECK(original_choice_count >= 0),
            original_choice_digest TEXT NOT NULL
                CHECK(length(original_choice_digest) = 64 AND original_choice_digest NOT GLOB '*[^0-9a-f]*'),
            header_json TEXT NOT NULL CHECK(json_valid(header_json)),
            header_encoded_bytes INTEGER NOT NULL CHECK(header_encoded_bytes >= 0),
            prepared_choice_count INTEGER NOT NULL DEFAULT 0 CHECK(prepared_choice_count >= 0),
            prepared_choice_bytes INTEGER NOT NULL DEFAULT 0 CHECK(prepared_choice_bytes >= 0),
            prepared_command_id TEXT,
            lifecycle TEXT NOT NULL
                CHECK(lifecycle IN ('building','sealed','active','inactive','partially_expired','closed')),
            expired_choice_count INTEGER NOT NULL DEFAULT 0 CHECK(expired_choice_count >= 0),
            created_utc TEXT NOT NULL,
            sealed_utc TEXT,
            last_use_utc TEXT,
            CHECK(prepared_choice_count <= original_choice_count),
            CHECK(expired_choice_count <= original_choice_count),
            CHECK(lifecycle = 'building' OR sealed_utc IS NOT NULL)
        ) STRICT;

        CREATE UNIQUE INDEX IF NOT EXISTS consent_header_originating_command
            ON consent_header(originating_command_id) WHERE originating_command_id IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS consent_header_prepared_command
            ON consent_header(prepared_command_id) WHERE prepared_command_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS consent_header_pair_lifecycle
            ON consent_header(pair_id, lifecycle);

        CREATE TABLE IF NOT EXISTS consent_item (
            consent_id TEXT NOT NULL REFERENCES consent_header(consent_id) ON DELETE RESTRICT,
            ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
            relative_path_key TEXT NOT NULL,
            original_relative_path TEXT NOT NULL,
            original_expected_group TEXT NOT NULL,
            original_action TEXT NOT NULL,
            choice_json TEXT NOT NULL CHECK(json_valid(choice_json)),
            choice_sha256 TEXT NOT NULL
                CHECK(length(choice_sha256) = 64 AND choice_sha256 NOT GLOB '*[^0-9a-f]*'),
            encoded_bytes INTEGER NOT NULL CHECK(encoded_bytes >= 0),
            lifecycle TEXT NOT NULL CHECK(lifecycle IN ('eligible','issued','completed','expired')),
            issued_utc TEXT,
            completed_utc TEXT,
            expired_utc TEXT,
            PRIMARY KEY(consent_id, ordinal)
        ) STRICT;

        CREATE UNIQUE INDEX IF NOT EXISTS consent_item_path
            ON consent_item(consent_id, relative_path_key);
        CREATE INDEX IF NOT EXISTS consent_item_lifecycle
            ON consent_item(consent_id, lifecycle, ordinal);

        ALTER TABLE operation ADD COLUMN consent_id TEXT
            REFERENCES consent_header(consent_id) ON DELETE RESTRICT;

        ALTER TABLE operation ADD COLUMN consent_item_ordinal INTEGER
            CHECK(consent_item_ordinal IS NULL OR consent_item_ordinal >= 0);

        CREATE INDEX IF NOT EXISTS operation_consent_item
            ON operation(consent_id, consent_item_ordinal) WHERE consent_id IS NOT NULL;

        ALTER TABLE command_request ADD COLUMN consent_id TEXT
            REFERENCES consent_header(consent_id) ON DELETE RESTRICT;

        CREATE INDEX IF NOT EXISTS command_request_consent
            ON command_request(consent_id) WHERE consent_id IS NOT NULL;

        CREATE TRIGGER IF NOT EXISTS consent_header_identity_immutable
        BEFORE UPDATE ON consent_header
        WHEN NOT (NEW.consent_id IS OLD.consent_id
                  AND NEW.originating_command_id IS OLD.originating_command_id
                  AND NEW.pair_id IS OLD.pair_id
                  AND NEW.original_request_sha256 IS OLD.original_request_sha256
                  AND NEW.original_choice_count IS OLD.original_choice_count
                  AND NEW.original_choice_digest IS OLD.original_choice_digest
                  AND NEW.header_json IS OLD.header_json
                  AND NEW.header_encoded_bytes IS OLD.header_encoded_bytes
                  AND NEW.prepared_command_id IS OLD.prepared_command_id
                  AND NEW.created_utc IS OLD.created_utc)
        BEGIN
            SELECT RAISE(ABORT, 'consent header identity, original digest and original count are immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS consent_header_progress_monotonic
        BEFORE UPDATE ON consent_header
        WHEN NEW.prepared_choice_count < OLD.prepared_choice_count
          OR NEW.prepared_choice_bytes < OLD.prepared_choice_bytes
          OR NEW.expired_choice_count < OLD.expired_choice_count
          OR (CASE NEW.lifecycle WHEN 'building' THEN 0 WHEN 'sealed' THEN 1 WHEN 'active' THEN 2
                                 WHEN 'inactive' THEN 3 WHEN 'partially_expired' THEN 4 ELSE 5 END)
             < (CASE OLD.lifecycle WHEN 'building' THEN 0 WHEN 'sealed' THEN 1 WHEN 'active' THEN 2
                                   WHEN 'inactive' THEN 3 WHEN 'partially_expired' THEN 4 ELSE 5 END)
        BEGIN
            SELECT RAISE(ABORT, 'consent preparation progress and lifecycle may only advance');
        END;

        CREATE TRIGGER IF NOT EXISTS consent_item_body_immutable
        BEFORE UPDATE ON consent_item
        WHEN NOT (NEW.consent_id IS OLD.consent_id
                  AND NEW.ordinal IS OLD.ordinal
                  AND NEW.relative_path_key IS OLD.relative_path_key
                  AND NEW.original_relative_path IS OLD.original_relative_path
                  AND NEW.original_expected_group IS OLD.original_expected_group
                  AND NEW.original_action IS OLD.original_action
                  AND NEW.choice_json IS OLD.choice_json
                  AND NEW.choice_sha256 IS OLD.choice_sha256
                  AND NEW.encoded_bytes IS OLD.encoded_bytes)
        BEGIN
            SELECT RAISE(ABORT, 'consent item identity and original choice bytes are immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS consent_item_lifecycle_monotonic
        BEFORE UPDATE OF lifecycle, issued_utc, completed_utc, expired_utc ON consent_item
        WHEN NOT (NEW.lifecycle IS OLD.lifecycle
                  OR (OLD.lifecycle = 'eligible' AND NEW.lifecycle IN ('issued','expired'))
                  OR (OLD.lifecycle = 'issued' AND NEW.lifecycle IN ('completed','expired')))
        BEGIN
            SELECT RAISE(ABORT, 'consent item lifecycle may only advance to issued, completed or expired');
        END;

        CREATE TRIGGER IF NOT EXISTS consent_item_operation_reference
        BEFORE DELETE ON consent_item
        WHEN EXISTS (SELECT 1 FROM operation o
                     WHERE o.consent_id = OLD.consent_id AND o.consent_item_ordinal = OLD.ordinal)
        BEGIN
            SELECT RAISE(ABORT, 'a retained operation still references this original consent item');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_consent_pair_insert
        BEFORE INSERT ON operation
        WHEN (NEW.consent_id IS NULL) <> (NEW.consent_item_ordinal IS NULL)
        BEGIN
            SELECT RAISE(ABORT, 'an operation names either both its consent header and original ordinal or neither');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_consent_pair_update
        BEFORE UPDATE OF consent_id, consent_item_ordinal ON operation
        WHEN (NEW.consent_id IS NULL) <> (NEW.consent_item_ordinal IS NULL)
        BEGIN
            SELECT RAISE(ABORT, 'an operation names either both its consent header and original ordinal or neither');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_consent_item_exists_insert
        BEFORE INSERT ON operation
        WHEN NEW.consent_id IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM consent_item i
                          WHERE i.consent_id = NEW.consent_id AND i.ordinal = NEW.consent_item_ordinal)
        BEGIN
            SELECT RAISE(ABORT, 'the operation consent ordinal is not a retained original choice of that consent');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_consent_item_sealed_insert
        BEFORE INSERT ON operation
        WHEN NEW.consent_id IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM consent_header h
                          WHERE h.consent_id = NEW.consent_id AND h.lifecycle <> 'building')
        BEGIN
            SELECT RAISE(ABORT, 'a building consent header is never authority for an operation');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_consent_item_stamped_once
        BEFORE UPDATE OF consent_id, consent_item_ordinal ON operation
        WHEN NOT (OLD.consent_id IS NULL AND OLD.consent_item_ordinal IS NULL
                  AND NEW.consent_id IS NOT NULL AND NEW.consent_item_ordinal IS NOT NULL)
        BEGIN
            SELECT RAISE(ABORT, 'the consent authority of an operation is stamped once and is write-once thereafter');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_consent_item_valid_update
        BEFORE UPDATE OF consent_id, consent_item_ordinal ON operation
        WHEN NEW.consent_id IS NOT NULL
          AND (NOT EXISTS (SELECT 1 FROM consent_item i
                           WHERE i.consent_id = NEW.consent_id AND i.ordinal = NEW.consent_item_ordinal)
               OR NOT EXISTS (SELECT 1 FROM consent_header h
                              WHERE h.consent_id = NEW.consent_id AND h.lifecycle <> 'building'))
        BEGIN
            SELECT RAISE(ABORT, 'the operation consent ordinal or its header is not retained and non-building');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_consent_item_issue_from_attempt
        AFTER INSERT ON operation_attempt
        WHEN EXISTS (SELECT 1 FROM operation o
                     WHERE o.operation_id = NEW.operation_id AND o.consent_id IS NOT NULL)
        BEGIN
            UPDATE consent_item SET lifecycle = 'issued',
                   issued_utc = coalesce(issued_utc, strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')
            WHERE consent_id = (SELECT o.consent_id FROM operation o WHERE o.operation_id = NEW.operation_id)
              AND ordinal = (SELECT o.consent_item_ordinal FROM operation o WHERE o.operation_id = NEW.operation_id)
              AND lifecycle = 'eligible';
            UPDATE consent_header SET last_use_utc = strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z'
            WHERE consent_id = (SELECT o.consent_id FROM operation o WHERE o.operation_id = NEW.operation_id);
        END;

        CREATE TRIGGER IF NOT EXISTS operation_consent_item_complete_from_operation
        AFTER UPDATE OF status ON operation
        WHEN NEW.consent_id IS NOT NULL AND NEW.status = 'completed' AND OLD.status <> 'completed'
        BEGIN
            UPDATE consent_item SET lifecycle = 'completed',
                   completed_utc = coalesce(completed_utc, strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z')
            WHERE consent_id = NEW.consent_id AND ordinal = NEW.consent_item_ordinal
              AND lifecycle IN ('eligible','issued');
        END;

        CREATE TRIGGER IF NOT EXISTS command_request_consent_sealed
        BEFORE INSERT ON command_request
        WHEN NEW.consent_id IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM consent_header h
                          WHERE h.consent_id = NEW.consent_id AND h.lifecycle <> 'building')
        BEGIN
            SELECT RAISE(ABORT, 'a building consent header is never authority for a retained command');
        END;

        CREATE TRIGGER IF NOT EXISTS command_request_consent_immutable
        BEFORE UPDATE OF consent_id ON command_request
        WHEN NOT (NEW.consent_id IS OLD.consent_id)
        BEGIN
            SELECT RAISE(ABORT, 'the consent authority of a retained command is write-once');
        END;
        """;
}
