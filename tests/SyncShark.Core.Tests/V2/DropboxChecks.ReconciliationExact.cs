using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    internal static async Task ReconciliationExactMetadataAsync()
    {
        using var h = new Harness();
        var directory = LocalChecks.FreshRoot("exact-dropbox-batch");
        using var store = SqliteStateStore.Open(Path.Combine(directory, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var local = LocalChecks.RootOf(directory, "ra");
        var pair = new PairDef("p1", "Pair", local, Root(), [], [], true);
        store.SavePair(pair);
        var fake = new FakeEndpoint(Side.B, RootId);
        var rows = Enumerable.Range(0, 5000).Select(index => fake.Seed($"row-{index:D5}.txt", "value")).ToList();
        rows.Add(fake.Seed("Missing", "", ItemKind.Folder));
        rows.Add(fake.Seed("Missing/child.txt", "retained"));
        rows.Add(fake.Seed("unknown.txt", "retained"));
        var gen = store.BeginGeneration(RootId, "fixture");
        store.WriteSnapshot(new(RootId, Side.B, gen, "cursor", rows, []));
        store.CompleteGeneration(gen, "cursor", true);
        h.Wire.MetadataFailurePath = DropboxWire.RootId + "/unknown.txt";
        var cloud = new DropboxInventory(Root(), Side.B, h.Client, store, pair.PairId);
        var provider = new FakeEndpointProvider().Add(local, new FakeEndpoint(Side.A, "ra")).Add(Root(), fake);
        var run = new SyncRun(store, new Executor(store, StorageChecks.Fast, ExecutorChecks.Admission(),
            isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException),
            provider, StorageChecks.Fast, TimeProvider.System);
        var paths = rows.Take(126).Select(item => item.Path).Concat(["Missing", "unknown.txt"]).ToArray();
        var pages = 0;
        store.PrefixHydrationPageForTest = () => pages++;
        var observedGen = store.BeginGeneration(RootId, "delta");
        var method = typeof(SyncRun).GetMethod("ReadDropboxExactScopesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(run, [Root(), cloud, cloud, paths, "cursor", observedGen,
            (Func<string, bool>)(_ => true), (Action<string>)(_ => { }), CancellationToken.None])!;
        Require(pages == 0 && h.Wire.ListRequests.Count == 0 && h.Wire.MetadataPaths.Distinct().Count() == paths.Length + 1,
            "a 128-path reconciliation batch uses exact metadata and never pages the 5000-row root inventory");
        Require(store.Live(RootId, ["Missing"]).Count == 1 && store.Live(RootId, ["unknown.txt"]).Count == 1 &&
            store.Live(RootId, ["Missing/child.txt"]).Count == 1,
            "a missing folder waits for recursive observation; network errors and unselected descendants remain live");
        var delta = await cloud.ReadExactAsync(["Missing", "unknown.txt"], "cursor", observedGen, _ => true, default);
        Require(delta.IncompleteSubtrees.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(["Missing", "unknown.txt"]) &&
            delta.Changes.All(change => change.Path != "unknown.txt" && change.Path != "Missing"),
            "failed metadata is explicitly incomplete, never a deletion candidate or observed receipt");
        cloud.ExactMetadataForTest = (_, _) => Task.FromResult<IReadOnlyDictionary<string, PilotRemoteEntry?>>(
            new Dictionary<string, PilotRemoteEntry?>());
        var omitted = await cloud.ReadExactAsync(["unknown.txt"], "cursor", observedGen, _ => true, default);
        store.WriteDelta(omitted);
        Require(omitted.IncompleteSubtrees.SequenceEqual(["unknown.txt"]) &&
            omitted.IncompleteReasons.ContainsKey("unknown.txt") && omitted.IncludesPath!("unknown.txt") == false &&
            omitted.Changes.Count == 0 && store.Live(RootId, ["unknown.txt"]).Count == 1,
            "an omitted metadata key without a failure callback is unknown, never an observed receipt or deletion");
    }
}
