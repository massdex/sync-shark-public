namespace Massdex.SyncShark.V2;

/// <summary>
/// FIX1 checks: rule 27 replaces the conflict correspondence with two frozen create-only successor intents in one
/// Storage transaction, and a cold restart resumes only a verified, not-applied, untagged-provider successor.
/// These use the production planner, executor and SqliteStateStore over fake endpoints; no live pair or state.db.
/// </summary>
internal static class ConflictTransitionChecks
{
    private const string PairId = "p1";
    private const string OriginalPath = "file.txt";
    private const string SharedKey = "shared";
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static V2Settings Settings => V2Settings.Default with
    {
        MaxWorkers = 1,
        ProgressInterval = TimeSpan.Zero,
    };

    public static async Task RunAsync()
    {
        await BeforeEitherCopyAsync();
        await AfterOneCopyAsync();
        await OccupiedTargetAsync();
        await OrdinaryNotAppliedStaysSkippedAsync();
        Console.WriteLine("Conflict transition checks passed: cold-reopen resume, one-copy observation, occupancy and ordinary intent.");
    }

    /// <summary>Forget committed, neither copy attempted; a cold restart resumes both exactly once.</summary>
    private static async Task BeforeEitherCopyAsync()
    {
        var rig = NewRig("conflict-transition-before-copy");
        Plan plan = null!;
        SyncAction forget = null!;
        SyncAction[] successors = [];
        var store = SqliteStateStore.Open(rig.Root, Settings, TimeProvider.System);
        try
        {
            var gen = SeedBaseline(store, rig);
            (plan, forget, successors) = await ConflictPlanAsync(store, rig, gen, CancellationToken.None);

            rig.WriterA.PutFailure = FailConflictCopy;
            rig.WriterB.PutFailure = FailConflictCopy;
            var results = await ApplyAsync(new Executor(store, Settings, Admission()), plan, rig.WriterA, rig.WriterB);
            Require(results.Count(result => result.Outcome == Outcome.Done) == 1 &&
                results.Count(result => result.Outcome == Outcome.Failed) == 2,
                "forget commits Done while both create-only successors fail before any copy exists");
            Require(store.UnresolvedIntents(PairId).Count == 2 &&
                store.UnresolvedIntents(PairId).All(intent => intent.ConflictTransitionKey == forget.ObjectKey),
                "both frozen successors remain tagged and outstanding for the next run");
            Require(store.Baselines(PairId, [SharedKey]).Count == 0,
                "the replaced correspondence is forgotten in the same transition");
        }
        finally
        {
            rig.WriterA.PutFailure = null;
            rig.WriterB.PutFailure = null;
            store.Dispose();
        }
        Require(await rig.A.ObserveAsync(successors.Single(action => action.Target == Side.A).Path, CancellationToken.None) is null &&
            await rig.B.ObserveAsync(successors.Single(action => action.Target == Side.B).Path, CancellationToken.None) is null,
            "no conflict copy reached either endpoint before the restart");

        using var reopened = SqliteStateStore.Open(rig.Root, Settings, TimeProvider.System);
        var run = NewRun(reopened, rig);
        var report = await run.IncrementalAsync(PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.FailureCount == 0 && report.KeptBoth == 0, "the resumed create-only copies complete without new failures");

        await AssertResolvedAsync(reopened, rig, forget, successors);
        var next = await PlanNowAsync(reopened, rig, reopened.BeginGeneration(rig.Pair.A.RootId, "test"), CancellationToken.None);
        Require(next.Actions.Count == 0 && next.Actions.All(action => action.Rule != 4),
            "the next plan neither duplicates a conflict copy nor turns the originals into a newest-wins update");

        var after = await MapAsync(rig.A);
        var second = await run.IncrementalAsync(PairId, new RecordingProgress(), CancellationToken.None);
        Require(second.FailureCount == 0 && second.KeptBoth == 0 && SameMap(after, await MapAsync(rig.A)),
            "a further run leaves both sides exactly as the resumed transition wrote them");
    }

    /// <summary>Forget committed and one copy exists; the verified-absent successor is attempted once, the committed one reused.</summary>
    private static async Task AfterOneCopyAsync()
    {
        var rig = NewRig("conflict-transition-after-one-copy");
        Plan plan = null!;
        SyncAction forget = null!;
        SyncAction[] successors = [];
        var store = SqliteStateStore.Open(rig.Root, Settings, TimeProvider.System);
        try
        {
            var gen = SeedBaseline(store, rig);
            (plan, forget, successors) = await ConflictPlanAsync(store, rig, gen, CancellationToken.None);

            // One target keeps its copy; the other fails before its effect, so the restart sees one created copy
            // and one verified-not-applied create-only successor.
            var failedTarget = successors[0].Target;
            if (failedTarget == Side.A) rig.WriterA.PutFailure = FailConflictCopy;
            else rig.WriterB.PutFailure = FailConflictCopy;
            var results = await ApplyAsync(new Executor(store, Settings, Admission()), plan, rig.WriterA, rig.WriterB);
            Require(results.Count(result => result.Outcome == Outcome.Done) == 2 &&
                results.Count(result => result.Outcome == Outcome.Failed) == 1,
                "forget and one conflict copy commit while the other successor fails");
            Require(store.UnresolvedIntents(PairId).Count == 1,
                "only the failed successor stays outstanding across the restart");
            Require(store.Baselines(PairId, [SharedKey]).Count == 0, "the original correspondence stays forgotten");
        }
        finally
        {
            rig.WriterA.PutFailure = null;
            rig.WriterB.PutFailure = null;
            store.Dispose();
        }

        using var reopened = SqliteStateStore.Open(rig.Root, Settings, TimeProvider.System);
        var putsBefore = rig.WriterA.Puts + rig.WriterB.Puts;
        var run = NewRun(reopened, rig);
        var report = await run.IncrementalAsync(PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.FailureCount == 0 && report.KeptBoth == 0, "the missing successor is created once without another failure");
        Require(rig.WriterA.Puts + rig.WriterB.Puts == putsBefore + 1,
            "cold reconcile writes exactly the one missing copy and never rewrites the completed one");

        await AssertResolvedAsync(reopened, rig, forget, successors);
        var next = await PlanNowAsync(reopened, rig, reopened.BeginGeneration(rig.Pair.A.RootId, "test"), CancellationToken.None);
        Require(next.Actions.Count == 0 && next.Actions.All(action => action.Rule != 4),
            "the next plan neither duplicates the observed copy nor chooses a newest-wins original");
    }

    /// <summary>An occupied destination retains the successor under a Retry-only notice until the user frees it.</summary>
    private static async Task OccupiedTargetAsync()
    {
        var rig = NewRig("conflict-transition-occupied");
        Plan plan = null!;
        SyncAction forget = null!;
        SyncAction[] successors = [];
        ItemState occupant = null!;
        var store = SqliteStateStore.Open(rig.Root, Settings, TimeProvider.System);
        try
        {
            var gen = SeedBaseline(store, rig);
            (plan, forget, successors) = await ConflictPlanAsync(store, rig, gen, CancellationToken.None);
            var blocked = successors[0];
            var blockedEndpoint = blocked.Target == Side.A ? rig.A : rig.B;
            occupant = blockedEndpoint.Seed(blocked.Path, "occupied destination", versionId: "occupant");

            rig.WriterA.PutFailure = FailConflictCopy;
            rig.WriterB.PutFailure = FailConflictCopy;
            await ApplyAsync(new Executor(store, Settings, Admission()), plan, rig.WriterA, rig.WriterB);
            Require(store.UnresolvedIntents(PairId).Count == 2, "both successors remain unresolved before the restart");
        }
        finally
        {
            rig.WriterA.PutFailure = null;
            rig.WriterB.PutFailure = null;
            store.Dispose();
        }

        using var reopened = SqliteStateStore.Open(rig.Root, Settings, TimeProvider.System);
        var run = NewRun(reopened, rig);
        var report = await run.IncrementalAsync(PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.KeptBoth == 0 && report.Uncertain >= 1, "the occupied successor stays pending, never retired as not applied");

        var blocked = successors[0];
        var other = successors[1];
        var blockedEndpoint = blocked.Target == Side.A ? rig.A : rig.B;
        var otherEndpoint = other.Target == Side.A ? rig.A : rig.B;
        Require((await blockedEndpoint.ObserveAsync(blocked.Path, CancellationToken.None))?.Hash == occupant.Hash,
            "the occupant is never overwritten");
        Require((await otherEndpoint.ObserveAsync(other.Path, CancellationToken.None))?.Hash == other.Source!.Hash,
            "the other create-only successor completes normally");
        var originalA = await rig.A.ObserveAsync(OriginalPath, CancellationToken.None);
        var originalB = await rig.B.ObserveAsync(OriginalPath, CancellationToken.None);
        Require(originalA?.Hash == Hash("edit-a") && originalB?.Hash == Hash("edit-b"),
            "both originals keep their own differing content");

        var pending = reopened.UnresolvedIntents(PairId).Single();
        Require(pending.ActionId == blocked.ActionId && pending.ConflictTransitionKey == forget.ObjectKey,
            "the occupied successor retains its frozen intent and transition key");
        var notice = reopened.OpenNotices(PairId).Single(item => item.ReasonKey == ExecutionReasons.ConflictCopyBlocked);
        Require(notice.Path == blocked.Path,
            "the occupied destination raises the dedicated keep-both-copy-blocked reason at the copy path");
        Require(NoticeChoices.IsRetryNotice(notice) &&
            NoticeChoices.Options(notice).Select(option => option.Choice).SequenceEqual(new[] { NoticeChoices.RetryNow }),
            "the blocked copy offers Retry only, never Ignore or a decision choice");

        // The user moves the occupant away, then asks the notice to Retry. The next run must create the retained
        // copy, close the notice and retire the intent.
        var movedPath = "excluded-away/occupant.txt";
        await blockedEndpoint.MoveAsync(new SyncAction("user-moved-occupant", 0, ActionKind.Move, blocked.Target,
            movedPath, blocked.Path, occupant, occupant, false, null), CancellationToken.None);
        _ = await run.ApplyRetryNoticeChoiceAsync(PairId, notice.NoticeId!, NoticeChoices.RetryNow, CancellationToken.None);
        var retried = await run.IncrementalAsync(PairId, new RecordingProgress(), CancellationToken.None);
        Require(retried.FailureCount == 0 && retried.KeptBoth == 0, "Retry completes the retained copy without a new failure");
        Require((await blockedEndpoint.ObserveAsync(blocked.Path, CancellationToken.None))?.Hash == blocked.Source!.Hash,
            "the retained create-only copy lands once the destination is free");
        Require(reopened.NoticeById(PairId, notice.NoticeId!)!.Value.Closed,
            "the blocked-copy notice closes automatically when the retained copy succeeds");
        Require(reopened.UnresolvedIntents(PairId).Count == 0,
            "the successor intent is gone once its copy is committed");

        var before = (await MapAsync(rig.A), await MapAsync(rig.B));
        var second = await run.IncrementalAsync(PairId, new RecordingProgress(), CancellationToken.None);
        Require(second.KeptBoth == 0 && SameMap(before.Item1, await MapAsync(rig.A)) &&
            SameMap(before.Item2, await MapAsync(rig.B)),
            "the next run is clean: no duplicate copy, no newest-wins and no residual conflict work");
        Require(originalA?.Hash == Hash("edit-a") && originalB?.Hash == Hash("edit-b"),
            "both originals still keep their own content after the retried copy");
    }

    /// <summary>An ordinary not-applied intent is still skipped, never resumed through the transition path.</summary>
    private static async Task OrdinaryNotAppliedStaysSkippedAsync()
    {
        var rig = NewRig("conflict-transition-ordinary");
        using (var store = SqliteStateStore.Open(rig.Root, Settings, TimeProvider.System))
        {
            store.SavePair(rig.Pair);
            store.MarkFirstSyncDone(PairId);
            var source = rig.A.Seed("source.txt", "ordinary source", versionId: "source-1");
            var ordinary = new SyncAction("ordinary-keepboth", 17, ActionKind.KeepBoth, Side.B, "ordinary copy.txt",
                null, source, null, false, PlanIds.ObjectKey(PairId, Side.A, source.ObjectId));
            store.RecordIntent(PairId, ordinary);
        }
        using var reopened = SqliteStateStore.Open(rig.Root, Settings, TimeProvider.System);
        var gen = reopened.BeginGeneration(rig.Pair.A.RootId, "test");
        var executor = new Executor(reopened, Settings, Admission());
        var results = await executor.ReconcileAsync(reopened.UnresolvedIntents(PairId), gen, rig.WriterA, rig.WriterB,
            CancellationToken.None);
        var result = results.Single();
        Require(result.Outcome == Outcome.Skipped && result.Reason == EffectResolver.NotApplied,
            "an ordinary untagged not-applied intent remains skipped after a cold restart");
        Require(await rig.B.ObserveAsync("ordinary copy.txt", CancellationToken.None) is null,
            "ordinary reconciliation still performs no effect");
        Require(reopened.UnresolvedIntents(PairId).Count == 0,
            "the ordinary intent retires exactly as before, without the conflict-transition exception");
    }

    // ---- fixture ------------------------------------------------------------

    private sealed record Rig(string Root, PairDef Pair, FakeEndpoint A, FakeEndpoint B, CountingWriter WriterA,
        CountingWriter WriterB, FakeEndpointProvider Provider);

    private static Rig NewRig(string name)
    {
        var root = LocalChecks.FreshRoot(name);
        var pair = new PairDef(PairId, PairId,
            new RootDef("ra", SyncProviders.Local, null, @"C:\a", "id:ra"),
            new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:rb"),
            ["excluded-away"], [], true);
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var writerA = new CountingWriter(a);
        var writerB = new CountingWriter(b);
        var provider = new FakeEndpointProvider().Add(pair.A, a).Add(pair.B, b)
            .AddWriter(pair.A, writerA).AddWriter(pair.B, writerB);
        return new Rig(root, pair, a, b, writerA, writerB, provider);
    }

    private static long SeedBaseline(IStateStore store, Rig rig)
    {
        store.SavePair(rig.Pair);
        store.MarkFirstSyncDone(PairId);
        var baseA = rig.A.Seed(OriginalPath, "base", versionId: "base-a", mtime: T0);
        var baseB = rig.B.Seed(OriginalPath, "base", versionId: "base-b", mtime: T0);
        var gen = store.BeginGeneration(rig.Pair.A.RootId, "test");
        var adopt = new SyncAction("seed-" + SharedKey, 3, ActionKind.Adopt, Side.B, OriginalPath, null, baseA, baseB,
            false, SharedKey);
        store.RecordIntent(PairId, adopt);
        store.CommitResult(PairId, gen, adopt, new ActionResult(adopt.ActionId, Outcome.Done, null, baseB, null, null));
        rig.A.Seed(OriginalPath, "edit-a", versionId: "edit-a", mtime: T0.AddMinutes(5));
        rig.B.Seed(OriginalPath, "edit-b", versionId: "edit-b", mtime: T0.AddMinutes(10));
        return gen;
    }

    private static async Task<(Plan Plan, SyncAction Forget, SyncAction[] Successors)> ConflictPlanAsync(
        IStateStore store, Rig rig, long gen, CancellationToken ct)
    {
        var plan = await PlanNowAsync(store, rig, gen, ct);
        var forget = plan.Actions.Single(action => action.Rule == 27 && action.Kind == ActionKind.Forget);
        var successors = plan.Actions.Where(action => action.Rule == 27 && action.Kind == ActionKind.KeepBoth).ToArray();
        Require(successors.Length == 2 && successors.Select(action => action.Target).Distinct().Count() == 2 &&
            successors.All(action => action.ConflictTransitionKey == forget.ObjectKey &&
                action.ObjectKey == PlanIds.Split(SharedKey, action.Target == Side.A ? Side.B : Side.A)),
            "rule 27 freezes both create-only successors under the forgotten correspondence");
        return (plan, forget, successors);
    }

    private static async Task<Plan> PlanNowAsync(IStateStore store, Rig rig, long gen, CancellationToken ct)
    {
        var snapshotA = await rig.A.FullAsync(gen, _ => true, new NullProgress(), ct);
        var snapshotB = await rig.B.FullAsync(gen, _ => true, new NullProgress(), ct);
        var deleted = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        var complete = new Completeness(true, true, new HashSet<string>(), new HashSet<string>());
        var rows = Planner.Join(store.Baselines(PairId), snapshotA.Items, snapshotB.Items, deleted, deleted, complete,
            firstSync: false);
        var labels = new LabelRule(Settings.KeepBothLabelFormat, Settings.KeepBothTimeFormat,
            TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow),
            SyncProviders.DisplayName(rig.Pair.A.Provider), SyncProviders.DisplayName(rig.Pair.B.Provider));
        var options = new PlanOptions(Settings.TieWindow, false, labels, Settings.MaxLabelAttempts)
        {
            ObservedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            ProviderA = rig.Pair.A.Provider,
            ProviderB = rig.Pair.B.Provider,
        };
        return Planner.Decide(PairId, gen, rows, options);
    }

    private static SyncRun NewRun(IStateStore store, Rig rig) =>
        new(store, new Executor(store, Settings, Admission()), rig.Provider, Settings, TimeProvider.System);

    private static async Task<List<ActionResult>> ApplyAsync(Executor executor, Plan plan, IEndpointWriter a,
        IEndpointWriter b)
    {
        var results = new List<ActionResult>();
        await foreach (var result in executor.ApplyAsync(plan, a, b, CancellationToken.None)) results.Add(result);
        return results;
    }

    private static Exception? FailConflictCopy(SyncAction action) =>
        action.Kind == ActionKind.KeepBoth ? new IOException("simulated interruption before the conflict copy") : null;

    private static SyncTransferResourceController Admission() => ExecutorChecks.Admission();

    private static async Task AssertResolvedAsync(IStateStore store, Rig rig, SyncAction forget, SyncAction[] successors)
    {
        Require(store.UnresolvedIntents(PairId).Count == 0, "every resumed successor is durably resolved");
        Require(store.Baselines(PairId, [forget.ObjectKey!]).Count == 0,
            "the replaced correspondence stays forgotten after the restart");
        Require(store.Baselines(PairId, successors.Select(action => action.ObjectKey!)).Count == 2,
            "each completed conflict copy owns its own split baseline");

        var mapA = await MapAsync(rig.A);
        var mapB = await MapAsync(rig.B);
        var copyOnA = successors.Single(action => action.Target == Side.A);
        var copyOnB = successors.Single(action => action.Target == Side.B);
        Require(mapA.Count == 2 && mapB.Count == 2, "each side holds exactly its original and the other version's copy");
        Require(mapA.TryGetValue(OriginalPath, out var aHash) && aHash == Hash("edit-a") &&
            mapB.TryGetValue(OriginalPath, out var bHash) && bHash == Hash("edit-b"),
            "both originals keep their own content, with no newest-wins replacement");
        Require(mapA.TryGetValue(copyOnA.Path, out var copyAHash) && copyAHash == Hash("edit-b") &&
            mapB.TryGetValue(copyOnB.Path, out var copyBHash) && copyBHash == Hash("edit-a"),
            "each copy has the opposite side's exact content exactly once");
    }

    private static string Hash(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();

    private static async Task<Dictionary<string, string>> MapAsync(FakeEndpoint endpoint)
    {
        var snapshot = await endpoint.FullAsync(0, _ => true, new NullProgress(), CancellationToken.None);
        return snapshot.Items.ToDictionary(item => item.Path, item => item.Hash ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool SameMap(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value);

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
