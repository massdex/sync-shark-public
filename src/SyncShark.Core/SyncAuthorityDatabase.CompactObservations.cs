using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

/// <summary>
/// The compact observation representation of one complete inventory. The producer supplies its whole
/// inventory once; unchanged rows reuse the captured parent cut's interval, membership and version, and
/// only changed, new or removed rows create candidate records. No legacy observation row is created,
/// rewritten or deleted here, so every legacy scan keeps its exact bytes and its complete meaning.
///
/// Preparation is bounded and restartable: an open cut exposes no content, a sealed cut resolves its
/// captured parent generation plus its own upserts and removals, and one publication transaction closes
/// the named prior intervals, adds the new ones, completes the scan, marks the cut published and removes
/// only its own delta rows. Every row is written inside an existing authority transaction with the same
/// schema, health and recovery machinery; no second database or generalized event store exists.
/// </summary>
public sealed partial class SyncAuthorityDatabase
{
    /// <summary>
    /// The canonical semantic-key namespace prefix of one evidence row. A normalized relative path can
    /// never contain ':' because Path.GetInvalidFileNameChars rejects it, so within one root a path key
    /// and an evidence key can never collide.
    /// </summary>
    private const string CompactEvidenceKeyPrefix = "evidence:";

    internal const string CompactIntervalIdentifierRole = "compact-interval";
    internal const string CompactObservationIdentifierPrefix = "observation-";

    private sealed record CompactCutHeader(string CutId, string ScanGenerationId, string RootBindingId, string Side,
        long Generation, string? CapturedParentScanId, long? CapturedParentGeneration, string State,
        long? SealedInputCount, string CutDigest, string? SealedInputDigest);

    private sealed record CompactMembershipRow(string MembershipId, string ObjectId, string State, long Generation);

    private sealed record CompactParentInterval(string IntervalId, string? MembershipId, string? ObjectVersionId,
        string SemanticDigest, string EvidenceJson);

    private sealed record CompactDeltaRow(string SemanticKey, string SemanticDigest, string Kind, string? IntervalId,
        string? ExpectedPriorIntervalId, string? MembershipId, string? ObjectVersionId, string? EvidenceJson,
        string? ObjectId, string? ObjectKind, string? IdentityJson, long MembershipGeneration,
        bool MembershipRequired, string? ContentHash, long? Size);

    /// <summary>
    /// Prepares one complete compact candidate for exactly one open scan generation. The supplied arrays
    /// are the producer's complete inventory: all normal entries, all evidence rows and the existing
    /// optional fenced binding or identity-recovery proof. Nothing here rescans a provider, and no
    /// publication happens before this returns.
    /// </summary>
    internal async Task PrepareCompactScanAsync(SyncAuthorityScanGeneration scan,
        IReadOnlyList<SyncAuthorityObservationInput> entries,
        IReadOnlyList<(string ObservationId, string EvidenceJson)> evidence,
        SyncAuthorityObservationBindingAuthority? bindingAuthority,
        IReadOnlyList<SyncCompletedCopyMove>? identityRecovery,
        CancellationToken cancellationToken = default,
        Func<SyncComparisonProgress, Task>? onProgress = null,
        string endpointLabel = "selected endpoint",
        bool concurrentProgressRetention = false)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(evidence);
        foreach (var entry in entries)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.RelativePath);
            if (entry.ObjectKind is not ("file" or "directory")) throw new ArgumentOutOfRangeException(nameof(entries));
            RequireJson(entry.IdentityJson, nameof(entries));
            RequireJson(entry.EvidenceJson, nameof(entries));
            if (entry.ContentHash is not null) RequireHash(entry.ContentHash, nameof(entries));
            if (entry.Size < 0) throw new ArgumentOutOfRangeException(nameof(entries));
            if (entry.BoundObjectId is not null) RequireIdentifier(entry.BoundObjectId, nameof(entries));
        }
        foreach (var item in evidence)
        {
            RequireIdentifier(item.ObservationId, nameof(evidence));
            RequireJson(item.EvidenceJson, nameof(evidence));
        }
        if (bindingAuthority is null && entries.Any(item => item.BoundObjectId is not null))
            throw new InvalidOperationException("A logical-object observation binding requires fenced operation authority.");
        if (bindingAuthority is not null && identityRecovery is { Count: > 0 })
            throw new InvalidOperationException("Identity recovery cannot replace fenced cross-pair authority.");

        var started = DateTimeOffset.UtcNow;
        if (onProgress is not null)
            await onProgress(new SyncComparisonProgress("Resolving File Identities", scan.PairId, 0, entries.Count,
                started, started,
                $"Loading the retained {endpointLabel} identity map and checking {entries.Count:N0} observed files/folders before saving their metadata.", "records resolved"));

        CompactCutHeader? cut = null;
        var parentIntervals = new Dictionary<string, CompactParentInterval>(StringComparer.Ordinal);
        var membershipRows = new Dictionary<string, CompactMembershipRow>(StringComparer.Ordinal);
        var priorCloudPaths = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        Dictionary<string, string> identities = null!;
        await WriteAsync(async (connection, transaction) =>
        {
            if (bindingAuthority is not null)
                await ValidateBoundObservationAuthorityAsync(connection, transaction, scan, entries, bindingAuthority,
                    cancellationToken);
            if (await ScalarLongAsync(connection, transaction, """
                    SELECT count(*) FROM scan_generation WHERE scan_generation_id=$id AND root_binding_id=$root
                      AND generation=$generation AND status='incomplete';
                    """, cancellationToken, ("$id", scan.ScanGenerationId), ("$root", scan.RootBindingId),
                    ("$generation", scan.Generation)) != 1)
                throw new InvalidOperationException("Compact observation preparation requires the current incomplete scan generation.");
            var cloudScanRoot = await LoadCloudScanRootAsync(connection, transaction, scan.RootBindingId, scan.PairId,
                scan.Side, cancellationToken);
            identities = await ResolveObservationBatchAsync(connection, transaction, scan, entries, cloudScanRoot,
                cancellationToken, identityRecovery);
            membershipRows = await LoadCompactMembershipRowsAsync(connection, transaction, scan.RootBindingId,
                cancellationToken);
            cut = await LoadCompactCutHeaderAsync(connection, transaction, scan.ScanGenerationId, cancellationToken);
            if (cut is null)
            {
                var parent = await LoadCapturedParentAsync(connection, transaction, scan.RootBindingId,
                    scan.ScanGenerationId, cancellationToken);
                cut = new CompactCutHeader(AuthorityDeterministicId("compact-cut", scan.ScanGenerationId),
                    scan.ScanGenerationId, scan.RootBindingId, scan.Side, scan.Generation, parent.ScanId,
                    parent.Generation, "open", null,
                    CompactCutDigest(scan.RootBindingId, scan.ScanGenerationId, scan.Generation), null);
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO observation_cut(cut_id,scan_generation_id,root_binding_id,side,generation,
                        captured_parent_scan_id,captured_parent_generation,state,sealed_input_count,cut_digest,created_utc)
                    VALUES($cut,$scan,$root,$side,$generation,$parent,$parentGeneration,'open',NULL,$digest,$utc);
                    """, cancellationToken, ("$cut", cut.CutId), ("$scan", scan.ScanGenerationId),
                    ("$root", scan.RootBindingId), ("$side", scan.Side), ("$generation", scan.Generation),
                    ("$parent", (object?)parent.ScanId ?? DBNull.Value),
                    ("$parentGeneration", (object?)parent.Generation ?? DBNull.Value),
                    ("$digest", cut.CutDigest), ("$utc", Utc(DateTimeOffset.UtcNow)));
            }
            else if (string.Equals(cut.State, "published", StringComparison.Ordinal))
                throw new InvalidDataException("This scan generation already published a compact cut; a second publication is refused.");
            else if (string.Equals(cut.State, "open", StringComparison.Ordinal))
            {
                // An interrupted open candidate exposes nothing. Its partial change set is discarded here so
                // one retry of the same complete inventory is the only writer of its delta rows.
                await ExecuteAsync(connection, transaction, "DELETE FROM observation_delta WHERE cut_id=$cut;",
                    cancellationToken, ("$cut", cut.CutId));
            }
            parentIntervals = await LoadCompactParentIntervalsAsync(connection, transaction, scan.RootBindingId,
                cut.CapturedParentScanId, cancellationToken);
            priorCloudPaths = await LoadCompactCloudNativePriorPathsAsync(connection, transaction, scan.RootBindingId,
                scan.Side, CompactCloudNativeFileIds(evidence), cancellationToken);
        }, cancellationToken);

        if (cut is null)
            throw new InvalidDataException("The compact cut header must exist after the cut is prepared.");

        var desired = new List<(string Key, string Digest)>();
        var deltas = new List<CompactDeltaRow>();
        var semanticKeys = new HashSet<string>(StringComparer.Ordinal);
        var payloads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var path = PilotPath.NormalizeRelative(entry.RelativePath);
            if (!semanticKeys.Add(path))
                throw new InvalidDataException($"The inventory contains the same path more than once: {path}.");
            var objectId = identities[path];
            var versionId = AuthorityDeterministicId("version-" + scan.Side, objectId, entry.IdentityJson,
                entry.ContentHash ?? "none", (entry.Size ?? 0).ToString(CultureInfo.InvariantCulture));
            membershipRows.TryGetValue(path, out var existing);
            string membershipId;
            long membershipGeneration;
            var membershipRequired = false;
            if (existing is not null && string.Equals(existing.ObjectId, objectId, StringComparison.Ordinal) &&
                string.Equals(existing.State, "active", StringComparison.Ordinal))
            {
                // The captured parent cut retained this exact root, path spelling, logical object and active
                // membership, so the row reuses that membership and resolves only its exact immutable version.
                membershipId = existing.MembershipId;
                membershipGeneration = existing.Generation;
            }
            else
            {
                membershipId = AuthorityDeterministicId("membership-" + scan.Side, scan.RootBindingId, path, objectId);
                membershipGeneration = checked((existing?.Generation ?? 0) + 1);
                membershipRequired = true;
            }
            var semanticDigest = CompactSemanticDigest(scan.Side, path, membershipId, versionId, entry.EvidenceJson);
            desired.Add((path, semanticDigest));
            parentIntervals.TryGetValue(path, out var prior);
            if (prior is not null && string.Equals(prior.MembershipId, membershipId, StringComparison.Ordinal) &&
                string.Equals(prior.ObjectVersionId, versionId, StringComparison.Ordinal) &&
                string.Equals(prior.EvidenceJson, entry.EvidenceJson, StringComparison.Ordinal))
                continue;
            deltas.Add(new CompactDeltaRow(path, semanticDigest, "upsert",
                AuthorityDeterministicId(CompactIntervalIdentifierRole, cut.CutId, path), prior?.IntervalId,
                membershipId, versionId, entry.EvidenceJson, objectId, entry.ObjectKind, entry.IdentityJson,
                membershipGeneration, membershipRequired, entry.ContentHash, entry.Size));
        }

        foreach (var item in evidence)
            AppendCompactEvidenceRow(scan, item.EvidenceJson, cut, parentIntervals, semanticKeys, payloads,
                priorCloudPaths, desired, deltas);

        // One pending removal per captured parent key that the complete supplied inventory no longer
        // contains. Publication closes that key's interval at this cut's generation, so the removed path
        // disappears from the new cut while every earlier cut still resolves it unchanged, and a missing
        // row is never by itself permission to delete anything.
        foreach (var retained in parentIntervals)
        {
            if (semanticKeys.Contains(retained.Key)) continue;
            deltas.Add(new CompactDeltaRow(retained.Key, retained.Value.SemanticDigest, "remove", null,
                retained.Value.IntervalId, null, null, null, null, null, null, 0, false, null, null));
        }

        var sealDigest = CompactSealDigest(desired);
        if (string.Equals(cut.State, "sealed", StringComparison.Ordinal))
        {
            if (cut.SealedInputCount != desired.Count ||
                !string.Equals(cut.SealedInputDigest, sealDigest, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "A different complete inventory was supplied for a scan generation that is already sealed; the changed candidate was refused.");
            return;
        }

        var metadataBatchSize = concurrentProgressRetention && bindingAuthority is null && onProgress is not null
            ? 1024
            : 128;
        var saved = 0;
        foreach (var batch in deltas.Chunk(metadataBatchSize))
        {
            if (onProgress is not null)
                await onProgress(new SyncComparisonProgress("Saving Folder Inventories", batch[0].SemanticKey, saved,
                    deltas.Count, started, DateTimeOffset.UtcNow,
                    $"Writing the next {batch.Length:N0} {endpointLabel} metadata records; {saved:N0} records are already committed. No file transfer is running.", "records saved"));
            var rows = batch;
            await WriteAsync(async (connection, transaction) =>
            {
                if (bindingAuthority is not null)
                    await RequireFenceAsync(connection, transaction, bindingAuthority.OperationId, bindingAuthority.Lease,
                        cancellationToken);
                using var commands = new ObservationCommands(connection, transaction);
                if (await commands.ScalarLongAsync(
                        "SELECT count(*) FROM scan_generation WHERE scan_generation_id=$id AND status='incomplete';",
                        cancellationToken, ("$id", scan.ScanGenerationId)) != 1)
                    throw new InvalidOperationException("The inventory is no longer open for compact metadata writes.");
                foreach (var row in rows)
                {
                    var now = Utc(DateTimeOffset.UtcNow);
                    if (row.MembershipId is not null)
                    {
                        var insertedObject = await commands.ExecuteAsync(
                            "INSERT OR IGNORE INTO logical_object(object_id,object_kind,created_utc) VALUES($id,$kind,$utc);",
                            cancellationToken, ("$id", row.ObjectId), ("$kind", row.ObjectKind), ("$utc", now));
                        if (insertedObject != 1 && await commands.ScalarLongAsync(
                                "SELECT count(*) FROM logical_object WHERE object_id=$id AND object_kind=$kind;",
                                cancellationToken, ("$id", row.ObjectId), ("$kind", row.ObjectKind)) != 1)
                            throw new InvalidOperationException("The deterministic observed object collides with a different kind.");
                        var insertedVersion = await commands.ExecuteAsync(
                            "INSERT OR IGNORE INTO object_version(object_version_id,object_id,side,identity_json,content_hash,size_bytes,observed_utc) VALUES($id,$object,$side,$identity,$hash,$size,$utc);",
                            cancellationToken, ("$id", row.ObjectVersionId), ("$object", row.ObjectId),
                            ("$side", scan.Side), ("$identity", row.IdentityJson), ("$hash", row.ContentHash),
                            ("$size", row.Size), ("$utc", now));
                        if (insertedVersion != 1 && await commands.ScalarLongAsync(
                                "SELECT count(*) FROM object_version WHERE object_version_id=$id AND object_id=$object AND side=$side AND identity_json=$identity AND content_hash IS $hash AND size_bytes IS $size;",
                                cancellationToken, ("$id", row.ObjectVersionId), ("$object", row.ObjectId),
                                ("$side", scan.Side), ("$identity", row.IdentityJson), ("$hash", row.ContentHash),
                                ("$size", row.Size)) != 1)
                        {
                            var retained = await commands.ScalarStringAsync(
                                "SELECT json_object('id',object_version_id,'sameHash',content_hash IS $hash,'sameSize',size_bytes IS $size) FROM object_version WHERE object_id=$object AND side=$side AND identity_json=$identity;",
                                cancellationToken, ("$hash", row.ContentHash), ("$size", row.Size),
                                ("$object", row.ObjectId), ("$side", scan.Side), ("$identity", row.IdentityJson));
                            throw new InvalidOperationException(
                                $"The deterministic observed version collides with different evidence. Side {scan.Side}; path {row.SemanticKey}; expected {row.ObjectVersionId}; retained comparison {retained ?? "absent"}.");
                        }
                        if (row.MembershipRequired)
                        {
                            var insertedMembership = await commands.ExecuteAsync(
                                "INSERT OR IGNORE INTO membership(membership_id,object_id,root_binding_id,relative_path_key,generation,state,observed_utc) VALUES($id,$object,$root,$path,$generation,'active',$utc);",
                                cancellationToken, ("$id", row.MembershipId), ("$object", row.ObjectId),
                                ("$root", scan.RootBindingId), ("$path", row.SemanticKey),
                                ("$generation", row.MembershipGeneration), ("$utc", now));
                            if (insertedMembership != 1 && await commands.ScalarLongAsync(
                                    "SELECT count(*) FROM membership WHERE membership_id=$id AND object_id=$object AND root_binding_id=$root AND relative_path_key=$path AND generation=$generation AND state='active';",
                                    cancellationToken, ("$id", row.MembershipId), ("$object", row.ObjectId),
                                    ("$root", scan.RootBindingId), ("$path", row.SemanticKey),
                                    ("$generation", row.MembershipGeneration)) != 1)
                                throw new InvalidOperationException("The deterministic observed membership collides with different evidence.");
                        }
                    }
                    await commands.ExecuteAsync("""
                        INSERT INTO observation_delta(delta_id,cut_id,scan_generation_id,root_binding_id,side,
                            semantic_key,semantic_digest,kind,interval_id,expected_prior_interval_id,membership_id,
                            object_version_id,evidence_json)
                        VALUES($delta,$cut,$scan,$root,$side,$key,$digest,$kind,$interval,$prior,$membership,$version,$evidence);
                        """, cancellationToken,
                        ("$delta", AuthorityDeterministicId("compact-delta", cut.CutId, row.SemanticKey)),
                        ("$cut", cut.CutId), ("$scan", scan.ScanGenerationId), ("$root", scan.RootBindingId),
                        ("$side", scan.Side), ("$key", row.SemanticKey), ("$digest", row.SemanticDigest),
                        ("$kind", row.Kind), ("$interval", row.IntervalId), ("$prior", row.ExpectedPriorIntervalId),
                        ("$membership", row.MembershipId), ("$version", row.ObjectVersionId),
                        ("$evidence", row.EvidenceJson));
                }
            }, cancellationToken);
            saved += batch.Length;
            if (onProgress is not null)
                await onProgress(new SyncComparisonProgress("Saving Folder Inventories", batch[^1].SemanticKey, saved,
                    deltas.Count, started, DateTimeOffset.UtcNow,
                    $"Committed {batch.Length:N0} {endpointLabel} compact records. The complete inventory is not published until the seal validates.", "records saved"));
        }

        // The seal is the last write of preparation: it records the full supplied input count and its exact
        // digest only after every batch and every evidence expansion above has succeeded. Deltas written
        // before the seal cannot leak, because the view resolves an open cut to no content at all.
        await WriteAsync(async (connection, transaction) =>
        {
            if (bindingAuthority is not null)
                await RequireFenceAsync(connection, transaction, bindingAuthority.OperationId, bindingAuthority.Lease,
                    cancellationToken);
            if (await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM scan_generation WHERE scan_generation_id=$id AND status='incomplete';",
                    cancellationToken, ("$id", scan.ScanGenerationId)) != 1)
                throw new InvalidOperationException("The inventory is no longer open for the compact seal.");
            if (await ScalarLongAsync(connection, transaction, "SELECT count(*) FROM observation_delta WHERE cut_id=$cut;",
                    cancellationToken, ("$cut", cut.CutId)) != deltas.Count)
                throw new InvalidOperationException("The compact candidate change set is not the exact supplied change set; the seal was refused.");
            var sealedRows = await ExecuteCountAsync(connection, transaction, """
                UPDATE observation_cut SET state='sealed',sealed_input_count=$count,sealed_input_digest=$digest,
                    sealed_utc=$utc
                WHERE cut_id=$cut AND state='open';
                """, cancellationToken, ("$count", desired.Count), ("$digest", sealDigest),
                ("$utc", Utc(DateTimeOffset.UtcNow)), ("$cut", cut.CutId));
            if (sealedRows != 1)
                throw new InvalidOperationException("The compact candidate was not the one open cut expected by the seal.");
        }, cancellationToken);
    }

    private void AppendCompactEvidenceRow(SyncAuthorityScanGeneration scan, string payload, CompactCutHeader cut,
        Dictionary<string, CompactParentInterval> parentIntervals, HashSet<string> semanticKeys, HashSet<string> payloads,
        Dictionary<string, List<string>> priorCloudPaths, List<(string Key, string Digest)> desired,
        List<CompactDeltaRow> deltas)
    {
        Add(payload);
        // A known cloud object may have been renamed to a name this device cannot represent. Retaining its
        // earlier paths as explicit evidence is what keeps that rename from being read as deletion.
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("state", out var state) ||
            !string.Equals(state.GetString(), "cloud_native", StringComparison.Ordinal) ||
            !document.RootElement.TryGetProperty("fileId", out var fileId) || fileId.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(fileId.GetString()) ||
            !priorCloudPaths.TryGetValue(fileId.GetString()!, out var priorPaths))
            return;
        foreach (var path in priorPaths)
        {
            var alias = JsonNode.Parse(payload)!.AsObject();
            alias["blockedCloudPath"] = alias["path"]?.GetValue<string>();
            alias["path"] = path;
            Add(alias.ToJsonString());
        }

        void Add(string value)
        {
            if (!payloads.Add(value))
                throw new InvalidDataException(
                    "A compact producer may not supply the identical evidence payload twice; the duplicate was refused rather than coalesced.");
            var semanticKey = CompactEvidenceSemanticKey(scan.Side, value);
            if (!semanticKeys.Add(semanticKey))
                throw new InvalidDataException("Two compact evidence rows resolved to the same canonical semantic key.");
            var semanticDigest = CompactSemanticDigest(scan.Side, semanticKey, null, null, value);
            desired.Add((semanticKey, semanticDigest));
            parentIntervals.TryGetValue(semanticKey, out var prior);
            if (prior is not null && prior.MembershipId is null && prior.ObjectVersionId is null &&
                string.Equals(prior.EvidenceJson, value, StringComparison.Ordinal))
                return;
            deltas.Add(new CompactDeltaRow(semanticKey, semanticDigest, "upsert",
                AuthorityDeterministicId(CompactIntervalIdentifierRole, cut.CutId, semanticKey), prior?.IntervalId,
                null, null, value, null, null, null, 0, false, null, null));
        }
    }

    private static List<string> CompactCloudNativeFileIds(
        IReadOnlyList<(string ObservationId, string EvidenceJson)> evidence)
    {
        var result = new List<string>();
        foreach (var item in evidence)
        {
            using var document = JsonDocument.Parse(item.EvidenceJson);
            if (!document.RootElement.TryGetProperty("state", out var state) ||
                !string.Equals(state.GetString(), "cloud_native", StringComparison.Ordinal) ||
                !document.RootElement.TryGetProperty("fileId", out var fileId) ||
                fileId.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(fileId.GetString()))
                continue;
            if (!result.Contains(fileId.GetString()!, StringComparer.Ordinal)) result.Add(fileId.GetString()!);
        }
        return result;
    }

    /// <summary>
    /// Publishes one sealed compact candidate inside the caller's existing transaction. Returns false when
    /// this scan generation has no compact cut at all, so the exact legacy publication path stays in charge
    /// for a legacy scan. Only a sealed, current, non-stale candidate whose sealed input count still equals
    /// its own resolved row count may publish.
    /// </summary>
    private static async Task<bool> TryPublishCompactCutAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncAuthorityScanGeneration scan, CancellationToken cancellationToken)
    {
        var cut = await LoadCompactCutHeaderAsync(connection, transaction, scan.ScanGenerationId, cancellationToken);
        if (cut is null) return false;
        if (string.Equals(cut.State, "published", StringComparison.Ordinal))
            throw new InvalidDataException("The compact cut for this scan generation is already published; a second publication is refused.");
        if (!string.Equals(cut.State, "sealed", StringComparison.Ordinal) || cut.SealedInputCount is null)
            throw new InvalidDataException("The compact candidate for this scan generation is not sealed; an unsealed candidate is never published.");
        if (await ScalarLongAsync(connection, transaction, """
                SELECT count(*) FROM scan_generation WHERE scan_generation_id=$id AND pair_id=$pair
                  AND root_binding_id=$root AND side=$side AND generation=$generation AND status='incomplete';
                """, cancellationToken, ("$id", scan.ScanGenerationId), ("$pair", scan.PairId),
                ("$root", scan.RootBindingId), ("$side", scan.Side), ("$generation", scan.Generation)) != 1)
            throw new InvalidOperationException(
                "The compact scan generation is not the current incomplete generation of its exact pair, side and root.");
        var parent = await LoadCapturedParentAsync(connection, transaction, scan.RootBindingId, scan.ScanGenerationId,
            cancellationToken);
        if (!string.Equals(parent.ScanId, cut.CapturedParentScanId, StringComparison.Ordinal) ||
            parent.Generation != cut.CapturedParentGeneration)
            throw new InvalidDataException(
                "The latest complete parent of this root changed after the compact candidate captured it; the stale candidate was refused and nothing was published.");
        var duplicate = await ScalarStringAsync(connection, transaction, """
            SELECT min(m.relative_path_key) FROM effective_observation o
            JOIN object_version v ON v.object_version_id=o.object_version_id
            JOIN membership m ON m.membership_id=o.membership_id
            WHERE o.scan_generation_id=$scan GROUP BY v.object_id
            HAVING count(DISTINCT m.relative_path_key)>1 LIMIT 1;
            """, cancellationToken, ("$scan", scan.ScanGenerationId));
        if (duplicate is not null)
            throw new InvalidDataException($"The inventory contains a conflicting file identity at {duplicate}. The incomplete snapshot was not published and no action may be planned from it.");
        var resolved = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM effective_observation WHERE scan_generation_id=$scan;", cancellationToken,
            ("$scan", scan.ScanGenerationId));
        if (resolved != cut.SealedInputCount)
            throw new InvalidDataException(
                "The sealed compact candidate no longer resolves to its sealed complete input count; publication was refused.");
        var expectedPrior = await ScalarLongAsync(connection, transaction, """
            SELECT count(*) FROM observation_delta d WHERE d.cut_id=$cut AND d.kind='upsert'
              AND d.expected_prior_interval_id IS NOT NULL;
            """, cancellationToken, ("$cut", cut.CutId));
        var openPrior = await ScalarLongAsync(connection, transaction, """
            SELECT count(*) FROM observation_delta d JOIN observation_interval i
              ON i.interval_id=d.expected_prior_interval_id
            WHERE d.cut_id=$cut AND d.kind='upsert' AND i.to_generation IS NULL;
            """, cancellationToken, ("$cut", cut.CutId));
        if (expectedPrior != openPrior)
            throw new InvalidDataException(
                "A captured parent interval was already closed by another publication; the stale candidate was refused.");

        var now = Utc(DateTimeOffset.UtcNow);
        await ExecuteAsync(connection, transaction, """
            UPDATE observation_interval SET to_generation=$generation
            WHERE root_binding_id=$root AND to_generation IS NULL
              AND semantic_key IN (SELECT semantic_key FROM observation_delta WHERE cut_id=$cut);
            """, cancellationToken, ("$generation", cut.Generation), ("$root", cut.RootBindingId), ("$cut", cut.CutId));
        await ExecuteAsync(connection, transaction, """
            INSERT INTO observation_interval(interval_id,root_binding_id,side,semantic_key,semantic_digest,
                from_generation,to_generation,membership_id,object_version_id,evidence_json,observation_id,observed_utc)
            SELECT d.interval_id,d.root_binding_id,d.side,d.semantic_key,d.semantic_digest,$generation,NULL,
                   d.membership_id,d.object_version_id,d.evidence_json,$prefix || d.semantic_digest,$utc
            FROM observation_delta d WHERE d.cut_id=$cut AND d.kind='upsert';
            """, cancellationToken, ("$generation", cut.Generation),
            ("$prefix", CompactObservationIdentifierPrefix + cut.CutDigest + "-"), ("$utc", now), ("$cut", cut.CutId));
        var completed = await ExecuteCountAsync(connection, transaction,
            "UPDATE scan_generation SET status='complete',completed_utc=$utc,entry_count=$count WHERE scan_generation_id=$id AND status='incomplete';",
            cancellationToken, ("$utc", now), ("$count", cut.SealedInputCount.Value), ("$id", scan.ScanGenerationId));
        if (completed != 1)
            throw new InvalidOperationException("The compact scan generation was not the one incomplete generation expected by its publication.");
        var publishedRows = await ExecuteCountAsync(connection, transaction,
            "UPDATE observation_cut SET state='published',published_utc=$utc WHERE cut_id=$cut AND state='sealed';",
            cancellationToken, ("$utc", now), ("$cut", cut.CutId));
        if (publishedRows != 1)
            throw new InvalidOperationException("The compact cut was not the one sealed cut expected by its publication.");
        await ExecuteAsync(connection, transaction, "DELETE FROM observation_delta WHERE cut_id=$cut;", cancellationToken,
            ("$cut", cut.CutId));
        var published = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM effective_observation WHERE scan_generation_id=$scan;", cancellationToken,
            ("$scan", scan.ScanGenerationId));
        if (published != cut.SealedInputCount)
            throw new InvalidDataException(
                "The published compact cut does not resolve to its sealed complete input count; the publication was rolled back.");
        return true;
    }

    private static async Task<CompactCutHeader?> LoadCompactCutHeaderAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string scanGenerationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT cut_id,scan_generation_id,root_binding_id,side,generation,captured_parent_scan_id,
                   captured_parent_generation,state,sealed_input_count,cut_digest,sealed_input_digest
            FROM observation_cut WHERE scan_generation_id=$scan;
            """;
        command.Parameters.AddWithValue("$scan", scanGenerationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new CompactCutHeader(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private static async Task<(string? ScanId, long? Generation)> LoadCapturedParentAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string rootBindingId, string excludedScanId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT scan_generation_id,generation FROM scan_generation
            WHERE root_binding_id=$root AND status='complete' AND scan_generation_id<>$excluded
            ORDER BY generation DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$root", rootBindingId);
        command.Parameters.AddWithValue("$excluded", excludedScanId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (null, null);
        return (reader.GetString(0), reader.GetInt64(1));
    }

    private static async Task<Dictionary<string, CompactMembershipRow>> LoadCompactMembershipRowsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string rootBindingId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, CompactMembershipRow>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT m.relative_path_key,m.membership_id,m.object_id,m.state,m.generation
            FROM membership m
            WHERE m.root_binding_id=$root
              AND m.generation=(SELECT max(x.generation) FROM membership x
                               WHERE x.root_binding_id=m.root_binding_id AND x.relative_path_key=m.relative_path_key);
            """;
        command.Parameters.AddWithValue("$root", rootBindingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetString(0)] = new CompactMembershipRow(reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4));
        return result;
    }

    /// <summary>
    /// The root's interval window at the captured parent generation, keyed by semantic key.
    /// A legacy observation can follow an older compact cut without closing its intervals.
    /// Include those still-open intervals in the next complete diff so omitted paths get
    /// explicit removal deltas. Historical legacy rows and earlier compact cuts stay unchanged.
    /// </summary>
    private static async Task<Dictionary<string, CompactParentInterval>> LoadCompactParentIntervalsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string rootBindingId, string? parentScanId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, CompactParentInterval>(StringComparer.Ordinal);
        if (parentScanId is null) return result;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.semantic_key,i.interval_id,i.membership_id,i.object_version_id,i.semantic_digest,i.evidence_json
            FROM scan_generation s
            JOIN observation_interval i ON i.root_binding_id=s.root_binding_id
              AND i.from_generation<=s.generation
              AND (i.to_generation IS NULL OR i.to_generation>s.generation)
            WHERE s.scan_generation_id=$parent AND s.root_binding_id=$root AND s.status='complete';
            """;
        command.Parameters.AddWithValue("$parent", parentScanId);
        command.Parameters.AddWithValue("$root", rootBindingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetString(0)] = new CompactParentInterval(reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5));
        return result;
    }

    private static async Task<Dictionary<string, List<string>>> LoadCompactCloudNativePriorPathsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string rootBindingId, string side,
        IReadOnlyList<string> fileIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (fileIds.Count == 0) return result;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH files AS (SELECT value AS file_id FROM json_each($files))
            SELECT f.file_id,m.relative_path_key
            FROM files f
            JOIN object_version v ON v.side=$side AND json_extract(v.identity_json,'$.fileId')=f.file_id
            JOIN effective_observation o ON o.object_version_id=v.object_version_id
              AND o.root_binding_id=$root AND o.complete_generation=1
            JOIN membership m ON m.membership_id=o.membership_id
            ORDER BY m.relative_path_key;
            """;
        command.Parameters.AddWithValue("$root", rootBindingId);
        command.Parameters.AddWithValue("$side", side);
        command.Parameters.AddWithValue("$files", JsonSerializer.Serialize(fileIds));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fileId = reader.GetString(0);
            if (!result.TryGetValue(fileId, out var paths)) result[fileId] = paths = [];
            paths.Add(reader.GetString(1));
        }
        return result;
    }

    private static string CompactCutDigest(string rootBindingId, string scanGenerationId, long generation) =>
        Sha256Hex("compact-cut\n" + rootBindingId + "\n" + scanGenerationId + "\n" +
                  generation.ToString(CultureInfo.InvariantCulture));

    private static string CompactEvidenceSemanticKey(string side, string payload) =>
        CompactEvidenceKeyPrefix + Sha256Hex("compact-evidence\n" + side + "\n" + payload);

    private static string CompactSemanticDigest(string side, string semanticKey, string? membershipId,
        string? objectVersionId, string evidenceJson) =>
        Sha256Hex("compact-semantic\n" + side + "\n" + semanticKey + "\n" + (membershipId ?? "none") + "\n" +
                  (objectVersionId ?? "none") + "\n" + evidenceJson);

    private static string CompactSealDigest(IReadOnlyList<(string Key, string Digest)> rows)
    {
        var material = new StringBuilder("compact-seal\n");
        foreach (var row in rows.OrderBy(item => item.Key, StringComparer.Ordinal))
            material.Append(row.Key).Append('\n').Append(row.Digest).Append('\n');
        return Sha256Hex(material.ToString());
    }

    private static string Sha256Hex(string material) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
}
