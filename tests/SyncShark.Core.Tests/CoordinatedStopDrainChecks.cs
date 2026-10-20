using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// Original M1: only provider input is controlled. Run-all admission, CLI exact-target
// cancellation, linked tokens, the effect pump, receipts and recovery are production.
internal static class CoordinatedStopDrainChecks
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(25);

    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("M1 public binding needs Windows directory identities.");
        await RunWindowsAsync(Path.Combine(root, "m1-public-stop-drain"));
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunWindowsAsync(string root)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(root);
        var failures = new List<string>();
        void Expect(bool value, string subcase, string detail)
        {
            Console.WriteLine((value ? "PASS " : "FAIL ") + "M1/" + subcase + " " + detail);
            if (!value) failures.Add(subcase + ": " + detail);
        }
        var previous = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            await SuccessControlAsync(root, Expect);
            await StopAsync(root, appliedBeforeStop: false, Expect);
            await StopAsync(root, appliedBeforeStop: true, Expect);
        }
        finally { SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = previous; }
        Console.WriteLine(JsonSerializer.Serialize(new { requirement = "M1", failures,
            entrypoints = new[] { "SyncAuthorityRuntimeHost.RunPairsAsync", "SyncCli.ExecuteAsync: cancel --key --yes",
                "SyncAuthorityRuntimeHost.RecoverPairsAsync" } }));
        if (failures.Count != 0)
            throw new InvalidOperationException("Original M1 public Stop/drain: " + string.Join("; ", failures));
    }

    [SupportedOSPlatform("windows")]
    private static async Task SuccessControlAsync(string root, Action<bool, string, string> expect)
    {
        using var wire = new CloudPairHostBindingChecks.HostWire();
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("success-control", wire);
        await ConfigureAsync(rig);
        var left = CloudPairHostBindingChecks.HostDropbox("m1-control-left", "/M1 Control Left");
        var right = CloudPairHostBindingChecks.HostDropbox("m1-control-right", "/M1 Control Right");
        var first = await PairAsync(rig, root, left, "control-left", 2);
        var second = await PairAsync(rig, root, right, "control-right", 1);
        await File.WriteAllTextAsync(Path.Combine(first.LocalRoot, "control-a.txt"), "ordinary first file");
        await File.WriteAllTextAsync(Path.Combine(second.LocalRoot, "control-b.txt"), "ordinary second file");
        using var bound = new CancellationTokenSource(Bound);
        var result = await rig.Host.RunPairsAsync([first, second], "Manual", cancellationToken: bound.Token);
        Print("ordinary no-Stop control", result);
        expect(!result.Stopped && result.Items.Count == 2 && result.Items.All(item =>
            item.FailureType is null && item.Result?.Decisions.Any(decision =>
                decision.Kind == SyncDecisionKind.Uploaded) == true), "success-control",
            "ordinary default run-all completes both independent files without Stop");
        expect(Encoding.UTF8.GetString((await rig.ReadDropboxFileAsync(left, "control-a.txt")).Bytes) == "ordinary first file" &&
            Encoding.UTF8.GetString((await rig.ReadDropboxFileAsync(right, "control-b.txt")).Bytes) == "ordinary second file",
            "success-control", "actual provider adapter readback matches both ordinary successful inputs");
    }

    [SupportedOSPlatform("windows")]
    private static async Task StopAsync(string root, bool appliedBeforeStop, Action<bool, string, string> expect)
    {
        var name = appliedBeforeStop ? "applied-lost-reply" : "cancel-before-effect";
        using var wire = new CloudPairHostBindingChecks.HostWire();
        using var gate = new StopTransport(wire, appliedBeforeStop);
        var completedEndpoint = CloudPairHostBindingChecks.HostDropbox("m1-done-" + name, "/M1 Done " + name);
        var activeEndpoint = CloudPairHostBindingChecks.HostDropbox("m1-active-" + name, "/M1 Active " + name);
        var queuedEndpoint = CloudPairHostBindingChecks.HostDropbox("m1-queued-" + name, "/M1 Queued " + name);
        SyncPair completed = null!, active = null!, queued = null!;
        string runId = "", completedOperation = "", activeOperation = "";
        CloudPairHostBindingChecks.AdapterRead? appliedRead = null;
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync(name, wire, gate))
        {
            await ConfigureAsync(rig);
            completed = await PairAsync(rig, root, completedEndpoint, name + "-done", 3);
            active = await PairAsync(rig, root, activeEndpoint, name + "-active", 2);
            queued = await PairAsync(rig, root, queuedEndpoint, name + "-queued", 1);
            await File.WriteAllTextAsync(Path.Combine(completed.LocalRoot, "completed.txt"), "retained independent success");
            await File.WriteAllTextAsync(Path.Combine(active.LocalRoot, "blocked.txt"), "exact admitted request bytes");
            await File.WriteAllTextAsync(Path.Combine(active.LocalRoot, "queued.txt"), "not admitted after stop");
            await File.WriteAllTextAsync(Path.Combine(queued.LocalRoot, "later-pair.txt"), "later pair not admitted");
            gate.Armed = true;
            using var bound = new CancellationTokenSource(Bound);
            var live = new ConcurrentQueue<SyncAuthorityBatchProgress>();
            var running = rig.Host.RunPairsAsync([completed, active, queued], "Manual",
                onProgress: progress => { live.Enqueue(progress); Print(name + " progress", progress); return Task.CompletedTask; },
                cancellationToken: bound.Token);
            Task<CliResult>? stopping = null;
            try
            {
                await gate.Entered.Task.WaitAsync(Bound);
                runId = rig.Host.ActiveRun?.Id ?? throw new InvalidOperationException("M1 has no public active run identity.");
                completedOperation = await OperationAsync(rig, completed.Id, "completed.txt");
                activeOperation = await OperationAsync(rig, active.Id, "blocked.txt");
                var committed = await rig.Db.LoadOperationAsync(completedOperation);
                expect(rig.Host.ActiveRun?.Kind == "run_all" && committed.Status == SyncAuthorityOperationStatus.Completed &&
                    committed.Attempts.Count == 1 && committed.Attempts[0].Status == SyncAuthorityAttemptStatus.VerifiedSuccess,
                    name, "real run-all has a completed independent file before one actual request is in flight");
                expect(await KnownSuccessAsync(rig, completedOperation) == 1 &&
                    Encoding.UTF8.GetString((await rig.ReadDropboxFileAsync(completedEndpoint, "completed.txt")).Bytes) ==
                    "retained independent success", name, "completed file has a real receipt and exact adapter readback");

                var wrong = await CancelAsync(rig, "command-not-this-run");
                Print(name + " wrong exact CLI target", wrong);
                expect(wrong.Exit != 0 && JsonDocument.Parse(wrong.Output).RootElement.GetProperty("ok").GetBoolean() == false &&
                    rig.Host.ActiveRun?.Id == runId && !gate.CancellationObserved.Task.IsCompleted && !running.IsCompleted,
                    name, "public CLI wrong-run rejection leaves the actual admitted transfer and exact run untouched");

                stopping = CancelAsync(rig, runId);
                await gate.CancellationObserved.Task.WaitAsync(Bound);
                var callsAtStop = gate.UploadCalls.ToArray();
                expect(gate.TokenCanBeCanceled && gate.TokenWasCanceled && rig.Host.HasActiveRun &&
                    rig.Host.ActiveRun?.Id == runId && !stopping.IsCompleted && !running.IsCompleted && !gate.Returned.Task.IsCompleted,
                    name, "exact public CLI Stop cancels the transport token but waits for the admitted worker to drain");
                expect(await rig.CountAsync("SELECT count(*) FROM command_request WHERE command_kind='stop' AND target_id=" +
                    Quote(runId) + " AND status='executing' AND terminal_utc IS NULL;") == 1 &&
                    await rig.ScalarAsync("SELECT status FROM command_request WHERE command_request_id=" + Quote(runId) + ";") == "executing",
                    name, "Stop and run durable commands remain nonterminal while the admitted worker is held");
                expect(!callsAtStop.Any(path => path.EndsWith("/queued.txt", StringComparison.Ordinal) ||
                    path.EndsWith("/later-pair.txt", StringComparison.Ordinal)) &&
                    await AttemptsAtPathAsync(rig, active.Id, "queued.txt") == 0 &&
                    await AttemptsAtPathAsync(rig, queued.Id, "later-pair.txt") == 0,
                    name, "same-pair queued file and later pair have zero admitted effect attempts after Stop");

                gate.ReleaseDrain.TrySetResult();
                var stopped = await stopping.WaitAsync(Bound);
                var result = await running.WaitAsync(Bound);
                Print(name + " exact CLI Stop", stopped);
                Print(name + " public run-all result", result);
                expect(stopped.Exit == 0 && JsonDocument.Parse(stopped.Output).RootElement.GetProperty("ok").GetBoolean() &&
                    gate.Returned.Task.IsCompleted && !rig.Host.HasActiveRun && rig.Host.ActiveRun is null && result.Stopped,
                    name, "CLI checkpoint completion follows actual drain and clears only the stopped public run");
                expect(result.Items.Single(item => item.Pair.Id == completed.Id).Result?.Decisions.Any(decision =>
                    decision.RelativePath == "completed.txt" && decision.Kind == SyncDecisionKind.Uploaded) == true,
                    name, "public stopped batch retains its completed independent file as Uploaded, not Stopped or unknown");
                expect(result.Items.Where(item => item.Pair.Id != completed.Id).All(item =>
                    item.Result?.Decisions.All(decision => decision.Kind != SyncDecisionKind.Uploaded) == true),
                    name, "unacknowledged and unadmitted files cannot become false public successes");
                expect(result.Items.Single(item => item.Pair.Id == active.Id).Result?.Decisions.Any(decision =>
                    decision.RelativePath == "blocked.txt" && decision.Kind == SyncDecisionKind.ResponseUnknown) == true,
                    name, "the transmitted unacknowledged file remains public ResponseUnknown, not a generic stopped-file outcome");
                expect(live.Any(progress => progress.Pair.Id == active.Id &&
                    progress.Action is { RelativePath: "blocked.txt", Phase: SyncActivityPhase.EffectUnknown } action &&
                    action.OperationId == activeOperation && action.Detail.Contains("not verified", StringComparison.Ordinal)),
                    name, "live operation output identifies the exact drained uncertain file and reason, not only a final run summary");
                expect(gate.UploadCalls.SequenceEqual(callsAtStop) &&
                    await AttemptsAtPathAsync(rig, active.Id, "queued.txt") == 0 &&
                    await AttemptsAtPathAsync(rig, queued.Id, "later-pair.txt") == 0,
                    name, "draining Stop starts no later file or pair and does not replay the completed file");
                expect(await rig.CountAsync("SELECT count(*) FROM command_request WHERE command_kind='stop' AND target_id=" +
                    Quote(runId) + " AND status='completed' AND json_extract(result_json,'$.checkpointReached')=1;") == 1,
                    name, "exact Stop completion has the real retained checkpointReached result");
                await AssertUncertainAsync(rig, activeOperation, name, expect);
                var entries = await rig.DropboxClient(activeEndpoint).ListEntriesAsync();
                expect(entries.Any(entry => entry.RelativePath == "blocked.txt") == appliedBeforeStop &&
                    entries.All(entry => entry.RelativePath != "queued.txt"), name,
                    "provider retained membership distinguishes applied-lost-reply from cancelled-before-effect without disappearance");
                if (appliedBeforeStop)
                    appliedRead = await rig.ReadDropboxFileAsync(activeEndpoint, "blocked.txt");
                expect(await KnownSuccessAsync(rig, completedOperation) == 1 &&
                    (await rig.Db.LoadOperationAsync(completedOperation)).Status == SyncAuthorityOperationStatus.Completed,
                    name, "Stop preserves the completed operation and its successful receipt");
                expect(await rig.CountAsync("SELECT count(*) FROM command_request c,json_each(c.result_json,'$.pairs') p" +
                    " WHERE c.command_request_id=" + Quote(runId) + " AND c.terminal_utc IS NOT NULL" +
                    " AND json_extract(c.result_json,'$.Stopped')=1 AND json_extract(p.value,'$.pairId')=" +
                    Quote(completed.Id) + " AND json_extract(p.value,'$.uploaded')=1;") == 1,
                    name, "retained stopped run summary truthfully preserves the independent completed-file success");
                expect(await File.ReadAllTextAsync(Path.Combine(active.LocalRoot, "blocked.txt")) == "exact admitted request bytes" &&
                    await File.ReadAllTextAsync(Path.Combine(active.LocalRoot, "queued.txt")) == "not admitted after stop" &&
                    await File.ReadAllTextAsync(Path.Combine(queued.LocalRoot, "later-pair.txt")) == "later pair not admitted",
                    name, "Stop preserves the exact local sources for uncertain and never-admitted work");
                Print(name + " stopped retained run", (await rig.Db.LoadRunHistoryAsync()).Single(item => item.Id == runId));
            }
            finally
            {
                // A failed assertion/timeout cannot abandon a fixture-owned admitted worker.
                gate.ReleaseDrain.TrySetResult();
                if (!running.IsCompleted) bound.Cancel();
                try { await running.WaitAsync(TimeSpan.FromSeconds(30)); }
                finally { if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(30)); }
            }
        }

        // Reopen the same protected state and same provider objects, not a new decision fake.
        using var reopened = await CloudPairHostBindingChecks.HostRig.CreateAsync(name, wire, gate);
        foreach (var endpoint in new[] { completedEndpoint, activeEndpoint, queuedEndpoint })
            await reopened.AddEndpointAsync(endpoint);
        expect(!reopened.Host.HasActiveRun && reopened.Host.ActiveRun is null &&
            (await reopened.Db.LoadRunHistoryAsync()).Any(item => item.Id == runId && item.CompletedUtc is not null),
            name, "reopen retains the stopped run terminal history without resurrecting its active identity");
        await AssertUncertainAsync(reopened, activeOperation, name + " reopen", expect);
        expect((await reopened.Db.LoadOperationAsync(completedOperation)).Status == SyncAuthorityOperationStatus.Completed &&
            (await reopened.Db.LoadOperationAsync(completedOperation)).Attempts.Count == 1,
            name, "reopen retains completed-file success and one original attempt");
        if (appliedBeforeStop)
        {
            expect(appliedRead is not null && Encoding.UTF8.GetString(appliedRead.Bytes) == "exact admitted request bytes",
                name, "lost-reply provider effect has exact bytes read through the actual adapter");
            gate.Armed = false;
            var requests = gate.UploadCalls.Count;
            // Remove only the two never-admitted fixture inputs, so this explicit recovery
            // checks the retained unknown request rather than authorising unrelated new work.
            File.Delete(Path.Combine(active.LocalRoot, "queued.txt"));
            File.Delete(Path.Combine(queued.LocalRoot, "later-pair.txt"));
            using var bound = new CancellationTokenSource(Bound);
            var recovery = await reopened.Host.RecoverPairsAsync([completed, active], cancellationToken: bound.Token);
            Print(name + " public reopened recovery", recovery);
            var reconciled = await reopened.Db.LoadOperationAsync(activeOperation);
            var read = await reopened.ReadDropboxFileAsync(activeEndpoint, "blocked.txt");
            expect(!recovery.Stopped && gate.UploadCalls.Count == requests &&
                reconciled.Status == SyncAuthorityOperationStatus.Completed && reconciled.Attempts.Count == 1 &&
                reconciled.Attempts[0].Status == SyncAuthorityAttemptStatus.VerifiedSuccess &&
                Encoding.UTF8.GetString(read.Bytes) == "exact admitted request bytes" &&
                read.Result == appliedRead!.Result, name,
                "public recovery verifies the existing exact successor without re-upload, duplicate attempt or identity change");
            var repeated = await reopened.Host.RecoverPairsAsync([completed, active], cancellationToken: bound.Token);
            Print(name + " repeated public recovery", repeated);
            expect(!repeated.Stopped && gate.UploadCalls.Count == requests &&
                (await reopened.Db.LoadOperationAsync(activeOperation)).Attempts.Count == 1 &&
                (await reopened.Db.LoadOperationAsync(completedOperation)).Attempts.Count == 1,
                name, "repeat/reopen does not blindly replay either applied-unknown or completed success");
        }
    }

    private static async Task ConfigureAsync(CloudPairHostBindingChecks.HostRig rig) =>
        await rig.Host.SaveTransferSettingsAsync(new SyncTransferSettings(MaximumConcurrentPairs: 1,
            ResourceMode: "Performance", MaximumConcurrentFiles: 1));

    [SupportedOSPlatform("windows")]
    private static async Task<SyncPair> PairAsync(CloudPairHostBindingChecks.HostRig rig, string root,
        CloudPairHostBindingChecks.EndpointSpec endpoint, string name, int priority)
    {
        await rig.AddEndpointAsync(endpoint);
        var local = Path.Combine(root, "local", name);
        Directory.CreateDirectory(local);
        var recoveryId = "id:m1-recovery-" + name;
        rig.RegisterFolder(endpoint, CloudPairHostBindingChecks.HostRig.RecoveryContainerPath, recoveryId);
        var pair = (await rig.Host.BindPairAsync(new SyncPairBindingRequest(null, "M1 " + name, local,
            new DropboxFolderEntry(name, endpoint.FolderPath, endpoint.FolderId),
            new DropboxFolderEntry("Recovery", CloudPairHostBindingChecks.HostRig.RecoveryContainerPath, recoveryId),
            Enabled: true, Priority: priority, PropagateDeletions: false, ConnectionId: endpoint.ConnectionId))).Pair;
        await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
        await rig.EnablePairAsync(await rig.Host.ApproveBootstrapMergeAsync(pair.Id));
        pair = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
        var baseline = await rig.Host.RunPairsAsync([pair], "Manual");
        if (baseline.Stopped || baseline.Items.Single().FailureType is not null)
            throw new InvalidOperationException("M1 empty public baseline failed for " + name);
        return pair;
    }

    private static Task<string> OperationAsync(CloudPairHostBindingChecks.HostRig rig, string pair, string path) =>
        rig.ScalarAsync("SELECT o.operation_id FROM operation o JOIN operation_member m ON m.operation_id=o.operation_id" +
            " WHERE o.kind='create_remote' AND o.pair_id=" + Quote(pair) + " AND m.role='source' AND m.expected_path_key=" +
            Quote(path) + " ORDER BY o.created_utc DESC,o.operation_id DESC LIMIT 1;");

    private static Task<long> AttemptsAtPathAsync(CloudPairHostBindingChecks.HostRig rig, string pair, string path) =>
        rig.CountAsync("SELECT count(DISTINCT a.attempt_id) FROM operation_attempt a JOIN operation o ON o.operation_id=a.operation_id" +
            " JOIN operation_member m ON m.operation_id=o.operation_id WHERE o.pair_id=" + Quote(pair) +
            " AND m.expected_path_key=" + Quote(path) + ";");

    private static Task<long> KnownSuccessAsync(CloudPairHostBindingChecks.HostRig rig, string operation) =>
        rig.CountAsync("SELECT count(*) FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id" +
            " WHERE a.operation_id=" + Quote(operation) + " AND r.certainty='known_success';");

    private static async Task AssertUncertainAsync(CloudPairHostBindingChecks.HostRig rig, string operation,
        string name, Action<bool, string, string> expect)
    {
        var retained = await rig.Db.LoadOperationAsync(operation);
        Print(name + " unacknowledged journal", retained);
        expect((retained.Status is SyncAuthorityOperationStatus.EffectIssuing or SyncAuthorityOperationStatus.EffectUnknown) &&
            retained.Attempts.Count == 1 && (retained.Attempts[0].Status is
                SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.EffectUnknown) &&
            await KnownSuccessAsync(rig, operation) == 0,
            name, "one dispatched but unacknowledged attempt remains uncertain, never Completed or verified success");
        expect(await rig.CountAsync("SELECT count(*) FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id" +
            " WHERE a.operation_id=" + Quote(operation) + " AND r.certainty='known_not_applied';") == 0,
            name, "Stop alone cannot invent verified no-effect evidence for a transmitted request");
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static void Print(string phase, object value) => Console.WriteLine(JsonSerializer.Serialize(new
        { requirement = "M1", phase, value }));
    private sealed record CliResult(int Exit, string Output, string Error);

    private static async Task<CliResult> CancelAsync(CloudPairHostBindingChecks.HostRig rig, string exactRun)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await SyncCli.ExecuteAsync(["--cli", "cancel", "--key", exactRun, "--yes", "--json"],
            output, error, () => new BorrowedCli(rig));
        return new(exit, output.ToString(), error.ToString());
    }

    // Borrowing only lifetime keeps cancellation dispatch in SyncCliRegistry and
    // SyncCliOperationsBase. The adapter does not override any decision or control.
    private sealed class BorrowedCli(CloudPairHostBindingChecks.HostRig rig) : SyncCliOperationsBase
    {
        protected override SyncAuthorityRuntimeHost Host => rig.Host;
        protected override PilotStateAuthority StateAuthority => rig.StateAuthority;
        public override string Mode => "controlled-public-host";
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StopTransport(CloudPairHostBindingChecks.HostWire wire, bool applyBeforeStop) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner = new(wire, disposeHandler: false);
        internal bool Armed { get; set; }
        internal bool TokenCanBeCanceled { get; private set; }
        internal bool TokenWasCanceled { get; private set; }
        internal ConcurrentQueue<string> UploadCalls { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDrain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri?.Host != "content.dropboxapi.com" || request.RequestUri.AbsolutePath != "/2/files/upload")
                return await _inner.SendAsync(request, token);
            using var argument = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            var path = argument.RootElement.GetProperty("path").GetString() ?? throw new InvalidDataException("M1 upload has no path.");
            UploadCalls.Enqueue(path);
            if (!Armed || !path.EndsWith("/blocked.txt", StringComparison.Ordinal))
                return await _inner.SendAsync(request, token);
            TokenCanBeCanceled = token.CanBeCanceled;
            if (applyBeforeStop)
            {
                // Real adapter request is consumed once by provider input before losing
                // its reply. No authority receipt/result is inserted by this transport.
                using var lostReply = await _inner.SendAsync(request, token);
                if (!lostReply.IsSuccessStatusCode) throw new InvalidDataException("M1 controlled upload was rejected.");
            }
            using var registration = token.Register(() =>
            {
                TokenWasCanceled = true;
                CancellationObserved.TrySetResult();
            });
            Entered.TrySetResult();
            try
            {
                await CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await ReleaseDrain.Task.WaitAsync(TimeSpan.FromSeconds(30));
                throw new OperationCanceledException("M1 controlled transmitted request ended after Stop drain release.", token);
            }
            finally { Returned.TrySetResult(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
