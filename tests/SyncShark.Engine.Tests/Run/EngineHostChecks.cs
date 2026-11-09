using System.Reflection;
using System.Text;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>The host lifetime checks: preference failure isolation, truthful cancellable status, stop
/// ordering, the page-wait notice and a legacy-paused import that must not dispatch before enable.</summary>
public static class EngineHostChecks
{
    // 1 s base, adopted backoff owner values; the page-wait cases supply a zero provider wait so no clock advances.
    private static readonly EngineSettings Settings = new("{name} ({side} {time}){ext}", "yyyyMMddHHmmss",
        "Local", "Dropbox", ".case", RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "", 262144, ScanTestSettings.BatchRows)
    {
        JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes,
        LogReadBufferBytes = ScanTestSettings.LogReadBufferBytes,
        Backoff = new FailureRetryOwner.BackoffSettings(1, 1.6, 0.2, 120),
        // The shipped defaults for the paging and folder-poll values these checks exercise.
        StoragePageSize = 100, BatchPollIntervalSeconds = 0.1, FolderDeleteMinimumPollSeconds = 1
    };

    private static readonly HttpClient Http = new();

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "syncshark-engine-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var productRoot = OverrideProductRoot(root);
            RunAsync(root).GetAwaiter().GetResult();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task RunAsync(string root)
    {
        Check.Require(typeof(EngineHost).GetNestedType("GenerationFactory", BindingFlags.Public | BindingFlags.NonPublic) is null &&
            typeof(EngineHost).GetField("_composeGeneration", BindingFlags.Instance | BindingFlags.NonPublic) is null,
            "generation composition cannot be replaced by a caller");
        Check.Require(typeof(Massdex.SyncShark.Engine.Run.AutoSync).GetMethod("OpportunityAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                is { IsPrivate: true } &&
            typeof(PairSession).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .All(constructor => constructor.IsPrivate),
            "AutoSync opportunities and session construction remain private to their owners");
        await QuietWorkingStatusHasNoEmptyNameAsync(root).ConfigureAwait(false);
        await HostStartupReadsSavedPreferencesBeforeReadyAsync(root).ConfigureAwait(false);
        await HostStartupWithoutPreferencesReturnsDefaultsWithoutSeedingAsync(root).ConfigureAwait(false);
        await HostPreferenceFailureKeepsReadyAndPreservesFailureAsync(root).ConfigureAwait(false);
        await HostPreferenceFailureKeepsAdmittedWorkAndStopAsync(root).ConfigureAwait(false);
        await PreferenceOperationsDoNoPairWorkAsync(root).ConfigureAwait(false);
        HostCompositionFailurePreventsReady(root);
        await DispatchRegistrationIsVisiblyRunningAndCancellableAsync(root).ConfigureAwait(false);
        await BackgroundFailureLeavesSupervisionAsync(root).ConfigureAwait(false);
        await AcceptedStopSignalsSupervisionBeforeDrainAsync(root).ConfigureAwait(false);
        await RejectedStopDoesNotSignalSupervisionAsync(root).ConfigureAwait(false);
        await PageWaitFirstFailureRaisesOneNoticeWithoutPairAccountingAsync(root).ConfigureAwait(false);
        await PageWaitRepeatedFailuresKeepOneNoticeAsync(root).ConfigureAwait(false);
        await PageWaitSuccessClosesOnlyPageWaitNoticeAsync(root).ConfigureAwait(false);
        await PageWaitCancelClosesContributionAsync(root).ConfigureAwait(false);
        await PageWaitStaleIdentityRefusesAsync(root).ConfigureAwait(false);
        await PageWaitPreservesIndependentFailedRepairDebtAsync(root).ConfigureAwait(false);
        await PageWaitStoreFailureSurfacesErrorAsync(root).ConfigureAwait(false);
        await FailedRepairRecordsOnePrerequisiteFailureAsync(root).ConfigureAwait(false);
        await SelectedPrerequisiteScheduleIsRevalidatedAsync(root);
        await PrerequisiteAccountingFailureReleasesAsync(root);
        await RefusedPrerequisiteOpportunitiesAsync(root, scheduled: false);
        await RefusedPrerequisiteOpportunitiesAsync(root, scheduled: true);
        await RefusedUnchangedScheduleAsync(root, remove: false);
        await RefusedUnchangedScheduleAsync(root, remove: true);
        await WaitingEligibilityAsync(root);
        WholeDropboxPairIsAffected();
        await ItemAttemptAuthorityAsync(root).ConfigureAwait(false);
        await ReservationWorkAsync(root).ConfigureAwait(false);
        await SelectedOpportunityAuthorityAsync(root).ConfigureAwait(false);
        await RequestAttemptIsNotRetriedAsync(root).ConfigureAwait(false);
        await TransferRetryRequiresProofAsync(root).ConfigureAwait(false);
        await PauseAdmissionRoutingAsync(root).ConfigureAwait(false);
        await PausedPrerequisiteLogsAnAdmissionErrorAsync(root).ConfigureAwait(false);
        StoppedPrerequisiteLogKeepsDistinctFailures(root);
        await ExternalClientOwnershipAsync(root).ConfigureAwait(false);
        await ExternalClientFailureAsync(root).ConfigureAwait(false);
        await StartupReceiptsAsync(root, fail: false, jobs: false, opportunity: false).ConfigureAwait(false);
        await StartupReceiptsAsync(root, fail: true, jobs: false, opportunity: false).ConfigureAwait(false);
        await StartupReceiptsAsync(root, fail: false, jobs: true, opportunity: false).ConfigureAwait(false);
        await StartupReceiptsAsync(root, fail: false, jobs: true, opportunity: false, shared: true).ConfigureAwait(false);
        await StartupReceiptsAsync(root, fail: false, jobs: true, opportunity: true).ConfigureAwait(false);
        await StartupReceiptsAsync(root, fail: false, jobs: true, opportunity: false, paused: true).ConfigureAwait(false);
        await StartupReceiptsAsync(root, fail: false, jobs: true, opportunity: true, startup: true).ConfigureAwait(false);
        await InProgressFolderJobRepollsToTerminalUnknownAsync(root);
        await InProgressFolderJobSettlesWithoutWakeAsync(root);
        await RepairHeldSurvivorPollAsync(root);
        await FolderPollLifecycleAsync(root, "pause");
        await FolderPollLifecycleAsync(root, "disable");
        await FolderPollLifecycleAsync(root, "restart");
        await FolderPollLifecycleAsync(root, "stop");
        try
        {
            await StartupReceiptsAsync(root, fail: true, jobs: false, opportunity: true).ConfigureAwait(false);
            throw new Exception("background storage failure was lost at shutdown");
        }
        catch (AggregateException error) when (error.InnerExceptions.Count == 1 &&
            error.InnerException is InvalidDataException) { }
    }

    // RC05-T2/T8: the host loads the authority's saved preferences before it reports ready.
    private static async Task HostStartupReadsSavedPreferencesBeforeReadyAsync(string root)
    {
        using var instance = Lock(root, "prefs-saved");
        await new UiPreferencesStore(instance.StateAuthority)
            .SaveAsync(new UiPreferences(UiPreferencesStore.DarkTheme)).ConfigureAwait(false);
        await using var host = Compose(instance.StateAuthority, new InMemoryStateStore());
        await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
        Check.Require(host.Status().Status == AutoStatus.Idle,
            "a started host with no pairs is ready and idle");
        var read = await host.ReadPreferencesAsync(CancellationToken.None).ConfigureAwait(false);
        Check.Require(read.Theme == UiPreferencesStore.DarkTheme,
            "the host reads the saved preferences through its injected authority before it is ready");
    }

    // RC05-T2: a genuinely absent preference state reads as the normalized default without creating a file.
    private static async Task HostStartupWithoutPreferencesReturnsDefaultsWithoutSeedingAsync(string root)
    {
        using var instance = Lock(root, "prefs-absent");
        var state = instance.StateAuthority.StateRoot;
        await using var host = Compose(instance.StateAuthority, new InMemoryStateStore());
        await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var read = await host.ReadPreferencesAsync(CancellationToken.None).ConfigureAwait(false);
        Check.Require(read.Theme == UiPreferencesStore.SystemTheme,
            "an absent preference state reads as the normalized default in memory");
        Check.Require(!File.Exists(Path.Combine(state, "ui-preferences.jsonl")) &&
            !File.Exists(Path.Combine(state, "ui-preferences-a.json")) &&
            !File.Exists(Path.Combine(state, "ui-preferences-b.json")),
            "a read-only preference load does not seed or migrate any state file");
    }

    // RC05-V5-P: a corrupt preference successor never fails Engine readiness and is never reset to defaults.
    private static async Task HostPreferenceFailureKeepsReadyAndPreservesFailureAsync(string root)
    {
        using var instance = Lock(root, "prefs-corrupt");
        var state = instance.StateAuthority.StateRoot;
        var corrupt = Path.Combine(state, "ui-preferences-a.json");
        var corruptBytes = Encoding.UTF8.GetBytes("not a saved preference slot");
        File.WriteAllBytes(corrupt, corruptBytes);
        await using var host = Compose(instance.StateAuthority, new InMemoryStateStore());
        await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
        Check.Require(host.Status().Status == AutoStatus.Idle,
            "a corrupt saved preference never fails Engine readiness");

        var readRefused = false;
        try { await host.ReadPreferencesAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (InvalidDataException) { readRefused = true; }
        Check.Require(readRefused, "a corrupt preference successor is refused on read");

        var updateRefused = false;
        try
        {
            await host.UpdatePreferencesAsync(
                new UiPreferencesUpdate { Theme = UiPreferencesStore.LightTheme }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException) { updateRefused = true; }
        Check.Require(updateRefused, "a corrupt preference successor is refused on update");

        Check.Require(File.ReadAllBytes(corrupt).SequenceEqual(corruptBytes),
            "the corrupt successor is preserved, never reset to defaults");
        Check.Require(!File.Exists(Path.Combine(state, "ui-preferences-b.json")),
            "no replacement default slot was written for the corrupt successor");
    }

    // RC05-V5-P: preference failure leaves independent admitted work and explicit stop valid.
    private static async Task HostPreferenceFailureKeepsAdmittedWorkAndStopAsync(string root)
    {
        using var instance = Lock(root, "prefs-admitted");
        File.WriteAllBytes(Path.Combine(instance.StateAuthority.StateRoot, "ui-preferences-a.json"),
            Encoding.UTF8.GetBytes("not a saved preference slot"));
        var store = new InMemoryStateStore();
        await using var host = Compose(instance.StateAuthority, store);
        await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
        const string PairId = "independent-admitted";
        store.SavePair(Pair(PairId));
        var worked = await host.AcquireAsync(PairId, (registration, pair, generation, token) => Task.FromResult(true),
            CancellationToken.None).ConfigureAwait(false);
        Check.Require(worked, "independent admitted work stays valid while preferences are unavailable");
        await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        Check.Require(host.Status().Status == AutoStatus.Off,
            "an explicit host stop still completes while preferences are unavailable");
    }

    // RC05-T8: preference read and update perform no pair dispatch, drain, wake or listing work.
    private static async Task PreferenceOperationsDoNoPairWorkAsync(string root)
    {
        using var instance = Lock(root, "prefs-cost");
        const string PairId = "prefs-cost-pair";
        var store = new InMemoryStateStore();
        await using var host = Compose(instance.StateAuthority, store);
        await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
        store.SavePair(Pair(PairId));
        var generation = store.ReadPairExecution(PairId)?.Generation;
        var local = store.ReadSource(PairId, Side.Local);
        var remote = store.ReadSource(PairId, Side.Dropbox);
        await host.ReadPreferencesAsync(CancellationToken.None).ConfigureAwait(false);
        var updated = await host.UpdatePreferencesAsync(
            new UiPreferencesUpdate { Theme = UiPreferencesStore.LightTheme }, CancellationToken.None)
            .ConfigureAwait(false);
        Check.Require(updated.Theme == UiPreferencesStore.LightTheme, "the update returns the committed preference");
        Check.Require(host.Status().Status == AutoStatus.Idle,
            "preference read and update never dispatch, drain or wake a pair");
        Check.Require(store.ReadPairExecution(PairId)?.Generation == generation,
            "preference operations leave the pair generation unchanged");
        var afterLocal = store.ReadSource(PairId, Side.Local);
        var afterRemote = store.ReadSource(PairId, Side.Dropbox);
        Check.Require(afterLocal?.Epoch == local?.Epoch && afterLocal?.Revision == local?.Revision &&
            afterRemote?.Epoch == remote?.Epoch && afterRemote?.Revision == remote?.Revision,
            "preference operations leave both source fences unchanged");
        Check.Require(store.ReadPrerequisiteRetry(PairId) is null && store.Issues(PairId).Count == 0,
            "preference operations record no retry, issue or repair work");
        var reread = await host.ReadPreferencesAsync(CancellationToken.None).ConfigureAwait(false);
        Check.Require(reread.Theme == UiPreferencesStore.LightTheme,
            "the committed preference is read back through the same store");
    }

    // RC05-V5-P: a non-preference Engine database failure still prevents host composition.
    private static void HostCompositionFailurePreventsReady(string root)
    {
        using var instance = Lock(root, "compose-failure");
        var refused = false;
        try
        {
            _ = EngineHost.Compose(instance.StateAuthority.ProductRoot, _ => { }, instance.StateAuthority,
                Settings, Http, TimeProvider.System, _ => null, _ => throw new InvalidOperationException(),
                EngineHost.Mode.Persistent);
        }
        catch (InvalidOperationException) { refused = true; }
        Check.Require(refused,
            "an Engine database failure prevents host composition instead of a default store");
    }

    // V13-A24: an admitted Dispatch registration is visibly running; a disappearing client cancels only
    // its own operation and the host stays usable.
    private static async Task DispatchRegistrationIsVisiblyRunningAndCancellableAsync(string root)
    {
        using var instance = Lock(root, "status");
        const string PairId = "status-pair";
        var store = new InMemoryStateStore();
        store.SavePair(Pair(PairId));
        await using var host = Compose(instance.StateAuthority, store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var running = host.DispatchAsync(PairId, async (registration, pair, generation, token) =>
            {
                entered.TrySetResult();
                using var registrationToken = token.Register(() => release.TrySetCanceled(token));
                await release.Task.ConfigureAwait(false);
                return pair;
            }, cancel.Token);
        await entered.Task.ConfigureAwait(false);
        Check.Require(host.Status().Status == AutoStatus.Running,
            "an admitted Dispatch registration is visibly running");
        cancel.Cancel();
        try { await running.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        Check.Require(host.Status().Status is null,
            "a cancelled client operation leaves no Running status behind");
        var again = await host.AcquireAsync(PairId, (registration, pair, generation, token) => Task.FromResult(true),
            CancellationToken.None).ConfigureAwait(false);
        Check.Require(again, "the host remains usable after a client operation is cancelled");
    }

    private static async Task BackgroundFailureLeavesSupervisionAsync(string root)
    {
        using var instance = Lock(root, "background-failure");
        var host = Compose(instance.StateAuthority, new InMemoryStateStore());
        var signalled = false;
        host.BindStopSupervision(() => signalled = true);
        using var acceptStop = CancellationTokenSource.CreateLinkedTokenSource(host.Stopping);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = acceptStop.Token.Register(() => stopped.TrySetResult());
        await using var status = host.SubscribeStatusAsync(CancellationToken.None).GetAsyncEnumerator();
        Check.Require(await status.MoveNextAsync(), "A status subscriber receives the initial host status.");
        var failure = new IOException("background-fixture");
        host.BackgroundFailed(failure);
        Check.Require(await status.MoveNextAsync() && status.Current.Status == AutoStatus.Held,
            "A background failure publishes Held before ending status subscriptions.");
        Check.Require(!await status.MoveNextAsync(),
            "A failed host ends its status stream before disposal so open clients cannot prevent exit.");
        await using var lateStatus = host.SubscribeStatusAsync(CancellationToken.None).GetAsyncEnumerator();
        Check.Require(!await lateStatus.MoveNextAsync(),
            "A subscriber arriving after background failure ends immediately without waiting for disposal.");
        await stopped.Task;
        Check.Require(acceptStop.IsCancellationRequested && !signalled,
            "A background failure ends host acceptance without stopping its supervisor.");
        AggregateException? reported = null;
        try { await host.DisposeAsync(); }
        catch (AggregateException error) { reported = error; }
        Check.Require(reported?.InnerExceptions.Contains(failure) == true && !signalled,
            "Host disposal reports the original failure for a failed exit and preserves supervision.");
    }

    // RC05-V5-F122: an accepted explicit stop signals supervision before it drains admitted work.
    private static async Task AcceptedStopSignalsSupervisionBeforeDrainAsync(string root)
    {
        using var instance = Lock(root, "stop-accepted");
        const string PairId = "stop-pair";
        var store = new InMemoryStateStore();
        store.SavePair(Pair(PairId));
        var host = Compose(instance.StateAuthority, store);
        var order = new List<string>();
        host.BindStopSupervision(() => order.Add("supervision"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = host.DispatchAsync(PairId, async (registration, pair, generation, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
                finally { order.Add("drained"); }
                return pair;
            }, CancellationToken.None);
        await entered.Task.ConfigureAwait(false);
        await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        try { await running.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        Check.Require(order.SequenceEqual(["supervision", "drained"]),
            "an accepted stop signals supervision before it drains admitted work (observed: " +
            string.Join(",", order) + ")");
        Check.Require(host.Status().Status == AutoStatus.Off, "the stopped host reports Off");
        await host.DisposeAsync().ConfigureAwait(false);
        Check.Require(host.Status().Status == AutoStatus.Off, "disposal is idempotent after an accepted stop");
    }

    // RC05-V5-F122: a rejected/uncertain stop never signals supervision.
    private static async Task RejectedStopDoesNotSignalSupervisionAsync(string root)
    {
        using var instance = Lock(root, "stop-rejected");
        var store = new InMemoryStateStore();
        var host = Compose(instance.StateAuthority, store);
        var signalled = false;
        host.BindStopSupervision(() => signalled = true);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var refused = false;
        try { await host.StopAsync(cancelled.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { refused = true; }
        Check.Require(refused, "a rejected stop surfaces its cancellation");
        Check.Require(!signalled, "a rejected stop never signals supervision");
        await host.DisposeAsync().ConfigureAwait(false);
    }

    // V16-Q5: the first transient prerequisite-page failure raises one notice without pair accounting.
    private static async Task PageWaitFirstFailureRaisesOneNoticeWithoutPairAccountingAsync(string root)
    {
        using var instance = Lock(root, "page-first");
        const string PairId = "page-first";
        var store = new InMemoryStateStore();
        store.SavePair(Pair(PairId));
        await using var host = Compose(instance.StateAuthority, store);
        var notices = await WithPrerequisiteListingAsync(host, PairId, async (listing, ct) =>
        {
            var attempt = listing.BeginAttempt();
            Check.Require(attempt.TryComplete(RequestResult.TransientFailure, null, TimeSpan.Zero, false),
                "the page attempt publishes its transient disposition");
            Check.Require(await listing.CompleteAttemptAsync(attempt, ct).ConfigureAwait(false),
                "a transient page failure asks the same page to retry");
            return RetryNotices(store, PairId);
        }).ConfigureAwait(false);
        Check.Require(notices == 1, "the first transient page failure raises exactly one RequiredRepairRetry notice");
        Check.Require(store.ReadPrerequisiteRetry(PairId) is null,
            "a page retry records no pair Attempts or retry deadline");
    }

    // V16-Q5: repeated transient page failures keep exactly one notice.
    private static async Task PageWaitRepeatedFailuresKeepOneNoticeAsync(string root)
    {
        using var instance = Lock(root, "page-repeat");
        const string PairId = "page-repeat";
        var store = new InMemoryStateStore();
        store.SavePair(Pair(PairId));
        await using var host = Compose(instance.StateAuthority, store);
        await WithPrerequisiteListingAsync(host, PairId, async (listing, ct) =>
        {
            for (var attemptIndex = 0; attemptIndex < 3; attemptIndex++)
            {
                var attempt = listing.BeginAttempt();
                Check.Require(attempt.TryComplete(RequestResult.TransientFailure, null, TimeSpan.Zero, false),
                    "each page attempt publishes its transient disposition");
                Check.Require(await listing.CompleteAttemptAsync(attempt, ct).ConfigureAwait(false),
                    "each transient failure retries the same page");
                Check.Require(RetryNotices(store, PairId) == 1,
                    "repeated transient page failures keep exactly one notice");
            }
            return true;
        }).ConfigureAwait(false);
        Check.Require(store.ReadPrerequisiteRetry(PairId) is null,
            "repeated page retries never advance pair Attempts or a deadline");
    }

    // V16-Q5: a completed same-page response closes only the page-wait notice.
    private static async Task PageWaitSuccessClosesOnlyPageWaitNoticeAsync(string root)
    {
        using var instance = Lock(root, "page-success");
        const string PairId = "page-success";
        var store = new InMemoryStateStore();
        store.SavePair(Pair(PairId));
        await using var host = Compose(instance.StateAuthority, store);
        var observed = await WithPrerequisiteListingAsync(host, PairId, async (listing, ct) =>
        {
            var transient = listing.BeginAttempt();
            transient.TryComplete(RequestResult.TransientFailure, null, TimeSpan.Zero, false);
            Check.Require(await listing.CompleteAttemptAsync(transient, ct).ConfigureAwait(false),
                "the transient failure asks for the same-page retry");
            var raised = RetryNotices(store, PairId);
            var success = listing.BeginAttempt();
            Check.Require(success.TryComplete(RequestResult.Success, null, null, false),
                "the same page completes successfully");
            var retried = await listing.CompleteAttemptAsync(success, ct).ConfigureAwait(false);
            return (Raised: raised, Retried: retried, Closed: RetryNotices(store, PairId));
        }).ConfigureAwait(false);
        Check.Require(observed.Raised == 1 && !observed.Retried && observed.Closed == 0,
            "a completed same-page response closes only the page-wait notice and asks for no retry");
    }

    // V16-Q5: cancelling the page wait closes only its own contribution.
    private static async Task PageWaitCancelClosesContributionAsync(string root)
    {
        using var instance = Lock(root, "page-cancel");
        const string PairId = "page-cancel";
        var store = new InMemoryStateStore();
        store.SavePair(Pair(PairId));
        await using var host = Compose(instance.StateAuthority, store);
        var remaining = await WithPrerequisiteListingAsync(host, PairId, async (listing, ct) =>
        {
            var attempt = listing.BeginAttempt();
            attempt.TryComplete(RequestResult.TransientFailure, null, TimeSpan.Zero, false);
            await listing.CompleteAttemptAsync(attempt, ct).ConfigureAwait(false);
            Check.Require(RetryNotices(store, PairId) == 1, "the retry wait is visible before cancellation");
            await listing.EndAsync().ConfigureAwait(false);
            return RetryNotices(store, PairId);
        }).ConfigureAwait(false);
        Check.Require(remaining == 0, "cancelling the operation closes only its own page-wait notice");
    }

    // V16-Q5: an attempt from another operation and an ended listing are refused under the identity fence.
    private static async Task PageWaitStaleIdentityRefusesAsync(string root)
    {
        using var instance = Lock(root, "page-stale");
        const string PairId = "page-stale";
        var store = new InMemoryStateStore();
        store.SavePair(Pair(PairId));
        await using var host = Compose(instance.StateAuthority, store);
        await WithPrerequisiteListingAsync(host, PairId, async (listing, ct) =>
        {
            await listing.EndAsync().ConfigureAwait(false);
            var endedRefused = false;
            try { listing.BeginAttempt(); }
            catch (ObjectDisposedException) { endedRefused = true; }
            Check.Require(endedRefused, "an ended listing refuses a new attempt");
            var staleRefused = false;
            try
            {
                await listing.CompleteAttemptAsync(
                    new RequestAttempt(new object(), AttemptOwner.Prerequisite), ct).ConfigureAwait(false);
            }
            catch (LiveAdmissionStaleException) { staleRefused = true; }
            Check.Require(staleRefused, "an attempt from a different operation is refused as stale");
            return true;
        }).ConfigureAwait(false);
    }

    // V16-Q5: independently protected failed-Repair debt and its notice survive a same-page success.
    private static async Task PageWaitPreservesIndependentFailedRepairDebtAsync(string root)
    {
        using var instance = Lock(root, "page-debt");
        const string PairId = "page-debt";
        var store = new InMemoryStateStore();
        store.SavePair(Pair(PairId));
        var local = store.ReadSource(PairId, Side.Local) ?? throw new InvalidOperationException();
        var remote = store.ReadSource(PairId, Side.Dropbox) ?? throw new InvalidOperationException();
        store.RecordPrerequisiteFailure(PairId, 0, local.Epoch, remote.Epoch, local.Revision, remote.Revision,
            null, new FailureRetryOwner.State(1, DateTimeOffset.UtcNow.AddMinutes(1)), RetryNotice());
        Check.Require(RetryNotices(store, PairId) == 1, "the independently protected failed-Repair notice is recorded");
        await using var host = Compose(instance.StateAuthority, store);
        var survived = await WithPrerequisiteListingAsync(host, PairId, async (listing, ct) =>
        {
            var transient = listing.BeginAttempt();
            transient.TryComplete(RequestResult.TransientFailure, null, TimeSpan.Zero, false);
            await listing.CompleteAttemptAsync(transient, ct).ConfigureAwait(false);
            var success = listing.BeginAttempt();
            success.TryComplete(RequestResult.Success, null, null, false);
            await listing.CompleteAttemptAsync(success, ct).ConfigureAwait(false);
            return (Notices: RetryNotices(store, PairId), Retry: store.ReadPrerequisiteRetry(PairId));
        }).ConfigureAwait(false);
        Check.Require(survived.Notices == 1 && survived.Retry is { Attempts: 1 },
            "the independently protected failed-Repair debt and notice survive a same-page success");
    }

    // V16-Q5: a Store failure while writing the notice surfaces an error instead of a healthy retry.
    private static async Task PageWaitStoreFailureSurfacesErrorAsync(string root)
    {
        using var instance = Lock(root, "page-store-failure");
        const string PairId = "page-store-failure";
        var store = new InMemoryStateStore();
        store.SavePair(Pair(PairId));
        await using var host = Compose(instance.StateAuthority, store);
        var refused = false;
        try
        {
            await WithPrerequisiteListingAsync(host, PairId, async (listing, ct) =>
            {
                store.RemovePair(PairId);
                var attempt = listing.BeginAttempt();
                attempt.TryComplete(RequestResult.TransientFailure, null, TimeSpan.Zero, false);
                await listing.CompleteAttemptAsync(attempt, ct).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        catch (LiveAdmissionStaleException) { refused = true; }
        Check.Require(refused,
            "a Store failure while writing the page-wait notice surfaces an error instead of a healthy retry");
        Check.Require(RetryNotices(store, PairId) == 0, "the failed notice write raised nothing");
    }

    public static void ReviewedPrerequisiteRetry() => ReviewedRetryCase(prerequisite: true, hasRetry: true);
    public static void ReviewedObligationRetry() => ReviewedRetryCase(prerequisite: true, hasRetry: false);
    public static void ReviewedRecoveryRetry() => ReviewedRetryCase(prerequisite: false, hasRetry: false);
    public static void ReviewedSourceWaitRetry() => ReviewedRetryCase(prerequisite: true, hasRetry: true, secondWait: true);
    public static void ReviewedDispatchWaitRetry() => ReviewedRetryCase(prerequisite: true, hasRetry: false, secondWait: true);

    public static void ReviewedRepairDispatchRetry() => ReviewedRetryCase(prerequisite: true, hasRetry: false, secondWait: true, clearDebt: true);

    private static void ReviewedRetryCase(bool prerequisite, bool hasRetry, bool secondWait = false, bool clearDebt = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "reviewed-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var productRoot = OverrideProductRoot(root);
            ReviewedRetryDuringRecompositionAsync(root, prerequisite, hasRetry, secondWait, clearDebt).GetAwaiter().GetResult();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task ReviewedRetryDuringRecompositionAsync(string root, bool prerequisite, bool hasRetry,
        bool secondWait, bool clearDebt)
    {
        var id = $"reviewed-retry-{prerequisite}-{hasRetry}-{secondWait}-{clearDebt}";
        using var instance = Lock(root, id);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "synthetic-account", "root", "home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        var store = new InMemoryStateStore();
        var pair = SourceFixture.Bound(Pair(id) with { ConnectionId = binding.ConnectionId });
        if (secondWait)
        {
            var localRoot = Directory.CreateDirectory(Path.Combine(root, "local")).FullName;
            pair = pair with { LocalRoot = localRoot, Roots = pair.Roots! with
            {
                LocalRoot = localRoot, Local = WindowsDirectoryListing.ReadIdentity(localRoot)
            } };
        }
        store.SavePair(pair);
        var generation = store.ReadPairExecution(id)!.Value.Generation;
        if (secondWait)
        {
            foreach (var side in new[] { Side.Local, Side.Dropbox })
            {
                var source = store.ReadSource(id, side)!.Value;
                var remoteRoot = pair.Roots!.Dropbox;
                var provenance = side == Side.Local ? source.Provenance : new SourceProvenance(
                    remoteRoot.AccountId, remoteRoot.RootNamespaceId, remoteRoot.FolderId, pair.DropboxRoot,
                    true, true, true, true, generation, source.Epoch);
                store.AdvanceSource(id, side, generation, source.Epoch, source.Revision,
                    source.Provenance, source.Cursor, source.Epoch, source.Revision + 1,
                    provenance, side == Side.Local ? source.Cursor : "cursor", hasRetry ? null : source.Obligation);
            }
            var checkpoint = NtfsUsnJournal.ReadCheckpoint(pair.LocalRoot);
            var sourceLocal = store.ReadSource(id, Side.Local)!.Value;
            Check.Require(store.AcquireJournalPage(pair.Roots!.LocalVolumeKey(), null,
                new JournalPosition((ulong)checkpoint.JournalId, (ulong)checkpoint.NextUsn),
                [(id, generation, sourceLocal.Epoch, sourceLocal.Revision, false)], []) is not null,
                "the later-boundary fixture has an acquired native journal base");
        }
        var local = store.ReadSource(id, Side.Local)!.Value;
        var remote = store.ReadSource(id, Side.Dropbox)!.Value;
        if (hasRetry)
            store.RecordPrerequisiteFailure(id, generation, local.Epoch, remote.Epoch, local.Revision,
                remote.Revision, null, FailureRetryOwner.AdoptedFailed(0, DateTimeOffset.UnixEpoch, null,
                    Settings.Backoff, static () => 0.5), RetryNotice());
        else store.Commit(id, prerequisite ? RetryNotice() : StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailLocalInUse, "file.txt",
                new IssueSubject([new IssueItem(Side.Local, "file.txt", "old")], null))]
        });
        var reviewed = PairSafety.ForReview(store.Issues(id).Single());
        var recovery = new FakeRecovery();
        var observedStore = Local.JournalSourceChecks.HookedStore.Wrap(store);
        var storeHook = (Local.JournalSourceChecks.HookedStore)(object)observedStore;
        using var http = new HttpClient(new OwnedHandler());
        await using var host = EngineHost.Compose(instance.StateAuthority.ProductRoot, _ => { },
            instance.StateAuthority, Settings, http, TimeProvider.System, _ => null, _ => observedStore,
            EngineHost.Mode.Persistent,
            new EngineExternals(new SyntheticCredentials(), _ => new HttpClient(new OwnedHandler()), recovery));
        var configuration = (SemaphoreSlim)typeof(EngineHost)
            .GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var held = configuration;
        var sourceCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var debtCleared = false;
        if (secondWait)
        {
            if (!clearDebt) await host.RecomposeCurrentAsync(id, CancellationToken.None);
            if (hasRetry)
            {
                var volumes = (System.Collections.IDictionary)typeof(EngineHost)
                    .GetField("_volumes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                var volume = volumes[pair.Roots!.LocalVolumeKey()]!;
                held = (SemaphoreSlim)volume.GetType()
                    .GetProperty("Serial", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(volume)!;
            }
            else storeHook.After = method =>
            {
                if (clearDebt && !debtCleared && method.Name == nameof(IStateStore.ReadPairExecution))
                {
                    Check.Require(store.Obligated(id), "ordinary Repair starts with outstanding source debt");
                    debtCleared = true;
                    foreach (var side in new[] { Side.Local, Side.Dropbox })
                    {
                        var source = store.ReadSource(id, side)!.Value;
                        store.AdvanceSource(id, side, generation, source.Epoch, source.Revision,
                            source.Provenance, source.Cursor, source.Epoch, source.Revision + 1,
                            source.Provenance, source.Cursor, null);
                    }
                }
                if (method.Name != nameof(IStateStore.ReadWorkWatermark)) return;
                storeHook.After = null;
                if (clearDebt)
                    Check.Require(configuration.Wait(0) && !store.Obligated(id),
                        "recomposition clears the debt before ordinary Repair reaches dispatch");
                var gate = typeof(EngineHost).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                lock (gate)
                    ((HashSet<string>)typeof(EngineHost).GetField("_closed", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(host)!).Add(id);
                sourceCompleted.TrySetResult();
            };
        }
        if (!clearDebt) await held.WaitAsync();
        Task<ResolveSummary> resolving;
        Issue replacement;
        var retryBefore = store.ReadPrerequisiteRetry(id);
        ItemRetryCandidate? candidateBefore;
        PendingRead pendingBefore;
        try
        {
            resolving = new EngineOperations(instance.StateAuthority.ProductRoot, _ => { }, host)
                .ResolveAsync(id, reviewed.Id, ResolutionChoice.Retry, null, null, false, null,
                    CancellationToken.None, reviewed.Reviewed);
            if (secondWait && !hasRetry)
                Check.Require(await Task.WhenAny(sourceCompleted.Task, resolving) == sourceCompleted.Task,
                    "the reviewed choice finishes source acquisition before its dispatch recomposition wait");
            Check.Require(!resolving.IsCompleted, secondWait
                ? "the reviewed choice waits at the later source or dispatch boundary"
                : "the reviewed choice waits for generation recomposition");
            if (prerequisite)
                store.Commit(id, StateChange.Empty with
                {
                    Raise = [(IssueKind.Failure, TextKey.RequiredRepairRetry, string.Empty,
                        new IssueSubject([], null, new LocalHashProof(1, 0, "replacement")))]
                });
            else
            {
                var entry = new Entry("file.txt", false, 1, DateTimeOffset.UnixEpoch, "replacement", "", "hash", null);
                var pending = new PendingEffect(1, "replacement-operation", 0, PendingKind.LocalDelete,
                    PendingPhase.Armed, entry.Path, new RemovalProof(entry, "1", "hash"), null,
                    null, null, null, null, false, null);
                store.ArmPending(id, pending);
                store.RecordInUseFailure(id, generation, local.Epoch, entry.Path,
                    new IssueSubject([new IssueItem(Side.Local, entry.Path, entry.Id)], null),
                    TextKey.FailLocalInUse, null, ItemRetryDispatch.LocalRecovery, pending, null, Guid.NewGuid());
            }
            replacement = PairSafety.ForReview(store.IssueById(id, reviewed.Id)!);
            Check.Require(replacement.Id == reviewed.Id && replacement.Reviewed != reviewed.Reviewed,
                "the waiting choice now refers to a replaced notice under the same id");
            candidateBefore = store.ReadItemRetryState(id, [reviewed.Id])[reviewed.Id];
            pendingBefore = store.Pending(id);
        }
        finally { held.Release(); }
        Exception? refusal = null;
        try { await resolving; }
        catch (Exception error) { refusal = error; }
        Check.Require(refusal is EngineRequestException { Key: TextKey.ResolveStale },
            (secondWait ? hasRetry ? "a replacement during source acquisition" : "a replacement before dispatch recomposition"
                : "a replacement during recomposition") + " refuses the reviewed retry with ResolveStale, not " +
            (refusal?.GetType().Name ?? "success"));
        Check.Require(PairSafety.ForReview(store.IssueById(id, reviewed.Id)!).Reviewed == replacement.Reviewed &&
            store.ReadPrerequisiteRetry(id) == retryBefore &&
            store.ReadItemRetryState(id, [reviewed.Id])[reviewed.Id] == candidateBefore &&
            store.Pending(id) == pendingBefore && recovery.Resolved.Count == 0,
            "stale refusal preserves the replacement fingerprint, failure count, retry identity and pending effect");
    }

    // V16-Q5: an actual unsuccessful enclosing Repair keeps the ordinary once-only durable accounting.
    private static async Task FailedRepairRecordsOnePrerequisiteFailureAsync(string root)
    {
        using var instance = Lock(root, "repair-failure");
        const string PairId = "repair-failure";
        using var store = StateStore.Open(Path.Combine(instance.StateAuthority.StateRoot, "repair-failure.db"));
        store.SavePair(Pair(PairId));
        var unfinished = await SeedOperationAsync(store, store.Pair(PairId)!, "Unfinished");
        var operation = store.FolderRemoval(PairId, unfinished.Single().OperationId);
        await using var host = Compose(instance.StateAuthority, store);
        Check.Require(await FailsPrerequisiteAsync(host, PairId).ConfigureAwait(false),
            "an enabled pair with no retained account fails its enclosing prerequisite Repair");
        Check.Require(store.ReadPrerequisiteRetry(PairId) is { Attempts: 1 },
            "the actual unsuccessful Repair records one attempted failure");
        Check.Require(RetryNotices(store, PairId) == 1, "the failed Repair raises one RequiredRepairRetry notice");
        Check.Require(await FailsPrerequisiteAsync(host, PairId).ConfigureAwait(false),
            "the second enclosing Repair also fails");
        Check.Require(store.ReadPrerequisiteRetry(PairId) is { Attempts: 2 },
            "each actual unsuccessful Repair advances the shared accounting once");
        Check.Require(RetryNotices(store, PairId) == 1 &&
            store.FolderRemoval(PairId, operation.OperationId) == operation &&
            store.ReadyFolderEffects(PairId, operation.OperationId, -1, Settings.StoragePageSize).SequenceEqual(unfinished),
            "R6-6 repeated failed prerequisites retain unfinished work and exactly one explicit RequiredRepairRetry failure");
    }

    private static async Task SelectedPrerequisiteScheduleIsRevalidatedAsync(string root)
    {
        using var instance = Lock(root, "selected-schedule");
        var store = new InMemoryStateStore();
        var pair = Pair("selected-schedule");
        store.SavePair(pair);
        await using var host = Compose(instance.StateAuthority, store);
        Check.Require(await FailsPrerequisiteAsync(host, pair.PairId), "seed a genuine prerequisite failure");
        var old = store.ReadPrerequisiteRetry(pair.PairId)!.Value;
        var generation = store.ReadPairExecution(pair.PairId)!.Value.Generation;
        Check.Require(await FailsPrerequisiteAsync(host, pair.PairId), "replace the selected schedule");
        var replacement = store.ReadPrerequisiteRetry(pair.PairId);
        foreach (var (selected, reason) in new[]
        {
            ((generation, old), "a callback selected before a replacement deadline neither executes nor increments it"),
            ((generation + 1, replacement!.Value), "a selected schedule from a different generation is refused")
        })
        {
            Exception? failure = null;
            RepairSummary? result = null;
            try { result = await host.RunPrerequisiteAsync(pair.PairId, default, schedule: selected); }
            catch (Exception error) { failure = error; }
            Check.Require(failure is null && result is null && store.ReadPrerequisiteRetry(pair.PairId) == replacement, reason);
        }
    }

    private static async Task PrerequisiteAccountingFailureReleasesAsync(string root)
    {
        using var instance = Lock(root, "accounting-release");
        var store = new FailingRetryStore(new InMemoryStateStore());
        var pair = Pair("accounting-release");
        store.SavePair(pair);
        await using var host = Compose(instance.StateAuthority, store);
        var failed = false;
        try { await host.RunPrerequisiteAsync(pair.PairId, default); }
        catch (IOException error) { failed = ReferenceEquals(error, store.Failure); }
        Check.Require(failed && store.Obligated(pair.PairId) && store.ReadPrerequisiteRetry(pair.PairId) is null,
            "failed durable accounting propagates without clearing debt or inventing a deadline");
        store.Fail = false;
        Check.Require(await FailsPrerequisiteAsync(host, pair.PairId) &&
            store.ReadPrerequisiteRetry(pair.PairId) is { Attempts: 1 },
            "accounting failure releases its exact registration so the next event can be admitted");
    }

    private static async Task RefusedPrerequisiteOpportunitiesAsync(string root, bool scheduled)
    {
        using var instance = Lock(root, "owed-" + scheduled);
        var store = new InMemoryStateStore();
        var pair = Pair("owed-pair");
        store.SavePair(pair);
        var clock = new FakeTimeProvider();
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        await using var host = EngineHost.Compose(instance.StateAuthority.ProductRoot, _ => { },
            instance.StateAuthority, Settings, Http, clock, _ => null, _ => store,
            EngineHost.Mode.Direct, startupMonitor: monitor);
        Check.Require(await FailsPrerequisiteAsync(host, pair.PairId), "seed refused-opportunity retry state");
        var selected = store.ReadPrerequisiteRetry(pair.PairId)!.Value;
        var generation = store.ReadPairExecution(pair.PairId)!.Value.Generation;
        var configuration = (SemaphoreSlim)typeof(EngineHost).GetField("_configuration",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        await configuration.WaitAsync();
        var first = host.RunPrerequisiteAsync(pair.PairId, default);
        try
        {
            Check.Require(!first.IsCompleted, "the active prerequisite waits at configuration");
            await host.RunPrerequisiteAsync(pair.PairId, default,
                schedule: scheduled ? (generation, selected) : null);
            await host.RunPrerequisiteAsync(pair.PairId, default,
                schedule: scheduled ? (generation, selected) : null);
        }
        finally { configuration.Release(); }
        try { await first; }
        catch (EngineRequestException) { }
        Check.Require(store.ReadPrerequisiteRetry(pair.PairId) is { Attempts: 2 },
            "refused opportunities never count as attempted failures");
        monitor.Start();
        if (!scheduled)
            await UntilAsync(() => store.ReadPrerequisiteRetry(pair.PairId) is { Attempts: 3 });
        else
        {
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadline = monitor.ScheduleQuietStatus(TimeSpan.Zero, () => observed.TrySetResult());
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Check.Require(store.ReadPrerequisiteRetry(pair.PairId)?.Attempts == (scheduled ? 2 : 3),
            "owed genuine events coalesce once; an owed schedule never bypasses its replacement deadline");
        if (scheduled)
        {
            var replacement = store.ReadPrerequisiteRetry(pair.PairId)!.Value;
            clock.Advance(replacement.RetryAfter - clock.GetUtcNow());
            await UntilAsync(() => store.ReadPrerequisiteRetry(pair.PairId) is { Attempts: 3 });
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadline = monitor.ScheduleQuietStatus(TimeSpan.Zero, () => observed.TrySetResult());
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Check.Require(store.ReadPrerequisiteRetry(pair.PairId) is { Attempts: 3 },
                "the replacement deadline admits the owed schedule exactly once without a later event");
        }
    }

    private static async Task RefusedUnchangedScheduleAsync(string root, bool remove)
    {
        using var instance = Lock(root, "owed-unchanged-" + remove);
        var store = new InMemoryStateStore();
        var pair = Pair("owed-unchanged");
        store.SavePair(pair);
        var clock = new FakeTimeProvider();
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        await using var host = EngineHost.Compose(instance.StateAuthority.ProductRoot, _ => { },
            instance.StateAuthority, Settings, Http, clock, _ => null, _ => store,
            EngineHost.Mode.Direct, startupMonitor: monitor);
        var generation = store.ReadPairExecution(pair.PairId)!.Value.Generation;
        var local = store.ReadSource(pair.PairId, Side.Local)!.Value;
        var remote = store.ReadSource(pair.PairId, Side.Dropbox)!.Value;
        var selected = new FailureRetryOwner.State(1, clock.GetUtcNow().AddSeconds(-1));
        store.RecordPrerequisiteFailure(pair.PairId, generation, local.Epoch, remote.Epoch,
            local.Revision, remote.Revision, null, selected, StateChange.Empty);
        var configuration = (SemaphoreSlim)typeof(EngineHost).GetField("_configuration",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        await configuration.WaitAsync();
        using var cancelled = new CancellationTokenSource();
        var active = host.RunPrerequisiteAsync(pair.PairId, cancelled.Token);
        try
        {
            monitor.Start();
            var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var barrier = monitor.ScheduleQuietStatus(TimeSpan.Zero, () => consumed.TrySetResult());
            await consumed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Check.Require(store.ReadPrerequisiteRetry(pair.PairId) == selected,
                "a due schedule refused during an active prerequisite leaves its retry state unchanged");
            if (remove) store.RemovePair(pair.PairId);
            cancelled.Cancel();
        }
        finally { configuration.Release(); }
        try { await active; }
        catch (OperationCanceledException) { }
        if (!remove) await UntilAsync(() => store.ReadPrerequisiteRetry(pair.PairId) is { Attempts: 2 });
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finalBarrier = monitor.ScheduleQuietStatus(TimeSpan.Zero, () => refreshed.TrySetResult());
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Check.Require(remove ? store.Pair(pair.PairId) is null && store.ReadPrerequisiteRetry(pair.PairId) is null :
            store.ReadPrerequisiteRetry(pair.PairId) is { Attempts: 2 },
            "release restores an unchanged due schedule once, while a removed retry disappears");
    }

    private static void WholeDropboxPairIsAffected()
    {
        var whole = Pair("whole") with { LocalRoot = @"C:\WholeFixture", DropboxRoot = "/" };
        var nested = Pair("nested") with { LocalRoot = @"D:\NestedFixture", DropboxRoot = "/Parent/Child" };
        var affected = typeof(EngineHost).GetMethod("Affected",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var (changed, other) in new[] { (nested, whole), (whole, nested) })
            Check.Require(((string[])affected.Invoke(null, [new[] { whole, nested }, changed])!).Contains(other.PairId),
                "a pair rooted at the whole Dropbox overlaps every pair on its connection");
    }

    private static async Task WaitingEligibilityAsync(string root)
    {
        using var instance = Lock(root, "waiting-eligibility");
        var store = new InMemoryStateStore();
        var clock = new FakeTimeProvider();
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = EngineHost.Compose(instance.StateAuthority.ProductRoot,
            message => delivered.TrySetResult(message), instance.StateAuthority, Settings with { StoragePageSize = 1 },
            Http, clock, _ => null, _ => store, EngineHost.Mode.Persistent, startupMonitor: monitor);
        foreach (var id in new[] { "disabled", "paused", "decision", "eligible", "working" })
            store.SavePair(Pair(id, enabled: id != "disabled"));
        await host.PauseAsync("paused", default);
        store.Commit("decision", StateChange.Empty with
        {
            Raise = [(IssueKind.DriveChange, TextKey.DriveRestored, string.Empty, new IssueSubject([], null))]
        });
        var ordinal = 0;
        foreach (var id in new[] { "disabled", "paused", "decision", "eligible" })
        {
            var generation = store.ReadPairExecution(id)!.Value.Generation;
            var local = store.ReadSource(id, Side.Local)!.Value;
            var remote = store.ReadSource(id, Side.Dropbox)!.Value;
            store.RecordPrerequisiteFailure(id, generation, local.Epoch, remote.Epoch,
                local.Revision, remote.Revision, null,
                new FailureRetryOwner.State(1, clock.GetUtcNow().AddHours(++ordinal)), StateChange.Empty);
        }
        Check.Require(host.Status().Status == AutoStatus.Waiting,
            "waiting observation pages beyond disabled, paused and decision-held earlier retry rows");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = host.DispatchAsync("working", async (_, _, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return true;
        }, default);
        try
        {
            await entered.Task;
            Check.Require(host.Status().Status == AutoStatus.Running,
                "an active pair keeps higher-priority status while another pair waits");
            monitor.Start(quietOnly: true);
            await UntilAsync(() => clock.HasArmedTimer);
            clock.Advance(clock.NextDueIn!.Value);
            Check.Require(await delivered.Task.WaitAsync(TimeSpan.FromSeconds(30)) ==
                Text.Get(TextKey.StatusStillWaiting, (TextArg.Reason, nameof(AttemptOwner.Prerequisite))),
                "another running pair cannot hide the eligible prerequisite waiting heartbeat");
        }
        finally { release.TrySetResult(); await running; }
        await host.PauseAsync("eligible", default);
        Check.Require(host.Status().Status != AutoStatus.Waiting,
            "no waiting status remains when every durable retry is ineligible");
    }

    private sealed class FailingRetryStore(IStateStore inner) : ForwardingStateStore(inner)
    {
        internal bool Fail = true;
        internal readonly IOException Failure = new("synthetic retry accounting failure");
        public override void RecordPrerequisiteFailure(string pairId, long expectedGeneration, long localEpoch,
            long dropboxEpoch, long localRevision, long dropboxRevision, FailureRetryOwner.State? expected,
            FailureRetryOwner.State next, StateChange failureNotice)
        {
            if (Fail) throw Failure;
            base.RecordPrerequisiteFailure(pairId, expectedGeneration, localEpoch, dropboxEpoch,
                localRevision, dropboxRevision, expected, next, failureNotice);
        }
    }

    // LOCAL-IN-USE C3/D1: item retry authority belongs to the host's exact registration, not the Store. A real
    // opportunity (never a clock tick) registers one attempt per subject and generation; a repeated report or
    // completion returns the one recorded result instead of writing twice, and a conflicting one is refused.
    private static async Task ReservationWorkAsync(string root)
    {
        // Doubling consumed subjects exposes the old triangular scan; singleton hashing needs no enumeration.
        foreach (var count in new[] { 128, 256 })
        {
            using var instance = Lock(root, "reservation-work-" + count);
            const string PairId = "reservation-work";
            var store = new InMemoryStateStore();
            store.SavePair(Paired(PairId));
            await using var host = Compose(instance.StateAuthority, store);
            var visits = 0;
            var reservationVisits = 0;
            var hashes = new HashSet<int>();
            await host.DispatchAsync(PairId, async (registration, pair, generation, token) =>
            {
                var source = store.ReadSource(PairId, Side.Local) ?? throw new InvalidOperationException();
                for (var index = 0; hashes.Count < count; index++)
                {
                    var path = "item-" + index;
                    var item = new IssueItem(Side.Local, path, path);
                    if (!hashes.Add(item.GetHashCode())) continue;
                    var subject = new IssueSubject(new Storage.StateStoreChecks.ObservedList<IssueItem>(
                        [item], () => visits++), null);
                    var before = visits;
                    await host.ExecuteItemAttemptAsync(registration, registration, null, subject, (id, ct) =>
                    {
                        reservationVisits += visits - before;
                        host.ReportItemFailure(registration, id, source.Epoch, path, subject,
                            TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null);
                        return Task.FromResult(true);
                    }, token);
                }
                return true;
            }, CancellationToken.None);
            Check.Require(reservationVisits == 0,
                "distinct singleton reservations never enumerate previously consumed subjects");
        }
    }

    private static async Task ItemAttemptAuthorityAsync(string root)
    {
        using var instance = Lock(root, "item-attempt");
        const string PairId = "item-attempt-pair";
        const string Path = "in-use.txt";
        var store = new InMemoryStateStore();
        store.SavePair(Paired(PairId));
        await using var host = Compose(instance.StateAuthority, store);
        var subject = new IssueSubject([new IssueItem(Side.Local, Path, "local-1")], null);
        ItemRetryCandidate? reported = null;
        var admitted = await host.DispatchAsync(PairId, async (registration, pair, generation, token) =>
            {
                Check.Require(!host.IsRegisteredItemAttempt(Guid.NewGuid(), PairId, generation, subject, null),
                    "no attempt is registered before an actual opportunity selects one");
                var first = await host.ExecuteItemAttemptAsync(registration, registration, null, subject,
                    (attemptId, ct) =>
                    {
                        Check.Require(host.IsRegisteredItemAttempt(attemptId, PairId, generation, subject, null),
                            "the selected opportunity registers its exact attempt, pair and generation");
                        Check.Require(!host.IsRegisteredItemAttempt(attemptId, PairId, generation + 1, subject, null),
                            "the same attempt id is not registered for another generation");
                        var source = store.ReadSource(PairId, Side.Local) ?? throw new InvalidOperationException();
                        var fabricated = false;
                        try
                        {
                            host.ReportItemFailure(registration, Guid.NewGuid(), source.Epoch, Path, subject,
                                TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null);
                        }
                        catch (LiveAdmissionStaleException) { fabricated = true; }
                        Check.Require(fabricated, "an unregistered attempt id cannot report a failure");
                        reported = host.ReportItemFailure(registration, attemptId, source.Epoch, Path, subject,
                            TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null);
                        var repeated = host.ReportItemFailure(registration, attemptId, source.Epoch, Path, subject,
                            TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null);
                        Check.Require(ReferenceEquals(reported, repeated),
                            "a repeated identical report returns the one recorded candidate");
                        var conflicted = false;
                        try
                        {
                            host.ReportItemFailure(registration, attemptId, source.Epoch, "other.txt", subject,
                                TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null);
                        }
                        catch (LiveAdmissionStaleException) { conflicted = true; }
                        Check.Require(conflicted, "a conflicting duplicate report is refused");
                        return Task.FromResult(true);
                    }, token).ConfigureAwait(false);
                var refused = false;
                try
                {
                    await host.ExecuteItemAttemptAsync(registration, registration, null,
                        new IssueSubject([subject.Items[0], subject.Items[0]], Side.Dropbox),
                        (_, _) => Task.FromResult(true), token);
                }
                catch (LiveAdmissionStaleException error)
                {
                    refused = error.Reason == LiveAdmissionStaleReason.SelectionChanged;
                }
                Check.Require(refused, "consumed item set refuses repetition regardless of Incoming or duplicates");
                var other = new IssueSubject([new IssueItem(Side.Local, "other.txt", "other")], null);
                Check.Require(await host.ExecuteItemAttemptAsync(registration, registration, null, other,
                    (_, _) => Task.FromResult(true), token), "another item set remains admissible");
                return first;
            }, CancellationToken.None).ConfigureAwait(false);
        Check.Require(admitted && reported is not null, "the first report is recorded once through the host");

        // A later opportunity completes the recorded candidate; the same change is idempotent, a different one refuses.
        var candidate = store.ReadItemRetryForSubject(PairId, store.ReadPairExecution(PairId)!.Value.Generation,
            Path, subject) ?? throw new InvalidOperationException();
        var completed = await host.DispatchAsync(PairId, (registration, pair, generation, token) =>
                host.ExecuteItemAttemptAsync(registration, registration, candidate, subject, (attemptId, ct) =>
                {
                    var change = StateChange.Empty with
                    { Raise = [(IssueKind.Failure, TextKey.FailLocalInUse, Path, subject)] };
                    host.CompleteItemAttempt(registration, attemptId, candidate, change, null, null);
                    host.CompleteItemAttempt(registration, attemptId, candidate, change, null, null);
                    var divergent = false;
                    try { host.CompleteItemAttempt(registration, attemptId, candidate, StateChange.Empty, null, null); }
                    catch (LiveAdmissionStaleException) { divergent = true; }
                    Check.Require(divergent, "a conflicting duplicate completion is refused");
                    return Task.FromResult(true);
                }, token), CancellationToken.None).ConfigureAwait(false);
        Check.Require(completed, "the recorded candidate completes once through the host");
        Check.Require(store.Issues(PairId, [IssueKind.Failure])
                .Count(issue => issue.Cause == TextKey.FailLocalInUse) == 1,
            "the in-use failure is one protected issue, not a duplicate write");
    }

    // A176: a WorkSelection opportunity is admitted only for its own pair and generation, through the one owner rule.
    private static async Task SelectedOpportunityAuthorityAsync(string root)
    {
        using var instance = Lock(root, "selected-opportunity");
        const string PairId = "selected-opportunity-pair";
        var store = new InMemoryStateStore();
        store.SavePair(Paired(PairId));
        await using var host = Compose(instance.StateAuthority, store);
        var subject = new IssueSubject([new IssueItem(Side.Local, "selected.txt", "local-selected")], null);
        var rejected = 0;
        var worked = await host.DispatchAsync(PairId, async (registration, pair, generation, token) =>
        {
            WorkSelection Selection(string pairId, long at, string key) =>
                new(pairId, at, 0, Side.Local, "local", null, key, 0, 0);
            static Task<bool> Run(Guid attemptId, CancellationToken attemptToken) => Task.FromResult(true);
            Check.Require(await host.ExecuteItemAttemptAsync(registration, registration, null, subject, Run, token),
                "the registration opportunity itself is admitted once");
            foreach (var key in new[] { "selected.txt", "selected-2.txt" })
            {
                var selectedSubject = new IssueSubject([new IssueItem(Side.Local, key, "local-" + key)], null);
                Check.Require(await host.ExecuteItemAttemptAsync(registration, Selection(PairId, generation, key), null,
                        selectedSubject, Run, token),
                    "each selected opportunity for this pair and generation is admitted");
            }
            foreach (var wrong in new[] { Selection("other-pair", generation, "selected.txt"),
                Selection(PairId, generation + 1, "selected.txt") })
            {
                var wrongSubject = new IssueSubject([new IssueItem(Side.Local, wrong.Key,
                    "local-" + wrong.PairId + wrong.PairGeneration)], null);
                try
                {
                    await host.ExecuteItemAttemptAsync(registration, wrong, null, wrongSubject, Run, token);
                }
                catch (LiveAdmissionStaleException error)
                {
                    if (error.Reason == LiveAdmissionStaleReason.SelectionChanged) rejected++;
                }
            }
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        Check.Require(worked && rejected == 2,
            "two selected opportunities are admitted; another pair or generation is refused with the existing stale cause");
    }

    // A163: the shared retry surface never retries a Request attempt; only DropboxApi's own loop does.
    private static async Task RequestAttemptIsNotRetriedAsync(string root)
    {
        using var instance = Lock(root, "request-no-retry");
        const string PairId = "request-no-retry-pair";
        var store = new InMemoryStateStore();
        store.SavePair(Paired(PairId));
        await using var host = Compose(instance.StateAuthority, store);
        var retried = await host.DispatchAsync(PairId, async (registration, pair, generation, token) =>
        {
            var listing = host.BeginListing(registration, AttemptOwner.Request);
            var attempt = listing.BeginAttempt();
            Check.Require(attempt.TryComplete(RequestResult.TransientFailure, null, TimeSpan.Zero,
                    noEffectProven: true),
                "a Request attempt publishes its transient completion");
            var retry = await listing.CompleteAttemptAsync(attempt, token).ConfigureAwait(false);
            await listing.EndAsync().ConfigureAwait(false);
            return retry;
        }, CancellationToken.None).ConfigureAwait(false);
        Check.Require(!retried, "a Request attempt is never retried by the shared retry surface");
    }

    // A164: a Transfer retry requires proved no-effect; an unproved one is not retried by the session.
    private static async Task TransferRetryRequiresProofAsync(string root)
    {
        using var instance = Lock(root, "transfer-no-retry");
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "synthetic-account", "root", "home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        using var inner = Massdex.SyncShark.Engine.Storage.StateStore.Open(
            Path.Combine(instance.StateAuthority.StateRoot, "transfer-retry.db"));
        var pair = Pair("transfer-no-retry-pair") with { ConnectionId = binding.ConnectionId };
        inner.SavePair(pair);
        var host = EngineHost.Compose(instance.StateAuthority.ProductRoot, _ => { }, instance.StateAuthority,
            Settings, Http, TimeProvider.System, _ => null, _ => inner, EngineHost.Mode.Direct,
            new EngineExternals(new SyntheticCredentials(), _ => new HttpClient()));
        try
        {
            await host.RecomposeCurrentAsync(pair.PairId, default);
            var unproved = await host.DispatchAsync(pair.PairId, async (registration, admitted, generation, token) =>
            {
                var session = PairSession.Open(inner, admitted, Settings, Http, false, null,
                    instance.StateAuthority, host, registration, generation, TimeProvider.System);
                var attempt = new RequestAttempt(new object(), AttemptOwner.Transfer);
                Check.Require(attempt.TryComplete(RequestResult.TransientFailure, null, TimeSpan.Zero,
                        noEffectProven: false),
                    "an unproved Transfer publishes its transient completion");
                return await session.Context.CompleteDownloadAttemptAsync(attempt, token).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
            Check.Require(!unproved, "an unproved Transfer attempt is not retried");
        }
        finally { await host.DisposeAsync(); }
    }

    private static void StoppedPrerequisiteLogKeepsDistinctFailures(string root)
    {
        var messages = new List<string>();
        using var log = RunLog.Open(Path.Combine(root, "stopped-diagnoses"), "pair", "run",
            messages.Add, Settings.LogBufferCharacters);
        var cancellation = new OperationCanceledException();
        var cleanup = new IOException("synthetic cleanup failure");
        log.Failed(cancellation);
        log.Failed(new PrerequisiteStoppedException(cancellation));
        log.Failed(cleanup);
        log.Failed(new OperationCanceledException());
        log.Dispose();
        var lines = File.ReadAllLines(log.Path).Select(line => line.Split(RunLog.FieldSeparator)).ToArray();
        Check.Require(lines.Count(fields => fields[RunLog.FieldKind] == nameof(RunEventKind.Failed)) == 3 &&
            messages.Count(message => message.Contains(Text.Get(TextKey.HumanStopped), StringComparison.Ordinal)) == 2,
            "a stop wrapper keeps its diagnosis identity without hiding distinct cancellation or cleanup failures");
        Check.Require(lines.Any(fields => fields[RunLog.FieldWhat] == nameof(IOException)),
            "a genuinely distinct cleanup failure remains visible");
    }

    // BUG-132: the prerequisite admission read happens before the attempt's owned log exists. A genuine failure
    // there is still diagnosed through a log the host opens for it, even while the pair is paused.
    private static async Task PausedPrerequisiteLogsAnAdmissionErrorAsync(string root)
    {
        using var instance = Lock(root, "paused-admission-error");
        const string PairId = "paused-admission-error-pair";
        var inner = new InMemoryStateStore();
        inner.SavePair(Pair(PairId));
        var store = new FailingPairReadStore(inner);
        var folder = Path.Combine(root, "paused-admission-error", "logs");
        var written = new List<string>();
        var host = EngineHost.Compose(instance.StateAuthority.ProductRoot, _ => { }, instance.StateAuthority,
            Settings, Http, TimeProvider.System, pairId =>
            {
                var log = RunLog.Open(folder, pairId, ResourceNames.NewId(), _ => { },
                    Settings.LogBufferCharacters);
                written.Add(log.Path);
                return log;
            }, _ => store, EngineHost.Mode.Direct);
        try
        {
            await host.PauseAsync(PairId, CancellationToken.None).ConfigureAwait(false);
            store.Error = new IOException("synthetic admission read failure");
            var thrown = false;
            try { await host.RunPrerequisiteAsync(PairId, CancellationToken.None).ConfigureAwait(false); }
            catch (IOException) { thrown = true; }
            Check.Require(thrown, "the admission read failure reaches the caller");
            var lines = written.SelectMany(File.ReadAllLines)
                .Select(line => line.Split(RunLog.FieldSeparator)).ToArray();
            Check.Require(lines.Any(fields => fields.Length > 2 && fields[1] == nameof(RunEventKind.Failed) &&
                    fields[2] == nameof(IOException)),
                "a paused prerequisite logs its genuine admission error");
        }
        finally
        {
            store.Error = null;
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    // V13/RC05-V5-F122: an explicit pause stops only Dispatch admission; the source acquisition lifetime and the
    // resumed Dispatch both stay valid, so a pause is not a general operation hold.
    private static async Task PauseAdmissionRoutingAsync(string root)
    {
        using var instance = Lock(root, "pause-routing");
        const string PairId = "pause-routing-pair";
        var store = new InMemoryStateStore();
        store.SavePair(Pair(PairId));
        await using var host = Compose(instance.StateAuthority, store);
        await host.PauseAsync(PairId, CancellationToken.None).ConfigureAwait(false);
        Check.Require(host.IsPaused(PairId), "the pair is paused");
        var dispatchRefused = false;
        try
        {
            await host.DispatchAsync(PairId, (_, _, _, _) => Task.FromResult(true), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (LiveAdmissionStaleException) { dispatchRefused = true; }
        Check.Require(dispatchRefused, "a paused pair refuses a new Dispatch registration");
        Check.Require(await host.AcquireAsync(PairId, (_, _, _, _) => Task.FromResult(true), CancellationToken.None)
                .ConfigureAwait(false),
            "a paused pair still admits its source acquisition lifetime");
        await host.ResumeAsync(PairId, CancellationToken.None).ConfigureAwait(false);
        Check.Require(!host.IsPaused(PairId), "resume clears the pause");
        Check.Require(await host.DispatchAsync(PairId, (_, _, _, _) => Task.FromResult(true), CancellationToken.None)
                .ConfigureAwait(false),
            "resume restores Dispatch admission");
    }

    private static async Task ExternalClientOwnershipAsync(string root)
    {
        using var instance = Lock(root, "external-clients");
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "synthetic-account", "root", "home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        var store = new InMemoryStateStore();
        var pairs = new[] { Pair("external-a"), Pair("external-b") }
            .Select(pair => pair with { ConnectionId = binding.ConnectionId }).ToArray();
        foreach (var pair in pairs) store.SavePair(pair);
        var handlers = new List<OwnedHandler>();
        var host = EngineHost.Compose(instance.StateAuthority.ProductRoot, _ => { }, instance.StateAuthority,
            Settings, Http, TimeProvider.System, _ => null, _ => store, EngineHost.Mode.Direct,
            new EngineExternals(new SyntheticCredentials(), _ =>
            {
                var handler = new OwnedHandler();
                handlers.Add(handler);
                return new HttpClient(handler);
            }));
        try
        {
            foreach (var pair in pairs) await host.RecomposeCurrentAsync(pair.PairId, CancellationToken.None);
            await host.DrainGenerationAsync(pairs[0].PairId, CancellationToken.None);
            await host.RecomposeCurrentAsync(pairs[0].PairId, CancellationToken.None);
            Check.Require(handlers.Count == 3 && handlers[0].Disposed == 1 && handlers[1].Disposed == 0 &&
                handlers[2].Disposed == 0, "recomposing one pair disposes only its former longpoll client");
            await host.AcquireAsync(pairs[1].PairId,
                (registration, pair, generation, _) => Task.FromResult(
                    host.BorrowGeneration(registration, pair.PairId, generation)), CancellationToken.None);
        }
        finally { await host.DisposeAsync(); }
        await host.DisposeAsync();
        Check.Require(handlers.All(handler => handler.Disposed == 1),
            "host stop and repeated disposal dispose every independently owned client exactly once");
    }

    private static async Task ExternalClientFailureAsync(string root)
    {
        using var instance = Lock(root, "external-client-failure");
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "synthetic-account", "root", "home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        var pair = Pair("external-failure") with { ConnectionId = binding.ConnectionId };
        var handler = new OwnedHandler();
        var limiter = new AdaptiveLimiter(1, 1, TimeProvider.System, new FakeNetworkSignal(), _ => { },
            Settings.Backoff);
        var refused = false;
        try
        {
            // The feed's process-wide session key refuses a missing caller identity after the client exists.
            PairSession.ComposeGeneration(new InMemoryStateStore(), pair, [pair], 0, Settings, Http,
                instance.StateAuthority, TimeProvider.System, _ => limiter, (_, _, _) => new MemoryStream(),
                null!, new EngineExternals(new SyntheticCredentials(), _ => new HttpClient(handler)));
        }
        catch (ArgumentNullException) { refused = true; }
        Check.Require(refused && handler.Disposed == 1,
            "A87: a constructor failure after the independently owned client is created disposes that client once");
        await Task.CompletedTask;
    }

    private static async Task StartupReceiptsAsync(string root, bool fail, bool jobs, bool opportunity,
        bool paused = false, bool startup = false, bool shared = false)
    {
        using var instance = Lock(root, shared ? "receipts-shared" : opportunity && !startup
            ? (fail ? "receipt-opportunity-failure" : "receipt-opportunity")
            : startup && !paused ? "receipts-startup" : paused ? "receipts-paused" : jobs ? "receipts-jobs" :
            fail ? "receipts-storage-failure" : "receipts-disabled");
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "synthetic-account", "root", "home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        using var inner = Massdex.SyncShark.Engine.Storage.StateStore.Open(
            Path.Combine(instance.StateAuthority.StateRoot, "receipts.db"));
        var pair = Pair("receipt-pair", enabled: opportunity || paused || startup) with { ConnectionId = binding.ConnectionId };
        inner.SavePair(pair);
        if (opportunity)
            foreach (var side in new[] { Side.Local, Side.Dropbox })
            {
                var source = inner.ReadSource(pair.PairId, side)!.Value;
                inner.AdvanceSource(pair.PairId, side, inner.ReadPairExecution(pair.PairId)!.Value.Generation,
                    source.Epoch, source.Revision, source.Provenance, source.Cursor, source.Epoch,
                    source.Revision + 1, source.Provenance, source.Cursor, null);
            }
        var store = new ReceiptReadStore(inner, fail);
        string? sharedJob = null;
        if (shared)
        {
            var first = await SeedOperationAsync(inner, pair, "SharedA");
            var second = await SeedOperationAsync(inner, pair, "SharedB");
            sharedJob = AcceptReceiptAsync(inner, pair, [.. first, .. second], "shared");
        }
        var bad = jobs && !shared ? await SeedReceiptAsync(inner, pair, "Bad") : null;
        var good = jobs && !shared ? await SeedReceiptAsync(inner, pair, "Good") : null;
        using var handler = new ReceiptHandler();
        using var http = new HttpClient(handler);
        await using var host = EngineHost.Compose(instance.StateAuthority.ProductRoot, _ => { },
            instance.StateAuthority, Settings, http, TimeProvider.System, _ => null, _ => store,
            EngineHost.Mode.Persistent,
            new EngineExternals(new SyntheticCredentials(), _ => new HttpClient(new OwnedHandler())));
        if (opportunity && !startup)
        {
            var auto = new Massdex.SyncShark.Engine.Run.AutoSync(store, _ => null, host);
            try
            {
                store.SelectBudget = 2;
                auto.Start();
                if (fail)
                {
                    await store.Entered.Task;
                    try { await auto.StopAsync(); }
                    catch (InvalidDataException error) when (ReferenceEquals(error, store.Failure)) { }
                    Check.Require(host.Status().Status == AutoStatus.Held && store.Passes == 1,
                        "Storage failure in an opportunity reaches BackgroundFailed, rather than a provider refusal.");
                    return;
                }
                await store.Selections.Task;
                Check.Require(store.Passes == 1 && store.SelectCalls == 4 && handler.Reads == 2,
                    $"One opportunity with two selections observes both jobs once " +
                    $"(passes={store.Passes}, reads={handler.Reads}, selects={store.SelectCalls}).");
                store.SelectedAt = 5;
                store.Selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                auto.Kick(pair.PairId);
                await store.Selected.Task;
                Check.Require(store.Passes == 2 && store.SelectCalls == 5 && handler.Reads == 3,
                    $"A later opportunity retries the persistent job but not the terminal receipt " +
                    $"(passes={store.Passes}, reads={handler.Reads}, selects={store.SelectCalls}).");
            }
            finally
            {
                try { await auto.StopAsync(); }
                catch (InvalidDataException error) when (fail && ReferenceEquals(error, store.Failure)) { }
            }
            Check.Require(inner.FolderSubmission(good!).Phase == FolderSubmission.SubmissionPhase.Terminal &&
                inner.FolderSubmission(bad!).Phase == FolderSubmission.SubmissionPhase.Accepted,
                "A bad observation does not fail AutoSync or invent completion.");
            return;
        }
        if (paused)
        {
            // A paused enabled pair: the Observation registration runs the startup pass; Dispatch is refused.
            await host.PauseAsync(pair.PairId, default);
            await ReconcileAtStartupAsync(host, store, http, instance.StateAuthority, pair.PairId);
            var dispatchRefused = false;
            try
            {
                await host.DispatchAsync(pair.PairId, (_, _, _, _) => Task.FromResult(true), default);
            }
            catch (LiveAdmissionStaleException) { dispatchRefused = true; }
            Check.Require(store.Passes == 1 && handler.Reads == 2 && dispatchRefused &&
                inner.FolderSubmission(bad!).Phase == FolderSubmission.SubmissionPhase.Accepted &&
                inner.FolderSubmission(good!).Phase == FolderSubmission.SubmissionPhase.Terminal,
                "A paused enabled pair is reconciled once at startup and dispatches nothing.");
            return;
        }
        if (startup)
        {
            // The order StartAsync uses: the pair's own receipt pass, then AutoSync's continuation opportunity.
            await ReconcileAtStartupAsync(host, store, http, instance.StateAuthority, pair.PairId);
            Check.Require(store.Passes == 1 && handler.Reads == 2,
                "An enabled startup reconciles both jobs before AutoSync continues.");
            var auto = new Massdex.SyncShark.Engine.Run.AutoSync(store, _ => null, host);
            try
            {
                store.SelectBudget = 2;
                auto.Start();
                await store.Selections.Task;
                Check.Require(store.Passes == 2 && store.SelectCalls == 4 && handler.Reads == 3 &&
                    handler.GoodReads == 1 && handler.BadReads == 2 &&
                    inner.FolderSubmission(good!).Phase == FolderSubmission.SubmissionPhase.Terminal &&
                    inner.FolderSubmission(bad!).Phase == FolderSubmission.SubmissionPhase.Accepted,
                    $"The AutoSync continuation rereads only the persistent job (passes={store.Passes}, " +
                    $"reads={handler.Reads}, good={handler.GoodReads}, bad={handler.BadReads}).");
            }
            finally { await auto.StopAsync(); }
            return;
        }
        Exception? caught = null;
        try { await host.StartAsync(default); }
        catch (InvalidDataException error) { caught = error; }
        Check.Require(store.Passes == 1 && (fail ? ReferenceEquals(caught, store.Failure) : caught is null),
            "Startup reaches one receipt pass for a disabled unbound pair; storage failure escapes unchanged.");
        if (shared)
        {
            Check.Require(handler.Reads == 1 &&
                inner.FolderSubmission(sharedJob!).Phase == FolderSubmission.SubmissionPhase.Terminal &&
                inner.FolderSubmission(sharedJob!).Cause is null &&
                inner.ActiveFolderRemovals(pair.PairId, default).Count() == 2,
                $"A submission shared by two operations is read once at startup (reads={handler.Reads}).");
        }
        else if (jobs)
        {
            Check.Require(inner.FolderSubmission(bad!).Phase == FolderSubmission.SubmissionPhase.Accepted &&
                inner.FolderSubmission(bad!).Cause == TextKey.DropboxRefused &&
                inner.FolderSubmission(good!).Phase == FolderSubmission.SubmissionPhase.Terminal,
                $"One bad job cannot prevent the next startup receipt: reads={handler.Reads}, " +
                $"bad={inner.FolderSubmission(bad!).Phase}/{inner.FolderSubmission(bad!).Cause}, " +
                $"good={inner.FolderSubmission(good!).Phase}/{inner.FolderSubmission(good!).Cause}.");
            Check.Require(inner.ActiveFolderRemovals(pair.PairId, default).Count() == 2,
                "Startup receipts never retire either operation.");
        }
        else Check.Require(!inner.ActiveFolderRemovals(pair.PairId, default).Any() && handler.Reads == 0,
            "An empty receipt pass does not invent recovery work or provider reads.");
    }

    // #79: an InProgress folder-delete job owns its next poll, so it completes with no unrelated wake.
    private static async Task InProgressFolderJobRepollsToTerminalUnknownAsync(string root)
    {
        using var instance = Lock(root, "folder-job-repoll");
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "synthetic-account", "root", "home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        using var store = Massdex.SyncShark.Engine.Storage.StateStore.Open(
            Path.Combine(instance.StateAuthority.StateRoot, "repoll.db"));
        var pair = Pair("repoll-pair", enabled: true) with { ConnectionId = binding.ConnectionId };
        store.SavePair(pair);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var source = store.ReadSource(pair.PairId, side)!.Value;
            store.AdvanceSource(pair.PairId, side, store.ReadPairExecution(pair.PairId)!.Value.Generation,
                source.Epoch, source.Revision, source.Provenance, source.Cursor, source.Epoch,
                source.Revision + 1, source.Provenance, source.Cursor, null);
        }
        var id = await SeedReceiptAsync(store, pair, "Poll");
        using var handler = new ReceiptHandler { ProgressReads = 2 };
        using var http = new HttpClient(handler);
        var clock = new FakeTimeProvider();
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        await using var host = EngineHost.Compose(instance.StateAuthority.ProductRoot, _ => { },
            instance.StateAuthority, Settings, http, clock, _ => null, _ => store,
            EngineHost.Mode.Persistent,
            new EngineExternals(new SyntheticCredentials(), _ => new HttpClient(new OwnedHandler())),
            startupMonitor: monitor);
        monitor.Start();
        var auto = new Massdex.SyncShark.Engine.Run.AutoSync(store, _ => null, host);
        try
        {
            auto.Start();
            await handler.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var armed = Environment.TickCount64 + 30_000;
            while (!clock.HasArmedTimer && Environment.TickCount64 < armed) await Task.Yield();
            Check.Require(clock.HasArmedTimer && store.FolderSubmission(id).Phase ==
                FolderSubmission.SubmissionPhase.Accepted,
                "the InProgress observation leaves the job accepted and arms its next poll");
            // No Kick: the armed poll is the only thing that can reach the completion.
            var completed = Environment.TickCount64 + 30_000;
            while (store.FolderSubmission(id).Phase != FolderSubmission.SubmissionPhase.Terminal &&
                   Environment.TickCount64 < completed)
            {
                if (clock.HasArmedTimer) clock.Advance(Settings.FolderDeletePollInterval);
                await Task.Yield();
            }
            var final = store.FolderSubmission(id);
            Check.Require(handler.GoodReads == 3 && final.Phase == FolderSubmission.SubmissionPhase.Terminal,
                $"the scheduled poll observes the job again and completes it (reads={handler.GoodReads}, phase={final.Phase})");
        }
        finally { await auto.StopAsync(); }
    }

    // R6-6: the real host poll survives a refused survivor, then a recovery wake reaches retained work.
    private static async Task RepairHeldSurvivorPollAsync(string root)
    {
        using var instance = Lock(root, "repair-held-survivor");
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "root", "home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        var localRoot = Directory.CreateDirectory(Path.Combine(root, "survivor-local")).FullName;
        var pair = Pair("survivor-pair") with
        {
            LocalRoot = localRoot, DropboxRoot = "/Pilot", ConnectionId = binding.ConnectionId,
            Roots = new RootBinding(localRoot, "/Pilot", binding.ConnectionId,
                WindowsDirectoryListing.ReadIdentity(localRoot),
                new DropboxRootIdentity(binding.AccountId, "root", "home", DropboxWire.RootId))
        };
        using var inner = StateStore.Open(Path.Combine(instance.StateAuthority.StateRoot, "survivor.db"));
        inner.SavePair(pair);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=" + Path.Combine(instance.StateAuthority.StateRoot, "survivor.db") + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE pair SET first_repair_done=1;";
            command.ExecuteNonQuery();
        }
        void Debt(SourceObligation? reason)
        {
            foreach (var side in new[] { Side.Local, Side.Dropbox })
            {
                var source = inner.ReadSource(pair.PairId, side)!.Value;
                inner.AdvanceSource(pair.PairId, side, inner.ReadPairExecution(pair.PairId)!.Value.Generation,
                    source.Epoch, source.Revision, source.Provenance, source.Cursor, source.Epoch,
                    source.Revision + 1, source.Provenance, source.Cursor, side == Side.Local ? reason : null);
            }
        }
        Debt(null);
        var store = new ReceiptReadStore(inner, false);
        using var wire = new DropboxWire { AccountBinding = binding, DeleteBatchAsyncJob = true };
        wire.SeedFolder("Folder");
        wire.SeedFolder("Folder/Keep");
        wire.SeedFolder("Folder/Gone");
        wire.SeedFolder("Poll");
        using var http = new HttpClient(wire, disposeHandler: false);
        var clock = new FakeTimeProvider();
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        await using var host = EngineHost.Compose(instance.StateAuthority.ProductRoot, _ => { },
            instance.StateAuthority, Settings, http, clock, _ => null, _ => store, EngineHost.Mode.Persistent,
            new EngineExternals(new SyntheticCredentials(), _ => new HttpClient(wire, disposeHandler: false),
                new FakeRecovery()), startupMonitor: monitor);
        await host.RecomposeCurrentAsync(pair.PairId, default);
        var owned = await host.DispatchSessionAsync(pair.PairId, () => null, async (session, token) =>
        {
            async Task<(string Operation, string Submission)> Begin(string path, bool survivor)
            {
                var entry = (await session.Dropbox.ReadOneAsync(path, token))!;
                var local = entry with { Id = "local-" + path };
                var saved = SyncedEntries.From(local, entry);
                var records = new List<SyncedEntry> { saved };
                if (survivor)
                {
                    var gone = (await session.Dropbox.ReadOneAsync(path + "/Gone", token))!;
                    records.Add(SyncedEntries.From(gone with { Id = "local-gone" }, gone));
                }
                store.Commit(pair.PairId, StateChange.Empty with { Record = records });
                var row = new Row(PathNames.DropboxKey(path), null, entry, saved, [], null,
                    new RowContext(false, false, false, null, false, false, true, true));
                using var plan = await new FolderPlanBuilder(session.Context).BuildAsync(row, Side.Dropbox,
                    new HashSet<string>(StringComparer.Ordinal), live: false, token);
                var operationId = store.BeginFolderRemoval(pair.PairId, plan, Settings.StoragePageSize, token);
                var operation = store.FolderRemoval(pair.PairId, operationId);
                store.AdvanceFolderRemoval(pair.PairId, operationId, operation.Generation, operation.Phase,
                    operation with { Generation = operation.Generation + 1,
                        Cursor = await session.Context.CaptureFolderCursorAsync!(token) });
                var effects = store.ReadyFolderEffects(pair.PairId, operationId, -1, Settings.StoragePageSize).ToArray();
                var id = store.ArmFolderSubmission(pair.PairId, effects);
                var armed = store.FolderSubmission(id);
                var result = (FolderSubmitResult.Accepted)await session.Dropbox.SubmitFolderRemovalAsync(
                    armed, effects, new RequestAttempt(new object(), AttemptOwner.Recovery), token);
                store.AdvanceFolderSubmission(armed, armed with { Generation = armed.Generation + 1,
                    Phase = FolderSubmission.SubmissionPhase.Accepted, JobId = result.JobId }, []);
                if (survivor)
                {
                    var terminal = (FolderJobResult.Terminal)await session.Dropbox.ReadFolderRemovalAsync(
                        result.JobId, effects.Length, new RequestAttempt(new object(), AttemptOwner.Recovery), token);
                    var accepted = store.FolderSubmission(id);
                    store.AdvanceFolderSubmission(accepted, accepted with { Generation = accepted.Generation + 1,
                        Phase = FolderSubmission.SubmissionPhase.Terminal }, terminal.Results);
                    var current = store.FolderRemoval(pair.PairId, operationId);
                    await session.Syncer.RecoverAsync(new RecoveryTarget(operationId, current.Generation, null, null),
                        RecoveryChoice.Check, token);
                    Check.Require(store.FolderRemoval(pair.PairId, operationId).Cursor is null,
                        "R6-6 fixture resume closes the survivor operation's post-window cursor");
                }
                return (operationId, id);
            }
            var survivor = await Begin("Folder", true);
            var poll = await Begin("Poll", false);
            return (Survivor: survivor.Operation, Poll: poll.Submission);
        }, default);
        Debt(SourceObligation.NormalRepair);
        wire.DeleteBatchPendingChecks = 1;
        // Use the host's own AutoSync, so WakeAfterOperation reaches exactly the scheduler under test.
        var auto = (Massdex.SyncShark.Engine.Run.AutoSync)typeof(EngineHost)
            .GetField("_auto", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        monitor.Start();
        bool Pending() => store.PendingFolderFollowUps(pair.PairId, owned.Survivor, -1, Settings.StoragePageSize).Any();
        async Task Until(Func<bool> condition)
        {
            var bound = Environment.TickCount64 + 30_000;
            while (!condition() && Environment.TickCount64 < bound) await Task.Yield();
        }
        try
        {
            auto.Start();
            await Until(() => Volatile.Read(ref store.PollDecisions) >= 1);
            var pending = Pending();
            Check.Require(pending, "R6-6 refused survivor remains retained: pending=" + pending);
            Check.Require(store.LocalRefusals > 1 && store.PollDecisions >= 1,
                "R6-6 survivor refusal is contained through opportunity completion: refusals=" +
                store.LocalRefusals + "; pollDecisions=" + store.PollDecisions);
            var polls = wire.Calls.Count(call => call == "/2/files/delete_batch/check");
            var bound = Environment.TickCount64 + 30_000;
            while (Volatile.Read(ref store.PollDecisions) < 2 && Environment.TickCount64 < bound)
            {
                if (clock.HasArmedTimer) clock.Advance(Settings.FolderDeletePollInterval);
                await Task.Yield();
            }
            var observedPolls = wire.Calls.Count(call => call == "/2/files/delete_batch/check");
            var phase = store.FolderSubmission(owned.Poll).Phase;
            Check.Require(observedPolls == polls + 1 && phase == FolderSubmission.SubmissionPhase.Terminal,
                "R6-6 sibling folder poll executes without a new source event: before=" + polls +
                "; after=" + observedPolls + "; phase=" + phase + "; pollDecisions=" + store.PollDecisions);
            var reads = store.LocalRefusals;
            // Repeat only live work; the later recovery wake deliberately reaches both live work and prerequisites.
            auto.Kick(pair.PairId);
            await Until(() => Volatile.Read(ref store.PollDecisions) >= 3);
            Check.Require(store.LocalRefusals > reads && Pending() && !Directory.Exists(Path.Combine(localRoot, "Folder", "Keep")) &&
                !inner.HasFolderJobProgress(pair.PairId, default),
                "R6-6 repeated refusal without prerequisite completion retains survivor; terminal receipt owns no polling");
            Debt(null);
            host.WakeAfterOperation(pair.PairId);
            await Until(() => Volatile.Read(ref store.PollDecisions) >= 4);
            Check.Require(!Pending() && Directory.Exists(Path.Combine(localRoot, "Folder", "Keep")),
                "R6-6 recovery wake reaches and completes the retained survivor without a new source event");
        }
        finally { await auto.StopAsync(); }
    }

    private static async Task InProgressFolderJobSettlesWithoutWakeAsync(string root)
    {
        using var instance = Lock(root, "folder-job-settle");
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "root", "home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        var localRoot = Directory.CreateDirectory(Path.Combine(root, "settle-local")).FullName;
        var pair = Pair("settle-pair") with
        {
            LocalRoot = localRoot, DropboxRoot = "/Pilot", ConnectionId = binding.ConnectionId,
            Roots = new RootBinding(localRoot, "/Pilot", binding.ConnectionId,
                WindowsDirectoryListing.ReadIdentity(localRoot),
                new DropboxRootIdentity(binding.AccountId, "root", "home", DropboxWire.RootId))
        };
        using var inner = Massdex.SyncShark.Engine.Storage.StateStore.Open(
            Path.Combine(instance.StateAuthority.StateRoot, "settle.db"));
        var store = new ReceiptReadStore(inner, false);
        store.SavePair(pair);
        using var wire = new DropboxWire
            { AccountBinding = binding, DeleteBatchAsyncJob = true, DeleteBatchPendingChecks = 2 };
        using var http = new HttpClient(wire, disposeHandler: false);
        var clock = new FakeTimeProvider();
        var progress = new System.Collections.Concurrent.ConcurrentQueue<string>();
        RunLog OpenLog(string pairId) => RunLog.Open(Path.Combine(root, "settle-logs"), pairId,
            Guid.NewGuid().ToString("N"), progress.Enqueue, Settings.LogBufferCharacters, clock);
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        await using var host = EngineHost.Compose(instance.StateAuthority.ProductRoot, progress.Enqueue,
            instance.StateAuthority, Settings, http, clock, OpenLog, _ => store, EngineHost.Mode.Persistent,
            new EngineExternals(new SyntheticCredentials(), _ => new HttpClient(wire, disposeHandler: false),
                new FakeRecovery()), startupMonitor: monitor);
        Check.Require(await host.RunPrerequisiteAsync(pair.PairId, default) is { Completed: true } &&
            store.FirstRepairDone(pair.PairId) && !store.Obligated(pair.PairId),
            "the receipt fixture starts after a real completed first Repair with current source coverage");
        wire.SeedFolder("Poll");
        var accepted = await host.DispatchSessionAsync(pair.PairId, () => null, async (session, token) =>
        {
            var entry = (await session.Dropbox.ReadOneAsync("Poll", token))!;
            var saved = Massdex.SyncShark.Engine.Executor.SyncedEntries.From(entry with { Id = "local-poll" }, entry);
            store.Commit(pair.PairId, StateChange.Empty with { Record = [saved] });
            var row = new Row(PathNames.DropboxKey(entry.Path), null, entry, saved, [], null,
                new RowContext(false, false, false, null, false, false, true, true));
            using var plan = store.OpenFolderPlan(pair.PairId, row, Side.Dropbox);
            await plan.CaptureAsync(session.Dropbox.ReadAllAsync(entry.Path,
                new ListOptions(Recursive: true, IncludeOutOfScope: true), token), token);
            Check.Require(!plan.Children(token).Any(), "the successful receipt fixture is an empty folder");
            plan.Complete(true, token);
            var operationId = store.BeginFolderRemoval(pair.PairId, plan, StateSchema.OneRow, token);
            var operation = store.FolderRemoval(pair.PairId, operationId);
            store.AdvanceFolderRemoval(pair.PairId, operationId, operation.Generation, operation.Phase,
                operation with { Generation = operation.Generation + 1,
                    Cursor = await session.Context.CaptureFolderCursorAsync!(token) });
            var effects = store.ReadyFolderEffects(pair.PairId, operationId, -1, StateSchema.OneRow).ToArray();
            var id = store.ArmFolderSubmission(pair.PairId, effects);
            var armed = store.FolderSubmission(id);
            var result = (FolderSubmitResult.Accepted)await session.Dropbox.SubmitFolderRemovalAsync(
                armed, effects, new RequestAttempt(new object(), AttemptOwner.Recovery), token);
            store.AdvanceFolderSubmission(armed, armed with { Generation = armed.Generation + 1,
                Phase = FolderSubmission.SubmissionPhase.Accepted, JobId = result.JobId }, []);
            return (Submission: id, Operation: operationId, Root: entry);
        }, default);
        var execution = store.ReadPairExecution(pair.PairId)!.Value;
        var remoteSource = store.ReadSource(pair.PairId, Side.Dropbox)!.Value;
        store.AcquireDropboxPage(pair.PairId, execution.Generation, remoteSource.Epoch, remoteSource.Revision,
            remoteSource.Provenance!, remoteSource.Cursor,
            [new ChangeNotice(Side.Dropbox, ChangeKind.Changed, accepted.Root.Path, null, accepted.Root.Id,
                accepted.Root, false, false),
             new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, "Poll/Child", null, "remote-child", null, false, true)],
            store.FolderRemoval(pair.PairId, accepted.Operation).Cursor!);
        bool WorkPending() => store.PendingAncestors(pair.PairId, execution.Generation, Side.Dropbox,
            [PathNames.DropboxKey("Poll"), PathNames.DropboxKey("Poll/Child")], 0).Any();
        Check.Require(WorkPending(), "the terminal receipt fixture starts with pending root and dependent work");
        monitor.Start();
        var auto = new Massdex.SyncShark.Engine.Run.AutoSync(store, OpenLog, host);
        try
        {
            auto.Start();
            var bound = Environment.TickCount64 + 30_000;
            while ((store.FolderRemoval(pair.PairId, accepted.Operation).Active || WorkPending()) &&
                Environment.TickCount64 < bound)
            {
                if (clock.NextDueIn == Settings.FolderDeletePollInterval) clock.Advance(Settings.FolderDeletePollInterval);
                await Task.Yield();
            }
            string[] calls;
            lock (wire.Calls) calls = wire.Calls.ToArray();
            Check.Require(store.FolderSubmission(accepted.Submission).Phase == FolderSubmission.SubmissionPhase.Terminal &&
                store.FolderRemoval(pair.PairId, accepted.Operation).Phase == FolderRemovalPhase.Complete &&
                !store.ActiveFolderRemovals(pair.PairId, default).Any() &&
                store.FolderRemovalItems(pair.PairId, accepted.Operation, -1, StateSchema.OneRow)
                    .All(item => item.EffectState == FolderRemovalItem.EffectStatus.Settled),
                "InProgress, InProgress, Terminal completes and retires the folder operation with no external wake; " +
                $"submission={store.FolderSubmission(accepted.Submission)}; operation={store.FolderRemoval(pair.PairId, accepted.Operation)}; " +
                $"selects={store.SelectCalls}; pending={WorkPending()}; nextDue={clock.NextDueIn}; " +
                $"passes={store.Passes}; pollDecisions={store.PollDecisions}; calls={string.Join(",", calls)}; " +
                $"progress={string.Join(" | ", progress)}; issues=" +
                string.Join(" | ", store.Issues(pair.PairId).Select(issue => issue.Path + ":" + issue.Cause)));
            Check.Require(wire.Calls.Count(call => call == "/2/files/delete_batch/check") == 3 &&
                wire.Calls.Count(call => call == "/2/files/delete_batch") == 1,
                "the successful terminal fixture has exactly three checks and never resends its one original delete");
            Check.Require(store.SelectCalls >= 2 && !WorkPending(),
                "terminal receipt-only completion wakes ordinary selection and retires root and dependent work without an external event");
        }
        finally { await auto.StopAsync(); }
    }

    private static async Task FolderPollLifecycleAsync(string root, string transition)
    {
        using var instance = Lock(root, "folder-poll-" + transition);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "synthetic-account", "root", "home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        var database = Path.Combine(instance.StateAuthority.StateRoot, "lifecycle.db");
        var pair = Pair("lifecycle-pair") with { ConnectionId = binding.ConnectionId };
        string submission;
        using (var seed = Massdex.SyncShark.Engine.Storage.StateStore.Open(database))
        {
            seed.SavePair(pair);
            foreach (var side in new[] { Side.Local, Side.Dropbox })
            {
                var source = seed.ReadSource(pair.PairId, side)!.Value;
                seed.AdvanceSource(pair.PairId, side, seed.ReadPairExecution(pair.PairId)!.Value.Generation,
                    source.Epoch, source.Revision, source.Provenance, source.Cursor, source.Epoch,
                    source.Revision + 1, source.Provenance, source.Cursor, null);
            }
            submission = await SeedReceiptAsync(seed, pair, "Poll");
        }
        var runs = transition == "restart" ? 2 : 1;
        for (var run = 0; run < runs; run++)
        {
            using var inner = Massdex.SyncShark.Engine.Storage.StateStore.Open(database);
            var store = new ReceiptReadStore(inner, false);
            using var handler = new ReceiptHandler { ProgressReads = 2, HoldRead = transition == "stop" ? 2 : 0 };
            using var http = new HttpClient(handler);
            var clock = new FakeTimeProvider();
            var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
            await using var host = EngineHost.Compose(instance.StateAuthority.ProductRoot, _ => { },
                instance.StateAuthority, Settings, http, clock, _ => null, _ => store, EngineHost.Mode.Persistent,
                new EngineExternals(new SyntheticCredentials(), _ => new HttpClient(new OwnedHandler())),
                startupMonitor: monitor);
            monitor.Start();
            var auto = new Massdex.SyncShark.Engine.Run.AutoSync(store, _ => null, host);
            try
            {
                auto.Start();
                await UntilAsync(() => handler.Reads == 1 && clock.NextDueIn == Settings.FolderDeletePollInterval);
                Check.Require(inner.FolderSubmission(submission).Phase == FolderSubmission.SubmissionPhase.Accepted,
                    "durable accepted receipt rebuilds its armed wait, including after restart");
                if (transition is "pause" or "disable")
                {
                    if (transition == "pause") await host.PauseAsync(pair.PairId, default);
                    else await host.ChangeGenerationAsync(pair, (_, state, _, _) =>
                    {
                        state.SavePair(pair with { Enabled = false });
                        return Task.FromResult(true);
                    }, default);
                    await UntilAsync(() => clock.NextDueIn is null || clock.NextDueIn > Settings.FolderDeletePollInterval);
                    clock.Advance(Settings.FolderDeletePollInterval);
                    await auto.StopAsync();
                    Check.Require(handler.Reads == 1, "pause/disable withdraws the pending folder deadline");
                }
                else if (transition == "stop")
                {
                    clock.Advance(Settings.FolderDeletePollInterval);
                    await handler.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    var autoStopping = auto.StopAsync();
                    var stopping = host.StopAsync(default);
                    Check.Require(!stopping.IsCompleted && !autoStopping.IsCompleted,
                        "stop joins a receipt callback that has not returned");
                    Check.Require(inner.FolderSubmission(submission).Phase == FolderSubmission.SubmissionPhase.Accepted,
                        "the store remains usable while its callback is draining");
                    handler.Release.TrySetResult();
                    await Task.WhenAll(stopping, autoStopping);
                }
            }
            finally { handler.Release.TrySetResult(); await auto.StopAsync(); }
            Check.Require(handler.Deletes == 0, "lifecycle recovery never resends a delete");
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var bound = Environment.TickCount64 + 30_000;
        while (!condition() && Environment.TickCount64 < bound) await Task.Yield();
        Check.Require(condition(), "the controlled asynchronous transition completes");
    }

    /// <summary>The per-pair body StartAsync runs: the Observation registration opens the session and reconciles once.</summary>
    private static async Task ReconcileAtStartupAsync(EngineHost host, IStateStore store, HttpClient http,
        PilotStateAuthority authority, string pairId)
    {
        await host.RecomposeCurrentAsync(pairId, default);
        await host.ObserveAsync(pairId,
            async (registration, pair, generation, token) =>
            {
                var session = PairSession.Open(store, pair, Settings, http, true, null, authority, host,
                    registration, generation, TimeProvider.System);
                await session.Syncer.ReconcileReceiptsAsync(token);
                return pair;
            }, default);
    }

    private static async Task<string> SeedReceiptAsync(IStateStore store, PairConfig pair, string path) =>
        AcceptReceiptAsync(store, pair, await SeedOperationAsync(store, pair, path), path);

    private static async Task<FolderRemovalEffect[]> SeedOperationAsync(IStateStore store, PairConfig pair, string path)
    {
        var side = new FakeSide(Side.Dropbox);
        var entry = await side.EnsureFolderAsync(path, default);
        var row = new Row(PathNames.DropboxKey(path), null, entry, null, [], null,
            new RowContext(false, false, false, null, false, false, true, true));
        using var plan = store.OpenFolderPlan(pair.PairId, row, Side.Dropbox);
        await plan.CaptureAsync(side.ReadAllAsync(path, new ListOptions(Recursive: true, IncludeOutOfScope: true), default), default);
        Check.Require(!plan.Children(default).Any(), "The receipt fixture is an empty folder.");
        plan.Complete(true, default);
        var operation = store.BeginFolderRemoval(pair.PairId, plan, StateSchema.OneRow, CancellationToken.None);
        var current = store.FolderRemoval(pair.PairId, operation);
        store.AdvanceFolderRemoval(pair.PairId, operation, current.Generation, current.Phase,
            current with { Generation = current.Generation + 1, Cursor = "cursor" });
        return store.ReadyFolderEffects(pair.PairId, operation, -1, 1).ToArray();
    }

    private static string AcceptReceiptAsync(IStateStore store, PairConfig pair,
        IReadOnlyList<FolderRemovalEffect> effects, string job)
    {
        var id = store.ArmFolderSubmission(pair.PairId, effects);
        var armed = store.FolderSubmission(id);
        store.AdvanceFolderSubmission(armed, armed with
        {
            Generation = armed.Generation + 1, Phase = FolderSubmission.SubmissionPhase.Accepted, JobId = job
        }, []);
        return id;
    }

    private sealed class ReceiptHandler : HttpMessageHandler
    {
        internal int Reads;
        internal int GoodReads;
        internal int BadReads;
        internal int ProgressReads;
        internal int Deletes;
        internal int HoldRead;
        internal readonly TaskCompletionSource Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FirstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/oauth2/token")
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent("{\"access_token\":\"synthetic\",\"token_type\":\"bearer\",\"expires_in\":14400,\"account_id\":\"synthetic-account\"}") };
            if (request.RequestUri.AbsolutePath == "/2/users/get_current_account")
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent("{\"account_id\":\"synthetic-account\",\"root_info\":{\"root_namespace_id\":\"root\",\"home_namespace_id\":\"home\"}}") };
            if (request.RequestUri.AbsolutePath == "/2/files/delete_batch") Deletes++;
            Check.Require(request.RequestUri.AbsolutePath == "/2/files/delete_batch/check",
                "Startup receipts perform no root proof, listing or effect.");
            Reads++;
            if (Reads == HoldRead)
            {
                Held.TrySetResult();
                await Release.Task;
            }
            var body = await request.Content!.ReadAsStringAsync(ct);
            if (body.Contains("Bad", StringComparison.Ordinal)) { BadReads++; throw new InvalidOperationException(); }
            GoodReads++;
            FirstRead.TrySetResult();
            if (GoodReads <= ProgressReads)
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent("{\".tag\":\"in_progress\"}") };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("{\".tag\":\"complete\",\"entries\":[]}") };
        }
    }

    private sealed class FailingPairReadStore(IStateStore inner) : ForwardingStateStore(inner)
    {
        internal Exception? Error;
        public override (PairConfig Pair, long Generation)? ReadPairExecution(string pairId) =>
            Error is { } error ? throw error : base.ReadPairExecution(pairId);
    }

    private sealed class ReceiptReadStore(IStateStore inner, bool fail) : ForwardingStateStore(inner)
    {
        internal int Passes;
        internal int SelectCalls;
        internal int PollDecisions;
        internal int LocalRefusals;

        public override LiveRefusal ReadLiveRefusal(string pairId)
        {
            var refusal = base.ReadLiveRefusal(pairId);
            if (refusal == LiveRefusal.LocalWrites) Interlocked.Increment(ref LocalRefusals);
            return refusal;
        }

        public override bool HasFolderJobProgress(string pairId, CancellationToken ct)
        {
            var pending = base.HasFolderJobProgress(pairId, ct);
            Interlocked.Increment(ref PollDecisions);
            return pending;
        }
        /// <summary>How many unrelated selections one opportunity names before the store's own answer.</summary>
        internal int SelectBudget;
        /// <summary>The select call that completes <see cref="Selected"/>; 0 completes it on every call.</summary>
        internal int SelectedAt;
        internal readonly InvalidDataException Failure = new();
        internal TaskCompletionSource Selected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Selections = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override WorkSelection? SelectWork(string pairId, long expectedGeneration, PathScope scope)
        {
            SelectCalls++;
            if (SelectCalls >= SelectedAt) Selected.TrySetResult();
            // DESIGN-114 section 3.6: the budgeted selections refuse as two lanes, so a serial round's select ends the opportunity.
            if (SelectBudget != 0 && SelectCalls == SelectBudget + 2) Selections.TrySetResult();
            return SelectCalls <= SelectBudget
                ? new WorkSelection(pairId, expectedGeneration, 0, Side.Local, "local", null,
                    "unrelated" + SelectCalls.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt", 0, 0)
                : base.SelectWork(pairId, expectedGeneration, scope);
        }

        public override IEnumerable<FolderSubmission> PendingFolderSubmissions(string pairId, CancellationToken ct, bool progressOnly = false)
        {
            Passes++;
            Entered.TrySetResult();
            if (fail) throw Failure;
            return base.PendingFolderSubmissions(pairId, ct, progressOnly);
        }
    }

    private sealed class OwnedHandler : HttpMessageHandler
    {
        internal int Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("composition must not contact a provider");
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed++;
            base.Dispose(disposing);
        }
    }

    private static async Task<bool> FailsPrerequisiteAsync(EngineHost host, string pairId)
    {
        try { await host.RunPrerequisiteAsync(pairId, CancellationToken.None).ConfigureAwait(false); }
        catch (EngineRequestException) { return true; }
        return false;
    }

    private static Task<T> WithPrerequisiteListingAsync<T>(EngineHost host, string pairId,
        Func<ListingReadContext, CancellationToken, Task<T>> body) =>
        host.DispatchAsync(pairId,
            (registration, pair, generation, token) =>
                body(host.BeginListing(registration, AttemptOwner.Prerequisite), token), CancellationToken.None);

    private static int RetryNotices(IStateStore store, string pairId) =>
        store.Issues(pairId, [IssueKind.Failure]).Count(issue => issue.Cause == TextKey.RequiredRepairRetry);

    private static StateChange RetryNotice() => StateChange.Empty with
    {
        Raise = [(IssueKind.Failure, TextKey.RequiredRepairRetry, string.Empty, new IssueSubject([], null))]
    };

    private static PairConfig Pair(string id, bool enabled = true) =>
        new(id, id, @"C:\EngineHostFixture", "/EngineHostFixture", "connection-a", [], [],
            FirstRunPolicy.NewestWins, enabled, null);

    // The item-attempt authority keys its work by the saved root identities, so this fixture pair carries a binding.
    private static PairConfig Paired(string id) =>
        new(id, id, @"C:\EngineHostFixture", "/EngineHostFixture", "connection-a", [], [],
            FirstRunPolicy.NewestWins, true, null,
            new RootBinding(@"C:\EngineHostFixture", "/EngineHostFixture", "connection-a",
                new DirectoryIdentity(1, 2, null),
                new DropboxRootIdentity("account-a", "ns-root", "ns-home", "folder-a")));

    private static async Task QuietWorkingStatusHasNoEmptyNameAsync(string root)
    {
        using var instance = Lock(root, "quiet-working");
        var store = new InMemoryStateStore();
        store.SavePair(Pair("quiet-pair"));
        var clock = new FakeTimeProvider();
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = EngineHost.Compose(instance.StateAuthority.ProductRoot,
            message => published.TrySetResult(message), instance.StateAuthority, Settings, Http, clock,
            _ => null, _ => store, EngineHost.Mode.Persistent, startupMonitor: monitor);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = host.DispatchAsync("quiet-pair", async (registration, pair, generation, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return true;
        }, CancellationToken.None);
        try
        {
            await entered.Task.ConfigureAwait(false);
            monitor.Start(quietOnly: true);
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!clock.HasArmedTimer)
            {
                bound.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            clock.Advance(clock.NextDueIn!.Value);
            Check.Require(await published.Task.WaitAsync(bound.Token).ConfigureAwait(false) ==
                Text.Get(TextKey.StatusStillQuiet), "a quiet working host publishes the name-free sentence");
        }
        finally
        {
            release.TrySetResult();
            await running.ConfigureAwait(false);
        }
    }

    private static EngineHost Compose(PilotStateAuthority authority, IStateStore store) =>
        EngineHost.Compose(authority.ProductRoot, _ => { }, authority, Settings, Http, TimeProvider.System,
            _ => null, _ => store, EngineHost.Mode.Persistent);

    private static PilotInstanceLock Lock(string root, string name) =>
        PilotInstanceLock.Acquire(Path.Combine(root, name, "state", "instance.lock"));

    // Core's test hook redirects the product root to a temporary root; Engine.Tests is not on Core's
    // InternalsVisibleTo list.
    private static IDisposable OverrideProductRoot(string root) =>
        (IDisposable)typeof(SyncSharkRuntimePaths)
            .GetMethod("OverrideProductRootForTest", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { root })!;
}
