using System.Net;
using System.Reflection;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checks for AdaptiveLimiter (`--engine-checks limiter`): ordinary admission, the account work window,
/// upload admission and growth, single-attempt provider floors and feedback (BATCHING 3.1-3.4, READING 8.5, D1).</summary>
public static class AdaptiveLimiterChecks
{
    private const int Initial = 4;
    private const int OneSlot = 1;
    private const int TwoSlots = 2;
    // The two sides of the provider's single-request limit, which the limiter now holds for its callers.
    private const long FitsDirect = DropboxApiLimits.SingleUploadMaxBytes;
    private const long TooLargeForDirect = DropboxApiLimits.SingleUploadMaxBytes + 1;
    private const int SavedLevel = 8;
    private const int ShortWait = 2;
    private const int LongWait = 8;
    private const int CapWaitSeconds = 300;
    private const int WorkWindowBatches = 2;
    private const int Burst = 32;
    private const int PriorCommitStage = 3;
    private const int DisplacementRounds = 200;
    private const double BackoffInitialSeconds = 2;
    private const double BackoffMultiplier = 2;
    private const double BackoffJitter = 0;
    private const double MaximumBackoffSeconds = 64;

    // Account identities are distinct where a provider floor is installed: the floor owner keeps them for the process.
    private const string Account = "account";
    private const string Source = "source";
    private const string UploadAccount = "upload";
    private const string FloorAccount = "floor";
    private const string CapAccount = "cap";
    private const string BurstAccount = "burst";
    private const string SemanticAccount = "semantic";
    private const string HttpFloorAccount = "http-floor";
    private const string BatchAccount = "batch";
    private const string SharedAccount = "shared-account";
    private const string OtherAccount = "other-account";

    private static readonly FailureRetryOwner.BackoffSettings Backoff =
        new(BackoffInitialSeconds, BackoffMultiplier, BackoffJitter, MaximumBackoffSeconds);

    /// <summary>Runs admission, work-window, upload, throttling and network checks.</summary>
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        Growth();
        ReadGrowth();
        Validation();
        await AdmissionAsync();
        await QueueOrderAsync();
        await DisplacedHeadAsync();
        await WorkWindowAsync();
        await UploadAdmissionAsync();
        await UploadGrowthAsync();
        await SingleAttemptAsync();
        await ProviderFloorAsync();
        await HttpFeedbackAsync();
        await ProviderFloorBurstReducesOnceAsync();
        await ProviderFloorHoldsWholeAccountAsync();
        NoWaitBurstLevel();
        await SemanticThrottleDuringExistingProviderFloorAsync();
        BatchFeedback();
        await NetworkAsync();
    }

    private static void ReadGrowth()
    {
        var limiter = Create(new ManualTime(), start: OneSlot);
        var identity = Identity("read-growth", DropboxProtocol.GetMetadata);
        limiter.ReportHttpOutcome(identity, (RequestResult.Success, false), null, null, isRead: true);
        Check.Require(limiter.Level == TwoSlots, "A successful observation grows admission from its initial single slot.");
        limiter.ReportHttpOutcome(identity, (RequestResult.Success, false), null, null, isRead: false);
        limiter.ReportHttpOutcome(identity, (RequestResult.PermanentFailure, false), null, null, isRead: true);
        Check.Require(limiter.Level == TwoSlots, "Writes and failed observations do not add read growth.");
        limiter.ReportHttpOutcome(identity, (RequestResult.TransientFailure, false), false, null, isRead: true);
        Check.Require(limiter.Level == OneSlot, "A request throttle still shrinks read-grown admission.");
        limiter.ReportHttpOutcome(identity, (RequestResult.Success, false), null, null, isRead: true);
        Check.Require(limiter.Level == TwoSlots, "A successful read resumes additive growth after a throttle.");
    }

    private static AdaptiveLimiter Create(ManualTime time, Action<WaitEvent>? waited = null,
        FakeNetworkSignal? network = null, int start = Initial, int upload = Initial) =>
        new(start, upload, time, network ?? new FakeNetworkSignal(), waited ?? (_ => { }), Backoff);

    private static (string AccountId, string RouteName, string SourceIdentity) Identity(string account, string route) =>
        (account, route, Source);

    private static object Gate(AdaptiveLimiter limiter) =>
        typeof(AdaptiveLimiter).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(limiter)!;

    private static Task<HttpResponseMessage> Send(AdaptiveLimiter limiter,
        (string AccountId, string RouteName, string SourceIdentity) identity,
        Func<HttpRequestMessage, HttpCompletionOption, CancellationToken, Task<HttpResponseMessage>> handler,
        CancellationToken ct = default, bool read = true, Func<HttpRequestMessage>? build = null) =>
        limiter.SendAsync(read, identity, () => Task.FromResult(build is null ? new HttpRequestMessage() : build()), handler,
            HttpCompletionOption.ResponseHeadersRead, ct);

    private static Task<HttpResponseMessage> Dispatch(AdaptiveLimiter limiter,
        (string AccountId, string RouteName, string SourceIdentity) identity, Action? sent = null,
        CancellationToken ct = default) =>
        Send(limiter, identity, (_, _, _) =>
        {
            sent?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }, ct);

    // True only when admission let an ordinary send through immediately; a granted response is released here.
    private static bool Dispatches(AdaptiveLimiter limiter,
        (string AccountId, string RouteName, string SourceIdentity) identity)
    {
        var send = Dispatch(limiter, identity);
        if (!send.IsCompletedSuccessfully) return false;
        send.Result.Dispose();
        return true;
    }

    private static void Growth()
    {
        var limiter = Create(new ManualTime());
        Check.Require(limiter.Level == Initial, nameof(Growth));
        limiter.ActionCompleted(false);
        Check.Require(limiter.Level == Initial, nameof(Growth));
        foreach (var round in new[] { Initial, Initial * AdaptiveLimiter.GrowthFactor })
        {
            for (var count = 1; count < round; count += 1)
            {
                limiter.ActionCompleted(true);
                Check.Require(limiter.Level == round, nameof(Growth));
            }
            limiter.ActionCompleted(true);
            Check.Require(limiter.Level == round * AdaptiveLimiter.GrowthFactor, nameof(Growth));
        }
    }

    private static void Validation()
    {
        Check.Require(Throws<ArgumentOutOfRangeException>(() => Create(new ManualTime(), start: 0)),
            "A zero ordinary start level is refused.");
        Check.Require(Throws<ArgumentOutOfRangeException>(() => Create(new ManualTime(), upload: 0)),
            "A zero upload start level is refused.");
        Check.Require(Create(new ManualTime()).WorkCapacity == WorkWindowBatches * DropboxApiLimits.BatchMaxEntries,
            "The work window is derived from the provider batch fact.");
    }

    private static async Task AdmissionAsync()
    {
        var limiter = Create(new ManualTime(), start: OneSlot);
        var first = await limiter.AdmitAsync(false, CancellationToken.None);
        var ordinary = limiter.AdmitAsync(false, CancellationToken.None);
        var priority = limiter.AdmitAsync(true, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelled = limiter.AdmitAsync(true, cancellation.Token);
        cancellation.Cancel();
        await CancelledAsync(cancelled);
        Check.Require(!ordinary.IsCompleted && !priority.IsCompleted, nameof(AdmissionAsync));
        await first.DisposeAsync();
        await first.DisposeAsync();
        var next = await priority;
        Check.Require(!ordinary.IsCompleted, nameof(AdmissionAsync));
        await next.DisposeAsync();
        await (await ordinary).DisposeAsync();
        await using var last = await limiter.AdmitAsync(false, CancellationToken.None);
    }

    private static async Task QueueOrderAsync()
    {
        var limiter = Create(new ManualTime(), start: OneSlot);
        var held = await limiter.AdmitAsync(false, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelledOrdinary = limiter.AdmitAsync(false, cancellation.Token);
        var ordinary = Enumerable.Range(0, Initial).Select(_ => limiter.AdmitAsync(false, CancellationToken.None)).ToArray();
        var cancelledHead = limiter.AdmitAsync(true, cancellation.Token);
        var priority = Enumerable.Range(0, Initial).Select(_ => limiter.AdmitAsync(true, CancellationToken.None)).ToArray();
        cancellation.Cancel();
        await CancelledAsync(cancelledHead);
        await CancelledAsync(cancelledOrdinary);
        Check.Require(priority.Concat(ordinary).All(task => !task.IsCompleted), nameof(QueueOrderAsync));
        await held.DisposeAsync();
        var ordered = priority.Concat(ordinary).ToArray();
        for (var index = 0; index < ordered.Length; index += 1)
        {
            var permit = await ordered[index].WaitAsync(TimeSpan.FromSeconds(LongWait));
            Check.Require(ordered.Skip(index + 1).All(task => !task.IsCompleted),
                "Priority precedes ordinary work, with FIFO admission within each queue.");
            await permit.DisposeAsync();
        }
        Check.Require(limiter.Level == OneSlot, "Admission and release alone must not grow the level.");
    }

    private static async Task DisplacedHeadAsync()
    {
        for (var round = 0; round < DisplacementRounds; round += 1)
        {
            var limiter = Create(new ManualTime(), start: OneSlot);
            var held = await limiter.AdmitAsync(false, CancellationToken.None);
            var ordinary = limiter.AdmitAsync(false, CancellationToken.None);
            var gate = Gate(limiter);
            Task<IAsyncDisposable> priority;
            // Hold the gate to force priority admission before the signalled ordinary continuation.
            lock (gate)
            {
                Check.Require(held.DisposeAsync().IsCompletedSuccessfully, "Permit release is synchronous.");
                priority = limiter.AdmitAsync(true, CancellationToken.None);
                Check.Require(priority.IsCompletedSuccessfully, "Priority takes the available slot.");
            }
            await Task.Yield();
            Check.Require(!ordinary.IsCompleted, "The signalled ordinary head cannot bypass priority.");
            await (await priority).DisposeAsync();
            await (await ordinary.WaitAsync(TimeSpan.FromSeconds(LongWait))).DisposeAsync();
        }
    }

    private static async Task WorkWindowAsync()
    {
        var limiter = Create(new ManualTime(), start: OneSlot);
        Check.Require(limiter.WorkCapacity == WorkWindowBatches * DropboxApiLimits.BatchMaxEntries, nameof(WorkWindowAsync));
        Check.Require(!limiter.SelectDirect(FitsDirect), "Zero work is not a singleton opportunity.");

        await using (var sole = await limiter.EnterWorkAsync(true, CancellationToken.None))
        {
            Check.Require(limiter.SelectDirect(FitsDirect), "The sole single-file scope may select a direct upload.");
            Check.Require(!limiter.SelectDirect(TooLargeForDirect), "The limiter holds the single-request size limit.");
            limiter.DisallowDirectUpload();
            Check.Require(!limiter.SelectDirect(FitsDirect), "Revocation is monotonic and never grants eligibility.");
        }

        var compositeLimiter = Create(new ManualTime());
        await using (var composite = await compositeLimiter.EnterWorkAsync(false, CancellationToken.None))
            Check.Require(!compositeLimiter.SelectDirect(FitsDirect), "A composite scope is never a singleton opportunity.");

        var held = new List<IAsyncDisposable>();
        for (var index = 0; index < limiter.WorkCapacity; index += 1)
            held.Add(await limiter.EnterWorkAsync(true, CancellationToken.None));
        Check.Require(!limiter.SelectDirect(FitsDirect), "A full work window is not a singleton opportunity.");

        var head = limiter.EnterWorkAsync(true, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelled = limiter.EnterWorkAsync(false, cancellation.Token);
        cancellation.Cancel();
        await CancelledAsync(cancelled.WaitAsync(TimeSpan.FromSeconds(LongWait)));
        Check.Require(!head.IsCompleted, "Cancelling a later waiter does not grant the head early.");

        await held[0].DisposeAsync();
        var granted = await head.WaitAsync(TimeSpan.FromSeconds(LongWait));
        Check.Require(!limiter.SelectDirect(FitsDirect), "Releasing one slot does not restore singleton eligibility.");
        await granted.DisposeAsync();
        foreach (var scope in held.Skip(1)) await scope.DisposeAsync();
        Check.Require(!limiter.SelectDirect(FitsDirect), "An empty window without an owned scope cannot select.");

        await using (var fresh = await limiter.EnterWorkAsync(true, CancellationToken.None))
            Check.Require(limiter.SelectDirect(FitsDirect), "A complete drain restores singleton eligibility.");
    }

    private static async Task UploadAdmissionAsync()
    {
        var identity = Identity(Account, DropboxProtocol.UploadSessionStart);
        var limiter = Create(new ManualTime(), start: SavedLevel, upload: TwoSlots);
        var first = limiter.AdmitUploadAsync(identity, CancellationToken.None);
        var second = limiter.AdmitUploadAsync(identity, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var third = limiter.AdmitUploadAsync(identity, cancellation.Token);
        Check.Require(first.IsCompletedSuccessfully && second.IsCompletedSuccessfully && !third.IsCompleted,
            "The upload pool starts from its own configured level.");
        Check.Require(limiter.Level == SavedLevel, "A saved ordinary level does not seed the upload pool.");
        cancellation.Cancel();
        await CancelledAsync(third.WaitAsync(TimeSpan.FromSeconds(LongWait)));
        await (await first).DisposeAsync();
        await (await second).DisposeAsync();

        var fifo = Create(new ManualTime(), start: OneSlot, upload: OneSlot);
        var held = await fifo.AdmitUploadAsync(identity, CancellationToken.None);
        var ordinary = fifo.AdmitUploadAsync(identity, CancellationToken.None);
        var later = fifo.AdmitUploadAsync(identity, CancellationToken.None);
        await held.DisposeAsync();
        var firstUp = await ordinary.WaitAsync(TimeSpan.FromSeconds(LongWait));
        Check.Require(!later.IsCompleted, "Upload admission is FIFO.");
        await firstUp.DisposeAsync();
        await (await later.WaitAsync(TimeSpan.FromSeconds(LongWait))).DisposeAsync();

        var time = new ManualTime();
        var pausedIdentity = Identity(UploadAccount, DropboxProtocol.UploadSessionStart);
        var paused = Create(time, start: OneSlot, upload: TwoSlots);
        var duration = TimeSpan.FromSeconds(ShortWait);
        paused.ReportHttpOutcome(pausedIdentity, (RequestResult.TransientFailure, false), false,
            DropboxProtocol.ProviderConstraintFor(UploadAccount, duration, time.GetTimestamp()));
        var waiting = paused.AdmitUploadAsync(pausedIdentity, CancellationToken.None);
        Check.Require(!waiting.IsCompleted, "A provider pause holds upload admission.");
        time.Advance(duration);
        await (await waiting.WaitAsync(TimeSpan.FromSeconds(LongWait))).DisposeAsync();
    }

    private static async Task UploadGrowthAsync()
    {
        var identity = Identity(Account, DropboxProtocol.UploadSessionStart);
        var limiter = Create(new ManualTime(), start: OneSlot, upload: OneSlot);
        limiter.UploadStaged();
        await RequireUploadSlotsAsync(limiter, TwoSlots, identity);
        limiter.UploadStaged();
        limiter.UploadStaged();
        await RequireUploadSlotsAsync(limiter, Initial, identity);
        Check.Require(limiter.Level == OneSlot, "Upload growth never changes the ordinary level.");

        var slowed = Create(new ManualTime(), start: OneSlot, upload: Initial);
        slowed.ReportHttpOutcome(identity, (RequestResult.TransientFailure, false), false, null);
        await RequireUploadSlotsAsync(slowed, TwoSlots, identity);
        slowed.UploadStaged();
        await RequireUploadSlotsAsync(slowed, Initial - OneSlot, identity);
        slowed.UploadStaged();
        await RequireUploadSlotsAsync(slowed, Initial, identity);

        var write = Create(new ManualTime(), start: OneSlot, upload: TwoSlots);
        write.ReportHttpOutcome(identity, (RequestResult.TransientFailure, false), true, null);
        await RequireUploadSlotsAsync(write, TwoSlots, identity);
        write.UploadStaged();
        write.UploadStaged();
        write.UploadStaged();
        await RequireUploadSlotsAsync(write, Initial, identity);

        var capacity = WorkWindowBatches * DropboxApiLimits.BatchMaxEntries;
        var capped = Create(new ManualTime(), start: OneSlot, upload: capacity + OneSlot);
        capped.UploadStaged();
        var permits = new List<IAsyncDisposable>();
        for (var index = 0; index < capped.WorkCapacity; index += 1)
            permits.Add(await capped.AdmitUploadAsync(identity, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        var overflow = capped.AdmitUploadAsync(identity, cancellation.Token);
        Check.Require(!overflow.IsCompleted, "Upload growth is capped at the work window.");
        cancellation.Cancel();
        await CancelledAsync(overflow.WaitAsync(TimeSpan.FromSeconds(LongWait)));
        foreach (var permit in permits) await permit.DisposeAsync();
    }

    private static async Task SingleAttemptAsync()
    {
        var identity = Identity(Account, DropboxProtocol.UploadSessionFinishBatch);
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
        {
            var waits = new List<WaitEvent>();
            var limiter = Create(new ManualTime(), waits.Add);
            var attempts = 0;
            using var result = await Send(limiter, identity, (request, completion, ct) =>
            {
                Check.Require(completion == HttpCompletionOption.ResponseHeadersRead, nameof(SingleAttemptAsync));
                attempts += 1;
                return Task.FromResult(new HttpResponseMessage(status));
            });
            Check.Require(attempts == 1, "A reply makes one network attempt and is never resent.");
            Check.Require(result.StatusCode == status, nameof(SingleAttemptAsync));
            Check.Require(limiter.Level == Initial && waits.Count == 0,
                "Reply classification and feedback belong to the HTTP owner, not the send gate.");
        }
    }

    private static async Task ProviderFloorAsync()
    {
        var time = new ManualTime();
        var waits = new List<WaitEvent>();
        var limiter = Create(time, waits.Add);
        var identity = Identity(FloorAccount, DropboxProtocol.UploadSessionFinishBatch);
        var duration = TimeSpan.FromSeconds(ShortWait);
        limiter.ReportHttpOutcome(identity, (RequestResult.TransientFailure, false), false,
            DropboxProtocol.ProviderConstraintFor(FloorAccount, duration, time.GetTimestamp()));
        Check.Require(waits.Count == OneSlot && waits[0] == new WaitEvent(TextKey.WaitTooManyRequests, duration),
            nameof(ProviderFloorAsync));
        var pendingCalls = 0;
        var pending = Dispatch(limiter, identity, () => pendingCalls += 1);
        Check.Require(!pending.IsCompleted && pendingCalls == 0, "A provider floor pauses later requests.");
        time.Advance(duration);
        using var second = await pending.WaitAsync(TimeSpan.FromSeconds(LongWait));
        Check.Require(pendingCalls == 1, nameof(ProviderFloorAsync));

        var zeroTime = new ManualTime();
        var zeroWaits = new List<WaitEvent>();
        var zero = Create(zeroTime, zeroWaits.Add);
        var zeroIdentity = Identity(Account, DropboxProtocol.UploadSessionFinishBatch);
        zero.ReportHttpOutcome(zeroIdentity, (RequestResult.TransientFailure, false), false,
            DropboxProtocol.ProviderConstraintFor(Account, TimeSpan.Zero, zeroTime.GetTimestamp()));
        Check.Require(zeroWaits.Count == OneSlot && zeroWaits[0].Wait == TimeSpan.Zero,
            "A zero provider wait is supplied precedence, not an absence.");
        Check.Require(Dispatches(zero, zeroIdentity), "A zero provider wait pauses nothing.");

        var capTime = new ManualTime();
        var capWaits = new List<WaitEvent>();
        var cap = Create(capTime, capWaits.Add);
        var capIdentity = Identity(CapAccount, DropboxProtocol.UploadSessionFinishBatch);
        var cappedWait = TimeSpan.FromSeconds(CapWaitSeconds);
        cap.ReportHttpOutcome(capIdentity, (RequestResult.TransientFailure, false), false,
            DropboxProtocol.ProviderConstraintFor(CapAccount, cappedWait, capTime.GetTimestamp()));
        Check.Require(capWaits.Count == OneSlot && capWaits[0].Wait == cappedWait,
            "A provider wait above the generic cap is kept.");

        var missingWaits = new List<WaitEvent>();
        var missing = Create(new ManualTime(), missingWaits.Add);
        missing.ReportHttpOutcome(zeroIdentity, (RequestResult.TransientFailure, false), false, null);
        Check.Require(missingWaits.Count == OneSlot && missingWaits[0].Reason == TextKey.WaitTooManyRequests,
            "A rate reply with no decodable wait is still one request-rate event.");
    }

    private static async Task HttpFeedbackAsync()
    {
        var identity = Identity(Account, DropboxProtocol.UploadSessionFinishBatch);

        var rateWaits = new List<WaitEvent>();
        var rate = Create(new ManualTime(), rateWaits.Add);
        rate.ReportHttpOutcome(identity, (RequestResult.TransientFailure, false), false, null);
        Check.Require(rate.Level == Initial / AdaptiveLimiter.ShrinkDivisor,
            "A first request throttle halves the ordinary level.");
        Check.Require(rateWaits.Count == OneSlot && rateWaits[0] == new WaitEvent(TextKey.WaitTooManyRequests, TimeSpan.Zero),
            "A request throttle with no provider wait reports a zero request wait.");
        Check.Require(Dispatches(rate, identity), "A generic failure arms no account floor of its own.");

        var writeWaits = new List<WaitEvent>();
        var write = Create(new ManualTime(), writeWaits.Add);
        write.ReportHttpOutcome(identity, (RequestResult.TransientFailure, false), true, null);
        Check.Require(write.Level == Initial, "Write contention alone never halves a level.");
        Check.Require(writeWaits.Count == OneSlot && writeWaits[0] == new WaitEvent(TextKey.WaitTooManyWrites, TimeSpan.Zero),
            "Write contention with no provider wait reports a zero write wait.");
        Check.Require(Dispatches(write, identity), "Write contention with no provider wait arms no account pause.");

        var unknownTime = new ManualTime();
        var unknownWaits = new List<WaitEvent>();
        var unknown = Create(unknownTime, unknownWaits.Add);
        unknown.ReportHttpOutcome(identity, (RequestResult.UnknownWrite, false), true, null);
        Check.Require(unknownWaits.Count == OneSlot && unknownWaits[0].Reason == TextKey.WaitTooManyWrites
            && unknownWaits[0].Wait > TimeSpan.Zero, "An unknown write keeps its own selected account pause.");
        Check.Require(unknown.Level == Initial, "An unknown-write pause never halves a level.");
        var paused = Dispatch(unknown, identity);
        Check.Require(!paused.IsCompleted, "The selected account pause holds later sends.");
        unknownTime.Advance(unknownWaits[0].Wait);
        using var resumed = await paused.WaitAsync(TimeSpan.FromSeconds(LongWait));
        Check.Require(resumed.StatusCode == HttpStatusCode.OK, nameof(HttpFeedbackAsync));

        var floorTime = new ManualTime();
        var floorWaits = new List<WaitEvent>();
        var floor = Create(floorTime, floorWaits.Add);
        var floorIdentity = Identity(HttpFloorAccount, DropboxProtocol.UploadSessionFinishBatch);
        var duration = TimeSpan.FromSeconds(ShortWait);
        floor.ReportHttpOutcome(floorIdentity, (RequestResult.TransientFailure, false), false,
            DropboxProtocol.ProviderConstraintFor(HttpFloorAccount, duration, floorTime.GetTimestamp()));
        Check.Require(floorWaits.Count == OneSlot && floorWaits[0] == new WaitEvent(TextKey.WaitTooManyRequests, duration),
            "A request throttle reports the provider wait it carried.");
        floor.ReportHttpOutcome(floorIdentity, (RequestResult.Success, false), null, null);
        var held = Dispatch(floor, floorIdentity);
        Check.Require(!held.IsCompleted, "A later success does not clear an existing provider floor.");
        floorTime.Advance(duration);
        using var released = await held.WaitAsync(TimeSpan.FromSeconds(LongWait));
        Check.Require(released.StatusCode == HttpStatusCode.OK, nameof(HttpFeedbackAsync));
    }

    // B-70 §14 H2: a provider wait reported on pair P1's request holds P2's request on the same account, from another
    // source, until it ends; P3 on another account is unaffected.
    private static async Task ProviderFloorHoldsWholeAccountAsync()
    {
        var time = new ManualTime();
        var account = Create(time);
        var other = Create(time);
        const string Route = DropboxProtocol.UploadSessionFinishBatch;
        var duration = TimeSpan.FromSeconds(ShortWait);
        account.ReportHttpOutcome((SharedAccount, Route, "pair-1"), (RequestResult.TransientFailure, false), false,
            DropboxProtocol.ProviderConstraintFor(SharedAccount, duration, time.GetTimestamp()));
        var held = Dispatch(account, (SharedAccount, Route, "pair-2"));
        Check.Require(!held.IsCompleted, "H2 P2 on the same account sends no request before R");
        Check.Require(Dispatches(other, (OtherAccount, Route, "pair-3")), "H2 P3 on another account is unaffected");
        time.Advance(duration);
        using var released = await held.WaitAsync(TimeSpan.FromSeconds(LongWait));
        Check.Require(released.StatusCode == HttpStatusCode.OK, "H2 P2 sends once R has passed");
    }

    private static async Task ProviderFloorBurstReducesOnceAsync()
    {
        var time = new ManualTime();
        var waits = new List<WaitEvent>();
        var limiter = Create(time, waits.Add, start: Burst);
        var identity = Identity(BurstAccount, DropboxProtocol.UploadSessionFinishBatch);
        var duration = TimeSpan.FromSeconds(ShortWait);
        for (var index = 0; index < Burst; index += 1)
            limiter.ReportHttpOutcome(identity, (RequestResult.TransientFailure, false), false,
                DropboxProtocol.ProviderConstraintFor(BurstAccount, duration, time.GetTimestamp()));
        Check.Require(limiter.Level == Burst / AdaptiveLimiter.ShrinkDivisor,
            "Concurrent positive rate replies reduce the level once for the first busy period.");
        Check.Require(waits.Count == Burst && waits.All(wait => wait == new WaitEvent(TextKey.WaitTooManyRequests, duration)),
            "Every burst reply still reports its own provider wait.");
        using var cancellation = new CancellationTokenSource();
        var pending = Dispatch(limiter, identity, ct: cancellation.Token);
        Check.Require(!pending.IsCompleted, "The burst leaves one account floor in force.");
        cancellation.Cancel();
        await CancelledAsync(pending);
    }

    private static void NoWaitBurstLevel()
    {
        var waits = new List<WaitEvent>();
        var limiter = Create(new ManualTime(), waits.Add, start: Burst);
        var identity = Identity(Account, DropboxProtocol.UploadSessionFinishBatch);
        for (var index = 0; index < Burst; index += 1)
            limiter.ReportHttpOutcome(identity, (RequestResult.TransientFailure, false), false, null);
        Check.Require(waits.Count == Burst
            && waits.All(wait => wait == new WaitEvent(TextKey.WaitTooManyRequests, TimeSpan.Zero)),
            "A no-wait burst reports one zero request wait per reply.");
        Check.Require(limiter.Level == OneSlot,
            "Without a live pause each no-wait reply is its own busy period, down to the positive minimum.");
    }

    private static async Task SemanticThrottleDuringExistingProviderFloorAsync()
    {
        var time = new ManualTime();
        var waits = new List<WaitEvent>();
        var limiter = Create(time, waits.Add);
        var finish = Identity(SemanticAccount, DropboxProtocol.UploadSessionFinishBatch);
        var longpoll = Identity(SemanticAccount, DropboxProtocol.ListFolderLongpoll);

        // A high commit stage: each free semantic throttle selects and arms one longer fallback.
        for (var stage = 0; stage < PriorCommitStage; stage += 1)
        {
            limiter.ReportBatchOutcome(finish, true, null);
            time.Advance(waits[^1].Wait);
        }
        Check.Require(waits[^1].Wait == FailureRetryOwner.AdoptedWait(PriorCommitStage - OneSlot, null, Backoff, () => 0),
            "Each free semantic throttle selects the next commit stage.");
        var expected = FailureRetryOwner.AdoptedWait(PriorCommitStage, null, Backoff, () => 0);

        var floor = TimeSpan.FromSeconds(LongWait);
        var remaining = TimeSpan.FromSeconds(ShortWait);
        FailureRetryOwner.InstallProviderConstraint((SemanticAccount, null, null),
            time.GetTimestamp() - (floor - remaining).Ticks, floor, time);
        Check.Require(FailureRetryOwner.RemainingProviderConstraint(finish, time) == remaining,
            "The account floor is nearly expired.");

        limiter.ReportBatchOutcome(finish, true, null);
        Check.Require(waits[^1] == new WaitEvent(TextKey.WaitTooManyWrites, TimeSpan.Zero),
            "A semantic throttle with no own wait selects nothing while the provider floor is live.");
        limiter.ReportBatchOutcome(finish, true, TimeSpan.Zero);
        Check.Require(waits[^1] == new WaitEvent(TextKey.WaitTooManyWrites, TimeSpan.Zero),
            "A supplied zero is precedence, never a fallback draw.");
        Check.Require(FailureRetryOwner.RemainingProviderConstraint(finish, time) == remaining,
            "A supplied zero never clears a longer account floor.");
        var positive = TimeSpan.FromSeconds(Initial);
        limiter.ReportBatchOutcome(finish, true, positive);
        Check.Require(waits[^1] == new WaitEvent(TextKey.WaitTooManyWrites, positive),
            "A positive own observation is reported unchanged.");
        Check.Require(FailureRetryOwner.RemainingProviderConstraint(finish, time) == remaining,
            "A positive own observation never rebases the account floor.");

        time.Advance(remaining);
        limiter.ReportBatchOutcome(finish, true, null);
        Check.Require(waits[^1] == new WaitEvent(TextKey.WaitTooManyWrites, expected),
            "After the floor expires the next throttle selects exactly the high commit stage.");

        // An unrelated longpoll floor is not a busy period for a finish_batch identity.
        var controlWaits = new List<WaitEvent>();
        var control = Create(time, controlWaits.Add);
        var duration = TimeSpan.FromSeconds(ShortWait);
        FailureRetryOwner.InstallProviderConstraint((SemanticAccount, DropboxProtocol.ListFolderLongpoll, Source),
            time.GetTimestamp(), duration, time);
        using var cancellation = new CancellationTokenSource();
        var blocked = Dispatch(control, longpoll, ct: cancellation.Token);
        Check.Require(!blocked.IsCompleted, "The longpoll floor gates its own route and source.");
        var open = Dispatch(control, finish);
        Check.Require(open.IsCompletedSuccessfully, "An unrelated longpoll floor does not gate finish_batch.");
        using var opened = await open;
        control.ReportBatchOutcome(finish, true, null);
        Check.Require(controlWaits.Count == OneSlot
            && controlWaits[0].Wait == TimeSpan.FromSeconds(BackoffInitialSeconds),
            "An unrelated floor leaves semantic feedback a free period, so it selects the first fallback.");
        cancellation.Cancel();
        await CancelledAsync(blocked);
    }

    private static void BatchFeedback()
    {
        var time = new ManualTime();
        var waits = new List<WaitEvent>();
        AdaptiveLimiter? owner = null;
        var limiter = Create(time, wait =>
        {
            waits.Add(wait);
            Check.Require(!Monitor.IsEntered(Gate(owner!)), "The waiter notification runs outside the limiter gate.");
        });
        owner = limiter;
        var identity = Identity(BatchAccount, DropboxProtocol.UploadSessionFinishBatch);
        var duration = TimeSpan.FromSeconds(LongWait);
        limiter.ReportBatchOutcome(identity, true, duration);
        Check.Require(waits.Count == OneSlot && waits[0] == new WaitEvent(TextKey.WaitTooManyWrites, duration),
            "Batch write contention reports the supplied account pause as the write-throttle wait.");
        Check.Require(limiter.Level == Initial, "Batch feedback never changes a numeric level.");
        var count = waits.Count;
        limiter.ReportBatchOutcome(identity, false, null);
        Check.Require(waits.Count == count, "A valid non-throttled batch reports no wait.");
        limiter.ReportBatchOutcome(identity, true, null);
        Check.Require(waits.Count == count + OneSlot && waits[^1].Reason == TextKey.WaitTooManyWrites
            && waits[^1].Wait > TimeSpan.Zero, "A free semantic period selects one account pause.");
        Check.Require(limiter.Level == Initial, "Semantic feedback never halves a level.");
    }

    private static async Task NetworkAsync()
    {
        var identity = Identity(Account, DropboxProtocol.UploadSessionFinishBatch);
        foreach (var (read, available) in new[] { (true, false), (false, false), (true, true) })
        {
            var network = new FakeNetworkSignal();
            network.SetAvailable(available);
            var waits = new List<WaitEvent>();
            var limiter = Create(new ManualTime(), waits.Add, network);
            var failure = new HttpRequestException();
            var calls = 0;
            var pending = Send(limiter, identity, (_, _, _) =>
            {
                calls += 1;
                return Task.FromException<HttpResponseMessage>(failure);
            }, read: read);
            try
            {
                using var result = await pending;
                Check.Require(false, nameof(NetworkAsync));
            }
            catch (HttpRequestException error)
            {
                Check.Require(ReferenceEquals(error, failure) && calls == 1 && limiter.Level == Initial,
                    "A network failure is handed to the attempt owner without a resend.");
                Check.Require(read && !available
                    ? waits.Single() == new WaitEvent(TextKey.NetworkDown, TimeSpan.Zero)
                    : waits.Count == 0, "Only a read with no network reports NetworkDown.");
            }
        }

        var cancelledLimiter = Create(new ManualTime());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledCalls = 0;
        await CancelledAsync(Send(cancelledLimiter, identity, (_, _, _) =>
        {
            cancelledCalls += 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }, cancellation.Token));
        Check.Require(cancelledCalls == 0, "Cancellation before dispatch sends nothing.");
    }

    private static async Task RequireUploadSlotsAsync(AdaptiveLimiter limiter, int count,
        (string AccountId, string RouteName, string SourceIdentity) identity)
    {
        var permits = new List<IAsyncDisposable>();
        for (var index = 0; index < count; index += 1)
        {
            var permit = limiter.AdmitUploadAsync(identity, CancellationToken.None);
            Check.Require(permit.IsCompletedSuccessfully, $"The upload pool admits {count}.");
            permits.Add(await permit);
        }
        using var cancellation = new CancellationTokenSource();
        var extra = limiter.AdmitUploadAsync(identity, cancellation.Token);
        Check.Require(!extra.IsCompleted, $"The upload pool admits exactly {count}.");
        cancellation.Cancel();
        await CancelledAsync(extra.WaitAsync(TimeSpan.FromSeconds(LongWait)));
        foreach (var permit in permits) await permit.DisposeAsync();
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }

    private static async Task CancelledAsync(Task task)
    {
        try
        {
            await task;
            Check.Require(false, nameof(CancelledAsync));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Fake time whose monotonic timestamps advance with <see cref="Advance"/>, so elapsed provider floors
    /// and account pauses expire without sleeping.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();

        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();

        public override long GetTimestamp() => GetUtcNow().UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _clock.CreateTimer(callback, state, dueTime, period);

        public void Advance(TimeSpan by) => _clock.Advance(by);
    }
}
