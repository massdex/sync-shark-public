using System.Net;
using System.Text;

namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    // Fix A: the staging buffer is decoupled from, and larger than, one finish batch. Its single home is
    // V2Settings, derived from the batch size and the commit concurrency it must stay ahead of.
    private static void UploadPipeline_StagingBufferDefault()
    {
        var settings = V2Settings.Default;
        var expected = settings.UploadBatchSize * (settings.UploadCommitConcurrency + 2);
        Require(settings.PendingUploadCommitLimit == expected && expected == 3000,
            $"the default staging buffer is UploadBatchSize x (UploadCommitConcurrency + 2) = {settings.PendingUploadCommitLimit}");
        Require(settings.PendingUploadCommitLimit >= settings.UploadBatchSize,
            "the staging buffer always holds at least one full batch");
        Require((V2Settings.Default with { PendingUploadCommitLimit = 7 }).PendingUploadCommitLimit == 7,
            "an explicit staging-buffer cap is honoured over the derived default");
        Require((V2Settings.Default with { UploadBatchSize = 250 }).PendingUploadCommitLimit == 250 * 3,
            "the derived default tracks a changed batch size");
        // The executor's own pending-effect bound still holds with the larger buffer.
        var capacity = checked(settings.MaxWorkers + settings.PendingUploadCommitLimit +
            settings.UploadBatchSize * settings.UploadCommitConcurrency);
        Require(capacity == 64 + 3000 + 1000, $"the executor pending-effect bound still holds ({capacity})");
    }

    // Fix A: with a slow commit the buffer holds the current batch plus the ones behind it, so staging workers keep
    // working while the serial commit runs instead of stalling at one batch.
    private static async Task UploadPipeline_StagesAheadWhileCommitInFlightAsync()
    {
        const int batchSize = 4, limit = batchSize * 3;
        var settings = V2Settings.Default with { UploadBatchSize = batchSize, PendingUploadCommitLimit = limit,
            UploadBatchMaxAge = TimeSpan.FromMilliseconds(20) };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new DropboxUploadQueue(settings, async (batch, _) =>
        {
            firstCommit.TrySetResult();
            await release.Task;
            foreach (var item in batch)
                item.Result.TrySetResult(new PilotUploadResult("id", "rev", item.Size, item.ExpectedContentHash));
        });
        var staged = new List<DropboxUploadCommit>();
        try
        {
            for (var i = 0; i < limit; i++)
            {
                await queue.Slots.WaitAsync();
                var item = new DropboxUploadCommit("f", null, "s" + i, "hash", 1);
                staged.Add(item);
                queue.Enqueue(item);
            }
            await firstCommit.Task.WaitAsync(HeldEffectTimeout);
            // One batch is in flight (holding its commit), so the two batches behind it stay buffered, not blocking:
            // the buffer still holds both, so only limit - 2 batches' worth of slots have been returned.
            Require(queue.Slots.CurrentCount == limit - 2 * batchSize,
                $"two more batches are buffered while one commit is in flight ({limit - queue.Slots.CurrentCount} staged)");
            Require(staged.Take(batchSize).All(i => !i.Result.Task.IsCompleted),
                "a held commit leaves the in-flight entries unresolved");
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(staged.Select(i => i.Result.Task)).WaitAsync(HeldEffectTimeout);
        Require(queue.Slots.CurrentCount == limit, "every staged slot is returned once the queue drains");
    }

    // Fix A: an aged partial tail whose age elapses while every commit lane is busy keeps trying (re-arms) instead of
    // firing once, so it still dispatches once a lane frees without waiting for another arrival.
    private static async Task UploadPipeline_AgeFlushReArmsAsync()
    {
        var settings = V2Settings.Default with { UploadBatchSize = 3, PendingUploadCommitLimit = 9,
            UploadBatchMaxAge = TimeSpan.FromMilliseconds(30) };
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatches = new List<int>();
        var queue = new DropboxUploadQueue(settings, async (batch, _) =>
        {
            bool first;
            lock (dispatches) { first = dispatches.Count == 0; dispatches.Add(batch.Count); }
            if (first) { firstStarted.TrySetResult(); await releaseFirst.Task; }
            foreach (var item in batch)
                item.Result.TrySetResult(new PilotUploadResult("id", "rev", item.Size, item.ExpectedContentHash));
        });
        var first = Enumerable.Range(0, 3).Select(i => new DropboxUploadCommit("f", null, "b" + i, "hash", 1)).ToArray();
        foreach (var item in first) { await queue.Slots.WaitAsync(); queue.Enqueue(item); }
        await firstStarted.Task.WaitAsync(HeldEffectTimeout);
        // A short tail arrives while the only lane is busy; its age elapses several times over.
        await queue.Slots.WaitAsync();
        var tail = new DropboxUploadCommit("f", null, "tail", "hash", 1);
        queue.Enqueue(tail);
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        Require(!tail.Result.Task.IsCompleted, "the tail waits while the only commit lane is held");
        Require(queue.AgeFlushAttempts >= 2, $"the age flush re-armed through several windows ({queue.AgeFlushAttempts})");
        releaseFirst.TrySetResult();
        await tail.Result.Task.WaitAsync(HeldEffectTimeout);
        lock (dispatches) Require(dispatches.Count == 2, "the tail dispatches without a new arrival once the lane frees");
    }

    // Fix B: the committer records many entries' checkpoints as one durable write (one transaction), and a failing
    // write fails the whole group so no caller sees a one-by-one split.
    private static async Task GroupCommit_ProviderCheckpointsAreOneTransactionAsync()
    {
        await ProviderOwner_GroupingAndRecoveryAsync();
        const int entries = 8;
        var inner = new InMemoryStateStore();
        inner.SavePair(Pair());
        var store = new JournalStore(inner, new List<string>());
        using var gate = new SemaphoreSlim(1, 1);
        var committer = new StoreCommitter(store, gate, "p1", 1);
        var actions = Enumerable.Range(0, entries)
            .Select(i => Act(ActionKind.CopyNew, Side.B, "f" + i, key: "k" + i)).ToArray();
        var contexts = actions.Select(a => committer.CreateProviderContext(a)).ToArray();
        Require(contexts.All(c => c.Owner is not null && c.Action is not null),
            "an executor-created context carries its action and committer");
        Require(new ProviderEffectContext(_ => Task.CompletedTask).Owner is null,
            "a free-standing context has no committer");
        var checkpoints = contexts
            .Select((c, i) => (Action: c.Action!,
                Checkpoint: new ProviderEffectCheckpoint("upload-commit", "s" + i, DateTimeOffset.UtcNow)))
            .ToArray();

        await committer.RecordProviderCheckpointsAsync(checkpoints).WaitAsync(HeldEffectTimeout);

        Require(store.IntentBatches.Count == 1 && store.IntentBatches[0] == entries,
            $"all {entries} checkpoints share one store transaction ({string.Join(",", store.IntentBatches)})");
        var intents = inner.UnresolvedIntents("p1");
        Require(intents.Count == entries && intents.All(i => i.ProviderEffect?.Kind == "upload-commit"),
            "every entry's commit checkpoint is durable after the single write");
        Require(actions.All(a => committer.LatestAction(a).ProviderEffect?.Kind == "upload-commit"),
            "each action's latest checkpoint is published only after durability");
        Require(intents.Select(i => i.ProviderEffect!.Id).OrderBy(id => id)
                .SequenceEqual(checkpoints.Select(c => c.Checkpoint.Id).OrderBy(id => id)),
            "each durable checkpoint carries its own entry's session id");

        // A failing checkpoint write makes nothing durable and surfaces to the caller.
        var failing = new JournalStore(new InMemoryStateStore(), new List<string>());
        failing.SavePair(Pair());
        failing.FailIntentsWhen = batch => batch.Any(a => a.ProviderEffect?.Kind == "upload-commit");
        using var failingGate = new SemaphoreSlim(1, 1);
        var failingCommitter = new StoreCommitter(failing, failingGate, "p1", 1);
        var failingContexts = actions.Select(a => failingCommitter.CreateProviderContext(a)).ToArray();
        var failingCheckpoints = failingContexts
            .Select((c, i) => (c.Action!, new ProviderEffectCheckpoint("upload-commit", "s" + i, DateTimeOffset.UtcNow)))
            .ToArray();
        await ExpectAsync<IOException>(() => failingCommitter.RecordProviderCheckpointsAsync(failingCheckpoints));
        Require(failing.IntentBatches.Count == 0, "a failed checkpoint group durably records nothing");
    }

    // Fix C: under load the committer's group commit keeps transactions per write far below one — writes that queue
    // while a transaction runs are drained together, never one transaction per write.
    private static async Task GroupCommit_TransactionsPerWriteUnderLoadAsync()
    {
        var store = new JournalStore(new InMemoryStateStore(), new List<string>())
            { WriteCost = TimeSpan.FromMilliseconds(1) };
        store.SavePair(Pair());
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb") { Latency = TimeSpan.FromMilliseconds(5) };
        var record = new RecordingWriter(b, new List<string>());
        var executor = new Executor(store, Settings(128), Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage);
        var actions = Enumerable.Range(0, 128)
            .Select(i => Act(ActionKind.CopyNew, Side.B, "f" + i, source: a.Seed("f" + i, "v" + i), key: "k" + i))
            .ToArray();

        var results = await Apply(executor, Plan(actions), a, record);
        Require(results.Count == 128 && results.All(r => r.Outcome == Outcome.Done), "all 128 copies commit");
        var writes = store.IntentBatches.Sum() + store.ResultBatches.Sum();
        var transactions = store.IntentBatches.Count + store.ResultBatches.Count;
        Require(store.IntentBatches.Sum() == 128 && store.ResultBatches.Sum() == 128,
            "every intent and every result is written exactly once");
        var ratio = (double)transactions / writes;
        Console.WriteLine($"group commit: {transactions} transactions for {writes} writes ({ratio:F3} transactions/write)");
        Require(ratio < 0.5, $"transactions per write stays far below one under load ({ratio:F3})");
    }

    // Fix B, end to end: a finish batch's checkpoints are one transaction and are durable before the send, and if the
    // checkpoint write fails nothing is sent.
    private static async Task UploadPipeline_CheckpointOneTransactionBeforeSendAsync()
    {
        const int files = 4;
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16));
        var source = new FakeEndpoint(Side.A, "ra");
        var actions = Enumerable.Range(0, files)
            .Select(i => Act(ActionKind.CopyNew, Side.B, "f" + i, source: source.Seed("f" + i, bytes), key: "k" + i))
            .ToArray();
        using var wire = new UploadBatchWire(actions[0].Source!.Hash!) { HoldFirstCommit = true, ExpectedStarts = files };
        using var http = new HttpClient(wire);
        var client = UploadClient(http);
        var settings = Settings(files) with { UploadBatchSize = files, UploadBatchMaxAge = TimeSpan.FromSeconds(2) };
        var inner = new InMemoryStateStore();
        inner.SavePair(Pair());
        var store = new JournalStore(inner, new List<string>());
        var writer = new DropboxWriter(new RootDef("rb", "Dropbox", "acc", "/Pilot", DropboxWire.RootId),
            Side.B, client, "unused", isStateDamage: StorageFailureClassifier.IsStateDamage, settings);
        var work = Apply(new Executor(store, settings, Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage), Plan(actions), source, writer);
        try
        {
            // The finish request reaching the scripted transport proves every checkpoint was already awaited durable.
            await wire.FirstCommit.Task.WaitAsync(HeldEffectTimeout);
            var checkpoints = inner.UnresolvedIntents("p1").Where(i => i.ProviderEffect?.Kind == "upload-commit").ToArray();
            Require(checkpoints.Length == files, "every entry's commit checkpoint is durable before the send");
            var batches = store.IntentActionBatches
                .Where(b => b.Any(i => i.ProviderEffect?.Kind == "upload-commit")).ToArray();
            Require(batches.Length == 1, $"the batch's checkpoints share one store transaction ({batches.Length})");
            Require(batches[0].Count(i => i.ProviderEffect?.Kind == "upload-commit") == files,
                "the single transaction holds every entry's checkpoint");
        }
        finally { wire.ReleaseCommit.TrySetResult(); }
        var results = await work.WaitAsync(HeldEffectTimeout);
        Require(results.Count == files && results.All(r => r.Outcome == Outcome.Done), "the batch commits and all entries are Done");
        Require(inner.UnresolvedIntents("p1").Count == 0, "every result durably closes its intent");
    }

    // Fix B, failure path: when the checkpoint transaction fails, no finish is sent and no entry reports Done.
    private static async Task UploadPipeline_CheckpointWriteFails_NothingSentAsync()
    {
        const int files = 4;
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16));
        var source = new FakeEndpoint(Side.A, "ra");
        var actions = Enumerable.Range(0, files)
            .Select(i => Act(ActionKind.CopyNew, Side.B, "f" + i, source: source.Seed("f" + i, bytes), key: "k" + i))
            .ToArray();
        using var wire = new UploadBatchWire(actions[0].Source!.Hash!) { ExpectedStarts = files };
        using var http = new HttpClient(wire);
        var client = UploadClient(http);
        var settings = Settings(files) with { UploadBatchSize = files, UploadBatchMaxAge = TimeSpan.FromSeconds(2) };
        var inner = new InMemoryStateStore();
        inner.SavePair(Pair());
        var store = new JournalStore(inner, new List<string>())
            { FailIntentsWhen = batch => batch.Any(a => a.ProviderEffect?.Kind == "upload-commit") };
        var writer = new DropboxWriter(new RootDef("rb", "Dropbox", "acc", "/Pilot", DropboxWire.RootId),
            Side.B, client, "unused", isStateDamage: StorageFailureClassifier.IsStateDamage, settings);

        var results = await Apply(new Executor(store, settings, Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage), Plan(actions), source, writer)
            .WaitAsync(HeldEffectTimeout);
        Require(wire.Finishes == 0, "no finish is sent when the checkpoint write fails");
        Require(results.Count == files && results.All(r => r.Outcome != Outcome.Done),
            "no entry reports Done without a durable checkpoint");
    }

    private static async Task ProviderOwner_GroupingAndRecoveryAsync()
    {
        var firstStore = new InMemoryStateStore();
        var secondStore = new InMemoryStateStore();
        firstStore.SavePair(Pair());
        secondStore.SavePair(Pair());
        using var firstGate = new SemaphoreSlim(1, 1);
        using var secondGate = new SemaphoreSlim(1, 1);
        var first = new StoreCommitter(firstStore, firstGate, "p1", 1);
        var second = new StoreCommitter(secondStore, secondGate, "p1", 1);
        var calls = new int[2];
        var sizes = new int[2];
        var fallbackCalls = 0;
        var batch = new List<DropboxUploadCommit>();
        var previous = ProviderEffectContext.Current.Value;
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var index = i < 3 ? 0 : 1;
                var committer = index == 0 ? first : second;
                var action = Act(ActionKind.CopyNew, Side.B, "owner-" + i, key: "owner-key-" + i);
                var context = committer.CreateProviderContext(action);
                var owner = context.Owner!;
                Require(ReferenceEquals(owner.Key, committer) && owner.PairId == "p1",
                    "provider owner carries the actual committer key and pair identity");
                // A separate descriptor and delegate per effect must still produce one call per committer.
                var observed = owner with { RecordCheckpoints = async checkpoints =>
                {
                    calls[index]++;
                    sizes[index] = checkpoints.Count;
                    await owner.RecordCheckpoints(checkpoints);
                } };
                ProviderEffectContext.Current.Value = new ProviderEffectContext(action, observed,
                    _ => throw new InvalidOperationException("Owned entries must use grouped recording."));
                batch.Add(new DropboxUploadCommit(action.Path, null, "session-" + i, "hash", 1));
            }
            ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint =>
            {
                Require(checkpoint.Kind == "upload-commit" && checkpoint.Id == "free-session",
                    "ownerless fallback retains checkpoint kind and session identity");
                fallbackCalls++;
                return Task.CompletedTask;
            });
            batch.Add(new DropboxUploadCommit("free", null, "free-session", "hash", 1));
            ProviderEffectContext.Current.Value = null;
            batch.Add(new DropboxUploadCommit("untracked", null, "untracked-session", "hash", 1));
        }
        finally { ProviderEffectContext.Current.Value = previous; }

        var record = typeof(DropboxPilotClient).GetMethod("RecordUploadCommitCheckpointsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        using (StoreAdmission.WithCancellation(new CancellationToken(canceled: true)))
            await ((Task)record.Invoke(null, new object[] { batch })!).WaitAsync(HeldEffectTimeout);
        Require(calls.SequenceEqual(new[] { 1, 1 }) && sizes.SequenceEqual(new[] { 3, 2 }),
            "same committer gets exactly one grouped call; equal-pair distinct committers remain separate");
        Require(fallbackCalls == 1, "only the ownerless context uses per-context recording");
        Require(firstStore.UnresolvedIntents("p1").Count == 3 && secondStore.UnresolvedIntents("p1").Count == 2,
            "grouped checkpoints are durable in their own stores despite cancelled ambient admission");
        Require(batch.Take(5).All(item =>
            (ReferenceEquals(item.Context!.Owner!.Key, first) ? first : second)
                .LatestAction(item.Context.Action!).ProviderEffect?.Id == item.SessionId),
            "every checkpoint retains its action and session identity");

        var recoveryAction = Act(ActionKind.CopyNew, Side.B, "recovered", key: "recovery-key");
        var recoveryContext = first.CreateProviderContext(recoveryAction);
        var recovery = new RecoveryRef(Side.B, "archive/recovered", null, "hash", 1);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await recoveryContext.RecordRecoveryAsync(recoveryAction.ActionId, recoveryAction.Path, recovery,
            "other-pair", stopped.Token);
        await new ProviderEffectContext(_ => Task.CompletedTask).RecordRecoveryAsync(
            recoveryAction.ActionId, recoveryAction.Path, recovery, "p1", stopped.Token);
        Require(firstStore.RecoveryForAction("p1", recoveryAction.ActionId) is null,
            "pair mismatch and ownerless recovery are no-ops even with a cancelled token");
        await ExpectAsync<OperationCanceledException>(() => recoveryContext.RecordRecoveryAsync(
            recoveryAction.ActionId, recoveryAction.Path, recovery, "p1", stopped.Token));
        Require(firstStore.RecoveryForAction("p1", recoveryAction.ActionId) is null,
            "cancelled recovery admission cannot publish a receipt or permit removal");

        await firstGate.WaitAsync();
        Task durableRecovery;
        try
        {
            durableRecovery = recoveryContext.RecordRecoveryAsync(recoveryAction.ActionId, recoveryAction.Path,
                recovery, "p1", CancellationToken.None);
            Require(!durableRecovery.IsCompleted && firstStore.RecoveryForAction("p1", recoveryAction.ActionId) is null,
                "provider recovery does not complete while durable store admission is blocked");
        }
        finally { firstGate.Release(); }
        await durableRecovery.WaitAsync(HeldEffectTimeout);
        Require(firstStore.RecoveryForAction("p1", recoveryAction.ActionId)?.Location == recovery.Location,
            "provider may remove the original only after its recovery receipt is durable");

        await first.RecordIntentAsync(recoveryAction, CancellationToken.None);
        using (StoreAdmission.WithCancellation(stopped.Token))
            await first.CommitAsync(recoveryAction,
                new ActionResult(recoveryAction.ActionId, Outcome.Done, null, null, null, null))
                .WaitAsync(HeldEffectTimeout);
        Require(firstStore.UnresolvedIntents("p1").All(action => action.ActionId != recoveryAction.ActionId),
            "completed-effect commits remain noncancellable after owner descriptor migration");
    }

    private static DropboxPilotClient UploadClient(HttpClient http) => new(http, new FixedTokenSource(), "/Pilot",
        new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes), DropboxWire.RootId);
}
