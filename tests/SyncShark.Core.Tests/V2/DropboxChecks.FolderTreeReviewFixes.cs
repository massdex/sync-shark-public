using System.Text.Json.Nodes;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task FolderTreeReviewFixChecksAsync()
    {
        await FolderTreeRefusalAsync(false);
        await FolderTreeRefusalAsync(true);
        foreach (var terminal in new[] { "failed", "invalid_async_job_id", "expired_async_job_id" })
        {
            await FolderTreeUnavailableJobAsync(terminal, true);
            await FolderTreeUnavailableJobAsync(terminal, false);
        }
        foreach (var error in new[] { "not_file", "restricted_content", "not_found" })
            await FolderTreeUnknownPathErrorAsync(error);
        await FolderTreeRestoreBatchMarkersAsync();
        await FolderTreeRestoreMarkerFailureAsync();
        await FolderTreeDifferentRevisionAsync();
        await FolderTreeFallbackHistoryAsync();
        await FolderTreePauseAsync();
        await FolderRestoreSafetyChecksAsync();
    }

    private static async Task FolderTreeRefusalAsync(bool partialEffects)
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.RefuseFolderDelete = !partialEffects;
        rig.H.Wire.FailFolderResultAfterDelete = partialEffects;
        if (partialEffects) rig.H.Wire.SeedFile("big/late.txt", "late content");
        var results = await rig.ApplyAsync(rig.Plan());
        Require(results.All(result => result.Outcome != Outcome.Uncertain) &&
            rig.Store.UnresolvedIntents("p1").Count == 0,
            "a refused folder resolves without an endless reconciliation stall");
        if (partialEffects)
            Require(rig.H.Wire.Restores.SequenceEqual(["big/late.txt"]) &&
                results.Count(result => result.Outcome == Outcome.Done) == 61,
                "a failure entry does not bypass feed-proven deletion or unexpected-file restoration");
        else
            Require(results.All(result => result.Outcome == Outcome.Skipped) &&
                rig.Store.OpenNotices("p1").Any(notice => notice.ReasonKey == "folder-delete-refused"),
                "unconfirmed victims of a refused folder are skipped with a notice");
    }

    private static async Task FolderTreeUnavailableJobAsync(string terminal, bool tombstones)
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.FailFeedAfterFolderDelete = true;
        await rig.ApplyAsync(rig.Plan());
        var anchor = rig.Store.UnresolvedIntents("p1").Single(action => action.ProviderEffect?.Kind == "whole-folder-group");
        var group = JsonNode.Parse(anchor.ProviderEffect!.Id)!.AsObject();
        var provider = JsonNode.Parse(group["ProviderState"]!.GetValue<string>())!.AsObject();
        provider["Completed"] = null;
        provider["JobId"] = "lost-job";
        var submittedAt = DateTimeOffset.UtcNow;
        provider["SubmittedAt"] = submittedAt;
        if (!tombstones)
        {
            provider["Middle"] = "unavailable-middle";
            rig.H.Wire.SeedChangePage("unavailable-middle", [], false, "unavailable-end");
        }
        group["ProviderState"] = provider.ToJsonString();
        rig.Store.RecordIntent("p1", anchor with { ProviderEffect = anchor.ProviderEffect with { Id = group.ToJsonString() } });
        rig.H.Wire.FailFeedAfterFolderDelete = false;
        rig.H.Wire.DeleteJobTerminal = terminal;
        var executor = new Executor(rig.Store, V2Settings.Default, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
        var results = await executor.ReconcileAsync(rig.Store.UnresolvedIntents("p1"), 2, rig.A, rig.B, CancellationToken.None);
        if (tombstones)
            Require(results.All(result => result.Outcome == Outcome.Done),
                terminal + " immediately uses complete tombstone evidence");
        else
        {
            Require(results.All(result => result.Outcome == Outcome.Uncertain),
                terminal + " still waits for the no-job grace with missing evidence");
            anchor = rig.Store.UnresolvedIntents("p1").Single(action => action.ProviderEffect?.Kind == "whole-folder-group");
            group = JsonNode.Parse(anchor.ProviderEffect!.Id)!.AsObject();
            provider = JsonNode.Parse(group["ProviderState"]!.GetValue<string>())!.AsObject();
            Require(provider["JobId"]?.GetValue<string>() == "lost-job" &&
                provider["JobUnavailable"]?.GetValue<bool>() == true &&
                provider["SubmittedAt"]?.GetValue<DateTimeOffset>() == submittedAt,
                "unavailable job retains its durable identity and submission age without remaining pollable");
            provider["SubmittedAt"] = DateTimeOffset.UtcNow - TimeSpan.FromHours(3);
            group["ProviderState"] = provider.ToJsonString();
            rig.Store.RecordIntent("p1", anchor with { ProviderEffect = anchor.ProviderEffect with { Id = group.ToJsonString() } });
            var checks = rig.H.Wire.Calls.Count(call => call == "/2/files/delete_batch/check");
            results = await executor.ReconcileAsync(rig.Store.UnresolvedIntents("p1"), 3, rig.A, rig.B, CancellationToken.None);
            Require(results.All(result => result.Outcome == Outcome.Skipped) &&
                rig.H.Wire.Calls.Count(call => call == "/2/files/delete_batch/check") == checks,
                "expired no-job grace resolves skipped without polling the unusable id again");
        }
        Require(rig.H.Wire.WholeFolderDeletes == 1, "unavailable job recovery never replays deletion");
    }

    private static async Task FolderTreeUnknownPathErrorAsync(string error)
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.BeforeBuryFolder = () => rig.H.Wire.SeedFolder("big/late-empty");
        rig.H.Wire.UnknownRevisionPathError = error;
        var results = await rig.ApplyAsync(rig.Plan());
        Require(results.All(result => result.Outcome != Outcome.Uncertain) &&
            rig.Store.UnresolvedIntents("p1").Count == 0 &&
            results.Single(result => result.ActionId == "del-big").Outcome == Outcome.Skipped &&
            rig.Store.OpenNotices("p1").Any(notice => notice.Detail.Contains("type unknown", StringComparison.Ordinal)),
            "unknown tombstone path/" + error + " is recoverable-not-restored, never retried");
    }

    private static async Task FolderTreeRestoreBatchMarkersAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.SeedFile("big/late-a.txt", "a");
        rig.H.Wire.SeedFile("big/late-b.txt", "b");
        var tree = new FolderTree(rig.Delete.ExpectedTarget!, FolderDelete.LiveDescendants(rig.Store, RootId, "big")) { RevalidateDelete = () => Task.FromResult(true) };
        var markerWrites = 0;
        rig.H.Wire.BeforeRestore = () => Require(markerWrites == 1,
            "all folder restore markers are durable together before the first and subsequent restores");
        var results = await rig.B.DeleteFolderTreesAsync(rig.Cursor, [tree], (_, state) =>
        {
            var json = JsonNode.Parse(state)!;
            if (json["RestorePending"] is JsonArray paths)
            {
                markerWrites++;
                Require(paths.Count == 2, "one marker checkpoint contains both restore paths");
            }
            return Task.CompletedTask;
        }, CancellationToken.None);
        Require(markerWrites == 1 && results.Single().Restored.Count == 2, "one checkpoint per folder, not per restored path");
    }

    private static async Task FolderTreeRestoreMarkerFailureAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.SeedFile("big/late-a.txt", "a");
        rig.H.Wire.SeedFile("big/late-b.txt", "b");
        var tree = new FolderTree(rig.Delete.ExpectedTarget!, FolderDelete.LiveDescendants(rig.Store, RootId, "big")) { RevalidateDelete = () => Task.FromResult(true) };
        string? durable = null;
        var failed = await rig.B.DeleteFolderTreesAsync(rig.Cursor, [tree], (_, state) =>
        {
            if (JsonNode.Parse(state)!["RestorePending"] is JsonArray)
                throw new IOException("offline checkpoint failure");
            durable = state;
            return Task.CompletedTask;
        }, CancellationToken.None);
        Require(failed.Single().Status == FolderTreeStatus.Uncertain && rig.H.Wire.Restores.Count == 0,
            "a failed marker checkpoint prevents every restore effect in that folder");
        var resumed = await rig.B.ReconcileFolderTreesAsync(rig.Cursor, [tree], durable!,
            _ => Task.CompletedTask, CancellationToken.None);
        Require(resumed.Single().Status == FolderTreeStatus.Completed && rig.H.Wire.Restores.Count == 2 &&
            rig.H.Wire.WholeFolderDeletes == 1, "marker failure resumes without replaying deletion");
    }

    private static async Task FolderTreeDifferentRevisionAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.SeedFile("big/late.txt", "late");
        rig.H.Wire.RestoreDifferentRevision = true;
        var results = await rig.ApplyAsync(rig.Plan());
        var notices = rig.Store.OpenNotices("p1").Where(notice => notice.Detail.Contains("big/late.txt", StringComparison.Ordinal)).ToArray();
        Require(notices.Length == 1 && notices[0].ReasonKey == ExecutionReasons.FolderDeleteUnrestored.Key &&
            results.Single(result => result.ActionId == "del-big").Outcome == Outcome.Skipped,
            "a differing restored revision has one inspection notice and still protects its parent");
    }

    private static Task FolderTreePauseAsync() => FolderRestoreColdReopenAsync("parent-read-pause");

    private static async Task FolderTreeFallbackHistoryAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.SeedChangePage(rig.Cursor,
            [rig.H.Wire.ChangeDeleted("/Pilot/big")], false, "replaced-middle");
        await rig.ApplyAsync(rig.Plan());
        Require(rig.H.Wire.WholeFolderDeletes == 0 &&
            !rig.Store.OpenNotices("p1").Any(notice => notice.ReasonKey == "folder-delete-history"),
            "a candidate rejected by the pre-read closes its unsubmitted history warning");
    }
}
