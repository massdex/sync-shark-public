using System.Collections;
using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // Called by the main check runner; no timing/GC thresholds or large synthetic inventories.
    public static async Task MemoryReductionChecksAsync()
    {
        Scope_ReusesUnchangedRows();
        Scope_RefreshesOnlyHashEvidence();
        Scope_RefreshesMappedHashEvidence();
        CollisionFastPath_MatchesTwoDrafts();
        Draft_FinalizesOnlyOnce();
        PairedHashDiscovery_DoesNotDraft();
        Scope_WithoutHeldReusesInput();
        await Preview_ApplyUsesLookupWithoutRecapturingMatchesAsync();
        Console.WriteLine("PASS MemoryReductionChecks");
    }

    private static void Scope_ReusesUnchangedRows()
    {
        using var store = new InMemoryStateStore();
        var item = new ItemState("kept.txt", ItemKind.File, "id", null, null, 1,
            DateTimeOffset.UnixEpoch, ItemFlags.None);
        IReadOnlyList<ItemState> items = new[] { item };
        var hide = typeof(RunScope).GetMethod("Hide", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var scope = new RunScope(store, _ => true, _ => true);
        Require(ReferenceEquals(items, hide.Invoke(scope, [items, Side.A, null])),
            "unchanged scope returns its input collection");

        var ignored = item with { Flags = ItemFlags.Ignored };
        IReadOnlyList<ItemState> ignoredItems = new[] { ignored };
        var excluding = new RunScope(store, _ => false, _ => false);
        Require(ReferenceEquals(ignoredItems, hide.Invoke(excluding, [ignoredItems, Side.A, null])),
            "already-ignored rows need no flags clone or collection");
        var changed = (IReadOnlyList<ItemState>)hide.Invoke(excluding, [items, Side.A, null])!;
        Require(changed.Count == 1 && changed[0].Flags == ItemFlags.Ignored && item.Flags == ItemFlags.None,
            "new ignored flags clone the row without mutating input or adding carried-ignore");
    }

    private static void PairedHashDiscovery_DoesNotDraft()
    {
        const int count = 10_000;
        var rows = new PlanRow[count];
        var evidence = new Evidence(false, false, true, true);
        for (var i = 0; i < count; i++)
        {
            var a = new ItemState($"file-{i:D7}.txt", ItemKind.File, $"a-{i}", null, null, 1,
                DateTimeOffset.UnixEpoch, ItemFlags.None);
            rows[i] = new PlanRow(null, a, a with { ObjectId = $"b-{i}", Hash = new string('a', 64) }, evidence);
        }
        Planner.HashRequests([rows[0]]); // warm the code before measuring managed allocations
        var before = GC.GetAllocatedBytesForCurrentThread();
        var request = Planner.HashRequests(rows);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(request.PathsA.Count == count && request.PathsB.Count == 0,
            "paired hash discovery keeps every and only missing hash");
        Require(allocated < count * 512L, "paired hash discovery does not allocate two action drafts");
        rows[^1] = rows[^1] with { B = null };
        rows[^2] = rows[^2] with
        {
            A = rows[^2].A! with { Kind = ItemKind.Folder },
            B = rows[^2].B! with { Kind = ItemKind.Folder }
        };
        before = GC.GetAllocatedBytesForCurrentThread();
        request = Planner.HashRequests(rows);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(request.PathsA.Count == count - 2 && request.PathsB.Count == 0,
            "unlinked folders and one-sided files do not introduce collision hash needs");
        Require(allocated < count * 512L, "mixed unlinked discovery does not allocate two action drafts");
    }

    private static void Scope_RefreshesOnlyHashEvidence()
    {
        var fx = Build();
        var item = new ItemState("hash.txt", ItemKind.File, "hash-id", null, null, 1,
            DateTimeOffset.UnixEpoch, ItemFlags.None);
        var unchanged = item with { Path = "kept.txt", ObjectId = "kept-id" };
        fx.Store.WriteSnapshot(new Snapshot(fx.Pair.A.RootId, Side.A, 1, "", [item, unchanged], []));
        var scanner = new RunScope(fx.Store, path => path != "hash.txt", _ => true);
        var scope = scanner.LoadFull(fx.Pair);
        var hashed = item with { Hash = new string('a', 64) };
        fx.Store.WriteDelta(new Delta(fx.Pair.A.RootId, Side.A, 1, "", "",
            [new Change(ChangeKind.Upsert, item.ObjectId, item.Path, hashed, null)], false, []));
        var refreshed = scanner.RefreshHashes(fx.Pair, scope, new HashRequest([item.Path], []));
        var reloaded = scanner.LoadFull(fx.Pair);
        Require(refreshed.A.SequenceEqual(reloaded.A), "targeted hash refresh equals a full normalized reload");
        Require(ReferenceEquals(scope.B, refreshed.B) && ReferenceEquals(scope.Baselines, refreshed.Baselines),
            "hash refresh retains the other side and baselines");
        Require(ReferenceEquals(scope.A[1], refreshed.A[1]) && scope.A[0].Hash is null,
            "hash refresh retains unchanged entities without mutating earlier planning input");
        Require((refreshed.A[0].Flags & ItemFlags.Ignored) != 0, "hash refresh retains scope-only exclusions");
        Require(ReferenceEquals(refreshed.A, scanner.RefreshHashes(fx.Pair, refreshed,
            new HashRequest([item.Path], [])).A), "unchanged hash evidence retains its input collection");
    }

    private static void Scope_RefreshesMappedHashEvidence()
    {
        var fx = Build();
        var pair = fx.Pair with { A = fx.Pair.A with { Provider = SyncProviders.Local },
            B = fx.Pair.B with { Provider = SyncProviders.Dropbox } };
        var item = new ItemState("mapped:name.txt", ItemKind.File, "mapped-id", null, null, 1,
            DateTimeOffset.UnixEpoch, ItemFlags.Unreadable | ItemFlags.NeedsRehash);
        fx.Store.WriteSnapshot(new Snapshot(pair.B.RootId, Side.B, 1, "", [item], []));
        var scanner = new RunScope(fx.Store, _ => true, _ => false);
        var scope = scanner.LoadFull(pair);
        Require((scope.B.Single().Flags & ItemFlags.Unreadable) != 0, "unhashed legacy mapped name stays unreadable");
        var hashed = item with { Hash = new string('a', 64), Flags = ItemFlags.Unreadable };
        fx.Store.WriteDelta(new Delta(pair.B.RootId, Side.B, 1, "", "",
            [new Change(ChangeKind.Upsert, item.ObjectId, item.Path, hashed, null)], false, []));
        var refreshed = scanner.RefreshHashes(pair, scope, new HashRequest([], [item.Path]));
        Require(refreshed.B.SequenceEqual(scanner.LoadFull(pair).B), "Windows mapped hash refresh equals full reload");
        Require((refreshed.B.Single().Flags & (ItemFlags.Unreadable | ItemFlags.NeedsRehash)) == 0,
            "stored normalized hash clears legacy unreadable and rehash evidence");
        Require((refreshed.B.Single().Flags & ItemFlags.Ignored) != 0 &&
            DropboxNameMapping.LocalPath(refreshed.B.Single()) == "mapped_name.txt",
            "mapped refresh preserves scope exclusion and mapping identity");
        fx.Store.Dispose();
    }

    private static void CollisionFastPath_MatchesTwoDrafts()
    {
        var only = typeof(Planner).GetMethod("OnlyUnlinkedPaths", BindingFlags.Static | BindingFlags.NonPublic)!;
        var fast = typeof(Planner).GetMethod("CollisionHashNeeds", BindingFlags.Static | BindingFlags.NonPublic)!;
        var draft = typeof(Planner).GetMethod("BuildDraft", BindingFlags.Static | BindingFlags.NonPublic)!;
        var random = new Random(73191);
        for (var sample = 0; sample < 300; sample++)
        {
            var rows = new List<PlanRow>();
            var count = random.Next(1, 100);
            for (var i = 0; i < count; i++)
            {
                var kind = random.Next(4) == 0 ? ItemKind.Folder : ItemKind.File;
                var path = i % 2 == 0 ? $"folder-{i / 2}" : $"folder-{i / 2}/file.txt";
                ItemState Item(string side) => new(path, kind, $"{side}-{i}", null,
                    random.Next(3) == 0 ? null : new string(random.Next(2) == 0 ? 'a' : 'b', 64),
                    random.Next(3), DateTimeOffset.UnixEpoch.AddSeconds(random.Next(100)),
                    new[] { ItemFlags.None, ItemFlags.Ignored, ItemFlags.Unreadable, ItemFlags.NeedsRehash }[random.Next(4)]);
                var presence = random.Next(3);
                rows.Add(new PlanRow(null, presence == 0 ? null : Item("a"), presence == 1 ? null : Item("b"),
                    new Evidence(random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0)));
            }
            Require((bool)only.Invoke(null, [rows])!, "random fixture satisfies fast-path preconditions");
            var actual = ((IEnumerable<(Side, string)>)fast.Invoke(null, [rows])!).ToHashSet();
            var expected = new HashSet<(Side, string)>();
            foreach (var firstSync in new[] { false, true })
            {
                var options = new PlanOptions(TimeSpan.Zero, firstSync,
                    new LabelRule("{name} [{side}]{ext}", "s", TimeSpan.Zero, "A", "B"), int.MaxValue);
                var context = draft.Invoke(null, [string.Empty, rows, options])!;
                expected.UnionWith((IEnumerable<(Side, string)>)context.GetType().GetProperty("HashNeeds")!.GetValue(context)!);
            }
            Require(actual.SetEquals(expected), "collision fast path matches old two-draft discovery on randomized inputs");
        }
    }

    private static void Draft_FinalizesOnlyOnce()
    {
        var action = new SyncAction("s0", 18, ActionKind.CopyNew, Side.A, "x", null, null, null, false, null);
        action.FinalizeDraft("durable-id", false);
        var rejected = false;
        try { action.FinalizeDraft("second-id", true); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected && action.ActionId == "durable-id" && !action.OppositeAbsent,
            "a finalized action cannot be mutated again");
    }

    private static void Scope_WithoutHeldReusesInput()
    {
        var scope = new RunScope.Scope([], [], []);
        var method = typeof(SyncRun).GetMethod("WithoutHeld", BindingFlags.Static | BindingFlags.NonPublic)!;
        Require(ReferenceEquals(scope, method.Invoke(null, [scope, Array.Empty<HeldDeletion>()])),
            "zero held paths retain the original scope");
    }

    private static async Task Preview_ApplyUsesLookupWithoutRecapturingMatchesAsync()
    {
        var fx = Build();
        fx.A.Seed("shared.txt", "same");
        fx.B.Seed("shared.txt", "same");
        fx.A.Seed("local-only.txt", "local");
        var run = fx.Run();
        var prepared = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(prepared.MatchedBothSides.Count == 1, "prepare still captures matched rows");
        var lookup = new LookupOnlyPrepareMatches();
        var (applied, rebuildables) = await run.PreviewFirstSyncApplyResolvedAsync(fx.Pair.PairId,
            null, null, [], lookup, new RecordingProgress(), CancellationToken.None);
        Require(applied.MatchedBothSides.Count == 0, "apply preview does not recapture matched rows");
        Require(ReferenceEquals(applied.FirstSyncOptions?.MatchedAtPrepare, lookup),
            "deferred replans retain the original prepared lookup");
        Require(lookup.ContainsCalls > 0, "deletion-probe exclusion queries ContainsKey without enumeration");
        Require(rebuildables.Cloud == Side.B && rebuildables.Selected.Count == 0,
            "empty frozen rebuildables retain the cloud side");
        Require(Decisions(prepared) == Decisions(applied), "memory changes preserve the preview's actions");
    }

    private sealed class LookupOnlyPrepareMatches : IReadOnlyDictionary<string, PrepareMatch>
    {
        public int ContainsCalls { get; private set; }
        public int Count => 0;
        public IEnumerable<string> Keys => throw new InvalidOperationException("Do not enumerate prepared keys");
        public IEnumerable<PrepareMatch> Values => throw new InvalidOperationException("Do not enumerate prepared values");
        public PrepareMatch this[string key] => throw new KeyNotFoundException();
        public bool ContainsKey(string key) { ContainsCalls++; return false; }
        public bool TryGetValue(string key, out PrepareMatch value) { value = null!; return false; }
        public IEnumerator<KeyValuePair<string, PrepareMatch>> GetEnumerator() =>
            throw new InvalidOperationException("Do not enumerate prepared matches");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
