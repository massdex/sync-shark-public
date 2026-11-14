using System.Collections.Concurrent;

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

    /// <summary>The deferred-backlog schedule shares the run notice row but never a failure episode.</summary>
    public const string DeferredBacklogCause = "deferred-backlog";

    /// <summary>The configured exponential backoff, capped, with a saturating counter.</summary>
    public static State Failed(V2Settings settings, int attempts, DateTimeOffset now, bool countAttempt = true)
    {
        var next = countAttempt ? (attempts >= int.MaxValue ? int.MaxValue : Math.Max(0, attempts) + 1)
            : Math.Max(0, attempts);
        var delay = Math.Min(settings.Tunables.RetryMaximumDelay.TotalSeconds,
            settings.Tunables.RetryInitialDelay.TotalSeconds *
            Math.Pow(settings.Tunables.RetryDelayMultiplier, Math.Clamp(next - 1, 0, settings.Tunables.RetryMaximumDoublings)));
        return new(next, now.AddSeconds(delay));
    }

    /// <summary>Attaching evidence to sleeping debt is not another attempt. Due failures advance normally.</summary>
    public static State ReportedFailure(V2Settings settings, SyncRetry? prior, DateTimeOffset now, bool retainSleeping,
        bool countAttempt = true) => retainSleeping && prior is not null && !IsDue(prior.RetryAfter, now)
            ? new(prior.Attempts, prior.RetryAfter)
            : Failed(settings, prior?.Attempts ?? 0, now, countAttempt);

    /// <summary>One generic failure explanation, with a cause independent of changing attempt text.</summary>
    public static Notice RetryNotice(string pairId, string path, string cause, int attempts = 0,
        bool folderInspection = false, string? explanation = null) => new(pairId, 40, NoticeKind.Info, path,
            (explanation ?? $"This item is not synced yet: {cause}. Fix the reported cause and Retry.") +
            " Ignore silences this notice while retries continue." +
            (attempts > 0 ? $" Attempts: {attempts}." : ""), "sync-retry",
            FolderInspection: folderInspection, CauseKey: cause);

    public static string RunNoticeId(string pairId) => ShortId.Make("n", pairId, "40", "", "run-unavailable");

    /// <summary>Run debt belongs to a pair/run notice, never the root path's item or decision row.</summary>
    public static State RecordRunFailure(V2Settings settings, IStateStore store, string pairId, Exception error,
        DateTimeOffset now, Func<Exception, bool> isStateDamage)
    {
        if (ScopeOf(error, CancellationToken.None, isStateDamage) == FailureScope.State)
        {
            // Do not try to persist a notice through a store whose authority is lost.
            RunOutput.Observe(error);
            return new State(0, DateTimeOffset.MaxValue);
        }
        var id = RunNoticeId(pairId);
        var state = Failed(settings, RunFailureState(store, pairId)?.Attempts ?? 0, now);
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
    public static SyncRetry SaveFailedRetry(V2Settings settings, IStateStore store, string pairId, string path,
        string? reason, SyncRetry? old, DateTimeOffset now,
        FailureKind? failureKind = null, DateTimeOffset? retryNotBefore = null, bool countAttempt = true)
    {
        var cause = reason ?? (old?.LastError is { Length: > 0 } previous
            ? previous : "The item could not be confirmed or applied.");
        var kind = failureKind ?? (reason is null && old is not null ? KindOf(old) : ClassifyCause(cause));
        var state = ReportedFailure(settings, old, now,
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

    // ---- Adopted gRPC reconnect/backoff (READING 8.5; AutoSync 3.10.1) ----
    // One schedule for the selected listener, prerequisite, request, transfer and recovery attempts.
    // Source: grpc/grpc doc/connection-backoff.md, adopted by the owner decision of 31 October 2026.
    // Legacy members above are unchanged.
    // Listener reconnect state is keyed by the existing account/source identity; provider floors by
    // account, route and source. Both last for the process lifetime only; a new process starts with none.
    // This owner selects a wait only: unknown-write custody and every resend boundary stay with the consumer.

    /// <summary>
    /// Loaded policy settings passed by composition, never code defaults or global settings lookups.
    /// The first failure selects InitialBackoffSeconds exactly; later nominal delays multiply by
    /// BackoffMultiplier and cap at MaximumBackoffSeconds before applying BackoffJitter.
    /// </summary>
    public sealed record BackoffSettings(double InitialBackoffSeconds, double BackoffMultiplier,
        double BackoffJitter, double MaximumBackoffSeconds);

    /// <summary>
    /// The owner-held in-memory listener reconnection state for one existing account/source identity.
    /// It lasts only for this process and is never persisted; a new process starts with no session.
    /// </summary>
    public sealed class Session
    {
        internal Session() { }

        internal int _failures;
        internal double _nominal;
    }

    private static readonly ConcurrentDictionary<string, Session> Sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns this process's session for the existing account/source identity, creating it on first
    /// use. One shared source has one session and one active registered attempt; an event, read, wake or
    /// rearm neither redraws the selected delay nor resets the stage.
    /// </summary>
    public static Session SessionFor(string accountSourceIdentity) =>
        Sessions.GetOrAdd(accountSourceIdentity, static _ => new Session());

    /// <summary>
    /// The one adopted gRPC wait for one genuinely failed listener attempt on <paramref name="session"/>.
    /// An applicable provider wait, including a valid zero, takes precedence and is never shortened by an
    /// event, read, wake, rearm or manual Retry. Otherwise the session advances one consecutive failure and
    /// selects its delay from exactly one <paramref name="sample"/> draw, which returns a value in [0,1];
    /// the offset is (2*sample - 1) * <see cref="BackoffSettings.BackoffJitter"/> * nominal.
    /// The session retains the selected delay. A provider wait is not success.
    /// </summary>
    public static TimeSpan AdoptedListenerWait(Session session, TimeSpan? providerWait,
        BackoffSettings settings, Func<double> sample)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (providerWait is { } supplied) return supplied;
        session._failures = session._failures == int.MaxValue ? int.MaxValue : session._failures + 1;
        if (session._failures == 1)
        {
            session._nominal = settings.InitialBackoffSeconds;
            return TimeSpan.FromSeconds(session._nominal);
        }
        session._nominal = Math.Min(session._nominal * settings.BackoffMultiplier,
            settings.MaximumBackoffSeconds);
        return Jittered(session._nominal, settings, sample);
    }

    // This class's own gate guards the floors; nothing here calls out.
    private static readonly object ProviderFloorGate = new();
    private static readonly Dictionary<string, Dictionary<(string? RouteName, string? SourceIdentity),
        (long StartedTimestamp, TimeSpan Duration)>> ProviderFloors = new(StringComparer.Ordinal);

    /// <summary>
    /// Records one provider wait as an account/route/source floor, keeping the later expiry of an exact
    /// scope; a zero or malformed wait installs nothing (table row B).
    /// </summary>
    public static void InstallProviderConstraint(
        (string AccountId, string? RouteName, string? SourceIdentity) scope,
        long startedTimestamp, TimeSpan duration, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (duration <= TimeSpan.Zero) return;
        lock (ProviderFloorGate)
        {
            if (!ProviderFloors.TryGetValue(scope.AccountId, out var floors))
                ProviderFloors[scope.AccountId] = floors = new();
            var key = (scope.RouteName, scope.SourceIdentity);
            var remainder = duration - time.GetElapsedTime(startedTimestamp);
            if (!floors.TryGetValue(key, out var installed) ||
                installed.Duration - time.GetElapsedTime(installed.StartedTimestamp) < remainder)
                floors[key] = (startedTimestamp, duration);
        }
    }

    /// <summary>
    /// The longest unexpired remainder whose route and source are unrestricted or match the identity, or
    /// null when none applies; expired floors are dropped here (table rows B and C).
    /// </summary>
    public static TimeSpan? RemainingProviderConstraint(
        (string AccountId, string RouteName, string SourceIdentity) identity, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        lock (ProviderFloorGate)
        {
            if (!ProviderFloors.TryGetValue(identity.AccountId, out var floors)) return null;
            TimeSpan? maximum = null;
            List<(string? RouteName, string? SourceIdentity)>? expired = null;
            foreach (var (key, floor) in floors)
            {
                var remaining = floor.Duration - time.GetElapsedTime(floor.StartedTimestamp);
                if (remaining <= TimeSpan.Zero)
                {
                    (expired ??= new List<(string?, string?)>()).Add(key);
                    continue;
                }
                if ((key.RouteName is null || key.RouteName == identity.RouteName) &&
                    (key.SourceIdentity is null || key.SourceIdentity == identity.SourceIdentity) &&
                    (maximum is not { } current || remaining > current))
                    maximum = remaining;
            }
            if (expired is not null)
                foreach (var key in expired) floors.Remove(key);
            if (floors.Count == 0) ProviderFloors.Remove(identity.AccountId);
            return maximum;
        }
    }

    /// <summary>
    /// The remaining part of one supplied wait measured from when it was first known; a null anchor means
    /// the full duration, and elapsed past the wait means zero, never null.
    /// </summary>
    public static TimeSpan? RemainingProviderObservation(TimeSpan? duration, long? startedTimestamp,
        TimeProvider time)
    {
        if (duration is not { } supplied) return null;
        if (startedTimestamp is not { } started) return supplied;
        var remaining = supplied - time.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// An accepted valid listener response resets <paramref name="session"/>'s generic stage and selected
    /// delay only; an unexpired provider wait, including a valid zero, stays in force. A malformed or
    /// reset response, another route's success, or a watch rebuild is not success.
    /// </summary>
    public static void AdoptedListenerSucceeded(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session._failures = 0;
        session._nominal = 0;
    }

    /// <summary>
    /// The adopted schedule's wait for one finite operation-local episode (prerequisite listing page,
    /// Request, Transfer or Recovery) that ends with its operation. <paramref name="priorAttempts"/> is
    /// that episode's number of prior consecutive failures, 0 before the first, not the current attempt
    /// number. An applicable provider wait, including a valid zero, takes precedence and is never
    /// shortened; otherwise exactly one <paramref name="sample"/> draw (a value in [0,1], offset
    /// (2*sample - 1) * <see cref="BackoffSettings.BackoffJitter"/> * nominal) selects the delay. The episode retains it.
    /// </summary>
    public static TimeSpan AdoptedWait(int priorAttempts, TimeSpan? providerWait,
        BackoffSettings settings, Func<double> sample)
    {
        if (providerWait is { } supplied) return supplied;
        if (priorAttempts <= 0) return TimeSpan.FromSeconds(settings.InitialBackoffSeconds);
        return Jittered(Nominal(priorAttempts, settings), settings, sample);
    }

    /// <summary>
    /// The adopted schedule's persisted pair state for one genuinely failed attempt; the schedule is the
    /// same as <see cref="AdoptedWait"/>. <paramref name="priorAttempts"/> is the pair's number of prior
    /// consecutive eligible failures from <c>ReadPrerequisiteRetry</c>, 0 before the first. The returned
    /// <see cref="State.Attempts"/> is <paramref name="priorAttempts"/> + 1, saturating at int.MaxValue.
    /// <paramref name="sample"/> returns a value in [0,1] and exactly one draw selects the delay; an
    /// applicable provider wait, including a valid zero, takes precedence and is never shortened.
    /// Storage transports this value and never redraws it.
    /// </summary>
    public static State AdoptedFailed(int priorAttempts, DateTimeOffset now, TimeSpan? providerWait,
        BackoffSettings settings, Func<double> sample)
    {
        var attempts = priorAttempts >= int.MaxValue ? int.MaxValue : Math.Max(0, priorAttempts) + 1;
        return new State(attempts, now + AdoptedWait(priorAttempts, providerWait, settings, sample));
    }

    /// <summary>
    /// One finite operation-local episode's consecutive-failure count and adopted delay. It holds the
    /// remaining-provider-observation and adopted-wait reading and the saturating advance, so each context
    /// keeps only its own episode and never restates the schedule.
    /// </summary>
    public sealed class Episode(bool providerWaitCounts)
    {
        private int _failures;

        /// <summary>An accepted attempt ends this episode's consecutive failures.</summary>
        public void Succeeded() => _failures = 0;

        /// <summary>
        /// Returns this episode's adopted wait for one failed attempt and advances its consecutive-failure
        /// count, saturating at int.MaxValue. A context whose provider wait counts as a failure advances
        /// even when a provider wait applies; the other advances only on a generic failure. A provider wait
        /// still precedes and is returned unchanged.
        /// </summary>
        public TimeSpan Failed(TimeSpan? providerWait, long? waitStarted, TimeProvider time,
            BackoffSettings settings, Func<double> sample)
        {
            var provider = RemainingProviderObservation(providerWait, waitStarted, time);
            var wait = AdoptedWait(_failures, provider, settings, sample);
            if ((providerWaitCounts || provider is null) && _failures < int.MaxValue) _failures++;
            return wait;
        }
    }

    private static TimeSpan Jittered(double nominal, BackoffSettings settings, Func<double> sample)
    {
        var offset = (2 * sample() - 1) * settings.BackoffJitter * nominal;
        return TimeSpan.FromSeconds(nominal + offset);
    }

    private static double Nominal(int priorAttempts, BackoffSettings settings)
    {
        var nominal = settings.InitialBackoffSeconds;
        var maximum = settings.MaximumBackoffSeconds;
        if (settings.BackoffMultiplier <= 1 || maximum <= nominal)
            return Math.Min(nominal * Math.Pow(Math.Max(settings.BackoffMultiplier, 0), priorAttempts), maximum);
        for (var step = 0; step < priorAttempts && nominal < maximum; step++)
            nominal = Math.Min(nominal * settings.BackoffMultiplier, maximum);
        return nominal;
    }
}
