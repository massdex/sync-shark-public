using System.Text.Json.Nodes;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task FolderTreeRecoveryChecksAsync()
    {
        await FolderTreeCheckpointDamageAsync();
        await FolderTreeResumeSharedFeedAsync();
        await FolderTreeResumeAfterExclusionAsync();
        await FolderTreeResumeExcludedAnchorAsync();
        await FolderTreeResumeAddedFileAsync();
        await FolderTreeResumePartialCommitAsync();
        await FolderTreeUnknownAcceptanceAsync();
        await FolderTreeUnknownGraceAsync();
        await FolderTreePendingJobRecoveryAsync();
        await FolderTreeCheckConflictRecoveryAsync();
    }

    private static async Task FolderTreeCheckConflictRecoveryAsync()
    {
        foreach (var tag in new[] { "internal_error", "invalid_async_job_id", "expired_async_job_id", "unexpected_error" })
        {
            using var rig = await TreeRigAsync(60);
            rig.H.Wire.DeleteBatchAsyncJob = true;
            rig.H.Wire.DeleteJobTerminal = tag;
            var applied = await rig.ApplyAsync(rig.Plan());
            Require(applied.All(result => result.Outcome == Outcome.Uncertain) &&
                applied.Any(result => result.Reason?.Contains(tag, StringComparison.Ordinal) == true),
                "a check 409 retains uncertain victims and names its structured error tag: " + tag);
            var anchor = rig.Store.UnresolvedIntents("p1").Single(action => action.ProviderEffect?.Kind == "whole-folder-group");
            var group = JsonNode.Parse(anchor.ProviderEffect!.Id)!.AsObject();
            var provider = JsonNode.Parse(group["ProviderState"]!.GetValue<string>())!.AsObject();
            provider["SubmittedAt"] = DateTimeOffset.UtcNow - V2Settings.Default.StuckJobEscalation - TimeSpan.FromHours(1);
            group["ProviderState"] = provider.ToJsonString();
            rig.Store.RecordIntent("p1", anchor with { ProviderEffect = anchor.ProviderEffect with { Id = group.ToJsonString() } });
            rig.H.Wire.FailFeedAfterFolderDelete = true;
            var executor = new Executor(rig.Store, V2Settings.Default, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var pending = await executor.ReconcileAsync(rig.Store.UnresolvedIntents("p1"), 2 + attempt,
                    rig.A, rig.B, CancellationToken.None);
                Require(pending.All(result => result.Outcome == Outcome.Uncertain),
                    "failed checks/feed reads never prove deletion or authorize replay");
            }
            Require(rig.Store.OpenNotices("p1").Count(notice => notice.ReasonKey == ExecutionReasons.StuckProviderJob.Key &&
                    notice.Detail.Contains("delete-job-1", StringComparison.Ordinal)) == 1,
                "repeated whole-folder recovery errors raise one notice naming the saved job");
            rig.H.Wire.FailFeedAfterFolderDelete = false;
            if (tag == "unexpected_error")
            {
                var pending = await executor.ReconcileAsync(rig.Store.UnresolvedIntents("p1"), 4,
                    rig.A, rig.B, CancellationToken.None);
                Require(pending.All(result => result.Outcome == Outcome.Uncertain),
                    "unknown check errors are not silently classified as unavailable");
                rig.H.Wire.DeleteJobTerminal = "internal_error";
            }
            var done = await executor.ReconcileAsync(rig.Store.UnresolvedIntents("p1"), 5, rig.A, rig.B, CancellationToken.None);
            Require(done.All(result => result.Outcome == Outcome.Done) && rig.H.Wire.WholeFolderDeletes == 1 &&
                    rig.Store.UnresolvedIntents("p1").Count == 0 &&
                    !rig.Store.OpenNotices("p1").Any(notice => notice.ReasonKey == ExecutionReasons.StuckProviderJob.Key),
                "unavailable job checks reconcile from the shared feed without replay and close their stuck notice");
        }
    }

    private static async Task FolderTreeResumeSharedFeedAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.FailFeedAfterFolderDelete = true;
        rig.H.Wire.BeforeBuryFolder = () =>
        {
            var before = rig.Store.UnresolvedIntents("p1");
            Require(before.Count == 62 && before.Count(action => action.ProviderEffect?.Kind == "whole-folder-group") == 1,
                "all members and their frozen recovery anchor are durable before the provider effect");
            Require(rig.Store.OpenNotices("p1").Count(notice => notice.ReasonKey == "folder-delete-history") == 1,
                "history warning is durable before submission");
        };
        await rig.ApplyAsync(rig.Plan());
        var intents = rig.Store.UnresolvedIntents("p1");
        Require(intents.Count(action => action.ProviderEffect?.Kind == "whole-folder-group") == 1 &&
            intents.Count(action => action.ProviderEffect?.Kind == "whole-folder-member") == 61,
            "the frozen snapshot is stored once, with compact member references");
        var feeds = rig.H.Wire.Calls.Count(call => call == "/2/files/list_folder/continue");
        var metadata = rig.H.Wire.Calls.Count(call => call == "/2/files/get_metadata");
        rig.H.Wire.FailFeedAfterFolderDelete = false;
        // A fresh executor has no in-memory group or provider state to rely on.
        var executor = new Executor(rig.Store, V2Settings.Default, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage);
        var results = await executor.ReconcileAsync(intents, 2, rig.A, rig.B, CancellationToken.None);
        Require(results.All(result => result.Outcome == Outcome.Done) && rig.Store.UnresolvedIntents("p1").Count == 0,
            "a failed post-check resumes from the durable group and resolves all victims");
        // Fastdelete merge 5b404df (shared recovery 3892eaa) uses ReadChangePageAsync,
        // whose existing VerifyRootIdentityAsync performs one shared root metadata read.
        Require(rig.H.Wire.Calls.Count(call => call == "/2/files/list_folder/continue") == feeds + 1 &&
            rig.H.Wire.Calls.Count(call => call == "/2/files/get_metadata") == metadata + 1 &&
            rig.H.Wire.WholeFolderDeletes == 1,
            "recovery reads one shared feed, not per-victim absence, and never replays deletion");
    }

    private static async Task FolderTreeResumeAfterExclusionAsync()
    {
        foreach (var excluded in new[] { "big", "big/sub" })
        {
            using var rig = await TreeRigAsync(60);
            rig.H.Wire.SeedFile("big/sub/late.txt", "added after the frozen scan");
            rig.H.Wire.FailFeedAfterFolderDelete = true;
            await rig.ApplyAsync(rig.Plan());
            var pending = rig.Store.UnresolvedIntents("p1");
            Require(pending.Count == 62 && pending.Count(action =>
                    action.ProviderEffect?.Kind == "whole-folder-group") == 1,
                "post-check failure leaves the whole group uncertain before exclusions change");
            var pair = rig.Store.Pair("p1")! with { Excluded = [excluded] };
            rig.Store.SavePair(pair);
            rig.Store.MarkFirstSyncDone("p1");
            rig.H.Wire.FailFeedAfterFolderDelete = false;
            var provider = new FakeEndpointProvider().Add(pair.A, rig.A)
                .AddInventory(pair.B, rig.H.Inventory).AddWriter(pair.B, rig.B);
            var settings = V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };
            var run = new SyncRun(rig.Store, new Executor(rig.Store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage),
                provider, settings, TimeProvider.System);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var report = await run.IncrementalAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
                Require(report.Failed == 0 && report.Failures.Count == 0 &&
                        rig.Store.UnresolvedIntents("p1").Count == 0,
                    "both runs succeed after exclusion without stranding whole-folder members");
            }
            Require(rig.H.Wire.Restores.SequenceEqual(["big/sub/late.txt"]) &&
                    await rig.B.ObserveAsync("big/sub/late.txt", CancellationToken.None) is { Kind: ItemKind.File } &&
                    rig.H.Wire.WholeFolderDeletes == 1,
                "normal group reconcile restores excluded content and never replays the delete");
            Require(rig.Store.OpenNotices("p1").Any(notice =>
                        notice.ReasonKey == ExecutionReasons.FolderDeleteRestored.Key) &&
                    !rig.Store.OpenNotices("p1").Any(notice => notice.ReasonKey == SyncRun.ExcludedIntentNoticeKey),
                "group recovery, not per-path excluded settlement, resolves the anchor and members; excluded=" + excluded +
                "; open reasons=" + string.Join(",", rig.Store.OpenNotices("p1").Select(notice => notice.ReasonKey)));
        }
    }

    private static async Task FolderTreeResumeExcludedAnchorAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.SeedFolder("big2");
        for (var index = 0; index < 60; index++)
            rig.H.Wire.SeedFile($"big2/f{index}.txt", "content " + index);
        var gen = rig.Store.BeginGeneration(RootId, "snapshot");
        var snapshot = await rig.H.Inventory.FullAsync(gen, _ => true, Sink, CancellationToken.None);
        rig.Store.WriteSnapshot(snapshot);
        rig.Store.CompleteGeneration(gen, snapshot.CursorAfter, true);
        var secondDelete = rig.Delete with
        {
            ActionId = "del-big2", Path = "big2",
            ExpectedTarget = snapshot.Items.Single(item => item.Path == "big2"),
            FrozenVictimSignature = FolderDelete.Signature(FolderDelete.LiveDescendants(rig.Store, RootId, "big2"))
        };
        rig.H.Wire.FailFeedAfterFolderDelete = true;
        await rig.ApplyAsync(rig.Plan(secondDelete) with { ScanCursorB = snapshot.CursorAfter });
        var pending = rig.Store.UnresolvedIntents("p1");
        var anchor = pending.Single(action => action.ProviderEffect?.Kind == "whole-folder-group");
        Require(anchor.Path == "big2" && pending.Any(action => action.Path == "big" &&
                action.ProviderEffect?.Kind == "whole-folder-member"),
            "two deleted folders share one uncertain group with big2 as its anchor");
        var pair = rig.Store.Pair("p1")! with { Excluded = ["big2"] };
        rig.Store.SavePair(pair);
        rig.Store.MarkFirstSyncDone("p1");
        rig.H.Wire.FailFeedAfterFolderDelete = false;
        var provider = new FakeEndpointProvider().Add(pair.A, rig.A)
            .AddInventory(pair.B, rig.H.Inventory).AddWriter(pair.B, rig.B);
        var settings = V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };
        var run = new SyncRun(rig.Store, new Executor(rig.Store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage),
            provider, settings, TimeProvider.System);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var report = await run.IncrementalAsync("p1", new Progress<RunProgress>(), CancellationToken.None);
            Require(report.Failed == 0 && report.Failures.Count == 0 &&
                    rig.Store.UnresolvedIntents("p1").Count == 0,
                "both runs succeed when only the shared group's anchor folder is excluded");
        }
        Require(rig.H.Wire.WholeFolderDeletes == 2,
            "both folders reconcile without replaying either whole-folder delete");
    }

    private static async Task FolderTreeResumeAddedFileAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.SeedFile("big/sub/late.txt", "added after the frozen scan");
        rig.H.Wire.FailFeedAfterFolderDelete = true;
        await rig.ApplyAsync(rig.Plan());
        rig.H.Wire.FailFeedAfterFolderDelete = false;
        var executor = new Executor(rig.Store, V2Settings.Default, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage);
        var results = await executor.ReconcileAsync(rig.Store.UnresolvedIntents("p1"), 2, rig.A, rig.B, CancellationToken.None);
        Require(rig.H.Wire.Restores.SequenceEqual(["big/sub/late.txt"]) &&
            await rig.B.ObserveAsync("big/sub/late.txt", CancellationToken.None) is { Kind: ItemKind.File } &&
            results.Single(result => result.ActionId == "del-big").Outcome == Outcome.Skipped,
            "restart recovery restores a post-scan addition outside the frozen victim set and preserves its parents");
        Require(rig.Store.OpenNotices("p1").Any(notice => notice.ReasonKey == ExecutionReasons.FolderDeleteRestored.Key &&
                notice.Detail.Contains("big/sub/late.txt", StringComparison.Ordinal)) &&
            rig.Store.UnresolvedIntents("p1").Count == 0 && rig.H.Wire.WholeFolderDeletes == 1,
            "the added-file recovery notice persists and deletion is never replayed");
    }

    private static async Task FolderTreeResumePartialCommitAsync()
    {
        using var rig = await TreeRigAsync(60);
        var latest = rig.H.Wire.EditFile("big/f0.txt", "edited after scan, before deletion");
        rig.H.Wire.SeedChangePage(rig.Cursor, [rig.H.Wire.ChangeOf("big/f0.txt")], false, "recovery-middle");
        rig.H.Wire.FolderDeleteFeedCursor = "recovery-middle";
        rig.H.Wire.FailFeedAfterFolderDelete = true;
        await rig.ApplyAsync(rig.Plan());
        var first = rig.Store.UnresolvedIntents("p1").Single(action => action.Path == "big/f2.txt");
        // Model a process stopping after one member commit but before the anchor commit.
        rig.Store.CommitResult("p1", 2, first, new ActionResult(first.ActionId, Outcome.Done, null, null, null, null));
        var intents = rig.Store.UnresolvedIntents("p1");
        Require(intents.Count == 61 && intents.Any(action => action.ProviderEffect?.Kind == "whole-folder-group"),
            "partial member commits retain the entire frozen group on its anchor");
        rig.H.Wire.FailFeedAfterFolderDelete = false;
        var executor = new Executor(rig.Store, V2Settings.Default, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage);
        var results = await executor.ReconcileAsync(intents, 3, rig.A, rig.B, CancellationToken.None);
        Require(results.Single(result => result.ActionId == "del-big:big/f0.txt").Outcome == Outcome.Skipped &&
            (await rig.B.ObserveAsync("big/f0.txt", CancellationToken.None))?.VersionId == latest,
            "durable pre-read evidence restores an edited victim even after partial result commits");
        Require(rig.Store.OpenNotices("p1").Any(notice => notice.ReasonKey == ExecutionReasons.FolderDeleteRestored.Key &&
                notice.Detail.Contains("big/f0.txt", StringComparison.Ordinal)) &&
            rig.Store.UnresolvedIntents("p1").Count == 0 && rig.H.Wire.WholeFolderDeletes == 1,
            "restoration is noticed before the surviving anchor resolves, without another delete");
    }

    private static async Task FolderTreePendingJobRecoveryAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.DeleteBatchAsyncJob = true;
        rig.H.Wire.FailFeedAfterFolderDelete = true;
        await rig.ApplyAsync(rig.Plan());
        var anchor = rig.Store.UnresolvedIntents("p1").Single(action => action.ProviderEffect?.Kind == "whole-folder-group");
        var group = JsonNode.Parse(anchor.ProviderEffect!.Id)!.AsObject();
        var provider = JsonNode.Parse(group["ProviderState"]!.GetValue<string>())!.AsObject();
        Require(provider["JobId"]?.GetValue<string>() == "delete-job-1", "async identity is durably checkpointed");
        // Model the durable state at a poll timeout/crash: job id known, terminal checkpoint not yet written.
        provider["Completed"] = null;
        group["ProviderState"] = provider.ToJsonString();
        rig.Store.RecordIntent("p1", anchor with { ProviderEffect = anchor.ProviderEffect with { Id = group.ToJsonString() } });
        rig.H.Wire.DeleteBatchPendingChecks = 1;
        rig.H.Wire.FailFeedAfterFolderDelete = false;
        var executor = new Executor(rig.Store, V2Settings.Default, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage);
        var pending = await executor.ReconcileAsync(rig.Store.UnresolvedIntents("p1"), 2, rig.A, rig.B, CancellationToken.None);
        Require(pending.All(result => result.Outcome == Outcome.Uncertain), "a still-running job never resolves from absence");
        var complete = await executor.ReconcileAsync(rig.Store.UnresolvedIntents("p1"), 3, rig.A, rig.B, CancellationToken.None);
        Require(complete.All(result => result.Outcome == Outcome.Done) && rig.H.Wire.WholeFolderDeletes == 1,
            "terminal job evidence allows shared feed recovery without resubmitting");
    }

    private static async Task FolderTreeUnknownGraceAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.FailFeedAfterFolderDelete = true;
        await rig.ApplyAsync(rig.Plan());
        var anchor = rig.Store.UnresolvedIntents("p1").Single(action => action.ProviderEffect?.Kind == "whole-folder-group");
        var group = JsonNode.Parse(anchor.ProviderEffect!.Id)!.AsObject();
        var provider = JsonNode.Parse(group["ProviderState"]!.GetValue<string>())!.AsObject();
        provider["Completed"] = null;
        provider["JobId"] = null;
        provider["Middle"] = "unknown-grace";
        provider["SubmittedAt"] = DateTimeOffset.UtcNow;
        group["ProviderState"] = provider.ToJsonString();
        rig.Store.RecordIntent("p1", anchor with { ProviderEffect = anchor.ProviderEffect with { Id = group.ToJsonString() } });
        rig.H.Wire.FailFeedAfterFolderDelete = false;
        rig.H.Wire.SeedChangePage("unknown-grace", [], false, "unknown-grace-end");
        var executor = new Executor(rig.Store, V2Settings.Default, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage);
        var pending = await executor.ReconcileAsync(rig.Store.UnresolvedIntents("p1"), 2, rig.A, rig.B, CancellationToken.None);
        Require(pending.All(result => result.Outcome == Outcome.Uncertain),
            "no-job missing deletion evidence remains uncertain during the minimum two-hour grace");
        provider["SubmittedAt"] = DateTimeOffset.UtcNow - TimeSpan.FromHours(3);
        group["ProviderState"] = provider.ToJsonString();
        rig.Store.RecordIntent("p1", anchor with { ProviderEffect = anchor.ProviderEffect with { Id = group.ToJsonString() } });
        var ended = await executor.ReconcileAsync(rig.Store.UnresolvedIntents("p1"), 3, rig.A, rig.B, CancellationToken.None);
        Require(ended.All(result => result.Outcome == Outcome.Skipped) && rig.Store.UnresolvedIntents("p1").Count == 0,
            "owner bounded-wait policy: after grace unconfirmed victims are skipped for replan, not assumed deleted");
    }

    private static async Task FolderTreeUnknownAcceptanceAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.FailFeedAfterFolderDelete = true;
        await rig.ApplyAsync(rig.Plan());
        var anchor = rig.Store.UnresolvedIntents("p1").Single(action => action.ProviderEffect?.Kind == "whole-folder-group");
        var group = JsonNode.Parse(anchor.ProviderEffect!.Id)!.AsObject();
        var provider = JsonNode.Parse(group["ProviderState"]!.GetValue<string>())!.AsObject();
        // Model a crash after HTTP acceptance but before its response/job checkpoint became durable.
        provider["Completed"] = null;
        provider["JobId"] = null;
        group["ProviderState"] = provider.ToJsonString();
        rig.Store.RecordIntent("p1", anchor with
        {
            ProviderEffect = anchor.ProviderEffect with { Id = group.ToJsonString() },
        });
        rig.H.Wire.FailFeedAfterFolderDelete = false;
        var metadata = rig.H.Wire.Calls.Count(call => call == "/2/files/get_metadata");
        var executor = new Executor(rig.Store, V2Settings.Default, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage);
        var results = await executor.ReconcileAsync(rig.Store.UnresolvedIntents("p1"), 2, rig.A, rig.B, CancellationToken.None);
        Require(results.All(result => result.Outcome == Outcome.Done) &&
            rig.Store.UnresolvedIntents("p1").Count == 0 && rig.H.Wire.WholeFolderDeletes == 1 &&
            // The shared change feed verifies the root once, never each of the 62 victims.
            rig.H.Wire.Calls.Count(call => call == "/2/files/get_metadata") == metadata + 1,
            "owner policy: unknown acceptance with every victim tombstoned resumes the group post-check");
    }

    private static async Task FolderTreeCheckpointDamageAsync()
    {
        foreach (var code in new[] { 11, 26, 5 })
        {
            using var rig = await TreeRigAsync(1);
            var fault = new IOException("wrapped checkpoint", new Microsoft.Data.Sqlite.SqliteException("synthetic", code));
            var writes = 0;
            Exception? observed = null;
            IReadOnlyList<FolderTreeOutcome>? results = null;
            // Completed group, no saved post-feed: the first checkpoint must precede all recovery effects.
            var state = new JsonObject
            {
                ["Before"] = new JsonArray(), ["Middle"] = rig.Cursor,
                ["Completed"] = new JsonArray(JsonValue.Create(true))
            }.ToJsonString();
            try
            {
                results = await rig.B.ReconcileFolderTreesAsync(rig.Cursor,
                    [new FolderTree(rig.Delete.ExpectedTarget!, [])], state,
                    _ => { writes++; throw fault; }, default);
            }
            catch (Exception error) { observed = error; }
            Require(writes == 1 && rig.H.Wire.WholeFolderDeletes == 0,
                "failed checkpoint causes no subsequent checkpoint or destructive effect");
            Require(code == 5 ? observed is null && results!.All(row => row.Status == FolderTreeStatus.Uncertain)
                : observed is not null && StorageFailureClassifier.ScopeOf(observed, default) == FailureRetryOwner.FailureScope.State,
                "whole-folder wrapped corruption propagates; wrapped busy retains uncertain handling");
        }
    }
}
