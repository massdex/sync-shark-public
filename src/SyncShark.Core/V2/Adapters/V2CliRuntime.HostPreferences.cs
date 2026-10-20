using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    private readonly SemaphoreSlim _preferenceGate = new(1, 1);
    private PilotStateAuthority PreferenceAuthority => _instanceLock?.StateAuthority ??
        throw new SyncCliUnsupportedException("This runtime has no product preference authority.");

    private Task<SyncCliSettingsData> LoadHostSettingsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var authority = PreferenceAuthority;
        var automation = new SyncAutomationStore(authority).Load();
        var transfer = new SyncTransferSettingsStore(authority).Load();
        var preferences = new UiPreferencesStore(authority).Load();
        return Task.FromResult(new SyncCliSettingsData(
            new SyncCliAutomationData(automation.Enabled, automation.IntervalMinutes, automation.StartWithWindows),
            new SyncCliTransferData(transfer.MaximumConcurrentPairs, transfer.UploadKiBPerSecond,
                transfer.DownloadKiBPerSecond, transfer.ResourceMode, transfer.MaximumConcurrentFiles,
                transfer.UploadBatchSize, transfer.UploadCommitConcurrency, transfer.DeleteBatchSize),
            new SyncCliInterfaceData(preferences.Theme, preferences.KeepRunningWhenClosed, preferences.NotifyProblems,
                preferences.NotifyCompleted, preferences.NotifyTransfers, preferences.ExplorerIntegrationEnabled,
                preferences.AutomaticUpdateChecks, preferences.CompactRows, preferences.NotificationSound, preferences.StatusIconStyle),
            RecoveryMaxBytes: transfer.RecoveryMaxBytes, DataLocation: _productRoot));
    }

    private async Task<SyncCliSettingsData> SaveHostSettingsAsync(string json, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        // Changes requiring reconstruction of endpoints are not silently advertised as live-applied.
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name != "automation"))
            throw new SyncCliUsageException("The shared host currently accepts automation settings only. Stop the host and use the full GUI for transfer, interface or data-location changes; no settings were changed.");
        await _preferenceGate.WaitAsync(ct);
        try
        {
            var store = new SyncAutomationStore(PreferenceAuthority);
            var settings = store.Load();
            if (root.TryGetProperty("automation", out var patch))
            {
                if (patch.ValueKind != JsonValueKind.Object || patch.EnumerateObject().Any(p => p.Name is not ("enabled" or "intervalMinutes")))
                    throw new SyncCliUsageException("Use automation.enabled and automation.intervalMinutes. Windows launch configuration stays in the full GUI.");
                if (patch.TryGetProperty("enabled", out var enabled)) settings = settings with { Enabled = enabled.GetBoolean() };
                if (patch.TryGetProperty("intervalMinutes", out var interval)) settings = settings with { IntervalMinutes = interval.GetInt32() };
            }
            await store.SaveAsync(settings, ct);
            return await LoadHostSettingsAsync(ct);
        }
        finally { _preferenceGate.Release(); }
    }

    private async Task<SyncCliPauseData> SaveHostPauseAsync(string scope, string? target, bool paused, int? minutes,
        bool untilResumed, CancellationToken ct, DateTimeOffset? untilUtc, bool untilTomorrow, string? timeZoneId)
    {
        scope = PauseScopes.Require(scope);
        if (!paused && (minutes is not null || untilResumed || untilUtc is not null || untilTomorrow))
            throw new SyncCliUsageException("A resume takes no deadline.");
        if (new[] { minutes is not null, untilResumed, untilUtc is not null, untilTomorrow }.Count(x => x) > 1)
            throw new SyncCliUsageException("Choose one pause deadline.");
        if (scope == PauseScopes.Account && !ConnectionRecords().Any(item => item.ConnectionId == target))
            throw new SyncCliTargetException("No account has that exact connection identity.");
        if (scope is not (PauseScopes.Global or PauseScopes.Account))
            throw new SyncCliUsageException("For a pair use pairs disable or autosync disable --pair.");
        var now = DateTimeOffset.UtcNow;
        var zone = PauseSchedule.TryResolveZone(timeZoneId) ?? TimeZoneInfo.Local;
        if (minutes is { } bounded && bounded is < 1 or > 43200)
            throw new SyncCliUsageException("Minutes must be between 1 and 43200.");
        DateTimeOffset? deadline = !paused ? null : untilTomorrow
            ? PauseSchedule.Resolve(PauseOption.UntilTomorrow, now, zone).UntilUtc
            : untilUtc ?? (minutes is { } count ? now.AddMinutes(count) : null);
        if (deadline is { } exact && !PauseSchedule.ValidateDeadline(exact, now, out var reason))
            throw new SyncCliUsageException(reason);
        await _preferenceGate.WaitAsync(ct);
        try
        {
            var store = new UiPreferencesStore(PreferenceAuthority);
            var preferences = store.Load();
            if (scope == PauseScopes.Global)
                preferences = preferences with { PauseUntilResumed = paused && deadline is null,
                    PauseUntilUtc = deadline, PauseTimeZoneId = paused ? zone.Id : null };
            else
            {
                var pauses = (preferences.AccountPauses ?? []).Where(item => item.ConnectionId != target).ToList();
                if (paused) pauses.Add(new SyncAccountPause(target!, deadline is null, deadline, zone.Id));
                preferences = preferences with { AccountPauses = pauses };
            }
            await store.SaveAsync(preferences, ct);
            return new SyncCliPauseData(scope, target ?? "", paused, deadline,
                UntilResumed: paused && deadline is null, Indefinitely: paused && deadline is null, TimeZoneId: zone.Id);
        }
        finally { _preferenceGate.Release(); }
    }
}
