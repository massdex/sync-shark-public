namespace Massdex.SyncShark;

/// <summary>Running, stopping, monitoring and queueing the pair set the window calls.</summary>
public interface ISyncRunHost
{
    /// <summary>Runs the given pairs and returns the batch result.</summary>
    Task<SyncAuthorityBatchResult> RunPairsAsync(IEnumerable<SyncPair> pairs, string trigger, Func<SyncAuthorityBatchProgress, Task>? onProgress = null, CancellationToken cancellationToken = default, Action<string>? admissionProgress = null);

    /// <summary>Recovers the given pairs and returns the batch result.</summary>
    Task<SyncAuthorityBatchResult> RecoverPairsAsync(IEnumerable<SyncPair> pairs, Func<SyncAuthorityBatchProgress, Task>? onProgress = null, CancellationToken cancellationToken = default, Action<string>? admissionProgress = null);

    /// <summary>Stops the active run; false when none is running.</summary>
    Task<bool> StopActiveRunAsync(CancellationToken cancellationToken = default);

    /// <summary>Pauses or resumes the active run; null when none is running.</summary>
    Task<bool?> SetActiveRunPausedAsync(bool paused, CancellationToken cancellationToken = default);

    /// <summary>Waits for the active run's stop checkpoint within the timeout.</summary>
    Task<bool> WaitForActiveRunCheckpointAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Configures change monitors for the given pairs.</summary>
    Task<IReadOnlyList<SyncAuthorityMonitorStartResult>> ConfigureMonitorsAsync(IEnumerable<SyncPair> pairs, Func<IReadOnlySet<string>, Task<bool>> onDebouncedRunRequested, Func<string, Task>? onHealthChanged = null, CancellationToken cancellationToken = default);

    /// <summary>Stops all change monitors.</summary>
    Task StopMonitorsAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads the monitor projection.</summary>
    Task<IReadOnlyList<SyncAuthorityMonitorProjection>> LoadMonitorProjectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Counts the items the pending queue holds.</summary>
    Task<int> LoadPendingQueueCountAsync(CancellationToken cancellationToken = default, string? connectionId = null);

    /// <summary>Loads the pending queue projection.</summary>
    Task<IReadOnlyList<SyncAuthorityQueueProjection>> LoadQueueProjectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads a page of the pending queue projection.</summary>
    Task<IReadOnlyList<SyncAuthorityQueueProjection>> LoadQueueProjectionPageAsync(int limit, int offset, string? search, string? filter, CancellationToken cancellationToken = default, string? pairId = null, string? itemType = null, DateTimeOffset? sinceUtc = null, string? connectionId = null);

    /// <summary>The pair ids with a durable item needing attention.</summary>
    Task<IReadOnlySet<string>> LoadPairIdsNeedingAttentionAsync(CancellationToken cancellationToken = default);

    /// <summary>Interrupted same-root publication incidents.</summary>
    Task<IReadOnlyList<SyncAuthorityDatabase.SyncInterruptedPublicationIncident>> InterruptedPublicationIncidentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Repairs one interrupted publication; false when nothing was repaired.</summary>
    Task<bool> RecoverInterruptedPublicationAsync(string pairId, CancellationToken cancellationToken = default);
}
