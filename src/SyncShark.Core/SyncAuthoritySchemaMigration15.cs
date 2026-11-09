namespace Massdex.SyncShark;

// Settled-action lifetime stays in the existing installation, command and action
// records. Legacy identities remain null, never guessed from historical row IDs.
// This migration is integrated only with the consuming admission/closure paths.
internal static class SyncAuthoritySchemaMigration15
{
    public const int PreviousVersion = 14;
    public static readonly string Sql = """
        ALTER TABLE installation ADD COLUMN next_action_sequence INTEGER NOT NULL DEFAULT 1
            CHECK(next_action_sequence>0);
        ALTER TABLE operation ADD COLUMN registration_generation TEXT;
        ALTER TABLE operation ADD COLUMN registration_sequence INTEGER
            CHECK((registration_generation IS NULL AND registration_sequence IS NULL)
                OR (registration_generation IS NOT NULL AND registration_sequence>0));
        CREATE UNIQUE INDEX IF NOT EXISTS operation_registration_position
            ON operation(registration_generation,registration_sequence)
            WHERE registration_generation IS NOT NULL;
        ALTER TABLE operation ADD COLUMN registration_intent_sha256 TEXT
            CHECK(registration_intent_sha256 IS NULL OR length(registration_intent_sha256)=64);
        ALTER TABLE operation ADD COLUMN closure_utc TEXT;
        ALTER TABLE operation ADD COLUMN closure_json TEXT
            CHECK(closure_json IS NULL OR json_valid(closure_json));
        ALTER TABLE operation ADD COLUMN closure_sha256 TEXT
            CHECK(closure_sha256 IS NULL OR length(closure_sha256)=64);
        ALTER TABLE operation ADD COLUMN closure_encoded_bytes INTEGER
            CHECK((closure_utc IS NULL AND closure_json IS NULL AND closure_sha256 IS NULL AND closure_encoded_bytes IS NULL)
                OR (closure_utc IS NOT NULL AND closure_json IS NOT NULL AND closure_sha256 IS NOT NULL
                    AND closure_encoded_bytes>=0 AND status IN ('completed','cancelled','superseded','blocked')));
        CREATE INDEX IF NOT EXISTS operation_closed_detail
            ON operation(closure_utc,registration_sequence) WHERE closure_utc IS NOT NULL;
        ALTER TABLE command_request ADD COLUMN single_action_id TEXT;
        ALTER TABLE conflict_resolution ADD COLUMN preserve_action_id TEXT;
        ALTER TABLE conflict_resolution ADD COLUMN resolution_action_id TEXT;
        CREATE TRIGGER IF NOT EXISTS command_single_action_immutable
        BEFORE UPDATE OF single_action_id ON command_request
        WHEN OLD.single_action_id IS NOT NULL AND NEW.single_action_id IS NOT OLD.single_action_id
        BEGIN SELECT RAISE(ABORT,'A single-action command cannot allocate another action.'); END;
        CREATE TRIGGER IF NOT EXISTS conflict_action_bindings_immutable
        BEFORE UPDATE OF preserve_action_id,resolution_action_id ON conflict_resolution
        WHEN (OLD.preserve_action_id IS NOT NULL AND NEW.preserve_action_id IS NOT OLD.preserve_action_id)
          OR (OLD.resolution_action_id IS NOT NULL AND NEW.resolution_action_id IS NOT OLD.resolution_action_id)
        BEGIN SELECT RAISE(ABORT,'An accepted conflict step cannot allocate another action.'); END;
        ALTER TABLE command_request ADD COLUMN ordinary_issuance_closed_utc TEXT;
        CREATE INDEX IF NOT EXISTS command_ordinary_open_position
            ON command_request(admission_sequence)
            WHERE ordinary_issuance_closed_utc IS NULL;
        CREATE TRIGGER IF NOT EXISTS operation_registration_identity_immutable
        BEFORE UPDATE OF registration_generation,registration_sequence,registration_intent_sha256 ON operation
        WHEN NOT (NEW.registration_generation IS OLD.registration_generation
                  AND NEW.registration_sequence IS OLD.registration_sequence
                  AND NEW.registration_intent_sha256 IS OLD.registration_intent_sha256)
        BEGIN
            SELECT RAISE(ABORT,'An action registration position is immutable.');
        END;
        CREATE TRIGGER IF NOT EXISTS operation_closure_immutable
        BEFORE UPDATE OF closure_utc,closure_json,closure_sha256,closure_encoded_bytes ON operation
        WHEN OLD.closure_utc IS NOT NULL AND NOT
            (NEW.closure_utc IS OLD.closure_utc AND NEW.closure_json IS OLD.closure_json
             AND NEW.closure_sha256 IS OLD.closure_sha256 AND NEW.closure_encoded_bytes IS OLD.closure_encoded_bytes)
        BEGIN
            SELECT RAISE(ABORT,'An established action closure certificate is immutable.');
        END;
        CREATE TRIGGER IF NOT EXISTS command_ordinary_closure_immutable
        BEFORE UPDATE OF ordinary_issuance_closed_utc ON command_request
        WHEN OLD.ordinary_issuance_closed_utc IS NOT NULL
            AND NEW.ordinary_issuance_closed_utc IS NOT OLD.ordinary_issuance_closed_utc
        BEGIN
            SELECT RAISE(ABORT,'Ordinary command issuance cannot reopen after closure.');
        END;
        ALTER TABLE history_event ADD COLUMN producer_kind TEXT NOT NULL DEFAULT 'run'
            CHECK(producer_kind IN ('run','lifecycle'));
        DROP INDEX history_event_producer_ordinal;
        CREATE UNIQUE INDEX IF NOT EXISTS history_event_producer_ordinal
            ON history_event(producer_command_id,producer_kind,producer_ordinal)
            WHERE producer_command_id IS NOT NULL;
        CREATE TRIGGER IF NOT EXISTS history_lifecycle_producer_guard BEFORE INSERT ON history_event
        WHEN NEW.producer_kind='lifecycle'
        BEGIN
            SELECT CASE WHEN NEW.retention_class<>'ordinary' OR NEW.event_kind<>'presentation'
                OR NEW.operation_id IS NOT NULL OR NEW.producer_ordinal<>NEW.rowid
                OR NEW.producer_command_id IS NULL
                OR json_type(NEW.detail_json,'$.OperationSnapshot') IS NOT 'object'
                OR NOT EXISTS(SELECT 1 FROM command_request
                    WHERE command_request_id=NEW.producer_command_id AND admission_sequence IS NOT NULL)
                THEN RAISE(ABORT,'A lifecycle snapshot requires its exact retained command and sequence') END;
        END;
        ALTER TABLE operation ADD COLUMN completion_state_json TEXT
            CHECK(completion_state_json IS NULL OR json_valid(completion_state_json));
        CREATE TRIGGER IF NOT EXISTS operation_completion_state_immutable
        BEFORE UPDATE OF completion_state_json ON operation
        WHEN OLD.completion_state_json IS NOT NULL
          AND NEW.completion_state_json IS NOT OLD.completion_state_json
        BEGIN SELECT RAISE(ABORT,'The committed action outcome cannot be replaced'); END;
        CREATE INDEX IF NOT EXISTS operation_terminal_unclosed
            ON operation(coalesce(terminal_utc,created_utc),operation_id)
            WHERE closure_utc IS NULL AND status IN ('completed','cancelled','superseded','blocked');
        CREATE INDEX IF NOT EXISTS action_receipt_attempt ON effect_receipt(attempt_id);
        CREATE INDEX IF NOT EXISTS action_resource_owner ON resource_allocation(operation_id);
        CREATE INDEX IF NOT EXISTS action_namespace_owner ON namespace_allocation(operation_id);
        CREATE INDEX IF NOT EXISTS action_recovery_owner ON recovery_artifact(operation_id);
        CREATE INDEX IF NOT EXISTS action_upload_owner ON upload_session_checkpoint(operation_id);
        CREATE INDEX IF NOT EXISTS action_lease_owner ON lease(scope_kind,scope_id,status);
        ALTER TABLE operation ADD COLUMN closure_kind TEXT
            CHECK(closure_kind IS NULL OR closure_kind IN ('unissued','known_success','known_nonapplication'));
        CREATE INDEX IF NOT EXISTS action_closure_kind ON operation(closure_kind,closure_utc,registration_sequence);
        ALTER TABLE installation ADD COLUMN action_closed_count INTEGER NOT NULL DEFAULT 0 CHECK(action_closed_count>=0);
        ALTER TABLE installation ADD COLUMN action_closed_bytes INTEGER NOT NULL DEFAULT 0 CHECK(action_closed_bytes>=0);
        ALTER TABLE installation ADD COLUMN action_retired_count INTEGER NOT NULL DEFAULT 0 CHECK(action_retired_count>=0);
        ALTER TABLE installation ADD COLUMN action_closure_frontier_utc TEXT NOT NULL DEFAULT '';
        ALTER TABLE installation ADD COLUMN action_closure_frontier_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE installation ADD COLUMN action_retire_frontier_utc TEXT NOT NULL DEFAULT '';
        ALTER TABLE installation ADD COLUMN action_retire_frontier_id TEXT NOT NULL DEFAULT '';
        CREATE TRIGGER IF NOT EXISTS operation_closed_reactivation_guard
        BEFORE UPDATE OF status,execution_command_id ON operation
        WHEN OLD.closure_utc IS NOT NULL AND
          (NEW.status IS NOT OLD.status OR NEW.execution_command_id IS NOT OLD.execution_command_id)
        BEGIN SELECT RAISE(ABORT,'An exactly closed operation cannot be reactivated or adopted'); END;
        CREATE TRIGGER IF NOT EXISTS action_closure_kind_immutable BEFORE UPDATE OF closure_kind ON operation
        WHEN OLD.closure_kind IS NOT NULL AND NEW.closure_kind IS NOT OLD.closure_kind
        BEGIN SELECT RAISE(ABORT,'An action closure kind cannot change'); END;
        CREATE TRIGGER IF NOT EXISTS action_closed_account AFTER UPDATE OF closure_utc ON operation
        WHEN OLD.closure_utc IS NULL AND NEW.closure_utc IS NOT NULL
        BEGIN UPDATE installation SET action_closed_count=action_closed_count+1,
            action_closed_bytes=action_closed_bytes+NEW.closure_encoded_bytes; END;
        CREATE TRIGGER IF NOT EXISTS action_retired_account AFTER DELETE ON operation
        WHEN OLD.closure_utc IS NOT NULL
        BEGIN UPDATE installation SET action_closed_count=action_closed_count-1,
            action_closed_bytes=action_closed_bytes-OLD.closure_encoded_bytes,
            action_retired_count=action_retired_count+1; END;
        DROP TRIGGER operation_member_delete_only_while_planned;
        DROP TRIGGER operation_dependency_delete_only_while_planned;
        DROP TRIGGER attempt_no_delete;
        DROP TRIGGER receipt_no_delete;
        -- This staging table is renamed within the migration transaction. The
        -- final inventory still contains operation_dependency, never a second queue.
        CREATE TABLE operation_dependency_v15 (
            operation_id TEXT NOT NULL REFERENCES operation(operation_id) ON DELETE RESTRICT,
            depends_on_operation_id TEXT REFERENCES operation(operation_id) ON DELETE RESTRICT,
            dependency_kind TEXT NOT NULL CHECK(dependency_kind IN ('must_complete','must_commit','must_resolve')),
            retired_dependency_id TEXT,
            terminal_proof_json TEXT CHECK(terminal_proof_json IS NULL OR json_valid(terminal_proof_json)),
            terminal_proof_sha256 TEXT,
            CHECK((depends_on_operation_id IS NOT NULL AND retired_dependency_id IS NULL
                    AND terminal_proof_json IS NULL AND terminal_proof_sha256 IS NULL)
                OR (depends_on_operation_id IS NULL AND retired_dependency_id IS NOT NULL
                    AND terminal_proof_json IS NOT NULL AND length(terminal_proof_sha256)=64)),
            CHECK(operation_id<>coalesce(depends_on_operation_id,retired_dependency_id))
        ) STRICT;
        INSERT INTO operation_dependency_v15(operation_id,depends_on_operation_id,dependency_kind)
            SELECT operation_id,depends_on_operation_id,dependency_kind FROM operation_dependency;
        DROP TABLE operation_dependency;
        ALTER TABLE operation_dependency_v15 RENAME TO operation_dependency;
        CREATE UNIQUE INDEX IF NOT EXISTS action_dependency_identity
            ON operation_dependency(operation_id,coalesce(depends_on_operation_id,retired_dependency_id));
        CREATE INDEX IF NOT EXISTS action_dependency_prerequisite
            ON operation_dependency(depends_on_operation_id) WHERE depends_on_operation_id IS NOT NULL;
        CREATE TRIGGER IF NOT EXISTS operation_dependency_insert_only_while_planned
        BEFORE INSERT ON operation_dependency
        BEGIN
            SELECT CASE WHEN (SELECT status FROM operation WHERE operation_id=NEW.operation_id) IS NOT 'planned'
                OR NEW.depends_on_operation_id IS NULL OR NEW.retired_dependency_id IS NOT NULL
                OR NEW.terminal_proof_json IS NOT NULL OR NEW.terminal_proof_sha256 IS NOT NULL
                THEN RAISE(ABORT,'A new dependency requires a retained prerequisite and planned owner') END;
        END;
        CREATE TRIGGER IF NOT EXISTS operation_dependency_update_only_while_planned
        BEFORE UPDATE ON operation_dependency
        WHEN NOT(OLD.terminal_proof_json IS NULL
            AND (SELECT status FROM operation WHERE operation_id=OLD.operation_id)='planned'
            AND NEW.depends_on_operation_id IS NOT NULL AND NEW.retired_dependency_id IS NULL
            AND NEW.terminal_proof_json IS NULL AND NEW.terminal_proof_sha256 IS NULL)
        BEGIN
            SELECT CASE WHEN OLD.depends_on_operation_id IS NULL
                OR NEW.operation_id<>OLD.operation_id OR NEW.dependency_kind<>OLD.dependency_kind
                OR NEW.depends_on_operation_id IS NOT NULL
                OR NEW.retired_dependency_id IS NOT OLD.depends_on_operation_id
                OR NEW.terminal_proof_json IS NOT (SELECT closure_json FROM operation
                    WHERE operation_id=OLD.depends_on_operation_id)
                OR NEW.terminal_proof_sha256 IS NOT (SELECT closure_sha256 FROM operation
                    WHERE operation_id=OLD.depends_on_operation_id)
                OR syncshark_action_retirement_allowed('dependency_terminalization',
                    OLD.operation_id || char(31) || OLD.depends_on_operation_id,OLD.depends_on_operation_id)<>1
                THEN RAISE(ABORT,'A dependency can compact only its exact verified terminal prerequisite') END;
        END;
        CREATE VIEW IF NOT EXISTS operation_dependency_state AS
        SELECT d.operation_id,coalesce(d.depends_on_operation_id,d.retired_dependency_id) AS depends_on_operation_id,
            d.dependency_kind,coalesce(CASE WHEN d.depends_on_operation_id IS NOT NULL THEN p.status
                WHEN syncshark_terminal_prerequisite_valid(d.terminal_proof_json,d.terminal_proof_sha256,
                    d.retired_dependency_id)=1 THEN json_extract(d.terminal_proof_json,'$.status')
                END,'missing_or_invalid') AS prerequisite_status
        FROM operation_dependency d LEFT JOIN operation p ON p.operation_id=d.depends_on_operation_id;
        CREATE TRIGGER IF NOT EXISTS operation_settled_retirement_guard BEFORE DELETE ON operation
        BEGIN
            SELECT CASE WHEN syncshark_action_retirement_allowed('operation',OLD.operation_id,OLD.operation_id)<>1
                THEN RAISE(ABORT,'Action detail retirement requires exact verified closure authority') END;
        END;
        CREATE TRIGGER IF NOT EXISTS operation_member_settled_retirement_guard BEFORE DELETE ON operation_member
        BEGIN
            SELECT CASE WHEN syncshark_action_retirement_allowed('operation_member',OLD.operation_member_id,OLD.operation_id)<>1
                THEN RAISE(ABORT,'Action detail retirement requires exact verified closure authority') END;
        END;
        CREATE TRIGGER IF NOT EXISTS operation_dependency_settled_retirement_guard BEFORE DELETE ON operation_dependency
        BEGIN
            SELECT CASE WHEN syncshark_action_retirement_allowed('operation_dependency',OLD.operation_id || char(31) || coalesce(OLD.depends_on_operation_id,OLD.retired_dependency_id),OLD.operation_id)<>1
                THEN RAISE(ABORT,'Action detail retirement requires exact verified closure authority') END;
        END;
        CREATE TRIGGER IF NOT EXISTS operation_step_settled_retirement_guard BEFORE DELETE ON operation_step
        BEGIN
            SELECT CASE WHEN syncshark_action_retirement_allowed('operation_step',OLD.operation_step_id,OLD.operation_id)<>1
                THEN RAISE(ABORT,'Action detail retirement requires exact verified closure authority') END;
        END;
        CREATE TRIGGER IF NOT EXISTS resource_allocation_settled_retirement_guard BEFORE DELETE ON resource_allocation
        BEGIN
            SELECT CASE WHEN syncshark_action_retirement_allowed('resource_allocation',OLD.allocation_id,OLD.operation_id)<>1
                THEN RAISE(ABORT,'Action detail retirement requires exact verified closure authority') END;
        END;
        CREATE TRIGGER IF NOT EXISTS namespace_allocation_settled_retirement_guard BEFORE DELETE ON namespace_allocation
        BEGIN
            SELECT CASE WHEN syncshark_action_retirement_allowed('namespace_allocation',OLD.namespace_allocation_id,OLD.operation_id)<>1
                THEN RAISE(ABORT,'Action detail retirement requires exact verified closure authority') END;
        END;
        CREATE TRIGGER IF NOT EXISTS operation_attempt_settled_retirement_guard BEFORE DELETE ON operation_attempt
        BEGIN
            SELECT CASE WHEN syncshark_action_retirement_allowed('operation_attempt',OLD.attempt_id,OLD.operation_id)<>1
                THEN RAISE(ABORT,'Action detail retirement requires exact verified closure authority') END;
        END;
        CREATE TRIGGER IF NOT EXISTS effect_receipt_settled_retirement_guard BEFORE DELETE ON effect_receipt
        BEGIN
            SELECT CASE WHEN syncshark_action_retirement_allowed('effect_receipt',OLD.effect_receipt_id,(SELECT operation_id FROM operation_attempt WHERE attempt_id=OLD.attempt_id))<>1
                THEN RAISE(ABORT,'Action detail retirement requires exact verified closure authority') END;
        END;
        CREATE TRIGGER IF NOT EXISTS upload_session_checkpoint_settled_retirement_guard BEFORE DELETE ON upload_session_checkpoint
        BEGIN
            SELECT CASE WHEN syncshark_action_retirement_allowed('upload_session_checkpoint',OLD.allocation_id,OLD.operation_id)<>1
                THEN RAISE(ABORT,'Action detail retirement requires exact verified closure authority') END;
        END;
        CREATE TRIGGER IF NOT EXISTS lease_settled_retirement_guard BEFORE DELETE ON lease
        BEGIN
            SELECT CASE WHEN syncshark_action_retirement_allowed('lease',OLD.lease_id,OLD.scope_id)<>1
                THEN RAISE(ABORT,'Action detail retirement requires exact verified closure authority') END;
        END;
        CREATE TRIGGER IF NOT EXISTS lease_fence_settled_retirement_guard BEFORE DELETE ON lease_fence
        BEGIN
            SELECT CASE WHEN syncshark_action_retirement_allowed('lease_fence',OLD.scope_kind || char(31) || OLD.scope_id,OLD.scope_id)<>1
                THEN RAISE(ABORT,'Action detail retirement requires exact verified closure authority') END;
        END;
        """ + SyncActionJournalSchema.Sql + SyncConsentLifetimeSchema.Sql + SyncRunRetentionSchema.Sql + SyncContextLifetimeSchema.Sql + SyncObjectLifetimeSchema.Sql + SyncScanLifetimeSchema.Sql + SyncScanRetentionSchema.Sql + SyncCurrentBaselineSchema.Sql + SyncBothAbsentLifetimeSchema.Sql;
}
