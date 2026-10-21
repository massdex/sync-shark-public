namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task QuarantineHistoryAsync(string root)
    {
        QuarantineSnapshotRenameChecks(root);
        await QuarantineBlockedFolderRunAsync(root);
        await QuarantineReusedNameSyncAsync(root);
        await QuarantineRehashHoldAsync(root, false);
        await QuarantineRehashHoldAsync(root, true);
        await QuarantineFolderFullScanAsync(root);
        using var store = SqliteStateStore.Open(Path.Combine(root, "history"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("folder/child.txt", "keep child");
        fx.A.Seed("blocked.txt", "history bytes");
        await FirstSyncAsync(fx.Run(), fx);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(Path.Combine(root, "history"))))
        {
            connection.Open();
            using var prune = connection.CreateCommand();
            prune.CommandText = "DELETE FROM generation;"; // isolated fixture: sync_point intentionally outlives generation history
            prune.ExecuteNonQuery();
        }
        await QuarantineCandidatePagingAsync(root);
        var known = store.Live("ra", ["blocked.txt"]).Single();
        Require(store.QuarantineBaselineItems("ra").Any(i => i.ObjectId == known.ObjectId), "baseline existence does not require generation time");
        var localRoot = fx.Pair.A with { RootPath = "C:\\fake-history", RootObjectId = "id:local:00000001:0000000000000001" };
        var history = new FakeDefenderHistory();
        var inventory = new LocalInventory(localRoot, Side.A, store, new HashSet<string>(), Settings, TimeProvider.System, history);
        var empty = new PilotLocalScan(new Dictionary<string, PilotLocalEntry>(), [], 0);
        history.Detections = [new("file:_C:\\fake-history\\blocked.txt", DateTimeOffset.MinValue)];
        var scan = await inventory.ApplyHistoryAsync(empty, _ => true, default);
        Require(scan.Decisions.Single().DefenderHistoryMatch, "old exact detection still protects missing synced entity");
        history.Detections = [new("file:_C:\\elsewhere\\blocked.txt", DateTimeOffset.UtcNow)];
        scan = await inventory.ApplyHistoryAsync(empty, _ => true, default);
        Require(!scan.Decisions.Any(d => d.IsQuarantine), "history requires exact contained resource path");
        history.Detections = [new("file:_C:\\fake-history\\blocked.txt", DateTimeOffset.UtcNow)];
        scan = await inventory.ApplyHistoryAsync(empty, _ => true, default);
        Require(scan.Decisions.Single().DefenderHistoryMatch && scan.Decisions.Single().QuarantineCode is null,
            "exact history classifies honestly without inventing native225");
        var gen = store.BeginGeneration("ra", "fake-history-scan");
        store.WriteSnapshot(inventory.BuildSnapshot(gen, "ra", "", scan, p => p == "blocked.txt"));
        Require(store.Live("ra", ["blocked.txt"]).Single().Flags.HasFlag(ItemFlags.Quarantined), "history persists entity hold before disappearance");
        Require(store.OpenNotices("p1").Single(n => n.Path == "blocked.txt").Kind == NoticeKind.UserDecision,
            "inventory quarantine notice has stable UserDecision kind");
        history.Fail = true;
        scan = await inventory.ApplyHistoryAsync(empty, _ => true, default);
        Require(scan.Decisions.Count > 0 && scan.Decisions.All(d => d.DefenderHistoryUnknown && d.QuarantineCode is null), "unavailable history holds missing baseline entities honestly as UNKNOWN");
        Require(history.Calls == 4, "one bounded history call per inventory pass");
        Require(DefenderHistory.Parse("{\"Resource\":\"file:_C:\\\\fake-history\\\\blocked.txt\",\"DetectedAt\":\"2026-10-19T00:00:00Z\"}").Count == 1,
            "single Defender detection JSON object is not silently discarded");

        var calls = history.Calls;
        await inventory.ApplyHistoryAsync(empty, _ => true, default, []);
        Require(history.Calls == calls, "untouched delta makes no Defender query");
        await QuarantineHistoryStreamAsync();
        await QuarantineHistoryDeletionSafetyAsync(root);
        await QuarantineDeferralExpiryAsync(root);
        await QuarantineDeferralReviewAsync(root);
        await QuarantineDeferredFolderAsync(root);
        await QuarantineHeldNoticeAsync(root);
        await QuarantinePruneAsync(root);

        using var guard = new LocalItems.RefreshGuard();
        guard.RecordFailure("folder", new IOException("fake ancestor open", new System.ComponentModel.Win32Exception(226)));
        guard.RecordFailure("acl-folder", new System.ComponentModel.Win32Exception(5));
        Require(guard.Quarantine.Count == 1 && guard.Quarantine[0].QuarantineCode == 226,
            "refresh ancestor guard preserves225/226 without classifying ACL denial");
        var folderScan = empty with { Decisions = [new PilotDecision("folder", PilotDecisionKind.Blocked, "fake folder open225") { QuarantineCode = 225 }] };
        gen = store.BeginGeneration("ra", "fake-folder-scan");
        store.WriteSnapshot(inventory.BuildSnapshot(gen, "ra", "", folderScan, p => p.StartsWith("folder")));
        Require(store.Live("ra", ["folder/child.txt"]).Count == 1, "quarantined folder preserves unobserved baselined descendants");
        var delta = inventory.BuildDelta(gen, "ra", "", "", folderScan, ["folder"], _ => true, DateTimeOffset.UtcNow, []);
        Require(!delta.Changes.Any(c => c.Kind == ChangeKind.Delete), "quarantined folder scan produces no descendant tombstones");
        Require(!LinkExclusions.Of(store, fx.Pair, false).NarrowAction(_ => true)("folder"),
            "held descendant/folder prevents enclosing destructive action");
        var folder = store.Live("ra", ["folder"]).Single();
        store.ClearQuarantine("ra", folder);
        Require(store.Live("ra", ["folder/child.txt"]).Count == 1, "releasing folder hold cannot expose false deletion from failed scan");
    }

    // RQ-034: exercise the real full-run publisher, then release and reobserve the subtree.
    private static async Task QuarantineBlockedFolderRunAsync(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "blocked-folder-run"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("held", "", ItemKind.Folder);
        fx.A.Seed("held/child.txt", "must survive");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var child = store.Live("ra", ["held/child.txt"]).Single();
        var inventory = new LocalInventory(fx.Pair.A with { RootPath = "F:/fixture", RootObjectId = "id:local:00000001:0000000000000001" },
            Side.A, store, new HashSet<string>(), Settings, TimeProvider.System);
        var blocked = new BlockedFolderInventory(inventory, fx.A);
        fx.Provider.AddInventory(fx.Pair.A, blocked);
        await run.IncrementalAsync("p1", new RecordingProgress(), default);
        Require(blocked.FullScans == 1 && store.Live("ra", [child.Path]).Single().ObjectId == child.ObjectId &&
            store.Tombstones("ra").All(t => t.Path != child.Path),
            "full-run blocked folder preserves child identity without a tombstone");
        var held = store.Live("ra", ["held"]).Single();
        Require(held.Flags.HasFlag(ItemFlags.Quarantined), "full-run publication keeps held folder itself included");
        store.ClearQuarantine("ra", held);
        foreach (var notice in store.OpenNotices("p1").Where(n => n.Quarantine is not null))
            store.CloseNotice(notice.NoticeId!, NoticeChoices.QuarantineRetry);
        blocked.Blocked = false;
        await run.IncrementalAsync("p1", new RecordingProgress(), default);
        Require(blocked.FullScans == 2 && await fx.B.ObserveAsync(child.Path, default) is not null &&
            store.Live("ra", [child.Path]).Single().ObjectId == child.ObjectId &&
            store.Tombstones("ra").All(t => t.Path != child.Path) &&
            !store.Recoveries("p1").Any(r => r.Side == Side.B),
            "release and full rescan never delete the held folder's children on Dropbox");
    }

    private sealed class BlockedFolderInventory(LocalInventory inventory, IInventorySource source) : IInventorySource
    {
        internal bool Blocked = true;
        internal int FullScans;
        public Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct)
        {
            FullScans++;
            return Blocked ? Task.FromResult(inventory.BuildSnapshot(gen, "ra", "", new PilotLocalScan(
                new Dictionary<string, PilotLocalEntry>(),
                [new PilotDecision("held", PilotDecisionKind.Blocked, "blocked folder listing") { QuarantineCode = 225 }], 0), include))
                : source.FullAsync(gen, include, p, ct);
        }
        public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
            Task.FromResult(new Delta("ra", Side.A, gen, cursor, cursor, [], true, []));
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) => source.HashAsync(paths, ct);
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct) => source.DeletionTimesAsync(paths, ct);
    }

    private static async Task QuarantineCheckpointAsync(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "checkpoint"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.B.Seed("blocked.txt", "checkpoint bytes");
        fx.B.Seed("sibling.txt", "sibling bytes");
        var writer = new QuarantineWriter(fx.A, "write", 226) { Checkpoint = true };
        fx.Provider.AddWriter(fx.Pair.A, writer);
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        Require(store.FirstSyncDone("p1"), "durable quarantined checkpoint does not block first-sync completion");
        var pending = store.UnresolvedIntents("p1").Single();
        Require(pending.ProviderEffect?.Id == "fake-checkpoint", "quarantine retains checkpoint evidence");
        var attempts = writer.Attempts;
        await run.IncrementalAsync("p1", new RecordingProgress(), default);
        Require(writer.Attempts == attempts && store.UnresolvedIntents("p1").Count == 1,
            "ordinary run neither retries nor discards quarantined checkpoint");
        var notice = store.OpenNotices("p1").Single(n => n.Quarantine is not null);
        var deleted = await run.ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.QuarantineDeleteDropbox, default);
        Require(deleted.Outcome == Outcome.Skipped && await fx.B.ObserveAsync("blocked.txt", default) is not null,
            "competing delete cannot bypass interrupted checkpoint verification");
        await run.ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.QuarantineIgnore, default);
        await run.IncrementalAsync("p1", new RecordingProgress(), default);
        Require(store.UnresolvedIntents("p1").Count == 1 && writer.Attempts == attempts,
            "ignore suppresses notice without losing checkpoint or enabling retry");
    }

    private static async Task QuarantineCandidatePagingAsync(string root)
    {
        var data = Path.Combine(root, "candidate-pages");
        using var store = SqliteStateStore.Open(data, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("folder/child.txt", "page seed");
        await FirstSyncAsync(fx.Run(), fx);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(data)))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TEMP TABLE page_numbers(n INTEGER);
                WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<1100)
                INSERT INTO page_numbers SELECT x FROM n;
                INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,version_id,hash,size,mtime,flags,identity_json)
                SELECT f.root_no,f.dir_no,'PAGE-'||n.n,'page-'||n.n,f.kind,'page-id-'||n.n,f.version_id,f.hash,f.size,f.mtime,f.flags,f.identity_json
                FROM file_state f CROSS JOIN page_numbers n WHERE f.root_no=(SELECT root_no FROM root WHERE root_id='ra') AND f.name='child.txt';
                INSERT INTO sync_point(pair_no,object_key,gen,kind,hash,size,a_dir,a_name,a_name_key,a_object_id,a_version_id,a_mtime,b_dir,b_name,b_name_key,b_object_id,b_version_id,b_mtime)
                SELECT s.pair_no,'page-key-'||n.n,s.gen,s.kind,s.hash,s.size,s.a_dir,'page-'||n.n,'PAGE-'||n.n,'page-id-'||n.n,s.a_version_id,s.a_mtime,
                s.b_dir,s.b_name,s.b_name_key,'cloud-page-id-'||n.n,s.b_version_id,s.b_mtime
                FROM sync_point s CROSS JOIN page_numbers n WHERE s.a_name='child.txt';
                """;
            command.ExecuteNonQuery();
            command.CommandText = "EXPLAIN QUERY PLAN SELECT f.name FROM file_state f WHERE f.root_no=1 AND f.dir_no=1 AND (f.dir_no,f.name_key,f.name)>(0,'','') AND EXISTS (SELECT 1 FROM sync_point s WHERE s.pair_no=1 AND s.a_object_id=f.object_id) ORDER BY f.dir_no,f.name_key,f.name LIMIT 512;";
            var plans = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) plans.Add(reader.GetString(3));
            Require(plans.Any(p => p.Contains("PRIMARY KEY")) && plans.Any(p => p.Contains("sp_a_obj")) && !plans.Any(p => p.Contains("TEMP B-TREE")),
                "candidate page uses file primary key and baseline identity index without temporary sort");
        }
        var items = store.QuarantineBaselineItems("ra", ["folder", "folder/child.txt", "folder"]).ToArray();
        Require(items.Length == 1101 && items.Select(i => i.ObjectId).Distinct().Count() == 1101,
            "covered directory streams more than two pages exactly once despite overlapping scopes");
        Require(!store.QuarantineBaselineItems("ra", ["untouched"]).Any(), "uncovered delta scope does not enumerate unrelated baseline items");
    }

    private static async Task QuarantineHistoryDeletionSafetyAsync(string root)
    {
        foreach (var mode in new[] { "unavailable", "partial", "empty" })
        {
            using var store = SqliteStateStore.Open(Path.Combine(root, "history-delete-" + mode), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var item = fx.A.Seed("blocked.txt", "surviving cloud bytes");
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            await fx.A.DeleteAsync(new SyncAction("fake-missing", 30, ActionKind.Delete, Side.A, item.Path, null, null, item, false, null), default);
            var history = new FakeDefenderHistory { Fail = mode == "unavailable", Complete = mode != "partial" };
            var localRoot = fx.Pair.A with { RootPath = @"C:\fake-history", RootObjectId = "id:local:00000001:0000000000000001" };
            var inventory = new LocalInventory(localRoot, Side.A, store, new HashSet<string>(), Settings, TimeProvider.System, history);
            var empty = new PilotLocalScan(new Dictionary<string, PilotLocalEntry>(), [], 0);
            var scan = await inventory.ApplyHistoryAsync(empty, _ => true, default);
            Require(mode == "empty" ? scan.Decisions.Count == 0 : scan.Decisions.Count == 1 && scan.Decisions[0].DefenderHistoryUnknown,
                mode + " history has exact expected missing-baseline disposition");
            var gen = store.BeginGeneration("ra", "history-safety");
            store.WriteSnapshot(inventory.BuildSnapshot(gen, "ra", "", scan, _ => true));
            store.CompleteGeneration(gen, "", true);
            fx.Provider.AddInventory(fx.Pair.A, new MissingHistoryInventory(inventory, fx.A, item.Path));
            await run.IncrementalAsync("p1", new RecordingProgress(), default);
            Require(!store.Live("ra", [item.Path]).Any(i => i.Flags.HasFlag(ItemFlags.Quarantined)),
                mode + " UNKNOWN is automatic deferral, never a permanent quarantine flag");
            Require((await fx.B.ObserveAsync(item.Path, default) is not null) == (mode != "empty"),
                mode + " history next planner/apply " + (mode == "empty" ? "permits ordinary deletion" : "preserves cloud copy"));
            if (mode != "empty")
            {
                Require(store.DefenderDeferredPaths("ra", 512).Contains(item.Path), "deferred entity is queued without new watch events");
                history.Fail = false;
                history.Complete = true;
                store.RequestSyncRetryNow("p1", item.Path); // Explicit Retry after access/history recovers, not the sleeping backoff.
                await run.IncrementalAsync("p1", new RecordingProgress(), default);
                Require(await fx.B.ObserveAsync(item.Path, default) is null && store.DefenderDeferredPaths("ra", 512).Count == 0,
                    "complete no-match recheck releases automatic deferral and propagates deletion");
            }
        }
    }

    private static async Task QuarantineDeferralExpiryAsync(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "deferral-expiry"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("missing.txt", "preserve until evidence");
        await FirstSyncAsync(fx.Run(), fx);
        var history = new FakeDefenderHistory { Complete = false, Availability = DefenderAvailability.Active };
        var inventory = new LocalInventory(fx.Pair.A with { RootPath = @"C:\fake-history", RootObjectId = "id:local:00000001:0000000000000001" },
            Side.A, store, new HashSet<string>(), Settings, TimeProvider.System, history);
        var empty = new PilotLocalScan(new Dictionary<string, PilotLocalEntry>(), [], 0);
        for (var attempt = 0; attempt < 4; attempt++)
            Require((await inventory.ApplyHistoryAsync(empty, _ => true, default)).Decisions.Single().DefenderHistoryUnknown,
                "active Defender never releases incomplete history merely because attempt bound expires");
        history.Availability = DefenderAvailability.Unknown;
        Require((await inventory.ApplyHistoryAsync(empty, _ => true, default)).Decisions.Single().DefenderHistoryUnknown,
            "failed status query or timeout is not proof Defender is inactive");
        history.Availability = DefenderAvailability.Inactive;
        Require((await inventory.ApplyHistoryAsync(empty, _ => true, default)).Decisions.Count == 0 &&
            store.OpenNotices("p1").Count(n => n.ReasonKey == "defender-history-unavailable") == 1,
            "expired deferral plus confirmed inactive status releases with one aggregated notice");
        history.Detections = [new(@"file:_C:\fake-history\missing.txt", default)];
        Require((await inventory.ApplyHistoryAsync(empty, _ => true, default)).Decisions.Single().DefenderHistoryMatch,
            "inactive partial history still preserves exact positive evidence");
        var now = DateTimeOffset.UtcNow;
        Require(!store.DeferDefenderHistory(["counter"], "run1", now).Contains("counter") &&
            !store.DeferDefenderHistory(["counter"], "run1", now).Contains("counter") &&
            !store.DeferDefenderHistory(["counter"], "run1", now).Contains("counter") &&
            !store.DeferDefenderHistory(["counter"], "run2", now).Contains("counter") &&
            store.DeferDefenderHistory(["counter"], "run3", now).Contains("counter"),
            "entity counter increments once per run, including overlapping scopes");
        store.DeferDefenderHistory(["age-counter"], "run1", now);
        Require(store.DeferDefenderHistory(["age-counter"], "run2", now.AddHours(25)).Contains("age-counter"),
            "24-hour automatic deferral expiry is independent of attempt count");
    }

    private static async Task QuarantineDeferralReviewAsync(string root)
    {
        await QuarantineParallelReviewsAsync(root);
        await QuarantineSharedHeldAsync(root);
        foreach (var mode in new[] { "unknown", "active", "timeout", "cap", "age" })
        {
            using var store = SqliteStateStore.Open(Path.Combine(root, "deferral-review-" + mode), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            store.SavePair(fx.Pair with { A = fx.Pair.A with { RootObjectId = "id:local:00000001:0000000000000001" } });
            var item = fx.A.Seed("missing.txt", "cloud survives until explicit consent");
            var later = fx.A.Seed("later.txt", "deleted after the keep choice");
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            await fx.A.DeleteAsync(new SyncAction("missing", 30, ActionKind.Delete, Side.A, item.Path, null, null, item, false, null), default);
            var history = new FakeDefenderHistory { Complete = false, Fail = mode == "timeout", LimitReached = mode == "cap",
                Availability = mode == "active" ? DefenderAvailability.Active : DefenderAvailability.Unknown };
            var localRoot = fx.Pair.A with { RootPath = @"C:\fake-history", RootObjectId = "id:local:00000001:0000000000000001" };
            var historyStore = System.Reflection.DispatchProxy.Create<IStateStore, FolderHistoryStore>();
            var historyAudit = (FolderHistoryStore)(object)historyStore;
            historyAudit.Inner = store;
            historyAudit.ForbidDescendants = false;
            var inventory = new LocalInventory(localRoot, Side.A, historyStore, new HashSet<string>(), Settings, TimeProvider.System, history);
            var weekLater = new LocalInventory(localRoot, Side.A, store, new HashSet<string>(), Settings,
                new ShiftedClock(DefenderReview.KeepRecheck + TimeSpan.FromDays(1)), history);
            var empty = new PilotLocalScan(new Dictionary<string, PilotLocalEntry>(), [], 0);
            // later.txt is synced before this pass (a later case needs its baseline row), but it is still
            // present here: the first review must aggregate only the deleted missing.txt.
            var laterPresent = new PilotLocalScan(new Dictionary<string, PilotLocalEntry>
            {
                [later.Path] = new(later.Path, PilotEntryKind.File, later.Path, 0, default, null)
            }, [], 0);
            if (mode == "age") store.DeferDefenderHistory([item.ObjectId], "old-pass", DateTimeOffset.UtcNow.AddHours(-25));
            for (var i = 0; i < (mode is "cap" or "age" ? 1 : 3); i++) await inventory.ApplyHistoryAsync(laterPresent, _ => true, default);
            var notice = store.OpenNotices("p1").SingleOrDefault(n => n.DefenderHistory is not null);
            Require(notice?.Kind == NoticeKind.UserDecision && notice.DefenderHistory?.Count == 1 &&
                NoticeChoices.Options(notice).Select(o => o.Choice).SequenceEqual(new[] { NoticeChoices.DefenderProceed, NoticeChoices.DefenderHold }),
                mode + " bounded history creates one aggregate app decision with proceed and keep choices");
            var reviewed = notice!;
            await inventory.ApplyHistoryAsync(laterPresent, _ => true, default);
            Require(store.OpenNotices("p1").Single(n => n.DefenderHistory is not null) == reviewed,
                "aggregate retry keeps reviewed membership token stable");
            var runtime = new V2CliRuntime(null, store, run, new EndpointRootProbe(fx.Provider), root);
            var host = new V2SyncHost(store, run, new ObservedExecutor(fx.Executor), null!, Settings, TimeProvider.System, SqliteStateStore.DatabasePath(root));
            await host.ResolveConflictSelectionCommandAsync([new(new(reviewed.NoticeId!, "p1", "", 0),
                SyncAuthorityConflictResolutionKind.PreserveBoth, NoticeChoices.DefenderHold)]);
            var held = store.OpenNotices("p1").SingleOrDefault(n => n.DefenderHistory?.Cause == DefenderReview.HeldCause)!;
            Require(store.NoticeById("p1", reviewed.NoticeId!)!.Value.Closed && held?.DefenderHistory is { Count: 1 } &&
                NoticeChoices.Options(held).Select(o => o.Choice).SequenceEqual(new[] { NoticeChoices.DefenderReleaseHeld,
                    NoticeChoices.DefenderReleaseItem, NoticeChoices.DefenderExcludeHeld }) &&
                (await inventory.ApplyHistoryAsync(laterPresent, _ => true, default)).Decisions.Single().DefenderHistoryUnknown,
                mode + " actual app keep-held resolves the review into one standing held notice; the deletion stays protected");
            var calls = history.Calls;
            await inventory.ApplyHistoryAsync(laterPresent, _ => true, default);
            Require(history.Calls == calls && store.DefenderDeferredPaths("ra", 512).Count == 0,
                "Keep held costs no Defender history query and no rotation slot on every run");
            history.Complete = true;
            history.Fail = false;
            Require((await weekLater.ApplyHistoryAsync(empty, _ => true, default)).Decisions.Single().DefenderHistoryUnknown &&
                history.Calls == calls + 1,
                "explicit Keep held remains effective even after a complete no-match query");
            history.Complete = false;
            history.Fail = mode == "timeout";
            await fx.A.DeleteAsync(new SyncAction("later", 30, ActionKind.Delete, Side.A, later.Path, null, null, later, false, null), default);
            // A standing choice may carry an old root stamp after rebinding. It is not an unresolved
            // review to close/reopen while later missing items receive their own review.
            store.AddNotices([held! with { DefenderHistory = held!.DefenderHistory! with
                { ReviewedRoot = localRoot with { RootPath = @"C:\old-held-root" } } }]);
            for (var i = 0; i < 3; i++) await inventory.ApplyHistoryAsync(empty, _ => true, default);
            Notice Review() => store.OpenNotices("p1").SingleOrDefault(n => n.DefenderHistory is { Cause: not DefenderReview.HeldCause })!;
            var second = Review();
            Require(second?.NoticeId != reviewed.NoticeId && second?.DefenderHistory!.Count == 1 &&
                !historyAudit.ClosedNotices.Contains(held!.NoticeId!) &&
                store.OpenNotices("p1").Single(n => n.DefenderHistory?.Cause == DefenderReview.HeldCause).DefenderHistory!.Count == 1,
                mode + " Keep held never blocks later deletions: they get their own review without closing or reopening the standing held notice");
            store.DeferDefenderHistory(["later-entity"], "later-run", DateTimeOffset.UtcNow);
            await runtime.ResolveConflictAsync("p1", "", NoticeChoices.DefenderProceed, second!.NoticeId, 0, null, default);
            var consent = store.DefenderDeferralConsent(["later-entity", item.ObjectId, later.ObjectId]);
            Require(consent["later-entity"] == DefenderConsent.Pending && consent[item.ObjectId] == DefenderConsent.Keep &&
                consent[later.ObjectId] == DefenderConsent.Proceed,
                "bulk consent excludes entities outside reviewed membership, including Keep-held ones");
            history.Fail = false;
            history.Detections = [new(@"file:_C:\fake-history\later.txt", default)];
            Require((await inventory.ApplyHistoryAsync(empty, _ => true, default)).Decisions.Single(d => d.RelativePath == later.Path).DefenderHistoryMatch &&
                !store.DefenderDeferralConsent([later.ObjectId]).ContainsKey(later.ObjectId),
                "bulk proceed never overrides positive evidence and revokes its absence epoch");
            history.Detections = [];
            for (var i = 0; i < 3; i++) await inventory.ApplyHistoryAsync(empty, _ => true, default);
            var next = Review();
            Require(next.NoticeId != second.NoticeId && store.DefenderDeferralConsent([later.ObjectId])[later.ObjectId] == DefenderConsent.Pending,
                "new absence epoch cannot inherit old consent or stale notice key");
            await runtime.ResolveConflictAsync("p1", item.Path, NoticeChoices.DefenderReleaseItem, held!.NoticeId, 0, null, default);
            Require(store.DefenderDeferralConsent([item.ObjectId])[item.ObjectId] == DefenderConsent.Proceed &&
                store.NoticeById("p1", held.NoticeId!)!.Value.Closed,
                "CLI release of one held item makes it Proceed and closes the emptied standing notice");
            fx.Provider.AddInventory(fx.Pair.A, new MissingHistoryInventory(inventory, fx.A, item.Path));
            await run.IncrementalAsync("p1", new RecordingProgress(), default);
            Require(await fx.B.ObserveAsync(item.Path, default) is null,
                "a released Keep-held deletion propagates through actual planner and executor");
            Require(store.Recoveries("p1").Any(r => r.Path == item.Path && r.Side == Side.B) &&
                await fx.B.ObserveAsync(later.Path, default) is not null,
                "released full-scan deletion retains Recovery while the unrelated pending review stays protected");
        }
        foreach (var (mode, expected) in new[] { ("Passive Mode", DefenderAvailability.Inactive), ("Not running", DefenderAvailability.Inactive),
            ("SxS Passive Mode", DefenderAvailability.Unknown), ("EDR Block Mode", DefenderAvailability.Unknown) })
        {
            var availability = DefenderAvailability.Active;
            await DefenderHistory.ReadStreamAsync(new StringReader("STATUS:" + mode + "\nCOMPLETE\n"), [], default, status: value => availability = value);
            Require(availability == expected, "Defender mode " + mode + " is " + expected +
                (expected == DefenderAvailability.Unknown ? ": it can still remediate, so it never releases a deferral automatically" : ""));
        }
        Require(DefenderHistory.AvailabilityForMode("future unknown mode") == DefenderAvailability.Unknown,
            "unrecognized status is never inactivity proof");
        var evidence = new List<DefenderDetection>();
        var limited = false;
        await DefenderHistory.ReadStreamAsync(new StringReader("xxxxxxxx"), evidence, default, maxChars: 2, limitReached: () => limited = true);
        Require(limited, "byte cap explicitly requests immediate aggregate review");
        limited = false;
        var resource = System.Text.Json.JsonSerializer.Serialize(new { Resource = @"file:_C:\fake-history\x.txt" }) + "\n";
        await DefenderHistory.ReadStreamAsync(new StringReader(resource + resource), evidence, default, maxRecords: 1, limitReached: () => limited = true);
        Require(limited, "entry cap explicitly requests immediate aggregate review");

        using var queueStore = SqliteStateStore.Open(Path.Combine(root, "deferral-queue"), Settings, TimeProvider.System);
        var queueFx = Build(suppliedStore: queueStore);
        for (var i = 0; i < 7; i++) queueFx.A.Seed("q" + i + ".txt", "keep");
        await FirstSyncAsync(queueFx.Run(), queueFx);
        var items = queueStore.QuarantineBaselineItems("ra").ToArray();
        queueStore.DeferDefenderHistory(items.Select(i => i.ObjectId).ToArray(), "queue", DateTimeOffset.UtcNow.AddMinutes(-1));
        var queued = new LocalInventory(queueFx.Pair.A with { RootPath = @"C:\fake-history", RootObjectId = "id:local:00000001:0000000000000001" }, Side.A, queueStore, new HashSet<string>(), Settings with { LocalDeltaMaxPaths = 3 }, TimeProvider.System, new FakeDefenderHistory { Complete = false });
        var visited = new HashSet<string>();
        for (var i = 0; i < 4; i++)
        {
            var paths = queued.WithDefenderDeferredPaths(["watched.txt"], "ra");
            Require(paths.Count <= 3 && paths.Contains("watched.txt"), "deferral backlog stays within remaining delta budget without full rescan");
            foreach (var path in paths.Where(p => p != "watched.txt")) visited.Add(path);
        }
        Require(visited.Count == 7 && queueStore.Live("ra", items.Select(i => i.Path).ToArray()).Count == 7,
            "bounded queue rotates even unprocessed paths and omitted entities remain protected in inventory");
        var covered = queued.WithDefenderDeferredPaths([], "ra");
        var queuedScan = await queued.ApplyHistoryAsync(new PilotLocalScan(new Dictionary<string, PilotLocalEntry>(), [], 0), _ => true, default, covered);
        var queuedGen = queueStore.BeginGeneration("ra", "bounded-refresh");
        queueStore.WriteDelta(queued.BuildDelta(queuedGen, "ra", "", "", queuedScan, covered, _ => true, DateTimeOffset.UtcNow, []));
        Require(queueStore.Live("ra", items.Select(i => i.Path).ToArray()).Count == 7,
            "actual bounded delta preserves both selected UNKNOWN and omitted backlog entities");
        var bulkIds = Enumerable.Range(0, 513).Select(i => "bulk-deferral-" + i).ToArray();
        queueStore.DeferDefenderHistory(bulkIds, "bulk", DateTimeOffset.UtcNow);
        Require(queueStore.DefenderDeferralConsent(bulkIds).Count == bulkIds.Length,
            "bulk consent lookup spans the bounded SQL page boundary");
        queueStore.ClearDefenderDeferrals(bulkIds);
        Require(queueStore.DefenderDeferralConsent(bulkIds).Count == 0 &&
            queueStore.DefenderDeferralConsent(items.Select(i => i.ObjectId).ToArray()).Count == items.Length,
            "set-based clearing removes only requested deferrals and preserves unrelated queued entities");
        var appeared = items[0];
        queueStore.AssignDefenderBatch([appeared.ObjectId], "presence-epoch");
        queueStore.DecideDefenderBatch("presence-epoch", true, "ra");
        var present = new PilotLocalScan(new Dictionary<string, PilotLocalEntry>
        {
            [appeared.Path] = new(appeared.Path, PilotEntryKind.File, "C:/fake-history/" + appeared.Path, appeared.Size,
                DateTime.UtcNow, appeared.Hash, 1, 999)
        }, [], 0);
        await queued.ApplyHistoryAsync(present, _ => true, default, [appeared.Path]);
        Require(!queueStore.DefenderDeferralConsent([appeared.ObjectId]).ContainsKey(appeared.ObjectId),
            "observed presence or replacement revokes prior absence-epoch consent");
        foreach (var choice in new[] { NoticeChoices.DefenderProceed, NoticeChoices.DefenderHold, NoticeChoices.DefenderKeep })
        {
            var args = new[] { "--cli", "conflicts", "resolve", "--pair", "p1", "--path", "", "--key", "notice", "--generation", "0", "--choice", choice };
            try { SyncCliRegistry.Parse(args); throw new InvalidOperationException("missing --yes accepted"); } catch (SyncCliUsageException) { }
            SyncCliRegistry.Parse([.. args, "--yes"]);
        }
        Require(true, "new aggregate and keep-exclusion CLI choices require explicit --yes");
        foreach (var priorVersion in new[] { StoreSchema.Version - 1, StoreSchema.Version })
        {
            var migrationRoot = Path.Combine(root, "quarantine-consent-upgrade-" + priorVersion);
            using (var before = SqliteStateStore.Open(migrationRoot, Settings, TimeProvider.System))
                before.DeferDefenderHistory(["retained-epoch"], "before", DateTimeOffset.UtcNow);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(migrationRoot)))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DROP INDEX defender_deferral_batch; ALTER TABLE defender_deferral DROP COLUMN batch; ALTER TABLE defender_deferral DROP COLUMN consent; PRAGMA user_version=" + priorVersion + ";";
                command.ExecuteNonQuery();
            }
            using (var after = SqliteStateStore.Open(migrationRoot, Settings, TimeProvider.System))
            {
                after.AssignDefenderBatch(["retained-epoch"], "upgraded");
                Require(after.DefenderBatchCount("upgraded") == 1 && after.DefenderDeferralConsent(["retained-epoch"])["retained-epoch"] == 0,
                    "preview " + priorVersion + " gains consent metadata without losing prior deferrals");
            }
            using (var again = SqliteStateStore.Open(migrationRoot, Settings, TimeProvider.System))
                Require(again.DefenderBatchCount("upgraded") == 1, "quarantine consent upgrade is idempotent on reopen");
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(migrationRoot)))
            {
                connection.Open();
                StoreSchema.Verify(connection);
                Require((long)StoreSchema.Scalar(connection, null, "SELECT COUNT(*) FROM pragma_table_info('defender_deferral') WHERE name IN ('consent','batch');")! == 2,
                    "interrupted or stamped preview " + priorVersion + " verifies after reopening with both columns present");
            }
        }
    }

    private static async Task QuarantineHistoryStreamAsync()
    {
        var resource = @"file:_C:\fake-history\" + "\u05d0\u05d1.zip->member.exe";
        var json = System.Text.Json.JsonSerializer.Serialize(new { Resource = resource });
        var evidence = new List<DefenderDetection>();
        Require(await DefenderHistory.ReadStreamAsync(new StringReader(json + "\nCOMPLETE\n"), evidence, default) && evidence.Count == 1,
            "Unicode streamed evidence and explicit completion survive roundtrip");
        Require(DefenderHistory.RelativePath(@"C:\fake-history", evidence[0].Resource) == "\u05d0\u05d1.zip", "archive evidence maps to exact container");
        foreach (var text in new[] { json + "\n", json + "\nBROKEN\nCOMPLETE\n", "[" + json + ",{}]\nCOMPLETE\n" })
        {
            evidence.Clear();
            Require(!await DefenderHistory.ReadStreamAsync(new StringReader(text), evidence, default) && evidence.Count == 1,
                "partial malformed stream retains positive evidence and reports UNKNOWN");
        }
        evidence.Clear();
        Require(!await DefenderHistory.ReadStreamAsync(new StringReader(json + "\n" + json + "\nCOMPLETE\n"), evidence, default, maxRecords: 1) && evidence.Count == 1,
            "record bound is partial not empty or complete");
        Require(DefenderHistory.RelativePath(@"\\?\C:\fake-history", @"file:_\\?\C:\fake-history\x.txt") == "x.txt" &&
            DefenderHistory.RelativePath(@"\\?\UNC\server\share", @"file:_\\?\UNC\server\share\x.txt") == "x.txt" &&
            DefenderHistory.RelativePath(@"C:\fake-history", @"file:_C:\fake-history-sibling\x.txt") is null,
            "extended drive and UNC resources preserve exact root boundary");
        var run = new object();
        var completion = new TaskCompletionSource<DefenderHistoryResult>();
        var count = 0;
        Task<DefenderHistoryResult> Query() { count++; return completion.Task; }
        using var cancel = new CancellationTokenSource();
        var first = DefenderHistory.ReadCachedAsync(run, Query, cancel.Token);
        cancel.Cancel();
        try { await first; throw new Exception("cancelled history wait survived"); } catch (OperationCanceledException) { }
        var next = DefenderHistory.ReadCachedAsync(run, Query, default);
        completion.SetResult(new([], true));
        Require((await next).Complete && count == 1, "first caller cancellation does not poison shared history query");
    }

    // Isolate the synthetic inventory case from the broader suite's native scanning/journal checks.
    public static async Task QuarantineFolderFullScanChecksAsync(string root)
    {
        await QuarantineFolderFullScanAsync(root);
        await QuarantineDeleteChoiceAuthorityAsync(root);
    }

    private static async Task QuarantineFolderFullScanAsync(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "folder-full-scan"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("dir", "", ItemKind.Folder);
        foreach (var name in new[] { "inbatch", "outside", "kept" }) fx.A.Seed("dir/" + name + ".txt", "full scan " + name);
        fx.A.Seed("held", "", ItemKind.Folder);
        fx.A.Seed("held/child.txt", "protected by parent coverage");
        fx.A.Seed("clear", "", ItemKind.Folder);
        fx.A.Seed("clear/released.txt", "released in a different subtree");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var rows = store.QuarantineBaselineItems("ra").ToDictionary(i => i.Path, StringComparer.Ordinal);
        foreach (var row in rows.Values.OrderByDescending(r => r.Path.Length))
            await fx.A.DeleteAsync(new SyncAction("gone-" + row.Path, 30, ActionKind.Delete, Side.A, row.Path, null, null, row, false, null), default);
        store.DeferDefenderHistory(rows.Values.Select(r => r.ObjectId).ToArray(), "full-scan", DateTimeOffset.UtcNow);
        store.AssignDefenderBatch([rows["dir"].ObjectId, rows["dir/inbatch.txt"].ObjectId,
            rows["held/child.txt"].ObjectId, rows["clear"].ObjectId, rows["clear/released.txt"].ObjectId], "full-folder");
        store.DecideDefenderBatch("full-folder", true, "ra");
        store.AssignDefenderBatch([rows["dir/kept.txt"].ObjectId, rows["held"].ObjectId], "full-kept");
        store.DecideDefenderBatch("full-kept", false, "ra");
        var inventory = new LocalInventory(fx.Pair.A with { RootPath = @"C:\fake-history", RootObjectId = "id:local:00000001:0000000000000001" },
            Side.A, store, new HashSet<string>(), Settings, TimeProvider.System, new FakeDefenderHistory { Complete = false });
        var full = new FullScanHistoryInventory(inventory, fx.A);
        fx.Provider.AddInventory(fx.Pair.A, full);
        var cursorBefore = store.Cursor("ra");
        Require(await fx.WriterA.IsSourceAbsentAsync(rows["clear"], "clear", default) == true &&
            await fx.WriterA.IsSourceAbsentAsync(rows["clear/released.txt"], "clear/released.txt", default) == true,
            "released subtree's real baseline source identities and paths are freshly absent");
        var report = await run.IncrementalAsync("p1", new RecordingProgress(), default);
        Require(full.FullScans > 0 && await fx.B.ObserveAsync("dir/outside.txt", default) is not null &&
            await fx.B.ObserveAsync("dir/kept.txt", default) is not null && await fx.B.ObserveAsync("dir", default) is not null,
            "full-scan folder Proceed never deletes an out-of-batch or Keep-held child from Dropbox (actual planner and executor)");
        var released = await fx.B.ObserveAsync("clear/released.txt", default);
        var heldFolder = await fx.B.ObserveAsync("held", default);
        var heldChild = await fx.B.ObserveAsync("held/child.txt", default);
        var recoveries = store.Recoveries("p1").ToArray();
        var diagnostic = System.Text.Json.JsonSerializer.Serialize(new
        {
            Released = released, HeldFolder = heldFolder, HeldChild = heldChild, Recoveries = recoveries,
            CursorBefore = cursorBefore, CursorAfter = store.Cursor("ra"), Report = report,
            Retry = store.ReadSyncRetries("p1"), Notices = store.OpenNotices("p1"),
            SourceReleased = await fx.A.ObserveAsync("clear/released.txt", default)
        });
        Require(heldFolder is not null && heldChild is not null &&
            !recoveries.Any(r => PairPath.IsSelfOrUnder(r.Path, "held")),
            "held subtree and its Proceed child stay untouched; " + diagnostic);
        Require(store.Cursor("ra") == cursorBefore,
            "incomplete quarantine-history cursor does not advance; " + diagnostic);
        Require(released is null && recoveries.Any(r => r.Path == "clear/released.txt" && r.Side == Side.B),
            "complete sibling subtree propagates released deletion with Recovery; " + diagnostic);
    }

    private static async Task QuarantineParallelReviewsAsync(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "parallel-reviews"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var items = new[] { fx.A.Seed("first.txt", "first"), fx.A.Seed("later.txt", "later") };
        await FirstSyncAsync(fx.Run(), fx);
        foreach (var item in items)
            await fx.A.DeleteAsync(new SyncAction("remove-" + item.Path, 30, ActionKind.Delete, Side.A, item.Path, null, null, item, false, null), default);
        var inventory = new LocalInventory(fx.Pair.A with { RootObjectId = "id:local:00000001:0000000000000001" },
            Side.A, store, new HashSet<string>(), Settings, TimeProvider.System,
            new FakeDefenderHistory { Complete = false, LimitReached = true });
        var empty = new PilotLocalScan(new Dictionary<string, PilotLocalEntry>(), [], 0);
        await inventory.ApplyHistoryAsync(empty, path => path == "first.txt", default);
        var first = store.DefenderNotices("p1", "ra").Single();
        await inventory.ApplyHistoryAsync(empty, _ => true, default);
        var reviews = store.DefenderNotices("p1", "ra");
        var later = reviews.Single(n => n.NoticeId != first.NoticeId);
        Require(reviews.Count == 2 && reviews.Contains(first) && later.DefenderHistory!.Count == 1 &&
            store.DefenderDeferrals([items[0].ObjectId])[items[0].ObjectId].Batch == first.DefenderHistory!.Batch &&
            store.DefenderDeferrals([items[1].ObjectId])[items[1].ObjectId].Batch == later.DefenderHistory.Batch,
            "new eligible UNKNOWN gets a separate visible review while the first review and membership remain frozen");
        // Only the first notice is stale; the second remains valid while displaced members need a new review.
        store.AddNotices([first with { DefenderHistory = first.DefenderHistory! with
            { ReviewedRoot = fx.Pair.A with { RootPath = @"C:\old-review-root" } } }]);
        await inventory.ApplyHistoryAsync(empty, _ => true, default, []);
        reviews = store.DefenderNotices("p1", "ra");
        var fresh = reviews.SingleOrDefault(n => n.NoticeId != later.NoticeId);
        Require(store.NoticeById("p1", first.NoticeId!)!.Value.Closed && reviews.Contains(later) &&
            fresh?.DefenderHistory?.Count == 1 && fresh.NoticeId != first.NoticeId &&
            store.DefenderDeferrals([items[0].ObjectId])[items[0].ObjectId].Batch == fresh.DefenderHistory.Batch,
            "a review closed as review-changed moves its pending items into a fresh visible review; displaced members get a new notice even when another unchanged review remains open");
        await fx.Run().ResolveQuarantineAsync("p1", later.NoticeId!, NoticeChoices.DefenderProceed, default);
        var consent = store.DefenderDeferralConsent(items.Select(i => i.ObjectId).ToArray());
        Require(consent[items[0].ObjectId] == DefenderConsent.Pending && consent[items[1].ObjectId] == DefenderConsent.Proceed &&
            store.DefenderBatchCount(fresh!.DefenderHistory!.Batch) == fresh.DefenderHistory.Count,
            "Proceed covers exactly its review, not the displaced member in the other review");
        store.AddNotices([new Notice("p1", 30, NoticeKind.Info, "other", "unrelated", "other"),
            new Notice("p1", 30, NoticeKind.UserDecision, "", "different root", DefenderReview.ReviewKey("other-root", "x"),
                DefenderHistory: new("other-root", "x", 1, "history-bound", fx.Pair.A))]);
        Require(store.DefenderNotices("p1", "ra").Single().NoticeId == fresh!.NoticeId,
            "storage Defender lookup excludes unrelated reasons and other roots");
    }

    private static async Task QuarantineSharedHeldAsync(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "shared-held"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var item = fx.A.Seed("shared.txt", "keep on both pairs");
        await FirstSyncAsync(fx.Run(), fx);
        store.SavePair(fx.Pair with { PairId = "p2", Name = "second binding", B = fx.Pair.B with { RootId = "rc" } });
        store.DeferDefenderHistory([item.ObjectId], "held", DateTimeOffset.UtcNow);
        store.AssignDefenderBatch([item.ObjectId], "shared-review");
        store.DecideDefenderBatch("shared-review", false, "ra");
        DefenderReview.RefreshHeld(store, "ra");
        var first = store.DefenderNotices("p1", "ra").Single();
        var second = store.DefenderNotices("p2", "ra").Single();
        var sharedRelease = await fx.Run().ResolveQuarantineAsync("p1", first.NoticeId!, NoticeChoices.DefenderReleaseItem, default, itemPath: item.Path);
        var sharedReview = store.DefenderNotices("p1", "ra").Single(n => n.DefenderHistory?.Cause == DefenderReview.SharedReleaseCause);
        var options = NoticeChoices.Options(sharedReview);
        Require(sharedRelease.Outcome == Outcome.Done &&
            store.DefenderDeferralConsent([item.ObjectId])[item.ObjectId] == DefenderConsent.Keep &&
            store.DefenderNotices("p2", "ra").Single() == second &&
            options.Select(option => option.Choice).SequenceEqual(new[] { NoticeChoices.DefenderReleaseAllPairs, NoticeChoices.DefenderKeepShared }) &&
            options[0].Label == "Release in all pairs (also held by: second binding)" &&
            sharedReview.Detail.Contains("single-pair release is not available"),
            "shared release opens exactly the two approved options, names the other pair and changes no consent");
        Require((await fx.Run().ResolveQuarantineAsync("p1", sharedReview.NoticeId!, NoticeChoices.DefenderKeepShared, default)).Outcome == Outcome.Done &&
            store.DefenderDeferralConsent([item.ObjectId])[item.ObjectId] == DefenderConsent.Keep &&
            store.NoticeById("p1", sharedReview.NoticeId!)!.Value.Closed,
            "Keep held closes only the shared-release review and preserves the entity hold for every pair");
        var result = await fx.Run().ResolveQuarantineAsync("p1", first.NoticeId!, NoticeChoices.DefenderExcludeHeld, default, itemPath: item.Path);
        Require(result.Outcome == Outcome.Done && store.Pair("p1")!.Excluded.Contains(item.Path) &&
            !store.Pair("p2")!.Excluded.Contains(item.Path) &&
            store.DefenderDeferralConsent([item.ObjectId])[item.ObjectId] == DefenderConsent.Keep &&
            store.NoticeById("p1", first.NoticeId!)!.Value.Closed &&
            store.DefenderNotices("p2", "ra").Single() == second,
            "exclude changes only the chosen pair; other binding retains its Keep row and visible notice");
        Require(store.DefenderHeldPaths("ra", 10, "p1").Count == 0 &&
            store.DefenderHeldPaths("ra", 10, "p2").SequenceEqual(new[] { item.Path }),
            "held-item lists exclude the chosen pair's excluded prefixes but retain the other binding's item");
        Require((await fx.Run().ResolveQuarantineAsync("p1", first.NoticeId!, NoticeChoices.DefenderReleaseItem, default, itemPath: item.Path)).Outcome == Outcome.Skipped &&
            store.DefenderDeferralConsent([item.ObjectId])[item.ObjectId] == DefenderConsent.Keep,
            "stale excluded-pair release cannot release the remaining pair's hold");
        var inventory = new LocalInventory(fx.Pair.A with { RootObjectId = "id:local:00000001:0000000000000001" },
            Side.A, store, new HashSet<string>(), Settings, TimeProvider.System, new FakeDefenderHistory { Complete = true });
        var scan = await inventory.ApplyHistoryAsync(new PilotLocalScan(new Dictionary<string, PilotLocalEntry>(), [], 0), _ => true, default);
        Require(scan.Decisions.Any(d => d.RelativePath == item.Path && d.DefenderHistoryUnknown),
            "the remaining pair still holds the shared item even with complete history");
        Require((await fx.Run().ResolveQuarantineAsync("p2", second.NoticeId!, NoticeChoices.DefenderReleaseItem, default, itemPath: item.Path)).Outcome == Outcome.Done &&
            store.DefenderDeferralConsent([item.ObjectId])[item.ObjectId] == DefenderConsent.Proceed &&
            !store.DefenderNotices("p2", "ra").Any(n => n.DefenderHistory?.Cause == DefenderReview.SharedReleaseCause),
            "an item now held by only one including pair releases normally without a shared-release review");

        // A fresh Keep epoch shared by both pairs: explicit release-all-pairs is entity-wide.
        store.SavePair(store.Pair("p1")! with { Excluded = [] });
        store.ClearDefenderDeferrals([item.ObjectId]);
        store.DeferDefenderHistory([item.ObjectId], "shared-again", DateTimeOffset.UtcNow);
        store.AssignDefenderBatch([item.ObjectId], "shared-again");
        store.DecideDefenderBatch("shared-again", false, "ra");
        DefenderReview.RefreshHeld(store, "ra");
        await fx.Run().ResolveQuarantineAsync("p1", first.NoticeId!, NoticeChoices.DefenderReleaseItem, default, itemPath: item.Path);
        sharedReview = store.DefenderNotices("p1", "ra").Single(n => n.DefenderHistory?.Cause == DefenderReview.SharedReleaseCause);
        var runtime = new V2CliRuntime(null, store, fx.Run(), new EndpointRootProbe(fx.Provider), root);
        await runtime.ResolveConflictAsync("p1", item.Path, NoticeChoices.DefenderReleaseAllPairs, sharedReview.NoticeId, 0, null, default);
        Require(store.DefenderDeferralConsent([item.ObjectId])[item.ObjectId] == DefenderConsent.Proceed &&
            store.DefenderNotices("p1", "ra").Count == 0 && store.DefenderNotices("p2", "ra").Count == 0,
            "explicit CLI release in all pairs changes the entity consent and closes both standing notices and the item review");
        foreach (var choice in new[] { NoticeChoices.DefenderReleaseAllPairs, NoticeChoices.DefenderKeepShared })
        {
            string[] args = ["--cli", "conflicts", "resolve", "--pair", "p1", "--path", item.Path, "--key", sharedReview.NoticeId!, "--generation", "0", "--choice", choice];
            try { SyncCliRegistry.Parse(args); throw new InvalidOperationException("shared choice accepted without --yes"); }
            catch (SyncCliUsageException) { }
            SyncCliRegistry.Parse([.. args, "--yes"]);
        }
    }

    private static async Task QuarantineHeldNoticeAsync(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "held-notice"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        store.SavePair(fx.Pair with { A = fx.Pair.A with { RootObjectId = "id:local:00000001:0000000000000001" } });
        var items = new[] { "a.txt", "b.txt", "c.txt" }.Select(path => fx.A.Seed(path, "held " + path)).ToArray();
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        foreach (var item in items)
            await fx.A.DeleteAsync(new SyncAction("gone-" + item.Path, 30, ActionKind.Delete, Side.A, item.Path, null, null, item, false, null), default);
        var inventory = new LocalInventory(fx.Pair.A with { RootPath = @"C:\fake-history", RootObjectId = "id:local:00000001:0000000000000001" },
            Side.A, store, new HashSet<string>(), Settings, TimeProvider.System, new FakeDefenderHistory { Complete = false });
        var empty = new PilotLocalScan(new Dictionary<string, PilotLocalEntry>(), [], 0);
        for (var i = 0; i < 3; i++) await inventory.ApplyHistoryAsync(empty, _ => true, default);
        Notice Review() => store.OpenNotices("p1").Single(n => n.DefenderHistory is { Cause: not DefenderReview.HeldCause });
        Notice Held() => store.OpenNotices("p1").SingleOrDefault(n => n.DefenderHistory is { Cause: DefenderReview.HeldCause })!;
        var first = Review();
        var rebound = store.Pair("p1")! with { A = store.Pair("p1")!.A with { RootPath = @"C:\rebound" } };
        store.SavePair(rebound);
        // An untouched delta: the items are not candidates now, so only the review-changed move can make them visible.
        await inventory.ApplyHistoryAsync(empty, _ => true, default, []);
        var fresh = store.OpenNotices("p1").SingleOrDefault(n => n.DefenderHistory is { Cause: not DefenderReview.HeldCause })!;
        Require(store.NoticeById("p1", first.NoticeId!)!.Value.Closed && fresh?.NoticeId != first.NoticeId &&
            fresh?.DefenderHistory!.Count == 3 && fresh.DefenderHistory.ReviewedRoot == rebound.A,
            "a review closed as review-changed moves its pending items into a fresh visible review");
        var runtime = new V2CliRuntime(null, store, run, new EndpointRootProbe(fx.Provider), root);
        await runtime.ResolveConflictAsync("p1", "", NoticeChoices.DefenderHold, fresh!.NoticeId, 0, null, default);
        var held = Held();
        Require(store.NoticeById("p1", fresh.NoticeId!)!.Value.Closed && held.DefenderHistory!.Count == 3 &&
            held.DefenderHistory.ReviewedRoot == rebound.A && !store.OpenNotices("p1").Any(n => n.DefenderHistory is { Cause: not DefenderReview.HeldCause }),
            "CLI Keep held resolves its review and lists every held item in one standing notice");
        var host = new V2SyncHost(store, run, new ObservedExecutor(fx.Executor), null!, Settings, TimeProvider.System, SqliteStateStore.DatabasePath(root));
        Require((await host.LoadHeldItemsAsync("p1", held.NoticeId!, 10)).SequenceEqual(new[] { "a.txt", "b.txt", "c.txt" }),
            "the app loads the held items for a per-item choice");
        await host.ResolveConflictSelectionCommandAsync([new(new(held.NoticeId!, "p1", "a.txt", 0),
            SyncAuthorityConflictResolutionKind.PreserveBoth, NoticeChoices.DefenderReleaseItem)]);
        Require(store.DefenderDeferralConsent([items[0].ObjectId])[items[0].ObjectId] == DefenderConsent.Proceed && Held()?.DefenderHistory!.Count == 2,
            "app release of one held item releases only that item");
        await host.ResolveConflictSelectionCommandAsync([new(new(held.NoticeId!, "p1", "b.txt", 0),
            SyncAuthorityConflictResolutionKind.PreserveBoth, NoticeChoices.DefenderExcludeHeld)]);
        Require(store.Pair("p1")!.Excluded.Contains("b.txt") && !store.DefenderDeferralConsent([items[1].ObjectId]).ContainsKey(items[1].ObjectId) &&
            Held()?.DefenderHistory!.Count == 1 && await fx.B.ObserveAsync("b.txt", default) is not null,
            "app stop-syncing one held item excludes it and keeps both copies");
        try
        {
            await runtime.ResolveConflictAsync("p1", "a.txt", NoticeChoices.DefenderReleaseItem, held.NoticeId, 0, null, default);
            throw new InvalidOperationException("a released item was accepted as still held");
        }
        catch (SyncCliReviewException) { }
        Require(true, "a per-item choice refuses an item no longer held by your choice");
        await runtime.ResolveConflictAsync("p1", "", NoticeChoices.DefenderReleaseHeld, held.NoticeId, 0, null, default);
        Require(store.DefenderDeferralConsent([items[2].ObjectId])[items[2].ObjectId] == DefenderConsent.Proceed &&
            store.NoticeById("p1", held.NoticeId!)!.Value.Closed && !store.OpenNotices("p1").Any(n => n.DefenderHistory is not null),
            "CLI release all empties and closes the standing notice");
    }

    private static async Task QuarantinePruneAsync(string root)
    {
        var clock = new ShiftedClock(TimeSpan.Zero);
        using var store = SqliteStateStore.Open(Path.Combine(root, "deferral-prune"), Settings, clock);
        var fx = Build(suppliedStore: store);
        var items = new[] { "kept.txt", "pending.txt", "proceeded.txt", "released.txt" }.Select(path => fx.A.Seed(path, "prune " + path)).ToArray();
        await FirstSyncAsync(fx.Run(), fx);
        var ids = items.Select(i => i.ObjectId).ToArray();
        store.DeferDefenderHistory([.. ids, "orphan-entity"], "prune", clock.GetUtcNow());
        store.AssignDefenderBatch([ids[0]], "prune-keep");
        store.DecideDefenderBatch("prune-keep", false, "ra");
        store.AssignDefenderBatch([ids[2]], "prune-proceed");
        store.DecideDefenderBatch("prune-proceed", true, "ra");
        store.ReleaseDefenderDeferrals([ids[3]]);
        clock.Offset = Settings.HistoryRetention + TimeSpan.FromDays(1);
        store.Prune();
        Require(store.DefenderDeferralConsent([.. ids, "orphan-entity"]).Keys.Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { ids[0], ids[1] }.Order(StringComparer.Ordinal)),
            "retention prunes only proceeded, released or gone-on-both-sides deferrals; Keep-held and pending rows of live entities survive");
        var history = new FakeDefenderHistory { Complete = true };
        var inventory = new LocalInventory(fx.Pair.A with { RootPath = @"C:\fake-history", RootObjectId = "id:local:00000001:0000000000000001" },
            Side.A, store, new HashSet<string>(), Settings, clock, history);
        var empty = new PilotLocalScan(new Dictionary<string, PilotLocalEntry>(), [], 0);
        Func<string, bool> keptOnly = path => path == "kept.txt";
        var scan = await inventory.ApplyHistoryAsync(empty, keptOnly, default);
        var gen = store.BeginGeneration("ra", "prune-full-scan");
        store.WriteSnapshot(inventory.BuildSnapshot(gen, "ra", "", scan, keptOnly));
        Require(history.Calls == 1 && scan.Decisions.Single().RelativePath == "kept.txt" && store.Live("ra", ["kept.txt"]).Count == 1 &&
            store.DefenderDeferralConsent([ids[0]])[ids[0]] == DefenderConsent.Keep,
            "a full-scan root keeps a Keep-held item past retention even after a complete no-match history");
        await inventory.ApplyHistoryAsync(empty, keptOnly, default);
        Require(history.Calls == 1, "a weekly recheck stamps the Keep-held row, so the next run makes no history query");
    }

    private sealed class ShiftedClock(TimeSpan offset) : TimeProvider
    {
        internal TimeSpan Offset = offset;
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + Offset;
    }

    private sealed class FullScanHistoryInventory(LocalInventory inventory, IInventorySource hashes) : IInventorySource
    {
        internal int FullScans;
        public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct)
        {
            FullScans++;
            return inventory.BuildSnapshot(gen, "ra", "", await inventory.ApplyHistoryAsync(
                new PilotLocalScan(new Dictionary<string, PilotLocalEntry>(), [], 0), include, ct), include);
        }
        // Every run re-reads the whole root, as on a UNC, non-NTFS or journal-less root.
        public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
            Task.FromResult(new Delta("ra", Side.A, gen, cursor, cursor, [], true, []));
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) => hashes.HashAsync(paths, ct);
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct) => hashes.DeletionTimesAsync(paths, ct);
    }

    private sealed class MissingHistoryInventory(LocalInventory inventory, IInventorySource hashes, string path) : IInventorySource
    {
        private static PilotLocalScan Empty() => new(new Dictionary<string, PilotLocalEntry>(), [], 0);
        public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct) =>
            inventory.BuildSnapshot(gen, "ra", "", await inventory.ApplyHistoryAsync(Empty(), include, ct), include);
        public async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
            inventory.BuildDelta(gen, "ra", cursor, cursor, await inventory.ApplyHistoryAsync(Empty(), include, ct, [path]),
                [path], include, DateTimeOffset.UtcNow, []);
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) => hashes.HashAsync(paths, ct);
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct) => hashes.DeletionTimesAsync(paths, ct);
    }

    private static async Task QuarantineDeferredFolderAsync(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "deferred-folder"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("foo", "", ItemKind.Folder);
        fx.A.Seed("foo/held.txt", "held");
        fx.A.Seed("foo/other.txt", "other");
        fx.A.Seed("foo/allowed.txt", "allowed");
        await FirstSyncAsync(fx.Run(), fx);
        var rows = store.QuarantineBaselineItems("ra").ToArray();
        var folder = rows.Single(i => i.Path == "foo");
        store.DeferDefenderHistory(rows.Select(i => i.ObjectId).ToArray(), "initial", DateTimeOffset.UtcNow);
        var proxy = System.Reflection.DispatchProxy.Create<IStateStore, FolderHistoryStore>();
        var audit = (FolderHistoryStore)(object)proxy;
        audit.Inner = store;
        var history = new FakeDefenderHistory { Complete = false };
        var inventory = new LocalInventory(fx.Pair.A with { RootPath = @"C:\fake-history", RootObjectId = "id:local:00000001:0000000000000001" },
            Side.A, proxy, new HashSet<string>(), Settings with { LocalDeltaMaxPaths = 2 }, TimeProvider.System, history);
        PilotLocalScan Empty() => new(new Dictionary<string, PilotLocalEntry>(), [], 0);
        async Task<PilotLocalScan> Recheck() => await inventory.ApplyHistoryAsync(Empty(), _ => true, default, [], ["foo"]);
        var scan = await Recheck();
        Require(audit.OpenNoticeReads == 1, "one open-notice read per bound pair per inventory pass");
        Require(scan.Decisions.Single().DefenderHistoryUnknown && audit.DescendantEnumerations == 0 && audit.ExactRows <= 2,
            "100000-descendant deferred folder history recheck reads only exact queued entity, zero descendants");
        history.Detections = [new(@"file:_C:\fake-history\foobar\bad.exe", DateTimeOffset.UtcNow)];
        history.Complete = true;
        scan = await Recheck();
        Require(scan.Decisions.Count == 0, "folder history boundary foo never matches foobar");
        store.DeferDefenderHistory([folder.ObjectId], "again", DateTimeOffset.UtcNow);
        history.Detections = [new(@"file:_C:\fake-history\foo\held.txt", DateTimeOffset.UtcNow)];
        scan = await Recheck();
        Require(scan.Decisions.Any(d => d.RelativePath == "foo" && d.IsQuarantine) && audit.DescendantEnumerations == 0,
            "positive descendant Defender evidence holds enclosing folder without subtree enumeration");
        history.Detections = [];
        store.DeferDefenderHistory([folder.ObjectId], "empty", DateTimeOffset.UtcNow);
        scan = await Recheck();
        Require(scan.Decisions.Count == 0 && audit.DescendantEnumerations == 0,
            "complete empty folder history releases ordinary deletion without a history descendant walk");
        audit.ForbidDescendants = false;
        history.Complete = false;
        store.ClearDefenderDeferrals(rows.Select(i => i.ObjectId).ToArray());
        store.DeferDefenderHistory(rows.Select(i => i.ObjectId).ToArray(), "watcher", DateTimeOffset.UtcNow);
        var watched = await inventory.ApplyHistoryAsync(Empty(), _ => true, default, ["foo"]);
        Require(watched.Decisions.Count == rows.Length,
            "ordinary changed-folder history refresh still checks every baseline descendant");
        audit.ForbidDescendants = true;
        audit.DescendantEnumerations = 0;
        var presentFolder = new PilotLocalScan(new Dictionary<string, PilotLocalEntry>
        {
            ["foo"] = new("foo", PilotEntryKind.Directory, "C:/fake-history/foo", 0, DateTime.UtcNow, null, 1, 999)
        }, [], 0);
        var exactScope = LocalInventory.RefreshDeletionScopes(presentFolder, ["foo"], new HashSet<string> { "foo" });
        var presentDelta = inventory.BuildDelta(store.BeginGeneration("ra", "exact-present"), "ra", "", "", presentFolder,
            exactScope, _ => true, DateTimeOffset.UtcNow, []);
        Require(presentDelta.Changes.All(c => c.Kind == ChangeKind.Upsert) && audit.DescendantEnumerations == 0 &&
            LocalInventory.RefreshDeletionScopes(presentFolder, ["foo"], null).Contains("foo"),
            "exact-only present folder never deletes unseen children while watcher folder keeps its subtree scope");
        audit.ForbidDescendants = false;
        var held = rows.Single(i => i.Path == "foo/held.txt");
        var allowed = rows.Single(i => i.Path == "foo/allowed.txt");
        var other = rows.Single(i => i.Path == "foo/other.txt");
        store.AssignDefenderBatch([folder.ObjectId, allowed.ObjectId], "folder-reviewed");
        store.DecideDefenderBatch("folder-reviewed", true, "ra");
        store.AssignDefenderBatch([held.ObjectId], "child-kept");
        store.DecideDefenderBatch("child-kept", false, "ra");
        scan = await Recheck();
        Delta DeltaFor(PilotLocalScan value) => inventory.BuildDelta(store.BeginGeneration("ra", "folder-consent"), "ra", "", "", value,
            ["foo"], _ => true, DateTimeOffset.UtcNow, []);
        var delta = DeltaFor(scan);
        Require(delta.Changes.Count == 1 && delta.Changes[0].Path == allowed.Path,
            "folder Proceed preserves out-of-batch and Keep-held descendants while reviewed sibling proceeds");
        store.WriteDelta(delta);
        Require(store.Live("ra", [folder.Path, held.Path, other.Path]).Count == 3,
            "ordinary deletion excludes unconsented descendants and their enclosing folder");
        history.Complete = true;
        scan = await Recheck();
        delta = DeltaFor(scan);
        Require(delta.Changes.Count == 1 && delta.Changes[0].Path == other.Path,
            "complete folder history resolves unknown descendant but never overrides explicit Keep held");
        store.WriteDelta(delta);
        Require(store.DefenderDeferredPaths("ra", 10).Contains("foo") &&
            store.DefenderDeferralConsent([folder.ObjectId])[folder.ObjectId] is DefenderConsent.Proceed or DefenderConsent.Released,
            "evidence-resolved parent remains queued without being an unresolved member of its old review");
        store.ReleaseDefenderHeld("ra", held.ObjectId);
        store.HoldQuarantine("ra", held);
        Require(!store.DefenderDeferralConsent([held.ObjectId]).ContainsKey(held.ObjectId),
            "native positive evidence revokes prior UNKNOWN consent before explicit recovery");
        scan = await Recheck();
        Require(DeltaFor(scan).Changes.Count == 0,
            "recorded native or positive descendant remains protected during ordinary folder deletion");
        store.ClearQuarantine("ra", held);
        scan = await Recheck();
        delta = DeltaFor(scan);
        Require(delta.Changes.Count == 1 && delta.Changes[0].Path == folder.Path,
            "resolved children eventually permit ordinary conditional folder deletion");
        store.WriteDelta(delta);
        Require(store.Live("ra", [folder.Path, held.Path]).Count == 0,
            "ordinary folder deletion finishes after child holds resolve");

    }

    public class FolderHistoryStore : System.Reflection.DispatchProxy
    {
        internal IStateStore Inner = null!;
        internal bool ForbidDescendants = true;
        internal int DescendantEnumerations;
        internal int ExactRows;
        internal int OpenNoticeReads;
        internal HashSet<string> ClosedNotices = new(StringComparer.Ordinal);
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IStateStore.CloseNotice)) ClosedNotices.Add((string)args![0]!);
            if (method.Name is nameof(IStateStore.OpenNotices) or nameof(IStateStore.DefenderNotices)) OpenNoticeReads++;
            if (method!.Name == nameof(IStateStore.QuarantineBaselineItems) && args![1] is IReadOnlyList<string> scopes && scopes.Contains("foo") ||
                method.Name == nameof(IStateStore.LiveUnder))
            {
                if (ForbidDescendants && method.Name == nameof(IStateStore.QuarantineBaselineItems))
                {
                    var folder = Inner.Live("ra", ["foo"]).Single();
                    return new[] { folder }.Concat(Enumerable.Range(0, 100000).Select(i =>
                    {
                        DescendantEnumerations++;
                        if (ForbidDescendants) throw new InvalidOperationException("deferred folder recheck attempted enumeration of 100000 fake descendants");
                        return folder with { ObjectId = "virtual-" + i, Path = "foo/" + i, Kind = ItemKind.File };
                    }));
                }
                DescendantEnumerations++;
                if (ForbidDescendants) throw new InvalidOperationException("deferred folder recheck attempted LiveUnder");
            }
            var result = method.Invoke(Inner, args);
            if (method.Name == nameof(IStateStore.Live) && result is IReadOnlyList<ItemState> rows) ExactRows += rows.Count;
            return result;
        }
    }

    private sealed class FakeDefenderHistory : IDefenderHistory
    {
        internal IReadOnlyList<DefenderDetection> Detections = [];
        internal bool Fail;
        internal bool Complete = true;
        internal bool LimitReached;
        internal DefenderAvailability Availability;
        internal int Calls;
        public Task<DefenderHistoryResult> ReadAsync(CancellationToken ct)
        { Calls++; if (Fail) throw new IOException("history unavailable"); return Task.FromResult(new DefenderHistoryResult(Detections, Complete, Availability, LimitReached)); }
    }
}
