namespace Massdex.SyncShark;

// The running-application operations surface. All command semantics come from SyncCliOperationsBase, so
// the CLI that talks to the window behaves exactly like the CLI that owns the lock itself.
internal sealed class CliAppRuntime : SyncCliOperationsBase
{
    private readonly SyncAuthorityRuntimeHost _host;
    private readonly PilotStateAuthority _stateAuthority;
    private readonly Func<bool> _runActive;
    private readonly Func<bool> _runPaused;
    private readonly Action _showWindow;
    private readonly Func<Task> _exitApplication;
    private readonly Func<string, CancellationToken, Task<SyncCliControlData>>? _applyUpdate;
    private readonly Func<string, CancellationToken, Task<SyncCliControlData>>? _configureDiscovery;
    private readonly Func<CancellationToken, Task>? _beforeSettingsSave;
    private readonly Func<SyncCliSettingsData, SyncCliSettingsData, CancellationToken, Task<bool>>? _applySettings;
    private readonly Func<Task>? _completeSettingsSave;
    private readonly Func<SyncCliInvocation, CancellationToken, Task<object>>? _appAction;
    private readonly Func<CancellationToken, Task>? _beforePause;
    private readonly Func<Task>? _afterPause;
    private readonly Func<Task<IReadOnlyList<SyncCliAttentionItemData>>>? _attentionItems;

    internal CliAppRuntime(SyncAuthorityRuntimeHost host, PilotStateAuthority stateAuthority, Func<bool> runActive,
        Func<bool> runPaused, Action showWindow, Func<Task> exitApplication,
        Func<string, CancellationToken, Task<SyncCliControlData>>? applyUpdate = null,
        Func<string, CancellationToken, Task<SyncCliControlData>>? configureDiscovery = null,
        Func<CancellationToken, Task>? beforeSettingsSave = null,
        Func<SyncCliSettingsData, SyncCliSettingsData, CancellationToken, Task<bool>>? applySettings = null,
        Func<Task>? completeSettingsSave = null,
        Func<SyncCliInvocation, CancellationToken, Task<object>>? appAction = null,
        Func<CancellationToken, Task>? beforePause = null, Func<Task>? afterPause = null,
        Func<Task<IReadOnlyList<SyncCliAttentionItemData>>>? attentionItems = null)
    {
        _host = host;
        _stateAuthority = stateAuthority;
        _runActive = runActive;
        _runPaused = runPaused;
        _showWindow = showWindow;
        _exitApplication = exitApplication;
        _applyUpdate = applyUpdate;
        _configureDiscovery = configureDiscovery;
        _beforeSettingsSave = beforeSettingsSave;
        _applySettings = applySettings;
        _completeSettingsSave = completeSettingsSave;
        _appAction = appAction;
        _beforePause = beforePause;
        _afterPause = afterPause;
        _attentionItems = attentionItems;
    }

    protected override SyncAuthorityRuntimeHost Host => _host;
    protected override PilotStateAuthority StateAuthority => _stateAuthority;
    protected override bool ApplicationOwnsRuntime => true;
    protected override bool HostRunActive => _host.HasActiveRun;
    protected override bool HostRunPaused => _host.ActiveRun?.Paused == true;
    public override string Mode => "running-application";

    protected override Task<IReadOnlyList<SyncCliAttentionItemData>> LoadAttentionItemsCoreAsync(CancellationToken cancellationToken) =>
        _attentionItems?.Invoke() ?? base.LoadAttentionItemsCoreAsync(cancellationToken);

    protected override Task ShowApplicationCoreAsync(CancellationToken cancellationToken)
    {
        _showWindow();
        return Task.CompletedTask;
    }

    protected override Task ExitApplicationCoreAsync(CancellationToken cancellationToken) => _exitApplication();

    public override Task<SyncCliControlData> UpdatesApplyAsync(string version, CancellationToken cancellationToken) =>
        _applyUpdate is null ? base.UpdatesApplyAsync(version, cancellationToken) : _applyUpdate(version, cancellationToken);

    public override Task<SyncCliControlData> ConfigureDiscoveryAsync(string mode, CancellationToken cancellationToken) =>
        _configureDiscovery is null ? base.ConfigureDiscoveryAsync(mode, cancellationToken) : _configureDiscovery(mode, cancellationToken);

    protected override Task BeforeSaveSettingsAsync(CancellationToken cancellationToken) =>
        _beforeSettingsSave?.Invoke(cancellationToken) ?? base.BeforeSaveSettingsAsync(cancellationToken);
    protected override Task<bool> ApplySavedSettingsAsync(SyncCliSettingsData previous, SyncCliSettingsData saved,
        CancellationToken cancellationToken) => _applySettings?.Invoke(previous, saved, cancellationToken) ??
        base.ApplySavedSettingsAsync(previous, saved, cancellationToken);
    protected override Task CompleteSettingsSaveAsync() =>
        _completeSettingsSave?.Invoke() ?? base.CompleteSettingsSaveAsync();

    protected override Task BeforePauseChangeAsync(CancellationToken cancellationToken) =>
        _beforePause?.Invoke(cancellationToken) ?? base.BeforePauseChangeAsync(cancellationToken);
    protected override Task CompletePauseChangeAsync() =>
        _afterPause?.Invoke() ?? base.CompletePauseChangeAsync();

    public override ValueTask DisposeAsync()
    {
        // The application owns the host and the product lock; the bridge must never dispose either.
        return ValueTask.CompletedTask;
    }

    public override Task<object> ExecuteAppActionAsync(SyncCliInvocation invocation, CancellationToken cancellationToken) =>
        _appAction?.Invoke(invocation, cancellationToken) ?? base.ExecuteAppActionAsync(invocation, cancellationToken);
}
