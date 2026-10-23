using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    public static async Task GateChecksAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var store = SqliteStateStore.Open(root, Fast, TimeProvider.System);
        var pair = Pair("p1");
        store.SavePair(pair with { A = pair.A with { Provider = SyncProviders.Local } });
        var gate = (FairStoreGate)typeof(SqliteStateStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        var actions = Enumerable.Range(0, 768).Select(i => IntentFor("gate-" + i)).ToArray();
        store.RecordIntents("p1", actions);
        using var first = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var pages = 0;
        store.HeldBatchCommittedForTest = () =>
        {
            if (Interlocked.Increment(ref pages) != 1) return;
            first.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
        };
        var background = Task.Run(() => store.SetIntentsHeld("p1", actions.Select(a => a.ActionId).ToArray(), true));
        Require(first.Wait(TimeSpan.FromSeconds(15)), "first real SQLite held page committed");
        var foreground = Task.Run(() => store.CountHeldIntents("p1"));
        try
        {
            Require(SpinWait.SpinUntil(() => gate.Waiting == 1, TimeSpan.FromSeconds(15)), "foreground queued during first batch");
            release.Set();
            Require(await foreground.WaitAsync(TimeSpan.FromSeconds(15)) == 256,
                "foreground admitted after one background batch, before the next batch");
            await background.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { release.Set(); await background; store.HeldBatchCommittedForTest = null; }

        var consumedReview = store.WritePrepare(pair.PairId, writer => writer.Commit("{}", "review"),
            SqliteStateStore.RemovalKey(pair, "reviewed"));
        store.ConsumeRemovalReview(consumedReview.Generation);
        using var ownerReady = new ManualResetEventSlim();
        using var ownerRelease = new ManualResetEventSlim();
        var owner = Task.Run(() =>
        {
            using (gate.Enter())
            using (gate.Enter())
            { ownerReady.Set(); if (!ownerRelease.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(); }
        });
        Require(ownerReady.Wait(TimeSpan.FromSeconds(15)), "reentrant store gate holder entered");
        using var cancel = new CancellationTokenSource();
        var waiter = Task.Run(() =>
        {
            using var admission = FairStoreGate.WithCancellation(cancel.Token);
            try { store.CountHeldIntents("p1"); return false; }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return true; }
        });
        try
        {
            Require(SpinWait.SpinUntil(() => gate.Waiting == 1, TimeSpan.FromSeconds(15)), "cancellable SQLite caller queued");
            cancel.Cancel();
            Require(await waiter.WaitAsync(TimeSpan.FromSeconds(2)), "cancelled waiter returns while holder still owns gate");
            Require(gate.Waiting == 0, "cancelled ticket removed without stranding later callers");
            var cleanupTimedOut = false;
            using (FairStoreGate.WithCancellation(cancel.Token))
            {
                try { FairStoreGate.Cleanup(() => store.ClearRemovalReview(consumedReview.Generation), TimeSpan.FromMilliseconds(50)); }
                catch (TimeoutException) { cleanupTimedOut = true; }
            }
            Require(cleanupTimedOut && gate.Waiting == 0,
                "blocked cleanup surfaces timeout instead of cancellation or silent loss and removes its ticket");
            var fatal = new StateDamagedException("original state damage");
            Exception? retained = null;
            try
            {
                Exception? primary = null;
                try { throw fatal; }
                catch (Exception error) { primary = error; throw; }
                finally
                {
                    FairStoreGate.Cleanup(() => store.ClearRemovalReview(consumedReview.Generation),
                        TimeSpan.FromMilliseconds(50), primaryError: primary);
                }
            }
            catch (Exception error) { retained = error; }
            Require(ReferenceEquals(retained, fatal) && fatal.Data["StoreCleanupFailure"] is TimeoutException &&
                FailureRetryOwner.ScopeOf(retained!, default) == FailureRetryOwner.FailureScope.State && gate.Waiting == 0,
                "finally cleanup timeout retains original fatal exception and attaches secondary failure without downgrading state damage");
        }
        finally { ownerRelease.Set(); await owner; }
        Require(store.CountHeldIntents("p1") == 768, "queue remains usable after cancellation");
        Require(store.ReadRemovalReview(pair, "reviewed")?.Digest == "consumed",
            "cleanup timeout retains consumed digest and cannot revive removal consent");
        using (FairStoreGate.WithCancellation(cancel.Token))
        {
            FairStoreGate.Cleanup(() => store.ClearRemovalReview(consumedReview.Generation));
            var cleaned = false;
            FairStoreGate.Cleanup(() => cleaned = store.CountHeldIntents("p1") == 768);
            Require(cleaned, "cleanup enters real SQLite despite cancelled ambient run");
            Require(FairStoreGate.CurrentCancellation == cancel.Token,
                "cleanup restores the original cancelled admission scope");
        }

        Require(store.ReadRemovalReview(pair, "reviewed") is null,
            "subsequent cleanup retires consumed rows without replaying consent");

        var captureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var captureRelease = new ManualResetEventSlim();
        var insideGate = false;
        store.DefenderCaptureForTest = capturedItem =>
        {
            insideGate |= gate.IsHeldByCurrentThread;
            captureEntered.TrySetResult();
            if (!captureRelease.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
            return capturedItem with { Hash = new string('a', 64) };
        };
        var item = new ItemState("held.txt", ItemKind.File, "held-entity", null, null, 3, T0, ItemFlags.None);
        var notice = new Notice("p1", 30, NoticeKind.UserDecision, item.Path, "Antivirus hold", QuarantineFailure.NoticeKey,
            Quarantine: new(Side.A, null, item));
        var capturing = Task.Run(() => store.AddNotices([notice]));
        try
        {
            await captureEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Require(await Task.Run(() => store.CountHeldIntents("p1")).WaitAsync(TimeSpan.FromSeconds(2)) == 768,
                "foreground SQLite read proceeds during delayed optional hash capture");
            Require(!insideGate, "optional local hashing never owns store gate");
            // Unrelated background publication must not invalidate this entity's evidence.
            store.SetIntentHeld("p1", actions[0].ActionId, false);
        }
        finally { captureRelease.Set(); await capturing; store.DefenderCaptureForTest = null; }
        Require(store.OpenNotices("p1").Single(n => n.ReasonKey == QuarantineFailure.NoticeKey).Quarantine!.ExpectedTarget!.Hash == new string('a', 64),
            "unrelated store writes do not discard captured entity evidence");

        var changed = item with { Path = "changed.txt", ObjectId = "changed-entity" };
        var generation = store.BeginGeneration(pair.A.RootId, "full");
        store.WriteSnapshot(new(pair.A.RootId, Side.A, generation, "before", [changed], []));
        store.DefenderCaptureForTest = observed =>
        {
            store.WriteSnapshot(new(pair.A.RootId, Side.A, generation, "after", [changed with { Size = 4 }], []));
            return observed with { Hash = new string('c', 64) };
        };
        store.AddNotices([notice with { Path = changed.Path, Quarantine = new(Side.A, null, changed) }]);
        store.DefenderCaptureForTest = null;
        Require(store.OpenNotices("p1").Single(n => n.Path == changed.Path).Quarantine!.ExpectedTarget!.Hash is null,
            "changed held entity rejects stale optional hash but retains actionable notice");

        var unchanged = item with { Path = "unchanged.txt", ObjectId = "unchanged-entity" };
        store.DefenderCaptureForTest = observed => observed with { Hash = new string('b', 64) };
        store.AddNotices([notice with { Path = unchanged.Path, Quarantine = new(Side.A, null, unchanged) }]);
        store.DefenderCaptureForTest = null;
        Require(store.OpenNotices("p1").Single(n => n.Path == unchanged.Path).Quarantine!.ExpectedTarget!.Hash == new string('b', 64),
            "unchanged captured version publishes its optional hash");
        using (FairStoreGate.WithCancellation(new CancellationToken(canceled: true)))
            store.CommitResult("p1", 1, actions[0], new ActionResult(actions[0].ActionId, Outcome.Done, null, null, null, null));
        Require(store.IntentById("p1", actions[0].ActionId) is null,
            "already completed effect receipt persists even after stop");

        using var serial = new SemaphoreSlim(1);
        var committer = new StoreCommitter(store, serial, "p1", 1);
        var admitted = IntentFor("admitted-checkpoint");
        using (FairStoreGate.WithCancellation(new CancellationToken(canceled: true)))
        {
            await committer.RecordIntentAsync(admitted, CancellationToken.None);
            await committer.CommitAsync(admitted, new ActionResult(admitted.ActionId, Outcome.Done, null, null, null, null));
        }
        Require(store.IntentById("p1", admitted.ActionId) is null,
            "explicitly admitted checkpoint/result group ignores cancelled ambient run");
        using var stoppedDuringFlush = new CancellationTokenSource();
        var midFlush = IntentFor("mid-flush");
        await serial.WaitAsync();
        Task intentWrite, resultWrite;
        using (FairStoreGate.WithCancellation(stoppedDuringFlush.Token))
        {
            intentWrite = committer.RecordIntentAsync(midFlush, stoppedDuringFlush.Token);
            resultWrite = committer.CommitAsync(midFlush, new ActionResult(midFlush.ActionId, Outcome.Done, null, null, null, null));
        }
        stoppedDuringFlush.Cancel();
        serial.Release();
        await Task.WhenAll(intentWrite, resultWrite).WaitAsync(TimeSpan.FromSeconds(15));
        Require(store.IntentById("p1", midFlush.ActionId) is null,
            "stop while group flush waits cannot discard mixed intent and completed result");

        var duplicate = IntentFor("duplicate-quarantine");
        var quarantined = new ActionResult(duplicate.ActionId, Outcome.Failed, null, null, null, "blocked") { Quarantined = true };
        store.CommitResults("p1", 1, [(duplicate, quarantined), (duplicate, quarantined)]);
        Require(store.OpenNotices("p1").Any(n => n.ReasonKey == QuarantineFailure.NoticeKey && n.Path == duplicate.Path),
            "duplicate quarantined action IDs retain last-wins capture instead of failing receipt batch");

        using var operations = RunOperations.Start(TimeProvider.System, Fast with { OperationSweepInterval = TimeSpan.Zero }, _ => { });
        using (gate.Enter(operationName: "fixture-owner"))
        {
            Require(operations.Summarize().Oldest?.Label == "hold fixture-owner", "holder has separately named hold timing");
        }
        Require(operations.Summarize().Oldest is null, "hold tracking ends on release");
        Console.WriteLine("PASS store gate checks");
    }
}
