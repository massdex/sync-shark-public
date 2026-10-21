namespace Massdex.SyncShark;

internal static class SyncAuthoritySchemaMigration11
{
    public const int PreviousVersion = 10;

    // Additive and narrow: five nullable or defaulted columns on the three EXISTING admission tables
    // (command_request, installation, operation, lease), three new read/unique indexes and two
    // immutability triggers. No retained table, column, row, index, trigger, view or constraint is
    // created, dropped, rewritten or relaxed, so every legacy command request, installation identity,
    // operation, attempt, receipt, allocation and lease keeps its identity and its exact bytes. Adding a
    // column creates no new schema object, so the exact current object inventory is the version 10
    // inventory plus command_request_admission_sequence, operation_origin_command,
    // operation_execution_command, operation_origin_command_immutable and
    // lease_admission_authority_immutable.
    //
    // This migration creates the admission STRUCTURE only. It backfills nothing, reclassifies nothing,
    // and activates no retirement: every legacy row keeps NULL admission_generation,
    // admission_purpose, admission_sequence, origin_command_id, execution_command_id and
    // command_request_id with dispatch_allowed at its default 0, so a legacy row is never silently
    // promoted into new effect permission. installation.next_command_sequence starts at 1 and
    // ordinary_closed_command_floor at 0 for the one retained installation row; new admissions
    // allocate and increment that counter inside their own creating transaction.
    //
    // command_request.admission_generation is the installation.database_generation that was current at
    // acceptance, and admission_sequence is the exact value of installation.next_command_sequence at
    // that same instant, allocated once and never reused: the UNIQUE(generation, sequence) index makes
    // two accepted commands share one admission position impossible, while a NULL generation or
    // sequence is the retained meaning of every pre-version-11 row and never collides. admission_purpose
    // is the narrow effect class the accepted command may carry: normal_effect for an already-authorized
    // file-effect command, read_only for a scan or control command that authorizes no file effect, and
    // control_allocation for the one internal pair-binding setup allocation.
    //
    // operation.origin_command_id is the immutable command that first registered the operation and
    // operation.execution_command_id is the command that currently owns its execution; both are
    // nullable under ON DELETE RESTRICT so a retained operation keeps its producing command alive, and
    // the BEFORE UPDATE OF origin_command_id trigger makes the originating command write-once at INSERT
    // so an exact legacy replay can never transfer or rewrite it. execution_command_id stays
    // deliberately mutable: only the explicit retained-operation adoption path may move it, and only
    // after its own guard set. lease.command_request_id records the command the lease was bound to and
    // lease.dispatch_allowed is the checked 0/1 issuance flag; the lease trigger keeps both unchanged
    // after acquisition, so a renewal or a receipt can never turn an unpermitted lease into an issuing
    // one or move execution ownership.
    public const string Sql = """
        ALTER TABLE command_request ADD COLUMN admission_generation TEXT;

        ALTER TABLE command_request ADD COLUMN admission_purpose TEXT
            CHECK(admission_purpose IS NULL OR admission_purpose IN ('normal_effect', 'read_only', 'control_allocation'));

        ALTER TABLE command_request ADD COLUMN admission_sequence INTEGER
            CHECK(admission_sequence IS NULL OR admission_sequence > 0);

        CREATE UNIQUE INDEX IF NOT EXISTS command_request_admission_sequence
            ON command_request(admission_generation, admission_sequence);

        ALTER TABLE installation ADD COLUMN next_command_sequence INTEGER NOT NULL DEFAULT 1
            CHECK(next_command_sequence > 0);

        ALTER TABLE installation ADD COLUMN ordinary_closed_command_floor INTEGER NOT NULL DEFAULT 0
            CHECK(ordinary_closed_command_floor >= 0);

        ALTER TABLE operation ADD COLUMN origin_command_id TEXT
            REFERENCES command_request(command_request_id) ON DELETE RESTRICT;

        ALTER TABLE operation ADD COLUMN execution_command_id TEXT
            REFERENCES command_request(command_request_id) ON DELETE RESTRICT;

        CREATE INDEX IF NOT EXISTS operation_origin_command
            ON operation(origin_command_id) WHERE origin_command_id IS NOT NULL;

        CREATE INDEX IF NOT EXISTS operation_execution_command
            ON operation(execution_command_id) WHERE execution_command_id IS NOT NULL;

        ALTER TABLE lease ADD COLUMN command_request_id TEXT
            REFERENCES command_request(command_request_id) ON DELETE RESTRICT;

        ALTER TABLE lease ADD COLUMN dispatch_allowed INTEGER NOT NULL DEFAULT 0
            CHECK(dispatch_allowed IN (0, 1));

        CREATE TRIGGER IF NOT EXISTS operation_origin_command_immutable
        BEFORE UPDATE OF origin_command_id ON operation
        BEGIN
            SELECT RAISE(ABORT, 'the originating command of an operation is write-once');
        END;

        CREATE TRIGGER IF NOT EXISTS lease_admission_authority_immutable
        BEFORE UPDATE OF command_request_id, dispatch_allowed ON lease
        BEGIN
            SELECT RAISE(ABORT, 'lease dispatch authority is immutable after acquisition');
        END;
        """;
}
