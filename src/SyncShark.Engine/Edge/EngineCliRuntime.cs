using Massdex.SyncShark.Engine.Run;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>The CLI runtime for `engine …` commands (section 9.2). Construction does no state access; the old-command
/// members throw SyncCliUnsupportedException.</summary>
public sealed class EngineCliRuntime : ISyncCliRuntime, IEngineCliAccess, IEngineHostReads
{
    private readonly EngineOperations? _operations;
    private readonly int _width = RunLog.HumanWidth;
    public EngineCliRuntime() { }
    public EngineCliRuntime(int width) => _width = width;
    public EngineCliRuntime(EngineOperations operations) =>
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));

    public Task<SyncCliStatusData> LoadStatusAsync(CancellationToken cancellationToken) => throw OldCommand();
    public Task<SyncCliAccountListData> LoadAccountsAsync(CancellationToken cancellationToken) => throw OldCommand();
    public Task<SyncCliPairListData> LoadPairsAsync(CancellationToken cancellationToken) => throw OldCommand();
    public Task<SyncCliPairData> LoadPairAsync(string pairId, CancellationToken cancellationToken) => throw OldCommand();
    public Task<SyncCliActivityListData> LoadActivityAsync(string? accountId, int limit, int offset,
        CancellationToken cancellationToken, SyncActivityQuery? query, string? search,
        string? filter, string? pairId, string? itemType, DateTimeOffset? sinceUtc) => throw OldCommand();
    public Task<SyncCliSettingsData> LoadSettingsAsync(CancellationToken cancellationToken) => throw OldCommand();
    public Task<SyncCliPlanData> LoadPlanAsync(string pairId, CancellationToken cancellationToken, int limit, int offset) =>
        throw OldCommand();
    public Task<SyncCliRunData> RunPairAsync(string pairId, Action<string> progress, CancellationToken cancellationToken) =>
        throw OldCommand();

    /// <summary>Runs one engine invocation and maps EngineRequestException to a usage error. A preference command is
    /// host-only: this standalone runtime has no verified host-bound operations instance, so it reports the command
    /// unavailable instead of constructing a local operations instance.</summary>
    public async Task<object> RunEngineCommandAsync(SyncCliInvocation invocation, Action<string> progress,
        CancellationToken ct)
    {
        if (_operations is null && EngineCliCatalog.CommandKey(invocation.Command) is
            (EngineCommandKey.PreferencesShow or EngineCommandKey.PreferencesSet))
            throw new SyncCliUnsupportedException(EngineCliCatalog.Message(EngineMessageKey.NeedsEngine));
        try
        {
            return await new EngineCommands(_operations ?? new EngineOperations(SyncSharkRuntimePaths.ProductRoot,
                progress, _width)).RunAsync(invocation, ct);
        }
        catch (EngineRequestException error)
        {
            throw new SyncCliUsageException(error.Message);
        }
    }

    /// <summary>Answers one host-owned read from the host's own operations, with no product lock. A missing pair is
    /// the same usage refusal the direct path reports.</summary>
    public async Task<object> ReadHostAsync(EngineHostReadKind kind, SyncCliInvocation invocation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await EngineHostReads.ReadAsync(
                _operations ?? new EngineOperations(SyncSharkRuntimePaths.ProductRoot, static _ => { }),
                kind, invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (EngineRequestException error)
        {
            throw new SyncCliUsageException(error.Message);
        }
    }

    /// <summary>No resources to release: EngineOperations owns each call's lock/store (8.6; 9.2).</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static SyncCliUnsupportedException OldCommand() =>
        new(EngineCliCatalog.Message(EngineMessageKey.OldCommand));
}
