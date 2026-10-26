namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task UserChangePriorityChecksAsync()
    {
        await UrgentIncidentAsync();
        await UrgentBoundsAsync();
        await UrgentFairnessAsync();
        await UrgentMutationAsync();
        Console.WriteLine("PASS UserChangePriority: incident, retry, restart, handoff, rotation, gates and bounds");
    }

    private static async Task UrgentIncidentAsync()
    {
        var folder = FixtureFolder("urgent-incident");
        var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        try
        {
            var fx = Build(suppliedStore: store);
            foreach (var path in new[] { "Edit", "Rename", "Move", "Remote" })
            { fx.A.Seed(path, "original"); fx.B.Seed(path, "original"); }
            await FirstSyncAsync(fx.Run(), fx);
            SeedPriorityBacklog(store, fx.Pair);
            await MakePriorityChangesAsync(fx);
            var fullA = fx.A.FullCalls;
            var fullB = fx.B.FullCalls;
            var held = await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(fx.A.FullCalls == fullA && fx.B.FullCalls == fullB, "incident changes arrive through provider deltas, not bulk inventories");
            var expected = new[] { "New/File", "Edit", "Renamed", "Destination/Moved", "Remote" };
            AssertUrgentProtected(store, fx, expected, held);
            AssertPriorityQueryPlans(store);
            var count = store.UrgentPending(fx.Pair.PairId);
            var admitted = fx.Run().AdmitPlanningPage(fx.Pair.PairId);
            Require(admitted.Take((int)count).All(row => row.Reason == SqliteStateStore.UserChangeReason), "urgent refresh moves existing bulk rows ahead of bulk FIFO");
            Require(store.UrgentPending(fx.Pair.PairId) == count, "intake rotation preserves urgent provenance while proof is outstanding");
            store.Dispose();
            store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            Require(store.UrgentPending(fx.Pair.PairId) == count, "urgent provenance survives store reopen");
            await VerifyPriorityAsync(store, fx, expected);
            Require(store.UrgentPending(fx.Pair.PairId) == count, "index planning handoff preserves urgent provenance");
            var run = new SyncRun(store, NewExecutor(store), fx.Provider, Settings, TimeProvider.System);
            var applied = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            await AssertPriorityAppliedAsync(fx, applied);
            AssertPriorityRollback(store);
        }
        finally { store.Dispose(); DeleteFixture(folder); }
    }

    private static async Task MakePriorityChangesAsync(Fixture fx)
    {
        fx.A.Seed("New", "", ItemKind.Folder);
        fx.A.Seed("New/File", "new local");
        fx.A.Seed("Edit", "local edit");
        await fx.A.MoveAsync(new("rename", 9, ActionKind.Move, Side.A, "Renamed", "Rename", null, null, false, null), default);
        fx.A.Seed("Destination", "", ItemKind.Folder);
        await fx.A.MoveAsync(new("move", 9, ActionKind.Move, Side.A, "Destination/Moved", "Move", null, null, false, null), default);
        fx.B.Seed("Remote", "remote overwrite");
        fx.B.Seed("RemoteCreate", "remote create");
    }

    private static void AssertUrgentProtected(SqliteStateStore store, Fixture fx, string[] expected, RunReport report)
    {
        var urgent = PriorityRows(store, fx.Pair.PairId);
        // A protected baseline can retain the former spelling after a rename or move.
        // Intake resolves the entity's current aliases; the saved Path is not its identity.
        foreach (var path in expected)
        {
            var item = store.Live(fx.Pair.A.RootId, [path]).Single();
            Require(urgent.Any(row => row.RootId == fx.Pair.A.RootId && row.ObjectId == item.ObjectId && row.Paths.Contains(path)),
                "run delta deferral retains the local proof dependency and current alias: " + path);
        }
        Require(urgent.Any(row => row.RootId == fx.Pair.A.RootId && row.Paths.Contains("Remote")),
            "the remote overwrite delta prioritizes its unchanged local proof dependency");
        Require(expected.All(path => store.ProtectionBatch(fx.Pair.A.RootId, ItemFlags.NeedsRehash, [path])[0].Covered),
            "urgent scheduling never discharges verification protection");
        Require(report.UrgentPending == urgent.Count, "run summary reports pair urgentPending truthfully");
    }

    private static async Task AssertPriorityAppliedAsync(Fixture fx, RunReport report)
    {
        Require(report.Failed == 0, "urgent apply completes without item failures");
        foreach (var path in new[] { "New/File", "Edit", "Renamed", "Destination/Moved", "Remote", "RemoteCreate" })
        {
            var a = await fx.A.ObserveAsync(path, default);
            var b = await fx.B.ObserveAsync(path, default);
            Require(a is not null && b is not null && a.Hash == b.Hash, "each incident change is applied in the first run after verification: " + path);
        }
        Require(await fx.B.ObserveAsync("Rename", default) is null && await fx.B.ObserveAsync("Move", default) is null,
            "rename and move retire their old remote spellings");
    }

    private static async Task VerifyPriorityAsync(SqliteStateStore store, Fixture fx, string[] expected)
    {
        var io = new UrgentCheckIo(fx.A) { FailPath = "Edit" };
        var gaps = (ILocalGapStore)store;
        await using var worker = new LocalGapWorker(gaps, io, StorageFailureClassifier.IsStateDamage);
        var epoch = gaps.GapEpoch(fx.Pair.A.RootId)!;
        worker.Discover(fx.Pair.A, epoch, default);
        await worker.HashPage(fx.Pair.A, epoch, default);
        Require(io.Attempts.Count == expected.Length && io.Attempts.All(path => expected.Contains(path)), "active-input hashing attempts every urgent item before any bulk item");
        AssertUrgentFailure(store, fx.Pair.A.RootId, "Edit");
        var count = io.Attempts.Count;
        await worker.HashPage(fx.Pair.A, epoch, default);
        Require(io.Attempts.Count == count, "failed urgent hash obeys backoff and does not loop");
        store.RequestIndexRetry(fx.Pair.A.RootId);
        await using var indexing = new LocalIndexingRuns(store, (_, _) => Task.FromResult(0), worker, io, _ => PriorityCheckpoint);
        await indexing.RunAsync(fx.Pair.A, default);
        Require(expected.All(path => !store.ProtectionBatch(fx.Pair.A.RootId, ItemFlags.NeedsRehash, [path])[0].Covered),
            "real indexing run publishes verified proofs and hands them to planning");
        var budget = new LocalGapIo(store, Settings, new HashSet<string>(), () => true);
        Require(!await budget.Budget(1, default) && await budget.Budget(1, default, urgent: true),
            "production byte budget retains the bulk activity gate but bypasses it for urgent work");
    }

    private static NtfsUsnCheckpoint PriorityCheckpoint => new(1, 0, 200, 0);

    private sealed class UrgentCheckIo(FakeEndpoint endpoint) : ILocalGapIo
    {
        public bool Paused => true;
        public string? FailPath { get; set; }
        public string? ChangingPath { get; set; }
        public List<string> Attempts { get; } = [];
        public LocalGapProof Probe(RootDef root, ItemState item) => new(PriorityCheckpoint.Cursor,
            new NtfsFileUsn(1, 1, 100, item.Size, item.Mtime.UtcDateTime.ToFileTimeUtc()));
        public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct) =>
            Task.FromResult(new LocalGapHashResult(LocalGapHashOutcome.Paused));
        public async Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, bool urgent, CancellationToken ct)
        {
            Require(urgent, "active input never opens bulk content");
            Attempts.Add(item.Path);
            if (ChangingPath == item.Path)
            {
                endpoint.Seed(item.Path, "saved during hash " + Attempts.Count);
                return new(LocalGapHashOutcome.ChangedDuringRead);
            }
            if (FailPath == item.Path) { FailPath = null; return new(LocalGapHashOutcome.Unverifiable, Reason: "fixture read failure"); }
            var current = await endpoint.ObserveAsync(item.Path, ct);
            return new(LocalGapHashOutcome.Verified, current!.Hash, Probe(root, item));
        }
    }
}
