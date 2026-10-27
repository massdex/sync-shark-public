namespace Massdex.SyncShark.V2;

public sealed partial class V2SyncHost
{
    /// <inheritdoc />
    public Task<IReadOnlyList<SyncRecoveryPairChoice>> LoadRecoveryPairChoicesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SyncRecoveryPairChoice>>(
            _store.Pairs().Select(pair => new SyncRecoveryPairChoice(pair.PairId, pair.Name, true)).ToArray());
    }

    /// <inheritdoc />
    public Task<SyncRecoveryCatalogPage> LoadRecoveryCatalogPageAsync(string pairId, int archiveOffset = 0,
        string? deletedCursor = null, bool includeDropboxDeletedItems = false, int pageSize = 200, string? search = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pair = _store.Pair(pairId) ?? throw new InvalidDataException("This folder pair is no longer available.");
        return Task.FromResult(RecoveryMapping.CatalogPage(pair, _store.Recoveries(pairId), archiveOffset, pageSize,
            search));
    }

    /// <inheritdoc />
    public async Task<SyncRecoveryEffectPreview> PreviewRecoveryAsync(SyncRecoveryCatalogItem item, bool restoreAsCopy,
        CancellationToken cancellationToken = default)
    {
        var result = await RecoveryItemAsync(item, restoreAsCopy, true, cancellationToken).ConfigureAwait(false);
        return new SyncRecoveryEffectPreview(item.PairId, item.Key, item.Source, result.Path,
            result.Destination ?? result.Path, restoreAsCopy,
            result.Classification is "SKIP-PRESENT" or "CONFLICT",
            result.Classification + ": " + result.Detail);
    }

    /// <inheritdoc />
    public async Task<SyncRecoveryRestoreResult> RestoreRecoveryAsync(SyncRecoveryCatalogItem item, bool restoreAsCopy,
        CancellationToken cancellationToken = default)
    {
        var result = await RecoveryItemAsync(item, restoreAsCopy, false, cancellationToken).ConfigureAwait(false);
        return new SyncRecoveryRestoreResult(item.PairId, item.Key, result.Destination ?? result.Path,
            result.Classification + ": " + result.Detail);
    }

    private async Task<RecoveryRestoreItem> RecoveryItemAsync(SyncRecoveryCatalogItem item, bool asCopy, bool dryRun,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.PairId) || string.IsNullOrWhiteSpace(item.Key))
            throw new ArgumentException("Select one retained Recovery item before restoring.", nameof(item));
        // SyncRun's pair admission is the sole conflict owner, shared by manual and automatic work.
        var results = await _run.RestoreRecoveryPageAsync(item.PairId, new RecoveryQuery(Id: item.Key, Limit: 1),
            asCopy, dryRun, ct).ConfigureAwait(false);
        return results.SingleOrDefault() ?? throw new InvalidDataException(
            "This retained Recovery item is no longer available. Refresh Recovery and select another version.");
    }

    /// <inheritdoc />
    public Task<SyncRecoveryUsageSummary> LoadRecoveryUsageAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RecoveryMapping.Usage(pairId, _store.Recoveries(pairId), _time.GetUtcNow(), _settings));
    }
}
