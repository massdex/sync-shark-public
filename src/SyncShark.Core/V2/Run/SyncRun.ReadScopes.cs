namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private sealed record ScopeRead(IReadOnlyList<string> Touched, IReadOnlyList<string> Incomplete,
        IReadOnlyDictionary<string, string> Reasons, bool RootSettles, IReadOnlyList<string> Observed);

    // Targeted inventory authority is shared by scan retries and reconciliation batches.
    private async Task<ScopeRead> ReadScopesAsync(PairDef pair, RootDef root, Side side,
        IInventorySource inventory, IReadOnlyList<string> scopes, string cursor, long gen,
        Func<string, bool> include, Action<string> changed, ProgressThrottle throttle, CancellationToken ct)
    {
        if (RehearsalEndpoints.Unwrap(inventory) is LocalInventory local)
            return await ReadLocalScopesAsync(pair, root, inventory, local, scopes, cursor, gen, include, changed, ct);
        if (RehearsalEndpoints.Unwrap(inventory) is DropboxInventory dropbox && _store is SqliteStateStore sql)
            return await ReadDropboxScopesAsync(root, side, inventory, dropbox, sql, scopes, cursor, gen, include, changed, ct);
        return await ReadGenericScopesAsync(root, inventory, scopes, gen, include, changed, throttle, ct);
    }

    private async Task<ScopeRead> ReadLocalScopesAsync(PairDef pair, RootDef root, IInventorySource inventory,
        LocalInventory local, IReadOnlyList<string> scopes, string cursor, long gen,
        Func<string, bool> include, Action<string> changed, CancellationToken ct)
    {
        var incomplete = new List<string>();
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Exact-path reads never enumerate descendants of the admitted scopes.
        var before = _store.Live(root.RootId, scopes);
        var reread = RehearsalEndpoints.Apply(inventory,
            await local.ChangesForPathsAsync(scopes, cursor, gen, include, ct), include);
        if (reread.CursorInvalid)
        {
            var reason = reread.IncompleteReasons.GetValueOrDefault(string.Empty) ?? local.LastFallbackReason;
            RecordIncomplete(pair.PairId, scopes, scopes.ToDictionary(path => path, _ => reason!), incomplete, reasons);
            return new([], incomplete, reasons, false, []);
        }
        _store.WriteDelta(reread with { ChangedObject = changed });
        foreach (var change in reread.Changes)
            if (change.ObjectId.Length > 0) changed(change.ObjectId);
        RecordIncomplete(pair.PairId, reread.IncompleteSubtrees, reread.IncompleteReasons, incomplete, reasons);
        CachedIdentityRefresh.SettleObserved(_store, pair.PairId, scopes, reread.IncompleteSubtrees,
            root.RootId, before, incomplete, reasons);
        return CompleteScopeRead(reread.Changes.Select(change => change.Path), scopes, include, incomplete, reasons);
    }

    private async Task<ScopeRead> ReadDropboxScopesAsync(RootDef root, Side side, IInventorySource inventory,
        DropboxInventory dropbox, SqliteStateStore sql, IReadOnlyList<string> scopes, string cursor, long gen,
        Func<string, bool> include, Action<string> changed, CancellationToken ct)
    {
        var incomplete = new List<string>();
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Prefix hydration preserves the root's complete tombstone-classification header.
        try
        {
            sql.BeginPrefixHydration(root.RootId, ct);
            var failures = await RehearsalEndpoints.HydrateAsync(inventory, dropbox, scopes, rows =>
            {
                sql.StagePrefixHydration(root.RootId, rows.Where(row => include(row.Path))
                    .Select(row => (row.Path, row.Entry is null ? null : DropboxItems.ToItem(row.Entry))).ToArray());
                return Task.CompletedTask;
            }, ct);
            bool observed(string path) => include(path) && !DropboxPathIdentity.InScope(failures, path);
            sql.InstallPrefixHydration(root, side, gen, scopes, observed, cursor, ct, changed);
            foreach (var failure in failures)
            {
                incomplete.Add(failure.Key);
                reasons[failure.Key] = failure.Value;
            }
        }
        catch (Exception error) when (ScopeFailure(error, ct))
        {
            incomplete.AddRange(scopes);
            RunLog.Trace("Targeted inventory remains incomplete: " + error.Message);
        }
        return CompleteScopeRead([], scopes, include, incomplete, reasons);
    }

    private async Task<ScopeRead> ReadGenericScopesAsync(RootDef root, IInventorySource inventory,
        IReadOnlyList<string> scopes, long gen, Func<string, bool> include, Action<string> changed,
        ProgressThrottle throttle, CancellationToken ct)
    {
        // Generic inventories narrow snapshot authority independently of ancestor traversal.
        bool within(string path) => scopes.Any(scope => scope.Length == 0 || PairPath.IsSelfOrUnder(path, scope));
        bool traverse(string path) => include(path) && (within(path) || scopes.Any(scope => PairPath.IsSelfOrUnder(scope, path)));
        var reread = await ReadFullAsync(inventory, gen, traverse, root, throttle, ct);
        _store.WriteSnapshot(reread with
        {
            IncludesPath = path => include(path) && within(path) && (reread.IncludesPath?.Invoke(path) ?? true),
            ChangedObject = changed
        });
        var incomplete = reread.IncompleteSubtrees.ToList();
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in incomplete)
            if (reread.IncompleteReasons.GetValueOrDefault(path) is { Length: > 0 } detail) reasons[path] = detail;
        return CompleteScopeRead(reread.Items.Select(item => item.Path), scopes, include, incomplete, reasons);
    }

    private static ScopeRead CompleteScopeRead(IEnumerable<string> touched, IReadOnlyList<string> scopes,
        Func<string, bool> include, IReadOnlyList<string> incomplete, IReadOnlyDictionary<string, string> reasons) =>
        new(touched.Concat(scopes.Where(scope => scope.Length > 0)).ToArray(), incomplete, reasons,
            !incomplete.Contains(string.Empty), SuccessfullyObservedScopes(scopes, include, incomplete).ToArray());
}
