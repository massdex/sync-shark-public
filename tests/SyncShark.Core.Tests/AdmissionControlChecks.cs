using System.Runtime.Versioning;
using Massdex.SyncShark;
using HostRig = CloudPairHostBindingChecks.HostRig;

// SS-D01: the real public batch run, bootstrap scan/preparation and reviewed first sync reserve their
// exact cancellation target BEFORE their preparation/health/admission work. The run is therefore visible
// and stoppable while it is still preparing, a blocked audit writer never delays its own cancellation,
// a refused invocation publishes no control at all, and a cancelled pre-admission invocation leaves no
// accepted command, no provider effect and no held gate.
//
// Only real public entries are used here: no BeginRunForTest/reflection substitute decides any case, and
// the single synthetic provider wire is an input, never the decision. The bounded
// command-admission probe is the only delay applied, including concurrent control-command audits.
// No test delay depends on scanning the full retained database.
[SupportedOSPlatform("windows")]
internal static class AdmissionControlChecks
{
    private const string Payload = "SS-D01 admission control payload";
    private const string RunGate = "Another sync command is already active.";
    private const string UploadBoundary = "request-sent-before-upload";
    private const string FirstUpload = "payload.txt";
    private const string SecondUpload = "second-upload.txt";
    private const string ThirdUpload = "third-upload.txt";

    private static int _checks;

    internal static async Task<int> RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        CloudPairHostBindingChecks.UseFixtureRoot(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var token = timeout.Token;
        using var rig = await HostRig.CreateAsync("admission-control");
        var host = rig.Host;
        // Deterministic functional mode through the public setting only. Actual available-memory
        // admission remains enabled; no pressure seam or performance run is used.
        await host.SaveTransferSettingsAsync(host.LoadTransferSettings() with { ResourceMode = "Performance" }, token);
        var cloud = CloudPairHostBindingChecks.HostDropbox("ss-d01", "/SS D01 Admission");
        await rig.AddEndpointAsync(cloud);
        const string recoveryId = "id:ss-d01-recovery";
        rig.RegisterFolder(cloud, HostRig.RecoveryContainerPath, recoveryId);
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        await File.WriteAllTextAsync(Path.Combine(localRoot, FirstUpload), Payload, token);
        var bound = await host.BindPairAsync(Binding(localRoot, cloud, recoveryId, "SS D01 admission pair"), token);
        await host.RefreshBootstrapInventoryAsync(bound.Pair.Id, token);
        var pair = bound.Pair;
        Check(!pair.InitialReviewApproved.GetValueOrDefault() && pair.Enabled,
            "SS-D01 the real public binding and scan produce one enabled, not-yet-reviewed pair");

        // The reviewed first-sync entry is proved while this pair is still awaiting its review.
        await ReviewedFirstSyncEntryAsync(rig, pair, token);
        // Then the effect-free scan/preparation entry on the same pair.
        await ScanEntryAsync(rig, pair, token);

        pair = await host.ApproveBootstrapMergeAsync(pair.Id, token);
        Check(pair.InitialReviewApproved == true && pair.Enabled && !pair.FilesOnDemandEnabled,
            "SS-D01 first-sync approval completes after both stopped preparation entries released their gates");
        await BatchEntryAsync(rig, pair, token);
        await PauseDuringPreparationAsync(rig, pair, token);
        await StopDuringActiveWorkAsync(rig, pair, token);

        Console.WriteLine("{\"result\":\"PASS\",\"checks\":" + _checks +
            ",\"boundary\":\"real public host admission control; isolated authority; synthetic provider; no network\"}");
        return 0;
    }

    // ---- the real batch entry: reserve before admission, stop without waiting on the audit writer ----
    private static async Task BatchEntryAsync(HostRig rig, SyncPair pair, CancellationToken token)
    {
        var host = rig.Host;
        using var gate = new HealthGate();
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<SyncAuthorityBatchResult>? running = null;
        gate.Install();
        try
        {
            var writesBefore = rig.Wire.Writes.Total;
            var requestsBefore = rig.Wire.TotalRequests;
            var commandsBefore = await rig.CountAsync("SELECT count(*) FROM command_request;");
            running = Task.Run(() => host.RunPairsAsync([pair], "SS-D01 admission-control batch",
                cancellationToken: runCancellation.Token));
            await gate.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30), token);
            var active = host.ActiveRun!;
            Check(host.HasActiveRun && active is { Preparing: true } && active!.PairId == pair.Id &&
                  active.Kind == "run_pair" && active.Id.StartsWith("command-", StringComparison.Ordinal) &&
                  !running.IsCompleted,
                "SS-D01/1 the real batch entry publishes its one stoppable preparing run before admission completes");
            Check(rig.Wire.Writes.Total == writesBefore && rig.Wire.TotalRequests == requestsBefore &&
                  await rig.CountAsync("SELECT count(*) FROM command_request;") == commandsBefore &&
                  await rig.CountAsync("SELECT count(*) FROM command_request WHERE command_request_id='" +
                      active!.Id + "';") == 0 &&
                  await rig.CountAsync("SELECT count(*) FROM command_request WHERE status IN ('accepted','executing');") == 0,
                "SS-D01/2 a preparing run creates no provider request and no accepted or retained run command");
            // The exact stop must reach the reserved token while the durable audit is still blocked. The
            // run cannot end while the health boundary is held, so an unfinished stop is decisive here.
            var stop = Task.Run(() => host.StopActiveRunAsync(active!.Id, TimeSpan.FromSeconds(60), token));
            try
            {
                await WaitUntilAsync(() => host.ActiveRun is { StopRequested: true } stopping && stopping.Id == active!.Id,
                    TimeSpan.FromSeconds(30), token);
            }
            catch (Exception waitError)
            {
                throw new InvalidOperationException(
                    "Admission control: the expected state was not reached in time. gateEntries=" + gate.Entries + " gateWaitTimeouts=" + gate.GateWaitTimeouts +
                    " stopStatus=" + stop.Status + " active=" +
                    (host.ActiveRun is { } current
                        ? current.Id + " preparing=" + current.Preparing + " stopRequested=" + current.StopRequested
                        : "none") + " | " + waitError.Message, waitError);
            }
            Check(!stop.IsCompleted && host.ActiveRun is { StopRequested: true } stoppedActive &&
                  stoppedActive.Id == active.Id,
                "SS-D01/4 the captured stop reaches the reserved run token without waiting for the blocked audit writer");
            gate.Release();
            var cancelled = false;
            try { await running; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !host.HasActiveRun && host.ActiveRun is null,
                "SS-D01/5 a pre-admission cancellation ends as cancellation and releases the reserved gate exactly once");
            Check(await stop && rig.Wire.Writes.Total == writesBefore &&
                  await rig.CountAsync("SELECT count(*) FROM command_request WHERE command_request_id='" +
                      active.Id + "';") == 0 &&
                  await rig.CountAsync("SELECT count(*) FROM command_request WHERE status IN ('accepted','executing');") == 0,
                "SS-D01/6 the cancelled preparing run leaves no accepted command and no provider effect");
        }
        finally
        {
            gate.Release();
            runCancellation.Cancel();
            if (running is not null) try { await running; } catch (Exception) { }
        }
        // The released gate must admit the next real invocation, which applies the one approved upload.
        var next = await host.RunPairsAsync([pair], "SS-D01 post-cancellation batch", cancellationToken: token);
        Check(!next.Stopped && next.Items.Count == 1 && next.Items[0].FailureType is null &&
              next.Items[0].Result is { Uploaded: 1, Blocked: 0, Conflicts: 0, NeedsVerification: 0 } &&
              !host.HasActiveRun && host.ActiveRun is null,
            "SS-D01/7 the next real run acquires the released gate and completes exactly one approved upload");
    }

    // ---- the real effect-free scan/preparation entry ------------------------------------------------
    private static async Task ScanEntryAsync(HostRig rig, SyncPair pair, CancellationToken token)
    {
        var host = rig.Host;
        using var gate = new HealthGate();
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? scanning = null;
        gate.Install();
        try
        {
            var writesBefore = rig.Wire.Writes.Total;
            var commandsBefore = await rig.CountAsync("SELECT count(*) FROM command_request;");
            scanning = Task.Run(() => host.RefreshBootstrapInventoryAsync(pair.Id, runCancellation.Token));
            await gate.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30), token);
            try
            {
                await WaitUntilAsync(() => host.ActiveRun is { Preparing: true }, TimeSpan.FromSeconds(30), token);
            }
            catch (Exception waitError)
            {
                throw new InvalidOperationException(
                    "ScanEntry preparing state not reached. scanning=" + DescribeTask(scanning) +
                    " active=" + (host.ActiveRun is { } scanActive
                        ? scanActive.Kind + "/" + scanActive.Preparing + "/" + scanActive.StopRequested
                        : "none") + " gateEntries=" + gate.Entries + " gateWaitTimeouts=" + gate.GateWaitTimeouts +
                    " | " + waitError.Message, waitError);
            }
            var active = host.ActiveRun!;
            Check(active.Kind == "scan" && active.PairId == pair.Id && !scanning.IsCompleted &&
                  rig.Wire.Writes.Total == writesBefore,
                "SS-D01/8 the real scan entry publishes its preparing run before its own admission stage");
            var stop = Task.Run(() => host.StopActiveRunAsync(active.Id, TimeSpan.FromSeconds(60), token));
            // A scan waiting for admission can cancel and dispose its preparing control before the
            // observer samples StopRequested. The exact task's cancellation is equally decisive; a fault,
            // ordinary completion, different active run or cancellation of the external token is not.
            try
            {
                await WaitUntilAsync(() => host.ActiveRun is { StopRequested: true, Preparing: true } stopped &&
                        stopped.Id == active.Id || scanning.IsCanceled,
                    TimeSpan.FromSeconds(30), token);
            }
            catch (Exception waitError)
            {
                throw new InvalidOperationException("Scan stop did not reach its exact preparing task: scanning=" +
                    DescribeTask(scanning) + " stop=" + DescribeTask(stop) + " active=" +
                    (host.ActiveRun is { } current ? current.Id + "/" + current.Preparing + "/" +
                        current.StopRequested : "none") + " gateEntries=" + gate.Entries, waitError);
            }
            Check(!runCancellation.IsCancellationRequested && !stop.IsCompleted &&
                    (host.ActiveRun is { StopRequested: true, Preparing: true } stopping && stopping.Id == active.Id ||
                     scanning.IsCanceled && host.ActiveRun is null),
                "SS-D01/9 the exact scan receives Stop or completes cancellation while its stop audit is still held");
            gate.Release();
            var scanCancelled = false;
            try { await scanning; } catch (OperationCanceledException) { scanCancelled = true; }
            Check(scanCancelled && await stop && !host.HasActiveRun && host.ActiveRun is null,
                "SS-D01/10 the stopped scan reaches its own checkpoint and releases its reserved gate");
            Check(rig.Wire.Writes.Total == writesBefore &&
                  await rig.CountAsync("SELECT count(*) FROM command_request WHERE command_request_id='" +
                      active.Id + "';") == 0 &&
                  await rig.CountAsync("SELECT count(*) FROM command_request WHERE status IN ('accepted','executing');") == 0 &&
                  await rig.CountAsync("SELECT count(*) FROM command_request;") <= commandsBefore + 1,
                "SS-D01/11 the cancelled scan leaves no accepted command and no provider effect");
        }
        finally
        {
            gate.Release();
            runCancellation.Cancel();
            if (scanning is not null) try { await scanning; } catch (Exception) { }
        }
    }

    // ---- the real reviewed first-sync entry ---------------------------------------------------------
    private static async Task ReviewedFirstSyncEntryAsync(HostRig rig, SyncPair pair, CancellationToken token)
    {
        var host = rig.Host;
        var review = await host.PrepareBootstrapInventoryAsync(pair.Id, token);
        var choices = review.Items.Where(item => SyncBootstrapActions.IsActionable(item.Group))
            .Select(item => new SyncBootstrapActionChoice(item.RelativePath, item.Group,
                SyncBootstrapActions.TransferAction(item.Group)!, item.Move)).ToArray();
        Check(review.InventoryComplete && review.LocalScanGenerationId is not null &&
              review.DropboxScanGenerationId is not null && !review.HasMore && choices.Length >= 1,
            "SS-D01/12 the reviewed first-sync fixture has both exact reviewed snapshots and an actionable choice");
        var plan = new SyncBootstrapActionPlan(pair.Id, review.LocalScanGenerationId!,
            review.DropboxScanGenerationId!, choices);
        using var gate = new HealthGate();
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<SyncBootstrapActionResult>? applying = null;
        gate.Install();
        try
        {
            var writesBefore = rig.Wire.Writes.Total;
            var commandsBefore = await rig.CountAsync("SELECT count(*) FROM command_request;");
            applying = Task.Run(() => host.RunBootstrapActionPlanAsync(plan, cancellationToken: runCancellation.Token,
                expectedPolicyRevision: pair.PolicyRevision));
            await gate.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30), token);
            var active = host.ActiveRun!;
            Check(host.HasActiveRun && active is { Preparing: true } && active!.PairId == pair.Id &&
                  active.Kind == "run_pair" && !applying.IsCompleted,
                "SS-D01/13 the real reviewed first-sync entry publishes its preparing run before health and admission");
            var stop = Task.Run(() => host.StopActiveRunAsync(active!.Id, TimeSpan.FromSeconds(60), token));
            try
            {
                await WaitUntilAsync(() => host.ActiveRun is { StopRequested: true } stopping && stopping.Id == active!.Id,
                    TimeSpan.FromSeconds(30), token);
            }
            catch (Exception waitError)
            {
                throw new InvalidOperationException(
                    "Admission control: the expected state was not reached in time. gateEntries=" + gate.Entries + " gateWaitTimeouts=" + gate.GateWaitTimeouts +
                    " stopStatus=" + stop.Status + " active=" +
                    (host.ActiveRun is { } current
                        ? current.Id + " preparing=" + current.Preparing + " stopRequested=" + current.StopRequested
                        : "none") + " | " + waitError.Message, waitError);
            }
            Check(!stop.IsCompleted &&
                  await rig.CountAsync("SELECT count(*) FROM command_request WHERE command_request_id='" +
                      active.Id + "';") == 0,
                "SS-D01/14 the first-sync stop reaches its reserved token before that command is durable");
            gate.Release();
            try { await applying; } catch (Exception) { }
            Check(await stop && !host.HasActiveRun && host.ActiveRun is null && rig.Wire.Writes.Total == writesBefore,
                "SS-D01/15 the cancelled first sync releases its gate with no provider effect");
            Check(await rig.CountAsync("SELECT count(*) FROM command_request WHERE status IN ('accepted','executing');") == 0 &&
                  await rig.CountAsync("SELECT count(*) FROM command_request;") <= commandsBefore + 1,
                "SS-D01/16 the cancelled first sync leaves no accepted command and no retained run command");
        }
        finally
        {
            gate.Release();
            runCancellation.Cancel();
            if (applying is not null) try { await applying; } catch (Exception) { }
        }
    }

    // ---- pause during preparation holds the first effect until resume --------------------------------
    private static async Task PauseDuringPreparationAsync(HostRig rig, SyncPair pair, CancellationToken token)
    {
        var host = rig.Host;
        await File.WriteAllTextAsync(Path.Combine(pair.LocalRoot, SecondUpload), Payload, token);
        using var gate = new HealthGate();
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<SyncAuthorityBatchResult>? running = null;
        var effectReached = 0;
        SyncAuthorityCreateRemoteExecutor.PhaseForTest = phase =>
        {
            if (phase == UploadBoundary) Interlocked.Exchange(ref effectReached, 1);
        };
        gate.Install();
        try
        {
            var writesBefore = rig.Wire.Writes.Total;
            running = Task.Run(() => host.RunPairsAsync([pair], "SS-D01 pause during preparation",
                cancellationToken: runCancellation.Token));
            await gate.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30), token);
            var pausing = Task.Run(() => host.SetActiveRunPausedAsync(true, token));
            try
            {
                            await WaitUntilAsync(() => host.ActiveRun?.Paused == true, TimeSpan.FromSeconds(30), token);
            }
            catch (Exception waitError)
            {
                throw new InvalidOperationException(
                    "PauseDuringPreparation Paused expected state not reached in time. gateEntries=" + gate.Entries +
                    " gateWaitTimeouts=" + gate.GateWaitTimeouts + " paused=" + (host.ActiveRun is { } pausedRun ? pausedRun.Paused.ToString() : "none") +
                    " preparing=" + (host.ActiveRun is { } preparingRun ? preparingRun.Preparing.ToString() : "none") + " | " + waitError.Message, waitError);
            }
            Check(!pausing.IsCompleted,
                "SS-D01/17 pause during preparation applies to the reserved run before its own audit is unblocked");
            gate.Release();
            Check(await pausing == true && host.ActiveRun is { Paused: true } && !running.IsCompleted,
                "SS-D01/18 the same preparing run is paused while it is still preparing");
            try
            {
                            await WaitUntilAsync(() => host.ActiveRun is { Preparing: false }, TimeSpan.FromSeconds(60), token);
            }
            catch (Exception waitError)
            {
                throw new InvalidOperationException(
                    "PauseDuringPreparation PreparingFalse expected state not reached in time. gateEntries=" + gate.Entries +
                    " gateWaitTimeouts=" + gate.GateWaitTimeouts + " paused=" + (host.ActiveRun is { } pausedRun ? pausedRun.Paused.ToString() : "none") +
                    " preparing=" + (host.ActiveRun is { } preparingRun ? preparingRun.Preparing.ToString() : "none") + " | " + waitError.Message, waitError);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            Check(Volatile.Read(ref effectReached) == 0 && rig.Wire.Writes.Total == writesBefore &&
                  !running.IsCompleted && host.ActiveRun is { Paused: true },
                "SS-D01/19 the accepted paused run reaches no first effect and stays accepted while paused");
            Check(await host.SetActiveRunPausedAsync(false, token) == false,
                "SS-D01/20 resume is explicit and reported for the same accepted run");
            var completed = await running;
            Check(!completed.Stopped && completed.Items.Count == 1 &&
                  completed.Items[0].Result is { Uploaded: 1, Blocked: 0, Conflicts: 0, NeedsVerification: 0 } &&
                  Volatile.Read(ref effectReached) == 1,
                "SS-D01/21 resume lets the same run reach and complete its first approved effect");
        }
        finally
        {
            gate.Release();
            runCancellation.Cancel();
            SyncAuthorityCreateRemoteExecutor.PhaseForTest = null;
            if (running is not null) try { await running; } catch (Exception) { }
        }
    }

    // ---- stop during active work: checkpoint semantics and retained interrupted attempt --------------
    private static async Task StopDuringActiveWorkAsync(HostRig rig, SyncPair pair, CancellationToken token)
    {
        var host = rig.Host;
        await File.WriteAllTextAsync(Path.Combine(pair.LocalRoot, ThirdUpload), Payload, token);
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var boundary = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseEffect = new ManualResetEventSlim(false);
        Task<SyncAuthorityBatchResult>? running = null;
        SyncAuthorityCreateRemoteExecutor.PhaseForTest = phase =>
        {
            if (phase != UploadBoundary) return;
            boundary.TrySetResult(true);
            if (!releaseEffect.Wait(TimeSpan.FromSeconds(60)))
                throw new TimeoutException(
                    "EffectGate: the intentionally held pre-effect boundary was never released within its 60 second upper bound.");
        };
        try
        {
            var attemptsBefore = await rig.CountAsync("SELECT count(*) FROM operation_attempt;");
            var receiptsBefore = await rig.CountAsync("SELECT count(*) FROM effect_receipt;");
            running = Task.Run(() => host.RunPairsAsync([pair], "SS-D01 stop during active work",
                cancellationToken: runCancellation.Token));
            await boundary.Task.WaitAsync(TimeSpan.FromSeconds(90), token);
            var active = host.ActiveRun!;
            var attemptsAtBoundary = await rig.CountAsync("SELECT count(*) FROM operation_attempt;");
            Check(!active.Preparing && active.Kind == "run_pair" &&
                  await rig.CountAsync("SELECT count(*) FROM operation WHERE pair_id='" + pair.Id + "';") >= 1 &&
                  attemptsAtBoundary > attemptsBefore &&
                  await rig.CountAsync("SELECT count(*) FROM effect_receipt;") == receiptsBefore,
                "SS-D01/22 the accepted run reaches its pre-upload boundary with one newly retained fenced attempt and no effect receipt");
            // A wrong exact id refuses and must leave the accepted, running job completely untouched.
            var refusedWrongId = await host.StopActiveRunAsync("command-not-this-job", TimeSpan.FromSeconds(5), token);
            var afterWrongId = host.ActiveRun;
            Check(!refusedWrongId && afterWrongId is { StopRequested: false, Paused: false } untouched &&
                  untouched.Id == active.Id && !running.IsCompleted,
                "SS-D01/27 a wrong exact id refuses and leaves the accepted running job completely untouched");
            var stop = Task.Run(() => host.StopActiveRunAsync(active.Id, TimeSpan.FromSeconds(60), token));
            await WaitUntilAsync(() => host.ActiveRun?.StopRequested == true, TimeSpan.FromSeconds(20), token);
            Check(!stop.IsCompleted && host.ActiveRun is { StopRequested: true } stopped && stopped.Id == active.Id,
                "SS-D01/23 stop during active work signals the exact accepted run immediately");
            releaseEffect.Set();
            Check(await stop, "SS-D01/24 the captured stop completes only at that run's own checkpoint");
            // The interrupted effect may surface as cancellation or as a refusal; the decisive facts are
            // the reached checkpoint, the retained rows and the released gate asserted immediately below.
            try { await running; } catch (Exception) { }
            Check(!host.HasActiveRun && host.ActiveRun is null &&
                  await rig.CountAsync("SELECT count(*) FROM operation_attempt;") >= attemptsAtBoundary &&
                  await rig.CountAsync("SELECT count(*) FROM operation WHERE pair_id='" + pair.Id + "';") >= 1,
                "SS-D01/25 the stopped run releases its gate and retains its interrupted operation and attempt rows");
        }
        finally
        {
            releaseEffect.Set();
            SyncAuthorityCreateRemoteExecutor.PhaseForTest = null;
            runCancellation.Cancel();
            if (running is not null) try { await running; } catch (Exception) { }
        }
        // A following real invocation must never be refused by a gate leaked from the stopped run.
        string? followingFailure = null;
        try { await host.RunPairsAsync([pair], "SS-D01 post-stop gate probe", cancellationToken: token); }
        catch (Exception error) { followingFailure = error.Message; }
        Check(followingFailure != RunGate && !host.HasActiveRun && host.ActiveRun is null,
            "SS-D01/26 a following run is never refused by a gate leaked from the stopped accepted run");
    }

    // ---- shared fixture and delay helpers -----------------------------------------------------------
    private static SyncPairBindingRequest Binding(string localRoot,
        CloudPairHostBindingChecks.EndpointSpec cloud, string recoveryId, string name) =>
        new(null, name, localRoot,
            new DropboxFolderEntry(name, cloud.FolderPath, cloud.FolderId),
            new DropboxFolderEntry("Recovery", HostRig.RecoveryContainerPath, recoveryId),
            Enabled: true, Priority: 0, PropagateDeletions: false, FilesOnDemandEnabled: false,
            ConnectionId: cloud.ConnectionId);

    private static string DescribeTask(Task? task) => task is null
        ? "not-started"
        : task.IsCompletedSuccessfully
            ? "completed"
            : task.IsFaulted
                ? "faulted:" + task.Exception?.GetBaseException().GetType().Name + ":" + task.Exception?.GetBaseException().Message
                : task.IsCanceled
                    ? "cancelled"
                    : "running";

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10, token);
        if (!condition())
            throw new InvalidOperationException("Admission control: the expected state was not reached in time.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Admission control: " + message);
        _checks++;
        Console.WriteLine("PASS " + message);
    }

    /// <summary>
    /// A bounded probe at actual command admission. Concurrent control-command audits
    /// reach this same boundary without a full retained-data scan. Always removed after use.
    /// </summary>
    private sealed class HealthGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entries;

        internal Task FirstEntered => _entered.Task;

        internal int Entries => Volatile.Read(ref _entries);

        // G4 diagnostic: record explicitly whether the synchronous gate wait expired instead of being released.
        internal int GateWaitTimeouts => Volatile.Read(ref _gateWaitTimeouts);
        private int _gateWaitTimeouts;

        internal void Install()
        {
            SyncAuthorityDatabase.CommandAdmissionCheckedForTest = _ =>
            {
                if (Interlocked.Increment(ref _entries) == 1) _entered.TrySetResult(true);
                var released = _release.Wait(TimeSpan.FromSeconds(120));
                if (!released)
                {
                    Interlocked.Increment(ref _gateWaitTimeouts);
                    throw new TimeoutException(
                        "HealthGate: the intentionally held health boundary was never released within its 120 second upper bound; entries=" + Entries + " gateWaitTimeouts=" + _gateWaitTimeouts);
                }
            };
        }

        internal void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            SyncAuthorityDatabase.CommandAdmissionCheckedForTest = null;
            _release.Dispose();
        }
    }
}

