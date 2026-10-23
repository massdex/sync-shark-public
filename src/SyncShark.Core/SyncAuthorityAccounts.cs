namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost
{
    private async Task<IReadOnlyList<string>?> AccountPairIdsAsync(string? connectionId, CancellationToken cancellationToken)
    {
        if (connectionId is null) return null;
        if (LoadAccountBinding(connectionId) is null) return [];
        return (await LoadPairsAsync(cancellationToken)).Pairs.Where(pair => PairConnectionIds(pair).Contains(connectionId, StringComparer.Ordinal))
            .Select(pair => pair.Id).ToArray();
    }

    public IReadOnlyList<string> PairConnectionIds(SyncPair pair)
    {
        if (pair.EndpointDocument is { } document)
            return new[] { document.Left, document.Right }.Where(endpoint => endpoint is { IsLocal: false })
                .Select(endpoint => endpoint!.ConnectionId).Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        if (IsLocalPair(pair)) return [];
        var connectionId = string.IsNullOrEmpty(pair.ConnectionId) ? connectionStore.Load()?.ConnectionId : pair.ConnectionId;
        return connectionId is null ? [] : [connectionId];
    }

    public bool IsPairAccountPaused(SyncPair pair) => PairConnectionIds(pair).Any(IsAccountPaused);

    // This gates NEW work only. Recovery may still verify an already-issued effect
    // while paused, but must never turn an unissued plan into a fresh transfer.
    internal string? NewSyncAdmissionReason(SyncPair pair)
    {
        if (!pair.Enabled) return "This folder pair is paused. Resume the pair before starting new sync work.";
        if (PauseStateProjection.GlobalPaused(new UiPreferencesStore(stateAuthority).Load(), DateTimeOffset.UtcNow))
            return "Sync is paused globally. Resume sync before starting new work.";
        return IsPairAccountPaused(pair)
            ? "This pair's account is paused. Resume the account before starting new sync work."
            : null;
    }

    public bool IsAccountPaused(string? connectionId)
    {
        var id = string.IsNullOrEmpty(connectionId) ? connectionStore.Load()?.ConnectionId : connectionId;
        return id is not null && (new UiPreferencesStore(stateAuthority).Load().AccountPauses ?? [])
            .Any(item => item.ConnectionId == id && item.IsActive);
    }

    private async Task StopAccountRuntimeAsync(string connectionId, CancellationToken cancellationToken)
    {
        var pairIds = (await LoadPairsAsync(cancellationToken)).Pairs
            .Where(pair => PairConnectionIds(pair).Contains(connectionId, StringComparer.Ordinal)).Select(pair => pair.Id).ToHashSet();
        await _monitorConfigurationGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var monitor in _localMonitors.Where(item => pairIds.Contains(item.PairId)).ToArray())
            { await monitor.DisposeAsync(); _localMonitors.Remove(monitor); }
            foreach (var monitor in _dropboxMonitors.Where(item => pairIds.Contains(item.PairId)).ToArray())
            { await monitor.DisposeAsync(); _dropboxMonitors.Remove(monitor); }
        }
        finally { _monitorConfigurationGate.Release(); }
        var providers = new List<WindowsCloudFilesProvider>();
        lock (_cloudProviderGate)
            foreach (var id in pairIds)
                if (_cloudProviders.Remove(id, out var provider)) providers.Add(provider);
        foreach (var provider in providers) provider.Dispose();
        _accountTokens.TryRemove(connectionId, out _);
    }
}
