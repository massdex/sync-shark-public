namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    internal async Task<DateRepairReport> RepairDatesAsync(string pairId, bool dryRun, CancellationToken ct,
        DateOnly? localDate = null, string after = "", DateRepairBound? from = null, DateRepairBound? to = null, string? excludeList = null, bool reportMismatch = false,
        IReadOnlyList<DateProvenanceSource>? provenanceSources = null)
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
            try
            {
                await _run.WithPairAdmissionAsync(pairId, async () => { await body(); return true; }, ct, Console.Error.WriteLine,
                    immediate: true);
            }
            catch (SyncCliBusyException)
            {
                throw new SyncCliBusyException("A pair operation is running. Let it finish, then resume dates repair from the report's page cursor.");
            }
        }
        return await DateRepair.RunAsync(sqlite, pair,
            (_, _) => throw new InvalidOperationException("Date repair uses bounded metadata pages, not individual root checks."),
            _productRoot, dryRun, _composition?.Endpoints.ProductOwnedRelative(pair) ?? [], ct, DateRepairInputs.Exclusions(excludeList), DateRepairInputs.Provenance(provenanceSources),
            provenanceSources is { Count: > 0 }, StorageFailureClassifier.IsStateDamage, StorageFailureClassifier.IsDirectStateDamage,
            StorageFailureClassifier.IsBusy, localDate, after, PageLock,
            async (paths, failed, token) =>
            {
                var entries = await client.ReadMetadataPageAsync(paths, token, failed);
                return entries.ToDictionary(row => row.Key, row => row.Value is { ClientModifiedUtc: not null } entry
                    ? DropboxItems.ToItem(entry) : null);
            }, from, to, excludeList, reportMismatch, provenanceSources).ConfigureAwait(false);
    }
    internal async Task<DateRepairReport> RestoreDatesAsync(string audit, bool dryRun, CancellationToken ct)
    {
        var pair = RequirePair(DateRepair.AuditPair(audit));
        if (_store is not SqliteStateStore sqlite) throw new SyncCliUnsupportedException("Date restore requires SQLite.");
        async Task PageLock(Func<Task> body)
        {
            await _run.WithPairAdmissionAsync(pair.PairId, async () => { await body(); return true; }, ct, Console.Error.WriteLine);
        }
        return await DateRepair.RestoreAsync(sqlite, pair, audit, _productRoot, dryRun,
            _composition?.Endpoints.ProductOwnedRelative(pair) ?? [], ct, StorageFailureClassifier.IsStateDamage, StorageFailureClassifier.IsDirectStateDamage, PageLock).ConfigureAwait(false);
    }
}
