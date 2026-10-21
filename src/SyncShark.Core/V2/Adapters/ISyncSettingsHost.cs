namespace Massdex.SyncShark;

/// <summary>The settings and presentation surface the window calls.</summary>
public interface ISyncSettingsHost
{
    /// <summary>Loads the automation settings.</summary>
    SyncAutomationSettings LoadAutomation();

    /// <summary>Saves the automation settings.</summary>
    Task SaveAutomationAsync(SyncAutomationSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Loads the transfer settings.</summary>
    SyncTransferSettings LoadTransferSettings();

    /// <summary>Saves the transfer settings.</summary>
    Task SaveTransferSettingsAsync(SyncTransferSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Loads the presentation snapshot (accounts and preferences).</summary>
    SyncPresentationSnapshot LoadPresentationSnapshot();
}
