using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

/// <summary>
/// D2 opaque hierarchy retention (source only).
///
/// Publication has provider-verified hierarchy, but immediate DB readback and restart do not. This retains ONE
/// typed, authority-bound OBSERVATION of the provider-verified complete hierarchy in the EXISTING history_event
/// storage under one approved event kind. It is a retained observation, NOT current provider authority and NOT a
/// trusted caller flag: only the internal DB-owned path may retain it, and it is accepted on readback only when
/// the CURRENT active root identity, generation, account and stored digests still agree exactly. Every
/// preparation, reservation and effect path still requires its fresh provider context.
///
/// No new table, column, schema version, service, controller or key is introduced, and an ordinary read appends
/// nothing.
/// </summary>
public sealed partial class SyncAuthorityDatabase
{
    internal const string OpaqueHierarchyRetainedHistoryKind = "opaque_hierarchy_retained";

    /// <summary>The typed retained binding. The complete verified snapshot itself is stored alongside it.</summary>
    internal sealed record RetainedOpaqueHierarchy(
        string EventId,
        string PairId,
        string Side,
        string RootBindingId,
        long RootGeneration,
        string InstallationId,
        string RootIdentityDigest,
        string EvidenceDigest,
        SyncAuthorityCloudHierarchySnapshot Snapshot);

    /// <summary>
    /// Retains ONE observation of an already provider-verified complete hierarchy for the exact current active
    /// root of this pair and side. The snapshot must bind to the current active root identity, be complete to a
    /// corpus root, and the account must be active for this installation inside the same transaction. A repeat
    /// of the same exact binding and evidence is idempotent and appends nothing.
    /// </summary>
    internal async Task<string> RetainOpaqueHierarchyObservationAsync(
        SyncAuthorityCloudHierarchySnapshot snapshot, string pairId, string side, long rootGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        if (rootGeneration < 1) throw new ArgumentOutOfRangeException(nameof(rootGeneration));
        if (!snapshot.CompleteToCorpusRoot || snapshot.ChainFolderIds.Count < 2)
            throw new InvalidDataException(
                "A retained opaque hierarchy observation requires the complete provider chain to its corpus root.");

        var installationId = await LoadInstallationIdAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "The authority database retains no installation identity for hierarchy retention.");
        string eventId = "";
        await WriteAsync(async (connection, transaction) =>
        {
            var identityJson = await ScalarStringAsync(connection, transaction,
                "SELECT identity_json FROM root_binding WHERE root_binding_id=$root AND pair_id=$pair " +
                "AND side=$side AND generation=$generation AND state IN ('active','blocked');",
                cancellationToken, ("$root", snapshot.RootBindingId), ("$pair", pairId), ("$side", side),
                ("$generation", rootGeneration))
                ?? throw new InvalidOperationException(
                    "Hierarchy retention requires the exact CURRENT ACTIVE root binding of this pair and side.");
            if (!SyncAuthorityCloudRootPersistence.TryRead(identityJson, out var provider, out var account,
                    out var namespaceId, out var folderId) ||
                !string.Equals(provider, snapshot.Provider, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(account, snapshot.ProviderAccountId, StringComparison.Ordinal) ||
                !string.Equals(namespaceId, snapshot.NamespaceId, StringComparison.Ordinal) ||
                !string.Equals(folderId, snapshot.FolderId, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "The supplied hierarchy does not bind to the current active root identity of this pair and side.");
            await RequireActiveCandidateAccountAsync(connection, transaction, installationId, provider!,
                account!, namespaceId!, cancellationToken);

            var identityDigest = Digest(identityJson);
            var evidenceDigest = EvidenceDigest(snapshot);
            var existing = await ScalarStringAsync(connection, transaction,
                "SELECT history_event_id FROM history_event WHERE event_kind=$kind " +
                "AND json_extract(detail_json,'$.PairId')=$pair " +
                "AND json_extract(detail_json,'$.Side')=$side " +
                "AND json_extract(detail_json,'$.RootBindingId')=$root " +
                "AND json_extract(detail_json,'$.RootGeneration')=$generation " +
                "AND json_extract(detail_json,'$.EvidenceDigest')=$digest ORDER BY occurred_utc DESC LIMIT 1;",
                cancellationToken, ("$kind", OpaqueHierarchyRetainedHistoryKind), ("$pair", pairId),
                ("$side", side), ("$root", snapshot.RootBindingId), ("$generation", rootGeneration),
                ("$digest", evidenceDigest));
            if (existing is not null)
            {
                eventId = existing;
                return;
            }
            var id = "history-" + Guid.NewGuid().ToString("N");
            var detail = JsonSerializer.Serialize(new
            {
                EventId = id,
                EventKind = OpaqueHierarchyRetainedHistoryKind,
                PairId = pairId,
                Side = side,
                RootBindingId = snapshot.RootBindingId,
                RootGeneration = rootGeneration,
                InstallationId = installationId,
                RootIdentityDigest = identityDigest,
                EvidenceDigest = evidenceDigest,
                Snapshot = snapshot,
                RetainedUtc = Utc(DateTimeOffset.UtcNow)
            });
            await ExecuteAsync(connection, transaction,
                "INSERT INTO history_event(retention_class,rowid,history_event_id,operation_id,event_kind,detail_json,occurred_utc) " +
                "VALUES('safety',(SELECT next_history_sequence FROM installation),$id,NULL,$kind,$detail,$utc);",
                cancellationToken, ("$id", id), ("$kind", OpaqueHierarchyRetainedHistoryKind),
                ("$detail", detail), ("$utc", Utc(DateTimeOffset.UtcNow)));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM history_event WHERE history_event_id=$id AND event_kind=$kind " +
                "AND detail_json=$detail;",
                cancellationToken, ("$id", id), ("$kind", OpaqueHierarchyRetainedHistoryKind),
                ("$detail", detail));
            if (exact != 1)
                throw new InvalidOperationException(
                    "The retained opaque hierarchy observation was not retained exactly.");
            eventId = id;
        }, cancellationToken);
        return eventId;
    }

    /// <summary>
    /// D2: the internal DB-owned PRODUCER. Retains the already-provider-verified chain of one ACTIVE pair root as
    /// its observation, exactly once per changed exact binding (the retention below is idempotent for the same
    /// pair, side, root, generation and evidence digest). A recovery boundary, an unknown or non-active root, a
    /// side other than local/dropbox, or a chain that does not bind the exact current root identity is never
    /// retained; a retention that cannot bind is ignored rather than thrown, so publication is never broken by
    /// evidence bookkeeping and the readback consumer keeps its fail-closed behaviour.
    /// </summary>
    internal async Task<string?> RetainVerifiedPairRootObservationAsync(
        SyncAuthorityCloudRootIdentity authority, SyncAuthorityCloudHierarchySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!string.Equals(authority.AuthorityKind, SyncAuthorityCloudRootIdentity.PairRootAuthority,
                StringComparison.Ordinal)) return null;
        if (authority.CompatibilitySide is not ("local" or "dropbox")) return null;
        var owner = await LoadActiveRootOwnershipAsync(authority.AuthorityId, cancellationToken);
        if (owner is null) return null;
        if (!string.Equals(owner.Value.Side, authority.CompatibilitySide, StringComparison.Ordinal)) return null;
        return await RetainVerifiedPairRootObservationAsync(owner.Value.PairId, owner.Value.Side, snapshot,
            cancellationToken);
    }

    /// <summary>
    /// D2: the same producer keyed by pair and side, for a caller that has just verified one of its own roots
    /// (the publication refresh and the candidate registration). Only a registered PATH-OPAQUE provider chain is
    /// retained, and only against the exact current root generation of that pair and side.
    /// </summary>
    internal async Task<string?> RetainVerifiedPairRootObservationAsync(string pairId, string side,
        SyncAuthorityCloudHierarchySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) return null;
        if (!SyncEndpointProviders.TryGet(snapshot.Provider, out var definition) ||
            definition?.ProviderId is null || definition.PathsDescribeAncestry) return null;
        var generation = await LoadRootBindingGenerationAsync(pairId, snapshot.RootBindingId, cancellationToken);
        if (generation < 1) return null;
        try
        {
            return await RetainOpaqueHierarchyObservationAsync(snapshot, pairId, side, generation,
                cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The exact pair, side and generation of one ACTIVE root binding, or null.</summary>
    private async Task<(string PairId, string Side, long Generation)?> LoadActiveRootOwnershipAsync(
        string rootBindingId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pair_id,side,generation FROM root_binding " +
            "WHERE root_binding_id=$id AND state='active';";
        command.Parameters.AddWithValue("$id", rootBindingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
    }

    /// <summary>
    /// Resolves the retained observation for this pair and side, or null. It is accepted ONLY while the CURRENT
    /// active root binding is the same root at the same generation with the same identity digest, the account is
    /// still active for this installation, and the stored evidence digest still matches the stored snapshot
    /// exactly. A read appends nothing, performs no network or filesystem effect, and never mints authority.
    /// </summary>
    internal async Task<RetainedOpaqueHierarchy?> ResolveRetainedOpaqueHierarchyAsync(string pairId, string side,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        var installationId = await LoadInstallationIdAsync(cancellationToken);
        if (installationId is null) return null;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var readOnly = connection.BeginTransaction();
        return await ResolveRetainedOpaqueHierarchyCoreAsync(connection, readOnly, installationId, pairId, side,
            cancellationToken);
    }

    /// <summary>
    /// The same resolution inside a caller-supplied connection/transaction, so a write-side consumer (the
    /// reservation and alias path) reads the pair rows, the current authority and the retained evidence in ONE
    /// coherent snapshot instead of opening a second connection. It never writes.
    /// </summary>
    internal static async Task<RetainedOpaqueHierarchy?> ResolveRetainedOpaqueHierarchyCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string installationId, string pairId,
        string side, CancellationToken cancellationToken, bool allowBlockedCandidateRoot = false)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        // The caller supplies ONE coherent snapshot - its read transaction, or the write transaction the
        // reservation and alias consumer already holds - for the pair rows, the current authority and the
        // retained evidence together.
        var readTransaction = transaction ?? throw new InvalidOperationException(
            "Resolving a retained opaque observation requires the caller's coherent read or write transaction.");
        var lookupStarted = TimingStart();
        var detail = await ScalarStringAsync(connection, readTransaction,
            "SELECT detail_json FROM history_event WHERE event_kind=$kind " +
            "AND json_extract(detail_json,'$.PairId')=$pair " +
            "AND json_extract(detail_json,'$.Side')=$side ORDER BY occurred_utc DESC, rowid DESC LIMIT 1;",
            cancellationToken, ("$kind", OpaqueHierarchyRetainedHistoryKind), ("$pair", pairId),
            ("$side", side));
        ReportTiming("opaque-hierarchy-history-lookup", lookupStarted);
        if (detail is null) return null;
        RetainedOpaqueHierarchy retained;
        try
        {
            using var document = JsonDocument.Parse(detail);
            var root = document.RootElement;
            var stored = root.GetProperty("Snapshot").Deserialize<SyncAuthorityCloudHierarchySnapshot>();
            if (stored is null) return null;
            retained = new RetainedOpaqueHierarchy(
                root.GetProperty("EventId").GetString() ?? "",
                root.GetProperty("PairId").GetString() ?? "",
                root.GetProperty("Side").GetString() ?? "",
                root.GetProperty("RootBindingId").GetString() ?? "",
                root.GetProperty("RootGeneration").GetInt64(),
                root.GetProperty("InstallationId").GetString() ?? "",
                root.GetProperty("RootIdentityDigest").GetString() ?? "",
                root.GetProperty("EvidenceDigest").GetString() ?? "",
                stored);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
                                          or NotSupportedException)
        {
            return null;
        }
        if (!string.Equals(retained.InstallationId, installationId, StringComparison.Ordinal)) return null;
        var identityJson = await ScalarStringAsync(connection, readTransaction,
            "SELECT identity_json FROM root_binding WHERE root_binding_id=$root AND pair_id=$pair " +
            "AND side=$side AND generation=$generation " +
            "AND (state='active' OR ($blocked=1 AND state='blocked'));",
            cancellationToken, ("$root", retained.RootBindingId), ("$pair", pairId), ("$side", side),
            ("$generation", retained.RootGeneration), ("$blocked", allowBlockedCandidateRoot ? 1 : 0));
        if (identityJson is null ||
            !string.Equals(Digest(identityJson), retained.RootIdentityDigest, StringComparison.Ordinal)) return null;
        if (!SyncAuthorityCloudRootPersistence.TryRead(identityJson, out var provider, out var account,
                out var namespaceId, out var folderId) ||
            !string.Equals(provider, retained.Snapshot.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(account, retained.Snapshot.ProviderAccountId, StringComparison.Ordinal) ||
            !string.Equals(namespaceId, retained.Snapshot.NamespaceId, StringComparison.Ordinal) ||
            !string.Equals(folderId, retained.Snapshot.FolderId, StringComparison.Ordinal)) return null;
        if (!string.Equals(EvidenceDigest(retained.Snapshot), retained.EvidenceDigest, StringComparison.Ordinal))
            return null;
        try
        {
            await RequireActiveCandidateAccountAsync(connection, readTransaction, installationId, provider!,
                account!, namespaceId!, cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            return null;
        }
        return retained;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// The canonical evidence digest of one verified hierarchy: provider, account, namespace, root, folder, the
    /// complete ordered chain, the corpus root, completeness and the object-key coverage.
    /// </summary>
    private static string EvidenceDigest(SyncAuthorityCloudHierarchySnapshot snapshot)
    {
        var canonical = new StringBuilder();
        void Append(string value) => canonical.Append(value.Length).Append(':').Append(value).Append('|');
        Append(snapshot.Provider);
        Append(snapshot.ProviderAccountId);
        Append(snapshot.NamespaceId);
        Append(snapshot.RootBindingId);
        Append(snapshot.FolderId);
        Append(snapshot.CompleteToCorpusRoot ? "1" : "0");
        Append(snapshot.CorpusRootId ?? "");
        Append(snapshot.ObjectKeyCoverage.ToString());
        foreach (var id in snapshot.ChainFolderIds) Append(id);
        canonical.Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }
}
