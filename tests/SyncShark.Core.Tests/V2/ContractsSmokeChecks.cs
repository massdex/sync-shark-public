using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>Tiny smoke checks for the v2 shared contracts and test fakes.</summary>
internal static class ContractsSmokeChecks
{
    public static async Task RunAsync()
    {
        StoreRoundTrip();
        StorePairRecords();
        StoreTargetedLookups();
        StoreCommitSemantics();
        StoreRecoveriesAndNotices();
        SettingsDefaults();
        await ConditionalPutMismatchThrowsAsync();
        await UncertainWriteLeavesItemAppliedAsync();
        FakeObjectIdsIndependentOfContent();
        await OpenReadChecksPlannedVersionAsync();
        PairAdmissionChecks.Run();
        Console.WriteLine("v2 contract smoke checks passed.");
    }

    private static void StoreRoundTrip()
    {
        using var store = new InMemoryStateStore();
        var first = Item("a.txt", "obj-a", "h-a");
        var second = Item("b.txt", "obj-b", "h-b");
        var snapshotGen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, snapshotGen, "1", new[] { first, second }, Array.Empty<string>()));
        store.CompleteGeneration(snapshotGen, "1", true);
        Require(store.Cursor("root") == "1", "snapshot cursor round-trips");
        Require(store.Live("root").Count == 2, "snapshot writes every row");
        Require(store.ByObject("root", "obj-a")?.Hash == "h-a", "object lookup finds the row");

        var edited = first with { Hash = "h-a2", VersionId = "v2" };
        var deltaGen = store.BeginGeneration("root", "delta");
        store.WriteDelta(new Delta("root", Side.A, deltaGen, "1", "2", new[]
        {
            new Change(ChangeKind.Upsert, "obj-a", "a.txt", edited, null),
            new Change(ChangeKind.Delete, "obj-b", "b.txt", null, DateTimeOffset.UtcNow),
        }, false, Array.Empty<string>()));
        store.CompleteGeneration(deltaGen, "2", true);
        Require(store.Cursor("root") == "2", "delta cursor round-trips");
        var live = store.Live("root");
        Require(live.Count == 1 && live[0].Hash == "h-a2", "delta updates and deletes only changed rows");
        Require(store.ByObject("root", "obj-b") is null, "deleted object is no longer live");
    }

    private static void StorePairRecords()
    {
        using var store = new InMemoryStateStore();
        var pair = Pair("p1");
        store.SavePair(pair);
        Require(store.Pair("p1") == pair && store.Pairs().Count == 1, "pair records round-trip");
        Require(!store.FirstSyncDone("p1"), "first sync is not done on a fresh pair");
        Require(store.Pair("missing") is null, "an unknown pair is null");
        ExpectThrows<InvalidOperationException>(() => store.MarkFirstSyncDone("missing"));
        store.MarkFirstSyncDone("p1");
        Require(store.FirstSyncDone("p1"), "the first-sync-done marker round-trips");
        store.SavePair(pair with { Enabled = false });
        Require(store.FirstSyncDone("p1"), "re-saving a pair keeps its first-sync-done marker");
    }

    private static void StoreTargetedLookups()
    {
        using var store = new InMemoryStateStore();
        var gen = store.BeginGeneration("root", "snapshot");
        store.WriteSnapshot(new Snapshot("root", Side.A, gen, "1", new[]
        {
            Item("a.txt", "id:a", "ha"),
            Item("d/x.txt", "id:x", "hx"),
            Item("d/e/y.txt", "id:y", "hy"),
            Item("dd/z.txt", "id:z", "hz"),
        }, Array.Empty<string>()));
        var under = store.LiveUnder("root", new[] { "d" });
        Require(under.Count == 2 && under.Any(s => s.Path == "d/x.txt") && under.Any(s => s.Path == "d/e/y.txt"),
            "LiveUnder returns a folder and its descendants, not its siblings");
        Require(store.LiveUnder("root", new[] { "" }).Count == 4, "LiveUnder with the empty prefix returns all rows");
        Require(store.LiveUnder("root", new[] { "D" }).Count == 2, "LiveUnder ignores case (owner decision 34)");

        store.SavePair(Pair("p1"));
        SeedBaseline(store, "p1", "k1", Item("a.txt", "id:a", "ha"), Item("b.txt", "id:b", "hb"));
        SeedBaseline(store, "p1", "k2", Item("d/x.txt", "id:x", "hx"), Item("d/y.txt", "id:y", "hy"));
        Require(store.BaselinesTouching("p1", Side.A, new[] { "id:a" }, Array.Empty<string>()).Count == 1,
            "BaselinesTouching finds a baseline by object id");
        Require(store.BaselinesTouching("p1", Side.A, new[] { "id:b" }, Array.Empty<string>()).Count == 0,
            "BaselinesTouching by object id matches only its own side");
        var byPrefix = store.BaselinesTouching("p1", Side.B, Array.Empty<string>(), new[] { "d" });
        Require(byPrefix.Count == 1 && byPrefix[0].ObjectKey == "k2",
            "BaselinesTouching finds a baseline by its side path prefix");
        Require(store.BaselinesTouching("p1", Side.A, new[] { "id:zzz" }, new[] { "nope" }).Count == 0,
            "BaselinesTouching without a match returns empty");
    }

    private static void StoreCommitSemantics()
    {
        using var store = new InMemoryStateStore();
        store.SavePair(Pair("p1"));
        var a = Item("x.txt", "id:a", "ha");
        var b = Item("x.txt", "id:b", "ha");
        SeedBaseline(store, "p1", "k1", a, b);

        var update = new SyncAction("a-update", 2, ActionKind.Update, Side.B, "x.txt", null, a, b, false, "k1");
        store.RecordIntent("p1", update);
        store.CommitResult("p1", 1, update, new ActionResult("a-update", Outcome.Failed, null, null, null, "boom"));
        Require(store.UnresolvedIntents("p1").Any(i => i.ActionId == "a-update"), "a failed intent stays unresolved");
        store.CommitResult("p1", 1, update, new ActionResult("a-update", Outcome.Done, a, b, null, null));
        Require(store.UnresolvedIntents("p1").Count == 0, "a later done outcome closes the intent");
        Require(store.Baselines("p1").Count == 1, "a done Update writes the baseline");

        var skipped = new SyncAction("a-skip", 2, ActionKind.Update, Side.B, "x.txt", null, a, b, false, "k1");
        store.RecordIntent("p1", skipped);
        store.CommitResult("p1", 1, skipped, new ActionResult("a-skip", Outcome.Skipped, null, null, null, "mismatch"));
        Require(store.UnresolvedIntents("p1").Count == 0, "a skipped intent is closed");

        var delete = new SyncAction("a-del", 6, ActionKind.Delete, Side.B, "x.txt", null, null, b, true, "k1");
        store.CommitResult("p1", 1, delete, new ActionResult("a-del", Outcome.Done, null, null, null, null));
        Require(store.Baselines("p1").Count == 0, "a done Delete removes its baseline");

        SeedBaseline(store, "p1", "k2", a, b);
        var forget = new SyncAction("a-forget", 15, ActionKind.Forget, Side.B, "", null, null, null, false, "k2");
        store.CommitResult("p1", 1, forget, new ActionResult("a-forget", Outcome.Done, null, null, null, null));
        Require(store.Baselines("p1").Count == 0, "a done Forget removes its baseline");
    }

    private static void StoreRecoveriesAndNotices()
    {
        using var store = new InMemoryStateStore();
        store.SavePair(Pair("p1"));
        var victim = Item("x.txt", "id:a", "ha");
        var action = new SyncAction("a1", 6, ActionKind.Delete, Side.B, "x.txt", null, null, victim, true, "k1");
        store.RecordIntent("p1", action);
        var recovery = new RecoveryRef(Side.B, "F:\\recovery\\op1", victim.VersionId, victim.Hash!, victim.Size);
        store.CommitResult("p1", 1, action, new ActionResult("a1", Outcome.Done, null, null, recovery, null));
        var records = store.Recoveries("p1");
        Require(records.Count == 1 && records[0].ActionId == "a1" && records[0].Location == "F:\\recovery\\op1",
            "Recoveries returns the recorded copy");
        Require(store.Recoveries("other").Count == 0, "Recoveries is empty for an unknown pair");

        store.AddNotices(new[] { new Notice("p1", 4, NoticeKind.AutoConflict, "x.txt", "conflict", "conflict") });
        var open = store.OpenNotices("p1");
        Require(open.Count == 1 && open[0].NoticeId is not null, "notices are stored with an id");
        store.CloseNotice(open[0].NoticeId!, "keep-both");
        Require(store.OpenNotices("p1").Count == 0, "CloseNotice removes the notice from the open list");
        store.AddNotices(new[] { new Notice("p1", 4, NoticeKind.AutoConflict, "X.txt", "conflict again", "conflict") });
        store.AddNotices(new[] { new Notice("p1", 4, NoticeKind.AutoConflict, "x.txt", "conflict", "conflict") });
        var reopened = store.OpenNotices("p1");
        Require(reopened.Count == 1 && reopened[0].NoticeId == open[0].NoticeId,
            "raising a closed problem again reopens its notice, and raising it while open adds nothing (decision 26)");
    }

    private static void SettingsDefaults()
    {
        var s = V2Settings.Default;
        Require(s.StoreBatchRows == 20_000 && s.StoreCacheMb == 64 && s.MaxWorkers == 64 && s.LocalDeltaMaxPaths == 10_000,
            "settings carry the numeric defaults");
        Require(s.TieWindow == TimeSpan.FromSeconds(2) && s.StoreBusyTimeout == TimeSpan.FromSeconds(5) &&
            s.LocalDiscoveryTimeout == TimeSpan.FromSeconds(10) && s.AdmissionRetryDelay == TimeSpan.FromMilliseconds(20) &&
            s.ProgressInterval == TimeSpan.FromMilliseconds(250), "settings carry the time defaults");
        Require(s.HistoryRetention == TimeSpan.FromDays(30) && s.RecoveryRetention == TimeSpan.FromDays(30),
            "settings carry the retention defaults");
        Require(s.KeepBothLabelFormat == "{name} [{side} version {time}]{ext}" && s.KeepBothTimeFormat == "d MMM HH.mm",
            "settings carry the keep-both label defaults");
    }

    private static async Task ConditionalPutMismatchThrowsAsync()
    {
        var endpoint = new FakeEndpoint(Side.A, "root");
        endpoint.Seed("a.txt", "hello");
        var stale = new ItemState("a.txt", ItemKind.File, "id:stale", "v-stale", "deadbeef", 1,
            DateTimeOffset.UnixEpoch, ItemFlags.None);
        var action = new SyncAction("act-put", 2, ActionKind.Update, Side.A, "a.txt", null, null, stale, false, "a.txt");
        await ExpectAsync<ExpectedTargetMismatchException>(() =>
            endpoint.PutAsync(action, new MemoryStream(new byte[] { 1 }), CancellationToken.None));
    }

    private static async Task UncertainWriteLeavesItemAppliedAsync()
    {
        var endpoint = new FakeEndpoint(Side.A, "root");
        endpoint.Seed("a.txt", "old");
        var current = await endpoint.ObserveAsync("a.txt", CancellationToken.None);
        var payload = Encoding.UTF8.GetBytes("changed-content");
        endpoint.NextWriteUncertain = true;
        var action = new SyncAction("act-uncertain", 2, ActionKind.Update, Side.A, "a.txt", null, current, current, false, "a.txt");
        await ExpectAsync<UncertainEffectException>(() =>
            endpoint.PutAsync(action, new MemoryStream(payload), CancellationToken.None));
        var after = await endpoint.ObserveAsync("a.txt", CancellationToken.None);
        Require(after is not null && after.Size == payload.Length, "uncertain write still applied its bytes");
    }

    private static void FakeObjectIdsIndependentOfContent()
    {
        var endpoint = new FakeEndpoint(Side.A, "root");
        var first = endpoint.Seed("a.txt", "same-bytes");
        var second = endpoint.Seed("b.txt", "same-bytes");
        Require(first.ObjectId != second.ObjectId, "object ids are independent of content");
    }

    private static async Task OpenReadChecksPlannedVersionAsync()
    {
        var endpoint = new FakeEndpoint(Side.A, "root");
        var planned = endpoint.Seed("a.txt", "hello");
        await using (var stream = await endpoint.OpenReadAsync(planned, CancellationToken.None))
            Require(stream.Length == 5, "OpenRead returns the planned version's bytes");
        await ExpectAsync<ExpectedTargetMismatchException>(() =>
            endpoint.OpenReadAsync(planned with { Hash = "different" }, CancellationToken.None));
    }

    private static void SeedBaseline(InMemoryStateStore store, string pairId, string key, ItemState a, ItemState b)
    {
        var action = new SyncAction("seed-" + key, 3, ActionKind.Adopt, Side.B, b.Path, null, a, b, false, key);
        store.RecordIntent(pairId, action);
        store.CommitResult(pairId, 1, action, new ActionResult(action.ActionId, Outcome.Done, a, b, null, null));
    }

    private static PairDef Pair(string id) => new(id, id,
        new RootDef("ra", "Local", null, "C:\\a", "id:local:1"),
        new RootDef("rb", "Dropbox", "acc", "/b", "id:dbx:1"),
        Array.Empty<string>(), Array.Empty<string>(), true);

    private static ItemState Item(string path, string objectId, string hash) =>
        new(path, ItemKind.File, objectId, "v1", hash, 1, DateTimeOffset.UnixEpoch, ItemFlags.None);

    private static async Task ExpectAsync<TException>(Func<Task> operation) where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException)
        {
            Console.WriteLine("PASS rejects via " + typeof(TException).Name);
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    private static void ExpectThrows<TException>(Action operation) where TException : Exception
    {
        try
        {
            operation();
        }
        catch (TException)
        {
            Console.WriteLine("PASS rejects via " + typeof(TException).Name);
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
