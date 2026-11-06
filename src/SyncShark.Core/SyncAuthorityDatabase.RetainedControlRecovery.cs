using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

/// <summary>
/// RC639 retained original-control recovery (source only).
///
/// A previously owned private control namespace survives a state reset physically, but its current
/// authority database refuses to adopt it: the deterministic allocation collides with the existing owned
/// object and is recorded as a non-creating KnownConflict. Recovery represents that AUTHENTICATED identity
/// reuse without adopting by marker, without rewriting the immutable collided history, without restoring old
/// pair settings and without a new table, column, schema family, operation namespace or public bypass.
///
/// Authority comes only from a typed proof derived by reading an independently pinned original database
/// through the existing trusted file guard (excluding any unbound write-ahead log at the held boundary), and
/// from an authentic held-native reopen of the exact retained control namespace whose single-link marker read
/// guard is held across the decisive metadata transaction. The proof is retained under the approved
/// retained_control_recovered event identity in the EXISTING history_event detail_json and resolved by one
/// central transaction-bound scoped resolver. The collided rows stay byte-identical and the blocked
/// allocation is only an inert relational anchor, never recovered authority. The shared KnownNoEffect
/// refusal predicate is never broadened.
/// </summary>
public sealed partial class SyncAuthorityDatabase
{
    /// <summary>The approved existing history_event kind that carries the retained recovery observation.</summary>
    internal const string RetainedControlRecoveryHistoryKind = "retained_control_recovered";

    /// <summary>
    /// The typed, authority-bound retained proof. There is no public caller-JSON or allow-adopt constructor:
    /// it is produced only by the pinned trusted derivation, and the current collision anchor is separated
    /// from the original allocation provenance.
    /// </summary>
    internal sealed record RetainedControlRecoveryProof(
        string PairId,
        string Side,
        string RootBindingId,
        long RootGeneration,
        string RootIdentityJson,
        string InstallationId,
        string ControlNamespaceId,
        string OriginAllocationId,
        string AnchorAllocationId,
        string OriginOperationId,
        string OriginAttemptId,
        string OriginReceiptId,
        string OriginLeaseId,
        string OriginLockRootBindingId,
        long PairControlBindingGeneration,
        string CollisionOperationId,
        string CollisionDigest,
        string ControlVolumeSerialHex,
        string ControlFileIdHex,
        string MarkerFileId,
        string MarkerContentHash,
        string IdentityJson,
        string MarkerIdentityJson);

    /// <summary>
    /// The bounded, pinned original-database read request for one retained-control recovery. The current
    /// installation is never caller-supplied; it is read from the database. Public so the ordinary UI and CLI
    /// authoring surfaces can name the pinned source without any internal-only escape hatch.
    /// </summary>
    public sealed record RetainedControlSource(
        string SourceDatabasePath,
        string ExpectedSourceSha256,
        string PairId,
        string Side,
        string RootBindingId);

    internal sealed record RetainedControlRecoveryOutcome(
        string PairId,
        string Side,
        string RootBindingId,
        long RootGeneration,
        string ControlNamespaceId,
        string CollisionDigest,
        string HistoryEventId,
        bool Replayed);

    // ---------------------------------------------------------------------------------------------------
    // RC639-1.AUTH-3: the exact current collision tuple, the inert anchor and the compact digest.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// RC639-1.AUTH-3: require the pair's current state to be EXACTLY the immutable non-creating collision
    /// plus nothing else unresolved. It returns the collision operation id, the CURRENT anchor allocation id
    /// and a compact digest over the current collided records. Every attempt must carry its own
    /// known_conflict non-creating evidence; no issued, pending, unknown, effectful, planning-referenced or
    /// otherwise unresolved pair work and no active lease on the pair, operation, allocation, members or
    /// roots may exist. Nothing here mutates a row.
    /// </summary>
    private static async Task<(string OperationId, string AnchorAllocationId, string Digest)>
        RequireRetainedCollisionTupleAsync(SqliteConnection connection, SqliteTransaction? transaction,
            string pairId, string side, CancellationToken cancellationToken)
    {
        var operations = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT operation_id FROM operation WHERE pair_id=$pair AND kind='allocate_control' " +
                "ORDER BY operation_id;";
            command.Parameters.AddWithValue("$pair", pairId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) operations.Add(reader.GetString(0));
        }
        if (operations.Count != 1)
            throw new InvalidOperationException(
                "The retained-control recovery requires exactly one current private staging allocation " +
                "operation for the pair; a missing or ambiguous allocation refuses.");
        var operationId = operations[0];

        var anchor = await ScalarStringAsync(connection, transaction,
            "SELECT namespace_allocation_id FROM namespace_allocation WHERE operation_id=$operation " +
            "AND side=$side ORDER BY namespace_allocation_id LIMIT 1;",
            cancellationToken, ("$operation", operationId), ("$side", side))
            ?? throw new InvalidOperationException(
                "The retained-control recovery requires the current private staging allocation on this side.");

        var violations = await ScalarLongAsync(connection, transaction,
            """
            SELECT
              (SELECT count(*) FROM operation o WHERE o.operation_id=$operation AND o.status<>'blocked')
            + (SELECT count(*) FROM namespace_allocation n WHERE n.operation_id=$operation AND n.side=$side
                 AND (n.status<>'blocked' OR n.bound_identity IS NOT NULL))
            + (CASE WHEN EXISTS(SELECT 1 FROM namespace_allocation n WHERE n.namespace_allocation_id=$allocation
                 AND n.side=$side AND n.status='blocked' AND n.bound_identity IS NULL) THEN 0 ELSE 1 END)
            + (CASE WHEN EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=$operation) THEN 0 ELSE 1 END)
            + (SELECT count(*) FROM operation_attempt a WHERE a.operation_id=$operation
                 AND (a.status<>'blocked' OR a.effect_kind<>'local-control-directory-precondition'))
            + (SELECT count(*) FROM operation_attempt a WHERE a.operation_id=$operation
                 AND NOT EXISTS(SELECT 1 FROM effect_receipt e WHERE e.attempt_id=a.attempt_id
                        AND e.certainty='known_conflict'))
            + (SELECT count(*) FROM operation_attempt a JOIN effect_receipt e ON e.attempt_id=a.attempt_id
                 WHERE a.operation_id=$operation AND e.certainty<>'known_conflict')
            + (SELECT count(*) FROM lease l WHERE l.status='active' AND (l.scope_id=$operation
                 OR l.scope_id=$pair OR l.scope_id=$allocation
                 OR l.scope_id IN (SELECT operation_member_id FROM operation_member WHERE operation_id=$operation)
                 OR l.scope_id IN (SELECT root_binding_id FROM root_binding WHERE pair_id=$pair)));
            """,
            cancellationToken, ("$operation", operationId), ("$allocation", anchor),
            ("$side", side), ("$pair", pairId));
        if (violations != 0)
            throw new InvalidOperationException(
                "The current private staging allocation is not the immutable non-creating collision " +
                "(an attempt without its own known_conflict evidence, or issued, pending, unknown, effectful, " +
                "bound or leased work, refuses); retained-control recovery does not rewrite or adopt it.");

        var unresolved = await ScalarLongAsync(connection, transaction,
            $"""
            SELECT count(*) FROM operation o
            WHERE (o.pair_id=$pair OR EXISTS(SELECT 1 FROM json_each(o.preconditions_json,'$.planningAuthorities') retained
                    WHERE json_extract(retained.value,'$.PairId')=$pair))
              AND o.operation_id<>$operation
              AND o.status NOT IN ('completed','cancelled','superseded')
              AND NOT(o.status='blocked' AND {KnownNoEffectRefusalPredicate});
            """,
            cancellationToken, ("$pair", pairId), ("$operation", operationId));
        if (unresolved != 0)
            throw new InvalidOperationException(
                "Retained-control recovery cannot proceed while queued, interrupted, blocked, cleanup or " +
                "planning-referenced pair work remains.");

        var digest = await ComputeRetainedCollisionDigestAsync(connection, transaction, operationId, anchor,
            pairId, cancellationToken);
        return (operationId, anchor, digest);
    }

    /// <summary>
    /// A compact, order-stable digest over the CURRENT immutable collided records. Its lease scope is
    /// EXACTLY the scope the transaction boundary checks: the collision operation, the pair, the anchor
    /// allocation, every operation member and every pair root binding. It is recomputed from current rows and
    /// never written.
    /// </summary>
    private static async Task<string> ComputeRetainedCollisionDigestAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string operationId, string anchorAllocationId, string pairId,
        CancellationToken cancellationToken)
    {
        var canonical = new StringBuilder();
        await AppendCanonicalRowsAsync(connection, transaction, canonical, cancellationToken,
            "SELECT operation_id,status,kind,configuration_revision,preconditions_json FROM operation " +
            "WHERE operation_id=$id ORDER BY operation_id;", ("$id", operationId));
        await AppendCanonicalRowsAsync(connection, transaction, canonical, cancellationToken,
            "SELECT namespace_allocation_id,operation_id,side,parent_identity,deterministic_name," +
            "owner_nonce_hash,status,COALESCE(bound_identity,'') FROM namespace_allocation " +
            "WHERE namespace_allocation_id=$id ORDER BY namespace_allocation_id;", ("$id", anchorAllocationId));
        await AppendCanonicalRowsAsync(connection, transaction, canonical, cancellationToken,
            "SELECT a.attempt_id,a.attempt_no,a.effect_kind,a.status,a.request_fingerprint," +
            "COALESCE(e.effect_receipt_id,''),COALESCE(e.certainty,''),COALESCE(e.evidence_digest,'') " +
            "FROM operation_attempt a LEFT JOIN effect_receipt e ON e.attempt_id=a.attempt_id " +
            "WHERE a.operation_id=$id ORDER BY a.attempt_no,a.attempt_id,e.effect_receipt_id;",
            ("$id", operationId));
        await AppendCanonicalRowsAsync(connection, transaction, canonical, cancellationToken,
            "SELECT operation_member_id,role,root_binding_id,COALESCE(expected_path_key,'') " +
            "FROM operation_member WHERE operation_id=$id ORDER BY operation_member_id;", ("$id", operationId));
        await AppendCanonicalRowsAsync(connection, transaction, canonical, cancellationToken,
            "SELECT lease_id,scope_kind,scope_id,status,fencing_token FROM lease WHERE " +
            "scope_id IN ($id,$allocation,$pair) OR scope_id IN (SELECT operation_member_id FROM " +
            "operation_member WHERE operation_id=$id) OR scope_id IN (SELECT root_binding_id FROM root_binding " +
            "WHERE pair_id=$pair) ORDER BY lease_id;",
            ("$id", operationId), ("$allocation", anchorAllocationId), ("$pair", pairId));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static async Task AppendCanonicalRowsAsync(SqliteConnection connection,
        SqliteTransaction? transaction, StringBuilder canonical, CancellationToken cancellationToken,
        string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var value = reader.IsDBNull(index)
                    ? ""
                    : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "";
                canonical.Append(value.Length).Append(':').Append(value).Append('|');
            }
            canonical.Append('\n');
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // RC639-2: the authentic held-native reopen of the exact retained root and generation.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// RC639-2: reopen the retained, authentic owned private control namespace for its exact selected root.
    /// The existing SyncControlNamespace.OpenLocal read guard authenticates the deterministic same-volume
    /// sibling directory. It creates no directory, writes no marker and allocates no new namespace.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static PilotDirectoryLease ReopenRetainedControlNamespace(SyncControlNamespaceBinding binding,
        PilotDirectoryLease selectedRoot)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(selectedRoot);
        selectedRoot.VerifyBinding();
        if (binding.Local is null) throw new InvalidDataException(
            "The retained private control namespace binding is absent.");
        return SyncControlNamespace.OpenLocal(binding, selectedRoot);
    }

    /// <summary>
    /// RC639-2/RC639-4.AUTH-4: reopen the exactly retained control namespace of this pair, side, root binding
    /// and generation through the SAME central current-ownership resolver the publication guards use, after
    /// requiring the held selected root to be the pair's retained root at that generation. Ambiguity or a
    /// retired, superseded or foreign root refuses.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal async Task<PilotDirectoryLease> ReopenRetainedControlAsync(string pairId, string side,
        string rootBindingId, long rootGeneration, PilotDirectoryLease selectedRoot,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        RequireIdentifier(rootBindingId, nameof(rootBindingId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        if (rootGeneration < 1) throw new ArgumentOutOfRangeException(nameof(rootGeneration));
        ArgumentNullException.ThrowIfNull(selectedRoot);
        var installationId = await LoadInstallationIdAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "The authority database retains no installation identity for retained-control recovery.");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var retainedRoot = await ScalarStringAsync(connection, null,
            "SELECT identity_json FROM root_binding WHERE root_binding_id=$root AND pair_id=$pair " +
            "AND side=$side AND generation=$generation AND state IN ('active','blocked');",
            cancellationToken, ("$root", rootBindingId), ("$pair", pairId), ("$side", side),
            ("$generation", rootGeneration));
        if (string.IsNullOrEmpty(retainedRoot))
            throw new InvalidOperationException(
                "The pair retains no legitimate selected-root binding at that exact id and generation.");
        selectedRoot.VerifyBinding();
        // The exact retained root row exists at this id and generation. The physical correspondence between
        // the held selected root and the retained private control namespace is established by OpenLocal, which
        // requires the control root to be the exact deterministic same-volume sibling of the held root. A
        // cloud candidate's root row carries a cloud identity rather than a directory identity, so no
        // directory-identity comparison is made against it.
        var retained = await ResolveRecoveredControlOwnershipAsync(connection, null, pairId, side, rootBindingId,
            rootGeneration, installationId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The pair retains no current authenticated recovered control ownership for that exact root " +
                "and generation.");
        return ReopenRetainedControlNamespace(BuildRetainedControlBinding(installationId, retained, selectedRoot),
            selectedRoot);
    }

    /// <summary>Builds the retained control binding from the retained record and the held selected root.</summary>
    [SupportedOSPlatform("windows")]
    private static SyncControlNamespaceBinding BuildRetainedControlBinding(string installationId,
        RetainedControlRecoveryProof retained, PilotDirectoryLease selectedRoot)
    {
        var namespaceId = SyncControlNamespace.NormalizeNamespaceId(retained.ControlNamespaceId);
        var plan = SyncControlNamespace.PlanLocalAllocation(selectedRoot, installationId, namespaceId);
        if (!uint.TryParse(retained.ControlVolumeSerialHex, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var volumeSerial) || volumeSerial == 0 ||
            !ulong.TryParse(retained.ControlFileIdHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out var fileId) || fileId == 0)
            throw new InvalidDataException("The retained private control identity is malformed.");
        return new SyncControlNamespaceBinding(installationId, namespaceId,
            new SyncLocalControlNamespaceBinding(plan.ControlPath, volumeSerial, fileId,
                retained.MarkerFileId, retained.MarkerContentHash));
    }

    // ---------------------------------------------------------------------------------------------------
    // RC639-3.AUTH-1 / RC639-2.AUTH-2 / RC639-3: pinned derivation, held-native reopen, then commit.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// RC639-3: the single guarded retained-control recovery. It captures the exact held current selected
    /// root, pins and reads the trusted original database, authenticates the retained control namespace and
    /// holds its single-link marker read guard across the commit, rechecks the current installation and root
    /// inside the write transaction, and only then appends the retained history record and registers the
    /// retained control and pair-control binding through the internal typed branch. The collided rows stay
    /// byte-identical. Replay compares the complete bound tuple and is idempotent.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal async Task<RetainedControlRecoveryOutcome> RetainRecoveredControlAsync(RetainedControlSource source,
        PilotDirectoryLease selectedRoot, CancellationToken cancellationToken = default,
        long? expectedUnpublishedRevision = null)
    {
        // an EXPLICIT supplied expectation is preserved and enforced with no bypass - the
        // caller's revision is used with MustBeUnpublished = true. Only an OMITTED expectation derives the current
        // mode and revision, and even then the transaction still always checks them.
        if (expectedUnpublishedRevision is { } suppliedRevision)
            return await RetainRecoveredControlAsync(source, selectedRoot,
                new RetainedControlRecoveryExpectation(source.PairId, suppliedRevision, MustBeUnpublished: true),
                cancellationToken);
        var current = await LoadRetainedPairStateAsync(source.PairId, cancellationToken);
        if (current is not { } state)
            throw new InvalidOperationException(
                "No current retained pair row exists for this recovery; it is refused before any metadata is written.");
        return await RetainRecoveredControlAsync(source, selectedRoot,
            new RetainedControlRecoveryExpectation(source.PairId, state.Revision, MustBeUnpublished: !state.Published),
            cancellationToken);
    }

    /// <summary>
    /// the exact current mode, identity and revision the recovery binds INSIDE its retention
    /// transaction. There is no null bypass: an initial publication requires the pair to be UNPUBLISHED at that
    /// revision and a published repair requires it to be PUBLISHED at that revision.
    /// </summary>
    internal sealed record RetainedControlRecoveryExpectation(string PairId, long ExpectedRevision,
        bool MustBeUnpublished);

    /// <summary>The current retained pair mode and revision, or null when no legitimate row exists.</summary>
    internal async Task<(long Revision, bool Published)?> LoadRetainedPairStateAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT configuration_revision, pair_json IS NOT NULL FROM sync_pair WHERE pair_id=$pair " +
            "AND state<>'retired' ORDER BY configuration_revision DESC LIMIT 1;";
        command.Parameters.AddWithValue("$pair", pairId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetInt64(0), reader.GetInt64(1) != 0);
    }

    [SupportedOSPlatform("windows")]
    internal async Task<RetainedControlRecoveryOutcome> RetainRecoveredControlAsync(RetainedControlSource source,
        PilotDirectoryLease selectedRoot, RetainedControlRecoveryExpectation expectation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selectedRoot);
        ArgumentNullException.ThrowIfNull(expectation);
        RequireIdentifier(source.PairId, nameof(source));
        RequireIdentifier(source.RootBindingId, nameof(source));
        RequireHash(source.ExpectedSourceSha256, nameof(source));
        if (source.Side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(source));
        selectedRoot.VerifyBinding();

        var installationId = await LoadInstallationIdAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "The authority database retains no installation identity for retained-control recovery.");

        long expectedRootGeneration;
        string expectedRootIdentity;
        string expectedCollisionOperationId;
        string expectedAnchorAllocationId;
        string expectedDigest;
        await using (var readConnection = await OpenConnectionAsync(cancellationToken))
        {
            // The pair must own this exact root row at this exact id. Its state may be the published active
            // state or the unpublished candidate blocked state, because both are legitimate pair authoring
            // states for the same physical root; a superseded or out-of-scope row is never accepted and the
            // held physical identity is still required to match exactly.
            expectedRootGeneration = await ScalarLongAsync(readConnection, null,
                "SELECT generation FROM root_binding WHERE root_binding_id=$root AND pair_id=$pair " +
                "AND side=$side AND state IN ('active','blocked') ORDER BY generation DESC LIMIT 1;",
                cancellationToken, ("$root", source.RootBindingId), ("$pair", source.PairId),
                ("$side", source.Side));
            if (expectedRootGeneration < 1)
                throw new InvalidOperationException(
                    "The pair retains no owned selected-root binding at that exact id on this side.");
            expectedRootIdentity = await ScalarStringAsync(readConnection, null,
                "SELECT identity_json FROM root_binding WHERE root_binding_id=$root AND pair_id=$pair " +
                "AND side=$side AND generation=$generation AND state IN ('active','blocked');",
                cancellationToken, ("$root", source.RootBindingId), ("$pair", source.PairId),
                ("$side", source.Side), ("$generation", expectedRootGeneration))
                ?? throw new InvalidOperationException(
                    "The pair retains no exact selected-root binding at that id and generation.");
            (expectedCollisionOperationId, expectedAnchorAllocationId, expectedDigest) =
                await RequireRetainedCollisionTupleAsync(readConnection, null, source.PairId, source.Side,
                    cancellationToken);
        }

        // The pinned physical-root correspondence is the HELD selected root identity recorded by the original
        // allocation; for a cloud/cloud candidate that is the held state anchor, and for a local pair it is the
        // held local root. The retained record still stores the root row's own identity for the resolver.
        var heldRootIdentityJson = JsonSerializer.Serialize(new
        {
            volumeSerial = selectedRoot.Identity.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture),
            fileId = selectedRoot.Identity.FileId.ToString("x16", CultureInfo.InvariantCulture)
        });
        var proof = (await DeriveRetainedControlProofAsync(source, installationId, heldRootIdentityJson,
            cancellationToken)) with
        {
            RootGeneration = expectedRootGeneration,
            RootIdentityJson = expectedRootIdentity,
            CollisionOperationId = expectedCollisionOperationId,
            AnchorAllocationId = expectedAnchorAllocationId,
            CollisionDigest = expectedDigest
        };

        // Authenticate the authentic retained control namespace and HOLD both the control directory guard and
        // the exact single-link marker read guard across the decisive transaction. A missing, moved, replaced,
        // tampered, hardlinked or foreign control refuses here with no database effect, and no writer can
        // substitute the marker while the commit runs.
        var binding = BuildRetainedControlBinding(installationId, proof, selectedRoot);
        using var heldControl = ReopenRetainedControlNamespace(binding, selectedRoot);
        heldControl.VerifyBinding();
        using var heldMarker = SyncLocalFileAuthority.OpenExistingReadGuard(
            Path.Combine(heldControl.Path, SyncControlNamespace.MarkerName));
        SyncControlNamespace.VerifyLocalMarker(binding, heldControl, heldMarker);

        RetainedControlRecoveryOutcome? outcome = null;
        await WriteAsync(async (connection, transaction) =>
        {
            // Rebind the CURRENT installation and the exact selected root id, pair, side, generation,
            // identity and legitimate authoring state against the captured expectation before appending
            // anything. A legitimate change between the read and the write refuses with no recovered row.
            var currentInstallation = await ScalarStringAsync(connection, transaction,
                "SELECT installation_id FROM installation ORDER BY installation_id LIMIT 1;", cancellationToken);
            if (!string.Equals(currentInstallation, installationId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The authority installation changed before the retained-control metadata was committed; " +
                    "the recovery refuses.");
            var currentRoot = await ScalarStringAsync(connection, transaction,
                "SELECT identity_json FROM root_binding WHERE root_binding_id=$root AND pair_id=$pair " +
                "AND side=$side AND generation=$generation AND state IN ('active','blocked');",
                cancellationToken, ("$root", source.RootBindingId), ("$pair", source.PairId),
                ("$side", source.Side), ("$generation", expectedRootGeneration));
            if (!string.Equals(currentRoot, expectedRootIdentity, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The exact selected root changed before the retained-control metadata was committed; the " +
                    "recovery refuses without effect.");

            // the current published-vs-unpublished mode and the expected current pair revision are
            // bound INSIDE this existing transaction, so a change between the caller's read and this commit
            // refuses with no recovered row written. The pair must still be an UNPUBLISHED candidate at exactly
            // the expected revision.
            {
            if (!string.Equals(expectation.PairId, source.PairId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The retained-control expectation does not name this exact pair; the recovery refuses " +
                    "without effect.");
            long currentPairRevision = -1;
            var currentPairPublished = false;
            await using (var expectCommand = connection.CreateCommand())
            {
                expectCommand.Transaction = transaction;
                expectCommand.CommandText =
                    "SELECT configuration_revision, pair_json IS NOT NULL FROM sync_pair WHERE pair_id=$pair " +
                    "AND state<>'retired' ORDER BY configuration_revision DESC LIMIT 1;";
                expectCommand.Parameters.AddWithValue("$pair", source.PairId);
                await using var expectReader = await expectCommand.ExecuteReaderAsync(cancellationToken);
                if (await expectReader.ReadAsync(cancellationToken))
                {
                    currentPairRevision = expectReader.GetInt64(0);
                    currentPairPublished = expectReader.GetInt64(1) != 0;
                }
            }
            if (currentPairRevision != expectation.ExpectedRevision ||
                currentPairPublished == expectation.MustBeUnpublished)
                throw new InvalidOperationException(
                    "The current pair mode or revision changed before the retained-control metadata was " +
                    "committed; the recovery refuses without effect.");
            }
            var (collisionOperationId, anchorAllocationId, digest) = await RequireRetainedCollisionTupleAsync(
                connection, transaction, source.PairId, source.Side, cancellationToken);
            if (!string.Equals(digest, expectedDigest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The immutable non-creating collision changed after the trusted source was read; the " +
                    "retained-control recovery replays against a stale expectation and refuses.");
            var bound = proof with
            {
                CollisionOperationId = collisionOperationId,
                AnchorAllocationId = anchorAllocationId,
                CollisionDigest = digest
            };
            var existing = await ReadRetainedControlRecordAsync(connection, transaction, source.PairId,
                source.Side, source.RootBindingId, bound.RootGeneration, cancellationToken);
            if (existing is not null)
            {
                RequireSameRecoveredAuthority(existing, bound);
                var replayEvent = await ScalarStringAsync(connection, transaction,
                    "SELECT history_event_id FROM history_event WHERE event_kind=$kind " +
                    "AND json_extract(detail_json,'$.PairId')=$pair " +
                    "AND json_extract(detail_json,'$.Side')=$side " +
                    "AND json_extract(detail_json,'$.RootBindingId')=$root " +
                    "AND json_extract(detail_json,'$.RootGeneration')=$generation " +
                    "ORDER BY occurred_utc DESC, rowid DESC LIMIT 1;",
                    cancellationToken, ("$kind", RetainedControlRecoveryHistoryKind), ("$pair", source.PairId),
                    ("$side", source.Side), ("$root", source.RootBindingId),
                    ("$generation", bound.RootGeneration));
                outcome = new RetainedControlRecoveryOutcome(source.PairId, source.Side, source.RootBindingId,
                    bound.RootGeneration, bound.ControlNamespaceId, digest, replayEvent ?? "", Replayed: true);
                return;
            }
            var historyEventId = await AppendRetainedControlRecoveryAsync(connection, transaction, bound,
                collisionOperationId, cancellationToken);
            await RegisterRecoveredControlNamespaceCoreAsync(connection, transaction, bound,
                anchorAllocationId, cancellationToken);
            outcome = new RetainedControlRecoveryOutcome(source.PairId, source.Side, source.RootBindingId,
                bound.RootGeneration, bound.ControlNamespaceId, digest, historyEventId, Replayed: false);
        }, cancellationToken);
        heldControl.VerifyBinding();
        SyncControlNamespace.VerifyLocalMarker(binding, heldControl, heldMarker);
        return outcome ?? throw new InvalidOperationException(
            "The retained-control recovery did not reach a retained result.");
    }

    /// <summary>
    /// LC653-4: the CURRENT configuration revision of this pair in any legitimate authoring state (published or
    /// unpublished candidate), used only as the CLI's current-authority CAS for an initial publication. It reads
    /// no pair JSON and creates nothing.
    /// </summary>
    internal async Task<long> LoadRetainedPairRevisionAsync(string pairId, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarLongAsync(connection, null,
            "SELECT coalesce(max(configuration_revision),0) FROM sync_pair WHERE pair_id=$pair AND state<>'retired';",
            cancellationToken, ("$pair", pairId));
    }

    /// <summary>RC639-3.REPLAY-1: the full bound semantic authority tuple must match exactly on replay.</summary>
    private static void RequireSameRecoveredAuthority(RetainedControlRecoveryProof existing,
        RetainedControlRecoveryProof bound)
    {
        foreach (var (name, left, right) in new[]
                 {
                     ("pair", existing.PairId, bound.PairId),
                     ("side", existing.Side, bound.Side),
                     ("root binding", existing.RootBindingId, bound.RootBindingId),
                     ("root identity", existing.RootIdentityJson, bound.RootIdentityJson),
                     ("installation", existing.InstallationId, bound.InstallationId),
                     ("control namespace", existing.ControlNamespaceId, bound.ControlNamespaceId),
                     ("origin allocation", existing.OriginAllocationId, bound.OriginAllocationId),
                     ("anchor allocation", existing.AnchorAllocationId, bound.AnchorAllocationId),
                     ("origin operation", existing.OriginOperationId, bound.OriginOperationId),
                     ("origin attempt", existing.OriginAttemptId, bound.OriginAttemptId),
                     ("origin receipt", existing.OriginReceiptId, bound.OriginReceiptId),
                     ("origin lease", existing.OriginLeaseId, bound.OriginLeaseId),
                     ("origin lock root", existing.OriginLockRootBindingId, bound.OriginLockRootBindingId),
                     ("control identity", existing.IdentityJson, bound.IdentityJson),
                     ("marker identity", existing.MarkerIdentityJson, bound.MarkerIdentityJson),
                     ("marker file identity", existing.MarkerFileId, bound.MarkerFileId),
                     ("marker hash", existing.MarkerContentHash, bound.MarkerContentHash),
                     ("control volume", existing.ControlVolumeSerialHex, bound.ControlVolumeSerialHex),
                     ("control file id", existing.ControlFileIdHex, bound.ControlFileIdHex),
                     ("collision operation", existing.CollisionOperationId, bound.CollisionOperationId),
                     ("collision digest", existing.CollisionDigest, bound.CollisionDigest)
                 })
        {
            if (!string.Equals(left, right, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "A different retained-control recovery is already bound for this exact pair, side, root " +
                    "and generation (the " + name + " disagrees); the conflicting replay refuses.");
        }
        if (existing.RootGeneration != bound.RootGeneration ||
            existing.PairControlBindingGeneration != bound.PairControlBindingGeneration)
            throw new InvalidOperationException(
                "A different retained-control recovery is already bound for this exact pair, side and root " +
                "(the root or pair-control binding generation disagrees); the conflicting replay refuses.");
    }

    /// <summary>
    /// RC639-3.AUTH-1: derive the typed proof by reading the PINNED original authority database through the
    /// existing trusted read guard. A non-empty unpinned write-ahead log refuses at the held boundary, the
    /// exact bytes are hashed from the held handle, the held identity, single link and final path are
    /// revalidated after the read, the source installation must equal the current database installation, the
    /// original selected root must correspond physically to the held root identity, and the ONE coherent
    /// completed chain is required: completed allocate_control, verified allocation whose bound identity
    /// equals the control identity, exactly one selected-root lock member, exactly one verified-success
    /// attempt whose own lease is released and equals the known-success receipt's recorded lease, an active
    /// control and an active pair binding with its exact binding generation. Zero or multiple, foreign,
    /// pending, issued, unknown, unjoined or hardlinked cases refuse. The source is read-only and never
    /// written.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task<RetainedControlRecoveryProof> DeriveRetainedControlProofAsync(RetainedControlSource source,
        string currentInstallationId, string expectedRootIdentityJson, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(source.SourceDatabasePath);
        if (!File.Exists(path))
            throw new InvalidDataException("The pinned original authority database is not present.");

        // Take the trusted read guard FIRST: it excludes writers and deleters, so no unbound write-ahead log
        // can be created or extended for the whole pinned derivation.
        using var pinnedSource = SyncLocalFileAuthority.OpenExistingReadGuard(path);
        var heldIdentity = pinnedSource.VerifySingleLink();
        if (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 0)
            throw new InvalidDataException(
                "The pinned original authority database carries a non-empty unpinned write-ahead log at the " +
                "held read boundary; an unbound WAL cannot contribute retained-control authority, so recovery " +
                "refuses.");
        string observed;
        {
            var stream = pinnedSource.Stream;
            stream.Position = 0;
            observed = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
        }
        if (!string.Equals(observed, source.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The pinned original authority database does not match its exact source identity; the " +
                "retained-control derivation refuses a changed source.");

        RetainedControlRecoveryProof? derived = null;
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync(cancellationToken);
            var sourceInstallation = await ScalarStringAsync(connection, null,
                "SELECT installation_id FROM installation ORDER BY installation_id LIMIT 1;", cancellationToken)
                ?? throw new InvalidDataException(
                    "The pinned original authority database retains no installation.");
            if (!string.Equals(sourceInstallation, currentInstallationId, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "The pinned original authority database belongs to another installation than the current " +
                    "database; retained-control recovery refuses.");

            var chains = new List<RetainedControlRecoveryProof>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT c.control_namespace_id,a.namespace_allocation_id,c.identity_json,
                           c.marker_identity_json,c.owner_marker_hash,a.operation_id,o.pair_id,
                           m.root_binding_id,t.attempt_id,r.effect_receipt_id,t.lease_id,
                           COALESCE(b.binding_generation,0)
                    FROM control_namespace c
                    JOIN namespace_allocation a ON a.namespace_allocation_id=c.namespace_allocation_id
                      AND a.bound_identity=c.identity_json
                    JOIN operation o ON o.operation_id=a.operation_id
                    JOIN pair_control_namespace b ON b.control_namespace_id=c.control_namespace_id
                      AND b.pair_id=o.pair_id AND b.side=c.side AND b.status='active'
                    JOIN operation_member m ON m.operation_id=o.operation_id AND m.role='lock'
                    JOIN root_binding rb ON rb.root_binding_id=m.root_binding_id AND rb.pair_id=o.pair_id
                      AND rb.state IN ('active','blocked')
                    JOIN operation_attempt t ON t.operation_id=o.operation_id AND t.status='verified_success'
                    JOIN effect_receipt r ON r.attempt_id=t.attempt_id AND r.certainty='known_success'
                      AND r.recorded_lease_id=t.lease_id
                    JOIN lease l ON l.lease_id=t.lease_id AND l.status='released'
                    WHERE o.pair_id=$pair AND a.side=$side AND c.side=$side AND c.status='active'
                      AND o.kind='allocate_control' AND o.status='completed'
                      AND a.status='verified' AND a.bound_identity IS NOT NULL
                      -- The ORIGINAL physical root correspondence: the original allocation recorded the
                      -- exact selected-root identity it was created beside. For a cloud/cloud candidate that
                      -- is the held state anchor; for a local pair it is the held local root. It is compared
                      -- to the held identity rather than to a string-equal lock root id.
                      AND json_extract(o.preconditions_json,'$.selectedRootIdentity')=$rootIdentity
                      AND (SELECT count(*) FROM operation_member lm WHERE lm.operation_id=o.operation_id
                            AND lm.role='lock')=1
                      AND (SELECT count(*) FROM operation_attempt ta JOIN effect_receipt ra ON ra.attempt_id=ta.attempt_id
                            WHERE ta.operation_id=o.operation_id AND ta.status='verified_success'
                              AND ra.certainty='known_success')=1
                      AND (SELECT count(*) FROM operation_attempt ta WHERE ta.operation_id=o.operation_id
                            AND ta.status IN ('started','request_sent','response_received','effect_unknown'))=0
                      AND (SELECT count(*) FROM operation_attempt ta JOIN effect_receipt ra ON ra.attempt_id=ta.attempt_id
                            WHERE ta.operation_id=o.operation_id AND ra.certainty IN ('unknown','known_partial'))=0
                      AND NOT EXISTS(SELECT 1 FROM lease la WHERE la.scope_id=o.operation_id AND la.status='active')
                    ORDER BY c.control_namespace_id;
                    """;
                command.Parameters.AddWithValue("$pair", source.PairId);
                command.Parameters.AddWithValue("$side", source.Side);
                command.Parameters.AddWithValue("$rootIdentity", expectedRootIdentityJson);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var identityJson = reader.GetString(2);
                    var identity = ParsePilotDirectoryIdentity(identityJson);
                    chains.Add(new RetainedControlRecoveryProof(source.PairId, source.Side, source.RootBindingId,
                        0, expectedRootIdentityJson, currentInstallationId, reader.GetString(0),
                        reader.GetString(1), "", reader.GetString(5), reader.GetString(8), reader.GetString(9),
                        reader.GetString(10), reader.GetString(7), reader.GetInt64(11), "", "",
                        identity.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture),
                        identity.FileId.ToString("x16", CultureInfo.InvariantCulture),
                        ReadMarkerFileIdentity(reader.GetString(3)), reader.GetString(4), identityJson,
                        reader.GetString(3)));
                }
            }
            if (chains.Count != 1)
                throw new InvalidDataException(
                    "The pinned original database does not carry exactly one authenticated completed private " +
                    "staging creation whose original selected root physically corresponds to the held root; an " +
                    "absent, ambiguous, unjoined or unresolved chain refuses.");
            derived = chains[0];
        }
        var proof = derived ?? throw new InvalidDataException(
            "The pinned original derivation produced no coherent completed chain.");
        foreach (var value in new[] { proof.ControlNamespaceId, proof.OriginAllocationId,
                     proof.OriginOperationId, proof.OriginAttemptId, proof.OriginReceiptId,
                     proof.OriginLeaseId, proof.OriginLockRootBindingId })
            RequireIdentifier(value, nameof(source));
        RequireHash(proof.MarkerContentHash, nameof(source));
        if (proof.PairControlBindingGeneration < 1)
            throw new InvalidDataException(
                "The pinned original pair-control binding carries no exact binding generation; recovery refuses.");

        // Revalidate the held file identity and path after the read, and refuse a late unbound WAL.
        pinnedSource.VerifySameSingleLink(heldIdentity);
        if (!string.Equals(PilotDirectoryLease.FinalPath(pinnedSource.Handle), path,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The pinned original authority database was replaced during the read; recovery refuses.");
        if (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 0)
            throw new InvalidDataException(
                "The pinned original authority database acquired a non-empty unpinned write-ahead log during " +
                "the read; recovery refuses.");
        return proof;
    }

    /// <summary>
    /// Appends ONE retained_control_recovered history_event through the EXISTING INSERT shape under the
    /// approved retained_control_recovered identity (no separate versioned subject namespace). The operation
    /// association is the current collided operation; the detail carries the full typed identity, the
    /// separated origin and anchor allocation ids and the compact current-collision digest.
    /// </summary>
    private static async Task<string> AppendRetainedControlRecoveryAsync(SqliteConnection connection,
        SqliteTransaction transaction, RetainedControlRecoveryProof proof, string collisionOperationId,
        CancellationToken cancellationToken)
    {
        var historyEventId = "history-" + Guid.NewGuid().ToString("N");
        var detail = JsonSerializer.Serialize(new
        {
            EventId = historyEventId,
            EventKind = RetainedControlRecoveryHistoryKind,
            proof.PairId,
            proof.Side,
            proof.RootBindingId,
            proof.RootGeneration,
            proof.RootIdentityJson,
            proof.InstallationId,
            proof.ControlNamespaceId,
            proof.OriginAllocationId,
            proof.AnchorAllocationId,
            proof.OriginOperationId,
            proof.OriginAttemptId,
            proof.OriginReceiptId,
            proof.OriginLeaseId,
            proof.OriginLockRootBindingId,
            proof.PairControlBindingGeneration,
            proof.CollisionOperationId,
            proof.CollisionDigest,
            proof.ControlVolumeSerialHex,
            proof.ControlFileIdHex,
            proof.MarkerFileId,
            proof.MarkerContentHash,
            proof.IdentityJson,
            proof.MarkerIdentityJson,
            RecoveredUtc = Utc(DateTimeOffset.UtcNow)
        });
        await ExecuteAsync(connection, transaction,
            "INSERT INTO history_event(retention_class,rowid,history_event_id,operation_id,event_kind,detail_json,occurred_utc) " +
            "VALUES('safety',(SELECT next_history_sequence FROM installation),$id,$operation,$kind,$detail,$utc);",
            cancellationToken, ("$id", historyEventId), ("$operation", collisionOperationId),
            ("$kind", RetainedControlRecoveryHistoryKind), ("$detail", detail),
            ("$utc", Utc(DateTimeOffset.UtcNow)));
        var exact = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM history_event WHERE history_event_id=$id AND event_kind=$kind " +
            "AND detail_json=$detail;",
            cancellationToken, ("$id", historyEventId), ("$kind", RetainedControlRecoveryHistoryKind),
            ("$detail", detail));
        if (exact != 1)
            throw new InvalidOperationException(
                "The retained_control_recovered history record was not retained exactly.");
        return historyEventId;
    }

    /// <summary>
    /// RC639-3: the INTERNAL typed recovery registration branch. It retains the exact active control and the
    /// pair-control binding, at the retained binding generation, whose guards consume the already-validated
    /// typed proof. It is separate from the public RegisterControlNamespaceAsync: that method's
    /// verified-current-allocation requirement is unchanged for ordinary callers and no public skip flag is
    /// added. The control_namespace row is anchored on the exact CURRENT collision allocation as an inert
    /// relational anchor only; the collided rows are not transitioned, rebound or rewritten.
    /// </summary>
    private static async Task RegisterRecoveredControlNamespaceCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, RetainedControlRecoveryProof proof, string anchorAllocationId,
        CancellationToken cancellationToken)
    {
        var anchorExact = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM namespace_allocation WHERE namespace_allocation_id=$allocation " +
            "AND operation_id=$operation AND status='blocked' AND bound_identity IS NULL;",
            cancellationToken, ("$allocation", anchorAllocationId),
            ("$operation", proof.CollisionOperationId));
        if (anchorExact != 1)
            throw new InvalidOperationException(
                "The inert current collision anchor is not the exact blocked unbound allocation; recovery refuses.");
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO control_namespace(control_namespace_id,namespace_allocation_id,side," +
            "identity_json,marker_identity_json,owner_marker_hash,status,verified_utc) " +
            "VALUES($id,$allocation,$side,$identity,$marker,$hash,'active',$utc);",
            cancellationToken, ("$id", proof.ControlNamespaceId), ("$allocation", anchorAllocationId),
            ("$side", proof.Side), ("$identity", proof.IdentityJson), ("$marker", proof.MarkerIdentityJson),
            ("$hash", proof.MarkerContentHash), ("$utc", Utc(DateTimeOffset.UtcNow)));
        var exact = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM control_namespace WHERE control_namespace_id=$id AND side=$side " +
            "AND namespace_allocation_id=$allocation AND identity_json=$identity " +
            "AND marker_identity_json=$marker AND owner_marker_hash=$hash AND status='active';",
            cancellationToken, ("$id", proof.ControlNamespaceId), ("$side", proof.Side),
            ("$allocation", anchorAllocationId), ("$identity", proof.IdentityJson),
            ("$marker", proof.MarkerIdentityJson), ("$hash", proof.MarkerContentHash));
        if (exact != 1)
            throw new InvalidOperationException(
                "The recovered private control namespace collides with different retained authority.");
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO pair_control_namespace(pair_id,side,control_namespace_id," +
            "binding_generation,status) VALUES($pair,$side,$control,$generation,'active');",
            cancellationToken, ("$pair", proof.PairId), ("$side", proof.Side),
            ("$control", proof.ControlNamespaceId), ("$generation", proof.PairControlBindingGeneration));
        var bound = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM pair_control_namespace WHERE pair_id=$pair AND side=$side " +
            "AND control_namespace_id=$control AND binding_generation=$generation AND status='active';",
            cancellationToken, ("$pair", proof.PairId), ("$side", proof.Side),
            ("$control", proof.ControlNamespaceId), ("$generation", proof.PairControlBindingGeneration));
        if (bound != 1)
            throw new InvalidOperationException(
                "The recovered pair control-namespace binding was not retained exactly at its generation.");
    }

    // ---------------------------------------------------------------------------------------------------
    // RC639-4.AUTH-4: the one central transaction-bound scoped resolver.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// RC639-4.AUTH-4: the one central scoped resolver, used by reopen, publication and preparation alike. It
    /// returns the typed recovered ownership only when the exact DB-owned retained record for this pair, side,
    /// root binding and generation is fully consistent with the CURRENT state: the current installation, the
    /// exact root row at that generation in a legitimate authoring state and identity, the exact current
    /// collision tuple and digest, the exact inert anchor allocation and the exact active control and
    /// pair-control rows including their identity, marker, hash and binding generation. Missing, stale,
    /// forged, ambiguous or mismatched facts refuse. It never infers authority from the blocked anchor or from
    /// caller-supplied JSON.
    /// </summary>
    private static async Task<RetainedControlRecoveryProof?> ResolveRecoveredControlOwnershipAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string pairId, string side,
        string rootBindingId, long rootGeneration, string installationId, CancellationToken cancellationToken)
    {
        var retained = await ReadRetainedControlRecordAsync(connection, transaction, pairId, side, rootBindingId,
            rootGeneration, cancellationToken);
        if (retained is null) return null;
        if (!string.Equals(retained.InstallationId, installationId, StringComparison.Ordinal)) return null;
        var currentIdentity = await ScalarStringAsync(connection, transaction,
            "SELECT identity_json FROM root_binding WHERE root_binding_id=$root AND pair_id=$pair AND side=$side " +
            "AND generation=$generation AND state IN ('active','blocked');",
            cancellationToken, ("$root", rootBindingId), ("$pair", pairId), ("$side", side),
            ("$generation", rootGeneration));
        if (currentIdentity is null ||
            !string.Equals(currentIdentity, retained.RootIdentityJson, StringComparison.Ordinal)) return null;
        var (collisionOperationId, anchorAllocationId, digest) = await RequireRetainedCollisionTupleAsync(
            connection, transaction, pairId, side, cancellationToken);
        if (!string.Equals(collisionOperationId, retained.CollisionOperationId, StringComparison.Ordinal) ||
            !string.Equals(anchorAllocationId, retained.AnchorAllocationId, StringComparison.Ordinal) ||
            !string.Equals(digest, retained.CollisionDigest, StringComparison.OrdinalIgnoreCase))
            return null;
        var active = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM control_namespace c JOIN pair_control_namespace b " +
            "ON b.control_namespace_id=c.control_namespace_id " +
            "WHERE c.control_namespace_id=$control AND c.side=$side AND c.status='active' " +
            "AND c.namespace_allocation_id=$allocation AND c.identity_json=$identity " +
            "AND c.marker_identity_json=$marker AND c.owner_marker_hash=$hash " +
            "AND b.pair_id=$pair AND b.side=$side AND b.status='active' " +
            "AND b.binding_generation=$generation;",
            cancellationToken, ("$control", retained.ControlNamespaceId), ("$side", side),
            ("$allocation", anchorAllocationId), ("$identity", retained.IdentityJson),
            ("$marker", retained.MarkerIdentityJson), ("$hash", retained.MarkerContentHash),
            ("$pair", pairId), ("$generation", retained.PairControlBindingGeneration));
        return active == 1 ? retained : null;
    }

    /// <summary>
    /// RC639-4.INTEGRATION-1: the operation id of the exact authenticated recovered NONCREATING collision that
    /// is bound to THIS pair's actual current candidate root, or null. The pair must carry exactly one
    /// legitimate current root on the local side; ambiguity or an unmatched historical record yields null, so
    /// a wrong recovery can never excuse the collision for a different prepared root. Used only to excuse
    /// that one record from an otherwise unchanged refusal guard.
    /// </summary>
    private static async Task<string?> ResolveRecoveredNonCreatingCollisionOperationIdAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string pairId, string installationId,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string Root, long Generation)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT root_binding_id,generation FROM root_binding WHERE pair_id=$pair AND side='local' " +
                "AND state IN ('active','blocked') ORDER BY root_binding_id;";
            command.Parameters.AddWithValue("$pair", pairId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                candidates.Add((reader.GetString(0), reader.GetInt64(1)));
        }
        if (candidates.Count != 1) return null;
        var resolved = await ResolveRecoveredControlOwnershipAsync(connection, transaction, pairId, "local",
            candidates[0].Root, candidates[0].Generation, installationId, cancellationToken);
        return resolved?.CollisionOperationId;
    }

    /// <summary>RC639-5: the retained generation of one exact pair root binding, or 0 when it is absent.</summary>
    internal async Task<long> LoadRootBindingGenerationAsync(string pairId, string rootBindingId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        RequireIdentifier(rootBindingId, nameof(rootBindingId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarLongAsync(connection, null,
            "SELECT COALESCE(max(generation),0) FROM root_binding WHERE root_binding_id=$root " +
            "AND pair_id=$pair;",
            cancellationToken, ("$root", rootBindingId), ("$pair", pairId));
    }

    /// <summary>
    /// RC639-4: the central scoped reader for the granted consumers. It resolves only the exact authenticated
    /// retained recovery of this pair, side, root and generation.
    /// </summary>
    internal async Task<bool> HasRecoveredControlOwnershipAsync(string pairId, string side, string rootBindingId,
        long rootGeneration, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        RequireIdentifier(rootBindingId, nameof(rootBindingId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        if (rootGeneration < 1) throw new ArgumentOutOfRangeException(nameof(rootGeneration));
        var installationId = await LoadInstallationIdAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "The authority database retains no installation identity for retained-control recovery.");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ResolveRecoveredControlOwnershipAsync(connection, null, pairId, side, rootBindingId,
            rootGeneration, installationId, cancellationToken) is not null;
    }

    /// <summary>
    /// RC639-4: the alternate ownership branch beside the strict completed-candidate control check. The strict
    /// verified and completed current-allocation branch is attempted first and is unchanged; only when it does
    /// not hold may the fully validated scoped recovered ownership satisfy the same guard. The strict refusal
    /// text is preserved byte-for-byte.
    /// </summary>
    private static async Task RequireCompletedOrRecoveredCandidateControlAsync(SqliteConnection connection,
        SqliteTransaction transaction, string pairId, string leftRootBindingId,
        CancellationToken cancellationToken, string side = "local")
    {
        try
        {
            await RequireCompletedCandidateControlAsync(connection, transaction, pairId, leftRootBindingId,
                cancellationToken, side);
            return;
        }
        catch (InvalidOperationException)
        {
            // The strict branch did not hold. Only the fully validated scoped resolver may substitute.
        }
        var installationId = await ScalarStringAsync(connection, transaction,
            "SELECT installation_id FROM installation ORDER BY installation_id LIMIT 1;", cancellationToken);
        var generation = await ScalarLongAsync(connection, transaction,
            "SELECT generation FROM root_binding WHERE root_binding_id=$root AND pair_id=$pair AND side=$side;",
            cancellationToken, ("$root", leftRootBindingId), ("$pair", pairId), ("$side", side));
        if (installationId is not null && generation >= 1 &&
            await ResolveRecoveredControlOwnershipAsync(connection, transaction, pairId, side, leftRootBindingId,
                generation, installationId, cancellationToken) is not null)
            return;
        throw new InvalidOperationException(
            "Publication requires the exact completed private staging allocation of this candidate, " +
            "bound to its logical-left root; a missing, foreign or unresolved staging refuses.");
    }

    // ---------------------------------------------------------------------------------------------------
    // Shared retained-record reader and small parsing helpers.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves the typed retained_control_recovered record for this exact pair, side, root binding and
    /// generation from the EXISTING history_event detail_json under the approved event identity. A malformed
    /// record is not a proof and yields null rather than a half-bound one.
    /// </summary>
    private static async Task<RetainedControlRecoveryProof?> ReadRetainedControlRecordAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string pairId, string side,
        string rootBindingId, long rootGeneration, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT detail_json FROM history_event WHERE event_kind=$kind " +
            "AND json_extract(detail_json,'$.PairId')=$pair " +
            "AND json_extract(detail_json,'$.Side')=$side " +
            "AND json_extract(detail_json,'$.RootBindingId')=$root " +
            "AND json_extract(detail_json,'$.RootGeneration')=$generation " +
            "ORDER BY occurred_utc DESC, rowid DESC LIMIT 1;";
        command.Parameters.AddWithValue("$kind", RetainedControlRecoveryHistoryKind);
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$side", side);
        command.Parameters.AddWithValue("$root", rootBindingId);
        command.Parameters.AddWithValue("$generation", rootGeneration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        try
        {
            using var document = JsonDocument.Parse(reader.GetString(0));
            var root = document.RootElement;
            return new RetainedControlRecoveryProof(
                root.GetProperty("PairId").GetString() ?? "",
                root.GetProperty("Side").GetString() ?? "",
                root.GetProperty("RootBindingId").GetString() ?? "",
                root.GetProperty("RootGeneration").GetInt64(),
                root.GetProperty("RootIdentityJson").GetString() ?? "",
                root.GetProperty("InstallationId").GetString() ?? "",
                root.GetProperty("ControlNamespaceId").GetString() ?? "",
                root.GetProperty("OriginAllocationId").GetString() ?? "",
                root.GetProperty("AnchorAllocationId").GetString() ?? "",
                root.GetProperty("OriginOperationId").GetString() ?? "",
                root.GetProperty("OriginAttemptId").GetString() ?? "",
                root.GetProperty("OriginReceiptId").GetString() ?? "",
                root.GetProperty("OriginLeaseId").GetString() ?? "",
                root.GetProperty("OriginLockRootBindingId").GetString() ?? "",
                root.GetProperty("PairControlBindingGeneration").GetInt64(),
                root.GetProperty("CollisionOperationId").GetString() ?? "",
                root.GetProperty("CollisionDigest").GetString() ?? "",
                root.GetProperty("ControlVolumeSerialHex").GetString() ?? "",
                root.GetProperty("ControlFileIdHex").GetString() ?? "",
                root.GetProperty("MarkerFileId").GetString() ?? "",
                root.GetProperty("MarkerContentHash").GetString() ?? "",
                root.GetProperty("IdentityJson").GetString() ?? "",
                root.GetProperty("MarkerIdentityJson").GetString() ?? "");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// RC639-5: the ordinary-preparation reopen of an unpublished candidate whose only unresolved work is the
    /// exact authenticated retained-control recovery. It returns the retained LOCAL control binding when the
    /// central resolver authenticates it for this pair's own current root, otherwise null. It creates nothing.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal async Task<SyncLocalControlNamespaceBinding?> TryReopenRecoveredControlLocalAsync(string pairId,
        string side, PilotDirectoryLease selectedRoot, CancellationToken cancellationToken = default,
        string? expectedInstallationId = null, string? expectedRootBindingId = null,
        string? expectedNamespaceId = null)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        var installationId = await LoadInstallationIdAsync(cancellationToken);
        if (installationId is null) return null;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var root = await ScalarStringAsync(connection, null,
            "SELECT root_binding_id FROM root_binding WHERE pair_id=$pair AND side=$side " +
            "AND state IN ('active','blocked') ORDER BY root_binding_id LIMIT 1;",
            cancellationToken, ("$pair", pairId), ("$side", side));
        if (string.IsNullOrEmpty(root)) return null;
        var generation = await ScalarLongAsync(connection, null,
            "SELECT generation FROM root_binding WHERE root_binding_id=$root AND pair_id=$pair AND side=$side;",
            cancellationToken, ("$root", root), ("$pair", pairId), ("$side", side));
        var retained = await ResolveRecoveredControlOwnershipAsync(connection, null, pairId, side, root!,
            generation, installationId, cancellationToken);
        if (retained is null) return null;
        // R683: an EXISTING recovered proof must be bound to the exact caller authority. A mismatch REFUSES
        // rather than returning null and falling through to an ordinary allocation that would re-register.
        // The root generation comes from this exact retained root and its authenticated proof (resolved
        // above), never from the caller's configurationRevision.
        if (expectedInstallationId is not null &&
            !string.Equals(retained.InstallationId, expectedInstallationId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The retained recovered control binding belongs to a different installation than the caller.");
        if (expectedRootBindingId is not null &&
            !string.Equals(retained.RootBindingId, expectedRootBindingId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The retained recovered control binding belongs to a different root binding than the caller.");
        if (expectedNamespaceId is not null &&
            !string.Equals(SyncControlNamespace.NormalizeNamespaceId(retained.ControlNamespaceId),
                SyncControlNamespace.NormalizeNamespaceId(expectedNamespaceId), StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The retained recovered control binding belongs to a different namespace than the caller.");
        var binding = BuildRetainedControlBinding(installationId, retained, selectedRoot);
        using var reopened = ReopenRetainedControlNamespace(binding, selectedRoot);
        reopened.VerifyBinding();
        return binding.Local;
    }

    /// <summary>
    /// RC639-4.INTEGRATION-1: the resolved recovered non-creating collision of this pair, or null. One shared
    /// entry point for every guard that must discount exactly that one authenticated record.
    /// </summary>
    private static async Task<string?> ResolveRecoveredCollisionForPairAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string pairId, CancellationToken cancellationToken)
    {
        var installationId = await ScalarStringAsync(connection, transaction,
            "SELECT installation_id FROM installation ORDER BY installation_id LIMIT 1;", cancellationToken);
        return installationId is null ? null
            : await ResolveRecoveredNonCreatingCollisionOperationIdAsync(connection, transaction, pairId,
                installationId, cancellationToken);
    }

    /// <summary>Reads the retained marker file identity string out of its retained marker record.</summary>
    private static string ReadMarkerFileIdentity(string markerIdentityJson)
    {
        using var document = JsonDocument.Parse(markerIdentityJson);
        if (document.RootElement.TryGetProperty("fileIdentity", out var value) &&
            value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";
        throw new InvalidDataException("The retained control marker record carries no file identity.");
    }

    /// <summary>Parses the retained volumeSerial/fileId identity of a held directory.</summary>
    private static PilotDirectoryIdentity ParsePilotDirectoryIdentity(string identityJson)
    {
        using var document = JsonDocument.Parse(identityJson);
        var root = document.RootElement;
        var volume = root.GetProperty("volumeSerial").GetString();
        var fileId = root.GetProperty("fileId").GetString();
        if (!uint.TryParse(volume, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedVolume) ||
            parsedVolume == 0 ||
            !ulong.TryParse(fileId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedFileId) ||
            parsedFileId == 0)
            throw new InvalidDataException("The retained control-directory identity is malformed.");
        return new PilotDirectoryIdentity(parsedVolume, parsedFileId);
    }
}
