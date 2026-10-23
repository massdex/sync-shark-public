namespace Massdex.SyncShark.V2;

/// <summary>
/// The failure policy extracted from SyncRun.SaveFailedRetry. Persistence adapters own the
/// entity key and atomically load/store this state; they must not maintain another counter.
/// A deadline bounds retry frequency, not total attempts. A user retry may make it due early.
/// </summary>
public static class FailureRetryOwner
{
    public readonly record struct State(int Attempts, DateTimeOffset RetryAfter);

    public enum FailureScope { Item, Endpoint, Stop, State }

    /// <summary>
    /// Scope is independent of retry timing. Endpoint identity remains with the caller.
    /// State damage stops effects and forbids further writes to the damaged store.
    /// </summary>
    public static FailureScope ScopeOf(Exception error, CancellationToken cancellationToken) => error switch
    {
        StateDamagedException or StateNeedsFreshDatabaseException => FailureScope.State,
        Microsoft.Data.Sqlite.SqliteException sqlite when StoreSchema.IsDamaged(sqlite) => FailureScope.State,
        PairPausedException or OutOfMemoryException => FailureScope.Stop,
        OperationCanceledException when cancellationToken.IsCancellationRequested => FailureScope.Stop,
        EndpointAccessException => FailureScope.Endpoint,
        _ => FailureScope.Item
    };

    /// <summary>Ten-second exponential backoff, capped at five minutes, with a saturating counter.</summary>
    public static State Failed(int attempts, DateTimeOffset now, bool countAttempt = true)
    {
        var next = countAttempt ? (attempts >= int.MaxValue ? int.MaxValue : Math.Max(0, attempts) + 1)
            : Math.Max(0, attempts);
        var delay = Math.Min(300, 10 * Math.Pow(2, Math.Clamp(next - 1, 0, 5)));
        return new(next, now.AddSeconds(delay));
    }

    /// <summary>One generic failure explanation, with a cause independent of changing attempt text.</summary>
    public static Notice RetryNotice(string pairId, string path, string cause, int attempts = 0,
        bool folderInspection = false, string? explanation = null) => new(pairId, 40, NoticeKind.Info, path,
            (explanation ?? $"This item is not synced yet: {cause}. Fix the reported cause and Retry.") +
            " Ignore silences this notice while retries continue." +
            (attempts > 0 ? $" Attempts: {attempts}." : ""), "sync-retry",
            FolderInspection: folderInspection, CauseKey: cause);

    public static string RunNoticeId(string pairId) => ShortId.Make("n", pairId, "40", "", "run-unavailable");

    /// <summary>Run debt belongs to a pair/run notice, never the root path's item or decision row.</summary>
    public static State RecordRunFailure(IStateStore store, string pairId, Exception error, DateTimeOffset now)
    {
        if (ScopeOf(error, CancellationToken.None) == FailureScope.State)
        {
            // Do not try to persist a notice through a store whose authority is lost.
            RunOutput.Observe(error);
            return new State(0, DateTimeOffset.MaxValue);
        }
        var id = RunNoticeId(pairId);
        var previous = store.NoticeById(pairId, id);
        var state = Failed(previous is { Closed: false } ? previous.Value.Notice.FailureRetry?.Attempts ?? 0 : 0, now);
        var fix = error is EndpointAccessException ? "Reconnect the account, then Retry." :
            error is StateDamagedException or StateNeedsFreshDatabaseException ?
                "Restore a verified state backup before retrying; no further effects are safe." :
                "Check the stated failure and available storage, then Retry. Automatic retries continue.";
        store.AddNotices([new(pairId, 40, NoticeKind.Info, "",
            "Sync could not finish: " + error.Message + ". " + fix + " Ignore silences this cause without excluding files.",
            "run-unavailable", id, FailureRetry: state, CauseKey: error.Message)]);
        return state;
    }

    public static State? RunFailureState(IStateStore store, string pairId) =>
        store.NoticeById(pairId, RunNoticeId(pairId)) is { Closed: false } row ? row.Notice.FailureRetry : null;

    /// <summary>Persist a pair/path failure using the existing durable retry adapter.</summary>
    public static SyncRetry SaveFailedRetry(IStateStore store, string pairId, string path,
        string? reason, SyncRetry? old, DateTimeOffset now)
    {
        var state = Failed(old?.Attempts ?? 0, now, old is not { ReprobeEvidence: true });
        var retry = (old ?? new SyncRetry(path, "sync", 0, "", DateTimeOffset.MinValue)) with
        {
            Attempts = state.Attempts,
            DeleteBlockedDetail = null,
            DeleteBlockedDeferred = false,
            LastError = reason ?? (old?.LastError is { Length: > 0 } previous
                ? previous : "The item could not be confirmed or applied."),
            RetryAfter = state.RetryAfter
        };
        store.SaveSyncRetry(pairId, retry);
        return retry;
    }
}
