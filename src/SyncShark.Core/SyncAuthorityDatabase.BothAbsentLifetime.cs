using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private sealed record BothAbsentBasis(string DatabaseGeneration, string PairId, string ObjectId,
        string PointId, long AuthorityGeneration, string Path, string LocalRoot, string RemoteRoot, long ConfigurationRevision,
        string DeletionPolicyJson, string LocalClosureScan, string RemoteClosureScan);

    private const string EmptyCurrentCopyProjectionMatches = """
        c.coverage_verified=1 AND c.copy_operation_id IS NULL AND c.attempt_id IS NULL
        AND c.receipt_id IS NULL AND c.destination_identity_json IS NULL
        AND c.attempt_status IS NULL AND c.receipt_evidence_json IS NULL
        AND c.pair_id=$pair AND c.local_root_id=$local AND c.remote_root_id=$remote
        AND c.path_key=$path COLLATE syncshark_absence_path
        AND EXISTS(SELECT 1 FROM synchronization_point p WHERE p.synchronization_point_id=$point
          AND p.synchronization_point_id=c.point_id AND p.pair_id=c.pair_id AND p.object_id=c.object_id
          AND p.authority_generation=c.point_generation AND p.authority_generation=$generation
          AND c.local_version_id IS NOT NULL AND c.remote_version_id IS NOT NULL
          AND c.local_membership_id IS NOT NULL AND c.remote_membership_id IS NOT NULL
          AND p.local_object_version_id=c.local_version_id AND p.dropbox_object_version_id=c.remote_version_id
          AND p.local_membership_id=c.local_membership_id AND p.dropbox_membership_id=c.remote_membership_id)
        """;

    private const string BothAbsentPolicySql = """
        SELECT json_object('deletion_policy',deletion_policy,'deletion_grace_seconds',deletion_grace_seconds,
            'required_absence_observations',required_absence_observations,'conflict_policy',conflict_policy,
            'metadata_policy',metadata_policy,'recursive_mode',recursive_mode,'automatic_pruning',automatic_pruning,
            'recovery_retention_days',recovery_retention_days)
        FROM pair_config_revision WHERE pair_id=$pair AND revision=$revision;
        """;

    // Connection-local comparison agrees with normalized consent path identity,
    // including Unicode and case differences, without changing retained path bytes.
    private static void ConfigureAbsencePathComparison(SqliteConnection connection) =>
        connection.CreateCollation("syncshark_absence_path", (left, right) =>
            StringComparer.Ordinal.Compare(left is null ? null : PilotPath.CanonicalComparisonKey(left),
                right is null ? null : PilotPath.CanonicalComparisonKey(right)));

    internal async Task<bool> BindBothAbsentClosureAsync(string tombstoneId,
        SyncAuthoritySynchronizationPoint point, string localRoot, string remoteRoot,
        string localScan, string remoteScan, string path, long configurationRevision,
        CancellationToken token = default)
    {
        var bound = false;
        await WriteAsync(async (connection, tx) =>
        {
            ConfigureAbsencePathComparison(connection);
            var generation = await ScalarStringAsync(connection, tx,
                "SELECT database_generation FROM installation;", token);
            var policy = await ScalarStringAsync(connection, tx, BothAbsentPolicySql, token,
                ("$pair", point.PairId), ("$revision", configurationRevision));
            if (generation is null || policy is null ||
                !await IsAbsentPathInCurrentScopeAsync(connection,tx,point.PairId,point.ObjectId,path,token) ||
                await ScalarLongAsync(connection, tx, """
                    SELECT count(*) FROM tombstone t JOIN current_synchronization_point p
                      ON p.synchronization_point_id=$point AND p.pair_id=t.pair_id AND p.object_id=t.object_id
                    JOIN sync_pair pair ON pair.pair_id=t.pair_id
                    WHERE t.tombstone_id=$id AND t.pair_id=$pair AND t.object_id=$object
                      AND t.absent_side='both' AND t.status IN ('observed','grace','retained')
                      AND pair.configuration_revision=$revision AND pair.state<>'retired'
                      AND EXISTS(SELECT 1 FROM membership m WHERE m.membership_id=p.local_membership_id
                        AND m.root_binding_id=$localRoot AND m.relative_path_key=$path COLLATE syncshark_absence_path)
                      AND EXISTS(SELECT 1 FROM membership m WHERE m.membership_id=p.dropbox_membership_id
                        AND m.root_binding_id=$remoteRoot AND m.relative_path_key=$path COLLATE syncshark_absence_path);
                    """, token, ("$id", tombstoneId), ("$point", point.SynchronizationPointId),
                    ("$pair", point.PairId), ("$object", point.ObjectId), ("$revision", configurationRevision),
                    ("$localRoot", localRoot), ("$remoteRoot", remoteRoot), ("$path", path)) != 1)
                return;
            foreach (var (root, scan) in new[] { (localRoot, localScan), (remoteRoot, remoteScan) })
            {
                if (await ScalarStringAsync(connection, tx,
                        "SELECT scan_generation_id FROM scan_generation WHERE root_binding_id=$root ORDER BY generation DESC LIMIT 1;",
                        token, ("$root", root)) != scan ||
                    !await IsCompleteAbsentScanCoreAsync(connection, tx, scan, point.PairId, root,
                        point.ObjectId, path, token))
                    return;
            }
            var retained = await ScalarStringAsync(connection, tx,
                "SELECT both_absent_basis_json FROM tombstone WHERE tombstone_id=$id;", token, ("$id", tombstoneId));
            if (retained is not null)
            {
                var old = JsonSerializer.Deserialize<BothAbsentBasis>(retained);
                bound = old is not null && old.DatabaseGeneration == generation &&
                    old.PointId == point.SynchronizationPointId && old.LocalRoot == localRoot &&
                    old.RemoteRoot == remoteRoot && old.ConfigurationRevision == configurationRevision &&
                    old.DeletionPolicyJson == policy && PilotPath.CanonicalComparisonKey(old.Path) ==
                    PilotPath.CanonicalComparisonKey(path);
                return;
            }
            var basis = new BothAbsentBasis(generation, point.PairId, point.ObjectId,
                point.SynchronizationPointId, point.AuthorityGeneration, PilotPath.NormalizeRelative(path), localRoot, remoteRoot,
                configurationRevision, policy, localScan, remoteScan);
            if (!HistoryRetirementScopes.TryGetValue(connection, out var permission))
                throw new InvalidDataException("The exact absence closure scope is missing.");
            permission.Objects.Add(("both_absent_basis", tombstoneId));
            try
            {
                await ExecuteAsync(connection, tx, """
                    UPDATE tombstone SET both_absent_basis_json=$basis,both_absent_closed_utc=$utc
                    WHERE tombstone_id=$id AND both_absent_basis_json IS NULL;
                    """, token, ("$id", tombstoneId), ("$basis", JsonSerializer.Serialize(basis)),
                    ("$utc", Utc(ActivityRetentionNow)));
                bound = true;
            }
            finally { permission.Objects.Remove(("both_absent_basis", tombstoneId)); }
        }, token);
        return bound;
    }

    private static async Task<bool> IsCompleteAbsentScanCoreAsync(SqliteConnection connection,
        SqliteTransaction tx, string scan, string pair, string root, string objectId, string path,
        CancellationToken token) =>
        await ScalarLongAsync(connection, tx, """
            SELECT count(*) FROM scan_generation g JOIN root_binding r ON r.root_binding_id=g.root_binding_id
            WHERE g.scan_generation_id=$scan AND g.pair_id=$pair AND g.root_binding_id=$root
              AND r.pair_id=$pair AND r.state='active' AND g.status='complete' AND g.lifetime_retiring=0
              AND g.completed_utc IS NOT NULL AND json_extract(g.scope_json,'$.scope')='complete'
              AND NOT EXISTS(SELECT 1 FROM effective_observation o
                LEFT JOIN membership m ON m.membership_id=o.membership_id
                WHERE o.scan_generation_id=g.scan_generation_id AND
                  (m.membership_id IS NULL OR m.object_id=$object
                    OR m.relative_path_key=$path COLLATE syncshark_absence_path
                    OR substr(m.relative_path_key,1,length($path)+1)=($path || '/') COLLATE syncshark_absence_path));
            """, token, ("$scan", scan), ("$pair", pair), ("$root", root),
            ("$object", objectId), ("$path", path)) == 1;

    internal async Task<string> LoadBothAbsentRetirementHoldAsync(string tombstoneId,
        CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token);
        ConfigureAbsencePathComparison(connection);
        await using var tx = connection.BeginTransaction(deferred: true);
        return await BothAbsentRetirementHoldCoreAsync(connection, tx, tombstoneId, token);
    }

    // Empty means the current proof permits collection. It is not cached authority:
    // a collector must call this inside its own writer transaction before each batch.
    private async Task<string> BothAbsentRetirementHoldCoreAsync(SqliteConnection connection,
        SqliteTransaction tx, string tombstoneId, CancellationToken token)
    {
        string? json = null, closed = null;
        await using (var q = connection.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT both_absent_basis_json,both_absent_closed_utc FROM tombstone WHERE tombstone_id=$id AND absent_side='both' AND status IN ('observed','grace','retained');";
            q.Parameters.AddWithValue("$id", tombstoneId);
            await using var rows = await q.ExecuteReaderAsync(token);
            if (await rows.ReadAsync(token))
            { json = rows.IsDBNull(0) ? null : rows.GetString(0); closed = rows.IsDBNull(1) ? null : rows.GetString(1); }
        }
        if (json is null || closed is null) return "unproved_legacy_absence";
        var basis = JsonSerializer.Deserialize<BothAbsentBasis>(json);
        if (basis is null || !DateTimeOffset.TryParse(closed, out var since)) return "invalid_absence_basis";
        if (ActivityRetentionNow < since.AddDays(90)) return "ninety_day_grace";
        if (basis.DatabaseGeneration != await ScalarStringAsync(connection, tx,
                "SELECT database_generation FROM installation;", token)) return "different_database_generation";
        var policy = await ScalarStringAsync(connection, tx, BothAbsentPolicySql, token,
            ("$pair", basis.PairId), ("$revision", basis.ConfigurationRevision));
        if (basis.DeletionPolicyJson != policy) return "original_policy_unavailable_or_changed";
        if (await ScalarLongAsync(connection, tx, """
                SELECT count(*) FROM sync_pair WHERE pair_id=$pair AND state<>'retired' AND configuration_revision=$revision;
                """, token, ("$pair", basis.PairId), ("$revision", basis.ConfigurationRevision)) != 1)
            return "pair_configuration_changed";
        if(!await IsAbsentPathInCurrentScopeAsync(connection,tx,basis.PairId,basis.ObjectId,basis.Path,token))
            return "outside_current_selection";
        foreach (var root in new[] { basis.LocalRoot, basis.RemoteRoot })
        {
            var scans = new List<(string Id, string Started)>(2);
            await using (var q = connection.CreateCommand())
            {
                q.Transaction = tx;
                // Deliberately includes failed and incomplete generations.
                q.CommandText = "SELECT scan_generation_id,started_utc FROM scan_generation WHERE root_binding_id=$root ORDER BY generation DESC LIMIT 2;";
                q.Parameters.AddWithValue("$root", root);
                await using var rows = await q.ExecuteReaderAsync(token);
                while (await rows.ReadAsync(token)) scans.Add((rows.GetString(0), rows.GetString(1)));
            }
            if (scans.Count != 2) return "two_fresh_complete_observations_required";
            foreach (var scan in scans)
                if (!DateTimeOffset.TryParse(scan.Started, out var started) || started <= since ||
                    !await IsCompleteAbsentScanCoreAsync(connection, tx, scan.Id, basis.PairId,
                        root, basis.ObjectId, basis.Path, token))
                    return "two_fresh_complete_observations_required";
        }
        // These are exact object/path consumers. A sibling consent choice does not
        // pin this path, while unknown legacy selection remains an explicit hold.
        foreach (var (reason, sql) in new (string, string)[]
        {
            ("retained_operation", """
                SELECT EXISTS(SELECT 1 FROM operation WHERE object_id=$object)
                    OR EXISTS(SELECT 1 FROM operation_member WHERE object_id=$object);
                """),
            ("retained_conflict", "SELECT EXISTS(SELECT 1 FROM conflict WHERE object_id=$object);"),
            ("retained_recovery_artifact", "SELECT EXISTS(SELECT 1 FROM recovery_artifact WHERE object_id=$object);"),
            ("retained_copy_recovery",
                "SELECT EXISTS(SELECT 1 FROM current_copy_completion c WHERE c.object_id=$object AND NOT ("+
                EmptyCurrentCopyProjectionMatches+")) OR EXISTS(SELECT 1 FROM copy_predecessor WHERE object_id=$object);"),
            ("pending_dirty_work", "SELECT EXISTS(SELECT 1 FROM dirty_item WHERE object_id=$object AND status<>'closed');"),
            ("retained_original_choice", """
                SELECT EXISTS(SELECT 1 FROM consent_item i JOIN consent_header h ON h.consent_id=i.consent_id
                    WHERE h.pair_id=$pair AND i.relative_path_key=$key);
                """),
            ("retained_legacy_selection", """
                SELECT EXISTS(SELECT 1 FROM command_request c WHERE c.pair_id=$pair
                    AND json_type(c.payload_json,'$.reviewedPlan') IS NOT NULL
                    AND (c.admission_generation IS NULL OR c.ordinary_issuance_closed_utc IS NULL));
                """),
            ("active_rewind", "SELECT EXISTS(SELECT 1 FROM rewind_session WHERE pair_id=$pair AND status<>'resumed');"),
            ("another_pair_baseline", "SELECT EXISTS(SELECT 1 FROM synchronization_point WHERE object_id=$object AND pair_id<>$pair);"),
            ("newer_accepted_baseline", """
                SELECT EXISTS(SELECT 1 FROM synchronization_point WHERE object_id=$object AND pair_id=$pair
                    AND authority_generation>$generation);
                """)
        })
        {
            if (await ScalarLongAsync(connection, tx, sql, token, ("$object", basis.ObjectId),
                    ("$pair", basis.PairId), ("$key", PilotPath.CanonicalComparisonKey(basis.Path)),
                    ("$generation", basis.AuthorityGeneration), ("$point", basis.PointId),
                    ("$local", basis.LocalRoot), ("$remote", basis.RemoteRoot), ("$path", basis.Path)) != 0) return reason;
        }
        return "";
    }

    private sealed record AbsentRetirementRow(string Table, string Key, string Id, long Bytes);

    private static string AbsentEncodedBytes(params string[] columns) =>
        string.Join("+", columns.Select(column => $"coalesce(length(CAST({column} AS BLOB)),0)")) + "+128";

    private static async Task<AbsentRetirementRow?> ReadAbsentRetirementRowAsync(SqliteConnection connection,
        SqliteTransaction tx, string table, string key, string encoded, string predicate, string order,
        string objectId, string tombstoneId, CancellationToken token)
    {
        await using var q = connection.CreateCommand(); q.Transaction = tx;
        q.CommandText = $"SELECT {key},{encoded} FROM {table} t WHERE {predicate} ORDER BY {order} LIMIT 1;";
        q.Parameters.AddWithValue("$object", objectId); q.Parameters.AddWithValue("$tombstone", tombstoneId);
        await using var rows = await q.ExecuteReaderAsync(token);
        return await rows.ReadAsync(token) ? new(table, key, rows.GetString(0), rows.GetInt64(1)) : null;
    }

    private static string AbsentNoDetailReferences(SyncObjectLifetimeSchema.Target target,
        bool exceptOwnTombstone) => string.Join(" AND ", target.References.Select(reference =>
            $"NOT EXISTS(SELECT 1 FROM {reference.Table} r WHERE r.{reference.Column}=t.{target.Key}" +
            (exceptOwnTombstone && reference.Table == "tombstone" ? " AND r.tombstone_id<>$tombstone" : "") + ")"));

    private async Task<SyncObjectRetirementResult> RetireBothAbsentDetailsCoreAsync(SqliteConnection connection,
        SqliteTransaction tx, HistoryRetirementScope scope, System.Diagnostics.Stopwatch clock,
        long previousBytes, CancellationToken token)
    {
        ConfigureAbsencePathComparison(connection);
        var id = await ScalarStringAsync(connection, tx, """
            SELECT tombstone_id FROM tombstone
            WHERE absent_side='both' AND both_absent_basis_json IS NOT NULL
              AND tombstone_id>(SELECT both_absent_frontier_id FROM installation)
            ORDER BY tombstone_id LIMIT 1;
            """, token);
        if (id is null)
        {
            await ExecuteAsync(connection, tx, "UPDATE installation SET both_absent_frontier_id='';", token);
            return new(0, 0, 0);
        }
        await ExecuteAsync(connection, tx,
            "UPDATE installation SET both_absent_frontier_id=$id;", token, ("$id", id));
        var hold = await BothAbsentRetirementHoldCoreAsync(connection, tx, id, token);
        var examined = 1; var retired = 0; long bytes = 0;
        if (hold.Length != 0) return await Held(hold);
        var json = await ScalarStringAsync(connection, tx,
            "SELECT both_absent_basis_json FROM tombstone WHERE tombstone_id=$id;", token, ("$id", id));
        var basis = JsonSerializer.Deserialize<BothAbsentBasis>(json!)!;
        if (await ScalarLongAsync(connection, tx, """
                SELECT EXISTS(SELECT 1 FROM tombstone WHERE object_id=$object AND tombstone_id<>$id)
                  OR EXISTS(SELECT 1 FROM membership WHERE object_id=$object AND root_binding_id NOT IN ($local,$remote));
                """, token, ("$object", basis.ObjectId), ("$id", id),
                ("$local", basis.LocalRoot), ("$remote", basis.RemoteRoot)) != 0)
            return await Held("another_root_or_absence_episode");

        // Delete old decisions first, then accepted points oldest-first. The last
        // baseline is never removed while an older point of that object remains.
        for (var attempt = 0; examined < 20 && (attempt == 0 || clock.ElapsedMilliseconds < 45); attempt++)
        {
            // A verified empty projection contains no completed-copy candidate or receipt.
            // Full tuple equality was rechecked above; retire it before its accepted point.
            var row = await ReadAbsentRetirementRowAsync(connection, tx, "current_copy_completion",
                "object_id", AbsentEncodedBytes((CopySnapshotColumns+",coverage_verified").Split(',')),
                "t.object_id=$object", "t.object_id", basis.ObjectId, id, token);
            row ??= await ReadAbsentRetirementRowAsync(connection, tx, "reconciliation_decision",
                "reconciliation_decision_id", AbsentEncodedBytes("reconciliation_decision_id","dirty_item_id",
                    "pair_id","object_id","local_scan_generation_id","dropbox_scan_generation_id",
                    "synchronization_point_id","decision_kind","decision_json","decided_utc"),
                "t.object_id=$object", "t.reconciliation_decision_id", basis.ObjectId, id, token);
            if (row is null)
            {
                row = await ReadAbsentRetirementRowAsync(connection, tx, "synchronization_point",
                    "synchronization_point_id", AbsentEncodedBytes("synchronization_point_id","pair_id","object_id",
                        "local_object_version_id","dropbox_object_version_id","local_membership_id",
                        "dropbox_membership_id","accepted_utc","authority_generation"),
                    "t.object_id=$object", "t.authority_generation,t.synchronization_point_id", basis.ObjectId, id, token);
                if (row is not null && await ScalarLongAsync(connection, tx, """
                        SELECT EXISTS(
                         SELECT 1 FROM synchronization_point p
                         CROSS JOIN membership pl CROSS JOIN membership pd
                         WHERE p.synchronization_point_id=$point
                           AND pl.membership_id=p.local_membership_id AND pd.membership_id=p.dropbox_membership_id
                           AND (EXISTS(
                             SELECT 1 FROM membership ol
                             CROSS JOIN synchronization_point old INDEXED BY membership_lifetime_synchronization_point_local_membership_id_ref
                             LEFT JOIN membership od ON od.membership_id=old.dropbox_membership_id
                             WHERE ol.root_binding_id=pl.root_binding_id AND ol.relative_path_key=pl.relative_path_key
                               AND ol.generation<pl.generation AND old.local_membership_id=ol.membership_id
                               AND old.pair_id=p.pair_id AND old.object_id<>p.object_id
                               AND (od.membership_id IS NULL OR (od.root_binding_id=pd.root_binding_id
                                 AND od.relative_path_key=pd.relative_path_key AND od.generation<pd.generation)))
                           OR EXISTS(
                             SELECT 1 FROM membership od
                             CROSS JOIN synchronization_point old INDEXED BY membership_lifetime_synchronization_point_dropbox_membership_id_ref
                             WHERE od.root_binding_id=pd.root_binding_id AND od.relative_path_key=pd.relative_path_key
                               AND od.generation<pd.generation AND old.dropbox_membership_id=od.membership_id
                               AND old.local_membership_id IS NULL AND old.pair_id=p.pair_id AND old.object_id<>p.object_id)));
                        """, token, ("$point", row.Id)) != 0)
                    return await Held("accepted_point_protects_older_baseline");
            }
            if (row is null)
                foreach (var target in SyncObjectLifetimeSchema.Targets)
                {
                    row = await ReadAbsentRetirementRowAsync(connection, tx, target.Table, target.Key,
                        AbsentEncodedBytes(target.Scalars), "t.object_id=$object AND " +
                        AbsentNoDetailReferences(target, false), "t." + target.Key, basis.ObjectId, id, token);
                    if (row is not null) break;
                }
            if (row is null) break;
            examined++;
            if (row.Bytes > 1_048_576 - previousBytes - bytes) return await Held("metadata_payload_budget");
            await Delete(row);
        }

        // The final small component is removed atomically: tombstone, its last
        // directly pinned details and logical object. Observation/interval and
        // closed-dirty metadata can drain in their existing slots first.
        if (examined < 20)
        {
            var final = new List<AbsentRetirementRow>();
            foreach (var target in SyncObjectLifetimeSchema.Targets)
            {
                await using var q = connection.CreateCommand(); q.Transaction = tx;
                q.CommandText = $"SELECT t.{target.Key},{AbsentEncodedBytes(target.Scalars)}," +
                    $"CASE WHEN {AbsentNoDetailReferences(target, true)} THEN 1 ELSE 0 END " +
                    $"FROM {target.Table} t WHERE t.object_id=$object ORDER BY t.{target.Key} LIMIT 21;";
                q.Parameters.AddWithValue("$object", basis.ObjectId); q.Parameters.AddWithValue("$tombstone", id);
                await using var rows = await q.ExecuteReaderAsync(token);
                while (await rows.ReadAsync(token))
                {
                    if (rows.GetInt64(2) == 0) return await Held("retained_inventory_or_detail_reference");
                    final.Add(new(target.Table, target.Key, rows.GetString(0), rows.GetInt64(1)));
                }
            }
            if (await ScalarLongAsync(connection, tx, """
                    SELECT EXISTS(SELECT 1 FROM synchronization_point WHERE object_id=$object)
                      OR EXISTS(SELECT 1 FROM reconciliation_decision WHERE object_id=$object)
                      OR EXISTS(SELECT 1 FROM dirty_item WHERE object_id=$object)
                      OR EXISTS(SELECT 1 FROM tombstone_observation WHERE tombstone_id=$id);
                    """, token, ("$object", basis.ObjectId), ("$id", id)) != 0)
                return await Held("retained_point_or_auxiliary_reference");
            var tombstone = await ReadAbsentRetirementRowAsync(connection, tx, "tombstone", "tombstone_id",
                AbsentEncodedBytes("tombstone_id","pair_id","object_id","absent_side","prior_membership_id",
                    "prior_object_version_id","observed_absent_utc","complete_observation_count","status",
                    "both_absent_basis_json","both_absent_closed_utc"),
                "t.tombstone_id=$tombstone", "t.tombstone_id", basis.ObjectId, id, token);
            var logical = await ReadAbsentRetirementRowAsync(connection, tx, "logical_object", "object_id",
                AbsentEncodedBytes("object_id","object_kind","created_utc","lifetime_reference_epoch"),
                "t.object_id=$object", "t.object_id", basis.ObjectId, id, token);
            if (tombstone is null || logical is null) throw new InvalidDataException("The closed absence component changed.");
            final.Insert(0, tombstone); final.Add(logical);
            if (final.Count + examined > 20) return await Held("metadata_row_budget");
            if (final.Sum(item => item.Bytes) > 1_048_576 - previousBytes - bytes)
                return await Held("metadata_payload_budget");
            foreach (var row in final) { examined++; await Delete(row); }
            await ExecuteAsync(connection, tx, """
                UPDATE installation SET both_absent_expired_objects=both_absent_expired_objects+1;
                """, token);
        }
        await ExecuteAsync(connection, tx, "UPDATE installation SET both_absent_last_hold=NULL;", token);
        return new(examined, retired, bytes);

        async Task Delete(AbsentRetirementRow row)
        {
            scope.Objects.Add((row.Table, row.Id));
            try
            {
                ActivityRetirementBoundaryForTest?.Invoke("exact-both-absent-before-delete");
                if (await ExecuteCountAsync(connection, tx,
                        $"DELETE FROM {row.Table} WHERE {row.Key}=$id;", token, ("$id", row.Id)) != 1)
                    throw new InvalidDataException("The exact closed absence row did not retire once.");
                retired++; bytes += row.Bytes;
                await ExecuteAsync(connection, tx, """
                    UPDATE installation SET both_absent_expired_rows=both_absent_expired_rows+1;
                    """, token);
            }
            finally { scope.Objects.Remove((row.Table, row.Id)); }
        }
        async Task<SyncObjectRetirementResult> Held(string reason)
        {
            await ExecuteAsync(connection, tx,
                "UPDATE installation SET both_absent_last_hold=$reason;", token, ("$reason", reason));
            return new(examined, retired, bytes);
        }
    }

    private static async Task<bool> IsAbsentPathInCurrentScopeAsync(SqliteConnection connection,
        SqliteTransaction tx, string pairId, string objectId, string path, CancellationToken token)
    {
        await using var q=connection.CreateCommand(); q.Transaction=tx;
        q.CommandText="""
            SELECT p.pair_json,o.object_kind FROM sync_pair p CROSS JOIN logical_object o
            WHERE p.pair_id=$pair AND p.state<>'retired' AND o.object_id=$object;
            """;
        q.Parameters.AddWithValue("$pair",pairId); q.Parameters.AddWithValue("$object",objectId);
        await using var rows=await q.ExecuteReaderAsync(token);
        if(!await rows.ReadAsync(token)||rows.IsDBNull(0))return false;
        var pair=JsonSerializer.Deserialize<SyncPair>(rows.GetString(0));
        return pair is not null && SyncSelectionPolicy.Compile(pair).IsIncluded(path,rows.GetString(1)=="directory");
    }
}
