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
    public Task<SyncRecoveryUsageSummary> LoadRecoveryUsageAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RecoveryMapping.Usage(pairId, _store.Recoveries(pairId), _time.GetUtcNow(), _settings));
    }
}
