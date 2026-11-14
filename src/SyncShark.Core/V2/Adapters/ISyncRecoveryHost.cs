namespace Massdex.SyncShark;

/// <summary>The recovery and rewind surface the window calls.</summary>
public interface ISyncRecoveryHost
{
    /// <summary>Loads the pairs a user may choose from in the recovery page.</summary>
    Task<IReadOnlyList<SyncRecoveryPairChoice>> LoadRecoveryPairChoicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads a page of the recovery catalog for a pair.</summary>
    Task<SyncRecoveryCatalogPage> LoadRecoveryCatalogPageAsync(string pairId, int archiveOffset = 0, string? deletedCursor = null, bool includeDropboxDeletedItems = false, int pageSize = 200, string? search = null, CancellationToken cancellationToken = default);

    /// <summary>Loads the recovery usage summary for a pair.</summary>
    Task<SyncRecoveryUsageSummary> LoadRecoveryUsageAsync(string pairId, CancellationToken cancellationToken = default);

    /// <summary>Loads the Dropbox revision catalog for one path.</summary>
    Task<IReadOnlyList<SyncRecoveryCatalogItem>> LoadDropboxRevisionCatalogAsync(string pairId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Previews restoring one recovery item.</summary>
    Task<SyncRecoveryEffectPreview> PreviewRecoveryAsync(SyncRecoveryCatalogItem item, bool restoreAsCopy, CancellationToken cancellationToken = default);

    /// <summary>Restores one recovery item.</summary>
    Task<SyncRecoveryRestoreResult> RestoreRecoveryAsync(SyncRecoveryCatalogItem item, bool restoreAsCopy, CancellationToken cancellationToken = default);

    /// <summary>Removes one recovery copy.</summary>
    Task<SyncRecoveryCleanupResult> RemoveRecoveryCopyAsync(SyncRecoveryCatalogItem item, CancellationToken cancellationToken = default);

    /// <summary>Loads the rewind sessions.</summary>
    Task<IReadOnlyList<SyncRewindSession>> LoadRewindSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Prepares a rewind handoff for a pair.</summary>
    Task<SyncRewindHandoff> PrepareRewindAsync(string pairId, CancellationToken cancellationToken = default);

    /// <summary>Previews importing a rewind session for a pair.</summary>
    Task<SyncRewindSession> PreviewRewindImportAsync(string pairId, CancellationToken cancellationToken = default);

    /// <summary>Applies a rewind import for a pair.</summary>
    Task<SyncRewindApplyResult> ApplyRewindImportAsync(string pairId, SyncRewindApplyConfirmation confirmation, CancellationToken cancellationToken = default);

    /// <summary>Resumes after a rewind for a pair.</summary>
    Task<SyncRewindSession> ResumeAfterRewindAsync(string pairId, CancellationToken cancellationToken = default);
}
