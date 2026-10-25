namespace Massdex.SyncShark;

// A legacy tombstone has no closure basis. Only a fresh, exactly bound complete
// absence may establish the immutable age/policy/root basis for later collection.
internal static class SyncBothAbsentLifetimeSchema
{
    internal const string Sql = """
        ALTER TABLE tombstone ADD COLUMN both_absent_basis_json TEXT
            CHECK(both_absent_basis_json IS NULL OR json_valid(both_absent_basis_json));
        ALTER TABLE tombstone ADD COLUMN both_absent_closed_utc TEXT;
        CREATE INDEX IF NOT EXISTS tombstone_both_absent_age
            ON tombstone(both_absent_closed_utc,tombstone_id)
            WHERE both_absent_closed_utc IS NOT NULL;
        CREATE TRIGGER IF NOT EXISTS tombstone_both_absent_insert_guard
        BEFORE INSERT ON tombstone
        WHEN NEW.both_absent_basis_json IS NOT NULL OR NEW.both_absent_closed_utc IS NOT NULL
        BEGIN SELECT RAISE(ABORT,'Absence closure requires verified current observations'); END;
        CREATE TRIGGER IF NOT EXISTS tombstone_both_absent_identity_guard
        BEFORE UPDATE OF tombstone_id,pair_id,object_id,absent_side,prior_membership_id,prior_object_version_id ON tombstone
        WHEN OLD.both_absent_basis_json IS NOT NULL AND (
          NEW.tombstone_id IS NOT OLD.tombstone_id OR NEW.pair_id IS NOT OLD.pair_id
          OR NEW.object_id IS NOT OLD.object_id OR NEW.absent_side IS NOT OLD.absent_side
          OR NEW.prior_membership_id IS NOT OLD.prior_membership_id
          OR NEW.prior_object_version_id IS NOT OLD.prior_object_version_id)
        BEGIN SELECT RAISE(ABORT,'A proved absence cannot change identity'); END;
        CREATE TRIGGER IF NOT EXISTS tombstone_both_absent_update_guard
        BEFORE UPDATE OF both_absent_basis_json,both_absent_closed_utc ON tombstone
        WHEN syncshark_object_retirement_allowed('both_absent_basis',OLD.tombstone_id)<>1
          OR OLD.both_absent_basis_json IS NOT NULL OR OLD.both_absent_closed_utc IS NOT NULL
          OR NEW.both_absent_basis_json IS NULL OR NEW.both_absent_closed_utc IS NULL
          OR OLD.absent_side<>'both'
        BEGIN SELECT RAISE(ABORT,'Absence closure is immutable and requires exact proof'); END;

        ALTER TABLE installation ADD COLUMN both_absent_frontier_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE installation ADD COLUMN both_absent_last_hold TEXT;
        ALTER TABLE installation ADD COLUMN both_absent_expired_rows INTEGER NOT NULL DEFAULT 0 CHECK(both_absent_expired_rows>=0);
        ALTER TABLE installation ADD COLUMN both_absent_expired_objects INTEGER NOT NULL DEFAULT 0 CHECK(both_absent_expired_objects>=0);
        CREATE TRIGGER IF NOT EXISTS both_absent_empty_copy_delete_guard BEFORE DELETE ON current_copy_completion
        WHEN syncshark_object_retirement_allowed('current_copy_completion',OLD.object_id)<>1
          OR OLD.coverage_verified<>1 OR OLD.copy_operation_id IS NOT NULL OR OLD.attempt_id IS NOT NULL
          OR OLD.receipt_id IS NOT NULL OR OLD.destination_identity_json IS NOT NULL
          OR OLD.attempt_status IS NOT NULL OR OLD.receipt_evidence_json IS NOT NULL
        BEGIN SELECT RAISE(ABORT,'Only an exact proved obsolete empty copy projection can retire'); END;
        CREATE TRIGGER IF NOT EXISTS both_absent_decision_delete_guard BEFORE DELETE ON reconciliation_decision
        WHEN syncshark_object_retirement_allowed('reconciliation_decision',OLD.reconciliation_decision_id)<>1
        BEGIN SELECT RAISE(ABORT,'Decision retirement requires exact verified closure'); END;
        CREATE TRIGGER IF NOT EXISTS both_absent_point_delete_guard BEFORE DELETE ON synchronization_point
        WHEN syncshark_object_retirement_allowed('synchronization_point',OLD.synchronization_point_id)<>1
        BEGIN SELECT RAISE(ABORT,'Accepted point retirement requires exact verified closure'); END;
        CREATE TRIGGER IF NOT EXISTS both_absent_tombstone_delete_guard BEFORE DELETE ON tombstone
        WHEN syncshark_object_retirement_allowed('tombstone',OLD.tombstone_id)<>1
        BEGIN SELECT RAISE(ABORT,'Tombstone retirement requires exact verified closure'); END;
        CREATE TRIGGER IF NOT EXISTS both_absent_object_delete_guard BEFORE DELETE ON logical_object
        WHEN syncshark_object_retirement_allowed('logical_object',OLD.object_id)<>1
        BEGIN SELECT RAISE(ABORT,'Logical object retirement requires exact verified closure'); END;
        """;
}
