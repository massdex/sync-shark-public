using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>Fix1b: a collision successor retires its obsolete baseline in the same transaction that commits the
/// successor link. Fixtures use a real SQLite store, production planner/executor and fake endpoints only.</summary>
internal static class CollisionBaselineChecks
{
    private const string PairId = "p1";
    internal static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static V2Settings Settings => V2Settings.Default with { MaxWorkers = 1, ProgressInterval = TimeSpan.Zero };

    public static async Task RunAsync()
    {
        await CrashBeforeAdoptThenRetireAsync();
        await PhysicalUpdateBeforeResultThenReconcileAsync();
        Console.WriteLine("Collision baseline checks passed: Adopt and reconciled Update retire exactly one obsolete claim.");
    }

    /// <summary>No successor committed before the cold restart; the Adopt retains both claims until its Done commit
    /// removes exactly the obsolete one.</summary>
    private static async Task CrashBeforeAdoptThenRetireAsync()
    {
        var rig = NewRig("collision-baseline-adopt");
        var aBase = rig.A.Seed("p", "same", versionId: "a-base");
        var bBase = rig.B.Seed("p", "same", versionId: "b-base");
        var bOccupant = rig.B.Seed("q", "same", versionId: "b-occupant");
        var gen = SeedPair(rig);
        SeedBaseline(rig.Store, gen, aBase, bBase, "k1");
        SeedBaseline(rig.Store, gen, State("q", "id-a2", "22", bOccupant.Size),
            bOccupant with { Hash = "22", VersionId = "b-old" }, "k2");
        await rig.A.MoveAsync(new SyncAction("user-move", 9, ActionKind.Move, Side.A, "q", "p", aBase, aBase,
            false, null), CancellationToken.None);
        RequireSingleClaims(rig.Store, "seeded collision");

        var plan = await PlanNowAsync(rig.Store, rig, gen, CancellationToken.None);
        var adopt = plan.Actions.Single(action => action.Kind == ActionKind.Adopt);
        Require(adopt.ObsoleteBaselineKey == "k2" && plan.Actions.All(action => action.Kind != ActionKind.Forget),
            "the planned Adopt owns the obsolete baseline and no Forget action precedes it");

        // Cold restart before any successor commit: both baselines are still authoritative and singly claimed.
        rig.Store.Dispose();
        using var store = SqliteStateStore.Open(rig.Root, Settings, TimeProvider.System);
        rig.Store = store;
        RequireSingleClaims(store, "cold reopen before Adopt");
        var retried = await PlanNowAsync(store, rig, store.BeginGeneration(rig.Pair.A.RootId, "test"),
            CancellationToken.None);
        var retryAdopt = retried.Actions.Single(action => action.Kind == ActionKind.Adopt);
        Require(retryAdopt.ObsoleteBaselineKey == "k2", "the cold-reopened plan retains the same obsolete authority");

        var results = await ApplyAsync(new Executor(store, Settings, Admission()), retried, rig.WriterA, rig.WriterB);
        Require(results.Count(result => result.Outcome == Outcome.Done) == 2 &&
            results.All(result => result.Outcome == Outcome.Done),
            "Adopt and its vacating Delete both commit");
        Require(store.Baselines(PairId, ["k2"]).Count == 0 && store.Baselines(PairId, ["k1"]).Count == 1,
            "the Adopt retires k2 in its Done transaction and keeps one k1 link");
        RequireSingleClaims(store, "after Adopt");
        Require((await rig.B.ObserveAsync("p", CancellationToken.None)) is null,
            "the vacated old path is removed only after the Adopt link commits");
    }

    /// <summary>The Update's bytes reach the endpoint before the process dies; reconciliation observes them, commits
    /// Done once and retires the losing correspondence in that same transaction.</summary>
    private static async Task PhysicalUpdateBeforeResultThenReconcileAsync()
    {
        var rig = NewRig("collision-baseline-update");
        var folder = rig.B.Seed("F", "", ItemKind.Folder, versionId: "folder-b");
        var incoming = rig.B.Seed("F/c", "incoming", versionId: "incoming-b", mtime: T0.AddSeconds(10));
        var occupant = rig.A.Seed("G/c", "occupant", versionId: "occupant-a", mtime: T0);
        rig.A.Seed("G", "", ItemKind.Folder, versionId: "folder-a");
        var gen = SeedPair(rig);
        SeedBaseline(rig.Store, gen, State("F", "id-af", null, 0, ItemKind.Folder, "folder-a"), folder, "kf");
        SeedBaseline(rig.Store, gen, State("F/c", "id-ac", "00", incoming.Size),
            incoming with { Hash = "00", VersionId = "incoming-old" }, "kc");
        SeedBaseline(rig.Store, gen, occupant with { Hash = "00", VersionId = "occupant-old" },
            State("G/c", "id-bo", "00", occupant.Size), "ko");
        RequireSingleClaims(rig.Store, "seeded Update collision");

        var options = Options(firstSync: true);
        var rows = new[]
        {
            new PlanRow(new Baseline(PairId, "kf", State("F", "id-af", null, 0, ItemKind.Folder), folder, gen),
                State("G", "id-af", null, 0, ItemKind.Folder), folder, new Evidence(false, false, true, true)),
            new PlanRow(new Baseline(PairId, "kc", State("F/c", "id-ac", "00", incoming.Size),
                incoming with { Hash = "00" }, gen),
                null, incoming, new Evidence(false, false, true, true)),
            new PlanRow(new Baseline(PairId, "ko", occupant with { Hash = "00" },
                State("G/c", "id-bo", "00", occupant.Size), gen),
                occupant, null, new Evidence(false, false, true, true)),
        };
        var plan = Planner.Decide(PairId, gen, rows, options);
        var move = plan.Actions.Single(action => action.Kind == ActionKind.Move);
        var update = plan.Actions.Single(action => action.Kind == ActionKind.Update);
        Require(update.ObsoleteBaselineKey == "ko" && plan.Actions.All(action => action.Kind != ActionKind.Forget),
            "the winning Update owns the obsolete occupant baseline");

        // The folder move is physical and acknowledged; the Update's bytes land, but no result is committed.
        await rig.B.MoveAsync(move, CancellationToken.None);
        rig.Store.RecordIntent(PairId, update);
        var recovery = await rig.A.PreserveAsync(update.ExpectedTarget!, update.ActionId, CancellationToken.None);
        rig.Store.RecordRecovery(PairId, update.ActionId, update.ExpectedTarget!.Path, recovery);
        await rig.A.PutAsync(update, new MemoryStream(Encoding.UTF8.GetBytes("incoming")), CancellationToken.None);
        RequireSingleClaims(rig.Store, "physical Update before result");

        rig.Store.Dispose();
        using var store = SqliteStateStore.Open(rig.Root, Settings, TimeProvider.System);
        var executor = new Executor(store, Settings, Admission());
        var results = await executor.ReconcileAsync(store.UnresolvedIntents(PairId),
            store.BeginGeneration(rig.Pair.A.RootId, "test"), rig.WriterA, rig.WriterB, CancellationToken.None);
        var reconciled = results.Single();
        Require(reconciled.Outcome == Outcome.Done, "reconciliation observes the physically applied Update");
        Require(store.UnresolvedIntents(PairId).Count == 0, "the Update intent retires with its Done link");
        Require(store.Baselines(PairId, ["ko"]).Count == 0 && store.Baselines(PairId, ["kc"]).Count == 1,
            "the reconciled Done retires ko and retains the incoming link kc");
        RequireSingleClaims(store, "after reconciled Update");
    }

    // ---- fixtures ------------------------------------------------------------

    private sealed class Rig(string root, PairDef pair, FakeEndpoint a, FakeEndpoint b, CountingWriter writerA,
        CountingWriter writerB, FakeEndpointProvider provider)
    {
        public string Root { get; } = root;
        public PairDef Pair { get; } = pair;
        public FakeEndpoint A { get; } = a;
        public FakeEndpoint B { get; } = b;
        public CountingWriter WriterA { get; } = writerA;
        public CountingWriter WriterB { get; } = writerB;
        public FakeEndpointProvider Provider { get; } = provider;
        public SqliteStateStore Store { get; set; } = null!;
    }

    private static Rig NewRig(string name)
    {
        var root = LocalChecks.FreshRoot(name);
        var pair = new PairDef(PairId, PairId,
            new RootDef("ra", SyncProviders.Local, null, @"C:\a", "id:ra"),
            new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:rb"), [], [], true);
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var writerA = new CountingWriter(a);
        var writerB = new CountingWriter(b);
        var provider = new FakeEndpointProvider().Add(pair.A, a).Add(pair.B, b)
            .AddWriter(pair.A, writerA).AddWriter(pair.B, writerB);
        var rig = new Rig(root, pair, a, b, writerA, writerB, provider);
        rig.Store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
        return rig;
    }

    private static long SeedPair(Rig rig)
    {
        rig.Store.SavePair(rig.Pair);
        rig.Store.MarkFirstSyncDone(PairId);
        return rig.Store.BeginGeneration(rig.Pair.A.RootId, "test");
    }

    private static void SeedBaseline(IStateStore store, long gen, ItemState a, ItemState b, string key)
    {
        var adopt = new SyncAction("seed-" + key, 3, ActionKind.Adopt, Side.B, b.Path, null, a, b, false, key);
        store.RecordIntent(PairId, adopt);
        store.CommitResult(PairId, gen, adopt, new ActionResult(adopt.ActionId, Outcome.Done, a, b, null, null));
    }

    private static async Task<Plan> PlanNowAsync(IStateStore store, Rig rig, long gen, CancellationToken ct)
    {
        var snapshotA = await rig.A.FullAsync(gen, _ => true, new NullProgress(), ct);
        var snapshotB = await rig.B.FullAsync(gen, _ => true, new NullProgress(), ct);
        var deleted = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        var complete = new Completeness(true, true, new HashSet<string>(), new HashSet<string>());
        var rows = Planner.Join(store.Baselines(PairId), snapshotA.Items, snapshotB.Items, deleted, deleted, complete,
            firstSync: false);
        return Planner.Decide(PairId, gen, rows, Options(firstSync: false));
    }

    private static PlanOptions Options(bool firstSync) => new(Settings.TieWindow, firstSync,
        new LabelRule(Settings.KeepBothLabelFormat, Settings.KeepBothTimeFormat,
            TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow),
            SyncProviders.DisplayName(SyncProviders.Local), SyncProviders.DisplayName(SyncProviders.Dropbox)),
        Settings.MaxLabelAttempts)
    {
        ObservedAt = DateTimeOffset.UtcNow.AddMinutes(1),
        ProviderA = SyncProviders.Local,
        ProviderB = SyncProviders.Dropbox,
    };

    private static async Task<List<ActionResult>> ApplyAsync(Executor executor, Plan plan, IEndpointWriter a,
        IEndpointWriter b)
    {
        var results = new List<ActionResult>();
        await foreach (var result in executor.ApplyAsync(plan, a, b, CancellationToken.None)) results.Add(result);
        return results;
    }

    /// <summary>Every side/object pair is claimed by at most one baseline at every step.</summary>
    private static void RequireSingleClaims(IStateStore store, string step)
    {
        var baselines = store.Baselines(PairId);
        Require(baselines.GroupBy(baseline => baseline.A.ObjectId, StringComparer.Ordinal).All(group => group.Count() == 1),
            step + ": no object on side A has two baseline claims");
        Require(baselines.GroupBy(baseline => baseline.B.ObjectId, StringComparer.Ordinal).All(group => group.Count() == 1),
            step + ": no object on side B has two baseline claims");
    }

    private static ItemState State(string path, string id, string? hash, long size = 1,
        ItemKind kind = ItemKind.File, string? versionId = "v") =>
        new(path, kind, id, versionId, hash, size, T0, ItemFlags.None);

    private static SyncTransferResourceController Admission() => ExecutorChecks.Admission();

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private sealed class NullProgress : IProgress<string>
    {
        public void Report(string value) { }
    }
}
