using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

// ADM-L01/L02/L03 foundation: the ONE explicit work-admission authority of the product.
//
// The authority database already binds every installation to one database_generation. This partial adds the
// admission POSITION a newly accepted command occupies inside that generation:
// installation.next_command_sequence is allocated exactly once per accepted command inside the
// command-creating transaction, and installation.ordinary_closed_command_floor is the ordinary closed
// frontier at or below which no new position may be allocated. The pair (database_generation,
// admission_sequence) is unique by index, so the exact retained accepted command row together with a
// current installation generation and a sequence above the floor is the positive permission. Absence, a
// terminal status, a stale generation, a position at or below the floor, or an insufficient purpose is no
// permission.
//
// Nothing here backfills, reclassifies or deletes a retained row, and nothing here activates closure,
// retirement or a rollback: legacy rows keep their NULL admission columns and default lease flags, the
// ordinary floor starts at zero, and no sequence is ever inferred from ROWID, a row count, a maximum of
// retained rows, wall-clock time or caller payload.
public sealed partial class SyncAuthorityDatabase
{
    /// <summary>The current admission position of the one retained installation identity.</summary>
    internal sealed record SyncAuthorityCommandAdmissionPosition(string InstallationId, string Generation,
        long NextSequence, long OrdinaryClosedCommandFloor);

    /// <summary>
    /// Reads the exact current admission position of the retained installation without consuming it. It is
    /// the one place that answers "what position would the next admitted command occupy?".
    /// </summary>
    internal static async Task<SyncAuthorityCommandAdmissionPosition> ReadCommandAdmissionPositionCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT installation_id,database_generation,next_command_sequence,ordinary_closed_command_floor FROM installation ORDER BY installation_id LIMIT 2;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("The authority database has no installation identity to admit a command against.");
        var position = new SyncAuthorityCommandAdmissionPosition(reader.GetString(0), reader.GetString(1),
            reader.GetInt64(2), reader.GetInt64(3));
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Authority has multiple installation identities.");
        return position;
    }

    /// <summary>
    /// Allocates the exact current admission sequence for ONE newly created command, inside the caller's
    /// creating transaction, and advances the installation counter with checked arithmetic. The optimistic
    /// guarded update makes two allocations of the same position impossible, and a sequence at or below the
    /// ordinary closed frontier, or an exhausted counter, refuses instead of wrapping. An uncommitted
    /// allocation that rolls back leaves the counter unchanged, so the same position may be reallocated by
    /// the next command; a committed allocation is never reused.
    /// </summary>
    private static async Task<(string Generation, long Sequence)> AllocateCommandAdmissionSequenceCoreAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var position = await ReadCommandAdmissionPositionCoreAsync(connection, transaction, cancellationToken);
        if (position.NextSequence <= position.OrdinaryClosedCommandFloor)
            throw new InvalidOperationException(
                "The ordinary command floor has closed every admissible position; no new command can be registered at this generation.");
        if (position.NextSequence == long.MaxValue)
            throw new InvalidOperationException(
                "The explicit work-admission command sequence is exhausted; no new command can be registered.");
        await ExecuteAsync(connection, transaction,
            "UPDATE installation SET next_command_sequence=next_command_sequence+1 WHERE installation_id=$id AND next_command_sequence=$current;",
            cancellationToken, ("$id", position.InstallationId), ("$current", position.NextSequence));
        if (await ScalarLongAsync(connection, transaction, "SELECT changes();", cancellationToken) != 1)
            throw new InvalidOperationException("The admission command sequence changed while it was being allocated.");
        return (position.Generation, position.NextSequence);
    }

    /// <summary>
    /// The one durable admission predicate for a retained command: exact retained row, accepted or
    /// executing, bound generation equal to the installation generation it was accepted under, a non-null
    /// purpose, and a sequence still above the ordinary closed frontier.
    /// </summary>
    private static async Task<bool> HasCurrentCommandAdmissionAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string commandRequestId, CancellationToken cancellationToken)
        => await ScalarLongAsync(connection, transaction, """
            SELECT count(*) FROM command_request c
            WHERE c.command_request_id=$command
              AND c.status IN ('accepted','executing')
              AND (c.admission_purpose='read_only' OR c.ordinary_issuance_closed_utc IS NULL)
              AND c.admission_generation IS NOT NULL AND c.admission_purpose IS NOT NULL
              AND c.admission_sequence IS NOT NULL
              AND EXISTS(SELECT 1 FROM installation i
                         WHERE i.database_generation=c.admission_generation
                           AND c.admission_sequence > i.ordinary_closed_command_floor);
            """, cancellationToken, ("$command", commandRequestId)) == 1;

    /// <summary>
    /// Resolves the exact retained admission of one command. A legacy or unadmitted row returns
    /// <see langword="null"/>, which is the retained meaning of "no admission": it is never promoted into
    /// new effect permission by reading it.
    /// </summary>
    internal static async Task<SyncAuthorityCommandAdmission?> LoadRetainedCommandAdmissionAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string commandRequestId, CancellationToken cancellationToken)
    {
        var generation = await ScalarStringAsync(connection, transaction,
            "SELECT admission_generation FROM command_request WHERE command_request_id=$id;", cancellationToken,
            ("$id", commandRequestId));
        if (generation is null) return null;
        var purpose = await ScalarStringAsync(connection, transaction,
            "SELECT admission_purpose FROM command_request WHERE command_request_id=$id;", cancellationToken,
            ("$id", commandRequestId));
        if (purpose is null || !SyncAuthorityCommandPurposeNames.IsWireValue(purpose)) return null;
        var sequence = await ScalarLongAsync(connection, transaction,
            "SELECT coalesce(admission_sequence,0) FROM command_request WHERE command_request_id=$id;", cancellationToken,
            ("$id", commandRequestId));
        if (sequence <= 0) return null;
        return new SyncAuthorityCommandAdmission(commandRequestId, generation, sequence,
            SyncAuthorityCommandPurposeNames.Parse(purpose));
    }

    /// <summary>
    /// The read-only instance view of the same retained admission, so a producer that holds only the accepted
    /// command id can obtain the typed admission the strict operation, lease and dispatch guards require.
    /// </summary>
    internal async Task<SyncAuthorityCommandAdmission?> LoadRetainedCommandAdmissionAsync(string commandRequestId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(commandRequestId, nameof(commandRequestId));
        await using var connection = new SqliteConnection(ConnectionString(_databasePath, SqliteOpenMode.ReadOnly, false));
        await connection.OpenAsync(cancellationToken);
        return await LoadRetainedCommandAdmissionAsync(connection, null, commandRequestId, cancellationToken);
    }

    /// <summary>
    /// Requires that a caller-held typed admission is BOTH the exact retained admission of that command and
    /// still current, and that its purpose is one of the allowed classes. A stale, copied, forged, terminal,
    /// generation-changed or below-floor value fails closed here, so no consumer can turn a held value into
    /// permission by comparison alone.
    /// </summary>
    private static async Task RequireCurrentCommandAdmissionCoreAsync(SqliteConnection connection,
        SqliteTransaction? transaction, SyncAuthorityCommandAdmission admission,
        SyncAuthorityCommandPurpose[] allowedPurposes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (!allowedPurposes.Contains(admission.Purpose))
            throw new InvalidOperationException(
                "The accepted command admission class does not authorize this work.");
        var retained = await LoadRetainedCommandAdmissionAsync(connection, transaction, admission.CommandRequestId,
            cancellationToken) ?? throw new InvalidOperationException(
                "The command admission is not retained; a command with no current admission is no permission.");
        if (!string.Equals(retained.DatabaseGeneration, admission.DatabaseGeneration, StringComparison.Ordinal) ||
            retained.AdmissionSequence != admission.AdmissionSequence || retained.Purpose != admission.Purpose)
            throw new InvalidOperationException("The retained command admission differs from the presented admission.");
        if (!await HasCurrentCommandAdmissionAsync(connection, transaction, admission.CommandRequestId, cancellationToken))
            throw new InvalidOperationException(
                "The accepted command is no longer current at this installation generation; it authorizes nothing.");
    }

    /// <summary>
    /// Verifies that an already-retained command row is exactly the command the caller asked for and returns
    /// its retained admission. This is the only resolution of an ambiguous or repeated insertion attempt: it
    /// reads the exact identity and inserts nothing, so a reservation is never reminted and a missing row is
    /// never created by a second use.
    /// </summary>
    private static async Task<SyncAuthorityCommandAdmission> RequireExactAdmittedCommandCoreAsync(
        SqliteConnection connection, SqliteTransaction transaction, string commandRequestId, string commandKind,
        string? pairId, string? targetId, long? expectedGeneration, string payloadJson,
        SyncAuthorityCommandPurpose purpose, CancellationToken cancellationToken)
    {
        var exact = await ScalarLongAsync(connection, transaction, """
            SELECT count(*) FROM command_request
            WHERE command_request_id=$id AND command_kind=$kind AND pair_id IS $pair AND target_id IS $target
              AND expected_generation IS $generation AND payload_json=$payload AND admission_purpose=$purpose;
            """, cancellationToken, ("$id", commandRequestId), ("$kind", commandKind), ("$pair", pairId),
            ("$target", targetId), ("$generation", expectedGeneration), ("$payload", payloadJson),
            ("$purpose", SyncAuthorityCommandPurposeNames.Wire(purpose)));
        if (exact != 1)
            throw new InvalidOperationException(
                "The reserved command identity is already retained with different kind, target, payload, generation or purpose.");
        return await LoadRetainedCommandAdmissionAsync(connection, transaction, commandRequestId, cancellationToken)
               ?? throw new InvalidOperationException(
                   "The reserved command identity is retained without a current admission.");
    }

    /// <summary>
    /// Creates ONE command request row carrying its explicit admission, inside the caller's transaction.
    /// The reserved identity's single-use insertion claim is consumed at the actual INSERT. An already
    /// retained exact row is resolved by reading it, never by inserting a second one; a claimed reservation
    /// whose row is missing refuses, so a rolled-back first attempt can never later create the row.
    /// </summary>
    private static async Task<SyncAuthorityCommandAdmission> CreateAdmittedCommandRequestCoreAsync(
        SqliteConnection connection, SqliteTransaction transaction, string commandRequestId,
        SyncAuthorityCommandReservation? reservation, string commandKind, string? pairId, string? targetId,
        long? expectedGeneration, string payloadJson, SyncAuthorityCommandPurpose purpose,
        CancellationToken cancellationToken)
    {
        if (reservation is not null &&
            !string.Equals(reservation.CommandId, commandRequestId, StringComparison.Ordinal))
            throw new InvalidOperationException("The reserved command identity does not match the requested identity.");
        var retained = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM command_request WHERE command_request_id=$id;", cancellationToken,
            ("$id", commandRequestId));
        if (retained > 1)
            throw new InvalidDataException("The command request identity is not unique.");
        if (retained == 1)
            return await RequireExactAdmittedCommandCoreAsync(connection, transaction, commandRequestId,
                commandKind, pairId, targetId, expectedGeneration, payloadJson, purpose, cancellationToken);
        if (reservation is null)
            throw new InvalidOperationException(
                "A bare command identity with no retained row is not insertion authority; a fresh command requires its own matching reservation.");
        if (!reservation.TryClaimInsertion())
            throw new InvalidOperationException(
                "This reserved command identity already consumed its single insertion claim and created no retained command; a genuinely new command requires a fresh pending identity.");
        var (generation, sequence) = await AllocateCommandAdmissionSequenceCoreAsync(connection, transaction,
            cancellationToken);
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO command_request(command_request_id,command_kind,pair_id,target_id,
                expected_generation,payload_json,requested_utc,status,admission_generation,admission_purpose,
                admission_sequence)
            VALUES($id,$kind,$pair,$target,$generation,$payload,$utc,'requested',$admission,$purpose,$sequence);
            """, cancellationToken, ("$id", commandRequestId), ("$kind", commandKind), ("$pair", pairId),
            ("$target", targetId), ("$generation", expectedGeneration), ("$payload", payloadJson),
            ("$utc", Utc(DateTimeOffset.UtcNow)), ("$admission", generation),
            ("$purpose", SyncAuthorityCommandPurposeNames.Wire(purpose)), ("$sequence", sequence));
        if (await ScalarLongAsync(connection, transaction, "SELECT changes();", cancellationToken) != 1)
            throw new InvalidOperationException("The admitted command request was not recorded exactly once.");
        return new SyncAuthorityCommandAdmission(commandRequestId, generation, sequence, purpose);
    }

    /// <summary>
    /// The one production entry point that records and accepts a NEW command with an explicit effect
    /// purpose, and returns its typed admission. Creation and acceptance commit as one atomic transaction,
    /// exactly as the cold-admission path already does, so no admitted command ever rests in 'requested'.
    /// A reservation supplied here keeps the caller's published pending identity.
    /// </summary>
    public async Task<SyncAuthorityCommandAdmission> CreateAcceptedCommandAdmissionAsync(
        SyncAuthorityCommandReservation? reservation, string commandKind, string? pairId, string? targetId,
        long? expectedGeneration, string payloadJson, SyncAuthorityCommandPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        if (pairId is not null) RequireIdentifier(pairId, nameof(pairId));
        if (expectedGeneration is <= 0) throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        if (commandKind is not ("run_pair" or "run_all" or "pause" or "resume" or "stop" or
            "resolve_conflict" or "cancel_unstarted" or "reconcile" or "backup" or "integrity_check"))
            throw new ArgumentOutOfRangeException(nameof(commandKind));
        RequireJson(payloadJson, nameof(payloadJson));
        reservation ??= SyncAuthorityCommandReservation.Mint();
        var commandRequestId = reservation.CommandId;
        RequireIdentifier(commandRequestId, nameof(commandRequestId));
        SyncAuthorityCommandAdmission? admission = null;
        await WriteAsync(async (connection, transaction) =>
        {
            admission = await CreateAdmittedCommandRequestCoreAsync(connection, transaction, commandRequestId,
                reservation, commandKind, pairId, targetId, expectedGeneration, payloadJson, purpose,
                cancellationToken);
            var retainedStatus = await ScalarStringAsync(connection, transaction,
                "SELECT status FROM command_request WHERE command_request_id=$id;", cancellationToken,
                ("$id", commandRequestId));
            if (retainedStatus == "requested")
                await TransitionCommandRequestCoreAsync(connection, transaction, commandRequestId, "accepted", null,
                    cancellationToken);
            else
                // Exact replay reads the existing active admission without resetting its lifecycle.
                // Terminal, revoked, stale-generation or closed-floor requests cannot be reactivated.
                await RequireCurrentCommandAdmissionCoreAsync(connection, transaction, admission, [purpose],
                    cancellationToken);
        }, cancellationToken);
        return admission!;
    }

    /// <summary>
    /// The one explicit retained-operation adoption transaction. A fresh accepted run adopts ONLY the exact
    /// operation identities it actually selected: the exact retained operation must exist, must not be
    /// terminal, must not hold a nonexpired active lease, and must not be owned by a different live execution
    /// command. The update changes the execution owner alone and never the immutable originating command, so
    /// an exact pre-existing operation replay can never silently transfer execution ownership.
    /// It deliberately does not adopt every runnable operation globally and does not require a sealed group
    /// before registration, because the existing reviewed flow activates the group afterwards.
    /// </summary>
    public async Task<IReadOnlyList<string>> AuthorizeRetainedOperationsAsync(
        SyncAuthorityCommandAdmission admission, IReadOnlyCollection<string> exactOperationIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(exactOperationIds);
        if (exactOperationIds.Count == 0)
            throw new ArgumentException("At least one exact operation identity is required.", nameof(exactOperationIds));
        var targets = exactOperationIds.Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length != exactOperationIds.Count)
            throw new ArgumentException("The exact operation identity list is not distinct.", nameof(exactOperationIds));
        foreach (var operationId in targets) RequireIdentifier(operationId, nameof(exactOperationIds));
        var adopted = new List<string>();
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireCurrentCommandAdmissionCoreAsync(connection, transaction, admission,
                [SyncAuthorityCommandPurpose.NormalEffect, SyncAuthorityCommandPurpose.ControlAllocation],
                cancellationToken);
            foreach (var operationId in targets)
            {
                var status = await ScalarStringAsync(connection, transaction,
                    "SELECT status FROM operation WHERE operation_id=$id;", cancellationToken, ("$id", operationId))
                    ?? throw new InvalidOperationException("The operation selected for adoption is not retained.");
                if (await ScalarLongAsync(connection, transaction,
                        "SELECT closure_utc IS NOT NULL FROM operation WHERE operation_id=$id;",
                        cancellationToken, ("$id", operationId)) == 1)
                    throw new InvalidOperationException("An exactly closed operation cannot be adopted.");
                if (status is "completed" or "cancelled" or "superseded")
                    throw new InvalidOperationException("A terminal operation cannot be adopted by a fresh run.");
                // ADM-L03: the SAME shared purpose/kind/scope predicate as registration, ordinary lease
                // acquisition and the request_sent boundary, applied per exact selected operation BEFORE any
                // ownership update. One entry outside the command's admitted scope rolls back the whole
                // adoption rather than transferring execution ownership of work this command never covered.
                var adoptionPurpose = await ScalarStringAsync(connection, transaction,
                    "SELECT admission_purpose FROM command_request WHERE command_request_id=$command;",
                    cancellationToken, ("$command", admission.CommandRequestId));
                if (adoptionPurpose is null ||
                    !await HasCurrentOperationCommandScopeAsync(connection, transaction, operationId,
                        adoptionPurpose, cancellationToken, admission.CommandRequestId))
                    throw new InvalidOperationException(
                        "The operation's kind and pair scope are not covered by this command's admission; no execution ownership is transferred.");
                var activeLease = await ScalarLongAsync(connection, transaction, """
                    SELECT count(*) FROM lease l
                    WHERE l.scope_kind='operation' AND l.scope_id=$id AND l.status='active'
                      AND julianday(l.expires_utc) > julianday('now');
                    """, cancellationToken, ("$id", operationId));
                if (activeLease != 0)
                    throw new InvalidOperationException(
                        "An operation holding a nonexpired active lease cannot be adopted by another command.");
                var foreign = await ScalarLongAsync(connection, transaction, """
                    SELECT count(*) FROM operation o
                    WHERE o.operation_id=$id AND o.execution_command_id IS NOT NULL
                      AND o.execution_command_id <> $command
                      AND EXISTS(SELECT 1 FROM command_request c WHERE c.command_request_id=o.execution_command_id
                                 AND c.status IN ('accepted','executing')
                                 AND (c.admission_purpose='read_only' OR c.ordinary_issuance_closed_utc IS NULL));
                    """, cancellationToken, ("$id", operationId), ("$command", admission.CommandRequestId));
                if (foreign != 0)
                    throw new InvalidOperationException(
                        "The operation is still owned by a different live execution command.");
                await ExecuteAsync(connection, transaction,
                    "UPDATE operation SET execution_command_id=$command WHERE operation_id=$id;",
                    cancellationToken, ("$command", admission.CommandRequestId), ("$id", operationId));
                if (await ScalarLongAsync(connection, transaction, "SELECT changes();", cancellationToken) != 1)
                    throw new InvalidOperationException("The selected operation was not adopted exactly once.");
                adopted.Add(operationId);
            }
        }, cancellationToken);
        return adopted;
    }

    /// <summary>
    /// The ONE shared current command/operation scope predicate. Operation registration, retained adoption,
    /// ordinary lease acquisition and the actual request_sent boundary all ask this same question, so no
    /// consumer can accept a command whose purpose, kind or acted-on pair scope does not cover the operation
    /// it would authorize. A read_only admission authorizes no operation at all; a normal_effect admission
    /// covers only the existing effect-bearing kinds; the control-allocation admission covers only its own
    /// allocate_control setup operation. A single-pair command must name the operation's exact pair, a batch
    /// command must contain the acted-on pair in its retained immutable pairIds payload, a cross-pair move
    /// must name both its retained source and destination pair, and a null or malformed scope is no
    /// permission.
    /// </summary>
    private static async Task<bool> HasCurrentOperationCommandScopeAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string operationId, string purposeWire, CancellationToken cancellationToken,
        string? presentedCommandId = null)
        => await ScalarLongAsync(connection, transaction, """
            SELECT count(*) FROM operation o
            JOIN command_request c ON c.command_request_id=coalesce($presented_command,o.execution_command_id)
            JOIN installation i ON i.database_generation=c.admission_generation
            WHERE o.operation_id=$operation
              AND c.status IN ('accepted','executing')
              AND (c.admission_purpose='read_only' OR c.ordinary_issuance_closed_utc IS NULL)
              AND c.admission_purpose=$purpose
              AND c.admission_sequence IS NOT NULL
              AND c.admission_sequence > i.ordinary_closed_command_floor
              AND json_valid(c.payload_json)
              AND o.pair_id IS NOT NULL
              AND (($purpose='normal_effect' AND c.command_kind IN ('run_pair','run_all','resolve_conflict','reconcile'))
                   OR ($purpose='control_allocation' AND c.command_kind='reconcile' AND o.kind='allocate_control'
                     AND c.pair_id=o.pair_id
                     AND json_extract(c.payload_json,'$.installationId')=i.installation_id
                     AND json_extract(c.payload_json,'$.pairId')=o.pair_id
                     AND json_extract(c.payload_json,'$.operationId')=o.operation_id
                     AND json_extract(c.payload_json,'$.configurationRevision')=o.configuration_revision
                     AND json_extract(c.payload_json,'$.allowedOperationKind')='AllocateControl'
                     AND json_extract(c.payload_json,'$.nativeIdentity')=json_extract(o.preconditions_json,'$.ParentIdentityText')
                     AND json_extract(c.payload_json,'$.localRootBindingId')=c.target_id
                     AND (SELECT count(*) FROM operation_member m WHERE m.operation_id=o.operation_id)=1
                     AND EXISTS(SELECT 1 FROM operation_member m JOIN root_binding r ON r.root_binding_id=m.root_binding_id
                       WHERE m.operation_id=o.operation_id AND m.role='lock' AND m.root_binding_id=c.target_id
                         AND r.pair_id=o.pair_id AND r.side=json_extract(c.payload_json,'$.side'))))
              AND CASE
                    WHEN o.kind='cross_pair_move' THEN json_valid(o.preconditions_json)
                      AND json_extract(o.preconditions_json,'$.sourcePairId') IS NOT NULL
                      AND json_extract(o.preconditions_json,'$.destinationPairId') IS NOT NULL
                      AND json_extract(o.preconditions_json,'$.sourcePairId')
                          IN (SELECT value FROM json_each(c.payload_json,'$.pairIds'))
                      AND json_extract(o.preconditions_json,'$.destinationPairId')
                          IN (SELECT value FROM json_each(c.payload_json,'$.pairIds'))
                    WHEN c.command_kind='run_all' OR (c.command_kind='reconcile' AND c.pair_id IS NULL)
                      THEN json_type(c.payload_json,'$.pairIds')='array'
                      AND EXISTS(SELECT 1 FROM json_each(c.payload_json,'$.pairIds') j WHERE j.value=o.pair_id)
                    ELSE c.pair_id=o.pair_id
                  END;
            """, cancellationToken, ("$operation", operationId), ("$purpose", purposeWire),
            ("$presented_command", presentedCommandId)) == 1;

    internal async Task<SyncAuthorityCommandAdmission?> LoadCurrentOperationCommandAdmissionAsync(string operationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString(_databasePath, SqliteOpenMode.ReadOnly, false));
        await connection.OpenAsync(cancellationToken);
        var commandId = await ScalarStringAsync(connection, null,
            "SELECT execution_command_id FROM operation WHERE operation_id=$id;", cancellationToken, ("$id", operationId));
        if (commandId is null || !await HasCurrentCommandAdmissionAsync(connection, null, commandId, cancellationToken))
            return null;
        return await LoadRetainedCommandAdmissionAsync(connection, null, commandId, cancellationToken);
    }

    /// <summary>
    /// The one dispatch-authority question asked at an actual request_sent boundary: the lease must carry
    /// dispatch authority, must be bound to the operation's exact current execution command, and that
    /// command must still be an accepted or executing current-generation admission that shares the SAME
    /// purpose/kind/scope predicate as registration, adoption and ordinary lease acquisition. A stopped,
    /// terminated, stale-generation, below-floor, read-only, foreign-scope or foreign command refuses
    /// issuance, and a reconciliation-only lease never has dispatch authority.
    /// </summary>
    internal static async Task<bool> IsCurrentDispatchAuthorityAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string operationId, SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        var purpose = await ScalarStringAsync(connection, transaction, """
            SELECT c.admission_purpose FROM lease l
            JOIN operation o ON o.operation_id=l.scope_id
            JOIN command_request c ON c.command_request_id=o.execution_command_id
            JOIN installation i ON i.database_generation=c.admission_generation
            WHERE l.lease_id=$lease AND l.scope_kind='operation' AND l.scope_id=$operation
              AND l.status='active' AND l.fencing_token=$token AND l.dispatch_allowed=1
              AND l.command_request_id=o.execution_command_id
              AND o.current_lease_id=l.lease_id AND o.current_fencing_token=l.fencing_token
              AND c.status IN ('accepted','executing')
              AND (c.admission_purpose='read_only' OR c.ordinary_issuance_closed_utc IS NULL)
              AND c.admission_sequence IS NOT NULL AND c.admission_purpose IS NOT NULL
              AND c.admission_sequence > i.ordinary_closed_command_floor;
            """, cancellationToken, ("$lease", lease.LeaseId), ("$operation", operationId),
            ("$token", lease.FencingToken));
        if (purpose is null || !SyncAuthorityCommandPurposeNames.IsWireValue(purpose)) return false;
        return await HasCurrentOperationCommandScopeAsync(connection, transaction, operationId, purpose,
            cancellationToken);
    }
}

