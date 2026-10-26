using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    public static async Task NameMappingReviewAsync()
    {
        var failures = new List<string>();
        foreach (var (name, check) in new (string, Func<Task>)[] {
            ("held-alias-link", MappingHeldAliasAsync), ("cached-rows", MappingCachedAsync),
            ("alias-exclusions", MappingExclusionsAsync), ("notice-resolution", MappingResolutionAsync),
            ("occupied-move", MappingOccupiedAsync), ("indexed-context", MappingContextAsync),
            ("empty-folder", MappingEmptyFolderAsync), ("atomic-save", MappingAtomicSaveAsync),
            ("cached-blocks", MappingCachedBlocksAsync), ("linked-exclusion-triple", MappingLinkedExclusionTripleAsync),
            ("prepared-match-exclusion", MappingPreparedMatchExclusionAsync) })
        {
            try { await check(); Console.WriteLine("PASS mapping-review " + name); }
            catch (Exception e) { failures.Add(name + ": " + e.Message); Console.WriteLine("FAIL mapping-review " + failures[^1]); }
        }
        if (failures.Count != 0) throw new InvalidOperationException(string.Join("\n", failures));
    }

    private static async Task<(Fixture Fx, SyncRun Run, FakeEndpoint Local, FakeEndpoint Cloud)> MappingFixture(bool cloudOnA)
    {
        var fx = Build();
        if (cloudOnA)
        {
            var pair = fx.Pair with
            {
                A = fx.Pair.A with { Provider = SyncProviders.Dropbox, AccountId = "acc", RootPath = "/cloud" },
                B = fx.Pair.B with { Provider = SyncProviders.Local, AccountId = null, RootPath = "C:\\local" }
            };
            fx.Store.SavePair(pair);
            fx.Provider.Add(pair.A, fx.A).Add(pair.B, fx.B).AddWriter(pair.A, fx.WriterA).AddWriter(pair.B, fx.WriterB);
        }
        var local = cloudOnA ? fx.B : fx.A;
        var cloud = cloudOnA ? fx.A : fx.B;
        local.Seed("a_b", "same"); cloud.Seed("a:b", "same");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        Require(fx.Store.Baselines(fx.Pair.PairId).Count == 1, "mapping fixture adopted an existing pair");
        return (fx, run, local, cloud);
    }

    private static async Task MappingHeldAliasAsync()
    {
        foreach (var orientation in new[] { false, true })
        {
            var (fx, run, local, cloud) = await MappingFixture(orientation);
            cloud.Seed("a?b", "second");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var notices = fx.Store.OpenNotices(fx.Pair.PairId).Where(n => n.ReasonKey == "name-mapping-collision").ToArray();
            Require(notices.Any(n => n.Path == "a_b") && notices.Any(n => n.Path == "a:b") && notices.Any(n => n.Path == "a?b"),
                "held collision covers both endpoint spellings");
            // Check while the notices are open: hiding then auto-closing them would make a later check vacuous.
            var pending = typeof(SyncRun).GetMethod("PendingDecisions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Require(!((IEnumerable<string>)pending.Invoke(run, [fx.Pair.PairId, null])!).Any(),
                "open mapping holds are reevaluated, not fed into literal-path hiding");
            // A second ordinary delta must not turn a hidden counterpart into a literal underscore upload.
            local.Seed("a_b", "edited");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(await cloud.ObserveAsync("a_b", default) is null && fx.Store.Baselines(fx.Pair.PairId).Count == 1,
                "held aliases never upload an underscore duplicate or lose their baseline in either orientation");
            Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "name-mapping-collision"),
                "an unresolved collision remains visibly held after a subsequent delta");
        }
    }

    private static ItemState MappingItem(string path, string id, ItemKind kind = ItemKind.File) =>
        new(path, kind, id, "v", kind == ItemKind.File ? "11" : null, 1, DateTimeOffset.UnixEpoch, ItemFlags.None);

    private static (SqliteStateStore Store, PairDef Pair) MappingStore(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "mapping-review-" + name + "-" + Guid.NewGuid().ToString("N"));
        var store = SqliteStateStore.Open(root, Settings with { StoreDurableCommits = false }, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = Pair("mapping-review", "C:\\local", "/cloud");
        store.SavePair(pair);
        return (store, pair);
    }

    private static void MappingSnapshot(IStateStore store, RootDef root, Side side, params ItemState[] items)
    {
        var gen = store.BeginGeneration(root.RootId, "snapshot");
        store.WriteSnapshot(new(root.RootId, side, gen, "cursor", items, []));
        store.CompleteGeneration(gen, "cursor", true);
    }

    private static Task MappingCachedAsync()
    {
        var (store, pair) = MappingStore("cached");
        using (store)
        {
            MappingSnapshot(store, pair.A, Side.A, MappingItem("a_b", "local"));
            MappingSnapshot(store, pair.B, Side.B, MappingItem("a:b", "cloud") with { Flags = ItemFlags.Unreadable },
                MappingItem("CON:", "unsupported") with { Flags = ItemFlags.Unreadable });
            var scope = new RunScope(store, _ => true, _ => true).LoadTouched(pair, ["a_b"], [], []);
            Require(scope.B.Single().IdentityJson is not null && scope.B.Single().Flags == ItemFlags.None,
                "legacy invalid-name row derives its mapping and clears only the transferable name block");
            var plan = MappingPlan(scope);
            Require(plan.Actions.Single().Kind == ActionKind.Adopt, "cached delta adopts; never uploads underscore duplicate");
            var bad = DropboxNameMapping.FromStoredDropbox(MappingItem("CON/x:y", "bad") with { Flags = ItemFlags.Unreadable });
            Require(bad.Flags.HasFlag(ItemFlags.Unreadable), "reserved ancestor remains blocked after derivation");
        }
        return Task.CompletedTask;
    }

    private static Plan MappingPlan(RunScope.Scope scope)
    {
        var rows = Planner.Join(scope.Baselines, scope.A, scope.B, new Dictionary<string, DateTimeOffset?>(),
            new Dictionary<string, DateTimeOffset?>(), new(true, true, new HashSet<string>(), new HashSet<string>()), false,
            scope.ContextA, scope.ContextB, scope.ProtectedAliases);
        return Planner.Decide("mapping-review", 1, rows, new(TimeSpan.Zero, false,
            new("{name} [{side}]{ext}", "s", TimeSpan.Zero, "Local", "Dropbox"), 10)
        { MappingContextA = scope.ContextA, MappingContextB = scope.ContextB });
    }

    private static Task MappingExclusionsAsync()
    {
        var (store, pair) = MappingStore("exclude");
        using (store)
        {
            MappingSnapshot(store, pair.A, Side.A, MappingItem("a_b", "local"));
            MappingSnapshot(store, pair.B, Side.B, MappingItem("a:b", "cloud"), MappingItem("a?b", "other"));
            var scope = new RunScope(store, p => p != "a?b", p => p != "a?b").LoadTouched(pair, ["a_b"], [], []);
            var plan = MappingPlan(scope);
            Require(!plan.Notices.Any(n => n.ReasonKey == "name-mapping-collision") && plan.Actions.Single().Kind == ActionKind.Adopt,
                "excluding one ambiguous cloud entity resolves the collision without copying or deleting it");
            // Both-spelling propagation requires an established identity link, not just an unlinked match.
            var local = MappingItem("a_b", "local");
            var cloud = DropboxNameMapping.FromStoredDropbox(MappingItem("a:b", "cloud"));
            var adopt = new SyncAction("link", 3, ActionKind.Adopt, Side.B, cloud.Path, null, local, cloud, false, "linked");
            store.RecordIntent(pair.PairId, adopt);
            store.CommitResult(pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done, local, cloud, null, null));
            foreach (var excluded in new[] { "a_b", "a:b" })
            {
                MappingSnapshot(store, pair.B, Side.B, MappingItem("a:b", "cloud"));
                scope = new RunScope(store, p => p != excluded, p => p != excluded).LoadFull(pair);
                Require(scope.A.Single().Flags.HasFlag(ItemFlags.Ignored) && scope.B.Single().Flags.HasFlag(ItemFlags.Ignored),
                    "either spelling excludes both sides of the unambiguous mapping");
                Require(MappingPlan(scope).Actions.Count == 0, "excluded aliases produce no mapping hold or effects");
            }
            scope = new RunScope(store, _ => true, _ => true,
                rules: [new SyncIgnoreRule(SyncIgnoreKind.FileName, "a_b")]).LoadFull(pair);
            Require(scope.A.Single().Flags.HasFlag(ItemFlags.Ignored) && scope.B.Single().Flags.HasFlag(ItemFlags.Ignored),
                "ignore rules cover both mapped spellings");
            MappingSnapshot(store, pair.B, Side.B, cloud, MappingItem("a?b", "other"));
            scope = new RunScope(store, p => p != "a:b", p => p != "a:b").LoadFull(pair);
            Require(scope.A.Single().Flags.HasFlag(ItemFlags.Ignored) &&
                MappingPlan(scope).Actions.All(a => a.Kind == ActionKind.Hold) && store.Baselines(pair.PairId).Count == 1,
                "excluding a bound cloud entity keeps its local counterpart and link unavailable to a different cloud alias");
        }
        return Task.CompletedTask;
    }

    private static async Task MappingResolutionAsync()
    {
        var (fx, run, _, cloud) = await MappingFixture(true);
        var other = cloud.Seed("a?b", "second");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        await cloud.MoveAsync(new("rename", 1, ActionKind.Move, Side.A, "unique", "a?b", null, other, false, null), default);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(!fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "name-mapping-collision"),
            "rename reevaluates and closes old and counterpart mapping notices");
        // Exclusion is also a resolving choice, even with an otherwise empty change feed.
        cloud.Seed("a?b", "another");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var pair = fx.Store.Pair(fx.Pair.PairId)!;
        fx.Store.SavePair(pair with { Excluded = ["a?b"] });
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(!fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "name-mapping-collision"),
            "exclude choice closes mapping notices without requiring another inventory change");
    }

    private static Task MappingOccupiedAsync()
    {
        foreach (var identical in new[] { false, true })
        {
            var local = MappingItem("x/a_b", "local");
            var original = DropboxNameMapping.FromStoredDropbox(MappingItem("x/a:b", "cloud"));
            var moved = DropboxNameMapping.FromStoredDropbox(MappingItem("y/a:b", "cloud"));
            var occupant = MappingItem("y/a_b", "unrelated") with { Hash = identical ? "11" : "22" };
            var scope = new RunScope.Scope([local, occupant], [moved], [new("mapping-review", "linked", local, original, 1)]);
            var plan = MappingPlan(scope);
            Require(plan.Actions.Count > 0 && plan.Actions.All(a => a.Kind == ActionKind.Hold),
                "mapped move onto unrelated underscore is held, never merged or labelled, including equal bytes");
        }
        return Task.CompletedTask;
    }

    private static Task MappingContextAsync()
    {
        var (store, pair) = MappingStore("context");
        using (store)
        {
            MappingSnapshot(store, pair.B, Side.B, MappingItem("folder:", "folder", ItemKind.Folder),
                MappingItem("folder:/child", "child"), MappingItem("ordinary/deep/unrelated", "unrelated"));
            var recorder = new RecordingStore(store);
            var scope = new RunScope(recorder, _ => true, _ => true).LoadTouched(pair, ["folder_/child"], [], []);
            Require(recorder.ChildDirectories.Count == 0, "nonalias leaf resolves mapped ancestors only by indexed point lookups");
            Require(scope.B.Count == 1 && scope.B[0].Path == "folder:/child" && scope.ContextB.Single().Path == "folder:",
                "ancestor context is separate from planner rows");
            var linked = new RunScope(recorder, _ => true, _ => true, LinkExclusions.None.WithPending(["folder:"]));
            Require(linked.LoadTouched(pair, ["folder_/child"], [], []).ContextB.Count == 0,
                "protected ancestor is filtered out of mapping context too");
        }
        return Task.CompletedTask;
    }
}
