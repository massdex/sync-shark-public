namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    internal async Task<DateRepairReport> RepairDatesAsync(string pairId, bool dryRun, CancellationToken ct,
        DateOnly? localDate = null, string after = "")
    {
        var pair = RequirePair(pairId);
        if (_store is not SqliteStateStore sqlite)
            throw new SyncCliUnsupportedException("Date repair requires the v2 SQLite store.");
        if (!((pair.A.Provider == SyncProviders.Local && pair.B.Provider == SyncProviders.Dropbox) ||
            (pair.B.Provider == SyncProviders.Local && pair.A.Provider == SyncProviders.Dropbox)))
            throw new SyncCliUsageException("Date repair needs one local and one Dropbox side. Choose a local/Dropbox pair.");
        var cloud = pair.A.Provider == SyncProviders.Dropbox ? pair.A : pair.B;
        var client = await new DropboxRootClients(DropboxAccounts).OpenAsync(cloud, ct).ConfigureAwait(false);
        async Task PageLock(Func<Task> body)
        {
            if (!await _runGate.WaitAsync(0, ct).ConfigureAwait(false))
                throw new SyncCliBusyException("A pair operation is running. Let it finish, then resume dates repair from the report's page cursor.");
            try
            {
                await _run.WithDateRepairLockAsync(async () => { await body(); return true; }, ct);
            }
            finally { _runGate.Release(); }
        }
        return await DateRepair.RunAsync(sqlite, pair,
            (_, _) => throw new InvalidOperationException("Date repair uses bounded metadata pages, not individual root checks."),
            _productRoot, dryRun, _composition?.Endpoints.ProductOwnedRelative(pair) ?? [], ct, localDate, after, PageLock,
            async (paths, token) =>
            {
                var entries = await client.ReadMetadataPageAsync(paths, token);
                return entries.ToDictionary(row => row.Key, row => row.Value is { ClientModifiedUtc: not null } entry
                    ? DropboxItems.ToItem(entry) : null);
            }).ConfigureAwait(false);
    }
}
