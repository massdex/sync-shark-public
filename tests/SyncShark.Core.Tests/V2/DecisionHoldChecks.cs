namespace Massdex.SyncShark.V2;

/// <summary>
/// a first sync completes when every reviewed executable effect is done; a user-decision Hold
/// (a tie) does not block it. The tie path then stays untouched on both sides while its notice is open, and the
/// user's decision is applied when it is made.
/// </summary>
internal static class DecisionHoldChecks
{
    private static V2Settings Settings => V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };

    public static async Task RunAsync()
    {
        var store = new InMemoryStateStore();
        var pair = new PairDef("ptie", "ptie", new RootDef("ra", SyncProviders.Local, null, "C:\\a", "id:ra"),
            new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:rb"), [], [], true);
        store.SavePair(pair);
        var a = new FakeEndpoint(Side.A, pair.A.RootId);
        var b = new FakeEndpoint(Side.B, pair.B.RootId);
        var t0 = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        a.Seed("t.txt", "local v1", mtime: t0);
        b.Seed("t.txt", "cloud v1", mtime: t0.AddSeconds(1));   // within the 2 s tie window -> rule 5 Hold
        a.Seed("c.txt", "copy me");
        var provider = new FakeEndpointProvider().Add(pair.A, a).Add(pair.B, b)
            .AddWriter(pair.A, new CountingWriter(a)).AddWriter(pair.B, new CountingWriter(b));
        var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage), provider, Settings,
            TimeProvider.System);

        var plan = await run.PreviewFirstSyncAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(plan.Actions.Any(x => x.Kind == ActionKind.Hold && x.Path == "t.txt") &&
                plan.Actions.Any(x => x.Kind == ActionKind.CopyNew && x.Path == "c.txt"),
            "setup: the review holds the tie and copies the other file");
        var applied = await run.ApplyAsync(pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
        Require(store.FirstSyncDone(pair.PairId) && applied.Deferred == 0,
            "a user-decision Hold does not block first-sync completion");
        Require(await b.ObserveAsync("c.txt", CancellationToken.None) is not null, "the executable effect was applied");
        var tie = store.OpenNotices(pair.PairId).Single(n => n.Kind == NoticeKind.UserDecision && n.Path == "t.txt");

        // Both sides change the tie path; ordinary sync leaves it alone (no keep-both, no copy, no delete).
        a.Seed("t.txt", "local v2", mtime: t0.AddMinutes(5));
        b.Seed("t.txt", "cloud v2", mtime: t0.AddMinutes(6));
        var countB = (await b.FullAsync(0, _ => true, new Progress<string>(), CancellationToken.None)).Items.Count;
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        var localNow = await a.ObserveAsync("t.txt", CancellationToken.None);
        var cloudNow = await b.ObserveAsync("t.txt", CancellationToken.None);
        Require(localNow is not null && cloudNow is not null && localNow.Hash != cloudNow.Hash &&
                (await b.FullAsync(0, _ => true, new Progress<string>(), CancellationToken.None)).Items.Count == countB,
            "the pending tie path is untouched on both sides by the next incremental run");
        Require(store.OpenNotices(pair.PairId).Any(n => n.NoticeId == tie.NoticeId), "its decision notice stays open");

        // The user decides: the local version wins. The token pins the notice and both sides' fresh state.
        var snapshot = (await run.ObserveDecisionsAsync(pair.PairId, [(tie.NoticeId!, tie.Path)],
            CancellationToken.None)).Single();
        await run.ResolveDecisionAsync(pair.PairId, tie.Path, snapshot.Token, Side.A, new RecordingProgress(),
            CancellationToken.None);
        var decided = await b.ObserveAsync("t.txt", CancellationToken.None);
        Require(decided is not null && decided.Hash == (await a.ObserveAsync("t.txt", CancellationToken.None))!.Hash,
            "resolving the tie applies the chosen side");
        Require(store.OpenNotices(pair.PairId).All(n => n.NoticeId != tie.NoticeId), "the decided notice closes");
        Console.WriteLine("PASS decision 64: tie does not block first sync, stays pending, decision applied");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
