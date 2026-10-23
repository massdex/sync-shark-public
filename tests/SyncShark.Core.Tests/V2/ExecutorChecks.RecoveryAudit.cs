namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    public static async Task RunRecoveryAuditAsync(string root)
    {
        await CliRecoveryMappingChecks.RunAsync();
        await RecoveryAudit_GraceClockAsync();
        await RecoveryAudit_ThousandEntryLostAckAsync(root);
        await RecoveryAudit_DeleteJobRestartAsync(root);
        await ProviderCheckpoint_ReconcileKeepsPending();
        Console.WriteLine("v2 recovery audit checks passed.");
    }

    private static async Task RecoveryAudit_GraceClockAsync()
    {
        var (_, source, target, _) = Rig();
        var start = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        var action = Act(ActionKind.CopyNew, Side.B, "grace", source: source.Seed("grace", "content")) with
            { ProviderEffect = new ProviderEffectCheckpoint("upload-commit", "s1", start) };
        var legacy = await EffectResolver.ResolveAsync(action with
            { ProviderEffect = new ProviderEffectCheckpoint("upload-commit", "legacy") }, target, source, default, isStateDamage: StorageFailureClassifier.IsStateDamage,
            TimeSpan.FromMinutes(15), new RecoveryClock(start));
        Require(legacy.Outcome == Outcome.Uncertain && legacy.RetryAfterSeconds is null,
            "legacy checkpoint without timestamp needs review, not endless retry");
        foreach (var seconds in new[] { 30, 300, 1800 })
        {
            var result = await EffectResolver.ResolveAsync(action, target, source, default, isStateDamage: StorageFailureClassifier.IsStateDamage,
                TimeSpan.FromMinutes(15), new RecoveryClock(start.AddSeconds(seconds)));
            Require(seconds < 900 ? result.Outcome == Outcome.Uncertain && result.RetryAfterSeconds == 900 - seconds
                : result.Outcome == Outcome.Skipped && result.RetryAfterSeconds is null,
                "fake clock preserves exact remaining grace without a real wait");
        }
    }

    private sealed class RecoveryClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task RecoveryAudit_DeleteJobRestartAsync(string root)
    {
        using var wire = new DropboxWire { DeleteBatchAsyncJob = true };
        using var http = new HttpClient(wire);
        var settings = Settings(1) with { DeleteBatchSize = 1, StoreDurableCommits = false };
        var writer = new DropboxWriter(Pair().B, Side.B, UploadSafetyClient(http, new FixedTokenSource()), "unused", isStateDamage: StorageFailureClassifier.IsStateDamage, settings);
        wire.SeedFile("delete-restart", "old");
        var victim = (await writer.ObserveAsync("delete-restart", default))!;
        var source = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        var original = source.Seed(victim.Path, "old");
        var action = Act(ActionKind.Delete, Side.B, victim.Path, expected: victim, rf: true, key: "delete-key", rule: 6);
        var folder = Path.Combine(root, "delete-job-" + Guid.NewGuid().ToString("N"));
        var previous = ProviderEffectContext.Current.Value;
        using (var store = SqliteStateStore.Open(folder, settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(Pair());
            var adopt = Act(ActionKind.Adopt, Side.B, victim.Path, source: original, expected: victim, key: "delete-key");
            store.RecordIntent("p1", adopt);
            store.CommitResult("p1", 1, adopt, new ActionResult(adopt.ActionId, Outcome.Done, original, victim, null, null));
            Require(store.Baselines("p1").Count == 1, "delete restart fixture has a confirmed baseline");
            await RemoveSourceAsync(source, victim.Path);
            store.RecordIntent("p1", action);
            ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint =>
            {
                store.RecordIntent("p1", action with { ProviderEffect = checkpoint });
                // Stop at the exact durable job boundary, before a poll or result can be recorded.
                if (checkpoint.Kind == "delete-job") throw new IOException("injected process boundary");
                return Task.CompletedTask;
            });
            try
            {
                try { await writer.DeleteAsync(action, default); }
                catch (UncertainEffectException) { }
            }
            finally { ProviderEffectContext.Current.Value = previous; }
        }
        using var reopened = SqliteStateStore.Open(folder, settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pending = reopened.UnresolvedIntents("p1");
        Require(pending.Count == 1 && pending[0].ProviderEffect?.Kind == "delete-job", "restart retains the exact job checkpoint");
        var results = await new Executor(reopened, settings, Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException).ReconcileAsync(pending, 1,
            source, writer, default);
        Require(results.Single().Outcome == Outcome.Done && reopened.UnresolvedIntents("p1").Count == 0,
            "saved job resolves its original deletion after store reopen");
        Require(wire.Calls.Count(c => c == "/2/files/delete_batch") == 1 &&
            wire.Calls.Count(c => c == "/2/files/delete_batch/check") == 1,
            "restart checks the job once and never sends a second delete");
    }

    // Reopen the real store at the durable boundary, not just the in-memory journal. Half the
    // provider effects land before the acknowledgement disappears; reconciliation must not resend them.
    private static async Task RecoveryAudit_ThousandEntryLostAckAsync(string root)
    {
        const int count = 1000, landed = 500;
        var source = new FakeEndpoint(Side.A, "ra");
        var actions = Enumerable.Range(0, count).Select(i => Act(ActionKind.CopyNew, Side.B,
            "recover-" + i, source: source.Seed("recover-" + i, new string('x', 16)), key: "k" + i)).ToArray();
        using var wire = new UploadBatchWire(actions[0].Source!.Hash!)
            { LoseAcknowledgement = true, ApplyBeforeLostAcknowledgement = landed };
        using var http = new HttpClient(wire);
        var settings = Settings(8) with { UploadBatchSize = count, PendingUploadCommitLimit = count,
            UploadBatchMaxAge = TimeSpan.FromMinutes(1), StoreDurableCommits = false };
        var writer = new DropboxWriter(Pair().B, Side.B, UploadSafetyClient(http, new FixedTokenSource()), "unused", isStateDamage: StorageFailureClassifier.IsStateDamage, settings);
        var folder = Path.Combine(root, "lost-ack-" + Guid.NewGuid().ToString("N"));
        using (var store = SqliteStateStore.Open(folder, settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(Pair());
            var results = await Apply(new Executor(store, settings, Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException), Plan(actions), source, writer)
                .WaitAsync(TimeSpan.FromSeconds(20));
            Require(results.Count == count && wire.BatchSizes.SequenceEqual(new[] { count }), "one full 1000-entry uncertain finish");
            Require(results.Count(r => r.Outcome == Outcome.Done) == landed, "positive observations retain applied effects");
            Require(store.UnresolvedIntents("p1").Count == count - landed, "only unknown effects remain pending");
        }
        using (var reopened = SqliteStateStore.Open(folder, settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var pending = reopened.UnresolvedIntents("p1");
            Require(pending.All(a => a.ProviderEffect?.Kind == "upload-commit"), "commit checkpoints survive reopening");
            var executor = new Executor(reopened, settings, Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
            var results = await executor.ReconcileAsync(pending, 1, source, writer, default);
            Require(results.All(r => r.Outcome == Outcome.Uncertain), "absence alone never immediately replays an unknown finish");
            Require(wire.Finishes == 1 && wire.Starts == count, "restart sends no duplicate mutation");
            // Provider completes the remaining entries after the process died. Read-only replay closes them.
            foreach (var action in pending) wire.AppliedPaths[DropboxWire.RootId + "/" + action.Path] = "late-" + action.Path;
            results = await executor.ReconcileAsync(reopened.UnresolvedIntents("p1"), 1, source, writer, default);
            Require(results.All(r => r.Outcome == Outcome.Done) && reopened.UnresolvedIntents("p1").Count == 0,
                "late effects are recovered without duplicate uploads or lost work");
            Require(wire.Finishes == 1, "reconciliation never replays the batch");
        }
    }
}
