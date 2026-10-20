namespace Massdex.SyncShark;

/// <summary>The activity, run-history and health surface the window calls; it holds the members lifted from the v1 authority database.</summary>
public interface ISyncHistoryHost
{
    /// <summary>Loads a page of the activity projection.</summary>
    Task<IReadOnlyList<SyncAuthorityActivityProjection>> LoadActivityProjectionPageAsync(int limit, int offset, string? search, string? filter, string? itemType = null, string? pairId = null, DateTimeOffset? sinceUtc = null, CancellationToken cancellationToken = default, string? connectionId = null, SyncActivityQuery? query = null);

    /// <summary>Maintains ordinary activity history; null when nothing retired.</summary>
    Task<SyncActivityRetirementResult?> MaintainOrdinaryActivityHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>Retries persisting the pending activity events.</summary>
    Task RetryPendingActivityPersistenceAsync();

    /// <summary>Loads the retry plan for a run.</summary>
    Task<SyncRunRetryPlan> LoadRunRetryAsync(string runId, CancellationToken cancellationToken = default, string? selectedPairId = null, Action<string>? onReadProgress = null);

    /// <summary>Retries a run's selected items.</summary>
    Task<SyncBootstrapActionResult> RetryRunAsync(string runId, IReadOnlyCollection<string>? selectedPaths, string mode, Func<SyncAuthorityBatchProgress, Task>? onProgress = null, CancellationToken cancellationToken = default, string? selectedPairId = null, Action<string>? onPreparationProgress = null);

    /// <summary>Verifies the outcomes of a run and returns the batch result.</summary>
    Task<SyncAuthorityBatchResult> VerifyRunOutcomesAsync(string runId, Func<SyncAuthorityBatchProgress, Task>? onProgress = null, CancellationToken cancellationToken = default, Action<string>? onReadProgress = null);

    /// <summary>Loads retained run history, newest first. (Lifted from the v1 authority database.)</summary>
    Task<IReadOnlyList<SyncAuthorityRunHistory>> LoadRunHistoryAsync(int limit = 50, int offset = 0, CancellationToken cancellationToken = default);

    /// <summary>Loads the retries of one retained run. (Lifted from the v1 authority database.)</summary>
    Task<IReadOnlyList<SyncAuthorityRunHistory>> LoadRunRetriesAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Loads one retained run by id, or null. (Lifted from the v1 authority database.)</summary>
    Task<SyncAuthorityRunHistory?> LoadRunByIdAsync(string runId, CancellationToken cancellationToken = default, Action<string>? onReadProgress = null);

    /// <summary>Loads the retained activity window. (Lifted from the v1 authority database.)</summary>
    Task<SyncActivityRetentionWindow> LoadActivityRetentionWindowAsync(CancellationToken cancellationToken = default, string? runId = null);

    /// <summary>The current activity sequence number. (Lifted from the v1 authority database.)</summary>
    Task<long> LoadActivitySequenceAsync(CancellationToken cancellationToken = default);

    /// <summary>The last successful sync time, or null. (Lifted from the v1 authority database.)</summary>
    Task<DateTimeOffset?> LoadLastSuccessfulSyncUtcAsync(CancellationToken cancellationToken = default, IReadOnlyCollection<string>? allowedPairIds = null);

    /// <summary>Appends one diagnostic activity event. (Lifted from the v1 authority database.)</summary>
    Task AppendDiagnosticActivityAsync(SyncActivityEvent item, CancellationToken cancellationToken = default);

    /// <summary>Runs the database health check. (Lifted from the v1 authority database.)</summary>
    Task VerifyHealthAsync(CancellationToken cancellationToken = default, Action<string>? startupProgress = null);
}
