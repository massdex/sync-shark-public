using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

// A fresh identity is allocated before its members/resources are named. The
// ticket is never serialized and no factory accepts a requested operation ID.
public sealed class SyncAuthorityActionReservation
{
    private int _claimed;
    private SyncAuthorityActionReservation(string databasePath, string installationId, string generation,
        SyncAuthorityCommandAdmission? admission, string idempotencyKey)
    {
        OperationId = "operation-" + Guid.NewGuid().ToString("N");
        DatabasePath = databasePath;
        InstallationId = installationId;
        Generation = generation;
        Admission = admission;
        IdempotencyKey = idempotencyKey;
    }
    public string OperationId { get; }
    internal string DatabasePath { get; }
    internal string InstallationId { get; }
    internal string Generation { get; }
    internal SyncAuthorityCommandAdmission? Admission { get; }
    internal string IdempotencyKey { get; }
    internal bool InsertionClaimed => Volatile.Read(ref _claimed) != 0;
    internal bool TryClaimInsertion() => Interlocked.Exchange(ref _claimed, 1) == 0;
    internal static SyncAuthorityActionReservation Mint(string databasePath, string installationId,
        string generation, SyncAuthorityCommandAdmission? admission, string idempotencyKey) =>
        new(databasePath, installationId, generation, admission, idempotencyKey);
}

// A retained ID is a lookup result, with no ticket and no insertion permission.
internal sealed record SyncAuthorityActionRegistration(string OperationId,
    SyncAuthorityActionReservation? Reservation);

public sealed partial class SyncAuthorityDatabase
{
    internal async Task<SyncAuthorityActionRegistration> ReserveActionAsync(string idempotencyKey,
        SyncAuthorityCommandAdmission admission, CancellationToken token = default)
    {
        RequireIdentifier(idempotencyKey, nameof(idempotencyKey));
        ArgumentNullException.ThrowIfNull(admission);
        // A drained planning window already owns a bounded writer. Submit this
        // current-authority read through that same queue rather than opening one
        // unpooled SQLite schema per file. No operation or sequence is allocated
        // here; the actual insertion still revalidates and spends the fresh ticket.
        if (_executionConnectionScope.Value is { Active: true, BatchesWrites: true })
        {
            SyncAuthorityActionRegistration? registration = null;
            await WriteAsync(async (connection, transaction) =>
                registration = await ReadReservationAsync(connection, transaction), token);
            return registration ?? throw new InvalidDataException("Action reservation was not read.");
        }
        await using var connection = await OpenReadOnlyConnectionAsync(token);
        await using var snapshot = connection.BeginTransaction(deferred: true);
        return await ReadReservationAsync(connection, snapshot);

        async Task<SyncAuthorityActionRegistration> ReadReservationAsync(
            SqliteConnection readConnection, SqliteTransaction transaction)
        {
            await RequireCurrentCommandAdmissionCoreAsync(readConnection, transaction, admission,
                [SyncAuthorityCommandPurpose.NormalEffect, SyncAuthorityCommandPurpose.ControlAllocation], token);
            var retained = await ScalarStringAsync(readConnection, transaction,
                "SELECT operation_id FROM operation WHERE idempotency_key=$key;", token, ("$key", idempotencyKey));
            if (retained is not null) return new(retained, null);
            var position = await ReadCommandAdmissionPositionCoreAsync(readConnection, transaction, token);
            var ticket = SyncAuthorityActionReservation.Mint(_databasePath, position.InstallationId,
                position.Generation, admission, idempotencyKey);
            return new(ticket.OperationId, ticket);
        }
    }

    // Called at the actual INSERT boundary only after current intent/plan/consent
    // validation and exact retained-row replay have been resolved. Rollback never
    // unspends the ticket; a missing row then requires an independently fresh plan.
    private async Task<(string Generation, long Sequence)> ClaimActionRegistrationCoreAsync(
        SqliteConnection connection, SqliteTransaction transaction, SyncAuthorityOperationPlan plan,
        SyncAuthorityCommandAdmission admission, SyncAuthorityActionReservation? reservation,
        CancellationToken token)
    {
        if (reservation is null || reservation.OperationId != plan.OperationId ||
            reservation.IdempotencyKey != plan.IdempotencyKey ||
            !string.Equals(reservation.DatabasePath, _databasePath, StringComparison.OrdinalIgnoreCase) ||
            (reservation.Admission is not null ? reservation.Admission != admission
                : plan.Kind != SyncAuthorityOperationKind.AllocateControl))
            throw new InvalidDataException("A missing action requires its exact fresh registration ticket; a bare retained ID grants no insertion.");
        var installation = await ReadCommandAdmissionPositionCoreAsync(connection, transaction, token);
        if (reservation.InstallationId != installation.InstallationId ||
            reservation.Generation != installation.Generation)
            throw new InvalidDataException("The fresh action ticket belongs to another installation or database generation.");
        var sequence = await ScalarLongAsync(connection, transaction,
            "SELECT next_action_sequence FROM installation WHERE installation_id=$id AND database_generation=$generation;",
            token, ("$id", installation.InstallationId), ("$generation", installation.Generation));
        if (sequence <= 0 || sequence == long.MaxValue)
            throw new InvalidOperationException("The action registration sequence is unavailable or exhausted; no effect was admitted.");
        if (!reservation.TryClaimInsertion())
            throw new InvalidOperationException("This action ticket already spent its one insertion attempt; a missing row cannot be revived.");
        var next = checked(sequence + 1);
        var changed = await ExecuteCountAsync(connection, transaction,
            "UPDATE installation SET next_action_sequence=$next WHERE installation_id=$id AND database_generation=$generation AND next_action_sequence=$current;",
            token, ("$next", next), ("$id", installation.InstallationId),
            ("$generation", installation.Generation), ("$current", sequence));
        if (changed != 1) throw new InvalidOperationException("The exact action sequence changed before registration.");
        return (installation.Generation, sequence);
    }

    private static string ActionIntentFingerprint(SyncAuthorityOperationPlan plan, string persistedPreconditions) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            plan.OperationId, plan.IdempotencyKey, plan.Kind, plan.PairId, plan.ObjectId,
            plan.ConfigurationRevision, plan.CapabilityProfileId, Preconditions = persistedPreconditions,
            plan.Priority, plan.OperationGroupId,
            Members = plan.Members.OrderBy(x => x.MemberId, StringComparer.Ordinal),
            Dependencies = plan.Dependencies.OrderBy(x => x.DependsOnOperationId, StringComparer.Ordinal),
            Allocations = plan.Allocations.OrderBy(x => x.AllocationId, StringComparer.Ordinal).Select(x =>
                new { x.AllocationId, x.ControlNamespaceId, x.Kind, x.DeterministicName,
                    x.IntendedParentIdentity, x.OwnerNonceHash })
        }))).ToLowerInvariant();

    private static async Task<bool> IsExactRetainedActionCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncAuthorityOperationPlan plan, string preconditions, CancellationToken token)
    {
        if (await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM operation WHERE operation_id=$id OR idempotency_key=$key;",
                token, ("$id", plan.OperationId), ("$key", plan.IdempotencyKey)) == 0) return false;
        var exact = await ScalarLongAsync(connection, transaction, """
            SELECT count(*) FROM operation WHERE operation_id=$id AND idempotency_key=$key
              AND pair_id IS $pair AND object_id IS $object AND kind=$kind AND priority=$priority
              AND configuration_revision=$revision AND capability_profile_id=$profile
              AND preconditions_json=$preconditions AND operation_group_id IS $group;
            """, token, ("$id", plan.OperationId), ("$key", plan.IdempotencyKey),
            ("$pair", plan.PairId), ("$object", plan.ObjectId), ("$kind", SyncAuthorityNames.OperationKind(plan.Kind)),
            ("$priority", plan.Priority), ("$revision", plan.ConfigurationRevision),
            ("$profile", plan.CapabilityProfileId), ("$preconditions", preconditions), ("$group", plan.OperationGroupId));
        if (exact != 1) throw new InvalidOperationException("The retained operation identity collides with different retained authority.");
        var digest = await ScalarStringAsync(connection, transaction,
            "SELECT registration_intent_sha256 FROM operation WHERE operation_id=$id;", token, ("$id", plan.OperationId));
        if (digest is not null)
        {
            if (digest != ActionIntentFingerprint(plan, preconditions))
                throw new InvalidDataException("The retained action's members, dependencies or allocations differ from this replay.");
            return true;
        }
        // Legacy adoption cannot rely on the old header-only equality. Compare
        // every retained immutable child; lifecycle/status fields are not intent.
        async Task RequireCount(string table, int count)
        {
            if (await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM " + table + " WHERE operation_id=$id;", token,
                    ("$id", plan.OperationId)) != count)
                throw new InvalidDataException("The legacy action's exact child set differs from this replay.");
        }
        await RequireCount("operation_member", plan.Members.Count);
        await RequireCount("operation_dependency", plan.Dependencies.Count);
        await RequireCount("resource_allocation", plan.Allocations.Count);
        foreach (var m in plan.Members)
            if (await ScalarLongAsync(connection, transaction, """
                SELECT count(*) FROM operation_member WHERE operation_member_id=$id AND operation_id=$operation
                  AND pair_id=$pair AND object_id=$object AND role=$role AND root_binding_id=$root
                  AND membership_id IS $membership AND object_version_id IS $version
                  AND expected_membership_generation IS $generation AND expected_path_key IS $path
                  AND expected_state_json=$state;
                """, token, ("$id", m.MemberId), ("$operation", plan.OperationId), ("$pair", m.PairId),
                ("$object", m.ObjectId), ("$role", m.Role), ("$root", m.RootBindingId),
                ("$membership", m.MembershipId), ("$version", m.ObjectVersionId),
                ("$generation", m.ExpectedMembershipGeneration), ("$path", m.ExpectedPathKey),
                ("$state", m.ExpectedStateJson)) != 1)
                throw new InvalidDataException("A legacy action member differs from this replay.");
        foreach (var d in plan.Dependencies)
            if (await ScalarLongAsync(connection, transaction, """
                SELECT count(*) FROM operation_dependency WHERE operation_id=$id
                  AND coalesce(depends_on_operation_id,retired_dependency_id)=$other AND dependency_kind=$kind;
                """, token, ("$id", plan.OperationId), ("$other", d.DependsOnOperationId), ("$kind", d.Kind)) != 1)
                throw new InvalidDataException("A legacy action dependency differs from this replay.");
        foreach (var a in plan.Allocations)
            if (await ScalarLongAsync(connection, transaction, """
                SELECT count(*) FROM resource_allocation WHERE allocation_id=$id AND operation_id=$operation
                  AND control_namespace_id=$namespace AND resource_kind=$kind AND deterministic_name=$name
                  AND intended_parent_identity=$parent AND owner_nonce_hash=$nonce;
                """, token, ("$id", a.AllocationId), ("$operation", plan.OperationId),
                ("$namespace", a.ControlNamespaceId), ("$kind", SyncAuthorityNames.AllocationKind(a.Kind)),
                ("$name", a.DeterministicName), ("$parent", a.IntendedParentIdentity), ("$nonce", a.OwnerNonceHash)) != 1)
                throw new InvalidDataException("A legacy action allocation differs from this replay.");
        return true;
    }

    internal async Task<string?> FindRetainedActionIdAsync(string idempotencyKey, CancellationToken token = default)
    {
        RequireIdentifier(idempotencyKey, nameof(idempotencyKey));
        await using var connection = await OpenReadOnlyConnectionAsync(token);
        return await ScalarStringAsync(connection, null,
            "SELECT operation_id FROM operation WHERE idempotency_key=$key;", token, ("$key", idempotencyKey));
    }

    // Setup needs its fresh operation ID before accepting the one exact command
    // whose payload contains that ID. This ticket grants no command authority.
    internal async Task<SyncAuthorityActionRegistration> PrepareControlActionAsync(string idempotencyKey,
        CancellationToken token = default)
    {
        RequireIdentifier(idempotencyKey, nameof(idempotencyKey));
        await using var connection = await OpenReadOnlyConnectionAsync(token);
        await using var snapshot = connection.BeginTransaction(deferred: true);
        var retained = await ScalarStringAsync(connection, snapshot,
            "SELECT operation_id FROM operation WHERE idempotency_key=$key;", token, ("$key", idempotencyKey));
        if (retained is not null) return new(retained, null);
        var position = await ReadCommandAdmissionPositionCoreAsync(connection, snapshot, token);
        var ticket = SyncAuthorityActionReservation.Mint(_databasePath, position.InstallationId,
            position.Generation, null, idempotencyKey);
        return new(ticket.OperationId, ticket);
    }


    private static async Task RequireFreshActionTargetCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncAuthorityOperationPlan plan, SyncAuthorityCommandAdmission admission,
        CancellationToken token)
    {
        // A current, object-scoped baseline/cut is checked by the shared planning
        // guard in this same transaction; finishing that object invalidates it.
        if (plan.PlanningAuthorities is { Count: > 0 }) return;
        using var document = JsonDocument.Parse(plan.PreconditionsJson);
        var data = document.RootElement;
        if (plan.Kind == SyncAuthorityOperationKind.AllocateControl)
        {
            if (plan.Members.Count != 1 || plan.Members[0].Role != "lock" ||
                await ScalarLongAsync(connection, transaction, """
                    SELECT count(*) FROM root_binding r JOIN sync_pair p ON p.pair_id=r.pair_id
                    WHERE r.root_binding_id=$root AND r.pair_id=$pair
                      AND p.state<>'retired' AND p.configuration_revision=$revision
                      AND (r.state='active' OR ($setup=1 AND r.state='blocked'
                        AND p.pair_json IS NULL AND p.selected=0
                        AND (SELECT count(*) FROM root_binding b WHERE b.pair_id=p.pair_id)=2
                        AND (SELECT count(*) FROM root_binding b WHERE b.pair_id=p.pair_id
                          AND b.state='blocked' AND b.generation=p.configuration_revision
                          AND b.side IN ('local','dropbox')
                          AND json_extract(b.identity_json,'$.identityKind')='cloud')=2));
                    """, token, ("$root", plan.Members[0].RootBindingId), ("$pair", plan.PairId),
                    ("$revision", plan.ConfigurationRevision),
                    ("$setup", admission.Purpose == SyncAuthorityCommandPurpose.ControlAllocation ? 1 : 0)) != 1)
                throw new InvalidDataException("A fresh control action needs its exact current pair/root/configuration.");
            // Cloud/cloud authoring deliberately keeps both exact candidate roots blocked
            // until control ownership is prepared and atomic publication succeeds. Only its
            // scoped setup admission may use that unpublished, unselected two-root state.
            // Its retained physical allocation pins this action independently.
            // The actual command scope is rechecked after member insertion.
            return;
        }
        if (data.TryGetProperty("conflictId", out var conflict) &&
            data.TryGetProperty("expectedGeneration", out var generation))
        {
            var field = plan.Kind == SyncAuthorityOperationKind.ResolveConflict
                ? "preserve_action_id" : "resolution_action_id";
            if (await ScalarLongAsync(connection, transaction, """
                    SELECT count(*) FROM conflict c JOIN conflict_resolution r ON r.conflict_id=c.conflict_id
                    WHERE c.conflict_id=$conflict AND c.pair_id=$pair AND c.conflict_generation=$generation
                      AND c.status IN ('open','resolution_requested','resolving')
                      AND r.requested_generation=c.conflict_generation AND r.operation_group_id=$group
                      AND r.status IN ('planned','executing');
                    """, token, ("$conflict", conflict.GetString()), ("$pair", plan.PairId),
                    ("$generation", generation.GetInt64()), ("$group", plan.OperationGroupId)) != 1)
                throw new InvalidDataException("The exact unresolved conflict step is no longer current.");
            if (await ExecuteCountAsync(connection, transaction,
                    "UPDATE conflict_resolution SET " + field + "=$operation WHERE operation_group_id=$group AND " +
                    field + " IS NULL;", token, ("$operation", plan.OperationId), ("$group", plan.OperationGroupId)) != 1)
                throw new InvalidDataException("This conflict step already has its one action; missing detail grants no recreation.");
            return;
        }
        if (data.TryGetProperty("action", out var action) && action.GetString() == "restore_recovery" &&
            data.TryGetProperty("restoreCommandId", out var command) && command.GetString() == admission.CommandRequestId &&
            data.TryGetProperty("path", out var path))
        {
            if (await ExecuteCountAsync(connection, transaction, """
                    UPDATE command_request SET single_action_id=$operation
                    WHERE command_request_id=$command AND single_action_id IS NULL
                      AND pair_id=$pair AND command_kind='reconcile' AND status IN ('accepted','executing')
                      AND json_extract(payload_json,'$.action')='restore_recovery'
                      AND json_extract(payload_json,'$.TargetRelativePath')=$path;
                    """, token, ("$operation", plan.OperationId), ("$command", admission.CommandRequestId),
                    ("$pair", plan.PairId), ("$path", path.GetString())) != 1)
                throw new InvalidDataException("The exact restore target is stale, mismatched or already bound to its one action.");
            return;
        }
        throw new InvalidDataException("A fresh action needs exact current planning authority or a retained single-action target.");
    }

}
