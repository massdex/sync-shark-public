using System.Globalization;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// AUD04 focused proofs: the shared renewable fenced operation-lease lifetime over the real isolated SQLite
// authority, the real reconciliation planner, the real local-create transaction/stage allocations and the real
// production SyncAuthorityCreateLocalExecutor driven through its real operation lifetime. The Dropbox side of
// the transfer is supplied by the existing in-process FakeSyncRemoteStore, which performs a real read of its
// own inventory and counts every download it was actually asked to perform, so "one effect" is a counted
// provider read and "one receipt" is the retained effect_receipt row.
//
// Nothing here reaches a live account, credential, provider network, installed database, product state, UI or
// pair root. The controlled lease duration/renewal interval are the registered policy seam; every renewal,
// refusal and takeover still runs through the real database fences.
internal static class LeaseRenewalChecks
{
    private const string InstallationId = "installation-lease-renewal-317";
    private const string ProfileId = "profile-lease-renewal-317";
    private const string SourceRevision = "lease-renewal-317";
    private const string Path = "reports/lease-renewal.txt";
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = System.IO.Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated lease renewal fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The lease renewal proofs require Windows NTFS identities and the protected control namespace.");

        await CaseAsync("A-executor-outlives-lease",
            "A real local-create executor under a short controlled lease whose transfer outlives the original duration completes exactly one effect and one receipt through repeated same-fence renewal",
            ExecutorOutlivesLeaseAsync);
        await CaseAsync("B-repeated-same-fence-renewal",
            "Repeated same-fence renewal keeps the same lease identity and token and extends the retained expiry each time",
            RepeatedRenewalAsync);
        await CaseAsync("C-renewal-refusals",
            "A foreign worker, a wrong fencing token, an expired, a stolen and a released lease all refuse renewal and are never revived",
            RefusalsAsync);
        await CaseAsync("D-cancellation-and-disposal",
            "Caller cancellation stops the operation safely and disposal stops renewal before releasing the exact lease",
            CancellationAndDisposalAsync);
        await CaseAsync("E-live-renewed-lease-not-takeable",
            "A second worker cannot acquire a lease that renewal has kept alive past its original expiry, and a real second executor issues no duplicate transfer",
            LiveRenewedLeaseAsync);
        await CaseAsync("F-forced-renewal-loss",
            "A forced renewal loss cancels the future effect, exposes the loss and retains the uncertain prior effect for reconciliation",
            ForcedRenewalLossAsync);
        await CaseAsync("G-abandoned-unissued-lease",
            "Abandoned pre-dispatch leases return to reviewed planning; active, allocated and issued work cannot be reset",
            AbandonedUnissuedLeaseAsync);

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "LeaseRenewalChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(System.IO.Path.Combine(_root, "lease-renewal-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS lease renewal: " + _assertions + " assertions; the shared fenced renewal lifetime" +
            " over the real isolated SQLite operation/lease/attempt/receipt journal and a real local-create" +
            " executor whose transfer outlives its original lease, with every expiry, takeover, refusal," +
            " cancellation, disposal and forced-loss case.");
    }

    private static async Task CaseAsync(string id, string label, Func<Task> body)
    {
        SyncAuthorityLeasePolicy.ResetToDefaults();
        var before = _assertions;
        try
        {
            await body();
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "PASS", ["assertions"] = _assertions - before
            });
            Console.WriteLine("PASS " + id + ": " + label + " (" + (_assertions - before) + " assertions)");
        }
        catch (Exception exception)
        {
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "FAIL", ["assertions"] = _assertions - before,
                ["error"] = exception.GetType().Name + ": " + exception.Message
            });
            Console.WriteLine("FAIL " + id + ": " + exception.GetType().Name + ": " + exception.Message);
            throw;
        }
        finally
        {
            SyncAuthorityLeasePolicy.ResetToDefaults();
            SqliteConnection.ClearAllPools();
        }
    }

    private static void Assert(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidDataException("lease-renewal assertion failed: " + label);
    }

    // ----- A: a real executor outliving its original lease -----------------------------------------

    private static async Task ExecutorOutlivesLeaseAsync()
    {
        SyncAuthorityLeasePolicy.LeaseDuration = TimeSpan.FromSeconds(2);
        SyncAuthorityLeasePolicy.RenewalInterval = TimeSpan.FromMilliseconds(250);
        await using var rig = await Rig.CreateAsync("A-executor-outlives-lease", "a");

        string? firstLease = null, firstExpires = null, firstPointer = null, acquired = null;
        string? secondLease = null, secondExpires = null, secondPointer = null;
        rig.Remote.BeforeDownloadForTest = async cancellationToken =>
        {
            await Task.Delay(1200, cancellationToken);
            (firstLease, firstExpires, firstPointer, acquired) = rig.LeaseProbe();
            await Task.Delay(3800, cancellationToken);
            (secondLease, secondExpires, secondPointer, _) = rig.LeaseProbe();
        };

        var downloadsBefore = rig.Remote.DownloadCount;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await new SyncAuthorityCreateLocalExecutor(rig.BoundPair, rig.Remote, rig.Authority)
            .ExecuteAsync(rig.OperationId, rig.Worker, CancellationToken.None);
        watch.Stop();

        Assert(result.Status == SyncAuthorityOperationStatus.Completed,
            "the real executor completed the operation (observed " + result.Status + ")");
        Assert(watch.Elapsed > SyncAuthorityLeasePolicy.LeaseDuration,
            "the transfer really outlived the original lease duration (elapsed " +
            watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s > " +
            SyncAuthorityLeasePolicy.LeaseDuration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s)");
        Assert(rig.Remote.DownloadCount - downloadsBefore == 1,
            "exactly one provider download effect was issued (" + (rig.Remote.DownloadCount - downloadsBefore) + ")");
        var snapshot = await rig.Authority.LoadOperationAsync(rig.OperationId);
        Assert(snapshot.Status == SyncAuthorityOperationStatus.Completed, "the operation reached completed");
        Assert(snapshot.Attempts.Count == 1 &&
               snapshot.Attempts[0].Status == SyncAuthorityAttemptStatus.VerifiedSuccess,
            "exactly one attempt reached verified success");
        var receipt = await rig.Authority.LoadLatestEffectReceiptAsync(rig.OperationId,
            snapshot.Attempts[0].AttemptId, CancellationToken.None);
        Assert(receipt is not null && receipt.Certainty == "known_success",
            "exactly one known-success effect receipt was retained after the original lease expiry");
        Assert(rig.TargetText() == rig.PayloadText, "the local target holds the exact transferred bytes");

        Assert(firstLease is not null && secondLease == firstLease && firstPointer == firstLease &&
               secondPointer == firstLease,
            "both live probes saw the SAME lease identity and fence as the operation's current lease (no takeover)");
        Assert(ParseUtc(secondExpires) > ParseUtc(firstExpires),
            "renewal ran repeatedly: the retained expiry advanced between the two live probes");
        Assert(ParseUtc(secondExpires) - ParseUtc(acquired) >
               SyncAuthorityLeasePolicy.LeaseDuration + TimeSpan.FromSeconds(1),
            "the lease was extended well beyond its original acquired duration");
    }

    // ----- B: repeated same-fence renewal ----------------------------------------------------------

    private static async Task RepeatedRenewalAsync()
    {
        SyncAuthorityLeasePolicy.LeaseDuration = TimeSpan.FromSeconds(3);
        SyncAuthorityLeasePolicy.RenewalInterval = TimeSpan.FromMilliseconds(200);
        await using var rig = await Rig.CreateAsync("B-repeated-same-fence-renewal", "b");

        await using var renewal = await rig.Authority.BeginFencedOperationLeaseRenewalAsync(rig.OperationId,
            rig.Worker, SyncAuthorityLeasePolicy.LeaseDuration, CancellationToken.None);
        var lease = renewal.Lease;
        var previous = lease.ExpiresUtc;
        for (var index = 0; index < 3; index++)
        {
            var renewed = await rig.Authority.TryRenewOperationLeaseAsync(rig.OperationId, rig.Worker, lease);
            Assert(renewed is not null, "same-fence renewal attempt " + (index + 1) + " succeeded");
            Assert(renewed!.LeaseId == lease.LeaseId && renewed.FencingToken == lease.FencingToken &&
                   renewed.WorkerId == lease.WorkerId && renewed.OperationId == lease.OperationId,
                "renewal " + (index + 1) + " retained the exact lease identity, operation, worker and fencing token");
            Assert(renewed.ExpiresUtc > previous, "renewal " + (index + 1) + " extended the retained expiry");
            previous = renewed.ExpiresUtc;
            await Task.Delay(60);
        }
        var row = rig.LeaseProbe();
        Assert(row.LeaseId == lease.LeaseId && row.Pointer == lease.LeaseId,
            "the retained lease row is still the same lease and the operation's current fence");
        Assert(ParseUtc(row.Expires) >= previous, "the retained row carries the latest extended expiry");
        await renewal.ReleaseAsync();
        Assert(rig.LeaseStatus() == "released" && rig.CurrentLeaseId() is null,
            "the renewed lease released cleanly afterwards");
    }

    // ----- C: refusals -----------------------------------------------------------------------------

    private static async Task RefusalsAsync()
    {
        SyncAuthorityLeasePolicy.LeaseDuration = TimeSpan.FromSeconds(3);
        SyncAuthorityLeasePolicy.RenewalInterval = TimeSpan.FromMilliseconds(200);
        await using var rig = await Rig.CreateAsync("C-renewal-refusals", "c");

        await using (var renewal = await rig.Authority.BeginFencedOperationLeaseRenewalAsync(rig.OperationId,
                   rig.Worker, SyncAuthorityLeasePolicy.LeaseDuration, CancellationToken.None))
        {
            var lease = renewal.Lease;
            var expiryBefore = rig.LeaseProbe().Expires;
            Assert(await rig.Authority.TryRenewOperationLeaseAsync(rig.OperationId, "another-worker", lease) is null,
                "a foreign worker identity cannot renew this lease");
            var foreignToken = lease with { FencingToken = lease.FencingToken + 100 };
            Assert(await rig.Authority.TryRenewOperationLeaseAsync(rig.OperationId, rig.Worker, foreignToken) is null,
                "a wrong fencing token cannot renew this lease");
            var foreignLeaseId = lease with { LeaseId = Guid.NewGuid().ToString("N") };
            Assert(await rig.Authority.TryRenewOperationLeaseAsync(rig.OperationId, rig.Worker, foreignLeaseId) is null,
                "a foreign lease identity cannot renew this operation");
            Assert(rig.LeaseProbe().Expires == expiryBefore,
                "every refused renewal wrote nothing to the retained lease");

            rig.ForceExpire();
            Assert(await rig.Authority.TryRenewOperationLeaseAsync(rig.OperationId, rig.Worker, lease) is null,
                "an expired lease cannot renew");
            var expiredRow = rig.LeaseProbe();
            Assert(expiredRow.Expires == rig.ForcedExpiry && expiredRow.LeaseId == lease.LeaseId,
                "the expired lease row was left exactly as it was and never revived or extended");
        }

        var holder = await rig.Authority.AcquireOperationLeaseAsync(rig.OperationId, rig.Worker,
            SyncAuthorityLeasePolicy.LeaseDuration);
        rig.ForceExpire();
        var stolen = await rig.Authority.AcquireOperationLeaseAsync(rig.OperationId, "worker-takeover",
            SyncAuthorityLeasePolicy.LeaseDuration);
        Assert(stolen.FencingToken > holder.FencingToken, "the real takeover advanced the monotonic fence");
        Assert(await rig.Authority.TryRenewOperationLeaseAsync(rig.OperationId, rig.Worker, holder) is null,
            "the displaced holder cannot renew a stolen lease");
        Assert(rig.CurrentLeaseId() == stolen.LeaseId && rig.LeaseStatus() == "active",
            "the stolen lease remains the operation's current active lease");

        await rig.Authority.ReleaseLeaseAsync(stolen, CancellationToken.None);
        var fresh = await rig.Authority.AcquireOperationLeaseAsync(rig.OperationId, rig.Worker,
            SyncAuthorityLeasePolicy.LeaseDuration);
        await rig.Authority.ReleaseLeaseAsync(fresh, CancellationToken.None);
        Assert(await rig.Authority.TryRenewOperationLeaseAsync(rig.OperationId, rig.Worker, fresh) is null,
            "a released lease cannot renew");
        Assert(rig.CurrentLeaseId() is null, "the released lease left no current operation lease");
    }

    // ----- D: caller cancellation and disposal ------------------------------------------------------

    private static async Task CancellationAndDisposalAsync()
    {
        SyncAuthorityLeasePolicy.LeaseDuration = TimeSpan.FromSeconds(5);
        SyncAuthorityLeasePolicy.RenewalInterval = TimeSpan.FromMilliseconds(200);
        await using var rig = await Rig.CreateAsync("D-cancellation-and-disposal", "d");

        using (var caller = new CancellationTokenSource())
        {
            var renewal = await rig.Authority.BeginFencedOperationLeaseRenewalAsync(rig.OperationId, rig.Worker,
                SyncAuthorityLeasePolicy.LeaseDuration, caller.Token);
            await Task.Delay(500);
            Assert(renewal.RenewalCount >= 1, "renewal ran while the caller's token was live");
            caller.Cancel();
            Assert(renewal.WorkToken.IsCancellationRequested, "caller cancellation cancelled the operation token");
            Assert(!renewal.RenewalLost, "caller cancellation is a distinct condition from a lease loss");
            await renewal.ReleaseAsync();
            Assert(rig.LeaseStatus() == "released" && rig.CurrentLeaseId() is null,
                "the exact lease was released safely after caller cancellation");
        }
        var reacquired = await rig.Authority.AcquireOperationLeaseAsync(rig.OperationId, "worker-after-cancel",
            SyncAuthorityLeasePolicy.LeaseDuration);
        await rig.Authority.ReleaseLeaseAsync(reacquired, CancellationToken.None);
        Assert(reacquired.LeaseId.Length > 0, "a clean release let another worker acquire the operation afterwards");

        var disposable = await rig.Authority.BeginFencedOperationLeaseRenewalAsync(rig.OperationId, rig.Worker,
            SyncAuthorityLeasePolicy.LeaseDuration, CancellationToken.None);
        var expiryBefore = rig.LeaseProbe().Expires;
        await Task.Delay(600);
        Assert(disposable.RenewalCount >= 1, "disposal-bound renewal ran at least once");
        Assert(rig.LeaseProbe().Expires != expiryBefore, "the retained expiry advanced before disposal");
        var countAtDisposal = disposable.RenewalCount;
        await disposable.DisposeAsync();
        Assert(rig.LeaseStatus() == "released" && rig.CurrentLeaseId() is null,
            "disposal stopped renewal and released the exact lease");
        await Task.Delay(3 * (int)SyncAuthorityLeasePolicy.RenewalInterval.TotalMilliseconds);
        Assert(disposable.RenewalCount == countAtDisposal,
            "disposal awaited the loop: no renewal happened after disposal");
    }

    // ----- E: a live renewed lease cannot be taken over ---------------------------------------------

    private static async Task LiveRenewedLeaseAsync()
    {
        SyncAuthorityLeasePolicy.LeaseDuration = TimeSpan.FromSeconds(1);
        SyncAuthorityLeasePolicy.RenewalInterval = TimeSpan.FromMilliseconds(150);
        await using var rig = await Rig.CreateAsync("E-live-renewed-lease-not-takeable", "e");

        await using (var renewal = await rig.Authority.BeginFencedOperationLeaseRenewalAsync(rig.OperationId,
                   rig.Worker, SyncAuthorityLeasePolicy.LeaseDuration, CancellationToken.None))
        {
            await Task.Delay(1600);
            var row = rig.LeaseProbe();
            Assert(row.LeaseId == renewal.Lease.LeaseId && rig.LeaseStatus() == "active",
                "renewal kept the lease active well past its original one-second expiry");
            Assert(ParseUtc(row.Expires) > DateTimeOffset.UtcNow,
                "the live renewed lease is unexpired against the real database clock");
            var refusal = await ExpectThrowsAsync(
                () => rig.Authority.AcquireOperationLeaseAsync(rig.OperationId, "worker-second",
                    SyncAuthorityLeasePolicy.LeaseDuration), "a second worker acquiring a live renewed lease");
            Assert(refusal is InvalidOperationException &&
                   refusal.Message.Contains("already has an active lease", StringComparison.Ordinal),
                "the second worker was refused because the renewed lease is still live: " + refusal.Message);
            Assert(rig.LeaseProbe().LeaseId == renewal.Lease.LeaseId,
                "the refused second worker did not displace the renewed lease");
        }
        var afterRelease = await rig.Authority.AcquireOperationLeaseAsync(rig.OperationId, "worker-second",
            SyncAuthorityLeasePolicy.LeaseDuration);
        await rig.Authority.ReleaseLeaseAsync(afterRelease, CancellationToken.None);
        Assert(afterRelease.LeaseId.Length > 0,
            "the operation became leasable again only after the live lease released");

        SyncAuthorityLeasePolicy.LeaseDuration = TimeSpan.FromSeconds(2);
        SyncAuthorityLeasePolicy.RenewalInterval = TimeSpan.FromMilliseconds(250);
        await using var duplicate = await Rig.CreateAsync("E-duplicate-transfer-guard", "e2");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        duplicate.Remote.BeforeDownloadForTest = async cancellationToken =>
        {
            started.TrySetResult();
            await Task.Delay(3000, cancellationToken);
        };
        var downloadsBefore = duplicate.Remote.DownloadCount;
        var running = new SyncAuthorityCreateLocalExecutor(duplicate.BoundPair, duplicate.Remote,
            duplicate.Authority).ExecuteAsync(duplicate.OperationId, "worker-first", CancellationToken.None);
        await started.Task;
        Exception? refused = null;
        try
        {
            await new SyncAuthorityCreateLocalExecutor(duplicate.BoundPair, duplicate.Remote, duplicate.Authority)
                .ExecuteAsync(duplicate.OperationId, "worker-second", CancellationToken.None);
        }
        catch (Exception error) { refused = error; }
        Assert(refused is not null, "the concurrent second executor refused to run the same operation");
        var first = await running;
        Assert(first.Status == SyncAuthorityOperationStatus.Completed, "the first executor still completed");
        Assert(duplicate.Remote.DownloadCount - downloadsBefore == 1,
            "the refused second executor issued no duplicate transfer");
        var snapshot = await duplicate.Authority.LoadOperationAsync(duplicate.OperationId);
        Assert(snapshot.Attempts.Count == 1 &&
               snapshot.Attempts[0].Status == SyncAuthorityAttemptStatus.VerifiedSuccess,
            "the operation retained exactly one verified attempt");
    }

    // ----- F: forced renewal loss ------------------------------------------------------------------

    private static async Task ForcedRenewalLossAsync()
    {
        SyncAuthorityLeasePolicy.LeaseDuration = TimeSpan.FromSeconds(2);
        SyncAuthorityLeasePolicy.RenewalInterval = TimeSpan.FromMilliseconds(200);
        await using var rig = await Rig.CreateAsync("F-forced-renewal-loss", "f");

        var renewal = await rig.Authority.BeginFencedOperationLeaseRenewalAsync(rig.OperationId, rig.Worker,
            SyncAuthorityLeasePolicy.LeaseDuration, CancellationToken.None);
        await Task.Delay(300);
        Assert(renewal.RenewalCount >= 1, "renewal was live before the forced loss");
        rig.ForceExpire();
        var takeover = await rig.Authority.AcquireOperationLeaseAsync(rig.OperationId, "worker-takeover",
            SyncAuthorityLeasePolicy.LeaseDuration);
        Assert(takeover.FencingToken > renewal.Lease.FencingToken, "the real takeover advanced the fence");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!renewal.RenewalLost && DateTimeOffset.UtcNow < deadline) await Task.Delay(40);
        Assert(renewal.RenewalLost, "the forced loss was detected rather than silently ignored");
        Assert(renewal.RenewalFailure is SyncAuthorityLeaseLostException,
            "the loss is exposed as the explicit lease-loss condition (observed " +
            (renewal.RenewalFailure?.GetType().Name ?? "none") + ")");
        Assert(renewal.WorkToken.IsCancellationRequested, "the loss cancelled the operation's future calls");
        Assert(await rig.Authority.TryRenewOperationLeaseAsync(rig.OperationId, rig.Worker, renewal.Lease) is null,
            "the lost lease is refused renewal afterwards");
        var countAtLoss = renewal.RenewalCount;
        await Task.Delay(700);
        Assert(renewal.RenewalCount == countAtLoss, "renewal never retried after the loss");
        await renewal.DisposeAsync();
        Assert(rig.CurrentLeaseId() == takeover.LeaseId && rig.LeaseStatus() == "active",
            "tearing down the lost lifetime never stole or released the taker's lease");

        SyncAuthorityLeasePolicy.LeaseDuration = TimeSpan.FromSeconds(2);
        SyncAuthorityLeasePolicy.RenewalInterval = TimeSpan.FromMilliseconds(250);
        await using var executorRig = await Rig.CreateAsync("F-executor-renewal-loss", "f2");
        var downloadsBefore = executorRig.Remote.DownloadCount;
        executorRig.Remote.BeforeDownloadForTest = async cancellationToken =>
        {
            await Task.Delay(700, cancellationToken);
            executorRig.ForceExpire();
            await executorRig.Authority.AcquireOperationLeaseAsync(executorRig.OperationId, "worker-takeover",
                SyncAuthorityLeasePolicy.LeaseDuration);
            await Task.Delay(1200, CancellationToken.None);
        };
        Exception? failure = null;
        try
        {
            await new SyncAuthorityCreateLocalExecutor(executorRig.BoundPair, executorRig.Remote,
                executorRig.Authority).ExecuteAsync(executorRig.OperationId, executorRig.Worker,
                CancellationToken.None);
        }
        catch (Exception error) { failure = error; }

        Assert(failure is not null, "the real executor failed once its lease was lost");
        Assert(executorRig.Remote.DownloadCount - downloadsBefore == 1,
            "exactly one provider read was issued and no duplicate transfer was attempted");
        var snapshot = await executorRig.Authority.LoadOperationAsync(executorRig.OperationId);
        Assert(snapshot.Status != SyncAuthorityOperationStatus.Completed,
            "the operation did not report success after the loss (observed " + snapshot.Status + ")");
        Assert(snapshot.Status is SyncAuthorityOperationStatus.EffectIssuing or
                   SyncAuthorityOperationStatus.EffectUnknown,
            "the uncertain in-flight state is retained for reconciliation (observed " + snapshot.Status + ")");
        Assert(snapshot.Attempts.Count == 1 &&
               snapshot.Attempts[0].Status is SyncAuthorityAttemptStatus.RequestSent or
                   SyncAuthorityAttemptStatus.EffectUnknown,
            "the already-dispatched attempt is retained without a terminal reconciliation");
        var receipt = await executorRig.Authority.LoadLatestEffectReceiptAsync(executorRig.OperationId,
            snapshot.Attempts[0].AttemptId, CancellationToken.None);
        Assert(receipt is null, "no receipt was recorded for the uncertain effect");
        Assert(!File.Exists(System.IO.Path.Combine(executorRig.LocalRoot, Path)),
            "the cancelled future local effect never created its target");
        Assert(executorRig.LeaseStatus() == "active" && executorRig.CurrentLeaseId() is not null,
            "the taker's lease remains the operation's current lease");
    }

    internal static async Task RunReviewedReplanningGuardsAsync(string root)
    {
        var priorRoot = _root;
        var priorAssertions = _assertions;
        try
        {
            _root = System.IO.Path.GetFullPath(root);
            if (Directory.Exists(_root))
                throw new InvalidOperationException("Use a fresh isolated reviewed-replanning guard fixture root.");
            Directory.CreateDirectory(_root);
            await AbandonedUnissuedLeaseAsync();
            await CrossPairReservationRetainedAsync();
            Console.WriteLine("PASS reviewed replanning guards: " + (_assertions - priorAssertions) +
                " actual admission/lease/allocation/attempt/history/reservation assertions");
        }
        finally { _root = priorRoot; _assertions = priorAssertions; }
    }

    private static async Task CrossPairReservationRetainedAsync()
    {
        await using var rig = await Rig.CreateAsync("G-cross-pair", "g-cross-pair");
        var original = await rig.Authority.LoadOperationAsync(rig.OperationId);
        await rig.Authority.EnsureReviewedRunCanStartAsync(rig.BoundPair.Id, CancellationToken.None);
        var secondRoot = System.IO.Path.Combine(_root, "cross-pair-destination");
        Directory.CreateDirectory(secondRoot);
        using var secondLease = PilotDirectoryLease.Open(secondRoot);
        await rig.Authority.RegisterPairAsync("fixture-cross-pair-destination", 1);
        await rig.Authority.RegisterRootBindingAsync("fixture-cross-pair-destination-root",
            "fixture-cross-pair-destination", "local", 1,
            JsonSerializer.Serialize(new { secondLease.Identity.VolumeSerialNumber, secondLease.Identity.FileId }),
            secondRoot);
        var crossAdmission = await rig.Authority.CreateAcceptedCommandAdmissionAsync(null, "run_all", null, null, null,
            JsonSerializer.Serialize(new { pairIds = new[] { rig.BoundPair.Id, "fixture-cross-pair-destination" } }),
            SyncAuthorityCommandPurpose.NormalEffect);
        await rig.Authority.TransitionCommandRequestAsync(crossAdmission.CommandRequestId, "executing");
        var crossRegistration = await rig.Authority.ReserveActionAsync("fixture-cross-pair-key", crossAdmission);
        await rig.Authority.RegisterRootBindingAsync("fixture-cross-pair-destination-remote-root",
            "fixture-cross-pair-destination", "dropbox", 1,
            "{\"folderId\":\"fixture-cross-pair-destination-remote\"}", "/cross-pair-empty");
        var localCut = await rig.Authority.BeginScanGenerationAsync("fixture-cross-pair-destination",
            "fixture-cross-pair-destination-root", "local", "manual", "{}");
        await rig.Authority.CompleteScanGenerationAsync(localCut);
        var remoteCut = await rig.Authority.BeginScanGenerationAsync("fixture-cross-pair-destination",
            "fixture-cross-pair-destination-remote-root", "dropbox", "manual", "{}");
        await rig.Authority.CompleteScanGenerationAsync(remoteCut);
        var destinationSet = await rig.Authority.LoadPlanningObservationSetAsync("fixture-cross-pair-destination",
            1, "fixture-cross-pair-destination-root", "fixture-cross-pair-destination-remote-root",
            localCut.ScanGenerationId, remoteCut.ScanGenerationId);
        var sourceAuthority = original.PlanningAuthorities.Single(x => x.PairId == rig.BoundPair.Id);
        var observationSetId = "fixture-cross-pair-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|",
                sourceAuthority.LocalScanGenerationId, sourceAuthority.DropboxScanGenerationId,
                localCut.ScanGenerationId, remoteCut.ScanGenerationId)))).ToLowerInvariant();
        var source = original.Members.Single(item => item.Role == "source") with
            { MemberId = crossRegistration.OperationId + "-source" };
        var destination = new SyncAuthorityMember(crossRegistration.OperationId + "-destination",
            "fixture-cross-pair-destination", original.ObjectId!, "destination",
            "fixture-cross-pair-destination-root", null, null, null, Path, "{\"present\":false}");
        // The remote source is shared read authority, not a reservation of this
        // pair's local destination. Bind the existing lock role to the exact
        // local root/path we are testing, just as the cross-pair producer does.
        var localLock = original.Members.Single(item => item.Role == "destination") with
            { MemberId = crossRegistration.OperationId + "-local-lock", Role = "lock" };
        await rig.Authority.CreateOperationAsync(new(crossRegistration.OperationId, "fixture-cross-pair-key",
            SyncAuthorityOperationKind.CrossPairMove, rig.BoundPair.Id, original.ObjectId,
            1, ProfileId, JsonSerializer.Serialize(new { sourcePairId = rig.BoundPair.Id,
                destinationPairId = "fixture-cross-pair-destination", entryKind = "File" }),
            50, [source, destination, localLock], [], [],
            PlanningAuthorities: [sourceAuthority with { ObservationSetId = observationSetId },
                destinationSet.Bind(observationSetId)]), crossAdmission, reservation: crossRegistration.Reservation);
        await rig.Authority.EnsureReviewedRunCanStartAsync(rig.BoundPair.Id, CancellationToken.None);
        var group = "fixture-cross-pair-overlap-retry";
        await rig.Authority.RegisterOperationGroupAsync(group, "compound", "{}");
        var planned = await new SyncAuthorityReconciliationPlanner(rig.Authority,
            new SyncAuthorityPlannerOptions(1, ProfileId, rig.BoundPair.ControlNamespace!.NamespaceId,
                rig.BoundPair.ControlNamespace.NamespaceId), rig.Admission, operationGroupId: group).PlanAsync(rig.BoundPair.Id);
        var retained = await rig.Authority.LoadOperationAsync(crossRegistration.OperationId);
        Assert(retained.Status == SyncAuthorityOperationStatus.Ready,
            "reviewed admission never retires retained cross-pair authority");
        Assert(retained.Attempts.Count == 0 && rig.Remote.DownloadCount == 0,
            "a retained cross-pair reservation is not dispatched or replayed by fresh planning");
        Assert(planned.Any(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Blocked &&
                   item.Reason.Contains("nonterminal operation")),
            "the real planner refuses the exact overlapping local root/path cross-pair lock reservation");
    }

    private static async Task AbandonedUnissuedLeaseAsync()
    {
        foreach (var mode in new[] { "released", "expired" })
        {
            await using var rig = await Rig.CreateAsync("G-unissued-" + mode, "g-" + mode);
            var lease = await rig.Authority.AcquireOperationLeaseAsync(rig.OperationId, rig.Worker,
                TimeSpan.FromMinutes(5));
            await ExpectThrowsAsync(() => rig.Authority.EnsureReviewedRunCanStartAsync(rig.BoundPair.Id,
                CancellationToken.None), "review admission while another fenced holder is active");
            Assert(rig.CurrentLeaseId() == lease.LeaseId && rig.Remote.DownloadCount == 0,
                "admission never steals the live lease or dispatches a transfer");
            if (mode == "released") await rig.Authority.ReleaseLeaseAsync(lease);
            else rig.ForceExpire();
            var reopened = new SyncAuthorityDatabase(rig.DatabasePath);
            await reopened.EnsureReviewedRunCanStartAsync(rig.BoundPair.Id, CancellationToken.None);
            await reopened.EnsureReviewedRunCanStartAsync(rig.BoundPair.Id, CancellationToken.None);
            var restored = await reopened.LoadOperationAsync(rig.OperationId);
            Assert(restored.Status == SyncAuthorityOperationStatus.Superseded && restored.Attempts.Count == 0 &&
                   rig.CurrentLeaseId() is null && rig.Remote.DownloadCount == 0 &&
                   (await reopened.ListRecoveryOperationsForPairAsync(rig.BoundPair.Id)).Count == 0,
                mode + " lease recovery supersedes only an unissued plan, never startup transfer permission");
            Assert(Probe(rig.DatabasePath, "SELECT count(*) FROM history_event WHERE (operation_id=$id AND event_kind='unissued_lease_recovered') OR (event_kind='presentation' AND json_extract(detail_json,'$.OperationId')=$id AND json_extract(detail_json,'$.OperationSnapshot.EventKind')='unissued_lease_recovered');",
                ("$id", rig.OperationId)) == "1", "abandoned-lease recovery retains one idempotent history event");
            var recoveryLog = (await reopened.LoadActivityProjectionPageAsync(pairId: rig.BoundPair.Id,
                    query: new SyncActivityQuery(DetailedLog: true)))
                .Single(item => item.OperationId == rig.OperationId && item.Outcome == "Unissued plan discarded");
            Assert(recoveryLog.Phase == SyncActivityPhase.Superseded && recoveryLog.Sequence is > 0 &&
                   recoveryLog.Reason.Contains("No file was transferred or changed", StringComparison.Ordinal) &&
                   recoveryLog.Categories!.Contains(SyncActivityCategories.Sync),
                "abandoned-plan recovery is retained in the detailed log with its exact no-effect meaning");
            Assert(!(await reopened.LoadActivityProjectionPageAsync(pairId: rig.BoundPair.Id,
                    query: new SyncActivityQuery([SyncActivityCategories.Files])))
                .Any(item => item.EventId == recoveryLog.EventId),
                "unissued-plan maintenance never masquerades as recent file transfer activity");
            Assert(Probe(rig.DatabasePath, "SELECT count(*) FROM operation_transition WHERE from_status='leased' AND to_status='ready';") == "0",
                "recovery does not relax the sealed operation transition allowlist");
            await new SyncAuthorityObservationCoordinator(rig.BoundPair, rig.Remote, reopened,
                "root-lease-g-" + mode + "-local", "root-lease-g-" + mode + "-dropbox").ObserveAsync("manual");
            var fresh = (await new SyncAuthorityReconciliationPlanner(reopened,
                new SyncAuthorityPlannerOptions(1, ProfileId, rig.BoundPair.ControlNamespace!.NamespaceId,
                    rig.BoundPair.ControlNamespace.NamespaceId), rig.Admission).PlanAsync(rig.BoundPair.Id))
                .Single(item => item.RelativePath == Path && item.OperationId is not null).OperationId!;
            Assert(fresh != rig.OperationId, "a fresh observation/planner, not the abandoned plan, grants the next effect");
            var result = await new SyncAuthorityCreateLocalExecutor(rig.BoundPair, rig.Remote, reopened)
                .ExecuteAsync(fresh, rig.Worker);
            Assert(result.Status == SyncAuthorityOperationStatus.Completed && rig.Remote.DownloadCount == 1 &&
                   rig.TargetText() == rig.PayloadText, "fresh fenced execution after recovery completes exactly one verified download");
            await reopened.EnsureReviewedRunCanStartAsync(rig.BoundPair.Id, CancellationToken.None);
            Assert(rig.Remote.DownloadCount == 1 && (await reopened.LoadOperationAsync(fresh)).Attempts.Count == 1 &&
                   (await reopened.LoadOperationAsync(rig.OperationId)).Attempts.Count == 0,
                "subsequent admission preserves completed effect history without replay");
        }
        foreach (var mode in new[] { "allocation", "attempt" })
        {
            await using var rig = await Rig.CreateAsync("G-uncertain-" + mode, "g-" + mode);
            var lease = await rig.Authority.AcquireOperationLeaseAsync(rig.OperationId, rig.Worker,
                TimeSpan.FromMinutes(5));
            if (mode == "allocation")
            {
                var operation = await rig.Authority.LoadOperationAsync(rig.OperationId);
                await rig.Authority.TransitionAllocationAsync(operation.Allocations.Single(item =>
                        item.Kind == SyncAuthorityAllocationKind.TransactionDirectory).AllocationId,
                    lease, SyncAuthorityAllocationStatus.Creating);
            }
            else await rig.Authority.StartDispatchedAttemptAsync(rig.OperationId, lease, new string('b', 64), "download");
            await rig.Authority.ReleaseLeaseAsync(lease);
            var before = await rig.Authority.LoadOperationAsync(rig.OperationId);
            await ExpectThrowsAsync(() => rig.Authority.EnsureReviewedRunCanStartAsync(rig.BoundPair.Id,
                CancellationToken.None), mode + " uncertainty requires actual outcome verification");
            var after = await rig.Authority.LoadOperationAsync(rig.OperationId);
            Assert(before.Status == after.Status && before.Attempts.Count == after.Attempts.Count &&
                   before.Allocations.SequenceEqual(after.Allocations) && rig.Remote.DownloadCount == 0,
                "admission leaves " + mode + " evidence unchanged and issues no effect");
        }
    }

    // ----- shared helpers --------------------------------------------------------------------------

    private static async Task<Exception> ExpectThrowsAsync(Func<Task> body, string label)
    {
        try
        {
            await body();
        }
        catch (Exception error)
        {
            Assert(true, label + " was refused: " + error.GetType().Name);
            return error;
        }
        Assert(false, label + " must be refused and was not");
        throw new InvalidOperationException("unreachable");
    }

    private static DateTimeOffset ParseUtc(string? value)
    {
        Assert(value is not null, "an expected lease timestamp was absent");
        return DateTimeOffset.Parse(value!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal string CaseRoot = "";
        internal string LocalRoot = "";
        internal string DatabasePath = "";
        internal string OperationId = "";
        internal string Worker = "";
        internal string PayloadText = "";
        internal string ForcedExpiry = "";
        internal SyncAuthorityDatabase Authority = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal SyncPair BoundPair = null!;
        internal FakeSyncRemoteStore Remote = null!;

        internal static async Task<Rig> CreateAsync(string caseName, string slug)
        {
            var rig = new Rig
            {
                CaseRoot = System.IO.Path.Combine(_root, caseName),
                LocalRoot = System.IO.Path.Combine(_root, caseName, "pair"),
                Worker = "worker-lease-" + slug,
                OperationId = "operation-lease-" + slug
            };
            Directory.CreateDirectory(rig.LocalRoot);
            // The control-namespace identifier rule requires a 16..64 character identifier.
            var controlNamespaceId = "control-lease-namespace-" + slug;
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The lease renewal fixture requires Windows.");
            PilotDirectoryIdentity identity;
            using (var root = PilotDirectoryLease.Open(rig.LocalRoot)) identity = root.Identity;
            var pair = new SyncPair("pair-lease-" + slug, "Lease " + slug, rig.LocalRoot,
                identity.VolumeSerialNumber, identity.FileId, "/Lease", "folder-lease-" + slug);
            SyncLocalControlNamespaceBinding localControl;
            using (var selectedRoot = PilotDirectoryLease.Open(rig.LocalRoot, identity))
                localControl = SyncControlNamespace.BindLocalForTest(selectedRoot, InstallationId,
                    controlNamespaceId);
            rig.BoundPair = pair with
            {
                ControlNamespace = new SyncControlNamespaceBinding(InstallationId, controlNamespaceId,
                    localControl),
                RecoveryRoot = new SyncRemoteRecoveryRootBinding(
                    SyncDropboxArtifactPaths.RecoveryContainerPath, "recovery-folder", "root-namespace-a")
            };
            rig.DatabasePath = System.IO.Path.Combine(rig.CaseRoot, "syncshark.db");
            rig.Authority = new SyncAuthorityDatabase(rig.DatabasePath);
            await rig.Authority.InitializeAsync(InstallationId, ProfileId, SourceRevision, "{}");
            await rig.Authority.RegisterPairAsync(pair.Id, 1);
            await rig.Authority.RegisterRootBindingAsync("root-lease-" + slug + "-local", pair.Id, "local", 1,
                JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), rig.LocalRoot);
            await rig.Authority.RegisterRootBindingAsync("root-lease-" + slug + "-dropbox", pair.Id, "dropbox", 1,
                "{\"folderId\":\"folder-lease-" + slug + "\"}", pair.DropboxRoot);
            rig.Remote = new FakeSyncRemoteStore();
            rig.PayloadText = "lease renewal payload " + slug;
            rig.Remote.PutBytes(Path, Encoding.UTF8.GetBytes(rig.PayloadText));

            var coordinator = new SyncAuthorityObservationCoordinator(pair, rig.Remote, rig.Authority,
                "root-lease-" + slug + "-local", "root-lease-" + slug + "-dropbox");
            await coordinator.ObserveAsync("initial");
            await coordinator.ObserveAsync("initial");
            await new SyncAuthorityInitialPlanner(rig.Authority).AdoptAsync(pair.Id);
            rig.Admission = await rig.Authority.CreateAcceptedCommandAdmissionAsync(null, "run_pair", rig.BoundPair.Id,
                null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await rig.Authority.TransitionCommandRequestAsync(rig.Admission.CommandRequestId, "executing");
            var planner = new SyncAuthorityReconciliationPlanner(rig.Authority,
                new SyncAuthorityPlannerOptions(1, ProfileId, controlNamespaceId, controlNamespaceId), rig.Admission);
            var outcomes = await planner.PlanAsync(pair.Id);
            var planned = outcomes.SingleOrDefault(item =>
                item.Kind == SyncAuthorityPlannerOutcomeKind.Operation && item.OperationId is not null &&
                string.Equals(item.RelativePath, Path, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The real planner produced no local-create operation for " +
                    Path + " (outcomes: " + string.Join(", ",
                        outcomes.Select(item => item.Kind + ":" + item.RelativePath)) + ")");
            var operation = await rig.Authority.LoadOperationAsync(planned.OperationId!);
            if (operation.Kind != SyncAuthorityOperationKind.CreateLocal)
                throw new InvalidDataException("The planned operation is not a local create: " + operation.Kind);
            rig.OperationId = planned.OperationId!;
            return rig;
        }

        internal string TargetText() => File.ReadAllText(System.IO.Path.Combine(LocalRoot, Path));

        /// <summary>Reads the live retained operation lease row and the operation's current fence pointer.</summary>
        internal (string? LeaseId, string? Expires, string? Pointer, string? Acquired) LeaseProbe()
        {
            var lease = Probe(DatabasePath,
                "SELECT lease_id || char(31) || expires_utc || char(31) || acquired_utc FROM lease" +
                " WHERE scope_kind='operation' AND scope_id=$id AND status='active'" +
                " ORDER BY acquired_utc DESC LIMIT 1;", ("$id", OperationId));
            var parts = lease?.Split('\u001f');
            return (parts is null ? null : parts[0], parts is null ? null : parts[1], CurrentLeaseId(),
                parts is null ? null : parts[2]);
        }

        internal string? LeaseStatus() => Probe(DatabasePath,
            "SELECT status FROM lease WHERE scope_kind='operation' AND scope_id=$id" +
            " ORDER BY acquired_utc DESC LIMIT 1;", ("$id", OperationId));

        internal string? CurrentLeaseId() => Probe(DatabasePath,
            "SELECT current_lease_id FROM operation WHERE operation_id=$id;", ("$id", OperationId));

        /// <summary>Moves the held lease's retained expiry into the past exactly as a genuinely expired lease
        /// looks, without deleting or rewriting its identity.</summary>
        internal void ForceExpire()
        {
            ForcedExpiry = "2000-01-01T00:00:00.0000000Z";
            ExecuteWrite(DatabasePath,
                "UPDATE lease SET expires_utc=$expires WHERE scope_kind='operation' AND scope_id=$id" +
                " AND status='active';", ("$expires", ForcedExpiry), ("$id", OperationId));
        }

        public async ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            await Task.CompletedTask;
        }
    }

    private static string? Probe(string databasePath, string sql, params (string Name, object? Value)[] values)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 30
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var scalar = command.ExecuteScalar();
        return scalar is null or DBNull ? null : Convert.ToString(scalar, CultureInfo.InvariantCulture);
    }

    private static void ExecuteWrite(string databasePath, string sql,
        params (string Name, object? Value)[] values)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 30
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
