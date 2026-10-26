namespace Massdex.SyncShark;

internal static class SyncAuthoritySchemaSupplement
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS process_instance (
            process_instance_id TEXT PRIMARY KEY,
            installation_id TEXT NOT NULL REFERENCES installation(installation_id) ON DELETE RESTRICT,
            process_id INTEGER NOT NULL CHECK(process_id > 0),
            executable_sha256 TEXT NOT NULL CHECK(length(executable_sha256) = 64),
            source_revision TEXT NOT NULL,
            started_utc TEXT NOT NULL,
            heartbeat_utc TEXT NOT NULL,
            ended_utc TEXT,
            status TEXT NOT NULL CHECK(status IN ('starting','active','draining','ended','abandoned')),
            UNIQUE(installation_id, process_id, started_utc)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS provider_account (
            provider_account_id TEXT PRIMARY KEY,
            installation_id TEXT NOT NULL REFERENCES installation(installation_id) ON DELETE RESTRICT,
            provider TEXT NOT NULL CHECK(provider = 'dropbox'),
            account_id TEXT NOT NULL,
            root_namespace_id TEXT NOT NULL,
            home_namespace_id TEXT NOT NULL,
            display_label TEXT NOT NULL,
            binding_revision INTEGER NOT NULL CHECK(binding_revision > 0),
            verified_utc TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('active','reauth_required','namespace_changed','revoked','retired')),
            UNIQUE(provider, account_id, root_namespace_id)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS recovery_root_binding (
            recovery_root_binding_id TEXT PRIMARY KEY,
            installation_id TEXT NOT NULL REFERENCES installation(installation_id) ON DELETE RESTRICT,
            provider_account_id TEXT REFERENCES provider_account(provider_account_id) ON DELETE RESTRICT,
            side TEXT NOT NULL CHECK(side IN ('local','dropbox')),
            display_path TEXT NOT NULL,
            normalized_path_key TEXT NOT NULL,
            identity_json TEXT NOT NULL CHECK(json_valid(identity_json)),
            generation INTEGER NOT NULL CHECK(generation > 0),
            status TEXT NOT NULL CHECK(status IN ('active','unavailable','identity_changed','retired','blocked')),
            verified_utc TEXT NOT NULL,
            UNIQUE(installation_id, side, generation)
        ) STRICT;
        CREATE UNIQUE INDEX IF NOT EXISTS one_active_recovery_root_per_side
            ON recovery_root_binding(installation_id, side) WHERE status='active';

        CREATE TABLE IF NOT EXISTS volume (
            volume_id TEXT PRIMARY KEY,
            installation_id TEXT NOT NULL REFERENCES installation(installation_id) ON DELETE RESTRICT,
            volume_serial_hex TEXT NOT NULL,
            filesystem TEXT NOT NULL,
            volume_guid_path TEXT,
            first_seen_utc TEXT NOT NULL,
            last_seen_utc TEXT NOT NULL,
            supports_file_id_128 INTEGER NOT NULL CHECK(supports_file_id_128 IN (0,1)),
            supports_usn INTEGER NOT NULL CHECK(supports_usn IN (0,1)),
            supports_atomic_rename INTEGER NOT NULL CHECK(supports_atomic_rename IN (0,1)),
            status TEXT NOT NULL CHECK(status IN ('online','offline','identity_changed','unsupported','retired')),
            UNIQUE(installation_id, volume_serial_hex)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS pair_config_revision (
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            revision INTEGER NOT NULL CHECK(revision > 0),
            created_utc TEXT NOT NULL,
            created_by TEXT NOT NULL,
            deletion_policy TEXT NOT NULL CHECK(deletion_policy IN ('disabled','recoverable')),
            deletion_grace_seconds INTEGER NOT NULL CHECK(deletion_grace_seconds >= 0),
            required_absence_observations INTEGER NOT NULL CHECK(required_absence_observations >= 1),
            conflict_policy TEXT NOT NULL CHECK(conflict_policy IN ('manual_preserve_both')),
            metadata_policy TEXT NOT NULL CHECK(metadata_policy IN ('content_times_basic_attrs','content_only')),
            recursive_mode INTEGER NOT NULL CHECK(recursive_mode IN (0,1)),
            automatic_pruning INTEGER NOT NULL CHECK(automatic_pruning IN (0,1)),
            recovery_retention_days INTEGER NOT NULL CHECK(recovery_retention_days >= 1),
            PRIMARY KEY(pair_id, revision)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS scan_generation (
            scan_generation_id TEXT PRIMARY KEY,
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            root_binding_id TEXT NOT NULL REFERENCES root_binding(root_binding_id) ON DELETE RESTRICT,
            side TEXT NOT NULL CHECK(side IN ('local','dropbox')),
            generation INTEGER NOT NULL CHECK(generation > 0),
            trigger_kind TEXT NOT NULL CHECK(trigger_kind IN ('initial','monitor','periodic','recovery','manual','cursor_reset','overflow')),
            scope_json TEXT NOT NULL CHECK(json_valid(scope_json)),
            started_utc TEXT NOT NULL,
            completed_utc TEXT,
            status TEXT NOT NULL CHECK(status IN ('incomplete','complete','failed','superseded')),
            entry_count INTEGER NOT NULL DEFAULT 0 CHECK(entry_count >= 0),
            UNIQUE(root_binding_id, generation),
            CHECK((status = 'complete' AND completed_utc IS NOT NULL) OR status <> 'complete')
        ) STRICT;
        CREATE INDEX IF NOT EXISTS scan_pair_side_time
            ON scan_generation(pair_id, side, started_utc DESC);

        CREATE TABLE IF NOT EXISTS monitor_cursor (
            monitor_cursor_id TEXT PRIMARY KEY,
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            root_binding_id TEXT NOT NULL REFERENCES root_binding(root_binding_id) ON DELETE RESTRICT,
            kind TEXT NOT NULL CHECK(kind IN ('directory_changes','usn','dropbox_cursor','periodic')),
            generation INTEGER NOT NULL CHECK(generation > 0),
            cursor_value TEXT,
            source_identity_json TEXT NOT NULL CHECK(json_valid(source_identity_json)),
            status TEXT NOT NULL CHECK(status IN ('active','reset_required','unavailable','retired')),
            updated_utc TEXT NOT NULL,
            UNIQUE(pair_id, root_binding_id, kind, generation)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS monitor_event (
            monitor_event_id TEXT PRIMARY KEY,
            monitor_cursor_id TEXT REFERENCES monitor_cursor(monitor_cursor_id) ON DELETE RESTRICT,
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            source_event_key TEXT NOT NULL,
            normalized_path_key TEXT,
            object_identity_json TEXT CHECK(object_identity_json IS NULL OR json_valid(object_identity_json)),
            event_kind TEXT NOT NULL,
            event_json TEXT NOT NULL CHECK(json_valid(event_json)),
            received_utc TEXT NOT NULL,
            consumed_utc TEXT,
            UNIQUE(pair_id, source_event_key)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS monitor_event_unconsumed
            ON monitor_event(received_utc) WHERE consumed_utc IS NULL;

        CREATE TABLE IF NOT EXISTS dirty_item (
            dirty_item_id TEXT PRIMARY KEY,
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            object_id TEXT REFERENCES logical_object(object_id) ON DELETE RESTRICT,
            side TEXT CHECK(side IS NULL OR side IN ('local','dropbox','both')),
            normalized_path_key TEXT,
            reconciliation_level TEXT NOT NULL CHECK(reconciliation_level IN ('item','subtree','pair','full')),
            earliest_event_utc TEXT NOT NULL,
            latest_event_utc TEXT NOT NULL,
            event_count INTEGER NOT NULL CHECK(event_count > 0),
            planner_input_digest TEXT,
            status TEXT NOT NULL CHECK(status IN ('dirty','leased','planned','conflicted','blocked','closed')),
            lease_id TEXT REFERENCES lease(lease_id) ON DELETE RESTRICT,
            UNIQUE(pair_id, side, object_id, normalized_path_key, reconciliation_level)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS dirty_ready ON dirty_item(status, earliest_event_utc);

        CREATE TABLE IF NOT EXISTS reconciliation_decision (
            reconciliation_decision_id TEXT PRIMARY KEY,
            dirty_item_id TEXT REFERENCES dirty_item(dirty_item_id) ON DELETE RESTRICT,
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            object_id TEXT REFERENCES logical_object(object_id) ON DELETE RESTRICT,
            local_scan_generation_id TEXT REFERENCES scan_generation(scan_generation_id) ON DELETE RESTRICT,
            dropbox_scan_generation_id TEXT REFERENCES scan_generation(scan_generation_id) ON DELETE RESTRICT,
            synchronization_point_id TEXT REFERENCES synchronization_point(synchronization_point_id) ON DELETE RESTRICT,
            decision_kind TEXT NOT NULL CHECK(decision_kind IN ('unchanged','initial_observation','adopted','operation','conflict','tombstone','move','blocked')),
            decision_json TEXT NOT NULL CHECK(json_valid(decision_json)),
            decided_utc TEXT NOT NULL,
            UNIQUE(dirty_item_id, local_scan_generation_id, dropbox_scan_generation_id)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS conflict (
            conflict_id TEXT PRIMARY KEY,
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            object_id TEXT NOT NULL REFERENCES logical_object(object_id) ON DELETE RESTRICT,
            conflict_generation INTEGER NOT NULL CHECK(conflict_generation > 0),
            reason TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('open','resolution_requested','resolving','resolved','superseded','blocked')),
            created_utc TEXT NOT NULL,
            resolved_utc TEXT,
            UNIQUE(pair_id, object_id, conflict_generation)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS conflict_open ON conflict(pair_id, status, created_utc);

        CREATE TABLE IF NOT EXISTS conflict_version (
            conflict_id TEXT NOT NULL REFERENCES conflict(conflict_id) ON DELETE RESTRICT,
            side TEXT NOT NULL CHECK(side IN ('local','dropbox')),
            object_version_id TEXT NOT NULL REFERENCES object_version(object_version_id) ON DELETE RESTRICT,
            membership_id TEXT REFERENCES membership(membership_id) ON DELETE RESTRICT,
            PRIMARY KEY(conflict_id, side)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS operation_group (
            operation_group_id TEXT PRIMARY KEY,
            kind TEXT NOT NULL CHECK(kind IN ('compound','cross_pair_move','conflict_resolution','recovery','maintenance')),
            status TEXT NOT NULL CHECK(status IN ('planned','active','committed','completed','blocked','cancelled')),
            created_utc TEXT NOT NULL,
            terminal_utc TEXT,
            detail_json TEXT NOT NULL CHECK(json_valid(detail_json))
        ) STRICT;

        CREATE TABLE IF NOT EXISTS operation_step_transition (
            from_status TEXT NOT NULL,
            to_status TEXT NOT NULL,
            PRIMARY KEY(from_status, to_status)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS operation_step (
            operation_step_id TEXT PRIMARY KEY,
            operation_id TEXT NOT NULL REFERENCES operation(operation_id) ON DELETE RESTRICT,
            ordinal INTEGER NOT NULL CHECK(ordinal > 0),
            step_kind TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('planned','ready','leased','validating','allocation_authorized','staged','effect_issuing','effect_unknown','effect_observed','reconciling','verifying','committed','cleanup_pending','completed','blocked','cancelled','superseded')),
            preconditions_json TEXT NOT NULL CHECK(json_valid(preconditions_json)),
            created_utc TEXT NOT NULL,
            terminal_utc TEXT,
            UNIQUE(operation_id, ordinal)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS step_runnable ON operation_step(status, operation_id, ordinal);

        CREATE TABLE IF NOT EXISTS namespace_allocation (
            namespace_allocation_id TEXT PRIMARY KEY,
            operation_id TEXT NOT NULL REFERENCES operation(operation_id) ON DELETE RESTRICT,
            side TEXT NOT NULL CHECK(side IN ('local','dropbox')),
            parent_identity TEXT NOT NULL,
            deterministic_name TEXT NOT NULL,
            owner_nonce_hash TEXT NOT NULL CHECK(length(owner_nonce_hash) = 64),
            status TEXT NOT NULL CHECK(status IN ('authorized','creating','created','bound','verified','released','blocked')),
            bound_identity TEXT,
            transition_lease_id TEXT REFERENCES lease(lease_id) ON DELETE RESTRICT,
            transition_fencing_token INTEGER CHECK(transition_fencing_token IS NULL OR transition_fencing_token > 0),
            created_utc TEXT NOT NULL,
            CHECK((transition_lease_id IS NULL) = (transition_fencing_token IS NULL)),
            UNIQUE(side, parent_identity, deterministic_name)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS control_namespace (
            control_namespace_id TEXT PRIMARY KEY,
            namespace_allocation_id TEXT NOT NULL REFERENCES namespace_allocation(namespace_allocation_id) ON DELETE RESTRICT,
            side TEXT NOT NULL CHECK(side IN ('local','dropbox')),
            identity_json TEXT NOT NULL CHECK(json_valid(identity_json)),
            marker_identity_json TEXT NOT NULL CHECK(json_valid(marker_identity_json)),
            owner_marker_hash TEXT NOT NULL CHECK(length(owner_marker_hash) = 64),
            status TEXT NOT NULL CHECK(status IN ('active','unavailable','identity_changed','retiring','retired','blocked')),
            verified_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS pair_control_namespace (
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            side TEXT NOT NULL CHECK(side IN ('local','dropbox')),
            control_namespace_id TEXT NOT NULL REFERENCES control_namespace(control_namespace_id) ON DELETE RESTRICT,
            binding_generation INTEGER NOT NULL CHECK(binding_generation > 0),
            status TEXT NOT NULL CHECK(status IN ('active','superseded','blocked','retired')),
            PRIMARY KEY(pair_id, side, binding_generation)
        ) STRICT;
        CREATE UNIQUE INDEX IF NOT EXISTS one_active_pair_control_namespace_side
            ON pair_control_namespace(pair_id, side) WHERE status='active';

        CREATE TRIGGER IF NOT EXISTS namespace_allocation_transition_guard
        BEFORE UPDATE OF status ON namespace_allocation
        FOR EACH ROW WHEN OLD.status <> NEW.status
        BEGIN
            SELECT CASE WHEN NOT EXISTS (
                SELECT 1 FROM allocation_transition
                WHERE from_status=OLD.status AND to_status=NEW.status
            ) THEN RAISE(ABORT, 'invalid namespace allocation state transition') END;
        END;

        CREATE TRIGGER IF NOT EXISTS namespace_allocation_transition_requires_current_fence
        BEFORE UPDATE OF status ON namespace_allocation
        FOR EACH ROW WHEN OLD.status <> NEW.status AND NOT EXISTS (
            SELECT 1
            FROM lease l
            JOIN operation o ON o.operation_id=NEW.operation_id
            WHERE l.lease_id=NEW.transition_lease_id
              AND l.scope_kind='operation'
              AND l.scope_id=NEW.operation_id
              AND l.status='active'
              AND l.fencing_token=NEW.transition_fencing_token
              AND julianday(l.expires_utc)>julianday('now')
              AND o.current_lease_id=NEW.transition_lease_id
              AND o.current_fencing_token=NEW.transition_fencing_token
        )
        BEGIN
            SELECT RAISE(ABORT, 'namespace allocation transition does not hold the current fenced operation lease');
        END;

        CREATE TABLE IF NOT EXISTS recovery_artifact (
            recovery_artifact_id TEXT PRIMARY KEY,
            operation_id TEXT NOT NULL REFERENCES operation(operation_id) ON DELETE RESTRICT,
            object_id TEXT REFERENCES logical_object(object_id) ON DELETE RESTRICT,
            allocation_id TEXT REFERENCES resource_allocation(allocation_id) ON DELETE RESTRICT,
            side TEXT NOT NULL CHECK(side IN ('local','dropbox')),
            identity_json TEXT NOT NULL CHECK(json_valid(identity_json)),
            content_hash TEXT,
            size_bytes INTEGER CHECK(size_bytes IS NULL OR size_bytes >= 0),
            status TEXT NOT NULL CHECK(status IN ('retained','verified','release_authorized','released','blocked')),
            retained_utc TEXT NOT NULL,
            retain_until_utc TEXT,
            released_utc TEXT
        ) STRICT;
        CREATE INDEX IF NOT EXISTS recovery_retention ON recovery_artifact(status, retain_until_utc);

        CREATE TABLE IF NOT EXISTS tombstone (
            tombstone_id TEXT PRIMARY KEY,
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            object_id TEXT NOT NULL REFERENCES logical_object(object_id) ON DELETE RESTRICT,
            absent_side TEXT NOT NULL CHECK(absent_side IN ('local','dropbox','both')),
            prior_membership_id TEXT REFERENCES membership(membership_id) ON DELETE RESTRICT,
            prior_object_version_id TEXT REFERENCES object_version(object_version_id) ON DELETE RESTRICT,
            observed_absent_utc TEXT NOT NULL,
            complete_observation_count INTEGER NOT NULL CHECK(complete_observation_count > 0),
            status TEXT NOT NULL CHECK(status IN ('observed','grace','propagation_planned','retained','resolved','superseded','blocked')),
            UNIQUE(pair_id, object_id, absent_side, observed_absent_utc)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS tombstone_pending ON tombstone(status, observed_absent_utc);

        CREATE TABLE IF NOT EXISTS tombstone_observation (
            tombstone_id TEXT NOT NULL REFERENCES tombstone(tombstone_id) ON DELETE RESTRICT,
            local_scan_generation_id TEXT NOT NULL REFERENCES scan_generation(scan_generation_id) ON DELETE RESTRICT,
            dropbox_scan_generation_id TEXT NOT NULL REFERENCES scan_generation(scan_generation_id) ON DELETE RESTRICT,
            observed_utc TEXT NOT NULL,
            PRIMARY KEY(tombstone_id, local_scan_generation_id, dropbox_scan_generation_id)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS tombstone_observation_time
            ON tombstone_observation(tombstone_id, observed_utc);

        CREATE TABLE IF NOT EXISTS conflict_resolution (
            conflict_resolution_id TEXT PRIMARY KEY,
            conflict_id TEXT NOT NULL REFERENCES conflict(conflict_id) ON DELETE RESTRICT,
            requested_generation INTEGER NOT NULL CHECK(requested_generation > 0),
            resolution_kind TEXT NOT NULL CHECK(resolution_kind IN ('keep_local','keep_dropbox','preserve_both')),
            command_request_id TEXT,
            operation_group_id TEXT REFERENCES operation_group(operation_group_id) ON DELETE RESTRICT,
            status TEXT NOT NULL CHECK(status IN ('requested','validated','planned','executing','completed','stale','blocked','cancelled')),
            requested_utc TEXT NOT NULL,
            terminal_utc TEXT
        ) STRICT;

        CREATE TABLE IF NOT EXISTS command_request (
            command_request_id TEXT PRIMARY KEY,
            command_kind TEXT NOT NULL CHECK(command_kind IN ('run_pair','run_all','pause','resume','stop','resolve_conflict','cancel_unstarted','reconcile','backup','integrity_check')),
            pair_id TEXT REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            target_id TEXT,
            expected_generation INTEGER,
            payload_json TEXT NOT NULL CHECK(json_valid(payload_json)),
            requested_utc TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('requested','accepted','executing','completed','rejected','stale','blocked','cancelled')),
            terminal_utc TEXT,
            result_json TEXT CHECK(result_json IS NULL OR json_valid(result_json))
        ) STRICT;
        CREATE INDEX IF NOT EXISTS command_pending ON command_request(status, requested_utc)
            WHERE status IN ('requested','accepted','executing');

        CREATE TABLE IF NOT EXISTS scheduler_setting (
            singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
            global_effect_limit INTEGER NOT NULL CHECK(global_effect_limit BETWEEN 1 AND 32),
            per_pair_effect_limit INTEGER NOT NULL CHECK(per_pair_effect_limit BETWEEN 1 AND 8),
            recovery_first INTEGER NOT NULL CHECK(recovery_first = 1),
            updated_utc TEXT NOT NULL
        ) STRICT;
        INSERT OR IGNORE INTO scheduler_setting(singleton,global_effect_limit,per_pair_effect_limit,recovery_first,updated_utc)
            VALUES(1,2,1,1,'1970-01-01T00:00:00.0000000Z');

        CREATE TABLE IF NOT EXISTS projection_watermark (
            projection_name TEXT PRIMARY KEY,
            last_history_utc TEXT,
            last_history_event_id TEXT,
            rebuilt_utc TEXT NOT NULL,
            projection_digest TEXT NOT NULL CHECK(length(projection_digest) = 64)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS migration_import (
            migration_import_id TEXT PRIMARY KEY,
            source_kind TEXT NOT NULL CHECK(source_kind IN ('legacy_0_1_1_pairs','legacy_account_binding')),
            source_identity TEXT NOT NULL,
            source_sha256 TEXT NOT NULL CHECK(length(source_sha256) = 64),
            imported_json TEXT NOT NULL CHECK(json_valid(imported_json)),
            imported_utc TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('candidate','verified','accepted','rejected','rolled_back')),
            UNIQUE(source_kind, source_identity, source_sha256)
        ) STRICT;

        WITH seed(from_status,to_status) AS (VALUES
            ('planned','ready'),('ready','leased'),('leased','validating'),('leased','effect_issuing'),
            ('validating','allocation_authorized'),('validating','effect_issuing'),
            ('allocation_authorized','staged'),('allocation_authorized','effect_issuing'),
            ('staged','effect_issuing'),('effect_issuing','effect_unknown'),('effect_issuing','effect_observed'),
            ('effect_unknown','reconciling'),('effect_observed','reconciling'),('reconciling','verifying'),
            ('verifying','committed'),('committed','cleanup_pending'),('committed','completed'),
            ('cleanup_pending','completed'),('ready','cancelled'),('ready','superseded'))
        INSERT INTO operation_step_transition(from_status,to_status)
            SELECT from_status,to_status FROM seed
            WHERE NOT EXISTS(SELECT 1 FROM operation_step_transition LIMIT 1);

        CREATE TRIGGER IF NOT EXISTS operation_step_transition_no_insert
        BEFORE INSERT ON operation_step_transition
        BEGIN
            SELECT RAISE(ABORT, 'operation step transition allowlist is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_step_transition_no_update
        BEFORE UPDATE ON operation_step_transition
        BEGIN
            SELECT RAISE(ABORT, 'operation step transition allowlist is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_step_transition_no_delete
        BEFORE DELETE ON operation_step_transition
        BEGIN
            SELECT RAISE(ABORT, 'operation step transition allowlist is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_step_transition_guard
        BEFORE UPDATE OF status ON operation_step
        FOR EACH ROW WHEN OLD.status <> NEW.status AND NOT EXISTS(
            SELECT 1 FROM operation_step_transition
            WHERE from_status=OLD.status AND to_status=NEW.status)
        BEGIN
            SELECT RAISE(ABORT, 'invalid operation step transition');
        END;

        CREATE TRIGGER IF NOT EXISTS operation_complete_requires_terminal_steps
        BEFORE UPDATE OF status ON operation
        FOR EACH ROW WHEN NEW.status='completed' AND EXISTS(
            SELECT 1 FROM operation_step s WHERE s.operation_id=NEW.operation_id
              AND s.status NOT IN ('completed','cancelled','superseded'))
        BEGIN
            SELECT RAISE(ABORT, 'operation has nonterminal steps');
        END;

        CREATE TRIGGER IF NOT EXISTS no_prune_unresolved_recovery
        BEFORE UPDATE OF status ON recovery_artifact
        FOR EACH ROW WHEN NEW.status='released' AND EXISTS(
            SELECT 1 FROM operation o WHERE o.operation_id=OLD.operation_id
              AND o.status NOT IN ('completed','cancelled','superseded'))
        BEGIN
            SELECT RAISE(ABORT, 'unresolved operation retains its recovery artifact');
        END;
        """;
}
