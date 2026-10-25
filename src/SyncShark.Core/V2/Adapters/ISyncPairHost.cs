namespace Massdex.SyncShark;

/// <summary>The pair-editor and folder-selection surface the window calls.</summary>
public interface ISyncPairHost
{
    /// <summary>Loads the pair set for display.</summary>
    Task<SyncPairSet> LoadPairsAsync(CancellationToken cancellationToken = default, Action<string>? startupProgress = null);

    /// <summary>Selects a pair and returns the pair set.</summary>
    Task<SyncPairSet> SelectPairAsync(string pairId, CancellationToken cancellationToken = default);

    /// <summary>Enables or disables a pair and returns the pair set.</summary>
    bool IsPairPaused(string pairId) => false;
    Task<bool> SetPairPausedAsync(string pairId, bool paused, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Explicit pair pause requires core v2.");

    Task<SyncPairSet> SetPairEnabledAsync(string pairId, bool enabled, CancellationToken cancellationToken = default, Action<string>? progress = null);

    /// <summary>Retires a pair and returns the pair set.</summary>
    Task<SyncPairSet> RetirePairAsync(string pairId, CancellationToken cancellationToken = default);

    /// <summary>Binds a new pair from the editor and returns the binding result.</summary>
    Task<SyncPairBindingResult> BindPairAsync(SyncPairBindingRequest request, CancellationToken cancellationToken = default, Action<string>? progress = null);

    /// <summary>Binds a pair drafted by the endpoint editor and returns the binding result.</summary>
    Task<SyncPairBindingResult> BindEndpointPairAsync(SyncPair draft, string? replacingPairId = null, long? expectedPolicyRevision = null, CancellationToken cancellationToken = default, DropboxFolderEntry? recoveryFolder = null, Action<string>? progress = null);

    /// <summary>Previews a pair draft for the editor.</summary>
    Task<SyncPairDraftPreview> LoadPairDraftPreviewAsync(string localBrowseRoot, DropboxFolderEntry dropboxBrowseRoot, CancellationToken cancellationToken = default, string? connectionId = null);

    /// <summary>Loads a folder-selection page for a pair.</summary>
    Task<SyncFolderSelectionPage> LoadFolderSelectionPageAsync(string pairId, int limit = 250, int offset = 0, string? search = null, string? filter = null, bool refreshIndex = false, CancellationToken cancellationToken = default);

    /// <summary>Loads the folder-selection tree for a pair.</summary>
    Task<SyncFolderSelectionTree> LoadFolderSelectionTreeAsync(string pairId, CancellationToken cancellationToken = default);

    /// <summary>Previews the effect of the given ignore rules on a pair.</summary>
    Task<SyncIgnorePreview> PreviewIgnoreRulesAsync(string pairId, IReadOnlyList<SyncIgnoreRule> rules, CancellationToken cancellationToken = default);
}
