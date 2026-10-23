namespace Massdex.SyncShark.V2;

internal static partial class RunCliChecks
{
    private static async Task ReviewedRemovalFreshReadBothSidesAsync()
    {
        await ReviewedHashlessChoiceAsync();
        foreach (var side in new[] { Side.A, Side.B })
        foreach (var carried in new[] { false, true })
        foreach (var change in new[] { "unchanged", "new", "changed", "missing", "listing-failure", "unsupported", "partial", "timeout", "legacy" })
        {
            if (!carried && change == "legacy") continue;
            var directory = TempRoot();
            var passed = false;
            try
            {
                using var store = SqliteStateStore.Open(Path.Combine(directory, "state"), Settings, TimeProvider.System);
                var fx = Build(store: store);
                var target = side == Side.A ? fx.A : fx.B;
                var writer = side == Side.A ? fx.WriterA : fx.WriterB;
                var targetRoot = side == Side.A ? fx.Pair.A : fx.Pair.B;
                var folder = target.Seed("reviewed", "", ItemKind.Folder);
                var child = target.Seed("reviewed/child.tmp", "original bytes");
                var generation = store.BeginGeneration(targetRoot.RootId, "reviewed-seed");
                store.WriteSnapshot(new Snapshot(targetRoot.RootId, side, generation, "", [folder, child], []));
                store.MarkFirstSyncDone(fx.Pair.PairId);
                var pair = fx.Pair with { Excluded = ["reviewed"], IgnoreRules = carried ? ["FileExtension:tmp"] : [] };
                store.SavePair(pair);
                var action = new SyncAction("reviewed-remove", 32, ActionKind.Delete, side, folder.Path,
                    null, null, folder, false, null)
                {
                    FirstSyncChoice = true, ChoiceRemove = true,
                    // Consent-time freeze: persist the disclosed ceiling with the full proof,
                    // as production BindCarriedIgnoredBudgets/FreezeFolderDeletes do.
                    CarriedIgnoredBudget = carried ? new(1, 0, child.Size) : new(0, 0, 0),
                    FrozenVictimSignature = FolderDelete.Signature(carried ? [] : [child])
                };
                if (change != "legacy")
                    action = action with { FrozenRemovalSignature = await DeleteSourceGuard.CaptureReviewedRemovalAsync(
                        action, pair, store, target, default, isStateDamage: StorageFailureClassifier.IsStateDamage) };
                if (carried)
                {
                    Require(await DeleteSourceGuard.CaptureDisclosedRemovalAsync(action, pair, store, target,
                        1, 0, child.Size, default, isStateDamage: StorageFailureClassifier.IsStateDamage) is not null, "freeze matches disclosed carried totals");
                    Require(await DeleteSourceGuard.CaptureDisclosedRemovalAsync(action, pair, store, target,
                        0, 0, 0, default, isStateDamage: StorageFailureClassifier.IsStateDamage) is null, "undisclosed carried child cannot be silently frozen as approved");
                }
                var due = DateTimeOffset.UtcNow.AddMinutes(1);
                store.SaveSyncRetry(fx.Pair.PairId, new SyncRetry(folder.Path, "reviewed-removal", 1, "retry", due)
                    { ReviewedActions = [action] });
                var clock = new ReviewedRemovalRetryClock { Now = due.AddTicks(-1) };
                var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage), fx.Provider, Settings, clock);
                if (change == "new") target.Seed("reviewed/new.tmp", "unreviewed");
                if (change == "changed") target.Seed(child.Path, "changed and longer bytes");
                if (change == "missing")
                    await target.DeleteAsync(new SyncAction("external-remove", 6, ActionKind.Delete, side,
                        child.Path, null, null, child, false, null), default);
                await run.IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
                Require(writer.Deletes == 0, "sleeping reviewed removal has zero effects: " + side + "/" + carried + "/" + change);
                if (change == "listing-failure") target.SubtreeReadFailure = new IOException("listing failed after a partial page");
                if (change == "unsupported") target.SubtreeReadFailure = new NotSupportedException("Subtree listing not implemented");
                if (change == "timeout") target.SubtreeReadFailure = new TimeoutException("targeted listing timed out");
                if (change == "partial") target.SubtreeTransform = item => item with { IncompleteReason = "incomplete listing" };
                clock.Now = due.AddTicks(1);
                await run.IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
                if (change == "unchanged")
                    Require(writer.Deletes == (carried ? 1 : 2) && await target.ObserveAsync(folder.Path, default) is null,
                        "unchanged reviewed removal completes after direct fresh proof on " + side);
                else
                    Require(writer.Deletes == 0 && await target.ObserveAsync(folder.Path, default) is not null &&
                        store.UnresolvedNotices(fx.Pair.PairId).Any(notice => notice.Path == folder.Path && notice.ReasonKey == "reviewed-folder-grew"),
                        "new/changed/missing/unproven child holds whole removal with zero deletes: " + side + "/" + carried + "/" + change);
                passed = true;
            }
            finally
            {
                if (passed) Cleanup(directory);
                else Console.Error.WriteLine("Retained reviewed-removal fixture: " + directory);
            }
        }
    }
}
