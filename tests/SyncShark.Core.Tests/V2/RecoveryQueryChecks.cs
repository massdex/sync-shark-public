using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Recovery and restore-notice keyset paging against a real SQLite store: filters, continuation and isolation.</summary>
internal static class RecoveryQueryChecks
{
    internal static void Run(string root)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Use a fresh Recovery query fixture root.");
        Directory.CreateDirectory(root);
        var clock = new ManualClock();
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), StorageChecks.Fast, clock);
        var pair = Pair("pair-a");
        var other = Pair("pair-b");
        store.SavePair(pair);
        store.SavePair(other);

        var rows = new (string ActionId, Side Side, string Path)[]
        {
            ("p1", Side.A, "Root/alpha"),
            ("p2", Side.B, "Root/alpha/child"),
            ("p3", Side.A, "Root/a%b"),
            ("p4", Side.B, "Root/a%b/child"),
            ("p5", Side.A, "Root/axb"),
            ("p6", Side.A, "Root/a_b"),
            ("p7", Side.B, "Root/axb/deep"),
            ("p8", Side.A, "Root/alphabeta"),
        };
        var ids = new List<string>();
        var created = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            var id = Seed(store, pair, row.ActionId, row.Side, row.Path);
            ids.Add(id);
            created[id] = clock.GetUtcNow();
        }
        var otherId = Seed(store, other, "b1", Side.A, "Root/alpha");

        var exact = store.RecoveryPage(pair.PairId, new RecoveryQuery(Id: ids[2]));
        LocalChecks.Require(Ids(exact).SequenceEqual([ids[2]]), "recovery page filters by exact id");

        var percent = store.RecoveryPage(pair.PairId, new RecoveryQuery(PathPrefix: "Root/a%b"));
        var percentExpected = Sorted(ids[2], ids[3]);
        LocalChecks.Require(Ids(percent).SequenceEqual(percentExpected),
            "path prefix includes the literal path and its descendants but not axb");
        LocalChecks.Require(Ids(store.RecoveryPage(pair.PairId, new RecoveryQuery(PathPrefix: "ROOT/A%B")))
            .SequenceEqual(percentExpected), "path prefix matching ignores case");

        var underscore = store.RecoveryPage(pair.PairId, new RecoveryQuery(PathPrefix: "Root/a_b"));
        LocalChecks.Require(Ids(underscore).SequenceEqual([ids[5]]),
            "path prefix treats an underscore literally and does not match a single other character");

        var since = store.RecoveryPage(pair.PairId, new RecoveryQuery(Since: created[ids[3]]));
        LocalChecks.Require(Ids(since).SequenceEqual(Sorted(ids[3], ids[4], ids[5], ids[6], ids[7])),
            "since is inclusive and pages by recovery id");
        var subMillisecond = store.RecoveryPage(pair.PairId,
            new RecoveryQuery(Since: created[ids[2]].AddTicks(TimeSpan.TicksPerMillisecond / 2)));
        LocalChecks.Require(Ids(subMillisecond).SequenceEqual(Sorted(ids[3], ids[4], ids[5], ids[6], ids[7])),
            "a sub-millisecond since bound rounds up to the next stored millisecond");

        var sideB = store.RecoveryPage(pair.PairId, new RecoveryQuery(Side: Side.B));
        LocalChecks.Require(Ids(sideB).SequenceEqual(Sorted(ids[1], ids[3], ids[6])), "side filter selects one side");

        var byAction = store.RecoveryPage(pair.PairId, new RecoveryQuery(ActionId: "p5"));
        LocalChecks.Require(Ids(byAction).SequenceEqual([ids[4]]), "recovery page filters by exact action id");

        var otherPage = store.RecoveryPage(other.PairId, new RecoveryQuery(Limit: RecoveryQuery.MaximumLimit));
        LocalChecks.Require(Ids(otherPage).SequenceEqual([otherId]), "each pair pages only its own recovery rows");
        var alpha = store.RecoveryPage(pair.PairId, new RecoveryQuery(PathPrefix: "Root/alpha"));
        LocalChecks.Require(Ids(alpha).SequenceEqual(Sorted(ids[0], ids[1])) &&
            !Ids(alpha).Any(id => string.Equals(id, otherId, StringComparison.Ordinal)),
            "a prefix stops at a path separator and another pair's same path never leaks into a page");

        var expected = ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var paged = new List<string>();
        string? after = null;
        while (true)
        {
            var page = store.RecoveryPage(pair.PairId, new RecoveryQuery(AfterId: after, Limit: 2));
            paged.AddRange(Ids(page));
            if (page.Count < 2) break;
            after = page[^1].RecoveryId;
        }
        LocalChecks.Require(paged.SequenceEqual(expected) && paged.Count == expected.Length,
            "after id continues exclusively in recovery-id order without duplicates or gaps");

        LocalChecks.Require(store.RecoveryPage(pair.PairId, new RecoveryQuery()).Count == rows.Length,
            "the default recovery page limit is 100");
        LocalChecks.Require(store.RecoveryPage("unknown-pair", new RecoveryQuery()).Count == 0,
            "an unknown pair has no recovery page");
        foreach (var limit in new[] { 0, RecoveryQuery.MaximumLimit + 1 })
        {
            var rejected = false;
            try { store.RecoveryPage(pair.PairId, new RecoveryQuery(Limit: limit)); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            LocalChecks.Require(rejected, "recovery limit " + limit + " is rejected");
        }

        store.AddNotices([
            FrozenPending(pair.PairId, "Root/guard", "guard-recovery"),
            FrozenPending(pair.PairId, "Root/guard/child", "guard-child-recovery"),
            FrozenPending(pair.PairId, "Root/guard/sibling", "guard-sibling-recovery"),
            new Notice(pair.PairId, 0, NoticeKind.Info, "Root/ready", "Ready fixture", "recovery-restore-ready"),
            new Notice(pair.PairId, 0, NoticeKind.Info, "Root/ready/closed", "Ready closed fixture", "recovery-restore-ready"),
            new Notice(pair.PairId, 0, NoticeKind.Info, "Root/other", "Other fixture", "other-reason"),
            FrozenPending(other.PairId, "Root/otherpair", "other-pair-recovery"),
        ]);
        var readyClosed = store.OpenNotices(pair.PairId).Single(notice => notice.Path == "Root/ready/closed");
        store.CloseNotice(readyClosed.NoticeId!, "resolved");
        var expectedNotices = store.OpenNotices(pair.PairId)
            .Where(notice => notice.ReasonKey is "recovery-restore-pending" or "recovery-restore-ready")
            .OrderBy(notice => notice.NoticeId!, StringComparer.Ordinal).ToArray();
        LocalChecks.Require(expectedNotices.Length == 4, "open restore notices exclude closed and unrelated reasons");

        var noticePage = store.RecoveryRestoreNoticesPage(pair.PairId, limit: 2);
        LocalChecks.Require(noticePage.Select(notice => notice.NoticeId)
            .SequenceEqual(expectedNotices.Take(2).Select(notice => notice.NoticeId)),
            "restore notice page returns pending and ready in notice-id order");
        var nextNoticePage = store.RecoveryRestoreNoticesPage(pair.PairId, noticePage[^1].NoticeId, 2);
        LocalChecks.Require(nextNoticePage.Select(notice => notice.NoticeId)
            .SequenceEqual(expectedNotices.Skip(2).Select(notice => notice.NoticeId)),
            "restore notice after id continues exclusively");
        LocalChecks.Require(store.RecoveryRestoreNoticesPage(pair.PairId).Count == expectedNotices.Length &&
            store.RecoveryRestoreNoticesPage("unknown-pair").Count == 0,
            "restore notice page is bounded to one pair");
        foreach (var limit in new[] { 0, 501 })
        {
            var rejected = false;
            try { store.RecoveryRestoreNoticesPage(pair.PairId, limit: limit); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            LocalChecks.Require(rejected, "notice limit " + limit + " is rejected");
        }

        LocalChecks.Require(store.RecoveryRestoreReadyAt(pair.PairId,
                ["Root/ready", "Root/ready/closed", "Root/guard", "ROOT/READY"]).SingleOrDefault() is { } readyAt &&
            readyAt.Path == "Root/ready" && readyAt.ReasonKey == "recovery-restore-ready",
            "ready-at returns open ready notices at exact paths only");
        LocalChecks.Require(store.RecoveryRestoreReadyAt(pair.PairId, []).Count == 0,
            "ready-at with no paths reads nothing");
        var manyPaths = Enumerable.Range(0, 256).Select(index => "Root/decoy-" + index).Append("Root/ready").ToArray();
        LocalChecks.Require(store.RecoveryRestoreReadyAt(pair.PairId, manyPaths).Count == 1,
            "ready-at pages past the 256-path chunk boundary");

        LocalChecks.Require(store.RecoveryRestoreProtected(pair.PairId, "Root/guard"),
            "a pending notice protects its own path");
        LocalChecks.Require(store.RecoveryRestoreProtected(pair.PairId, "Root/guard/child/deep"),
            "a pending ancestor protects a descendant path");
        LocalChecks.Require(!store.RecoveryRestoreProtected(pair.PairId, "Root") &&
            store.RecoveryRestoreProtected(pair.PairId, "Root", descendants: true),
            "a pending notice below a path protects it only when descendants is requested");
        LocalChecks.Require(!store.RecoveryRestoreProtected(pair.PairId, "Root/ready"),
            "ready notices protect nothing");
        LocalChecks.Require(store.RecoveryRestoreProtected(pair.PairId, "Root/ready", includeReady: true) &&
            store.RecoveryRestoreProtected(pair.PairId, "Root/ready/closed", includeReady: true) &&
            store.RecoveryRestoreProtected(pair.PairId, "Root/ready/closed/child", includeReady: true) &&
            !store.RecoveryRestoreProtected(pair.PairId, "Root/ready/closed"),
            "includeReady counts open and closed ready history for approved-plan safety only");
        LocalChecks.Require(!store.RecoveryRestoreProtected(pair.PairId, "Root/guard-sibling"),
            "protection stops at a path separator");
        LocalChecks.Require(store.RecoveryRestoreProtected(pair.PairId, "ROOT/GUARD"),
            "protection path matching ignores case");
        LocalChecks.Require(!store.RecoveryRestoreProtected(pair.PairId, "Root/otherpair") &&
            store.RecoveryRestoreProtected(other.PairId, "Root/otherpair"),
            "pending protection is pair-scoped");

        var collisionChoice = "collision-recovery";
        var collisionId = ShortId.Make("rrs", pair.PairId, collisionChoice);
        var collisionPending = new Notice(pair.PairId, 0, NoticeKind.UserDecision, "Root/collision",
            "Collision pending", "recovery-restore-pending", collisionId,
            ConflictOperation: new ConflictNoticeOperation(collisionChoice, null, [], []));
        store.AddNotices([collisionPending]);
        var collisionRejected = false;
        try
        {
            store.AddNotices([collisionPending with { ReasonKey = "recovery-restore-ready",
                NoticeId = collisionId, ConflictOperation = null, Detail = "Other identity" }]);
        }
        catch (InvalidDataException) { collisionRejected = true; }
        LocalChecks.Require(collisionRejected &&
            store.NoticeById(pair.PairId, collisionId)?.Notice.ReasonKey == "recovery-restore-pending",
            "a different recovery identity cannot replace the pending row through its explicit id");

        var recoveryId = "recovery-identity";
        var durableId = ShortId.Make("rrs", pair.PairId, recoveryId);
        var pending = new Notice(pair.PairId, 0, NoticeKind.UserDecision, "Root/transition",
            "Pending transition", "recovery-restore-pending", durableId,
            ConflictOperation: new ConflictNoticeOperation(recoveryId, null,
                [FrozenIntent(pair.PairId, recoveryId, "Root/transition")], []));
        store.AddNotices([pending]);
        LocalChecks.Require(store.NoticeById(pair.PairId, durableId) is { Closed: false } found &&
            found.Notice.ReasonKey == "recovery-restore-pending",
            "an explicit recovery notice id is recognized");
        store.AddNotices([pending with { ReasonKey = "recovery-restore-ready", Kind = NoticeKind.Info,
            Detail = "Ready transition" }]);
        var transitioned = store.NoticeById(pair.PairId, durableId);
        LocalChecks.Require(transitioned is { Closed: false } ready &&
            ready.Notice.ReasonKey == "recovery-restore-ready" &&
            store.OpenNotices(pair.PairId).Count(notice => notice.NoticeId == durableId) == 1,
            "pending -> ready updates one row and leaves no stale reason");
        LocalChecks.Require(!store.RecoveryRestoreProtected(pair.PairId, "Root/transition"),
            "the transitioned ready notice no longer protects");
        store.AddNotices([pending with { ReasonKey = "recovery-restore-pending", Kind = NoticeKind.UserDecision,
            Path = "Root/transition-copy", Detail = "Retry destination",
            ConflictOperation = new ConflictNoticeOperation(recoveryId, "Root/transition-copy",
                [FrozenIntent(pair.PairId, recoveryId, "Root/transition-copy")], []) }]);
        var retried = store.NoticeById(pair.PairId, durableId);
        LocalChecks.Require(retried is { Closed: false } retryRow &&
            retryRow.Notice.ReasonKey == "recovery-restore-pending" &&
            retryRow.Notice.Path == "Root/transition-copy" &&
            store.OpenNotices(pair.PairId).Count(notice => notice.NoticeId == durableId) == 1,
            "a recovery retry updates the same row and destination");
        LocalChecks.Require(store.RecoveryRestoreProtected(pair.PairId, "Root/transition-copy") &&
            !store.RecoveryRestoreProtected(pair.PairId, "Root/transition"),
            "a retried destination updates the indexed path key");
        store.AddNotices([new Notice(pair.PairId, 0, NoticeKind.Info, "Root/derived",
            "Derived identity", "recovery-restore-pending",
            ConflictOperation: new ConflictNoticeOperation("derived-recovery", null, [], []))]);
        var derivedId = ShortId.Make("rrs", pair.PairId, "derived-recovery");
        LocalChecks.Require(store.NoticeById(pair.PairId, derivedId)?.Notice.NoticeId == derivedId,
            "a recovery notice without an explicit id derives the stable rrs id");

        using var memory = new InMemoryStateStore();
        memory.SavePair(pair);
        var memoryId = ShortId.Make("rrs", pair.PairId, "memory-recovery");
        var memoryPending = new Notice(pair.PairId, 0, NoticeKind.UserDecision, "Root/memory",
            "Memory pending", "recovery-restore-pending", memoryId,
            ConflictOperation: new ConflictNoticeOperation("memory-recovery", null, [], []));
        memory.AddNotices([memoryPending]);
        memory.AddNotices([memoryPending with { ReasonKey = "recovery-restore-ready", Kind = NoticeKind.Info,
            Detail = "Memory ready" }]);
        LocalChecks.Require(memory.NoticeById(pair.PairId, memoryId) is { Closed: false } memoryReady &&
            memoryReady.Notice.ReasonKey == "recovery-restore-ready" &&
            memory.OpenNotices(pair.PairId).Count(notice => notice.NoticeId == memoryId) == 1,
            "in-memory fixture updates pending -> ready in place under the same id");
        var memoryStore = (IStateStore)memory;
        LocalChecks.Require(memoryStore.RecoveryRestoreNoticesPage(pair.PairId).Any(notice => notice.NoticeId == memoryId) &&
            !memoryStore.RecoveryRestoreProtected(pair.PairId, "Root/memory"),
            "in-memory defaults see the ready transition as one non-protecting page row");
        LocalChecks.Require(memoryStore.RecoveryRestoreProtected(pair.PairId, "Root/memory", includeReady: true),
            "in-memory includeReady counts the open ready notice");
        LocalChecks.Require(memoryStore.RecoveryRestoreReadyAt(pair.PairId, ["ROOT/MEMORY"]).Count == 1,
            "in-memory ready-at filters open ready notices at exact paths");
        memory.CloseNotice(memoryId, "verified");
        LocalChecks.Require(!memoryStore.RecoveryRestoreProtected(pair.PairId, "Root/memory") &&
            memoryStore.RecoveryRestoreProtected(pair.PairId, "Root/memory", includeReady: true),
            "in-memory includeReady retains closed ready history");
        memory.AddNotices([new Notice(pair.PairId, 0, NoticeKind.Info, "Root/memory-derived",
            "Memory derived", "recovery-restore-pending",
            ConflictOperation: new ConflictNoticeOperation("memory-derived", null, [], []))]);
        LocalChecks.Require(memory.NoticeById(pair.PairId,
            ShortId.Make("rrs", pair.PairId, "memory-derived")) is not null,
            "in-memory fixture derives the stable rrs id without an explicit id");

        // Finding 2: only a notice with a frozen intent (bytes may have been written) may hold a path.
        var emptyPair = Pair("pair-empty");
        store.SavePair(emptyPair);
        var noNotices = RecoveryRestoreProtection.Create(store, emptyPair.PairId);
        var noNoticeLookups = store.RecoveryRestoreLookups;
        LocalChecks.Require(!noNotices.Active && noNotices.Count == 0 &&
            !noNotices.Covers("Root/anything", false) && !noNotices.Covers("Root", true) &&
            store.RecoveryRestoreLookups == noNoticeLookups,
            "a pair with no notices is inert: one gate, no path and no per-item SQL");
        var gate = Pair("pair-gate");
        store.SavePair(gate);
        store.AddNotices([ActionlessPending(gate.PairId, "Root/actionless", "gate-actionless")]);
        LocalChecks.Require(!store.RecoveryRestoreProtected(gate.PairId, "Root/actionless") &&
            !store.HasPendingRestoreHolds(gate.PairId),
            "an action-less failure notice (CONFLICT/PROTECTED/NO-VERSION/FAILED) holds nothing");
        var gateQueries = store.RecoveryRestoreGateQueries;
        var inert = RecoveryRestoreProtection.Create(store, gate.PairId);
        var inertLookups = store.RecoveryRestoreLookups;
        for (var index = 0; index < 1_000; index++) inert.Covers("Root/missing-" + index, index % 2 == 0);
        LocalChecks.Require(!inert.Active && inert.Count == 0 && store.RecoveryRestoreGateQueries == gateQueries + 1 &&
            store.RecoveryRestoreLookups == inertLookups,
            "no frozen intent: one indexed gate per run and zero per-item SQL");

        // Active-path behaviour: the snapshot answers self, ancestor and descendant with separator boundaries.
        store.AddNotices([FrozenPending(gate.PairId, "Root/held", "gate-held")]);
        var activeProtection = RecoveryRestoreProtection.Create(store, gate.PairId);
        var activeLookups = store.RecoveryRestoreLookups;
        LocalChecks.Require(activeProtection.Active && activeProtection.Count == 1 &&
            activeProtection.Covers("Root/held", false) && activeProtection.Covers("root/HELD/child", false) &&
            activeProtection.Covers("Root", true) && activeProtection.Covers("Root/held/deep", true) &&
            !activeProtection.Covers("Root/held-sibling", false) && !activeProtection.Covers("Root", false),
            "a held restore path protects its entity, ancestor depth and descendants, never a separator sibling");
        for (var index = 0; index < 1_000; index++) activeProtection.Covers("Root/probe-" + index, index % 2 == 0);
        LocalChecks.Require(store.RecoveryRestoreLookups == activeLookups,
            "a materialized hold snapshot performs no SQL while Covers runs");
        var heldId = store.RecoveryRestoreNoticesPage(gate.PairId).Single(notice => notice.Path == "Root/held").NoticeId;
        store.CloseNotice(heldId!, "verified");
        LocalChecks.Require(activeProtection.Covers("Root/held", false) &&
            !RecoveryRestoreProtection.Create(store, gate.PairId).Active,
            "a run snapshot stays consistent across a live close; the next run's gate releases it");

        // Beyond the bounded cap the cached indexed fallback retains the same answers without a path set.
        var overflowPair = Pair("pair-overflow");
        store.SavePair(overflowPair);
        store.AddNotices(Enumerable.Range(0, 5)
            .Select(index => FrozenPending(overflowPair.PairId, "Root/held-" + index, "overflow-" + index)).ToArray());
        var overflow = RecoveryRestoreProtection.Create(store, overflowPair.PairId, pathLimit: 2);
        var overflowLookups = store.RecoveryRestoreLookups;
        LocalChecks.Require(overflow.Active && overflow.Count == 0 && overflow.Covers("Root/held-0", false),
            "beyond the path cap the fallback still protects held paths");
        var firstLookup = store.RecoveryRestoreLookups;
        LocalChecks.Require(firstLookup == overflowLookups + 1 && overflow.Covers("Root/held-0", false) &&
            store.RecoveryRestoreLookups == firstLookup,
            "the overflow fallback caches a repeated held-path lookup");
        LocalChecks.Require(!overflow.Covers("Root/unheld", false) && store.RecoveryRestoreLookups == firstLookup + 1 &&
            !overflow.Covers("Root/unheld", false) && store.RecoveryRestoreLookups == firstLookup + 1,
            "the overflow fallback caches misses within its bounded cache");

        // Beyond the cap, one planning scope must keep one snapshot: live closes and additions are not observed.
        var frozenPair = Pair("pair-frozen");
        store.SavePair(frozenPair);
        store.AddNotices([
            FrozenPending(frozenPair.PairId, "Root/frozen-a", "frozen-a"),
            FrozenPending(frozenPair.PairId, "Root/frozen-b", "frozen-b")]);
        var capped = RecoveryRestoreProtection.Create(store, frozenPair.PairId, pathLimit: 1);
        LocalChecks.Require(capped.Active && !capped.SnapshotComplete, "a capped run keeps indexed fallback mode");
        using (var planning = store.OpenPlanningProtection(frozenPair))
        {
            var bound = capped.Bind(planning, frozenPair.PairId);
            // Pin the snapshot without caching any held path, so the post-mutation reads really use the transaction.
            LocalChecks.Require(!bound("Root/decoy", false), "the planning fallback pins its read snapshot");
            var frozenNoticeId = store.RecoveryRestoreNoticesPage(frozenPair.PairId)
                .Single(notice => notice.Path == "Root/frozen-a").NoticeId;
            store.CloseNotice(frozenNoticeId!, "verified");
            store.AddNotices([FrozenPending(frozenPair.PairId, "Root/live-added", "frozen-live")]);
            LocalChecks.Require(!store.RecoveryRestoreProtected(frozenPair.PairId, "Root/frozen-a"),
                "the live writer released the closed hold");
            LocalChecks.Require(bound("Root/frozen-a", false) && !bound("Root/live-added", false),
                "a capped planning fallback stays frozen against live closes and additions");
        }

        // Finding 1: the newest row for a path is chosen within the filters, across pages.
        var newestPair = Pair("pair-newest");
        store.SavePair(newestPair);
        clock.Advance(TimeSpan.FromMinutes(1));
        var winnerSince = clock.GetUtcNow();
        var olderId = Seed(store, newestPair, "newest-old", Side.A, "Root/winner");
        clock.Advance(TimeSpan.FromMinutes(1));
        var newerId = Seed(store, newestPair, "newest-new", Side.A, "Root/winner");
        clock.Advance(TimeSpan.FromMinutes(1));
        Seed(store, newestPair, "newest-other", Side.B, "Root/winner-deep");
        LocalChecks.Require(store.RecoveryNewestForPath(newestPair.PairId,
                new RecoveryQuery(AfterId: "zzzz", Limit: 1), "root/WINNER")?.RecoveryId == newerId,
            "newest for a path ignores AfterId/Limit, matches ignoring case and picks the greatest Created");
        LocalChecks.Require(store.RecoveryNewestForPath(newestPair.PairId,
                new RecoveryQuery(Since: winnerSince.AddTicks(1)), "Root/winner")?.RecoveryId == newerId,
            "newest retains the since filter");
        LocalChecks.Require(store.RecoveryNewestForPath(newestPair.PairId,
                new RecoveryQuery(PathPrefix: "Root/elsewhere"), "Root/winner") is null &&
            store.RecoveryNewestForPath(newestPair.PairId, new RecoveryQuery(Side: Side.B), "Root/winner") is null,
            "newest retains the path-prefix and side filters");
        LocalChecks.Require(store.RecoveryNewestForPath(newestPair.PairId,
                new RecoveryQuery(ActionId: "newest-old"), "Root/winner")?.RecoveryId == olderId,
            "newest retains the action filter");
        var tiePair = Pair("pair-tie");
        store.SavePair(tiePair);
        var tieA = Seed(store, tiePair, "tie-a", Side.A, "Root/tie");
        var tieB = Seed(store, tiePair, "tie-b", Side.A, "Root/tie");
        var tieWinner = new[] { tieA, tieB }.OrderBy(id => id, StringComparer.Ordinal).Last();
        LocalChecks.Require(store.RecoveryNewestForPath(tiePair.PairId, new RecoveryQuery(), "Root/tie")?.RecoveryId == tieWinner,
            "equal Created times break deterministically by greater recovery id");

        // Finding 4: the bounded index and the SQL predicate must agree for descendants on near-miss prefixes,
        // where a plain string-prefix match would wrongly include a separator sibling or a character neighbour.
        var parityPair = Pair("pair-parity");
        store.SavePair(parityPair);
        store.AddNotices([FrozenPending(parityPair.PairId, "Root/held", "parity-held")]);
        var parity = RecoveryRestoreProtection.Create(store, parityPair.PairId);
        LocalChecks.Require(parity.Active && parity.SnapshotComplete, "the parity fixture holds one path in the bounded index");
        var parityFailures = new List<string>();
        foreach (var path in new[]
                 {
                     "", "Root", "Root/hel", "Root/held", "Root/held/child", "Root/held-sibling",
                     "Root/held-sibling/child", "Root/held2", "Root/heldx", "ROOT/HELD", "Root/HELD-SIBLING"
                 })
        foreach (var descendants in new[] { false, true })
        {
            var indexed = parity.Covers(path, descendants);
            var sql = store.RecoveryRestoreProtected(parityPair.PairId, path, descendants);
            if (indexed != sql)
                parityFailures.Add("'" + (path.Length == 0 ? "<root>" : path) + "' descendants=" + descendants +
                    ": index=" + indexed + ", sql=" + sql);
        }
        LocalChecks.Require(parityFailures.Count == 0,
            "the bounded index and SQL agree on near-miss prefixes for descendants=true and self/ancestor" +
            (parityFailures.Count == 0 ? string.Empty : ": " + string.Join("; ", parityFailures)));

        // Low migration fix: an older binary writes path_key as NULL/stale and leaves restore_hold at zero.
        var migrateState = Path.Combine(LocalChecks.FreshRoot("recovery-migrate-" + Guid.NewGuid().ToString("N")), "state");
        var migratePair = Pair("pair-migrate");
        string migrateRow;
        using (var first = SqliteStateStore.Open(migrateState, StorageChecks.Fast, new ManualClock()))
        {
            first.SavePair(migratePair);
            first.AddNotices([FrozenPending(migratePair.PairId, "Root/original", "migrate-recovery")]);
            migrateRow = Seed(first, migratePair, "migrate-row", Side.A, "Root/winner");
        }
        using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = SqliteStateStore.DatabasePath(migrateState), Pooling = false }.ToString()))
        {
            raw.Open();
            using (var command = raw.CreateCommand())
            {
                command.CommandText = "UPDATE notice SET path='Root/moved', path_key=NULL, restore_hold=0 " +
                    "WHERE reason_key='recovery-restore-pending';";
                command.ExecuteNonQuery();
            }
            using (var command = raw.CreateCommand())
            {
                command.CommandText = "UPDATE recovery SET path='Root/moved-winner', path_key=NULL WHERE recovery_id=$id;";
                command.Parameters.AddWithValue("$id", migrateRow);
                command.ExecuteNonQuery();
            }
        }
        using (var reopened = SqliteStateStore.Open(migrateState, StorageChecks.Fast, new ManualClock()))
        {
            LocalChecks.Require(reopened.RecoveryRestoreProtected(migratePair.PairId, "Root/moved") &&
                !reopened.RecoveryRestoreProtected(migratePair.PairId, "Root/original") &&
                reopened.HasPendingRestoreHolds(migratePair.PairId),
                "reopen recomputes a restore notice's path key and hold after an older binary wrote the row");
            LocalChecks.Require(reopened.RecoveryNewestForPath(migratePair.PairId, new RecoveryQuery(),
                    "Root/moved-winner")?.RecoveryId == migrateRow,
                "reopen fills a Recovery row's path key after an older binary inserted it");
        }

        Console.WriteLine("Recovery query checks passed.");
    }

    private static PairDef Pair(string pairId) => new(pairId, pairId,
        new RootDef(pairId + "-a", SyncProviders.Local, null, "/fixture/a", pairId + "-object-a"),
        new RootDef(pairId + "-b", SyncProviders.Local, null, "/fixture/b", pairId + "-object-b"), [], [], true);

    // The restore engine's create-only intent: rrs id and SuppressReconciledBaseline, the frozen-intent predicate.
    private static SyncAction FrozenIntent(string pairId, string recoveryId, string path) =>
        new(ShortId.Make("rrs", pairId, recoveryId), 0, ActionKind.CopyNew, Side.A, path, null, null, null, false, null)
            { SuppressReconciledBaseline = true };

    private static Notice FrozenPending(string pairId, string path, string recoveryId) =>
        new(pairId, 0, NoticeKind.UserDecision, path, "Pending fixture", "recovery-restore-pending",
            ConflictOperation: new ConflictNoticeOperation(recoveryId, path,
                [FrozenIntent(pairId, recoveryId, path)], []));

    // A failure before any write (CONFLICT, PROTECTED, NO-VERSION, FAILED): informational, never a hold.
    private static Notice ActionlessPending(string pairId, string path, string recoveryId) =>
        new(pairId, 0, NoticeKind.UserDecision, path, "Failed fixture", "recovery-restore-pending",
            ConflictOperation: new ConflictNoticeOperation(recoveryId, path, [], []));

    private static string Seed(SqliteStateStore store, PairDef pair, string actionId, Side side, string path)
    {
        var action = new SyncAction(actionId, 0, ActionKind.Update, side, path, null, null, null, false, actionId);
        store.CommitResult(pair.PairId, 1, action, new ActionResult(actionId, Outcome.Failed, null, null,
            new RecoveryRef(side, "recovery/" + actionId, null, "hash", 1), null));
        return ShortId.Make("rc", pair.PairId, actionId);
    }

    private static string[] Ids(IReadOnlyList<RecoveryRecord> page) =>
        page.Select(row => row.RecoveryId).ToArray();

    private static string[] Sorted(params string[] ids) =>
        ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();
}
