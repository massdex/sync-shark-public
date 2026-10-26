using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private const string CopySnapshotColumns =
        "pair_id,object_id,point_id,point_generation,local_version_id,remote_version_id," +
        "local_membership_id,remote_membership_id,local_root_id,remote_root_id,path_key," +
        "copy_operation_id,attempt_id,receipt_id,destination_identity_json,attempt_status,receipt_evidence_json";

    // One exact point publication transaction owns both predecessor capture and
    // invalidation. An idempotent replay of the same point must not discard proof.
    private static async Task PrepareCurrentCopyPointAsync(SqliteConnection connection,
        SqliteTransaction transaction, string pointId, string pairId, string objectId,
        string? localVersion, string? remoteVersion, string? localMember, string? remoteMember,
        long generation, CancellationToken token)
    {
        using var commands = new ObservationCommands(connection, transaction);
        await PrepareCurrentCopyPointAsync(commands, pointId, pairId, objectId, localVersion, remoteVersion,
            localMember, remoteMember, generation, token);
    }

    // A batch passes one command set so its statements are prepared once per transaction.
    private static async Task PrepareCurrentCopyPointAsync(ObservationCommands commands,
        string pointId, string pairId, string objectId,
        string? localVersion, string? remoteVersion, string? localMember, string? remoteMember,
        long generation, CancellationToken token)
    {
        if (await commands.ScalarLongAsync(
                "SELECT count(*) FROM synchronization_point WHERE synchronization_point_id=$id;",
                token, ("$id", pointId)) != 0) return;
        await commands.ExecuteAsync($"""
            INSERT INTO copy_predecessor({CopySnapshotColumns})
            SELECT p.pair_id,p.object_id,p.synchronization_point_id,p.authority_generation,
                p.local_object_version_id,p.dropbox_object_version_id,p.local_membership_id,p.dropbox_membership_id,
                a.root_binding_id,b.root_binding_id,a.relative_path_key,
                c.copy_operation_id,c.attempt_id,c.receipt_id,c.destination_identity_json,c.attempt_status,c.receipt_evidence_json
            FROM synchronization_point p
            JOIN membership a ON a.membership_id=p.local_membership_id
            JOIN membership b ON b.membership_id=p.dropbox_membership_id
            LEFT JOIN current_copy_completion c ON c.pair_id=p.pair_id AND c.object_id=p.object_id
                AND c.point_id=p.synchronization_point_id AND c.point_generation=p.authority_generation
            WHERE p.pair_id=$pair AND p.object_id=$object
                AND p.authority_generation=(SELECT max(authority_generation) FROM synchronization_point
                    WHERE pair_id=$pair AND object_id=$object)
                AND p.local_object_version_id IS NOT NULL AND p.dropbox_object_version_id IS NOT NULL
                AND a.relative_path_key=b.relative_path_key
            ON CONFLICT(pair_id,object_id,local_membership_id) DO UPDATE SET
                point_id=excluded.point_id,point_generation=excluded.point_generation,
                local_version_id=excluded.local_version_id,remote_version_id=excluded.remote_version_id,
                remote_membership_id=excluded.remote_membership_id,local_root_id=excluded.local_root_id,
                remote_root_id=excluded.remote_root_id,path_key=excluded.path_key,
                copy_operation_id=excluded.copy_operation_id,attempt_id=excluded.attempt_id,
                receipt_id=excluded.receipt_id,destination_identity_json=excluded.destination_identity_json,
                attempt_status=excluded.attempt_status,receipt_evidence_json=excluded.receipt_evidence_json
            WHERE excluded.point_generation>copy_predecessor.point_generation;
            """, token, ("$pair", pairId), ("$object", objectId));
        await commands.ExecuteAsync("""
            INSERT INTO current_copy_completion(pair_id,object_id,point_id,point_generation,
                local_version_id,remote_version_id,local_membership_id,remote_membership_id,
                local_root_id,remote_root_id,path_key,coverage_verified)
            VALUES($pair,$object,$point,$generation,$local,$remote,$lm,$rm,
                (SELECT root_binding_id FROM membership WHERE membership_id=$lm),
                (SELECT root_binding_id FROM membership WHERE membership_id=$rm),
                (SELECT a.relative_path_key FROM membership a JOIN membership b
                    ON b.membership_id=$rm AND a.relative_path_key=b.relative_path_key WHERE a.membership_id=$lm),
                NOT EXISTS(SELECT 1 FROM synchronization_point WHERE pair_id=$pair AND object_id=$object))
            ON CONFLICT(pair_id,object_id) DO UPDATE SET
                point_id=excluded.point_id,point_generation=excluded.point_generation,
                local_version_id=excluded.local_version_id,remote_version_id=excluded.remote_version_id,
                local_membership_id=excluded.local_membership_id,remote_membership_id=excluded.remote_membership_id,
                local_root_id=excluded.local_root_id,remote_root_id=excluded.remote_root_id,path_key=excluded.path_key,
                copy_operation_id=NULL,attempt_id=NULL,receipt_id=NULL,
                destination_identity_json=NULL,attempt_status=NULL,receipt_evidence_json=NULL;
            """, token, ("$pair", pairId), ("$object", objectId), ("$point", pointId), ("$generation", generation),
            ("$local", localVersion), ("$remote", remoteVersion), ("$lm", localMember), ("$rm", remoteMember));
    }


    // Shared receipt selection for normal publication and read-only successor
    // normalization. Alias/operation expressions are fixed internal SQL only.
    private static string CopyReceiptSelect(string point,bool predecessor,string operation,bool bundle)
    {
        var evidence="""
            json_object('receiptId',r.effect_receipt_id,'attemptId',r.attempt_id,
                'operationId',o.operation_id,'certainty',r.certainty,'source',r.source_identity_json,
                'destination',r.destination_identity_json,'provider',r.provider_metadata_json,
                'digest',r.evidence_digest,'observedUtc',r.observed_utc,
                'leaseId',r.recorded_lease_id,'fencingToken',r.recorded_fencing_token)
            """;
        var values="o.operation_id,t.attempt_id,r.effect_receipt_id,r.destination_identity_json,t.status,"+evidence;
        var projection=bundle?"json_array("+values+")":values;
        var attempt=predecessor?" AND t.status='verified_success'":"";
        var exact=predecessor?"":$" AND o.operation_id={operation}";
        var destination=predecessor?"":$"""
            AND EXISTS(SELECT 1 FROM operation_member d WHERE d.operation_id=o.operation_id
                AND d.role='destination' AND d.expected_path_key={point}.path_key)
            """;
        return $"""
            SELECT {projection}
            FROM operation o JOIN operation_attempt t ON t.operation_id=o.operation_id{attempt}
            JOIN effect_receipt r ON r.attempt_id=t.attempt_id AND r.certainty='known_success'
            JOIN object_version v ON v.object_version_id={point}.local_version_id
            WHERE o.pair_id={point}.pair_id AND o.object_id={point}.object_id{exact}
                AND o.kind='create_local' AND o.status='completed'
                AND EXISTS(SELECT 1 FROM operation_member s WHERE s.operation_id=o.operation_id
                    AND s.role='source' AND s.object_version_id={point}.remote_version_id)
                {destination}
                AND json_extract(r.destination_identity_json,'$.identity')=v.identity_json
                AND json_extract(r.destination_identity_json,'$.path')={point}.path_key
            ORDER BY t.attempt_no DESC,r.observed_utc DESC,r.effect_receipt_id LIMIT 1
            """;
    }

    private static async Task CaptureCurrentCopyCompletionAsync(SqliteConnection connection,
        SqliteTransaction transaction, string operationId, CancellationToken token)
    {
        // Status, exact current point, source, destination and the physical-copy
        // receipt are independently bound inside the completing writer transaction.
        await ExecuteAsync(connection, transaction, $"""
            UPDATE current_copy_completion AS c SET
                (copy_operation_id,attempt_id,receipt_id,destination_identity_json,attempt_status,receipt_evidence_json)=(
                    {CopyReceiptSelect("c",false,"$op",false)})
            WHERE (c.pair_id,c.object_id)=(SELECT pair_id,object_id FROM operation WHERE operation_id=$op
                    AND kind='create_local' AND status='completed')
                AND EXISTS(SELECT 1 FROM synchronization_point p WHERE p.pair_id=c.pair_id AND p.object_id=c.object_id
                    AND p.synchronization_point_id=c.point_id AND p.authority_generation=c.point_generation
                    AND p.local_object_version_id=c.local_version_id AND p.dropbox_object_version_id=c.remote_version_id
                    AND p.local_membership_id=c.local_membership_id AND p.dropbox_membership_id=c.remote_membership_id)
                AND NOT EXISTS(SELECT 1 FROM synchronization_point p WHERE p.pair_id=c.pair_id AND p.object_id=c.object_id
                    AND p.authority_generation>c.point_generation);
            """, token, ("$op", operationId));
    }

    private const string CurrentCopyCandidatesSql = """
        SELECT c.object_id,p.point_id,c.point_id,p.path_key,c.path_key,p.local_version_id,c.local_version_id,
            p.remote_version_id,c.remote_version_id,p.local_membership_id,a.generation,
            c.local_membership_id,b.generation,c.copy_operation_id
        FROM current_copy_completion c
        JOIN copy_predecessor p ON p.pair_id=c.pair_id AND p.object_id=c.object_id
            AND p.point_generation<c.point_generation
        JOIN membership a ON a.membership_id=p.local_membership_id
        JOIN membership b ON b.membership_id=c.local_membership_id
        JOIN membership ar ON ar.membership_id=p.remote_membership_id
        JOIN membership br ON br.membership_id=c.remote_membership_id
        JOIN object_version v ON v.object_version_id=c.local_version_id
        WHERE c.pair_id=$pair AND ($object IS NULL OR c.object_id=$object)
            AND ($unverified=1 OR c.coverage_verified=1)
            AND c.copy_operation_id IS NOT NULL AND c.receipt_id IS NOT NULL
            AND json_extract(c.receipt_evidence_json,'$.receiptId')=c.receipt_id
            AND json_extract(c.receipt_evidence_json,'$.attemptId')=c.attempt_id
            AND json_extract(c.receipt_evidence_json,'$.operationId')=c.copy_operation_id
            AND json_extract(c.receipt_evidence_json,'$.certainty')='known_success'
            AND ($prior IS NULL OR p.point_id=$prior) AND ($current IS NULL OR c.point_id=$current)
            AND ($copy IS NULL OR c.copy_operation_id=$copy)
            AND p.path_key<>c.path_key AND p.local_version_id<>c.local_version_id
            AND p.local_root_id=c.local_root_id AND p.remote_root_id=c.remote_root_id
            AND a.root_binding_id=p.local_root_id AND b.root_binding_id=c.local_root_id
            AND ar.root_binding_id=p.remote_root_id AND br.root_binding_id=c.remote_root_id
            AND a.relative_path_key=p.path_key AND ar.relative_path_key=p.path_key
            AND b.relative_path_key=c.path_key AND br.relative_path_key=c.path_key
            AND json_extract(c.destination_identity_json,'$.identity')=v.identity_json
            AND json_extract(c.destination_identity_json,'$.path')=c.path_key
            AND EXISTS(SELECT 1 FROM synchronization_point s WHERE s.pair_id=c.pair_id AND s.object_id=c.object_id
                AND s.synchronization_point_id=c.point_id AND s.authority_generation=c.point_generation
                AND s.local_object_version_id=c.local_version_id AND s.dropbox_object_version_id=c.remote_version_id
                AND s.local_membership_id=c.local_membership_id AND s.dropbox_membership_id=c.remote_membership_id)
            AND NOT EXISTS(SELECT 1 FROM synchronization_point s WHERE s.pair_id=c.pair_id AND s.object_id=c.object_id
                AND s.authority_generation>c.point_generation)
        ORDER BY p.path_key,c.point_id,p.point_generation DESC;
        """;

    // Exact-object consumers must not walk every predecessor sharing a root
    // or pair just to satisfy the presentation ordering. The primary key bounds
    // this work to that object's still-relevant old memberships.
    internal static string CurrentCopyCandidateQuery(bool exactObject) => !exactObject
        ? CurrentCopyCandidatesSql
        : CurrentCopyCandidatesSql
            .Replace("($object IS NULL OR c.object_id=$object)", "c.object_id=$object", StringComparison.Ordinal)
            .Replace("copy_predecessor p ON",
                "copy_predecessor p INDEXED BY sqlite_autoindex_copy_predecessor_1 ON", StringComparison.Ordinal);

    private static async Task<IReadOnlyList<SyncCompletedCopyMove>> ReadCurrentCopyMovesCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string pairId,
        SyncCompletedCopyMove? expected, CancellationToken token, string? objectId = null, bool unverified = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CurrentCopyCandidateQuery(expected is not null || objectId is not null);
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$object", (object?)expected?.ObjectId ?? objectId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$prior", (object?)expected?.PriorPointId ?? DBNull.Value);
        command.Parameters.AddWithValue("$current", (object?)expected?.CurrentPointId ?? DBNull.Value);
        command.Parameters.AddWithValue("$copy", (object?)expected?.CopyOperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$unverified", unverified ? 1 : 0);
        return await ReadWithNativeCancellationAsync(connection, async () =>
        {
            var result = new List<SyncCompletedCopyMove>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetInt64(10),
                    reader.GetString(11), reader.GetInt64(12), reader.GetString(13)));
            return result;
        }, token, CompletedCopyMoveReadProgressForTest);
    }

    internal async Task<bool> IsNormalizedCompletedCopyMoveAsync(string pairId, SyncCompletedCopyMove proof,
        CancellationToken token)
    {
        await using var connection = await OpenReadOnlyConnectionAsync(token);
        return (await ReadCurrentCopyMovesCoreAsync(connection, null, pairId, proof, token)).Contains(proof);
    }

    // Invoked only by an explicit bounded successor preparation request. Failure
    // restores all compact state in the transaction and leaves legacy reads intact.
    internal async Task<bool> MaterializeCurrentCopyProofAsync(string pairId, string objectId,
        CancellationToken token)
    {
        var covered = false;
        await WriteAsync(async (connection, transaction) =>
        {
            if (await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM current_copy_completion WHERE pair_id=$pair AND object_id=$object AND coverage_verified=1;",
                    token, ("$pair", pairId), ("$object", objectId)) == 1)
            { covered = true; return; }
            var before = await ReadLegacyCompletedCopyMovesCoreAsync(connection, transaction, pairId,
                null, token, objectId, excludeCovered: false, limit: 4097);
            if (before.Count == 0 || before.Count > 4096) return;
            var current = before.Select(x => (x.CurrentPointId, x.CopyOperationId)).Distinct().ToArray();
            if (current.Length != 1 || before.GroupBy(x => x.OldMembershipId).Any(g => g.Distinct().Count() != 1)) return;
            await ExecuteAsync(connection, transaction, "SAVEPOINT copy_materialize;", token);
            await ExecuteAsync(connection, transaction,
                "DELETE FROM copy_predecessor WHERE pair_id=$pair AND object_id=$object; DELETE FROM current_copy_completion WHERE pair_id=$pair AND object_id=$object;",
                token, ("$pair", pairId), ("$object", objectId));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO current_copy_completion(pair_id,object_id,point_id,point_generation,
                    local_version_id,remote_version_id,local_membership_id,remote_membership_id,
                    local_root_id,remote_root_id,path_key,coverage_verified)
                SELECT p.pair_id,p.object_id,p.synchronization_point_id,p.authority_generation,
                    p.local_object_version_id,p.dropbox_object_version_id,p.local_membership_id,p.dropbox_membership_id,
                    a.root_binding_id,b.root_binding_id,a.relative_path_key,0
                FROM synchronization_point p JOIN membership a ON a.membership_id=p.local_membership_id
                JOIN membership b ON b.membership_id=p.dropbox_membership_id
                WHERE p.synchronization_point_id=$point AND p.pair_id=$pair AND p.object_id=$object
                    AND a.relative_path_key=b.relative_path_key;
                """, token, ("$point", current[0].CurrentPointId), ("$pair", pairId), ("$object", objectId));
            await CaptureCurrentCopyCompletionAsync(connection, transaction, current[0].CopyOperationId, token);
            foreach (var proof in before.Distinct())
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO copy_predecessor(pair_id,object_id,point_id,point_generation,
                        local_version_id,remote_version_id,local_membership_id,remote_membership_id,
                        local_root_id,remote_root_id,path_key)
                    SELECT p.pair_id,p.object_id,p.synchronization_point_id,p.authority_generation,
                        p.local_object_version_id,p.dropbox_object_version_id,p.local_membership_id,p.dropbox_membership_id,
                        a.root_binding_id,b.root_binding_id,a.relative_path_key
                    FROM synchronization_point p JOIN membership a ON a.membership_id=p.local_membership_id
                    JOIN membership b ON b.membership_id=p.dropbox_membership_id
                    WHERE p.synchronization_point_id=$prior AND p.pair_id=$pair AND p.object_id=$object
                        AND a.relative_path_key=b.relative_path_key;
                    """, token, ("$prior", proof.PriorPointId), ("$pair", pairId), ("$object", objectId));
            // Preserve the stricter mapping-recovery receipt as well as the ordinary
            // 14-field retirement proof; absence keeps that separate consumer closed.
            await CapturePredecessorReceiptsAsync(connection, transaction, pairId, objectId, token);
            var after = await ReadCurrentCopyMovesCoreAsync(connection, transaction, pairId, null,
                token, objectId, unverified: true);
            if (!before.ToHashSet().SetEquals(after))
            {
                await ExecuteAsync(connection, transaction, "ROLLBACK TO copy_materialize; RELEASE copy_materialize;", token);
                return;
            }
            await ExecuteAsync(connection, transaction,
                "UPDATE current_copy_completion SET coverage_verified=1 WHERE pair_id=$pair AND object_id=$object; RELEASE copy_materialize;",
                token, ("$pair", pairId), ("$object", objectId));
            covered = true;
        }, token);
        return covered;
    }

    private static async Task CapturePredecessorReceiptsAsync(SqliteConnection connection,
        SqliteTransaction transaction, string pairId, string objectId, CancellationToken token)
    {
        await ExecuteAsync(connection, transaction, $"""
            UPDATE copy_predecessor AS p SET
                (copy_operation_id,attempt_id,receipt_id,destination_identity_json,attempt_status,receipt_evidence_json)=(
                    {CopyReceiptSelect("p",true,"NULL",false)})
            WHERE p.pair_id=$pair AND p.object_id=$object;
            """, token, ("$pair", pairId), ("$object", objectId));
    }

    private static async Task<HashSet<(string ObjectId, string PhysicalKey)>> VerifyCurrentCopyObservationSeparationsAsync(
        SqliteConnection connection, SqliteTransaction transaction, SyncAuthorityScanGeneration scan,
        IReadOnlyList<SyncAuthorityObservationInput> entries, IReadOnlySet<string> collisionObjects,
        CancellationToken token)
    {
        var result = new HashSet<(string ObjectId, string PhysicalKey)>();
        if (scan.Side != "local" || collisionObjects.Count == 0) return result;
        var observed = entries.ToDictionary(x => PilotPath.NormalizeRelative(x.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        foreach (var objectId in collisionObjects)
        {
            var candidates = await ReadCurrentCopyMovesCoreAsync(connection, transaction, scan.PairId,
                null, token, objectId);
            foreach (var proof in candidates)
            {
                if (!observed.TryGetValue(proof.OldPath, out var old) ||
                    !observed.TryGetValue(proof.NewPath, out var current) ||
                    old.BoundObjectId is not null || current.BoundObjectId is not null ||
                    old.ObjectKind != "file" || current.ObjectKind != "file" ||
                    old.ContentHash is null || current.ContentHash is null ||
                    old.Size is null || current.Size is null) continue;
                await using var query = connection.CreateCommand();
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT av.identity_json,bv.identity_json,ar.identity_json,br.identity_json,
                        av.content_hash,bv.content_hash,av.size_bytes,bv.size_bytes
                    FROM object_version av JOIN object_version bv ON bv.object_version_id=$new
                    JOIN object_version ar ON ar.object_version_id=$oldRemote
                    JOIN object_version br ON br.object_version_id=$newRemote
                    JOIN membership a ON a.membership_id=$member AND a.root_binding_id=$root
                    JOIN root_binding rb ON rb.root_binding_id=a.root_binding_id AND rb.state='active'
                    WHERE av.object_version_id=$old
                        AND NOT EXISTS(SELECT 1 FROM operation o WHERE
                            (o.object_id=$object OR o.operation_id IN
                                (SELECT m.operation_id FROM operation_member m WHERE m.object_id=$object))
                            AND (o.status NOT IN ('completed','superseded','cancelled')
                                OR EXISTS(SELECT 1 FROM operation_attempt t WHERE t.operation_id=o.operation_id
                                    AND t.status NOT IN ('verified_success','verified_not_applied'))));
                    """;
                foreach (var (name, value) in new (string, object)[] {
                    ("$old", proof.OldVersionId), ("$new", proof.NewVersionId),
                    ("$oldRemote", proof.OldRemoteVersionId), ("$newRemote", proof.NewRemoteVersionId),
                    ("$member", proof.OldMembershipId), ("$root", scan.RootBindingId), ("$object", objectId) })
                    query.Parameters.AddWithValue(name, value);
                await using var row = await query.ExecuteReaderAsync(token);
                if (!await row.ReadAsync(token) || row.IsDBNull(4) || row.IsDBNull(5) ||
                    row.IsDBNull(6) || row.IsDBNull(7)) continue;
                var oldKey = ObservationPhysicalKey("local", row.GetString(0));
                var currentKey = ObservationPhysicalKey("local", row.GetString(1));
                if (oldKey == currentKey ||
                    ObservationPhysicalKey("dropbox", row.GetString(2)) != ObservationPhysicalKey("dropbox", row.GetString(3)) ||
                    ObservationPhysicalKey("local", old.IdentityJson) != oldKey ||
                    ObservationPhysicalKey("local", current.IdentityJson) != currentKey ||
                    !string.Equals(old.ContentHash, row.GetString(4), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.ContentHash, row.GetString(5), StringComparison.OrdinalIgnoreCase) ||
                    old.Size != row.GetInt64(6) || current.Size != row.GetInt64(7)) continue;
                result.Add((objectId, oldKey));
            }
        }
        return result;
    }

    private static async Task RetireAbsentCopyPredecessorsCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncAuthorityScanGeneration scan, CancellationToken token)
    {
        if (scan.Side != "local") return;
        // Selection exclusions are not absence. Missing saved selection or an
        // uncertain/incomplete/root-changed scan keeps the candidate unchanged.
        var pairJson = await ScalarStringAsync(connection, transaction,
            "SELECT pair_json FROM sync_pair WHERE pair_id=$pair AND state<>'retired';",
            token, ("$pair", scan.PairId));
        if (pairJson is null) return;
        var pair = JsonSerializer.Deserialize<SyncPair>(pairJson);
        if (pair is null) return;
        var selection = SyncSelectionPolicy.Compile(pair);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.object_id,p.local_membership_id,p.point_id,p.path_key
            FROM copy_predecessor p
            JOIN current_copy_completion c ON c.pair_id=p.pair_id AND c.object_id=p.object_id AND c.coverage_verified=1
            JOIN synchronization_point current ON current.synchronization_point_id=c.point_id
                AND current.authority_generation=c.point_generation
            JOIN scan_generation s ON s.scan_generation_id=$scan AND s.pair_id=p.pair_id
                AND s.root_binding_id=p.local_root_id AND s.side='local' AND s.generation=$generation
                AND s.status='complete' AND json_extract(s.scope_json,'$.scope')='complete'
                AND julianday(s.started_utc)>julianday(current.accepted_utc)
            JOIN root_binding rb ON rb.root_binding_id=s.root_binding_id AND rb.state='active'
            WHERE p.pair_id=$pair AND p.local_root_id=$root
                AND NOT EXISTS(SELECT 1 FROM scan_generation later WHERE later.root_binding_id=s.root_binding_id
                    AND later.generation>s.generation AND later.status='complete')
                AND NOT EXISTS(SELECT 1 FROM effective_observation o JOIN membership m ON m.membership_id=o.membership_id
                    WHERE o.scan_generation_id=s.scan_generation_id
                        AND (m.membership_id=p.local_membership_id OR m.relative_path_key=p.path_key))
                AND NOT EXISTS(SELECT 1 FROM operation o WHERE
                    (o.object_id=p.object_id OR o.operation_id IN
                        (SELECT m.operation_id FROM operation_member m WHERE m.object_id=p.object_id))
                    AND (o.status NOT IN ('completed','superseded','cancelled')
                        OR EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id
                            AND a.status NOT IN ('verified_success','verified_not_applied'))))
                AND NOT EXISTS(SELECT 1 FROM recovery_artifact a WHERE a.object_id=p.object_id AND a.released_utc IS NULL)
                AND NOT EXISTS(SELECT 1 FROM consent_header h JOIN consent_item i ON i.consent_id=h.consent_id
                    WHERE h.pair_id=p.pair_id AND h.lifecycle NOT IN ('inactive','closed')
                        AND i.lifecycle IN ('eligible','issued'))
                AND NOT EXISTS(SELECT 1 FROM rewind_session w WHERE w.pair_id=p.pair_id)
            ORDER BY p.object_id,p.local_membership_id LIMIT 200;
            """;
        foreach (var (key, value) in new (string, object)[] { ("$scan", scan.ScanGenerationId),
                     ("$pair", scan.PairId), ("$root", scan.RootBindingId), ("$generation", scan.Generation) })
            command.Parameters.AddWithValue(key, value);
        var candidates = new List<(string Object, string Member, string Point, string Path)>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        foreach (var candidate in candidates)
        {
            if (!selection.IsIncluded(candidate.Path, false)) continue;
            await ExecuteAsync(connection, transaction, """
                DELETE FROM copy_predecessor WHERE pair_id=$pair AND object_id=$object
                    AND local_membership_id=$member AND point_id=$point;
                """, token, ("$pair", scan.PairId), ("$object", candidate.Object),
                ("$member", candidate.Member), ("$point", candidate.Point));
        }
    }

    private static async Task RetireCompletedCopyPredecessorAsync(SqliteConnection connection,
        SqliteTransaction transaction, string operationId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT pair_id,preconditions_json FROM operation o WHERE operation_id=$op
                AND kind='delete_local' AND status='completed'
                AND EXISTS(SELECT 1 FROM operation_attempt t JOIN effect_receipt r ON r.attempt_id=t.attempt_id
                    WHERE t.operation_id=o.operation_id AND t.status='verified_success' AND r.certainty='known_success');
            """;
        command.Parameters.AddWithValue("$op", operationId);
        string pair, json;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) return;
            pair = reader.GetString(0); json = reader.GetString(1);
        }
        var proof = SyncAuthorityCompletedCopyMove.Parse(json);
        if (proof is null) return;
        // The completed fenced auxiliary operation's original precondition identifies
        // exactly the removed predecessor. A later current destination does not
        // authorize removing a newer candidate at that old membership.
        await ExecuteAsync(connection, transaction, """
            DELETE FROM copy_predecessor WHERE pair_id=$pair AND object_id=$object
                AND point_id=$prior AND local_membership_id=$member AND local_version_id=$version
                AND remote_version_id=$remote AND path_key=$path;
            """, token, ("$pair", pair), ("$object", proof.ObjectId), ("$prior", proof.PriorPointId),
            ("$member", proof.OldMembershipId), ("$version", proof.OldVersionId),
            ("$remote", proof.OldRemoteVersionId), ("$path", proof.OldPath));
    }
}
