namespace Massdex.SyncShark.V2;

/// <summary>
/// The failure policy extracted from SyncRun.SaveFailedRetry. Persistence adapters own the
/// entity key and atomically load/store this state; they must not maintain another counter.
/// A deadline bounds retry frequency, not total attempts. A user retry may make it due early.
/// </summary>
public static class FailureRetryOwner
{
    public readonly record struct State(int Attempts, DateTimeOffset RetryAfter);

    /// <summary>Retry authority starts at the exact persisted instant, never at a rounded index key.</summary>
    public static bool IsDue(DateTimeOffset retryAfter, DateTimeOffset now) => now >= retryAfter;

    public enum FailureScope { Item, Endpoint, Stop, State }
    public enum FailureKind { Other, ContentProof, ProviderThrottle }

    internal static State NotBefore(State state, DateTimeOffset? floor) =>
        floor is { } until && until > state.RetryAfter ? state with { RetryAfter = until } : state;

    internal static ProviderThrottleExhaustedException? ThrottleCause(Exception? error)
    {
        for (var cause = error; cause is not null; cause = cause.InnerException)
            if (cause is ProviderThrottleExhaustedException throttle) return throttle;
        return null;
    }

    internal static ActionResult WithCause(ActionResult result, Exception error)
    {
        var throttle = ThrottleCause(error);
        return result with
        {
            FailureHResult = error.HResult,
            FailureKind = throttle is not null ? FailureKind.ProviderThrottle : null,
            RetryNotBefore = throttle?.RetryAt
        };
    }

    // Legacy JSON has no typed kind. Keep raw-cause compatibility in this one owner.
    public static FailureKind ClassifyCause(string cause) =>
        cause.StartsWith("Hash:", StringComparison.Ordinal) ||
        cause.StartsWith("Hash deferred after repeated transport failures:", StringComparison.Ordinal) ||
        cause.StartsWith("Content proof is unavailable", StringComparison.Ordinal)
            ? FailureKind.ContentProof : FailureKind.Other;

    public static FailureKind KindOf(SyncRetry retry) => retry.FailureKind ?? ClassifyCause(retry.LastError);

    /// <summary>
    /// Scope is independent of retry timing. Endpoint identity remains with the caller.
    /// State damage stops effects and forbids further writes to the damaged store.
    /// </summary>
    public static FailureScope ScopeOf(Exception error, CancellationToken cancellationToken,
        Func<Exception, bool> isStateDamage)
    {
        ArgumentNullException.ThrowIfNull(isStateDamage);
        if (error is StateDamagedException or StateNeedsFreshDatabaseException || isStateDamage(error))
            return FailureScope.State;
        return error switch
        {
            PairPausedException or OutOfMemoryException => FailureScope.Stop,
            OperationCanceledException when cancellationToken.IsCancellationRequested => FailureScope.Stop,
            EndpointAccessException => FailureScope.Endpoint,
            _ => FailureScope.Item
        };
    }

    /// <summary>The backoff ceiling. One named limit shared by every retry tier.</summary>
    public const int MaximumRetrySeconds = 300;

    /// <summary>The deferred-backlog schedule shares the run notice row but never a failure episode.</summary>
    public const string DeferredBacklogCause = "deferred-backlog";

    /// <summary>Ten-second exponential backoff, capped at five minutes, with a saturating counter.</summary>
    public static State Failed(int attempts, DateTimeOffset now, bool countAttempt = true)
    {
        var next = countAttempt ? (attempts >= int.MaxValue ? int.MaxValue : Math.Max(0, attempts) + 1)
            : Math.Max(0, attempts);
        var delay = Math.Min(MaximumRetrySeconds, 10 * Math.Pow(2, Math.Clamp(next - 1, 0, 5)));
        return new(next, now.AddSeconds(delay));
    }

    /// <summary>Attaching evidence to sleeping debt is not another attempt. Due failures advance normally.</summary>
    public static State ReportedFailure(SyncRetry? prior, DateTimeOffset now, bool retainSleeping,
        bool countAttempt = true) => retainSleeping && prior is not null && !IsDue(prior.RetryAfter, now)
            ? new(prior.Attempts, prior.RetryAfter)
            : Failed(prior?.Attempts ?? 0, now, countAttempt);

    /// <summary>One generic failure explanation, with a cause independent of changing attempt text.</summary>
    public static Notice RetryNotice(string pairId, string path, string cause, int attempts = 0,
        bool folderInspection = false, string? explanation = null) => new(pairId, 40, NoticeKind.Info, path,
            (explanation ?? $"This item is not synced yet: {cause}. Fix the reported cause and Retry.") +
            " Ignore silences this notice while retries continue." +
            (attempts > 0 ? $" Attempts: {attempts}." : ""), "sync-retry",
            RetryNoticeId(pairId, path), FolderInspection: folderInspection, CauseKey: cause);

    public static string RunNoticeId(string pairId) => ShortId.Make("n", pairId, "40", "", "run-unavailable");

    /// <summary>The one folded per-path retry notice identity, shared by publication and clearance.</summary>
    public static string RetryNoticeId(string pairId, string path) =>
        ShortId.Make("n", pairId, "40", RowCodec.Key(path), "sync-retry");

    /// <summary>One folded "not synced yet" line for a settle refusal. An item retry or the more specific
    /// content-unconfirmed observation owns its own explanation.</summary>
    public static void ReportDeferredRefusal(IStateStore store, string pairId, string path, string cause)
    {
        if (store.SyncRetryAt(pairId, path) is not null || store.HasUnresolvedNotice(pairId, "content-unconfirmed", path))
            return;
        store.AddNotices([RetryNotice(pairId, path, cause)]);
    }

    /// <summary>Closes the folded refusal line once the path settles; an item retry keeps its notice.</summary>
    public static void ClearDeferredRefusal(IStateStore store, string pairId, string path)
    {
        var id = RetryNoticeId(pairId, path);
        if (store.NoticeById(pairId, id) is not { Closed: false }) return;
        if (store.SyncRetryAt(pairId, path) is not null) return;
        store.CloseNotice(id, "resolved");
    }

    /// <summary>Run debt belongs to a pair/run notice, never the root path's item or decision row.</summary>
    public static State RecordRunFailure(IStateStore store, string pairId, Exception error, DateTimeOffset now,
        Func<Exception, bool> isStateDamage)
    {
        if (ScopeOf(error, CancellationToken.None, isStateDamage) == FailureScope.State)
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
            "run-unavailable", id, FailureRetry: state, CauseKey: error.Message) { CauseIdentity = FailureCauseIdentity.Of(error) }]);
        return state;
    }

    /// <summary>A real run failure's counter lives on the run notice; a deferred schedule is not a failure.</summary>
    public static State? RunFailureState(IStateStore store, string pairId) =>
        RunNotice(store, pairId) is { } row && row.Notice.CauseKey != DeferredBacklogCause
            ? row.Notice.FailureRetry : null;

    /// <summary>The non-shrinking deferred backlog's own attempt counter, kept on the same run notice row.</summary>
    public static State? DeferredRetryState(IStateStore store, string pairId) =>
        RunNotice(store, pairId) is { } row && row.Notice.CauseKey == DeferredBacklogCause
            ? row.Notice.FailureRetry : null;

    /// <summary>
    /// Records one non-shrinking deferred backlog on the pair's existing run notice. Attempts advance
    /// through <see cref="Failed"/> so a run that makes no progress stops re-arming every ten seconds.
    /// </summary>
    public static void RecordDeferredRetry(IStateStore store, string pairId, int deferred, State state) =>
        store.AddNotices([new(pairId, 40, NoticeKind.Info, string.Empty,
            $"Sync is still catching up: {deferred} item(s) are waiting to retry. Automatic retries continue " +
            "and no file is skipped. Retry now starts them sooner. Ignore silences this notice while retries continue.",
            "run-unavailable", RunNoticeId(pairId), FailureRetry: state, CauseKey: DeferredBacklogCause)]);

    /// <summary>A successful run closes the run notice; a deferred backlog keeps its counter until real shrink.</summary>
    public static bool KeepsDeferredNotice(IStateStore store, string pairId, int deferred) =>
        deferred > 0 && IsDeferredNotice(store, pairId);

    /// <summary>Real drain resets the shared schedule: the next non-shrinking run starts at the first tier.</summary>
    public static void ResetDeferredRetry(IStateStore store, string pairId)
    {
        if (RunNotice(store, pairId) is { } row && row.Notice.CauseKey == DeferredBacklogCause)
            store.CloseNotice(row.Notice.NoticeId ?? RunNoticeId(pairId), "resolved");
    }

    private static bool IsDeferredNotice(IStateStore store, string pairId) =>
        RunNotice(store, pairId) is { } row && row.Notice.CauseKey == DeferredBacklogCause;

    private static (Notice Notice, bool Closed)? RunNotice(IStateStore store, string pairId) =>
        store.NoticeById(pairId, RunNoticeId(pairId)) is { Closed: false } row ? row : null;

    /// <summary>Persist a pair/path failure using the existing durable retry adapter.</summary>
    public static SyncRetry SaveFailedRetry(IStateStore store, string pairId, string path,
        string? reason, SyncRetry? old, DateTimeOffset now,
        FailureKind? failureKind = null, DateTimeOffset? retryNotBefore = null, bool countAttempt = true)
    {
        var cause = reason ?? (old?.LastError is { Length: > 0 } previous
            ? previous : "The item could not be confirmed or applied.");
        var kind = failureKind ?? (reason is null && old is not null ? KindOf(old) : ClassifyCause(cause));
        var state = ReportedFailure(old, now,
            retainSleeping: kind == FailureKind.ContentProof && old is not null && KindOf(old) == FailureKind.ContentProof,
            countAttempt: countAttempt && old is not { ReprobeEvidence: true });
        var retry = (old ?? new SyncRetry(path, "sync", 0, "", DateTimeOffset.MinValue)) with
        {
            Attempts = state.Attempts,
            DeleteBlockedDetail = null,
            DeleteBlockedDeferred = false,
            LastError = cause,
            FailureKind = kind,
            RetryAfter = NotBefore(state, retryNotBefore).RetryAfter
        };
        store.SaveSyncRetry(pairId, retry);
        return retry;
    }
}
