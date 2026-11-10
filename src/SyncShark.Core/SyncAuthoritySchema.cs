namespace Massdex.SyncShark;

internal static class SyncAuthoritySchema
{
    public const int Version = 18;

    public const string Sql = """
        CREATE TABLE IF NOT EXISTS schema_migration (
            schema_version INTEGER PRIMARY KEY,
            applied_utc TEXT NOT NULL,
            source_revision TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS authority_guard (
            singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
            transitions_sealed INTEGER NOT NULL CHECK(transitions_sealed IN (0, 1))
        ) STRICT;
        INSERT OR IGNORE INTO authority_guard(singleton, transitions_sealed) VALUES(1, 0);

        CREATE TABLE IF NOT EXISTS installation (
            installation_id TEXT PRIMARY KEY,
            database_generation TEXT NOT NULL UNIQUE,
            created_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS capability_profile (
            capability_profile_id TEXT PRIMARY KEY,
            source_revision TEXT NOT NULL,
            profile_json TEXT NOT NULL,
            created_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS sync_pair (
            pair_id TEXT PRIMARY KEY,
            configuration_revision INTEGER NOT NULL CHECK(configuration_revision > 0),
            state TEXT NOT NULL CHECK(state IN ('discovery_required', 'active', 'paused', 'blocked', 'retired')),
            created_utc TEXT NOT NULL,
            pair_json TEXT CHECK(pair_json IS NULL OR json_valid(pair_json)),
            selected INTEGER NOT NULL DEFAULT 0 CHECK(selected IN (0, 1))
        ) STRICT;

        CREATE UNIQUE INDEX IF NOT EXISTS one_selected_sync_pair
            ON sync_pair(selected) WHERE selected = 1;

        CREATE TABLE IF NOT EXISTS root_binding (
            root_binding_id TEXT PRIMARY KEY,
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            side TEXT NOT NULL CHECK(side IN ('local', 'dropbox', 'local_control', 'dropbox_control')),
            generation INTEGER NOT NULL CHECK(generation > 0),
            identity_json TEXT NOT NULL,
            display_path TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('active', 'superseded', 'blocked', 'out_of_scope')),
            observed_utc TEXT NOT NULL,
            UNIQUE(pair_id, side, generation)
        ) STRICT;

        CREATE UNIQUE INDEX IF NOT EXISTS one_active_root_per_pair_side
            ON root_binding(pair_id, side) WHERE state = 'active';

        CREATE TABLE IF NOT EXISTS logical_object (
            object_id TEXT PRIMARY KEY,
            object_kind TEXT NOT NULL CHECK(object_kind IN ('file', 'directory')),
            created_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS object_version (
            object_version_id TEXT PRIMARY KEY,
            object_id TEXT NOT NULL REFERENCES logical_object(object_id) ON DELETE RESTRICT,
            side TEXT NOT NULL CHECK(side IN ('local', 'dropbox')),
            identity_json TEXT NOT NULL,
            content_hash TEXT,
            size_bytes INTEGER CHECK(size_bytes IS NULL OR size_bytes >= 0),
            observed_utc TEXT NOT NULL,
            UNIQUE(object_id, side, identity_json)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS membership (
            membership_id TEXT PRIMARY KEY,
            object_id TEXT NOT NULL REFERENCES logical_object(object_id) ON DELETE RESTRICT,
            root_binding_id TEXT NOT NULL REFERENCES root_binding(root_binding_id) ON DELETE RESTRICT,
            relative_path_key TEXT NOT NULL,
            generation INTEGER NOT NULL CHECK(generation > 0),
            state TEXT NOT NULL CHECK(state IN ('active', 'provisional', 'retired', 'out_of_scope', 'ambiguous')),
            observed_utc TEXT NOT NULL,
            UNIQUE(root_binding_id, relative_path_key, generation)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS observation (
            observation_id TEXT PRIMARY KEY,
            scan_generation_id TEXT REFERENCES scan_generation(scan_generation_id) ON DELETE RESTRICT,
            root_binding_id TEXT NOT NULL REFERENCES root_binding(root_binding_id) ON DELETE RESTRICT,
            scan_generation INTEGER NOT NULL CHECK(scan_generation > 0),
            complete_generation INTEGER NOT NULL CHECK(complete_generation IN (0, 1)),
            object_version_id TEXT REFERENCES object_version(object_version_id) ON DELETE RESTRICT,
            membership_id TEXT REFERENCES membership(membership_id) ON DELETE RESTRICT,
            evidence_json TEXT NOT NULL,
            observed_utc TEXT NOT NULL
        ) STRICT;
        CREATE UNIQUE INDEX IF NOT EXISTS observation_scan_membership
            ON observation(scan_generation_id, membership_id)
            WHERE scan_generation_id IS NOT NULL AND membership_id IS NOT NULL;

        CREATE TABLE IF NOT EXISTS synchronization_point (
            synchronization_point_id TEXT PRIMARY KEY,
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            object_id TEXT NOT NULL REFERENCES logical_object(object_id) ON DELETE RESTRICT,
            local_object_version_id TEXT REFERENCES object_version(object_version_id) ON DELETE RESTRICT,
            dropbox_object_version_id TEXT REFERENCES object_version(object_version_id) ON DELETE RESTRICT,
            local_membership_id TEXT REFERENCES membership(membership_id) ON DELETE RESTRICT,
            dropbox_membership_id TEXT REFERENCES membership(membership_id) ON DELETE RESTRICT,
            accepted_utc TEXT NOT NULL,
            authority_generation INTEGER NOT NULL CHECK(authority_generation > 0),
            UNIQUE(pair_id, object_id, authority_generation)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS operation_transition (
            from_status TEXT NOT NULL,
            to_status TEXT NOT NULL,
            PRIMARY KEY(from_status, to_status)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS attempt_transition (
            from_status TEXT NOT NULL,
            to_status TEXT NOT NULL,
            PRIMARY KEY(from_status, to_status)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS allocation_transition (
            from_status TEXT NOT NULL,
            to_status TEXT NOT NULL,
            PRIMARY KEY(from_status, to_status)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS operation (
            operation_id TEXT PRIMARY KEY,
            pair_id TEXT REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            object_id TEXT REFERENCES logical_object(object_id) ON DELETE RESTRICT,
            kind TEXT NOT NULL CHECK(kind IN ('create_local','create_remote','update_local','update_remote','delete_local','delete_remote','resolve_conflict','move_local','move_remote','cross_pair_move','allocate_control','cleanup','reconcile')),
            status TEXT NOT NULL CHECK(status IN ('planned','ready','leased','validating','allocation_authorized','staged','effect_issuing','effect_unknown','effect_observed','reconciling','verifying','committed','cleanup_pending','completed','blocked','cancelled','superseded')),
            priority INTEGER NOT NULL,
            configuration_revision INTEGER NOT NULL CHECK(configuration_revision > 0),
            capability_profile_id TEXT NOT NULL REFERENCES capability_profile(capability_profile_id) ON DELETE RESTRICT,
            idempotency_key TEXT NOT NULL UNIQUE,
            preconditions_json TEXT NOT NULL,
            current_lease_id TEXT,
            current_fencing_token INTEGER CHECK(current_fencing_token IS NULL OR current_fencing_token > 0),
            last_transition_lease_id TEXT,
            last_transition_fencing_token INTEGER CHECK(last_transition_fencing_token IS NULL OR last_transition_fencing_token > 0),
            created_utc TEXT NOT NULL,
            ready_utc TEXT,
            terminal_utc TEXT,
            block_code TEXT,
            block_detail TEXT,
            operation_group_id TEXT REFERENCES operation_group(operation_group_id) ON DELETE RESTRICT,
            CHECK((current_lease_id IS NULL) = (current_fencing_token IS NULL)),
            CHECK((last_transition_lease_id IS NULL) = (last_transition_fencing_token IS NULL)),
            CHECK((status IN ('completed','cancelled','superseded') AND terminal_utc IS NOT NULL)
               OR (status NOT IN ('completed','cancelled','superseded') AND terminal_utc IS NULL))
        ) STRICT;

        CREATE TABLE IF NOT EXISTS operation_member (
            operation_member_id TEXT PRIMARY KEY,
            operation_id TEXT NOT NULL REFERENCES operation(operation_id) ON DELETE RESTRICT,
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            object_id TEXT NOT NULL REFERENCES logical_object(object_id) ON DELETE RESTRICT,
            role TEXT NOT NULL CHECK(role IN ('source','destination','affected','lock','recovery')),
            root_binding_id TEXT NOT NULL REFERENCES root_binding(root_binding_id) ON DELETE RESTRICT,
            membership_id TEXT REFERENCES membership(membership_id) ON DELETE RESTRICT,
            object_version_id TEXT REFERENCES object_version(object_version_id) ON DELETE RESTRICT,
            expected_membership_generation INTEGER CHECK(expected_membership_generation IS NULL OR expected_membership_generation > 0),
            expected_path_key TEXT,
            expected_state_json TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS operation_dependency (
            operation_id TEXT NOT NULL REFERENCES operation(operation_id) ON DELETE RESTRICT,
            depends_on_operation_id TEXT NOT NULL REFERENCES operation(operation_id) ON DELETE RESTRICT,
            dependency_kind TEXT NOT NULL CHECK(dependency_kind IN ('must_complete','must_commit','must_resolve')),
            PRIMARY KEY(operation_id, depends_on_operation_id),
            CHECK(operation_id <> depends_on_operation_id)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS resource_allocation (
            allocation_id TEXT PRIMARY KEY,
            operation_id TEXT NOT NULL REFERENCES operation(operation_id) ON DELETE RESTRICT,
            control_namespace_id TEXT NOT NULL,
            resource_kind TEXT NOT NULL CHECK(resource_kind IN ('transaction_directory','stage_file','local_recovery_file','remote_recovery_file','upload_session','control_namespace')),
            deterministic_name TEXT NOT NULL,
            intended_parent_identity TEXT NOT NULL,
            owner_nonce_hash TEXT NOT NULL CHECK(length(owner_nonce_hash) = 64),
            status TEXT NOT NULL CHECK(status IN ('authorized','creating','created','bound','verified','released','blocked')),
            bound_identity TEXT,
            content_hash TEXT,
            size_bytes INTEGER CHECK(size_bytes IS NULL OR size_bytes >= 0),
            transition_lease_id TEXT REFERENCES lease(lease_id) ON DELETE RESTRICT,
            transition_fencing_token INTEGER CHECK(transition_fencing_token IS NULL OR transition_fencing_token > 0),
            created_utc TEXT NOT NULL,
            CHECK((transition_lease_id IS NULL) = (transition_fencing_token IS NULL)),
            UNIQUE(control_namespace_id, intended_parent_identity, deterministic_name)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS lease_fence (
            scope_kind TEXT NOT NULL,
            scope_id TEXT NOT NULL,
            last_token INTEGER NOT NULL CHECK(last_token > 0),
            PRIMARY KEY(scope_kind, scope_id)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS lease (
            lease_id TEXT PRIMARY KEY,
            scope_kind TEXT NOT NULL CHECK(scope_kind IN ('operation','pair','object','provider_namespace','control_namespace','global_writer')),
            scope_id TEXT NOT NULL,
            worker_id TEXT NOT NULL,
            fencing_token INTEGER NOT NULL CHECK(fencing_token > 0),
            acquired_utc TEXT NOT NULL,
            expires_utc TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('active','released','expired','revoked'))
        ) STRICT;

        CREATE UNIQUE INDEX IF NOT EXISTS one_active_lease_per_scope
            ON lease(scope_kind, scope_id) WHERE status = 'active';

        CREATE TABLE IF NOT EXISTS operation_attempt (
            attempt_id TEXT PRIMARY KEY,
            operation_id TEXT NOT NULL REFERENCES operation(operation_id) ON DELETE RESTRICT,
            attempt_no INTEGER NOT NULL CHECK(attempt_no > 0),
            lease_id TEXT NOT NULL REFERENCES lease(lease_id) ON DELETE RESTRICT,
            fencing_token INTEGER NOT NULL CHECK(fencing_token > 0),
            transition_lease_id TEXT NOT NULL REFERENCES lease(lease_id) ON DELETE RESTRICT,
            transition_fencing_token INTEGER NOT NULL CHECK(transition_fencing_token > 0),
            request_fingerprint TEXT NOT NULL CHECK(length(request_fingerprint) = 64),
            effect_kind TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('started','request_sent','response_received','effect_unknown','verified_success','verified_not_applied','blocked','abandoned')),
            started_utc TEXT NOT NULL,
            ended_utc TEXT,
            UNIQUE(operation_id, attempt_no)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS effect_receipt (
            effect_receipt_id TEXT PRIMARY KEY,
            attempt_id TEXT NOT NULL REFERENCES operation_attempt(attempt_id) ON DELETE RESTRICT,
            recorded_lease_id TEXT NOT NULL REFERENCES lease(lease_id) ON DELETE RESTRICT,
            recorded_fencing_token INTEGER NOT NULL CHECK(recorded_fencing_token > 0),
            certainty TEXT NOT NULL CHECK(certainty IN ('known_success','known_not_applied','unknown','known_conflict','known_partial')),
            source_identity_json TEXT NOT NULL,
            destination_identity_json TEXT NOT NULL,
            provider_metadata_json TEXT NOT NULL,
            evidence_digest TEXT NOT NULL CHECK(length(evidence_digest) = 64),
            observed_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS history_event (
            history_event_id TEXT PRIMARY KEY,
            operation_id TEXT REFERENCES operation(operation_id) ON DELETE RESTRICT,
            event_kind TEXT NOT NULL,
            detail_json TEXT NOT NULL,
            occurred_utc TEXT NOT NULL
        ) STRICT;

        CREATE TRIGGER IF NOT EXISTS authority_guard_no_update_after_seal
        BEFORE UPDATE ON authority_guard
        FOR EACH ROW WHEN OLD.transitions_sealed = 1
        BEGIN
            SELECT RAISE(ABORT, 'authority guard is immutable after transition seal');
        END;

        CREATE TRIGGER IF NOT EXISTS authority_guard_no_delete
        BEFORE DELETE ON authority_guard
        BEGIN
            SELECT RAISE(ABORT, 'authority guard cannot be deleted');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_transition_no_insert_after_seal
        BEFORE INSERT ON operation_transition
        FOR EACH ROW WHEN (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 1
        BEGIN
            SELECT RAISE(ABORT, 'operation transition allowlist is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_transition_no_update
        BEFORE UPDATE ON operation_transition
        BEGIN
            SELECT RAISE(ABORT, 'operation transition allowlist is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_transition_no_delete
        BEFORE DELETE ON operation_transition
        BEGIN
            SELECT RAISE(ABORT, 'operation transition allowlist is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS attempt_transition_no_insert_after_seal
        BEFORE INSERT ON attempt_transition
        FOR EACH ROW WHEN (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 1
        BEGIN
            SELECT RAISE(ABORT, 'attempt transition allowlist is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS attempt_transition_no_update
        BEFORE UPDATE ON attempt_transition
        BEGIN
            SELECT RAISE(ABORT, 'attempt transition allowlist is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS attempt_transition_no_delete
        BEFORE DELETE ON attempt_transition
        BEGIN
            SELECT RAISE(ABORT, 'attempt transition allowlist is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS allocation_transition_no_insert_after_seal
        BEFORE INSERT ON allocation_transition
        FOR EACH ROW WHEN (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 1
        BEGIN
            SELECT RAISE(ABORT, 'allocation transition allowlist is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS allocation_transition_no_update
        BEFORE UPDATE ON allocation_transition
        BEGIN
            SELECT RAISE(ABORT, 'allocation transition allowlist is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS allocation_transition_no_delete
        BEFORE DELETE ON allocation_transition
        BEGIN
            SELECT RAISE(ABORT, 'allocation transition allowlist is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_transition_guard
        BEFORE UPDATE OF status ON operation
        FOR EACH ROW WHEN OLD.status <> NEW.status
        BEGIN
            SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM operation_transition
                WHERE from_status = OLD.status AND to_status = NEW.status
            ) THEN RAISE(ABORT, 'invalid operation state transition') END;
        END;

        CREATE TRIGGER IF NOT EXISTS attempt_transition_guard
        BEFORE UPDATE OF status ON operation_attempt
        FOR EACH ROW WHEN OLD.status <> NEW.status
        BEGIN
            SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM attempt_transition
                WHERE from_status = OLD.status AND to_status = NEW.status
            ) THEN RAISE(ABORT, 'invalid attempt state transition') END;
        END;

        CREATE TRIGGER IF NOT EXISTS allocation_transition_guard
        BEFORE UPDATE OF status ON resource_allocation
        FOR EACH ROW WHEN OLD.status <> NEW.status
        BEGIN
            SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM allocation_transition
                WHERE from_status = OLD.status AND to_status = NEW.status
            ) THEN RAISE(ABORT, 'invalid allocation state transition') END;
        END;

        CREATE TRIGGER IF NOT EXISTS operation_identity_immutable_after_ready
        BEFORE UPDATE OF pair_id, object_id, kind, configuration_revision, capability_profile_id, idempotency_key, preconditions_json
        ON operation
        FOR EACH ROW WHEN OLD.status <> 'planned'
        BEGIN
            SELECT RAISE(ABORT, 'operation identity and preconditions are immutable after ready');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_member_insert_only_while_planned
        BEFORE INSERT ON operation_member
        FOR EACH ROW WHEN (SELECT status FROM operation WHERE operation_id = NEW.operation_id) <> 'planned'
        BEGIN
            SELECT RAISE(ABORT, 'operation members are immutable once work starts');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_member_update_only_while_planned
        BEFORE UPDATE ON operation_member
        FOR EACH ROW WHEN (SELECT status FROM operation WHERE operation_id = OLD.operation_id) <> 'planned'
        BEGIN
            SELECT RAISE(ABORT, 'operation members are immutable once work starts');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_member_delete_only_while_planned
        BEFORE DELETE ON operation_member
        FOR EACH ROW WHEN (SELECT status FROM operation WHERE operation_id = OLD.operation_id) <> 'planned'
        BEGIN
            SELECT RAISE(ABORT, 'operation members are immutable once work starts');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_dependency_insert_only_while_planned
        BEFORE INSERT ON operation_dependency
        FOR EACH ROW WHEN (SELECT status FROM operation WHERE operation_id = NEW.operation_id) <> 'planned'
        BEGIN
            SELECT RAISE(ABORT, 'operation dependencies are immutable once work starts');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_dependency_update_only_while_planned
        BEFORE UPDATE ON operation_dependency
        FOR EACH ROW WHEN (SELECT status FROM operation WHERE operation_id = OLD.operation_id) <> 'planned'
        BEGIN
            SELECT RAISE(ABORT, 'operation dependencies are immutable once work starts');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_dependency_delete_only_while_planned
        BEFORE DELETE ON operation_dependency
        FOR EACH ROW WHEN (SELECT status FROM operation WHERE operation_id = OLD.operation_id) <> 'planned'
        BEGIN
            SELECT RAISE(ABORT, 'operation dependencies are immutable once work starts');
        END;

        CREATE TRIGGER IF NOT EXISTS allocation_insert_only_while_planned
        BEFORE INSERT ON resource_allocation
        FOR EACH ROW WHEN (SELECT status FROM operation WHERE operation_id = NEW.operation_id) <> 'planned'
        BEGIN
            SELECT RAISE(ABORT, 'resource allocation authority must be committed before work starts');
        END;

        CREATE TRIGGER IF NOT EXISTS allocation_identity_immutable
        BEFORE UPDATE OF operation_id, control_namespace_id, resource_kind, deterministic_name, intended_parent_identity, owner_nonce_hash
        ON resource_allocation
        BEGIN
            SELECT RAISE(ABORT, 'resource allocation identity is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS allocation_transition_requires_current_fence
        BEFORE UPDATE OF status ON resource_allocation
        FOR EACH ROW WHEN OLD.status <> NEW.status AND NOT EXISTS (
            SELECT 1
            FROM operation o
            JOIN lease l ON l.lease_id = NEW.transition_lease_id
            WHERE o.operation_id = OLD.operation_id
              AND l.scope_kind = 'operation'
              AND l.scope_id = OLD.operation_id
              AND l.status = 'active'
              AND l.fencing_token = NEW.transition_fencing_token
              AND julianday(l.expires_utc) > julianday('now')
              AND o.current_lease_id = NEW.transition_lease_id
              AND o.current_fencing_token = NEW.transition_fencing_token
        )
        BEGIN
            SELECT RAISE(ABORT, 'allocation transition does not hold the current fenced operation lease');
        END;

        CREATE TRIGGER IF NOT EXISTS allocation_transition_authority_changes_with_status
        BEFORE UPDATE OF transition_lease_id, transition_fencing_token ON resource_allocation
        FOR EACH ROW WHEN OLD.status = NEW.status
        BEGIN
            SELECT RAISE(ABORT, 'allocation transition authority changes only with a status transition');
        END;

        CREATE TRIGGER IF NOT EXISTS no_new_attempt_while_unreconciled
        BEFORE INSERT ON operation_attempt
        FOR EACH ROW WHEN EXISTS (
            SELECT 1 FROM operation_attempt a
            WHERE a.operation_id = NEW.operation_id
              AND a.status IN ('started','request_sent','response_received','effect_unknown')
        )
        BEGIN
            SELECT RAISE(ABORT, 'prior attempt must reach verified terminal reconciliation before retry');
        END;

        CREATE TRIGGER IF NOT EXISTS attempt_requires_current_fence
        BEFORE INSERT ON operation_attempt
        FOR EACH ROW WHEN NOT EXISTS (
            SELECT 1
            FROM lease l
            JOIN operation o ON o.operation_id = NEW.operation_id
            WHERE l.lease_id = NEW.lease_id
              AND l.scope_kind = 'operation'
              AND l.scope_id = NEW.operation_id
              AND l.status = 'active'
              AND l.fencing_token = NEW.fencing_token
              AND julianday(l.expires_utc) > julianday('now')
              AND o.current_lease_id = NEW.lease_id
              AND o.current_fencing_token = NEW.fencing_token
        )
        BEGIN
            SELECT RAISE(ABORT, 'attempt does not hold the current fenced operation lease');
        END;

        CREATE TRIGGER IF NOT EXISTS attempt_update_requires_current_fence
        BEFORE UPDATE OF status ON operation_attempt
        FOR EACH ROW WHEN OLD.status <> NEW.status AND NOT EXISTS (
            SELECT 1
            FROM lease l
            JOIN operation o ON o.operation_id = NEW.operation_id
            WHERE l.lease_id = NEW.transition_lease_id
              AND l.scope_kind = 'operation'
              AND l.scope_id = NEW.operation_id
              AND l.status = 'active'
              AND l.fencing_token = NEW.transition_fencing_token
              AND julianday(l.expires_utc) > julianday('now')
              AND o.current_lease_id = NEW.transition_lease_id
              AND o.current_fencing_token = NEW.transition_fencing_token
        )
        BEGIN
            SELECT RAISE(ABORT, 'attempt transition does not hold the current fenced operation lease');
        END;

        CREATE TRIGGER IF NOT EXISTS receipt_requires_current_fence
        BEFORE INSERT ON effect_receipt
        FOR EACH ROW WHEN NOT EXISTS (
            SELECT 1
            FROM operation_attempt a
            JOIN lease l ON l.lease_id = NEW.recorded_lease_id
            JOIN operation o ON o.operation_id = a.operation_id
            WHERE a.attempt_id = NEW.attempt_id
              AND l.scope_kind = 'operation'
              AND l.scope_id = a.operation_id
              AND l.status = 'active'
              AND l.fencing_token = NEW.recorded_fencing_token
              AND julianday(l.expires_utc) > julianday('now')
              AND o.current_lease_id = NEW.recorded_lease_id
              AND o.current_fencing_token = NEW.recorded_fencing_token
        )
        BEGIN
            SELECT RAISE(ABORT, 'effect receipt does not hold the current fenced operation lease');
        END;

        CREATE TRIGGER IF NOT EXISTS effect_transition_requires_current_fence
        BEFORE UPDATE OF status ON operation
        FOR EACH ROW WHEN OLD.status <> NEW.status AND (
            OLD.status IN ('effect_issuing','effect_unknown','effect_observed','reconciling','verifying','committed','cleanup_pending')
            OR NEW.status IN ('effect_issuing','effect_unknown','effect_observed','reconciling','verifying','committed','cleanup_pending'))
          AND NOT EXISTS (
            SELECT 1 FROM lease l
            WHERE l.lease_id = NEW.last_transition_lease_id
              AND l.scope_kind = 'operation'
              AND l.scope_id = NEW.operation_id
              AND l.status = 'active'
              AND l.fencing_token = NEW.last_transition_fencing_token
              AND julianday(l.expires_utc) > julianday('now')
              AND NEW.current_lease_id = NEW.last_transition_lease_id
              AND NEW.current_fencing_token = NEW.last_transition_fencing_token
        )
        BEGIN
            SELECT RAISE(ABORT, 'operation effect transition does not hold the current fenced lease');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_transition_authority_changes_with_status
        BEFORE UPDATE OF last_transition_lease_id, last_transition_fencing_token ON operation
        FOR EACH ROW WHEN OLD.status = NEW.status
        BEGIN
            SELECT RAISE(ABORT, 'operation transition authority changes only with a status transition');
        END;

        CREATE TRIGGER IF NOT EXISTS attempt_identity_immutable
        BEFORE UPDATE OF operation_id, attempt_no, lease_id, fencing_token, request_fingerprint, effect_kind, started_utc
        ON operation_attempt
        BEGIN
            SELECT RAISE(ABORT, 'attempt identity and request authority are immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS attempt_transition_authority_changes_with_status
        BEFORE UPDATE OF transition_lease_id, transition_fencing_token ON operation_attempt
        FOR EACH ROW WHEN OLD.status = NEW.status
        BEGIN
            SELECT RAISE(ABORT, 'attempt transition authority changes only with a status transition');
        END;

        CREATE TRIGGER IF NOT EXISTS attempt_no_delete
        BEFORE DELETE ON operation_attempt
        BEGIN
            SELECT RAISE(ABORT, 'attempt history is append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS receipt_no_update
        BEFORE UPDATE ON effect_receipt
        BEGIN
            SELECT RAISE(ABORT, 'effect receipts are append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS receipt_no_delete
        BEFORE DELETE ON effect_receipt
        BEGIN
            SELECT RAISE(ABORT, 'effect receipts are append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS observation_no_update
        BEFORE UPDATE ON observation
        BEGIN
            SELECT RAISE(ABORT, 'observations are append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS observation_no_delete
        BEFORE DELETE ON observation
        BEGIN
            SELECT RAISE(ABORT, 'observations are append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS history_no_update
        BEFORE UPDATE ON history_event
        BEGIN
            SELECT RAISE(ABORT, 'history is append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS history_no_delete
        BEFORE DELETE ON history_event
        BEGIN
            SELECT RAISE(ABORT, 'history is append-only');
        END;

        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'planned','ready' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'ready','leased' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'leased','validating' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'leased','effect_issuing' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'validating','allocation_authorized' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'validating','effect_issuing' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'allocation_authorized','staged' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'allocation_authorized','effect_issuing' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'staged','effect_issuing' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'effect_issuing','effect_unknown' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'effect_issuing','effect_observed' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'effect_issuing','blocked' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'effect_unknown','reconciling' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'effect_unknown','committed' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'effect_unknown','ready' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'effect_unknown','blocked' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'effect_observed','reconciling' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'effect_observed','validating' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'effect_observed','committed' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'effect_observed','ready' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'effect_observed','blocked' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'reconciling','verifying' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'reconciling','committed' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'reconciling','ready' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'reconciling','blocked' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'verifying','committed' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'verifying','ready' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'verifying','blocked' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'committed','cleanup_pending' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'committed','completed' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'cleanup_pending','completed' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'cleanup_pending','blocked' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'ready','cancelled' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'planned','cancelled' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'ready','superseded' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO operation_transition(from_status, to_status)
            SELECT 'leased','superseded' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;

        INSERT INTO attempt_transition(from_status, to_status)
            SELECT 'started','request_sent' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO attempt_transition(from_status, to_status)
            SELECT 'started','verified_not_applied' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO attempt_transition(from_status, to_status)
            SELECT 'started','blocked' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO attempt_transition(from_status, to_status)
            SELECT 'request_sent','response_received' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO attempt_transition(from_status, to_status)
            SELECT 'request_sent','effect_unknown' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO attempt_transition(from_status, to_status)
            SELECT 'request_sent','blocked' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO attempt_transition(from_status, to_status)
            SELECT 'response_received','verified_success' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO attempt_transition(from_status, to_status)
            SELECT 'response_received','verified_not_applied' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO attempt_transition(from_status, to_status)
            SELECT 'response_received','blocked' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO attempt_transition(from_status, to_status)
            SELECT 'effect_unknown','verified_success' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO attempt_transition(from_status, to_status)
            SELECT 'effect_unknown','verified_not_applied' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO attempt_transition(from_status, to_status)
            SELECT 'effect_unknown','blocked' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;

        INSERT INTO allocation_transition(from_status, to_status)
            SELECT 'authorized','creating' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO allocation_transition(from_status, to_status)
            SELECT 'authorized','blocked' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO allocation_transition(from_status, to_status)
            SELECT 'creating','created' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO allocation_transition(from_status, to_status)
            SELECT 'creating','blocked' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO allocation_transition(from_status, to_status)
            SELECT 'created','bound' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO allocation_transition(from_status, to_status)
            SELECT 'created','blocked' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO allocation_transition(from_status, to_status)
            SELECT 'bound','verified' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO allocation_transition(from_status, to_status)
            SELECT 'bound','blocked' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;
        INSERT INTO allocation_transition(from_status, to_status)
            SELECT 'verified','released' WHERE (SELECT transitions_sealed FROM authority_guard WHERE singleton = 1) = 0;

        UPDATE authority_guard SET transitions_sealed = 1
            WHERE singleton = 1 AND transitions_sealed = 0;
        """;
}
