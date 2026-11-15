namespace Massdex.SyncShark.V2;

/// <summary>rule 35 an online-only stub is replaced by a download, never deleted.</summary>
internal static partial class PlannerChecks
{
    private const ItemFlags Stub = ItemFlags.Placeholder;

    private static void RunStubChecks()
    {
        OwnedPointerChecks();
        Rule35_StubReplacedRegardlessOfNewestWins();
        Rule35b_StubWithoutCounterpartOnlyNotice();
        Rule35c_StubNeverDeletedByChoiceOrEvidence();
        Rule35d_StubWithBaselineDownloads();
        Rule35e_StubSourceHashRequested();
        Rule35f_StubOppositeUnreadableOrFolder();
        AssertStubLeftAlone(F("off.txt", "local", null, 5, null, Stub));
    }

    internal static void AssertStubLeftAlone(ItemState item)
    {
        Require(item.Flags == ItemFlags.Placeholder, "replacement off candidate remains a placeholder");
        using var store = new InMemoryStateStore();
        var pair = new PairDef(PairId, PairId,
            new RootDef("stub-a", SyncProviders.Local, null, "C:\\a", "id:a"),
            new RootDef("stub-b", SyncProviders.Dropbox, "account", "/b", "id:b"), [], [], true);
        store.SavePair(pair);
        var gen = store.BeginGeneration(pair.A.RootId, "full");
        store.WriteSnapshot(new Snapshot(pair.A.RootId, Side.A, gen, "0", [item], []));
        store.CompleteGeneration(gen, "0", true);
        var exclusions = LinkExclusions.Of(store, pair, stubsLeftAlone: true);
        Require(exclusions.Covers(item.Path), "replacement off candidate is excluded on both sides");
        var include = exclusions.Narrow(_ => true);
        var rows = new[] { Row(null, item, F(item.Path, "remote", "hash", item.Size)) }
            .Where(row => include(row.A!.Path)).ToArray();
        var plan = Planner.Decide(PairId, Gen, rows, Opt(true));
        AssertNoActions(plan);
        AssertEmptyNotices(plan);
        var summaries = exclusions.Summaries(PairId);
        Require(summaries.Count == 1 && summaries[0].Path.Length == 0 &&
            summaries[0].ReasonKey == LinkExclusions.StubReason.Key,
            "replacement off retains only the pair-level left-alone summary, no per-file notice");
    }

    internal static void AssertUnreadableStub(ItemState item)
    {
        var plan = Decide(true, Row(null, item, F(item.Path, "remote", "hash", item.Size)));
        AssertNoActions(plan);
        Require(plan.Notices.Any(n => n.ReasonKey == PlanNotices.CannotRead.Key),
            "unproven/partial/modified placeholder produces an unreadable notice");
        var removed = Planner.Decide(PairId, Gen, new[] { Row(null, item, null, Ev(B: true)) },
            Opt(true, oneSided: new[] { new OneSidedChoice("", Side.A, true) }));
        AssertNoActions(removed);
    }

    private static void Rule35_StubReplacedRegardlessOfNewestWins()
    {
        // The stub is "newer" than the cloud copy; it still has no content to keep, so it is downloaded over.
        var stub = F("a.txt", "lA", null, 10, T0.AddDays(5), Stub);
        var cloud = F("a.txt", "rB", "hB", 10, T0);
        foreach (var firstSync in new[] { true, false })
        {
            var plan = Decide(firstSync, Row(null, stub, cloud));
            AssertActions(plan, new Exp(ActionKind.Update, Side.A, "a.txt", null, false, 35, "a.txt", NK(Side.B, "rB")));
            AssertEmptyNotices(plan);
            Require(plan.Actions[0].ExpectedTarget == stub && plan.Actions[0].Source == cloud,
                "rule 35 expects the stub and copies the cloud item");
            var summary = Planner.StubSummary(plan);
            Require(summary == (1, 10, 0), "stub summary counts one replacement of 10 bytes");
        }
        var mirrored = Decide(true, Row(null, F("b.txt", "lA", "hA", 3), F("b.txt", "rB", null, 3, null, Stub)));
        AssertActions(mirrored, new Exp(ActionKind.Update, Side.B, "b.txt", null, false, 35, "b.txt", NK(Side.A, "lA")));
        Console.WriteLine("PASS rule 35: stub + file -> download over the stub, no Recovery, newest-wins ignored");
    }

    private static void Rule35b_StubWithoutCounterpartOnlyNotice()
    {
        var plan = Decide(true, Row(null, F("lonely.txt", "lA", null, 5, null, Stub), null));
        AssertNoActions(plan);
        AssertNotices(plan, new NExp(35, NoticeKind.Info, "lonely.txt"));
        Require(plan.Notices[0].ReasonKey == PlanNotices.StubWithoutCounterpart.Key, "stub-without-counterpart reason");
        Require(Planner.StubSummary(plan) == (0, 0, 1), "stub summary counts one stub without a counterpart");
        Console.WriteLine("PASS rule 35b: stub with no counterpart -> notice only");
    }

    private static void Rule35c_StubNeverDeletedByChoiceOrEvidence()
    {
        var stub = F("dir/s.txt", "lA", null, 5, null, Stub);
        var remove = new[] { new OneSidedChoice("", Side.A, true) };
        var chosen = Planner.Decide(PairId, Gen, new[] { Row(null, stub, null, Ev(B: true, atB: T0.AddDays(9))) },
            Opt(true, oneSided: remove));
        AssertNoActions(chosen);
        AssertNotices(chosen, new NExp(35, NoticeKind.Info, "dir/s.txt"));
        var winner = Planner.Decide(PairId, Gen, new[] { Row(null, stub, F("dir/s.txt", "rB", "hB", 5)) },
            Opt(true, winners: new Dictionary<string, Side?> { ["dir/s.txt"] = Side.A }));
        AssertActions(winner, new Exp(ActionKind.Update, Side.A, "dir/s.txt", null, false, 35, "dir/s.txt", NK(Side.B, "rB")));
        var baseA = F("dir/s.txt", "lA", "hA", 5);
        var baseB = F("dir/s.txt", "rB", "hA", 5);
        var deletedOther = Decide(false, Row(Base("k1", baseA, baseB), stub, null, Ev(B: true)));
        AssertNoActions(deletedOther);
        AssertNotices(deletedOther, new NExp(35, NoticeKind.Info, "dir/s.txt"));
        Console.WriteLine("PASS rule 35c: a remove choice, deletion evidence or a winner never deletes or uploads a stub");
    }

    private static void Rule35d_StubWithBaselineDownloads()
    {
        var baseA = F("doc.txt", "lA", "h1", 4);
        var baseB = F("doc.txt", "rB", "h1", 4);
        var plan = Decide(false, Row(Base("k2", baseA, baseB), F("doc.txt", "lA", null, 4, T0.AddDays(1), Stub),
            F("doc.txt", "rB", "h1", 4)));
        AssertActions(plan, new Exp(ActionKind.Update, Side.A, "doc.txt", null, false, 35, "doc.txt", "k2"));
        Console.WriteLine("PASS rule 35d: a dehydrated synced file is downloaded again under its baseline key");
    }

    private static void Rule35e_StubSourceHashRequested()
    {
        var row = Row(null, F("x.bin", "lA", null, 7, null, Stub), F("x.bin", "lB", null, 7));
        var request = Planner.HashRequests(new[] { row });
        Require(request.PathsA.Count == 0 && request.PathsB.SequenceEqual(new[] { "x.bin" }),
            "the stub is never hashed; its unhashed download source is");
        var plan = Decide(true, row);
        AssertNoActions(plan);
        AssertNotices(plan, new NExp(30, NoticeKind.Info, "x.bin"));
        Console.WriteLine("PASS rule 35e: an unhashed source is hashed first, the stub never");
    }

    private static void Rule35f_StubOppositeUnreadableOrFolder()
    {
        var stub = F("y", "lA", null, 1, null, Stub);
        AssertNotices(Decide(true, Row(null, stub, F("y", "rB", "h", 1, null, ItemFlags.Unreadable))),
            new NExp(30, NoticeKind.Info, "y"));
        AssertNotices(Decide(true, Row(null, stub, D("y", "rB"))), new NExp(30, NoticeKind.Info, "y"));
        AssertNoActions(Decide(true, Row(null, stub, F("y", "rB", "h", 1, null, Stub))));
        Console.WriteLine("PASS rule 35f: stub opposite unreadable, folder or stub -> notice, no action");
    }
}
