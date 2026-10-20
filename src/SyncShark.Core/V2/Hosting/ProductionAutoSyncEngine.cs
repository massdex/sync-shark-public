namespace Massdex.SyncShark.V2;

/// <summary>The one production composition owned by the background process, never by its clients.</summary>
public sealed class ProductionAutoSyncEngine : IAutoSyncHostEngine
{
    private readonly EngineComposition _composition;
    private readonly V2CliRuntime _cli;
    private readonly SyncAutomationStore _automation;
    private readonly UiPreferencesStore _preferences;
    private readonly AutoSync _auto;
    private readonly SemaphoreSlim _preferencesGate = new(1, 1);
    private readonly HttpClient _http;
    private readonly HostNoticeChoiceQueue _choices;
    private readonly CloudFilesLifetime _cloudFiles;
    private readonly StorageSettlementService _settlement;

    public ProductionAutoSyncEngine(PilotInstanceLock authority, string root)
    {
        _automation = new SyncAutomationStore(authority.StateAuthority);
        _preferences = new UiPreferencesStore(authority.StateAuthority);
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = EngineComposition.DropboxHttpTimeoutFor(V2Settings.Default) };
        try
        {
            _composition = EngineComposition.Open(new EngineHostInputs(root, authority.StateAuthority, _http),
                V2Settings.Default, TimeProvider.System);
            _cloudFiles = new CloudFilesLifetime(_composition.Endpoints.CreateCloudFiles,
                pair => Directory.Exists(pair.A.RootPath) &&
                    WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(pair.A.RootPath) is not null &&
                    !WindowsCloudFilesProvider.IsStatusOnlySyncRoot(PairMapping.ToSyncPair(pair, false, V2Settings.Default)));
            // Independent of run/pause state: retained pointers still need foreground hydration.
            _cloudFiles.Reconnect(_composition.Store.Pairs(), CancellationToken.None, (pair, error) =>
                _composition.Store.AddNotices(new[] { new Notice(pair.PairId, 38, NoticeKind.Info, string.Empty,
                    "Cloud Files reconnect failed: " + error.Message +
                    " Review the retained root binding and Retry. No registration was removed.",
                    "cloud-files-binding-needs-review") }));
            _cli = V2CliRuntime.Borrow(authority, _composition, root);
            _choices = new HostNoticeChoiceQueue(_composition.Store, ExecuteCliAsync);
            _auto = _composition.StartAutoSync(Paused,
                () => TimeSpan.FromMinutes(_automation.Load().IntervalMinutes));
            _settlement = new StorageSettlementService((SqliteStateStore)_composition.Store, _cloudFiles);
            _settlement.Start();
        }
        catch { _cloudFiles?.Dispose(); _composition?.Dispose(); _http.Dispose(); throw; }
    }

    private bool Paused(PairDef pair)
    {
        var saved = _preferences.Load();
        var now = DateTimeOffset.UtcNow;
        return !_automation.Load().Enabled || PauseStateProjection.GlobalPaused(saved, now)
            || PauseStateProjection.AccountPaused(saved, pair.A.AccountId, now)
            || PauseStateProjection.AccountPaused(saved, pair.B.AccountId, now);
    }

    public async Task<object> ExecuteAsync(AutoSyncHostRequest request, CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case "status":
                var states = _auto.States.ToDictionary(state => state.PairId, StringComparer.Ordinal);
                return new { running = true, automationEnabled = _automation.Load().Enabled, intervalMinutes = _automation.Load().IntervalMinutes,
                    pairs = _composition.Store.Pairs().Select(pair =>
                    {
                        states.TryGetValue(pair.PairId, out var state);
                        return new { pair.PairId, pair.Name, pair.Enabled, state = state?.Status.ToString() ?? "starting",
                            lastResult = _composition.Store.ReadRunHistory(pair.PairId, 1).FirstOrDefault(),
                            lastChecked = state?.LastReport?.CompletedUtc,
                            retryDeadline = state?.RetryAt, nextRun = state?.NextRunUtc, reason = state?.Reason };
                    }).ToArray() };
            case "enable":
            case "disable":
                await _preferencesGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (request.All)
                    {
                        foreach (var pair in _composition.Store.Pairs())
                            _composition.Run.UpdatePairSettings(() => SetEnabled(pair.PairId, request.Command == "enable"));
                        await _automation.SaveAsync(_automation.Load() with { Enabled = request.Command == "enable" }, cancellationToken);
                    }
                    else
                        _composition.Run.UpdatePairSettings(() => SetEnabled(request.PairId!, request.Command == "enable"));
                    _auto.Kick(request.PairId);
                    return new { enabled = request.Command == "enable", request.PairId, request.All };
                }
                finally { _preferencesGate.Release(); }
            case "interval":
                await _preferencesGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _automation.SaveAsync(_automation.Load() with { IntervalMinutes = request.Minutes!.Value }, cancellationToken);
                    _auto.Kick(null);
                    return new { intervalMinutes = request.Minutes.Value };
                }
                finally { _preferencesGate.Release(); }
            case "now":
                if (_composition.Store.Pair(request.PairId!) is null)
                    throw new SyncCliTargetException("No folder pair has that exact identity.");
                _auto.Kick(request.PairId);
                return new { queued = true, request.PairId,
                    detail = "The pair was signalled. A paused, disabled or unprepared pair remains held; enable or resume it first." };
            case "history":
                return _composition.Store.ReadRunHistory(request.PairId, request.Limit, request.After);
            case "logs":
                return _composition.Store.ReadRunHistoryAfter(request.After, request.Limit);
            case "cli":
                return await _choices.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            default: throw new InvalidDataException("Unknown host operation.");
        }
    }

    private async Task<object> ExecuteCliAsync(AutoSyncHostRequest request, CancellationToken cancellationToken)
    {
        var invocation = SyncCliRegistry.Parse(request.CliArgs!);
        if (invocation.Command is not ("status" or "pairs list" or "pairs show" or "accounts list" or
            "run" or "run pause" or "run resume" or "pause" or "resume" or "settings show" or "settings set" or
            "conflicts list" or "conflicts show" or "conflicts resolve" or "accounts show" or "accounts reauthorize" or "accounts pending" or
            "storage online-only" or "storage offline" or "storage inherit" or "storage list"))
            throw new SyncCliUsageException("This command is not yet available through the shared host. Stop AutoSync at its safe point, run the direct command, then restart AutoSync. Nothing was changed.");
        // Dispatch into the SAME runtime; the transport owns admission and cancellation, not the client.
        var writesPreferences = invocation.Command is "pause" or "resume" or "run pause" or "run resume" or "settings set";
        if (writesPreferences) await _preferencesGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var output = new HostCliWriter(100_000);
        using var errors = new HostCliWriter(16_000);
        int exitCode;
        try { exitCode = await SyncCli.ExecuteAsync(request.CliArgs!, output, errors, () => _cli, cancellationToken); }
        finally
        {
            if (writesPreferences) _preferencesGate.Release();
            try
            {
                if (invocation.Command == "accounts reauthorize")
                {
                    foreach (var pair in _composition.Store.Pairs().Where(pair =>
                        pair.A.AccountId == invocation.AccountId || pair.B.AccountId == invocation.AccountId))
                        _auto.Kick(pair.PairId);
                }
                else if (writesPreferences || invocation.Command == "conflicts resolve")
                    _auto.Kick(invocation.PairId);
            }
            catch (Exception wakeError) { RunOutput.Observe(wakeError); } // safety polling still retries

        }
        if (exitCode == 0 && invocation.Command == "pause" && invocation.AccountId is null)
            await _composition.Run.StopAsync().ConfigureAwait(false);
        var text = output.Truncated && invocation.Json
            ? System.Text.Json.JsonSerializer.Serialize(new { truncated = true, exitCode, detail = "Output exceeded the bounded IPC limit. Inspect history or request a smaller page." })
            : output.ToString();
        return new HostCliOutput(exitCode, text, errors.ToString() +
            (output.Truncated || errors.Truncated ? "\nEarlier output was truncated; showing the bounded tail. Exit status is unchanged. Inspect history.\n" : ""));
    }

    private void SetEnabled(string pairId, bool enabled)
    {
        var pair = _composition.Store.Pair(pairId) ?? throw new SyncCliTargetException("No pair has that exact identity.");
        if (enabled && PairAdmission.DecideSaved(pair, _composition.Store.Pairs()) is { } refusal)
            throw new SyncCliUsageException(refusal.Detail);
        // Only enablement changes here. Pair removal/definition edits are not admitted by this host yet.
        _composition.Store.SavePair(pair with { Enabled = enabled });
    }

    public Task StopAsync() => _choices.StopAsync(() => _auto.StopAsync());

    public async ValueTask DisposeAsync()
    {
        await _settlement.DisposeAsync().ConfigureAwait(false);
        _cloudFiles.Dispose();
        _composition.Dispose();
        _http.Dispose();
    }
}
