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
    private readonly object _pendingGate = new();
    private readonly Dictionary<Task, Task> _pendingChoices = [];
    private bool _stopping;

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
            _cli = V2CliRuntime.Borrow(authority, _composition, root);
            _auto = _composition.StartAutoSync(Paused,
                () => TimeSpan.FromMinutes(_automation.Load().IntervalMinutes));
        }
        catch { _composition?.Dispose(); _http.Dispose(); throw; }
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
                return await ExecuteOrQueueCliAsync(request, cancellationToken).ConfigureAwait(false);
            default: throw new InvalidDataException("Unknown host operation.");
        }
    }

    private async Task<object> ExecuteOrQueueCliAsync(AutoSyncHostRequest request, CancellationToken ct)
    {
        var invocation = SyncCliRegistry.Parse(request.CliArgs!);
        if (invocation.Command != "conflicts resolve" ||
            invocation.Choice?.ToLowerInvariant() is not (NoticeChoices.Ignore or NoticeChoices.Exclude))
            return await ExecuteCliAsync(request, ct).ConfigureAwait(false);
        Task<object> work;
        lock (_pendingGate)
        {
            if (_stopping || _pendingChoices.Count >= 64)
                throw new SyncCliUsageException("Notice choices are still draining. The notice is unchanged; retry shortly.");
            // Execute synchronously through validation/capture, then let the SAME runtime wait on
            // its run gate. A client disconnect or safe stop must not cancel accepted exclusions.
            var args = request.CliArgs!.ToList();
            var timeout = args.IndexOf("--timeout-seconds");
            if (timeout >= 0) args.RemoveRange(timeout, 2);
            work = ExecuteCliAsync(request with { CliArgs = args.ToArray() }, CancellationToken.None);
            if (!work.IsCompleted)
            {
                _pendingChoices.Add(work, Task.CompletedTask);
                _pendingChoices[work] = ObserveChoiceAsync(work, invocation.PairId!);
            }
        }
        if (work.IsCompleted) return await work.ConfigureAwait(false);
        var detail = $"Queued behind the active run of pair {invocation.PairId}. The exclusion will apply at the safe point independently of the client deadline; no files are deleted.";
        return new HostCliOutput(0, invocation.Json
            ? System.Text.Json.JsonSerializer.Serialize(new { queued = true, pairId = invocation.PairId, detail })
            : detail, string.Empty);
    }

    private async Task ObserveChoiceAsync(Task<object> work, string pairId)
    {
        await Task.Yield(); // publish the observer before it can remove its entry
        try
        {
            if (await work.ConfigureAwait(false) is HostCliOutput { ExitCode: not 0 } result)
                throw new IOException(result.Stderr + result.Stdout);
        }
        catch (Exception error)
        {
            RunOutput.Observe(error);
            try
            {
                _composition.Store.AddNotices([new Notice(pairId, 0, NoticeKind.Info, string.Empty,
                    "A queued notice choice could not be saved: " + error.Message +
                    ". Check store access and disk space, then Retry the original item choice; Ignore dismisses this notice.", "autosync-failed")]);
            }
            catch (Exception noticeError) { RunOutput.Observe(noticeError); }
        }
        finally { lock (_pendingGate) _pendingChoices.Remove(work); }
    }

    private async Task<object> ExecuteCliAsync(AutoSyncHostRequest request, CancellationToken cancellationToken)
    {
        var invocation = SyncCliRegistry.Parse(request.CliArgs!);
        if (invocation.Command is not ("status" or "pairs list" or "pairs show" or "accounts list" or
            "run" or "run pause" or "run resume" or "pause" or "resume" or "settings show" or "settings set" or
            "conflicts list" or "conflicts show" or "conflicts resolve" or "accounts show" or "accounts reauthorize" or "accounts pending"))
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

    public async Task StopAsync()
    {
        lock (_pendingGate) _stopping = true;
        await _auto.StopAsync().ConfigureAwait(false);
        // Stop the run first, then drain accepted choices before the store can be disposed.
        Task[] pending;
        lock (_pendingGate) pending = _pendingChoices.Values.ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (Exception error) { RunOutput.Observe(error); }
    }

    public ValueTask DisposeAsync()
    {
        _composition.Dispose();
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
