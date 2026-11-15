namespace Massdex.SyncShark;

public sealed record UiPreferences(string Theme, bool KeepRunningWhenClosed = true,
    bool CompactRows = false, bool NotifyProblems = true, bool NotifyCompleted = false,
    bool NotifyTransfers = true, bool NotificationSound = false,
    bool PauseUntilResumed = false, DateTimeOffset? PauseUntilUtc = null,
    IReadOnlyList<SyncAccountPause>? AccountPauses = null, bool ExplorerIntegrationEnabled = true,
    string? ExplorerRegistrationBeforeJson = null, string? UpdateDeliveryFolder = null,
    string? LastNotifiedUpdateVersion = null, bool AutomaticUpdateChecks = true,
    string? DownloadedUpdateFolder = null, IReadOnlyDictionary<string, string>? PageViewModes = null,
    string? PauseTimeZoneId = null, string StatusIconStyle = "Combined")
{
    // U1 presentation contract: the same persisted status-icon preference under the name the frozen
    // shared contract uses. One stored member keeps every existing reader, saved preference file and
    // CLI settings surface unchanged; this alias is only the shared presentation's vocabulary, so it
    // is deliberately not serialised and never becomes a second source of truth.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? IconSet
    {
        get => StatusIconStyle;
        init => StatusIconStyle = value ?? UiPreferencesStore.DefaultIconSet;
    }
}
