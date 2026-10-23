using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    internal async Task VerifyIdentityRecoveryInventoriesAsync(SyncAuthorityScanGeneration local,
        SyncAuthorityScanGeneration remote, IReadOnlyList<SyncAuthorityObservationInput> localEntries,
        IReadOnlyList<SyncAuthorityObservationInput> remoteEntries, IReadOnlyList<SyncCompletedCopyMove> recovery,
        CancellationToken token)
    {
        if (local.PairId != remote.PairId || local.Side != "local" || remote.Side != "dropbox")
            throw new InvalidDataException("Identity recovery requires both exact endpoint inventories for one pair.");
        await using var connection = await OpenReadOnlyConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await ReadWithNativeCancellationAsync(connection, async () =>
        {
            await ValidateIdentityRecoveryAsync(connection, transaction, local, localEntries, recovery, token);
            await ValidateIdentityRecoveryAsync(connection, transaction, remote, remoteEntries, recovery, token);
            return true;
        }, token);
    }

    internal async Task PublishIdentityRecoveryAsync(SyncAuthorityScanGeneration local,
        SyncAuthorityScanGeneration remote, IReadOnlyList<SyncAuthorityObservationInput> localEntries,
        IReadOnlyList<SyncAuthorityObservationInput> remoteEntries, IReadOnlyList<SyncCompletedCopyMove> proofs,
        long configurationRevision, string commandId, string completionStatus, string resultJson, CancellationToken token)
    {
        if (local.PairId != remote.PairId || local.Side != "local" || remote.Side != "dropbox" || proofs.Count == 0)
            throw new InvalidDataException("Identity recovery requires both exact inventories and the complete proposed mapping.");
        RequireJson(resultJson, nameof(resultJson));
        if (completionStatus is not ("completed" or "blocked")) throw new ArgumentOutOfRangeException(nameof(completionStatus));
        var resultHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(resultJson)));
        await WriteAsync(async (connection, transaction) =>
        {
            var replay = await ScalarLongAsync(connection, transaction, """
                SELECT count(*) FROM command_request c WHERE c.command_request_id=$command AND c.pair_id=$pair
                  AND c.status=$status AND json_extract(c.result_json,'$.identityRecoveryPublication.resultHash')=$hash
                  AND json_extract(c.result_json,'$.identityRecoveryPublication.localScan')=$local
                  AND json_extract(c.result_json,'$.identityRecoveryPublication.remoteScan')=$remote
                  AND (SELECT count(*) FROM scan_generation s WHERE s.pair_id=$pair AND s.status='complete'
                    AND s.scan_generation_id IN ($local,$remote))=2;
                """, token, ("$command", commandId), ("$pair", local.PairId), ("$status", completionStatus),
                ("$hash", resultHash), ("$local", local.ScanGenerationId), ("$remote", remote.ScanGenerationId));
            if (replay == 1) return;
            var current = await ScalarLongAsync(connection, transaction, """
                SELECT count(*) FROM scan_generation s JOIN root_binding r ON r.root_binding_id=s.root_binding_id
                JOIN sync_pair p ON p.pair_id=s.pair_id
                WHERE s.pair_id=$pair AND s.status='incomplete' AND r.state='active'
                  AND p.state<>'retired' AND p.configuration_revision=$revision
                  AND ((s.scan_generation_id=$local AND s.side='local' AND s.root_binding_id=$localRoot AND s.generation=$localGeneration)
                    OR (s.scan_generation_id=$remote AND s.side='dropbox' AND s.root_binding_id=$remoteRoot AND s.generation=$remoteGeneration))
                  AND s.generation=(SELECT max(g.generation) FROM scan_generation g WHERE g.pair_id=s.pair_id AND g.side=s.side)
                  AND EXISTS(SELECT 1 FROM command_request c WHERE c.command_request_id=$command
                    AND c.pair_id=$pair AND c.status='executing' AND c.command_kind='run_pair');
                """, token, ("$pair", local.PairId), ("$revision", configurationRevision), ("$command", commandId),
                ("$local", local.ScanGenerationId), ("$localRoot", local.RootBindingId), ("$localGeneration", local.Generation),
                ("$remote", remote.ScanGenerationId), ("$remoteRoot", remote.RootBindingId), ("$remoteGeneration", remote.Generation));
            if (current != 2) throw new InvalidDataException("Recovery command, configuration, roots or current inventory generations changed; neither inventory was published.");
            await ValidateIdentityRecoveryAsync(connection, transaction, local, localEntries, proofs, token);
            await ValidateIdentityRecoveryAsync(connection, transaction, remote, remoteEntries, proofs, token);
            var mappings = new List<object>();
            foreach (var proof in proofs)
                foreach (var path in new[] { proof.OldPath, proof.NewPath })
                {
                    var same = await ScalarLongAsync(connection, transaction, """
                        SELECT count(*) FROM effective_observation a JOIN membership am ON am.membership_id=a.membership_id
                        JOIN object_version av ON av.object_version_id=a.object_version_id
                        JOIN effective_observation b ON b.scan_generation_id=$remote
                        JOIN membership bm ON bm.membership_id=b.membership_id
                        JOIN object_version bv ON bv.object_version_id=b.object_version_id
                        WHERE a.scan_generation_id=$local AND am.relative_path_key=$path AND bm.relative_path_key=$path
                          AND av.object_id=bv.object_id
                          AND (($path=$old AND av.object_id<>$object) OR ($path=$new AND av.object_id=$object));
                        """, token, ("$local", local.ScanGenerationId), ("$remote", remote.ScanGenerationId),
                        ("$path", path), ("$old", proof.OldPath), ("$new", proof.NewPath), ("$object", proof.ObjectId));
                    if (same != 1) throw new InvalidDataException("The corrected endpoint inventories do not contain one coherent identity mapping; neither was published.");
                    var mappedObject = await ScalarStringAsync(connection, transaction, """
                        SELECT v.object_id FROM effective_observation o JOIN object_version v ON v.object_version_id=o.object_version_id
                        JOIN membership m ON m.membership_id=o.membership_id
                        WHERE o.scan_generation_id=$scan AND m.relative_path_key=$path;
                        """, token, ("$scan", local.ScanGenerationId), ("$path", path));
                    mappings.Add(new { path, objectId = mappedObject,
                        localIdentity = localEntries.Single(e => string.Equals(PilotPath.NormalizeRelative(e.RelativePath), path, StringComparison.OrdinalIgnoreCase)).IdentityJson,
                        remoteIdentity = remoteEntries.Single(e => string.Equals(PilotPath.NormalizeRelative(e.RelativePath), path, StringComparison.OrdinalIgnoreCase)).IdentityJson });
                }
            var retained = JsonNode.Parse(resultJson)!.AsObject();
            retained["identityRecoveryPublication"] = JsonSerializer.SerializeToNode(new
            {
                resultHash, localScan = local.ScanGenerationId, remoteScan = remote.ScanGenerationId,
                localGeneration = local.Generation, remoteGeneration = remote.Generation,
                configurationRevision, mappings
            });
            await CompleteScanGenerationCoreAsync(connection, transaction, local, token);
            await CompleteScanGenerationCoreAsync(connection, transaction, remote, token);
            await TransitionCommandRequestCoreAsync(connection, transaction, commandId, completionStatus,
                retained.ToJsonString(), token);
        }, token);
    }

    // One indexed read per inventory, not a JSON scan of the previous inventory for every file.
    // Paths join the two sides on first observation; a moved physical object retains its identity,
    // while a replacement at its former path receives a separate, deterministic incarnation.
    private static async Task<Dictionary<string, string>> ResolveObservationBatchAsync(
        SqliteConnection connection, SqliteTransaction transaction, SyncAuthorityScanGeneration scan,
        IReadOnlyList<SyncAuthorityObservationInput> entries, CloudScanRoot? cloudRoot,
        CancellationToken token, IReadOnlyList<SyncCompletedCopyMove>? identityRecovery = null)
    {
        var recovered = identityRecovery is null
            ? new HashSet<(string ObjectId, string PhysicalKey)>()
            : await ValidateIdentityRecoveryAsync(connection, transaction, scan, entries, identityRecovery, token);
        var known = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var priorKeysByObject = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var priorObjectsByPath = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var acceptedIdentities = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var occupied = new Dictionary<string, string>(StringComparer.Ordinal);
        var occupiedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                WITH latest_points AS (
                    SELECT object_id,max(authority_generation) generation FROM synchronization_point
                    WHERE pair_id=$pair GROUP BY object_id
                ), candidates AS (
                    SELECT v.object_id,v.identity_json,m.relative_path_key path,
                           CASE WHEN o.scan_generation_id=$scan THEN 1 ELSE 0 END current_scan,
                           0 accepted_baseline
                    FROM effective_observation o JOIN object_version v ON v.object_version_id=o.object_version_id
                    JOIN membership m ON m.membership_id=o.membership_id
                    WHERE o.scan_generation_id IN ($scan,(
                        SELECT scan_generation_id FROM scan_generation
                        WHERE pair_id=$pair AND side=$side AND status='complete'
                        ORDER BY generation DESC LIMIT 1))
                    UNION
                    SELECT v.object_id,v.identity_json,NULL,0,1
                    FROM latest_points latest JOIN synchronization_point p
                      ON p.pair_id=$pair AND p.object_id=latest.object_id AND p.authority_generation=latest.generation
                    JOIN object_version v ON v.object_version_id=CASE WHEN $side='local'
                        THEN p.local_object_version_id ELSE p.dropbox_object_version_id END
                ) SELECT object_id,identity_json,path,current_scan,accepted_baseline FROM candidates;
                """;
            command.Parameters.AddWithValue("$pair", scan.PairId);
            command.Parameters.AddWithValue("$side", scan.Side);
            command.Parameters.AddWithValue("$scan", scan.ScanGenerationId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var key = ObservationPhysicalKey(scan.Side, reader.GetString(1));
                if (!known.TryGetValue(key, out var ids)) known.Add(key, ids = new(StringComparer.Ordinal));
                ids.Add(reader.GetString(0));
                if (reader.GetInt64(4) == 1)
                {
                    if (!acceptedIdentities.TryGetValue(reader.GetString(0), out var accepted))
                        acceptedIdentities.Add(reader.GetString(0), accepted = new(StringComparer.Ordinal));
                    accepted.Add(key);
                }
                if (reader.GetInt64(3) == 0 && !reader.IsDBNull(2))
                {
                    if (!priorKeysByObject.TryGetValue(reader.GetString(0), out var physicalKeys))
                        priorKeysByObject.Add(reader.GetString(0), physicalKeys = new(StringComparer.Ordinal));
                    physicalKeys.Add(key);
                    var path = reader.GetString(2);
                    if (!priorObjectsByPath.TryGetValue(path, out var pathObjects))
                        priorObjectsByPath.Add(path, pathObjects = new(StringComparer.Ordinal));
                    pathObjects.Add(reader.GetString(0));
                }
                // Only entries already appended to THIS incomplete scan reserve a current path below.
                // Old paths cannot prevent a normal atomic-save replacement from keeping its identity.
                if (reader.GetInt64(3) == 1)
                {
                    occupied[reader.GetString(0)] = reader.GetString(2);
                    occupiedKeys[reader.GetString(0)] = key;
                }
            }
        }

        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var separatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var path = PilotPath.NormalizeRelative(entry.RelativePath);
            if (!keys.TryAdd(path, ValidateObservationPhysicalKey(scan.Side, entry.IdentityJson, cloudRoot)))
                throw new InvalidDataException($"The inventory contains the same path more than once: {path}.");
            if (entry.BoundObjectId is not null) byPath.Add(path, entry.BoundObjectId);
            else if (known.TryGetValue(keys[path], out var ids))
            {
                if (ids.Count != 1)
                    throw new InvalidDataException($"The physical identity of {path} has conflicting retained logical identities.");
                byPath.Add(path, ids.Single());
            }
        }

        // Read the historical origin of all potential collisions together. Looking
        // through the retained inventory history once per collided file made the
        // real Bridge inventory take minutes despite the batched metadata writes.
        var collisionObjects = byPath.Where(item =>
                priorKeysByObject.TryGetValue(item.Value, out var priorKeys) && priorKeys.Count > 1 ||
                occupied.TryGetValue(item.Value, out var occupiedPath) &&
                !string.Equals(occupiedPath, item.Key, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
        collisionObjects.UnionWith(byPath.GroupBy(item => item.Value, StringComparer.Ordinal)
            .Where(group => group.Select(item => keys[item.Key]).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key));
        // A completed copy of the SAME moved provider file can leave its old
        // local incarnation present. Its verified compact proof permits separating
        // only that observed old incarnation; no baseline/history or effect is
        // changed. Distinct provider files retain explicit recovery admission.
        recovered.UnionWith(await VerifyCurrentCopyObservationSeparationsAsync(connection, transaction,
            scan, entries, collisionObjects, token));
        var originalIdentities = await EarliestObservationIdentitiesAsync(connection, transaction,
            scan, collisionObjects, token);
        // A completed copy may already bind the replacement to this logical object.
        // Keep the accepted baseline's physical identity, not an older observation-only
        // incarnation that was archived elsewhere. Every other identity still passes
        // the unchanged effect-authority guard below; no history or baseline is rebound.
        foreach (var objectId in collisionObjects)
        {
            if (!acceptedIdentities.TryGetValue(objectId, out var accepted)) continue;
            if (accepted.Count != 1)
                throw new InvalidDataException("The retained object has conflicting accepted physical identities; no inventory repair was published.");
            originalIdentities[objectId] = accepted.Single();
        }

        // A legacy collision may straddle database batches: seeing the replacement
        // first must not reserve the moved original's identity. Use the complete
        // prior inventory, not only the current 128-row chunk, to recognize it.
        foreach (var item in byPath.ToArray())
        {
            if (!priorKeysByObject.TryGetValue(item.Value, out var priorKeys) || priorKeys.Count < 2) continue;
            var original = originalIdentities.GetValueOrDefault(item.Value);
            if (original is null || keys[item.Key] == original) continue;
            if (entries.Single(entry => string.Equals(PilotPath.NormalizeRelative(entry.RelativePath),
                    item.Key, StringComparison.OrdinalIgnoreCase)).BoundObjectId is not null ||
                await HasIdentityEffectAuthorityAsync(connection, transaction, scan, item.Value, keys[item.Key], token) &&
                !recovered.Contains((item.Value, keys[item.Key])))
                throw IdentityCollisionFailure(item.Key);
            byPath.Remove(item.Key);
            separatedPaths.Add(item.Key);
        }

        // Earlier releases could conflate a moved file and its replacement. Disentangle only
        // observation-only authority, preserving every old row. Any effect/baseline on the
        // replacement identity requires explicit recovery rather than rewriting that authority.
        foreach (var group in byPath.GroupBy(item => item.Value, StringComparer.Ordinal).ToArray())
        {
            if (group.Select(item => keys[item.Key]).Distinct(StringComparer.Ordinal).Count() < 2) continue;
            var original = originalIdentities.GetValueOrDefault(group.Key);
            var originalPaths = group.Where(item => keys[item.Key] == original).ToArray();
            if (originalPaths.Length != 1)
                throw new InvalidDataException($"Cannot safely resolve the retained identity collision involving {group.First().Key}: the original physical identity is ambiguous.");
            foreach (var replacement in group.Where(item => keys[item.Key] != original))
            {
                if (entries.Single(item => string.Equals(PilotPath.NormalizeRelative(item.RelativePath),
                        replacement.Key, StringComparison.OrdinalIgnoreCase)).BoundObjectId is not null ||
                    await HasIdentityEffectAuthorityAsync(connection, transaction, scan, group.Key,
                        keys[replacement.Key], token) && !recovered.Contains((group.Key, keys[replacement.Key])))
                    throw IdentityCollisionFailure(replacement.Key);
                byPath.Remove(replacement.Key);
                separatedPaths.Add(replacement.Key);
            }
        }

        foreach (var item in byPath.ToArray())
        {
            if (occupied.TryGetValue(item.Value, out var priorPath) &&
                !string.Equals(priorPath, item.Key, StringComparison.OrdinalIgnoreCase))
            {
                var original = originalIdentities.GetValueOrDefault(item.Value);
                if (occupiedKeys.GetValueOrDefault(item.Value) == original && keys[item.Key] != original &&
                    entries.Single(entry => string.Equals(PilotPath.NormalizeRelative(entry.RelativePath),
                        item.Key, StringComparison.OrdinalIgnoreCase)).BoundObjectId is null &&
                    (!await HasIdentityEffectAuthorityAsync(connection, transaction, scan, item.Value, keys[item.Key], token) ||
                     recovered.Contains((item.Value, keys[item.Key]))))
                {
                    byPath.Remove(item.Key);
                    separatedPaths.Add(item.Key);
                    continue;
                }
                throw new InvalidDataException($"The same retained object appears at both {priorPath} and {item.Key}; it cannot be planned as two independent files.");
            }
            occupied[item.Value] = item.Key;
            occupiedKeys[item.Value] = keys[item.Key];
        }
        var reservedObjects = known.Values.SelectMany(ids => ids).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in entries.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var path = PilotPath.NormalizeRelative(entry.RelativePath);
            if (byPath.ContainsKey(path)) continue;
            // An ordinary atomic save replaces the physical file at one unchanged
            // logical path. Preserve that path's identity unless the old object is
            // present elsewhere in this inventory or this is a proven legacy collision.
            // A later append that reveals a second location still fails closed at
            // publication; an incomplete inventory never grants planning authority.
            if (!separatedPaths.Contains(path) && priorObjectsByPath.TryGetValue(path, out var previous) &&
                previous.Count == 1 && priorKeysByObject[previous.Single()].Count == 1 &&
                (!occupied.TryGetValue(previous.Single(), out var occupiedPath) ||
                 string.Equals(occupiedPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                var retained = previous.Single();
                occupied[retained] = path;
                occupiedKeys[retained] = keys[path];
                byPath.Add(path, retained);
                continue;
            }
            var objectId = AuthorityDeterministicId("object", scan.PairId, path);
            while (reservedObjects.Contains(objectId) || occupied.TryGetValue(objectId, out var otherPath) &&
                   !string.Equals(otherPath, path, StringComparison.OrdinalIgnoreCase))
                objectId = AuthorityDeterministicId("object", scan.PairId, path, objectId);
            occupied[objectId] = path;
            occupiedKeys[objectId] = keys[path];
            byPath.Add(path, objectId);
        }
        return byPath;
    }

    private static string ValidateObservationPhysicalKey(string side, string json, CloudScanRoot? root)
    {
        var cloud = SyncAuthorityCloudObservationIdentity.TryParse(json, out var identity);
        if (root is null && cloud)
            throw new InvalidDataException("An explicit cloud identity requires a retained cloud root.");
        if (root is not null && (!cloud || identity is null ||
            !string.Equals(identity.Provider, root.Provider, StringComparison.OrdinalIgnoreCase) ||
            identity.ProviderAccountId != root.ProviderAccountId || identity.NamespaceId != root.NamespaceId))
            throw new InvalidDataException("The observation identity does not match its retained provider, account and namespace.");
        return ObservationPhysicalKey(side, json);
    }

    private static string ObservationPhysicalKey(string side, string json)
    {
        if (SyncAuthorityCloudObservationIdentity.TryParse(json, out var cloud) && cloud is not null)
            return JsonSerializer.Serialize(new[] { "cloud", cloud.Provider.ToLowerInvariant(),
                cloud.ProviderAccountId, cloud.NamespaceId, cloud.FileId });
        using var document = JsonDocument.Parse(json);
        var fileId = document.RootElement.GetProperty("fileId").GetString();
        var volume = side == "local" ? document.RootElement.GetProperty("volumeSerial").GetString() : null;
        if (string.IsNullOrWhiteSpace(fileId) || side == "local" && string.IsNullOrWhiteSpace(volume))
            throw new InvalidDataException("The observation has no complete physical identity.");
        return JsonSerializer.Serialize(new[] { side, volume, fileId });
    }

    private static async Task<Dictionary<string, string>> EarliestObservationIdentitiesAsync(
        SqliteConnection connection, SqliteTransaction transaction, SyncAuthorityScanGeneration scan,
        IReadOnlySet<string> objectIds, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (objectIds.Count == 0) return result;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH versions AS MATERIALIZED (
                SELECT object_version_id,object_id,identity_json FROM object_version
                WHERE object_id IN (SELECT value FROM json_each($objects)) AND side=$side
            ), ranked AS (
                SELECT v.object_id,v.identity_json,
                       row_number() OVER (PARTITION BY v.object_id
                           ORDER BY s.generation,o.observed_utc,o.observation_id) origin_rank
                FROM effective_observation o JOIN versions v ON v.object_version_id=o.object_version_id
                JOIN scan_generation s ON s.scan_generation_id=o.scan_generation_id
                WHERE s.pair_id=$pair AND s.side=$side AND s.status='complete'
            ) SELECT object_id,identity_json FROM ranked WHERE origin_rank=1;
            """;
        command.Parameters.AddWithValue("$pair", scan.PairId);
        command.Parameters.AddWithValue("$side", scan.Side);
        command.Parameters.AddWithValue("$objects", JsonSerializer.Serialize(objectIds));
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(reader.GetString(0), ObservationPhysicalKey(scan.Side, reader.GetString(1)));
        return result;
    }

    private static async Task<HashSet<(string ObjectId, string PhysicalKey)>> ValidateIdentityRecoveryAsync(
        SqliteConnection connection, SqliteTransaction transaction, SyncAuthorityScanGeneration scan,
        IReadOnlyList<SyncAuthorityObservationInput> entries, IReadOnlyList<SyncCompletedCopyMove> proofs,
        CancellationToken token)
    {
        var result = new HashSet<(string ObjectId, string PhysicalKey)>();
        var observed = entries.ToDictionary(item => PilotPath.NormalizeRelative(item.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        foreach (var proof in proofs)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Retained points are immutable. Bind their exact versions, paths,
            // roots and completed physical-copy receipts, and refuse any object
            // with unfinished or uncertain authority. No historical row is edited.
            command.CommandText = """
                SELECT CASE WHEN $side='local' THEN av.identity_json ELSE ar.identity_json END,
                       CASE WHEN $side='local' THEN bv.identity_json ELSE br.identity_json END,
                       ar.identity_json,br.identity_json
                FROM synchronization_point p JOIN synchronization_point c
                  ON c.pair_id=p.pair_id AND c.object_id=p.object_id
                JOIN membership a ON a.membership_id=p.local_membership_id
                JOIN membership b ON b.membership_id=c.local_membership_id
                JOIN membership am ON am.membership_id=p.dropbox_membership_id
                JOIN membership bm ON bm.membership_id=c.dropbox_membership_id
                JOIN object_version av ON av.object_version_id=p.local_object_version_id
                JOIN object_version bv ON bv.object_version_id=c.local_object_version_id
                JOIN object_version ar ON ar.object_version_id=p.dropbox_object_version_id
                JOIN object_version br ON br.object_version_id=c.dropbox_object_version_id
                WHERE p.pair_id=$pair AND p.object_id=$object
                  AND p.synchronization_point_id=$prior AND c.synchronization_point_id=$current
                  AND p.authority_generation<c.authority_generation
                  AND c.authority_generation=(SELECT max(x.authority_generation) FROM synchronization_point x
                      WHERE x.pair_id=c.pair_id AND x.object_id=c.object_id)
                  AND av.object_version_id=$oldLocal AND bv.object_version_id=$newLocal
                  AND ar.object_version_id=$oldRemote AND br.object_version_id=$newRemote
                  AND a.membership_id=$oldMember AND b.membership_id=$newMember
                  AND a.generation=$oldGeneration AND b.generation=$newGeneration
                  AND a.relative_path_key=$oldPath AND am.relative_path_key=$oldPath
                  AND b.relative_path_key=$newPath AND bm.relative_path_key=$newPath
                  AND a.root_binding_id=b.root_binding_id AND am.root_binding_id=bm.root_binding_id
                  AND CASE WHEN $side='local' THEN a.root_binding_id ELSE am.root_binding_id END=$root
                  AND EXISTS(SELECT 1 FROM operation o JOIN operation_member m ON m.operation_id=o.operation_id
                    JOIN operation_attempt t ON t.operation_id=o.operation_id
                    JOIN effect_receipt r ON r.attempt_id=t.attempt_id
                    WHERE o.pair_id=$pair AND o.object_id=$object AND o.kind='create_local' AND o.status='completed'
                      AND m.role='source' AND m.object_version_id=ar.object_version_id
                      AND t.status='verified_success' AND r.certainty='known_success'
                      AND json_extract(r.destination_identity_json,'$.identity')=av.identity_json
                      AND json_extract(r.destination_identity_json,'$.path')=$oldPath)
                  AND EXISTS(SELECT 1 FROM operation o JOIN operation_member m ON m.operation_id=o.operation_id
                    JOIN operation_attempt t ON t.operation_id=o.operation_id
                    JOIN effect_receipt r ON r.attempt_id=t.attempt_id
                    WHERE o.operation_id=$copy AND o.pair_id=$pair AND o.object_id=$object
                      AND o.kind='create_local' AND o.status='completed'
                      AND m.role='source' AND m.object_version_id=br.object_version_id
                      AND t.status='verified_success' AND r.certainty='known_success'
                      AND json_extract(r.destination_identity_json,'$.identity')=bv.identity_json
                      AND json_extract(r.destination_identity_json,'$.path')=$newPath)
                  AND NOT EXISTS(SELECT 1 FROM operation o WHERE
                    o.operation_id IN (SELECT m.operation_id FROM operation_member m WHERE m.object_id=$object
                      UNION SELECT related.operation_id FROM operation related WHERE related.object_id=$object) AND
                    (o.status NOT IN ('completed','superseded','cancelled')
                     OR EXISTS(SELECT 1 FROM operation_attempt t WHERE t.operation_id=o.operation_id
                       AND (o.status<>'completed' OR t.status NOT IN ('verified_success','verified_not_applied')))
                     OR EXISTS(SELECT 1 FROM lease l WHERE l.scope_kind='operation' AND l.scope_id=o.operation_id
                       AND l.status='active' AND julianday(l.expires_utc)>julianday('now'))));
                """;
            if (await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM current_copy_completion WHERE pair_id=$pair AND object_id=$object AND coverage_verified=1;",
                    token, ("$pair", scan.PairId), ("$object", proof.ObjectId)) == 1)
                command.CommandText = """
                SELECT CASE WHEN $side='local' THEN av.identity_json ELSE ar.identity_json END,
                       CASE WHEN $side='local' THEN bv.identity_json ELSE br.identity_json END,
                       ar.identity_json,br.identity_json
                FROM current_copy_completion c JOIN copy_predecessor p
                    ON p.pair_id=c.pair_id AND p.object_id=c.object_id
                JOIN membership a ON a.membership_id=p.local_membership_id
                JOIN membership b ON b.membership_id=c.local_membership_id
                JOIN membership am ON am.membership_id=p.remote_membership_id
                JOIN membership bm ON bm.membership_id=c.remote_membership_id
                JOIN object_version av ON av.object_version_id=p.local_version_id
                JOIN object_version bv ON bv.object_version_id=c.local_version_id
                JOIN object_version ar ON ar.object_version_id=p.remote_version_id
                JOIN object_version br ON br.object_version_id=c.remote_version_id
                WHERE c.pair_id=$pair AND c.object_id=$object AND c.coverage_verified=1
                    AND p.point_id=$prior AND c.point_id=$current AND p.point_generation<c.point_generation
                    AND av.object_version_id=$oldLocal AND bv.object_version_id=$newLocal
                    AND ar.object_version_id=$oldRemote AND br.object_version_id=$newRemote
                    AND a.membership_id=$oldMember AND b.membership_id=$newMember
                    AND a.generation=$oldGeneration AND b.generation=$newGeneration
                    AND a.relative_path_key=$oldPath AND am.relative_path_key=$oldPath
                    AND b.relative_path_key=$newPath AND bm.relative_path_key=$newPath
                    AND p.path_key=$oldPath AND c.path_key=$newPath
                    AND a.root_binding_id=b.root_binding_id AND am.root_binding_id=bm.root_binding_id
                    AND p.local_root_id=a.root_binding_id AND c.local_root_id=b.root_binding_id
                    AND p.remote_root_id=am.root_binding_id AND c.remote_root_id=bm.root_binding_id
                    AND CASE WHEN $side='local' THEN a.root_binding_id ELSE am.root_binding_id END=$root
                    AND c.copy_operation_id=$copy AND c.receipt_id IS NOT NULL AND p.receipt_id IS NOT NULL
                    AND c.attempt_status='verified_success' AND p.attempt_status='verified_success'
                    AND json_extract(p.destination_identity_json,'$.identity')=av.identity_json
                    AND json_extract(p.destination_identity_json,'$.path')=$oldPath
                    AND json_extract(c.destination_identity_json,'$.identity')=bv.identity_json
                    AND json_extract(c.destination_identity_json,'$.path')=$newPath
                    AND EXISTS(SELECT 1 FROM synchronization_point sp WHERE sp.pair_id=c.pair_id AND sp.object_id=c.object_id
                        AND sp.synchronization_point_id=c.point_id AND sp.authority_generation=c.point_generation
                        AND sp.local_object_version_id=c.local_version_id AND sp.dropbox_object_version_id=c.remote_version_id
                        AND sp.local_membership_id=c.local_membership_id AND sp.dropbox_membership_id=c.remote_membership_id)
                    AND NOT EXISTS(SELECT 1 FROM synchronization_point sp WHERE sp.pair_id=c.pair_id AND sp.object_id=c.object_id
                        AND sp.authority_generation>c.point_generation)
                  AND NOT EXISTS(SELECT 1 FROM operation o WHERE
                    o.operation_id IN (SELECT m.operation_id FROM operation_member m WHERE m.object_id=$object
                      UNION SELECT related.operation_id FROM operation related WHERE related.object_id=$object) AND
                    (o.status NOT IN ('completed','superseded','cancelled')
                     OR EXISTS(SELECT 1 FROM operation_attempt t WHERE t.operation_id=o.operation_id
                       AND (o.status<>'completed' OR t.status NOT IN ('verified_success','verified_not_applied')))
                     OR EXISTS(SELECT 1 FROM lease l WHERE l.scope_kind='operation' AND l.scope_id=o.operation_id
                       AND l.status='active' AND julianday(l.expires_utc)>julianday('now'))));
                """;
            foreach (var (name, value) in new (string, object)[]
            {
                ("$pair", scan.PairId), ("$side", scan.Side), ("$root", scan.RootBindingId),
                ("$object", proof.ObjectId), ("$prior", proof.PriorPointId), ("$current", proof.CurrentPointId),
                ("$oldLocal", proof.OldVersionId), ("$newLocal", proof.NewVersionId),
                ("$oldRemote", proof.OldRemoteVersionId), ("$newRemote", proof.NewRemoteVersionId),
                ("$oldMember", proof.OldMembershipId), ("$newMember", proof.NewMembershipId),
                ("$oldGeneration", proof.OldMembershipGeneration), ("$newGeneration", proof.NewMembershipGeneration),
                ("$oldPath", proof.OldPath), ("$newPath", proof.NewPath), ("$copy", proof.CopyOperationId)
            }) command.Parameters.AddWithValue(name, value);
            var identities = await ReadWithNativeCancellationAsync(connection, async () =>
            {
                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                    throw new InvalidDataException("Explicit identity recovery requires exact completed copies on both retained identities and no unfinished authority.");
                return (Older: reader.GetString(0), Current: reader.GetString(1),
                    OlderCloud: reader.GetString(2), CurrentCloud: reader.GetString(3));
            }, token);
            var oldKey = ObservationPhysicalKey(scan.Side, identities.Older);
            var newKey = ObservationPhysicalKey(scan.Side, identities.Current);
            if (oldKey == newKey || ObservationPhysicalKey("dropbox", identities.OlderCloud) ==
                    ObservationPhysicalKey("dropbox", identities.CurrentCloud))
                throw new InvalidDataException("Distinct-file identity recovery cannot reinterpret a moved physical file.");
            foreach (var (path, key) in new[] { (proof.OldPath, oldKey), (proof.NewPath, newKey) })
            {
                observed.TryGetValue(path, out var entry);
                var reason = entry is null ? "absent from the selected inventory"
                    : entry.BoundObjectId is not null ? "already bound to another recovery authority"
                    : entry.ObjectKind != "file" ? "not a regular file"
                    : entry.ContentHash is null ? "current content hash is unavailable"
                    : entry.Size is null ? "current byte length is unavailable"
                    : ObservationPhysicalKey(scan.Side, entry.IdentityJson) != key ? "physical identity differs from the retained verified copy"
                    : null;
                if (reason == "physical identity differs from the retained verified copy" &&
                    scan.Side == "dropbox" && path == proof.OldPath)
                {
                    // An atomic replacement can give the live cloud path a new
                    // physical ID after the older copy was completed. Keep the
                    // unique latest accepted archive as the old logical anchor;
                    // observe the new live file separately, never transplant the
                    // deleted file's accepted baseline or grant a copy here.
                    var observedKey = ObservationPhysicalKey(scan.Side, entry!.IdentityJson);
                    if (observedKey == newKey)
                        reason = "physical identity differs and duplicates the retained archive";
                    else if (entries.Any(item => ObservationPhysicalKey(scan.Side, item.IdentityJson) == oldKey))
                        reason = "the older cloud identity is still present elsewhere in the current inventory";
                    else if (await HasCurrentReplacementEffectAuthorityAsync(connection, transaction,
                                 scan, entry.IdentityJson, token))
                        reason = "the replacement cloud identity already has retained effect authority";
                    else { oldKey = observedKey; reason = null; }
                }
                if (reason is not null)
                    throw new IOException($"Identity recovery preserved {scan.Side} file '{path}': {reason}. Neither inventory was published; no file was changed.");
            }
            // Only the older physical incarnation separates. Current edits are
            // observations, not silently accepted baselines or copy permission.
            result.Add((proof.ObjectId, oldKey));
        }
        return result;
    }

    private static async Task<bool> HasCurrentReplacementEffectAuthorityAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncAuthorityScanGeneration scan, string identityJson, CancellationToken token)
    {
        using var identity = JsonDocument.Parse(identityJson);
        var fileId = identity.RootElement.GetProperty("fileId").GetString();
        return await ScalarLongAsync(connection, transaction, """
            SELECT count(*) FROM object_version v
            WHERE v.side=$side AND json_extract(v.identity_json,'$.fileId')=$file
              AND (EXISTS(SELECT 1 FROM synchronization_point p WHERE
                    p.dropbox_object_version_id=v.object_version_id)
                OR EXISTS(SELECT 1 FROM operation_member m JOIN operation_attempt a ON a.operation_id=m.operation_id
                    WHERE m.object_version_id=v.object_version_id));
            """, token, ("$side", scan.Side), ("$file", fileId!)) > 0;
    }

    private static InvalidDataException IdentityCollisionFailure(string path)
    {
        var error = new InvalidDataException($"The identity collision at {path} has retained effect authority and needs verification before it can be repaired.");
        error.Data[nameof(SyncAuthorityIdentityCollision)] = new SyncAuthorityIdentityCollision(path);
        return error;
    }

    private static async Task<bool> HasIdentityEffectAuthorityAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncAuthorityScanGeneration scan, string objectId, string key,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT v.identity_json FROM object_version v
            WHERE v.object_id=$object AND v.side=$side AND (
                EXISTS(SELECT 1 FROM operation_member m JOIN operation_attempt a ON a.operation_id=m.operation_id
                       WHERE m.object_version_id=v.object_version_id)
                OR EXISTS(SELECT 1 FROM synchronization_point p WHERE p.pair_id=$pair AND
                          (p.local_object_version_id=v.object_version_id OR p.dropbox_object_version_id=v.object_version_id)));
            """;
        command.Parameters.AddWithValue("$object", objectId);
        command.Parameters.AddWithValue("$side", scan.Side);
        command.Parameters.AddWithValue("$pair", scan.PairId);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            if (ObservationPhysicalKey(scan.Side, reader.GetString(0)) == key) return true;
        return false;
    }
}

internal sealed record SyncAuthorityIdentityCollision(string RelativePath);
