using System.Text.Json.Nodes;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task FolderRestoreSafetyChecksAsync()
    {
        foreach (var mode in new[] { "rejected", "pre-send", "lost-response", "lost-response-again", "lost-response-live", "confirmed-then-deleted" })
            await FolderRestoreColdReopenAsync(mode);
        await FolderRestoreColdReopenAsync("lost-response-again", legacyProvider: true);
        await UnknownFolderRestoreProtectsChildrenAsync();
        await PermanentRestoreRejectionAsync();
        await OccupiedFolderRestoreAsync();
    }

    private static async Task OccupiedFolderRestoreAsync()
    {
        foreach (var folderOccupant in new[] { false, true })
        {
            using var rig = await TreeRigAsync(60);
            rig.H.Wire.SeedFolder("big/late");
            rig.H.Wire.SeedChangePage(rig.Cursor, [rig.H.Wire.ChangeOf("big/late")], false, "occupied-middle");
            rig.H.Wire.FolderDeleteFeedCursor = "occupied-middle";
            rig.H.Wire.BeforeMetadataResponse = path =>
            {
                if (rig.H.Wire.WholeFolderDeletes == 0 || path != DropboxWire.RootId + "/big/late") return;
                if (folderOccupant) rig.H.Wire.SeedFolder("big/late");
                else rig.H.Wire.SeedFile("big/late", "occupant stays");
            };
            var creates = rig.H.Wire.Calls.Count(c => c == "/2/files/create_folder_v2");
            var results = await rig.ApplyAsync(rig.Plan());
            Require(results.All(r => r.Outcome != Outcome.Uncertain) && rig.Store.UnresolvedIntents("p1").Count == 0 &&
                rig.H.Wire.Calls.Count(c => c == "/2/files/create_folder_v2") == creates,
                "occupied folder restore target settles without recreating or replacing it");
            Require(folderOccupant || rig.Store.OpenNotices("p1").Any(n => n.ReasonKey == "folder-delete-unrestored" &&
                n.Detail.Contains("path now in use", StringComparison.Ordinal)), "file occupant gets an unrestored notice");
        }
    }

    private static async Task PermanentRestoreRejectionAsync()
    {
        foreach (var status in new[] { System.Net.HttpStatusCode.Conflict, System.Net.HttpStatusCode.Forbidden,
                     System.Net.HttpStatusCode.BadRequest })
        {
            using var rig = await TreeRigAsync(60);
            rig.H.Wire.SeedFile("big/late.txt", "late");
            rig.H.Wire.RejectRestore = true;
            rig.H.Wire.RestoreRejectionStatus = status;
            var results = await rig.ApplyAsync(rig.Plan());
            Require(results.All(r => r.Outcome != Outcome.Uncertain) &&
                rig.Store.UnresolvedIntents("p1").Count == 0 && rig.H.Wire.Restores.Count == 0,
                "permanent restore rejection settles without replay or loss of history");
            Require(rig.Store.OpenNotices("p1").Any(n => n.ReasonKey == "folder-delete-unrestored" &&
                n.Detail.Contains("restore rejected", StringComparison.Ordinal)),
                "permanent rejection leaves an actionable unrestored notice");
        }
    }

    private static async Task UnknownFolderRestoreProtectsChildrenAsync()
    {
        using var rig = await TreeRigAsync(60);
        var folder = Path.Combine(_root, "unknown-folder-restore");
        var settings = V2Settings.Default;
        var store = SqliteStateStore.Open(folder, settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        try
        {
            store.SavePair(rig.Store.Pair("p1")!);
            var gen = store.BeginGeneration(RootId, "snapshot");
            store.WriteSnapshot(new Snapshot(RootId, Side.B, gen, rig.Cursor,
                FolderDelete.LiveDescendants(rig.Store, RootId, "big").Append(rig.Delete.ExpectedTarget!).ToArray(), []));
            store.CompleteGeneration(gen, rig.Cursor, true);
            rig.H.Wire.SeedFolder("big/late");
            rig.H.Wire.SeedFile("big/late/child.txt", "late child");
            rig.H.Wire.SeedChangePage(rig.Cursor,
                [rig.H.Wire.ChangeOf("big/late"), rig.H.Wire.ChangeOf("big/late/child.txt")], false, "late-middle");
            rig.H.Wire.FolderDeleteFeedCursor = "late-middle";
            rig.H.Wire.LoseCreatedFolderResponse = "big/late";
            var executor = new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
            await foreach (var result in executor.ApplyAsync(rig.Plan() with { Gen = gen }, rig.A, rig.B, default))
                Require(result.Outcome == Outcome.Uncertain, "unknown folder recreation retains group");
            Require(rig.H.Wire.Restores.Count == 0, "child restore cannot indirectly replay its unknown parent");
            store.Dispose();
            store = SqliteStateStore.Open(folder, settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            rig.H.Wire.LoseCreatedFolderResponse = null;
            executor = new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
            var ended = await executor.ReconcileAsync(store.UnresolvedIntents("p1"), gen, rig.A, rig.B, default);
            Require(ended.All(r => r.Outcome == Outcome.Uncertain) && rig.H.Wire.Restores.Count == 0 &&
                await rig.B.ObserveAsync("big/late", default) is null,
                "cold verification never resurrects an unknown restored folder through its children");
        }
        finally { store.Dispose(); }
    }

    private static async Task FolderRestoreColdReopenAsync(string mode, bool legacyProvider = false)
    {
        using var rig = await TreeRigAsync(60);
        var folder = Path.Combine(_root, "restore-safety-" + mode + (legacyProvider ? "-legacy-provider" : ""));
        var settings = V2Settings.Default with { AutoRetryDelay = TimeSpan.FromHours(1) };
        var store = SqliteStateStore.Open(folder, settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        try
        {
            var savedPair = rig.Store.Pair("p1")!;
            if (legacyProvider) savedPair = savedPair with { B = savedPair.B with { Provider = "Dropbox" } };
            store.SavePair(savedPair);
            var gen = store.BeginGeneration(RootId, "snapshot");
            store.WriteSnapshot(new Snapshot(RootId, Side.B, gen, rig.Cursor,
                FolderDelete.LiveDescendants(rig.Store, RootId, "big").Append(rig.Delete.ExpectedTarget!).ToArray(), []));
            store.CompleteGeneration(gen, rig.Cursor, true);
            var writer = new DropboxWriter(Root(), Side.B, rig.H.Client, Path.Combine(_root, "spool"), isStateDamage: StorageFailureClassifier.IsStateDamage, settings);
            var executor = new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
            rig.H.Wire.SeedFile("big/late-a.txt", "recoverable content");
            if (mode == "confirmed-then-deleted") rig.H.Wire.SeedFile("big/late-b.txt", "second content");
            rig.H.Wire.BeforeBuryFolder = () => Require(store.OpenNotices("p1").Count(n =>
                n.ReasonKey == "folder-delete-history") == 1, "SQLite history warning precedes submission");
            if (mode == "rejected") rig.H.Wire.RejectRestore = true;
            if (mode == "pre-send") rig.H.Wire.AfterBuriedRevisionRead = () =>
                rig.H.Wire.MetadataFailurePath = DropboxWire.RootId + "/big";
            if (mode.StartsWith("lost-response", StringComparison.Ordinal))
            {
                rig.H.Wire.LoseRestoreResponse = true;
                if (mode is "lost-response" or "lost-response-again")
                    rig.H.Wire.AfterRestore = () => rig.H.Wire.DeleteRestoredFile("big/late-a.txt");
            }
            if (mode == "confirmed-then-deleted") rig.H.Wire.AfterRestore = () =>
            {
                rig.H.Wire.DeleteRestoredFile("big/late-a.txt");
                rig.H.Wire.RejectRestore = true;
            };
            var pauseRequested = false;
            if (mode == "parent-read-pause")
                rig.H.Wire.AfterBuriedRevisionRead = () => rig.H.Wire.BeforeMetadataResponse = path =>
                {
                    // The occupied-target read completes normally. The NEXT read is the production
                    // parent-folder preflight, whose PairPause.Check throws before transport.
                    if (path == DropboxWire.RootId + "/big/late-a.txt") pauseRequested = true;
                };
            var first = new List<ActionResult>();
            var paused = false;
            using (PairPause.Begin(store, "p1", () => pauseRequested))
            {
                try
                {
                    await foreach (var result in executor.ApplyAsync(rig.Plan() with { Gen = gen }, rig.A, writer, default))
                        first.Add(result);
                }
                catch (PairPausedException) { paused = true; }
            }
            Require(mode == "parent-read-pause" ? paused && first.Count == 0 :
                first.All(result => result.Outcome == Outcome.Uncertain), mode + " retains unresolved group");
            Require(mode != "parent-read-pause" || !rig.H.Wire.Calls.Contains("/2/files/restore"),
                "parent preflight pause never sends a restore");
            Require(mode != "pre-send" || !rig.H.Wire.Calls.Contains("/2/files/restore"),
                "pre-send failure never reaches files/restore");
            var saved = ProviderState(store);
            var retryPath = mode == "confirmed-then-deleted" ? "big/late-b.txt" : "big/late-a.txt";
            Require(mode.StartsWith("lost-response", StringComparison.Ordinal)
                    ? saved["RestoreStarted"]?[retryPath] is not null
                    : saved["RestoreStarted"]?[retryPath] is null && saved["RestoreRetryAfter"]?[retryPath] is not null,
                mode + " persists the correct submission classification");
            if (mode == "confirmed-then-deleted")
                Require(saved["Restored"]!.AsArray().Any(n => n!.GetValue<string>() == "big/late-a.txt"),
                    "confirmed restoration remains durably settled even after user deletion");
            store.Dispose();
            store = SqliteStateStore.Open(folder, settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            rig.H.Wire.RejectRestore = false;
            rig.H.Wire.LoseRestoreResponse = false;
            rig.H.Wire.AfterRestore = null;
            rig.H.Wire.AfterBuriedRevisionRead = null;
            rig.H.Wire.MetadataFailurePath = null;
            rig.H.Wire.BeforeMetadataResponse = null;
            executor = new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
            var calls = rig.H.Wire.Calls.Count(call => call == "/2/files/restore");
            var waiting = await executor.ReconcileAsync(store.UnresolvedIntents("p1"), gen, rig.A, writer, default);
            if (mode == "lost-response-live")
            {
                Require(waiting.All(result => result.Outcome != Outcome.Uncertain) &&
                    store.UnresolvedIntents("p1").Count == 0 && rig.H.Wire.Restores.Count == 1,
                    "matching observed revision verifies a lost response without replay");
                return;
            }
            Require(waiting.All(result => result.Outcome == Outcome.Uncertain) &&
                rig.H.Wire.Calls.Count(call => call == "/2/files/restore") == calls,
                mode + " cold reopen neither replays unknown restore nor ignores retry due time");
            Require(store.OpenNotices("p1").Count(n => n.ReasonKey == "folder-delete-unrestored") == 1,
                "repeated verification updates one deduplicated recovery notice");
            var notice = store.OpenNotices("p1").Single(n => n.ReasonKey == "folder-delete-unrestored");
            Require(NoticeChoices.IsRetryNotice(notice) && NoticeOptions.Options(notice).Select(o => o.Choice)
                .Order().SequenceEqual(new[] { NoticeChoices.RetryNow, NoticeChoices.RestoreAgain, NoticeChoices.Ignore, NoticeChoices.OpenDropbox }.Order()),
                "shared GUI/CLI choices provide verification, history and protected ignore");
            if (mode == "lost-response-again")
            {
                var revisionReads = rig.H.Wire.Calls.Count(call => call == "/2/files/list_revisions");
                var pair = store.Pair("p1")!;
                Require(pair.B.Provider == (legacyProvider ? "Dropbox" : ProviderIds.Dropbox),
                    "cold reopen preserves the provider spelling being tested");
                var endpoints = new FakeEndpointProvider().Add(pair.A, rig.A)
                    .AddInventory(pair.B, rig.H.Inventory).AddWriter(pair.B, writer);
                var run = new SyncRun(store, executor, endpoints, settings, TimeProvider.System);
                await run.ApplyRetryNoticeChoiceAsync("p1", notice.NoticeId!, NoticeChoices.RestoreAgain, default);
                Require(store.OpenNotices("p1").Any(n => n.NoticeId == notice.NoticeId),
                    "explicit authorization does not prematurely close the notice");
                store.Dispose();
                store = SqliteStateStore.Open(folder, settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
                executor = new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
                var explicitResult = await executor.ReconcileAsync(store.UnresolvedIntents("p1"), gen, rig.A, writer, default);
                Require(explicitResult.All(r => r.Outcome != Outcome.Uncertain) && rig.H.Wire.Restores.Count == 2 &&
                    rig.H.Wire.WholeFolderDeletes == 1 && rig.H.Wire.Calls.Count(call => call == "/2/files/list_revisions") == revisionReads &&
                    !store.OpenNotices("p1").Any(n => n.ReasonKey == "folder-delete-unrestored"),
                    "shared Restore again survives cold reopen, restores once and closes the resolved notice");
                return;
            }
            if (mode == "lost-response")
            {
                Require(await writer.ObserveAsync("big/late-a.txt", default) is null &&
                    store.UnresolvedIntents("p1").Any(a => a.ProviderEffect?.Kind == "whole-folder-group"),
                    "absent Started restore is never invented success");
                var run = new SyncRun(store, executor, new FakeEndpointProvider(), settings, TimeProvider.System);
                Require(run.TryApplyImmediateNoticeChoice("p1", notice.NoticeId!, NoticeChoices.Ignore, default),
                    "Ignore uses the shared notice authority");
                store.Dispose();
                store = SqliteStateStore.Open(folder, settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
                executor = new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
                await executor.ReconcileAsync(store.UnresolvedIntents("p1"), gen, rig.A, writer, default);
                Require(store.Pair("p1")!.Excluded.Count == 0 && store.UnresolvedIntents("p1").Count > 0 &&
                    !store.OpenNotices("p1").Any(n => n.ReasonKey == "folder-delete-unrestored") &&
                    rig.H.Wire.Calls.Count(call => call == "/2/files/restore") == calls,
                    "ignored unknown restore stays protected and silent after another cold reopen");
                store.AddNotices([notice with { FolderDeleteEpisode = "next-folder-delete", FolderRecoveryAnchor = null }]);
                Require(store.OpenNotices("p1").Any(n => n.NoticeId == notice.NoticeId &&
                    n.FolderDeleteEpisode == "next-folder-delete"),
                    "a later whole-folder deletion reopens the same ignored path notice");
                return;
            }
            // Advance only this fixture's durable due timestamp, without sleeping or changing production time.
            var anchor = store.UnresolvedIntents("p1").Single(a => a.ProviderEffect?.Kind == "whole-folder-group");
            var group = JsonNode.Parse(anchor.ProviderEffect!.Id)!;
            var provider = JsonNode.Parse(group["ProviderState"]!.GetValue<string>())!;
            provider["RestoreRetryAfter"]![retryPath] = DateTimeOffset.MinValue;
            group["ProviderState"] = provider.ToJsonString();
            store.RecordIntent("p1", anchor with { ProviderEffect = anchor.ProviderEffect with { Id = group.ToJsonString() } });
            store.Dispose();
            store = SqliteStateStore.Open(folder, settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            executor = new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
            var ended = await executor.ReconcileAsync(store.UnresolvedIntents("p1"), gen, rig.A, writer, default);
            Require(ended.All(result => result.Outcome != Outcome.Uncertain) && store.UnresolvedIntents("p1").Count == 0 &&
                rig.H.Wire.WholeFolderDeletes == 1 && await writer.ObserveAsync(retryPath, default) is not null,
                mode + " retries known no-effect restore once due without replaying deletion");
            if (mode == "confirmed-then-deleted")
                Require(rig.H.Wire.Restores.Count(p => p == "big/late-a.txt") == 1 &&
                    await writer.ObserveAsync("big/late-a.txt", default) is null,
                    "user deletion after confirmed restore is respected after cold reopen");
        }
        finally { store.Dispose(); }
    }

    private static JsonNode ProviderState(SqliteStateStore store)
    {
        var anchor = store.UnresolvedIntents("p1").Single(a => a.ProviderEffect?.Kind == "whole-folder-group");
        return JsonNode.Parse(JsonNode.Parse(anchor.ProviderEffect!.Id)!["ProviderState"]!.GetValue<string>())!;
    }
}
