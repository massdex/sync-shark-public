using System.Diagnostics;
using System.Reflection;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;
using Monitor = Massdex.SyncShark.Engine.Run.Monitor;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Checks for Monitor (`--engine-checks repair`): the one host wait that runs a due prerequisite
/// Repair, the listener reconnect deadline and the quiet-status deadline.</summary>
public static class MonitorChecks
{
    private const string Pair = "pair-a";
    private const string Other = "pair-b";
    private const string Connection = "connection-fixture";
    private const string Locked = "locked.txt";
    private const string LocalId = "local-locked";
    private const string Source = "source-a";
    private const string OtherSource = "source-b";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Pending = TimeSpan.FromHours(1);
    private static readonly HttpClient Http = new(new FailingHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly EngineSettings Settings = SettingsParser.Parse(ShippedSettingsJson(), null);

    // Prior failures whose nominal delay has reached the configured backoff cap, derived from the shipped
    // settings rather than tuned, so the next armed fallback cannot fall inside one check's window.
    private static readonly int CappedAttempts = SaturationStage();

    // Each case is one Monitor owner over the real host and an in-memory store: a pair is added, the
    // prerequisite episode is seeded through the store's own failure operation, and the scheduler is driven
    // by due state, coalesced events or a controlled barrier. No sleep and no provider call is used.
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "syncshark-engine-monitor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var product = OverrideProductRoot(root);
            using var instance = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
            var authority = instance.StateAuthority;
            DueEpisodeRunsOneAttempt(authority);
            PendingDeadlineIsNotDue(authority);
            RearmCreatesNoAttempt(authority);
            EventsAdmitOneEarlyAttempt(authority);
            QueuedEventsCoalesceBeforeAdmission(authority);
            HoldsBlockAutomaticRepair(authority);
            PageWaitCreatesNoSchedule(authority);
            InUseItemCreatesNoTimer(authority);
            UnrelatedPairStaysQuiet(authority);
            ListenerWaitIsOnePerSource(authority);
            EventNeverShortensListenerWait(authority);
            QuietStatusPublishesOnce(authority);
            StopJoinsCallbacksAndClosesWaits(authority);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // section 3.10.1: a due persisted prerequisite episode runs its one Repair attempt and that failure advances once.
    private static void DueEpisodeRunsOneAttempt(PilotStateAuthority authority)
    {
        using var rig = NewRig(authority, SavedPair());
        Seed(rig, Pair, CappedAttempts, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
        rig.Monitor.Start();
        WaitUntil(() => rig.Attempts.Count(Pair) == 1, "a due prerequisite episode is admitted");
        var state = rig.Store.ReadPrerequisiteRetry(Pair) ?? throw new InvalidOperationException();
        Check.Require(state.Attempts == CappedAttempts + 1,
            "one failed attempt advances the episode exactly once (observed " + state.Attempts + ")");
        Check.Require(state.RetryAfter > DateTimeOffset.UtcNow,
            "the failed attempt arms its next fallback from the adopted schedule");
        Iterate(rig.Monitor);
        Check.Require(rig.Attempts.Count(Pair) == 1,
            "a completed attempt does not immediately start another Repair");
    }

    // section 3.10.1: a pending generic fallback is not due; the deadline, not the loop, decides.
    private static void PendingDeadlineIsNotDue(PilotStateAuthority authority)
    {
        using var rig = NewRig(authority, SavedPair());
        Seed(rig, Pair, CappedAttempts, DateTimeOffset.UtcNow + Pending);
        rig.Monitor.Start();
        Iterate(rig.Monitor);
        Iterate(rig.Monitor);
        Check.Require(rig.Attempts.Count(Pair) == 0,
            "a pending generic fallback does not run before its own deadline");
        Check.Require(rig.Store.ReadPrerequisiteRetry(Pair) is { } state && state.Attempts == CappedAttempts &&
            state.RetryAfter > DateTimeOffset.UtcNow,
            "a not-due episode is neither reset nor advanced by scheduling");
    }

    // section 4B: RearmPrerequisiteWait only invalidates and recalculates the one wait; it creates no opportunity.
    private static void RearmCreatesNoAttempt(PilotStateAuthority authority)
    {
        using var rig = NewRig(authority, SavedPair());
        Seed(rig, Pair, CappedAttempts, DateTimeOffset.UtcNow + Pending);
        rig.Monitor.Start();
        Iterate(rig.Monitor);
        rig.Monitor.RearmPrerequisiteWait();
        Iterate(rig.Monitor);
        Check.Require(rig.Attempts.Count(Pair) == 0,
            "rearming the wait creates no opportunity: a not-due episode is not attempted");
    }

    // NS6-02: an enabled pair with a failed prerequisite and no provider floor. A real event before the
    // fallback expires admits exactly one reassessment, which is not root approval and not repair completion.
    // shortcut: the host's one-active-attempt record cannot be held open through the current declarations, so
    // overlap is asserted through once-only accounting; add a held-attempt barrier when the host exposes one.
    private static void EventsAdmitOneEarlyAttempt(PilotStateAuthority authority)
    {
        var pair = SavedPair();
        using var rig = NewRig(authority, pair);
        Seed(rig, Pair, CappedAttempts, DateTimeOffset.UtcNow + Pending);
        rig.Monitor.Start();
        Iterate(rig.Monitor);
        Check.Require(rig.Attempts.Count(Pair) == 0, "no attempt before the fallback deadline or a real event");
        rig.Monitor.Wake(Pair); // drive arrival
        WaitUntil(() => rig.Attempts.Count(Pair) == 1, "the real event admits one reassessment");
        var state = rig.Store.ReadPrerequisiteRetry(Pair) ?? throw new InvalidOperationException();
        Check.Require(state.Attempts == CappedAttempts + 1,
            "one event-driven attempt advances the episode exactly once (observed " + state.Attempts + ")");
        Iterate(rig.Monitor);
        Check.Require(rig.Attempts.Count(Pair) == 1,
            "the completed attempt arms a future fallback, so the event starts no immediate second Repair");
        Check.Require(ReferenceEquals(rig.Store.Pair(Pair)?.Roots, pair.Roots),
            "a real event never approves or replaces the pair's configured root binding");
        Check.Require(!rig.Store.FirstRepairDone(Pair) && rig.Store.ReadSource(Pair, Side.Local) is { } local &&
            local.Obligation is not null,
            "the attempt leaves the pair not up to date and its prerequisite obligation outstanding");
    }

    // Events queued before the loop reads them (an already-arrived drive, a restored network and a power
    // resume) coalesce into one attempt; a duplicate burst never advances the episode twice.
    private static void QueuedEventsCoalesceBeforeAdmission(PilotStateAuthority authority)
    {
        using var rig = NewRig(authority, SavedPair());
        Seed(rig, Pair, CappedAttempts, DateTimeOffset.UtcNow + Pending);
        rig.Monitor.Wake(Pair); // drive arrival before the scheduler inspects current state
        rig.Monitor.Wake(Pair); // network restored
        rig.Monitor.Wake(Pair); // power resume
        rig.Monitor.Start();
        WaitUntil(() => rig.Attempts.Count(Pair) == 1, "the queued events admit one reassessment");
        var state = rig.Store.ReadPrerequisiteRetry(Pair) ?? throw new InvalidOperationException();
        Check.Require(state.Attempts == CappedAttempts + 1,
            "a queued burst counts as one failed attempt (observed " + state.Attempts + ")");
        Iterate(rig.Monitor);
        Check.Require(rig.Attempts.Count(Pair) == 1, "the coalesced burst does not repeat the attempt");
    }

    // section 3.10.1a/section 3.10.1: pause and disable are holds that no timer or event turns into an automatic Repair.
    private static void HoldsBlockAutomaticRepair(PilotStateAuthority authority)
    {
        using (var rig = NewRig(authority, SavedPair()))
        {
            Seed(rig, Pair, CappedAttempts, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
            rig.Host.PauseAsync(Pair, CancellationToken.None).GetAwaiter().GetResult();
            rig.Monitor.Start();
            Iterate(rig.Monitor);
            Iterate(rig.Monitor);
            Check.Require(rig.Attempts.Count(Pair) == 0, "a paused pair runs no automatic Repair");
            Check.Require(rig.Store.ReadPrerequisiteRetry(Pair) is { } state && state.Attempts == CappedAttempts,
                "pausing neither advances nor clears the pending episode");
        }

        using (var rig = NewRig(authority, SavedPair(enabled: false)))
        {
            Seed(rig, Pair, CappedAttempts, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
            rig.Monitor.Start();
            Iterate(rig.Monitor);
            Iterate(rig.Monitor);
            Check.Require(rig.Attempts.Count(Pair) == 0, "a disabled pair runs no automatic Repair");
        }
    }

    // section 3.1.2/section 4B: an in-flight listing-page retry is operation-local; it writes no pair Attempts/RetryAfter and
    // raises no pair Repair attempt, and its own success clears only its page-wait notice.
    private static void PageWaitCreatesNoSchedule(PilotStateAuthority authority)
    {
        using var rig = NewRig(authority, SavedPair());
        var generation = Generation(rig.Store, Pair);
        var operation = new object();
        var notice = rig.Store.RecordPrerequisitePageWait(Pair, generation, operation, RetryChange());
        Check.Require(!Scheduled(rig.Store), "a page-wait failure creates no durable pair scheduling entry");
        Check.Require(rig.Store.ReadPrerequisiteRetry(Pair) is null,
            "a page-wait failure writes no pair Attempts/RetryAfter");
        Check.Require(rig.Store.Issues(Pair, [IssueKind.Failure]).Count == 1,
            "the existing page-wait notice is visible from the first failure");
        rig.Monitor.Start();
        Iterate(rig.Monitor);
        Iterate(rig.Monitor);
        Check.Require(rig.Attempts.Count(Pair) == 0,
            "a page-only retry is never scheduled as a pair Repair");
        rig.Store.CompletePrerequisitePageWait(Pair, generation, operation, notice);
        Check.Require(rig.Store.Issues(Pair, [IssueKind.Failure]).Count == 0,
            "page success closes only its page-wait notice");
        Check.Require(rig.Store.ReadPrerequisiteRetry(Pair) is null,
            "page success neither resets nor advances pair debt");
    }

    // section 3.3/section 3.10.1a: a locked item has no due stream, deadline or timer; time alone never attempts it, and the
    // item keeps its durable lifecycle for a real change/CLOSE, Repair or Retry.
    private static void InUseItemCreatesNoTimer(PilotStateAuthority authority)
    {
        using var rig = NewRig(authority, SavedPair());
        var subject = new IssueSubject([new IssueItem(Side.Local, Locked, LocalId)], null);
        var candidate = rig.Store.RecordInUseFailure(Pair, Generation(rig.Store, Pair),
            rig.Store.ReadSource(Pair, Side.Local)!.Value.Epoch, Locked, subject, TextKey.FailLocalInUse,
            null, ItemRetryDispatch.OrdinaryItem, null, null, Guid.NewGuid());
        Check.Require(!Scheduled(rig.Store), "an in-use item episode is not a pair scheduling entry");
        rig.Monitor.Start();
        Iterate(rig.Monitor);
        Iterate(rig.Monitor);
        Check.Require(rig.Attempts.Count(Pair) == 0, "time alone never attempts a locked item");
        var retained = rig.Store.ReadItemRetryState(Pair, [candidate.Token.IssueId]);
        Check.Require(retained.TryGetValue(candidate.Token.IssueId, out var current) && current is not null &&
            current.Token.WorkId == candidate.Token.WorkId,
            "the locked item keeps its durable lifecycle for a real opportunity");
    }

    // section 3.10.1: an event is scoped to its actual pair/source; it retries no unrelated pair.
    private static void UnrelatedPairStaysQuiet(PilotStateAuthority authority)
    {
        using var rig = NewRig(authority, SavedPair(), SavedPair(Other));
        Seed(rig, Pair, CappedAttempts, DateTimeOffset.UtcNow + Pending);
        Seed(rig, Other, CappedAttempts, DateTimeOffset.UtcNow + Pending);
        rig.Monitor.Start();
        Iterate(rig.Monitor);
        rig.Monitor.Wake(Other);
        WaitUntil(() => rig.Attempts.Count(Other) == 1, "the event admits its own pair");
        Iterate(rig.Monitor);
        Check.Require(rig.Attempts.Count(Pair) == 0, "an event for one pair never retries an unrelated pair");
        Check.Require(rig.Store.ReadPrerequisiteRetry(Pair) is { } state && state.Attempts == CappedAttempts,
            "the unrelated pair's episode is unchanged");
    }

    // section 3.10.1: one outstanding listener wait per source; disposing it releases that source for the next
    // reconnect, and independent sources reconnect independently.
    private static void ListenerWaitIsOnePerSource(PilotStateAuthority authority)
    {
        using var rig = NewRig(authority);
        rig.Monitor.Start();
        var held = 0;
        var waiting = rig.Monitor.ScheduleListenerReconnect(Source, Pending,
            _ => { Interlocked.Increment(ref held); return Task.CompletedTask; });
        Check.Require(Refused<InvalidOperationException>(() =>
                rig.Monitor.ScheduleListenerReconnect(Source, Pending, _ => Task.CompletedTask)),
            "one outstanding listener wait per source: a second subscriber is refused");
        waiting.Dispose();
        Iterate(rig.Monitor);
        Check.Require(Volatile.Read(ref held) == 0, "a disposed listener wait never reconnects");
        var again = 0;
        var reconnect = rig.Monitor.ScheduleListenerReconnect(Source, TimeSpan.Zero,
            _ => { Interlocked.Increment(ref again); return Task.CompletedTask; });
        WaitUntil(() => Volatile.Read(ref again) == 1, "the released source reconnects once");
        reconnect.Dispose();
        var left = 0;
        var right = 0;
        using var first = rig.Monitor.ScheduleListenerReconnect(Source, TimeSpan.Zero,
            _ => { Interlocked.Increment(ref left); return Task.CompletedTask; });
        using var second = rig.Monitor.ScheduleListenerReconnect(OtherSource, TimeSpan.Zero,
            _ => { Interlocked.Increment(ref right); return Task.CompletedTask; });
        WaitUntil(() => Volatile.Read(ref left) == 1 && Volatile.Read(ref right) == 1,
            "two live sources reconnect independently");
    }

    // section 3.10.1: a provider floor is owned by the feed; a notification for that source or another source never
    // shortens the registered wait.
    private static void EventNeverShortensListenerWait(PilotStateAuthority authority)
    {
        using var rig = NewRig(authority);
        rig.Monitor.Start();
        var fired = 0;
        using var wait = rig.Monitor.ScheduleListenerReconnect(Source, Pending,
            _ => { Interlocked.Increment(ref fired); return Task.CompletedTask; });
        rig.Monitor.WakeSource(OtherSource);
        rig.Monitor.WakeSource(Source);
        Iterate(rig.Monitor);
        Iterate(rig.Monitor);
        Check.Require(Volatile.Read(ref fired) == 0,
            "a notification never shortens a registered listener/provider wait");
    }

    // section 3.11.1: a quiet deadline publishes its one signal, and a withdrawn deadline publishes nothing.
    private static void QuietStatusPublishesOnce(PilotStateAuthority authority)
    {
        using var rig = NewRig(authority);
        rig.Monitor.Start();
        var published = 0;
        using var quiet = rig.Monitor.ScheduleQuietStatus(TimeSpan.Zero,
            () => Interlocked.Increment(ref published));
        WaitUntil(() => Volatile.Read(ref published) == 1, "the quiet deadline publishes once");
        Iterate(rig.Monitor);
        Check.Require(Volatile.Read(ref published) == 1,
            "one quiet deadline publishes exactly once");
        var withdrawn = 0;
        var late = rig.Monitor.ScheduleQuietStatus(Pending, () => Interlocked.Increment(ref withdrawn));
        late.Dispose();
        Iterate(rig.Monitor);
        Check.Require(Volatile.Read(ref withdrawn) == 0, "a withdrawn quiet deadline publishes nothing");
    }

    // section 3.10.1 final paragraph: stop cancels and joins an in-flight callback, and scheduling during or after
    // stop is refused rather than accepted into a dead loop.
    private static void StopJoinsCallbacksAndClosesWaits(PilotStateAuthority authority)
    {
        using var rig = NewRig(authority);
        rig.Monitor.Start();
        Check.Require(Refused<ArgumentException>(() => rig.Monitor.Wake(string.Empty)),
            "an empty pair id is refused");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inFlight = rig.Monitor.ScheduleListenerReconnect(Source, TimeSpan.Zero, async _ =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        });
        WaitUntil(() => entered.Task.IsCompleted, "the due listener callback starts");
        var stopping = rig.Monitor.StopAsync();
        Check.Require(!stopping.IsCompleted, "Stop joins an in-flight callback instead of abandoning it");
        release.TrySetResult();
        WaitUntil(() => stopping.IsCompleted, "Stop completes once the callback finishes");
        stopping.GetAwaiter().GetResult();
        Check.Require(Refused<OperationCanceledException>(() =>
                rig.Monitor.ScheduleListenerReconnect(Source, TimeSpan.Zero, _ => Task.CompletedTask)),
            "a stopped scheduler refuses a new listener wait");
        Check.Require(Refused<OperationCanceledException>(() =>
                rig.Monitor.ScheduleQuietStatus(TimeSpan.Zero, () => { })),
            "a stopped scheduler refuses a new quiet deadline");
    }

    private static Rig NewRig(PilotStateAuthority authority, params PairConfig[] pairs)
    {
        var store = new InMemoryStateStore();
        foreach (var pair in pairs) store.SavePair(pair);
        var host = EngineHost.Compose(authority.ProductRoot, _ => { }, authority, Settings, Http,
            TimeProvider.System, _ => null, _ => store, EngineHost.Mode.Direct);
        var attempts = new Attempts();
        var monitor = new Monitor(Settings, TimeProvider.System,
            attempts.Open, host.IsPaused, host);
        return new Rig(store, host, monitor, attempts);
    }

    // The real failure carrier for one failed prerequisite attempt: the same pair-root notice the host raises.
    private static void Seed(Rig rig, string pairId, int attempts, DateTimeOffset retryAfter)
    {
        var execution = rig.Store.ReadPairExecution(pairId) ?? throw new InvalidOperationException();
        var local = rig.Store.ReadSource(pairId, Side.Local) ?? throw new InvalidOperationException();
        var dropbox = rig.Store.ReadSource(pairId, Side.Dropbox) ?? throw new InvalidOperationException();
        rig.Store.RecordPrerequisiteFailure(pairId, execution.Generation, local.Epoch, dropbox.Epoch,
            local.Revision, dropbox.Revision, rig.Store.ReadPrerequisiteRetry(pairId),
            new FailureRetryOwner.State(attempts, retryAfter), RetryChange());
    }

    private static StateChange RetryChange() => StateChange.Empty with
    {
        Raise = [(IssueKind.Failure, TextKey.RequiredRepairRetry, string.Empty, new IssueSubject([], null))]
    };

    private static long Generation(IStateStore store, string pairId) =>
        (store.ReadPairExecution(pairId) ?? throw new InvalidOperationException()).Generation;

    private static bool Scheduled(IStateStore store) =>
        store.ReadPrerequisiteRetries(null, null, Settings.StoragePageSize).Any();

    // The same capped multiplication the retry owner applies: one stage per multiplication until the cap.
    private static int SaturationStage()
    {
        var backoff = Settings.Backoff;
        var nominal = backoff.InitialBackoffSeconds;
        var stage = 0;
        while (nominal < backoff.MaximumBackoffSeconds)
        {
            var next = Math.Min(nominal * backoff.BackoffMultiplier, backoff.MaximumBackoffSeconds);
            if (next <= nominal) break;
            nominal = next;
            stage += 1;
        }
        return stage;
    }

    // One scheduler iteration: a zero-delay quiet deadline is published by the same loop, after that
    // iteration's due pair attempts were admitted, so it is a deterministic barrier with no sleep.
    private static void Iterate(Monitor monitor)
    {
        var published = 0;
        using var deadline = monitor.ScheduleQuietStatus(TimeSpan.Zero,
            () => Interlocked.Increment(ref published));
        WaitUntil(() => Volatile.Read(ref published) == 1, "the scheduler completes one iteration");
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var bound = Stopwatch.StartNew();
        while (!condition())
        {
            if (bound.Elapsed > Bound) throw new InvalidOperationException("Timed out: " + what);
            Thread.Yield();
        }
    }

    private static bool Refused<TError>(Action action) where TError : Exception
    {
        try { action(); return false; }
        catch (Exception error) when (error is TError) { return true; }
    }

    private static PairConfig SavedPair(string id = Pair, bool enabled = true) => new(
        id, id, @"C:\MonitorFixture", "/MonitorFixture", Connection, [], [], FirstRunPolicy.NewestWins, enabled,
        null, Binding());

    private static RootBinding Binding() => new(@"C:\MonitorFixture", "/MonitorFixture", Connection,
        new DirectoryIdentity(1UL, 2UL, null),
        new DropboxRootIdentity("account-fixture", "namespace-fixture", "home-fixture", "folder-fixture"));

    private static string ShippedSettingsJson()
    {
        using var document = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        return document.RootElement.GetRawText();
    }

    // Core's test hook redirects the product lock and state folder to a temporary root; the engine test assembly
    // is not on Core's InternalsVisibleTo list.
    private static IDisposable OverrideProductRoot(string root) =>
        (IDisposable)typeof(SyncSharkRuntimePaths)
            .GetMethod("OverrideProductRootForTest", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { root })!;

    private sealed class Rig(InMemoryStateStore store, EngineHost host, Monitor monitor, Attempts attempts) : IDisposable
    {
        internal InMemoryStateStore Store { get; } = store;
        internal EngineHost Host { get; } = host;
        internal Monitor Monitor { get; } = monitor;
        internal Attempts Attempts { get; } = attempts;

        public void Dispose()
        {
            Monitor.StopAsync().GetAwaiter().GetResult();
            Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // The host logs each prerequisite error the monitor observed; that is the observable admission of one
    // reassessment, so the count is read from the declared log factory rather than from internal loop state.
    private sealed class Attempts
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        internal RunLog? Open(string pairId)
        {
            lock (_gate) _counts[pairId] = _counts.GetValueOrDefault(pairId) + 1;
            return null;
        }

        internal int Count(string pairId)
        {
            lock (_gate) return _counts.GetValueOrDefault(pairId);
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Monitor checks never call a provider: " + request.RequestUri);
    }
}
