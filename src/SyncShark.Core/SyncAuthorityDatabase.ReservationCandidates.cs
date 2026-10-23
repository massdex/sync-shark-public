using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    // This is a coarse candidate index, not a reservation or authority verdict.
    // It lives under the existing writer on one held planning connection. A new
    // transaction rechecks that connection's SQLite data_version before reuse;
    // external commits and rolled-back writes invalidate it. Status and final
    // authority are never cached.
    // The original final SQL and ordinal checks still decide every reservation.
    private SqliteTransaction? _executionReservationTransaction;
    private ExecutionReservationCandidates? _executionReservationCandidates;
    private bool _executionReservationRepresentationUnsupported;
    private SqliteConnection? _executionReservationConnection;
    private long _executionReservationDataVersion;
    internal static Action<int>? ReservationCandidateSnapshotLoadedForTest { get; set; }
    internal static Action<int>? ReservationCandidateSelectionForTest { get; set; }

    private void InvalidateExecutionReservationCandidates()
    {
        _executionReservationCandidates = null;
        _executionReservationRepresentationUnsupported = false;
    }

    private async Task BeginExecutionReservationTransactionAsync(SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var version = await ScalarLongAsync(connection, transaction, "PRAGMA data_version;", CancellationToken.None);
        if (!ReferenceEquals(connection, _executionReservationConnection) || version != _executionReservationDataVersion)
            InvalidateExecutionReservationCandidates();
        _executionReservationConnection = connection;
        _executionReservationDataVersion = version;
        _executionReservationTransaction = transaction;
    }

    private void EndExecutionReservationTransaction(bool retainCandidates = false)
    {
        if (!retainCandidates)
        {
            InvalidateExecutionReservationCandidates();
            _executionReservationConnection = null;
        }
        _executionReservationTransaction = null;
    }

    private void IncludeExecutionReservationMember(SqliteTransaction transaction,
        SyncAuthorityMember member, SyncAuthorityOperationKind kind)
    {
        if (ReferenceEquals(transaction, _executionReservationTransaction))
            _executionReservationCandidates?.Add(member.MemberId, member.RootBindingId,
                member.ExpectedPathKey, kind == SyncAuthorityOperationKind.CrossPairMove);
    }

    private async Task<IReadOnlySet<string>?> LoadExecutionReservationCandidateIdsAsync(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlySet<string> roots,
        IEnumerable<string> paths, CancellationToken token)
    {
        if (!ReferenceEquals(transaction, _executionReservationTransaction) ||
            _executionReservationRepresentationUnsupported)
            return null;
        if (_executionReservationCandidates is null)
        {
            var started = TimingStart();
            var candidates = new ExecutionReservationCandidates();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Complete headers from ALL statuses/history. Status/preconditions are
            // deliberately not cached; the final guard rereads them from SQLite.
            // Materialize only the two small header fields first. A direct indexed
            // join otherwise fetches each operation's large retained authority JSON
            // for every member, although this coarse lookup never consumes it.
            command.CommandText =
                """
                WITH headers AS MATERIALIZED (
                    SELECT operation_id,kind FROM operation
                )
                SELECT m.operation_member_id,m.root_binding_id,m.expected_path_key,o.kind
                FROM operation_member m JOIN headers o ON o.operation_id=m.operation_id
                WHERE m.expected_path_key IS NOT NULL;
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            try
            {
                while (await reader.ReadAsync(token))
                    candidates.Add(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3) == "cross_pair_move");
            }
            catch (Exception error) when (error is InvalidCastException or FormatException)
            {
                // An uncertain coarse representation cannot omit a candidate.
                // Use the unchanged query; do not swallow cancellation/SQLite errors.
                _executionReservationRepresentationUnsupported = true;
                return null;
            }
            _executionReservationCandidates = candidates;
            ReportTiming("reservation-candidate-snapshot", started);
            ReservationCandidateSnapshotLoadedForTest?.Invoke(candidates.Count);
        }
        var selected = _executionReservationCandidates.Select(roots, paths);
        ReservationCandidateSelectionForTest?.Invoke(selected.Count);
        return selected;
    }

    // The corresponding-included alias projection (OV637-3). The durable reservation collision boundary
    // compares root-relative paths within one root binding id; a parent "Parent/Child" and the child "Child"
    // reach one physical object through two different verified root bindings. This projection is derived
    // inside the caller's transaction from the active root bindings and the published pair definitions using
    // the shared correspondence decision, so the original ordinal comparison can run in one canonical
    // coordinate space. It never rewrites an operation, member, attempt, receipt or lease.
    private async Task<ReservationAliasProjection> LoadReservationAliasProjectionAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? cloudHierarchyEvidence,
        SyncAuthorityOperationPlan plan,
        CancellationToken cancellationToken)
    {
        var projection = new ReservationAliasProjection();
        var pairs = new Dictionary<string, SyncPair>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT pair_id, pair_json FROM sync_pair WHERE state<>'retired' AND pair_json IS NOT NULL;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var pair = ReadRetainedPair(reader.GetString(1), reader.GetString(0));
                if (pair is not null) pairs[pair.Id] = pair;
            }
        }
        if (pairs.Count < 2) return projection;
        var roots = new Dictionary<(string PairId, string Side), string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT root_binding_id, pair_id, side FROM root_binding WHERE state='active' " +
                "AND side IN ('local','dropbox');";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                roots[(reader.GetString(1), reader.GetString(2))] = reader.GetString(0);
        }
        var list = pairs.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        // D3/OV637-5: the alias fence can only be evaluated from a bound context. When NEITHER the caller nor a
        // retained observation supplies one, the missing alias edges must not be read as independent roots for
        // the exact operation whose own reservation could alias a live competitor on another published opaque
        // pair's root.
        var evidence = cloudHierarchyEvidence;
        // Path-descriptive endpoints (including Dropbox) establish ancestry from
        // their retained paths. Searching the entire activity history for opaque
        // hierarchy evidence for each local/Dropbox file provides no authority and
        // made the real two-pair run spend seconds per planned file.
        // Unknown endpoint representations retain the original conservative path;
        // any opaque provider still resolves evidence in this exact transaction.
        var needsOpaqueEvidence = list.Any(pair =>
        {
            var endpoints = SyncCloudPairConfiguration.SavedEndpoints(pair);
            return endpoints is null ||
                !endpoints.Left.IsLocal && !SyncEndpointProviders.Get(endpoints.Left.Kind).PathsDescribeAncestry ||
                !endpoints.Right.IsLocal && !SyncEndpointProviders.Get(endpoints.Right.Kind).PathsDescribeAncestry;
        });
        if (evidence is not { Count: > 0 } && needsOpaqueEvidence)
            evidence = await ResolveRetainedHierarchyEvidenceAsync(connection, transaction, list, cancellationToken);
        IReadOnlyDictionary<string, IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof>>
            opaqueProofs;
        if (evidence is not { Count: > 0 })
        {
            // No bound opaque context exists. A local/local correspondence still needs no proof, so the
            // projection below keeps its existing behaviour; the refusal below only closes the OPAQUE case.
            await RequireNoUnresolvedAliasFenceAsync(connection, transaction, plan, cancellationToken);
            opaqueProofs = new Dictionary<string, IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof>>(
                StringComparer.Ordinal);
        }
        else
            opaqueProofs = await BuildRetainedAliasProofsAsync(connection, transaction, list, evidence,
                cancellationToken);
        for (var left = 0; left < list.Length; left++)
        for (var right = left + 1; right < list.Length; right++)
        {
            if (!SyncPairPartitionPolicy.TryCorrespondingIncluded(list[left], list[right],
                    out _, out _, out _, out var endpointMatches,
                    ProofsFor(opaqueProofs, list[left]), ProofsFor(opaqueProofs, list[right]))) continue;
            // The endpoint bijection is carried from the single shared correspondence decision: each proven
            // endpoint pair names the ONE ordered endpoint of each pair that reaches the same physical object,
            // so a swapped local/local order and a provider-qualified side are followed exactly instead of
            // mapping inner local to outer local and dropbox to dropbox unconditionally. A cloud side is only
            // mapped because the shared decision already proved it from an independently bound hierarchy.
            foreach (var match in endpointMatches)
            {
                var innerPair = match.CandidateEndpointIsInner ? list[left] : list[right];
                var outerPair = match.CandidateEndpointIsInner ? list[right] : list[left];
                var innerIndex = match.CandidateEndpointIsInner ? match.CandidateEndpointIndex
                    : match.RetainedEndpointIndex;
                var outerIndex = match.CandidateEndpointIsInner ? match.RetainedEndpointIndex
                    : match.CandidateEndpointIndex;
                if (EndpointCompatibilitySide(innerPair, innerIndex) is not { } innerSide) continue;
                if (EndpointCompatibilitySide(outerPair, outerIndex) is not { } outerSide) continue;
                if (!roots.TryGetValue((innerPair.Id, innerSide), out var innerRoot)) continue;
                if (!roots.TryGetValue((outerPair.Id, outerSide), out var outerRoot)) continue;
                projection.AddEdge(innerRoot, outerRoot, match.Relative);
            }
        }
        return projection;
    }

    /// <summary>
    /// D3/OV637-5: refuses THIS operation when its own reservation could alias a live competing reservation on
    /// another published pair's opaque cloud root and NO bound alias context could be resolved. It never
    /// refuses for a pair that merely exists: the operation must itself carry a path or subtree reservation on
    /// an opaque provider root, another PUBLISHED pair must hold an active root on the same provider account,
    /// and that peer must really hold a live competing reservation on one of its own roots.
    /// </summary>
    private static async Task RequireNoUnresolvedAliasFenceAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncAuthorityOperationPlan plan, CancellationToken cancellationToken)
    {
        var ownRoots = plan.Members
            .Where(item => !string.IsNullOrWhiteSpace(item.ExpectedPathKey))
            .Select(item => item.RootBindingId)
            .Concat((plan.SubtreeReservations ?? []).Select(item => item.RootBindingId))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (ownRoots.Length == 0) return;
        var ownPairs = new HashSet<string>(StringComparer.Ordinal);
        var accounts = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT pair_id,json_extract(identity_json,'$.provider')," +
                "json_extract(identity_json,'$.providerAccountId') FROM root_binding " +
                "WHERE root_binding_id IN (SELECT value FROM json_each($roots));";
            command.Parameters.AddWithValue("$roots", JsonSerializer.Serialize(ownRoots));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ownPairs.Add(reader.GetString(0));
                if (reader.IsDBNull(1) || reader.IsDBNull(2)) continue;
                var provider = reader.GetString(1);
                if (!IsOpaqueProviderLiteral(provider)) continue;
                accounts.Add(provider.ToLowerInvariant() + "\u001f" + reader.GetString(2));
            }
        }
        if (accounts.Count == 0) return;
        var peerRoots = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT r.root_binding_id FROM root_binding r " +
                "JOIN sync_pair p ON p.pair_id=r.pair_id AND p.state<>'retired' AND p.pair_json IS NOT NULL " +
                "WHERE r.state='active' AND r.side IN ('local','dropbox') " +
                "AND lower(json_extract(r.identity_json,'$.provider'))||char(31)||" +
                "json_extract(r.identity_json,'$.providerAccountId') " +
                "IN (SELECT value FROM json_each($accounts));";
            command.Parameters.AddWithValue("$accounts", JsonSerializer.Serialize(accounts));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) peerRoots.Add(reader.GetString(0));
        }
        peerRoots.RemoveAll(root => ownRoots.Contains(root, StringComparer.Ordinal) ||
            ownPairs.Contains(root, StringComparer.Ordinal));
        if (peerRoots.Count == 0) return;
        var competing = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM operation_member m JOIN operation o ON o.operation_id=m.operation_id " +
            "WHERE m.root_binding_id IN (SELECT value FROM json_each($roots)) " +
            "AND m.expected_path_key IS NOT NULL AND o.operation_id<>$operation " +
            "AND o.status NOT IN ('completed','cancelled','superseded','blocked');",
            cancellationToken, ("$roots", JsonSerializer.Serialize(peerRoots)), ("$operation", plan.OperationId));
        if (competing == 0) return;
        throw new InvalidOperationException(
            "A related cloud pair holds a live reservation on an opaque root that could alias this exact " +
            "root/path, but no bound provider hierarchy context is available to decide the alias fence; the " +
            "operation is refused rather than treating the missing alias edges as independent roots.");
    }

    /// <summary>True when the provider literal is a registered path-opaque cloud provider.</summary>
    private static bool IsOpaqueProviderLiteral(string? provider) =>
        SyncEndpointProviders.TryGet(provider, out var definition) && definition?.ProviderId is not null &&
        !definition.PathsDescribeAncestry;


    /// <summary>
    /// The retained-authority-bound opaque cloud endpoint proofs of every published pair, or an empty map when
    /// no hierarchy evidence was supplied. The proof is built from the EXACT DB-held root identity and provider
    /// account inside this transaction, and the caller-supplied provider chain must bind and be complete for
    /// that identity; an absent, ambiguous or unbound proof yields no alias edge. A display path is never
    /// consulted. A retained row whose account no longer agrees fails closed for the whole projection.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof>>>
        BuildRetainedAliasProofsAsync(SqliteConnection connection, SqliteTransaction transaction,
            IReadOnlyList<SyncPair> list,
            IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? cloudHierarchyEvidence,
            CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof>>(
            StringComparer.Ordinal);
        // D2: when the caller supplies no fresh hierarchy evidence, the ONE bound validation context is built
        // from the RETAINED observations of the participating pairs through the same resolver and the same
        // connection/transaction. A retained observation is last-observed configuration evidence only, and it
        // is accepted solely while the current active root identity, generation, account and digests still
        // agree, so no fresh-context requirement for an effect is weakened here.
        var evidence = cloudHierarchyEvidence;
        if (evidence is not { Count: > 0 })
            evidence = await ResolveRetainedHierarchyEvidenceAsync(connection, transaction, list, cancellationToken);
        if (evidence is not { Count: > 0 }) return result;
        try
        {
            var authorities = await LoadActiveCloudAuthoritiesAsync(connection, transaction, "-",
                cancellationToken);
            foreach (var pair in list)
            {
                List<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof>? built = new();
                foreach (var counterpart in list)
                {
                    if (ReferenceEquals(counterpart, pair)) continue;
                    var side = await ReadPairOpaqueProofsAsync(connection, transaction, pair, counterpart, null,
                        authorities, evidence, cancellationToken);
                    if (side is null) { built = null; break; }
                    built.AddRange(side);
                }
                if (built is not null) result[pair.Id] = built;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or
                                          ArgumentException or FormatException)
        {
            // An unbound, ambiguous or account-disagreeing retained cloud authority proves nothing, so no
            // alias edge is derived rather than a half-bound one.
            result.Clear();
        }
        return result;
    }

    /// <summary>
    /// D2: the retained observations of the participating pairs as the bound validation context, or an empty
    /// list. Each is resolved inside the caller's own transaction, so the pair rows, the current authority and
    /// the retained evidence are read in one coherent snapshot and nothing is written.
    /// </summary>
    private static async Task<IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>> ResolveRetainedHierarchyEvidenceAsync(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<SyncPair> list,
        CancellationToken cancellationToken)
    {
        var installationId = await ScalarStringAsync(connection, transaction,
            "SELECT installation_id FROM installation ORDER BY installation_id LIMIT 1;", cancellationToken);
        if (installationId is null) return Array.Empty<SyncAuthorityCloudHierarchySnapshot>();
        var snapshots = new List<SyncAuthorityCloudHierarchySnapshot>();
        foreach (var pair in list)
        foreach (var side in new[] { SyncAuthorityCloudSides.LogicalLeft, SyncAuthorityCloudSides.LogicalRight })
        {
            var retained = await ResolveRetainedOpaqueHierarchyCoreAsync(connection, transaction, installationId,
                pair.Id, side, cancellationToken);
            if (retained is not null) snapshots.Add(retained.Snapshot);
        }
        return snapshots;
    }

    private static IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof>? ProofsFor(
        IReadOnlyDictionary<string, IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof>> proofs,
        SyncPair pair) => proofs.TryGetValue(pair.Id, out var value) ? value : null;

    /// <summary>The existing compatibility port of one ordered endpoint, or null when the pair is unreadable.</summary>
    private static string? EndpointCompatibilitySide(SyncPair pair, int endpointIndex)
    {
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(pair);
        if (endpoints is null || endpointIndex is < 0 or > 1) return null;
        return SyncEndpointContext.CompatibilityPort(endpoints.Left.Kind, endpoints.Right.Kind,
            endpointIndex == 0 ? SyncPairSide.Left : SyncPairSide.Right);
    }

    private sealed class ReservationAliasProjection
    {
        // nested (inner) root -> enclosing (outer) root and the relative prefix of the inner under the outer.
        private readonly Dictionary<string, string> _outerOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _prefixOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _innersOf = new(StringComparer.Ordinal);

        internal bool HasAliases => _outerOf.Count > 0;

        internal void AddEdge(string innerRoot, string outerRoot, string relativePrefix)
        {
            if (string.IsNullOrWhiteSpace(innerRoot) || string.IsNullOrWhiteSpace(outerRoot) ||
                string.Equals(innerRoot, outerRoot, StringComparison.Ordinal)) return;
            string normalized;
            try { normalized = PilotPath.NormalizeRelative(relativePrefix); }
            catch (ArgumentException) { return; }
            if (normalized.Length == 0) return;
            // One root is nested under exactly one verified enclosing root. A conflicting second edge is
            // dropped rather than merged, so the fence never compares two incompatible translations.
            if (_outerOf.TryGetValue(innerRoot, out var existing) &&
                !string.Equals(existing, outerRoot, StringComparison.Ordinal)) return;
            _outerOf[innerRoot] = outerRoot;
            _prefixOf[innerRoot] = normalized;
            if (!_innersOf.TryGetValue(outerRoot, out var inners))
                _innersOf[outerRoot] = inners = new List<string>();
            if (!inners.Contains(innerRoot, StringComparer.Ordinal)) inners.Add(innerRoot);
        }

        private string OuterMost(string root)
        {
            var current = root;
            for (var guard = 0; guard < 64 && _outerOf.TryGetValue(current, out var outer); guard++)
                current = outer;
            return current;
        }

        /// <summary>Every root that shares a component with any requested root, including the requested roots.</summary>
        internal IReadOnlySet<string> ExpandRoots(IReadOnlySet<string> roots)
        {
            if (!HasAliases) return roots;
            var members = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var root in AllRoots())
            {
                var component = OuterMost(root);
                if (!members.TryGetValue(component, out var set))
                    members[component] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(root);
            }
            var result = new HashSet<string>(roots, StringComparer.Ordinal);
            foreach (var root in roots)
                if (members.TryGetValue(OuterMost(root), out var set)) result.UnionWith(set);
            return result;
        }

        private IEnumerable<string> AllRoots()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in _outerOf.Keys) if (seen.Add(key)) yield return key;
            foreach (var value in _outerOf.Values) if (seen.Add(value)) yield return value;
        }

        /// <summary>One root-relative path in the enclosing (canonical) root's coordinate space.</summary>
        internal (string Root, string Path) Canonicalize(string root, string path)
        {
            var currentRoot = root;
            var currentPath = path;
            for (var guard = 0; guard < 64; guard++)
            {
                if (!_outerOf.TryGetValue(currentRoot, out var outer)) break;
                if (!_prefixOf.TryGetValue(currentRoot, out var prefix)) break;
                try { currentPath = PilotPath.NormalizeRelative(prefix + "/" + currentPath); }
                catch (ArgumentException) { break; }
                currentRoot = outer;
            }
            return (currentRoot, currentPath);
        }

        /// <summary>Every equivalent (root, path) this coordinate names through the alias graph.</summary>
        internal IReadOnlyList<(string Root, string Path)> EnumerateEquivalent(string root, string path)
        {
            var results = new List<(string Root, string Path)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<(string Root, string Path)>();
            stack.Push((root, path));
            while (stack.Count > 0)
            {
                var (currentRoot, currentPath) = stack.Pop();
                string normalized;
                try { normalized = PilotPath.NormalizeRelative(currentPath); }
                catch (ArgumentException) { continue; }
                if (!seen.Add(currentRoot + "\u0000" + normalized)) continue;
                results.Add((currentRoot, normalized));
                if (_outerOf.TryGetValue(currentRoot, out var outer) &&
                    _prefixOf.TryGetValue(currentRoot, out var prefix))
                    stack.Push((outer, prefix + "/" + normalized));
                if (!_innersOf.TryGetValue(currentRoot, out var inners)) continue;
                foreach (var inner in inners)
                {
                    if (!_prefixOf.TryGetValue(inner, out var innerPrefix)) continue;
                    if (!normalized.StartsWith(innerPrefix + "/", StringComparison.OrdinalIgnoreCase)) continue;
                    var stripped = normalized[(innerPrefix.Length + 1)..];
                    if (stripped.Length > 0) stack.Push((inner, stripped));
                }
            }
            return results;
        }
    }

    private sealed class ExecutionReservationCandidates
    {
        private readonly Dictionary<string, RootCandidates> _roots = new(StringComparer.Ordinal);
        internal int Count { get; private set; }

        internal void Add(string id, string root, string? path, bool crossPair)
        {
            if (path is null) return;
            if (!_roots.TryGetValue(root, out var entries))
                _roots.Add(root, entries = new RootCandidates());
            string normalized;
            try { normalized = PilotPath.NormalizeRelative(path); }
            catch (ArgumentException)
            {
                // Retain malformed/unsupported historical representations for
                // the final transactional guard; an index must not hide them.
                if (entries.Wide.Add(id)) Count++;
                return;
            }
            if (!entries.Paths.TryGetValue(normalized, out var members))
                entries.Paths.Add(normalized, members = new HashSet<string>(StringComparer.Ordinal));
            if (members.Add(id)) Count++;
            // A subtree can reserve descendants whose paths differ. Ordinary
            // Unicode members are exact ordinal candidates, not every sibling.
            if (crossPair) entries.Wide.Add(id);
        }

        internal IReadOnlySet<string> Select(IReadOnlySet<string> roots, IEnumerable<string> paths)
        {
            var requested = paths.Select(PilotPath.NormalizeRelative).ToArray();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                if (!_roots.TryGetValue(root, out var entries)) continue;
                ids.UnionWith(entries.Wide);
                foreach (var path in requested)
                    if (entries.Paths.TryGetValue(path, out var members)) ids.UnionWith(members);
            }
            return ids;
        }

        private sealed class RootCandidates
        {
            // Same normalization/comparer as SameRootAndPath. No Unicode FormC
            // folding: composed/decomposed names retain the existing contract.
            internal readonly Dictionary<string, HashSet<string>> Paths = new(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Wide = new(StringComparer.Ordinal);
        }
    }
}

