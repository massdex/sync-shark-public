using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Storage;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Checks for IssueTable (`--engine-checks store`).</summary>
public static class IssueTableChecks
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public static void Run()
    {
        using var connection = Open();
        RaiseCases(connection);
        CloseCases(connection);
        ReadCases(connection);
        HeldKeyCases(connection);
        HeldOwnerCases(connection);
        RetryCases(connection);
        ProofCases(connection);
        SkippedCases(connection);
        SkippedEvaluationOrderCases(connection);
        DismissCases(connection);
        NarrowReadCases(connection);
        FirstRunPreservationCases(connection);
        PageWaitCases();
        LiveSkippedCases();
        KeepFolderAlwaysCases();
        NoticeFailureAndConfigurationCases();
        PageSnapshotAndSearchCases();
        ScopedIssueCases();
        CloseScopeCases();
    }

    public static void ReviewedRootIdentity() => WithRealStore(store =>
    {
        var generation = store.ReadPairExecution(ContractPair)!.Value.Generation;
        store.ReconcileRootIdentity(ContractPair, generation, null, null, StateChange.Empty with
        {
            Raise = [(IssueKind.RootIdentity, TextKey.RootUnbound, string.Empty, new IssueSubject([], null))]
        });
        var expected = Massdex.SyncShark.Engine.Run.PairSafety.ForReview(store.Issues(ContractPair).Single());
        Exception? failure = null;
        try
        {
            store.ReconcileRootIdentity(ContractPair, generation, null, expected, StateChange.Empty with
            {
                Close = [(string.Empty, new[] { IssueKind.RootIdentity })]
            });
        }
        catch (Exception error) { failure = error; }
        Check.Require(failure is null && store.IssueById(ContractPair, expected.Id) is null,
            "a current projected root notice reconciles through the shared exact issue identity");
    });

    public static void ReviewedPairDecision()
    {
        WithRealStore(CheckDecision);
        using var memory = new TestDoubles.InMemoryStateStore();
        memory.SavePair(new PairConfig(ContractPair, ContractPair, ContractRoot, ContractRemote,
            ContractConnection, [], [], FirstRunPolicy.NewestWins, true, null));
        CheckDecision(memory);

        static void CheckDecision(IStateStore store)
        {
            var change = StateChange.Empty with
            {
                Raise = [(IssueKind.DriveChange, TextKey.DriveRestored, string.Empty, new IssueSubject([], null))]
            };
            store.Commit(ContractPair, change);
            var expected = store.Issues(ContractPair).Single();
            store.Commit(ContractPair, change with
            {
                Raise = [(expected.Kind, expected.Cause, expected.Path,
                    new IssueSubject([], null, new LocalHashProof(1, 0, "replacement")))]
            });
            var replacement = store.IssueById(ContractPair, expected.Id)!;
            var execution = store.ReadPairExecution(ContractPair)!.Value;
            var local = store.ReadSource(ContractPair, Side.Local);
            var remote = store.ReadSource(ContractPair, Side.Dropbox);
            foreach (var reset in new[] { false, true })
            {
                Exception? refusal = null;
                try
                {
                    store.SavePairDecision(execution.Pair, execution.Generation,
                        execution.Pair with { FirstRun = FirstRunPolicy.LocalWins }, expected, reset, null);
                }
                catch (Exception error) { refusal = error; }
                Check.Require(refusal is EngineRequestException { Key: TextKey.ResolveStale } &&
                    refusal.Message == Text.Get(TextKey.ResolveStale,
                        (TextArg.Id, expected.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    "stale pair decision reports ResolveStale for reset=" + reset);
                Check.Require(store.IssueById(ContractPair, expected.Id)!.Matches(replacement) &&
                    store.ReadPairExecution(ContractPair)!.Value.Generation == execution.Generation &&
                    System.Text.Json.JsonSerializer.Serialize(store.Pair(ContractPair)) ==
                        System.Text.Json.JsonSerializer.Serialize(execution.Pair) &&
                    store.ReadSource(ContractPair, Side.Local) == local &&
                    store.ReadSource(ContractPair, Side.Dropbox) == remote,
                    "stale pair decision preserves the replacement, configuration, generation and sources");
            }
        }
    }

    public static void ReviewedRetryReads()
    {
        WithRealStore(CheckReads);
        using var memory = new TestDoubles.InMemoryStateStore();
        memory.SavePair(new PairConfig(ContractPair, ContractPair, ContractRoot, ContractRemote,
            ContractConnection, [], [], FirstRunPolicy.NewestWins, true, null));
        CheckReads(memory);

        static void CheckReads(IStateStore store)
        {
            var change = StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.RequiredRepairRetry, string.Empty, new IssueSubject([], null))]
            };
            store.Commit(ContractPair, change);
            var expected = store.Issues(ContractPair).Single();
            store.ReadPrerequisiteRetry(ContractPair, expected);
            store.ReadItemRetryState(ContractPair, [expected.Id], expected);
            store.Commit(ContractPair, change with
            {
                Raise = [(IssueKind.Failure, TextKey.RequiredRepairRetry, string.Empty,
                    new IssueSubject([], null, new LocalHashProof(1, 0, "replacement")))]
            });
            var replacement = store.IssueById(ContractPair, expected.Id)!;
            Check.Require(!replacement.Matches(expected), "guarded retry-read fixture replaces the same issue id");
            foreach (Action read in new Action[]
            {
                () => store.ReadPrerequisiteRetry(ContractPair, expected),
                () => store.ReadItemRetryState(ContractPair, [expected.Id], expected)
            })
            {
                var stale = false;
                try { read(); }
                catch (EngineRequestException error) when (error.Key == TextKey.ResolveStale) { stale = true; }
                Check.Require(stale && store.IssueById(ContractPair, expected.Id)!.Matches(replacement),
                    "both retry state reads reject a stale expected issue without changing its replacement");
            }
        }
    }

    // B-122 B1-B14 (DESIGN-B122-2 §5 B, -4 §4, -5 §2.2): a result's CloseScope spares an incoming clash it does not
    // resolve and closes everything else, through Commit on the real store and the in-memory double alike (B11).
    private static void CloseScopeCases()
    {
        const string at = "Scope/B";
        var incoming = new IssueItem(Side.Local, "Scope/P", "x");
        var occupant = new IssueItem(Side.Dropbox, at, "o");
        static SyncedEntry Bind(string localPath, string localId, string dropboxPath, string dropboxId) =>
            new(PathNames.KeyFor(Side.Local, localPath), localPath, PathNames.KeyFor(Side.Dropbox, dropboxPath),
                dropboxPath, false, localId, 1, 0, dropboxId, "hash");
        static IssueSubject Clash(params IssueItem[] items) => new(items, Side.Local);
        var cases = new (string Id, IssueKind Kind, IssueSubject Subject, string ClosePath,
            (SyncedEntry? Binding, bool Absent)? Scope, bool Survives)[]
        {
            ("B1 unchanged occupant", IssueKind.NameClash, Clash(incoming, occupant), at, (Bind(at, "o1", at, "o"), false), true),
            ("B2 replacement occupant", IssueKind.NameClash, Clash(incoming, occupant), at, (Bind(at, "o1", at, "o2"), false), true),
            ("B3 other spelling", IssueKind.NameClash, Clash(incoming, occupant), "Scope/b", (Bind(at, "o1", at, "o"), false), true),
            ("B3 other spelling, real absence", IssueKind.NameClash, Clash(incoming, occupant), "Scope/b", (null, true), false),
            ("B3 other spelling, resolving binding", IssueKind.NameClash, Clash(incoming, occupant), "Scope/b",
                (Bind("Scope/C", "x", "Scope/C", "x2"), false), false),
            ("B4 same-side occupant", IssueKind.NameClash, Clash(incoming, new IssueItem(Side.Local, at, "o")), at,
                (Bind(at, "o", at, "o2"), false), true),
            ("B5 moved identity", IssueKind.NameClash, Clash(incoming, occupant), at, (Bind("Scope/C", "x", "Scope/C", "x2"), false), false),
            ("B6 arrival", IssueKind.NameClash, Clash(incoming, occupant), at, (Bind(at, "x", at, "x2"), false), false),
            ("B7 real absence", IssueKind.NameClash, Clash(incoming, occupant), at, (null, true), false),
            ("B8 unscoped", IssueKind.NameClash, Clash(incoming, occupant), at, null, false),
            ("B9 no incoming side", IssueKind.NameClash, new IssueSubject([incoming, occupant], null), at,
                (Bind(at, "z", at, "z2"), false), false),
            ("B10 disjoint Failure", IssueKind.Failure, new IssueSubject([incoming], null), at, (Bind(at, "y", at, "y2"), false), false),
            ("B10 stale Failure, other identity at its path", IssueKind.Failure,
                new IssueSubject([new IssueItem(Side.Local, at, "x")], null), at, (Bind(at, "y", at, "y2"), false), false),
            ("B12 observed new, bound old", IssueKind.NameClash, Clash(new IssueItem(Side.Local, at, "new"), occupant), at,
                (Bind(at, "old", at, "o"), false), true),
            ("B13 incoming paired with the occupant", IssueKind.NameClash, Clash(new IssueItem(Side.Local, at, "new"), occupant),
                at, (Bind(at, "new", at, "o"), false), true),
            ("B14 incoming paired after the rename", IssueKind.NameClash,
                Clash(new IssueItem(Side.Local, at, "new"), occupant, new IssueItem(Side.Dropbox, "Scope/Stage", "renamed")),
                at, (Bind(at, "new", at, "renamed"), false), false),
        };
        foreach (var (id, kind, subject, closePath, scope, survives) in cases)
        {
            void CheckStore(IStateStore store)
            {
                store.Commit(ContractPair, StateChange.Empty with
                {
                    Raise = [(kind, kind == IssueKind.NameClash ? TextKey.ClashNameTaken : TextKey.FailChanged, at, subject)]
                });
                var raised = store.Issues(ContractPair).Single();
                store.Commit(ContractPair, StateChange.Empty with { Close = [(closePath, [kind])], CloseScope = scope });
                Check.Require((store.IssueById(ContractPair, raised.Id) is not null) == survives,
                    id + (store is StateStore ? " real" : " in-memory"));
            }
            WithRealStore(store => CheckStore(store));
            using var memory = new TestDoubles.InMemoryStateStore();
            memory.SavePair(new PairConfig(ContractPair, ContractPair, ContractRoot, ContractRemote,
                ContractConnection, [], [], FirstRunPolicy.NewestWins, true, null));
            CheckStore(memory);
        }
    }

    private static void ScopedIssueCases()
    {
        WithRealStore(store => CheckStore(store));
        using var memory = new TestDoubles.InMemoryStateStore();
        memory.SavePair(new PairConfig(ContractPair, ContractPair, ContractRoot, ContractRemote,
            ContractConnection, [], [], FirstRunPolicy.NewestWins, true, null));
        CheckStore(memory);

        static void CheckStore(IStateStore store)
        {
            store.Commit(ContractPair, StateChange.Empty with
            {
                Raise =
                [
                    (IssueKind.Failure, TextKey.FailChanged, "Outside/Display",
                        new IssueSubject([Local("Outside/First"), Dropbox("Parent/Remote"), Local("Parent/Local")], null)),
                    (IssueKind.Tie, TextKey.TieEqualTimes, "Parent/EmptyTie", new IssueSubject([], null)),
                    (IssueKind.NameClash, TextKey.ClashNameTaken, "Parent/EmptyClash", new IssueSubject([], null)),
                    (IssueKind.Failure, TextKey.FailChanged, "Parent/EmptyFailure", new IssueSubject([], null)),
                    (IssueKind.Failure, TextKey.FailChanged, "Parent", new IssueSubject([Local("Parent")], null)),
                    (IssueKind.Failure, TextKey.FailChanged, "Parent2/Child", new IssueSubject([Local("Parent2/Child")], null)),
                    (IssueKind.Info, TextKey.InfoDropboxHistoryUnchecked, "Parent/EmptyInfo", new IssueSubject([], null)),
                    (IssueKind.Info, TextKey.InfoDropboxHistoryUnchecked, "Outside/Info", new IssueSubject([Dropbox("Parent/Info")], null))
                ]
            });
            var original = store.Issues(ContractPair);
            store.DismissIssue(ContractPair, original.Single(issue => issue.Path == "Outside/Display").Id);
            var expectedPaths = new[] { "Outside/Display", "Parent/EmptyTie", "Parent/EmptyClash",
                "Parent/EmptyFailure", "Parent", "Outside/Info" }.ToHashSet(StringComparer.Ordinal);
            foreach (var kinds in new IReadOnlyList<IssueKind>?[] { null, [IssueKind.Failure], [], IssueClosure.AfterHeldInSync })
            {
                var all = store.Issues(ContractPair, kinds);
                Check.Require(System.Text.Json.JsonSerializer.Serialize(store.OpenIssuesUnder(ContractPair, string.Empty, kinds)) ==
                    System.Text.Json.JsonSerializer.Serialize(all),
                    "Empty scope equals Issues, including dismissed and item-less rows.");
                var scoped = store.OpenIssuesUnder(ContractPair, PathNames.Key("pArEnT"), kinds).ToArray();
                Check.Require(scoped.Select(issue => issue.Id).SequenceEqual(
                    all.Where(issue => expectedPaths.Contains(issue.Path)).Select(issue => issue.Id)) &&
                    scoped.Select(issue => issue.Id).Distinct().Count() == scoped.Length,
                    "Scope matches any side-specific subject, item-less operational path, and case, once in id order.");
                Check.Require(!store.OpenIssuesUnder(ContractPair, PathNames.Key("Missing"), kinds).Any(),
                    "An unrelated scope contains no issues.");
            }
            Check.Require(store.OpenIssuesUnder(ContractPair, PathNames.Key("Parent")).Any(issue => issue.Dismissed),
                "Dismissal does not remove scoped membership.");
            Check.Require(!store.OpenIssuesUnder("missing-pair", string.Empty).Any(), "Pair membership remains isolated.");
            using (var read = store.OpenIssuesUnder(ContractPair, string.Empty).GetEnumerator())
                Check.Require(read.MoveNext(), "A streamed issue reader can stop before consuming the whole pair.");
            Check.Require(store.Issues(ContractPair).Count == original.Count, "Disposing a partial reader leaves issue state unchanged.");
        }
    }

    // R2/R3: real SQLite, two connections. One open-read transaction keeps the page and count on one snapshot even
    // when another connection commits between them, another keeps the whole keyset traversal on one snapshot while
    // that connection deletes rows and reinserts notices under their reused ids, and the path search follows
    // PathNames' Unicode case rule.
    private static void PageSnapshotAndSearchCases()
    {
        var folder = Directory.CreateTempSubdirectory("sync-shark-issue-page-").FullName;
        try
        {
            var path = Path.Combine(folder, "state.db");
            using var writer = StateStore.Open(path);
            writer.SavePair(Pair("pair-c", "connection-c"));
            foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
                Raise(writer, "pair-c", IssueKind.Failure, TextKey.FailChanged, name);
            using var reader = StateStore.Connect(path, SqliteOpenMode.ReadOnly);

            // The attention traversal: after page 1 another connection deletes those rows and inserts new notices,
            // which reuse the deleted ids. The one read transaction must still answer with the starting snapshot.
            var before = IssueTable.PageSnapshot(reader, null, 10).Rows;
            var seeded = before.Where(row => row.PairId == "pair-c").Select(row => row.Issue.Id).ToArray();
            var replaced = false;
            var (rows, total) = IssueTable.PageSnapshot(reader, null, 2, between: () =>
            {
                if (replaced) return;
                replaced = true;
                foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
                    writer.Commit("pair-c", StateChange.Empty with { Close = [(name, [IssueKind.Failure])] });
                foreach (var name in new[] { "x.txt", "y.txt", "z.txt" })
                    Raise(writer, "pair-c", IssueKind.Failure, TextKey.FailChanged, name);
            });
            var live = writer.Issues("pair-c");
            Check.Require(live.All(issue => issue.Id > seeded.Max()) &&
                live.Select(issue => issue.Path).SequenceEqual(["x.txt", "y.txt", "z.txt"]),
                "the replacing writes allocate ids above the deleted high-water mark");
            Check.Require(rows.Select(row => row.Issue.Id).SequenceEqual(before.Select(row => row.Issue.Id)) &&
                rows.Select(row => row.Issue.Path).SequenceEqual(before.Select(row => row.Issue.Path)) &&
                rows.Select(row => row.Issue.Id).Distinct().Count() == rows.Count && total == before.Count &&
                total == rows.Count,
                "the keyset traversal and its one count answer with the snapshot taken at the start: no skips, " +
                "no duplicates, a matching count; ids=" + string.Join(',', rows.Select(row => row.Issue.Id)) +
                " paths=" + string.Join(',', rows.Select(row => row.Issue.Path)) + " total=" + total);

            writer.SavePair(Pair("pair-a", "connection-a"));
            writer.SavePair(Pair("pair-b", "connection-b"));
            Raise(writer, "pair-a", IssueKind.Tie, TextKey.TieEqualTimes, "Café.txt");
            Raise(writer, "pair-a", IssueKind.Failure, TextKey.FailChanged, "b.txt");
            Raise(writer, "pair-b", IssueKind.Tie, TextKey.TieEqualTimes, "c.txt");

            var snapshot = IssueTable.OpenPage(reader, "pair-a", null, null, null, 10, 0,
                between: () => Raise(writer, "pair-a", IssueKind.Tie, TextKey.TieEqualTimes, "late.txt"));
            Check.Require(snapshot.Total == 2 && snapshot.Page.Count == 2,
                "the page and count share one snapshot; a commit between them is not visible");

            var accented = IssueTable.OpenPage(reader, "pair-a", null, "CAFÉ", null, 10, 0);
            Check.Require(accented.Total == 1 && accented.Page.Single().Issue.Path == "Café.txt",
                "the SQLite page search follows PathNames' Unicode case rule");

            var account = IssueTable.OpenPage(reader, null, "connection-b", null, null, 10, 0);
            Check.Require(account.Total == 1 && account.Page.Single().PairId == "pair-b",
                "the account filter is applied in Storage, not after paging");

            var none = IssueTable.OpenPage(reader, null, null, "unmatched", null, 10, 0);
            Check.Require(none.Total == 0 && none.Page.Count == 0, "a non-matching search pages nothing");

            var failures = IssueTable.PageSnapshot(reader, [IssueKind.Failure], 10);
            Check.Require(failures.Total == failures.Rows.Count &&
                failures.Rows.All(row => row.Issue.Kind == IssueKind.Failure) &&
                failures.Rows.Any(row => row.Issue.Path == "b.txt"),
                "a snapshot with kinds applies the filter in SQL and counts exactly its rows");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

    }

        private static PairConfig Pair(string pairId, string connectionId) => new(pairId, pairId,
            "C:/" + pairId, "/" + pairId, connectionId, [], [], FirstRunPolicy.NewestWins, true, null);

        private static void Raise(StateStore store, string pairId, IssueKind kind, TextKey cause, string issuePath) =>
            store.Commit(pairId, StateChange.Empty with
            {
                Raise = [(kind, cause, issuePath, new IssueSubject([], null))]
            });

    private static void RaiseCases(SqliteConnection connection)
    {
        const string pair = "raise";
        AddPair(connection, pair);
        Raise(connection, pair, IssueKind.Failure, TextKey.FailChanged, "a.txt", Now, Local("a.txt"));

        var issues = IssueTable.List(connection, pair);
        Check.Require(issues.Count == 1, "raise creates one open issue");
        Check.Require(issues[0].Kind == IssueKind.Failure && issues[0].Cause == TextKey.FailChanged &&
            issues[0].Path == "a.txt" && !issues[0].Dismissed && issues[0].At.UtcTicks == Now.UtcTicks,
            "raise stores kind, cause, path and time");
        Check.Require(issues[0].Subject.Items.Count == 1 && issues[0].Subject.Items[0] == Local("a.txt"),
            "raise stores the subject items");
        Check.Require(issues[0].Subject.Incoming is null, "raise stores the incoming side");

        Raise(connection, pair, IssueKind.Failure, TextKey.FailChanged, "a.txt", Now.AddHours(1), Local("a.txt"));
        issues = IssueTable.List(connection, pair);
        Check.Require(issues.Count == 1 && issues[0].At.UtcTicks == Now.UtcTicks, "an identical identity continues");

        Raise(connection, pair, IssueKind.Tie, TextKey.TieEqualTimes, "set.txt", Now,
            Local("set.txt"), Dropbox("set.txt"));
        Raise(connection, pair, IssueKind.Tie, TextKey.TieEqualTimes, "set.txt", Now.AddHours(1),
            Dropbox("set.txt"), Local("set.txt"));
        foreach (var issue in IssueTable.List(connection, pair))
            if (issue.Kind == IssueKind.Tie) Check.Require(issue.At.UtcTicks == Now.UtcTicks,
                "a subject item set in another order continues");

        IssueTable.Dismiss(connection, pair, issues[0].Id);
        Raise(connection, pair, IssueKind.Failure, TextKey.FailChanged, "a.txt", Now.AddHours(2), Local("a.txt"));
        Check.Require(IssueTable.ById(connection, pair, issues[0].Id)!.Dismissed,
            "a dismissed issue stays dismissed while its cause continues");

        Raise(connection, pair, IssueKind.Failure, TextKey.FailEarlierReplace, "a.txt", Now.AddHours(3), Local("a.txt"));
        var replaced = IssueTable.ById(connection, pair, issues[0].Id)!;
        Check.Require(replaced.Cause == TextKey.FailEarlierReplace && !replaced.Dismissed &&
            replaced.At.UtcTicks == Now.AddHours(3).UtcTicks, "a different cause replaces it open");

        Raise(connection, pair, IssueKind.Failure, TextKey.FailEarlierReplace, "a.txt", Now.AddHours(4),
            Local("a.txt"), Local("b.txt"));
        replaced = IssueTable.ById(connection, pair, issues[0].Id)!;
        Check.Require(replaced.Subject.Items.Count == 2 && replaced.At.UtcTicks == Now.AddHours(4).UtcTicks,
            "a different subject replaces it open");

        Close(connection, pair, "a.txt", IssueKind.Failure);
        Check.Require(IssueTable.ById(connection, pair, issues[0].Id) is null, "close removes the issue");
        Raise(connection, pair, IssueKind.Failure, TextKey.FailEarlierReplace, "a.txt", Now.AddHours(5), Local("a.txt"));
        var recurrence = IssueTable.List(connection, pair).Where(issue => issue.Path == "a.txt").ToArray();
        Check.Require(recurrence.Length == 1 && !recurrence[0].Dismissed, "a raise after a close opens a new issue");
    }

    private static void CloseCases(SqliteConnection connection)
    {
        const string pair = "close";
        AddPair(connection, pair);
        Raise(connection, pair, IssueKind.Failure, TextKey.FailChanged, "p.txt", Now, Local("p.txt"));
        Raise(connection, pair, IssueKind.Tie, TextKey.TieEqualTimes, "p.txt", Now, Local("p.txt"), Dropbox("p.txt"));

        Close(connection, pair, "p.txt", IssueKind.Failure);
        var issues = IssueTable.List(connection, pair);
        Check.Require(issues.Count == 1 && issues[0].Kind == IssueKind.Tie, "close removes only the named kinds");

        Close(connection, pair, "p.txt", IssueKind.Failure, IssueKind.Tie);
        Check.Require(IssueTable.List(connection, pair).Count == 0, "close removes every named kind");
        foreach (var path in new[] { "Parent/file.txt", "parent/FILE.txt", "Other/file.txt" })
            Raise(connection, pair, IssueKind.Failure, TextKey.FailChanged, path, Now, Local(path));
        Close(connection, pair, "PARENT/File.txt", IssueKind.Failure);
        Check.Require(IssueTable.List(connection, pair).Single().Path == "Other/file.txt",
            "Canonical closure removes every matching spelling but preserves another key.");
    }

    private static void ReadCases(SqliteConnection connection)
    {
        AddPair(connection, "read-a");
        AddPair(connection, "read-b");
        Raise(connection, "read-a", IssueKind.Info, TextKey.InfoNewestWins, "i.txt", Now, Local("i.txt"));

        var issue = IssueTable.List(connection, "read-a").Single();
        var byId = IssueTable.ById(connection, "read-a", issue.Id);
        Check.Require(byId is not null && byId.Id == issue.Id && byId.Kind == issue.Kind &&
            byId.Cause == issue.Cause && byId.Path == issue.Path && byId.At.UtcTicks == issue.At.UtcTicks &&
            byId.Subject.Items.SequenceEqual(issue.Subject.Items), "by id returns the issue");
        Check.Require(IssueTable.ById(connection, "read-b", issue.Id) is null, "by id is scoped to its pair");
        Check.Require(IssueTable.ById(connection, "read-a", issue.Id + 1) is null, "by id returns null when absent");
        Check.Require(IssueTable.List(connection, "read-b").Count == 0, "list is scoped to its pair");
        foreach (var kind in Enum.GetValues<IssueKind>())
            Raise(connection, "read-a", kind, TextKey.FailChanged, kind.ToString(), Now, Local(kind.ToString()));
        var all = IssueTable.List(connection, "read-a");
        foreach (var value in all.Where(value => value.Kind != IssueKind.Info))
            IssueTable.Dismiss(connection, "read-a", value.Id);
        all = IssueTable.List(connection, "read-a");
        Check.Require(all.Count == Enum.GetValues<IssueKind>().Length + 1 &&
            all.Select(value => value.Id).SequenceEqual(all.Select(value => value.Id).Order()),
            "omitted kinds retain every row in issue-ID order, including dismissals");
        foreach (var kinds in Enum.GetValues<IssueKind>().Select(kind => new[] { kind })
            .Append(IssueClosure.AfterHeldInSync.ToArray()))
        {
            var filtered = IssueTable.List(connection, "read-a", kinds);
            var expected = all.Where(value => kinds.Contains(value.Kind)).ToArray();
            Check.Require(filtered.Select(value => value.Id).SequenceEqual(expected.Select(value => value.Id)) &&
                filtered.Select(value => value.Dismissed).SequenceEqual(expected.Select(value => value.Dismissed)),
                "kind filtering preserves issue-ID order and dismissed rows");
        }
    }

    private static void HeldKeyCases(SqliteConnection connection)
    {
        const string pair = "held";
        AddPair(connection, pair);
        AddPair(connection, "held-other");

        Raise(connection, pair, IssueKind.Tie, TextKey.TieEqualTimes, "a.txt", Now,
            Local("a.txt"), Dropbox("b.txt"));
        IssueTable.Dismiss(connection, pair, IssueTable.List(connection, pair).Single().Id);
        Raise(connection, pair, IssueKind.NameClash, TextKey.ClashNameTaken, "c.txt", Now, Local("c.txt"), Local("a.txt"));
        Raise(connection, pair, IssueKind.NameClash, TextKey.ClashMappedNames, "m1.txt", Now,
            Local("m1.txt"), Dropbox("m2.txt"));
        Raise(connection, pair, IssueKind.NameClash, TextKey.ClashMappedDiffers, "m3.txt", Now, Local("m3.txt"));
        Raise(connection, pair, IssueKind.Failure, TextKey.FailChanged, "f.txt", Now, Local("f.txt"));
        Raise(connection, pair, IssueKind.Info, TextKey.InfoNewestWins, "i.txt", Now, Local("i.txt"));
        Raise(connection, "held-other", IssueKind.Tie, TextKey.TieEqualTimes, "o.txt", Now, Local("o.txt"));

        var held = IssueTable.HeldKinds(connection, pair);
        Check.Require(held.TryGetValue(PathNames.Key("a.txt"), out var tie) && tie == IssueKind.Tie &&
            held.TryGetValue(PathNames.DropboxKey("b.txt"), out var tieDropbox) && tieDropbox == IssueKind.Tie,
            "held keys come from every subject item with their issue kind, dismissed included");
        Check.Require(held.TryGetValue(PathNames.Key("c.txt"), out var clash) && clash == IssueKind.NameClash,
            "a name clash holds with its kind");
        Check.Require(!held.ContainsKey(PathNames.Key("m1.txt")) && !held.ContainsKey(PathNames.DropboxKey("m2.txt")) &&
            !held.ContainsKey(PathNames.Key("m3.txt")), "the mapped-name causes do not hold");
        Check.Require(!held.ContainsKey(PathNames.Key("f.txt")) && !held.ContainsKey(PathNames.Key("i.txt")),
            "a temporary failure and info issues do not hold");
        Check.Require(!held.ContainsKey(PathNames.Key("o.txt")), "held keys are scoped to their pair");
        Check.Require(held.Count == 3, "held keys hold exactly the subject items of the holding issues");
    }

    private static void HeldOwnerCases(SqliteConnection connection)
    {
        const string pair = "held-owner";
        AddPair(connection, pair);
        foreach (var kind in Enum.GetValues<IssueKind>())
        foreach (var cause in Enum.GetValues<TextKey>())
        {
            var path = kind + "/" + cause;
            Raise(connection, pair, kind, cause, path, Now, Local(path));
        }
        var held = IssueTable.HeldKinds(connection, pair);
        var expectedCount = 0;
        foreach (var kind in Enum.GetValues<IssueKind>())
        foreach (var cause in Enum.GetValues<TextKey>())
        {
            var path = kind + "/" + cause;
            var expected = IssuePolicy.Holds(kind, cause);
            Check.Require(held.TryGetValue(PathNames.Key(path), out var actual) == expected &&
                (!expected || actual == kind), "stored holding classification follows its owner: " + path);
            if (expected) expectedCount++;
        }
        Check.Require(held.Count == expectedCount, "holding reads add no keys beyond the policy's subjects");
    }

    private static void RetryCases(SqliteConnection connection)
    {
        const string pair = "retry";
        AddPair(connection, pair);
        AddPair(connection, "retry-other");
        Raise(connection, pair, IssueKind.Failure, TextKey.FailDropboxNameRejected, "a:b", Now);
        Raise(connection, pair, IssueKind.Failure, TextKey.FailDropboxNoPermission, "refused", Now,
            Local("refused"), Dropbox("remote:refused"));
        Raise(connection, pair, IssueKind.Tie, TextKey.TieEqualTimes, "tie", Now, Local("refused"));
        Raise(connection, pair, IssueKind.NameClash, TextKey.ClashNameTaken, "clash", Now, Local("clash"));
        Raise(connection, "retry-other", IssueKind.Failure, TextKey.FailDropboxNameRejected, "foreign", Now,
            Local("foreign"));
        var issues = IssueTable.List(connection, pair);
        var empty = issues.Single(issue => issue.Path == "a:b");
        var failure = issues.Single(issue => issue.Path == "refused");
        IssueTable.Dismiss(connection, pair, failure.Id);
        var held = IssueTable.HeldKinds(connection, pair);
        // A subject-less Failure's two side keys are the same frozen key in this build
        // (PathNames.DropboxKey == PathNames.Key), so it contributes one dictionary entry.
        Check.Require(held.Count == 4 && held[PathNames.Key("a:b")] == IssueKind.Failure &&
            held[PathNames.DropboxKey("a:b")] == IssueKind.Failure &&
            held[PathNames.Key("refused")] == IssueKind.Failure &&
            held[PathNames.DropboxKey("remote:refused")] == IssueKind.Failure,
            "definite failures hold both subject-less side keys and every dismissed subject, with earliest ID precedence");
        var selected = new HashSet<long> { failure.Id, issues.Single(issue => issue.Kind == IssueKind.Tie).Id,
            issues.Single(issue => issue.Kind == IssueKind.NameClash).Id };
        held = IssueTable.HeldKinds(connection, pair, selected);
        Check.Require(held.Count == 3 && held[PathNames.Key("refused")] == IssueKind.Tie &&
            held[PathNames.Key("clash")] == IssueKind.NameClash &&
            !held.ContainsKey(PathNames.DropboxKey("remote:refused")) &&
            !held.ContainsKey(PathNames.Key("foreign")),
            "retry omits only selected Failures, retaining other kinds, other failures and pair isolation");
        selected.Add(empty.Id);
        Execute(connection, null,
            "UPDATE issue SET subject_json='invalid' WHERE issue_id=$id;", (StateSchema.ParamId, failure.Id));
        held = IssueTable.HeldKinds(connection, pair, selected);
        Check.Require(held.Count == 2 && held[PathNames.Key("refused")] == IssueKind.Tie &&
            held[PathNames.Key("clash")] == IssueKind.NameClash,
            "retry skips selected failures before decoding, without suppressing Tie or NameClash");
        Check.Require(IssueTable.Counts(connection, pair)[IssueKind.Failure] == 1,
            "retry selection leaves failure rows and dismissal unchanged");
    }

    private static void ProofCases(SqliteConnection connection)
    {
        const string pair = "proof";
        AddPair(connection, pair);
        var local = new Entry("proof.txt", false, 5, Now, "local-id", "", "", null);
        var proof = new LocalHashProof(local.Size, local.Modified.UtcTicks, new string('a', StateSchema.HashHexLength));
        var subject = new IssueSubject([new IssueItem(Side.Local, local.Path, local.Id), Dropbox(local.Path)], null);
        RaiseSubject(connection, pair, IssueKind.Tie, TextKey.TieEqualTimes, local.Path, Now, subject);
        var original = IssueTable.List(connection, pair).Single();
        IssueTable.Dismiss(connection, pair, original.Id);
        subject = subject with { Items = subject.Items.Reverse().ToArray(), Incoming = Side.Dropbox };
        foreach (var next in new LocalHashProof?[] { proof, proof with { Hash = new string('b', StateSchema.HashHexLength) }, null })
        {
            subject = subject with { LocalHash = next };
            RaiseSubject(connection, pair, IssueKind.Tie, TextKey.TieEqualTimes, local.Path, Now.AddHours(1), subject);
            var stored = IssueTable.ById(connection, pair, original.Id)!;
            Check.Require(stored.Subject.LocalHash == next && stored.At == original.At && stored.Dismissed,
                "adding, replacing or removing a proof preserves the continuing ID, timestamp and dismissal");
            Check.Require(stored.Subject.Incoming == original.Subject.Incoming &&
                stored.Subject.Items.SequenceEqual(original.Subject.Items),
                "proof refresh preserves the stored incoming side and item order");
            Check.Require(stored.Subject.ReusableLocalHash(local) == next?.Hash,
                "a persisted matching proof round-trips through its reuse owner");
            using var changes = connection.CreateCommand();
            changes.CommandText = "SELECT total_changes();";
            var before = (long)changes.ExecuteScalar()!;
            RaiseSubject(connection, pair, IssueKind.Tie, TextKey.TieEqualTimes, local.Path, Now.AddHours(2), subject);
            Check.Require((long)changes.ExecuteScalar()! == before,
                "an identical proof and item set perform no write even when item order changes");
        }
        subject = subject with { LocalHash = proof with { Size = -1 } };
        RaiseSubject(connection, pair, IssueKind.Tie, TextKey.TieEqualTimes, local.Path, Now.AddHours(3), subject);
        var malformed = IssueTable.List(connection, pair).Single();
        Check.Require(malformed.Id == original.Id && malformed.Subject.ReusableLocalHash(local) is null,
            "a malformed persisted proof leaves the issue readable but cannot authorize reuse");
        RaiseSubject(connection, pair, IssueKind.Tie, TextKey.TieEqualTimes, local.Path, Now.AddHours(4),
            new IssueSubject([new IssueItem(Side.Local, local.Path, "replacement-id"), Dropbox(local.Path)], null));
        var replacement = IssueTable.ById(connection, pair, original.Id)!;
        Check.Require(replacement.Subject.LocalHash is null && !replacement.Dismissed &&
            replacement.At == Now.AddHours(4), "a changed participant reopens the issue without inheriting its old proof");
    }

    // The subject's items are materialised before its incoming side, as in the inline construction this replaced.
    private static void SkippedEvaluationOrderCases(SqliteConnection connection)
    {
        const string pair = "skipped-order";
        AddPair(connection, pair);
        (string Path, TextKey Cause, IssueSubject Subject)[] entries =
        [
            ("order.txt", TextKey.SkipLink, new IssueSubject(new ThrowingItems(), null)),
            ("order.txt", TextKey.SkipLink, null!),
        ];
        using var transaction = connection.BeginTransaction();
        try
        {
            IssueTable.ReplaceSkipped(connection, transaction, pair, PairRow(connection, transaction, pair), Guid.Empty.ToByteArray(), entries, Now);
        }
        catch (InvalidOperationException error)
        {
            Check.Require(error.Message == ThrowingItems.Message,
                "ReplaceSkipped enumerates the items before the incoming side: " + error.Message);
            return;
        }
        throw new InvalidOperationException("ReplaceSkipped did not enumerate the throwing item list");
    }

    private sealed class ThrowingItems : IReadOnlyList<IssueItem>
    {
        public const string Message = "throwing item list";
        public IssueItem this[int index] => throw new InvalidOperationException(Message);
        public int Count => 1;
        public IEnumerator<IssueItem> GetEnumerator() => throw new InvalidOperationException(Message);
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void SkippedCases(SqliteConnection connection)
    {
        const string pair = "skipped";
        AddPair(connection, pair);
        Raise(connection, pair, IssueKind.Failure, TextKey.FailChanged, "keep.txt", Now, Local("keep.txt"));

        using (var transaction = connection.BeginTransaction())
        {
            IssueTable.ReplaceSkipped(connection, transaction, pair, PairRow(connection, transaction, pair), Guid.Empty.ToByteArray(),
                [("s1.txt", TextKey.SkipLink, Subject(Local("s1.txt"))),
                 ("s2.txt", TextKey.SkipSpecial, Subject(Local("s2.txt")))], Now);
            transaction.Commit();
        }

        var skipped = IssueTable.List(connection, pair).Where(issue => issue.Kind == IssueKind.Skipped).ToArray();
        Check.Require(skipped.Length == 2 && skipped.All(issue => !issue.Dismissed && issue.At.UtcTicks == Now.UtcTicks),
            "replace skipped writes the open set");
        Check.Require(skipped.Any(issue => issue.Path == "s2.txt" && issue.Cause == TextKey.SkipSpecial),
            "replace skipped stores each cause");

        using (var transaction = connection.BeginTransaction())
        {
            IssueTable.ReplaceSkipped(connection, transaction, pair, PairRow(connection, transaction, pair), Guid.Empty.ToByteArray(),
                [("s3.txt", TextKey.SkipOnlineOnly, Subject(Local("s3.txt")))], Now.AddHours(1));
            transaction.Commit();
        }

        var issues = IssueTable.List(connection, pair);
        skipped = issues.Where(issue => issue.Kind == IssueKind.Skipped).ToArray();
        Check.Require(skipped.Length == 1 && skipped[0].Path == "s3.txt" &&
            skipped[0].Cause == TextKey.SkipOnlineOnly && skipped[0].At.UtcTicks == Now.AddHours(1).UtcTicks,
            "replace skipped replaces the whole set");
        Check.Require(issues.Any(issue => issue.Kind == IssueKind.Failure && issue.Path == "keep.txt"),
            "replace skipped leaves other kinds alone");

        using (var transaction = connection.BeginTransaction())
        {
            IssueTable.ReplaceSkipped(connection, transaction, pair, PairRow(connection, transaction, pair), Guid.Empty.ToByteArray(),
                [("both.txt", TextKey.SkipReservedName, Subject(Local("both.txt"))),
                 ("both.txt", TextKey.SkipReservedName, Subject(Dropbox("both.txt")))], Now.AddHours(2));
            transaction.Commit();
        }

        var both = IssueTable.List(connection, pair).Where(issue => issue.Kind == IssueKind.Skipped).ToArray();
        Check.Require(both.Length == 1 && both[0].Path == "both.txt" && both[0].Subject.Items.Count == 2,
            "replace skipped writes one issue per path with the subject items of both sides merged");

        IssueTable.Dismiss(connection, pair, both[0].Id);
        using (var transaction = connection.BeginTransaction())
        {
            IssueTable.ReplaceSkipped(connection, transaction, pair, PairRow(connection, transaction, pair), Guid.Empty.ToByteArray(),
                [("both.txt", TextKey.SkipReservedName, Subject(Dropbox("both.txt"), Local("both.txt")))],
                Now.AddHours(3));
            transaction.Commit();
        }

        var kept = IssueTable.List(connection, pair).Where(issue => issue.Kind == IssueKind.Skipped).ToArray();
        Check.Require(kept.Length == 1 && kept[0].Id == both[0].Id && kept[0].Dismissed &&
            kept[0].At.UtcTicks == both[0].At.UtcTicks, "an identical skipped issue keeps its row and dismissal");
    }

    private static void NarrowReadCases(SqliteConnection connection)
    {
        const string pair = "narrow";
        AddPair(connection, pair);
        AddPair(connection, "narrow-other");
        Raise(connection, pair, IssueKind.Failure, TextKey.FailExcludeHoldsProtected, "dismissed", Now, Local("dismissed"));
        Raise(connection, pair, IssueKind.Failure, TextKey.FailChanged, "ordinary", Now, Local("ordinary"));
        Raise(connection, pair, IssueKind.Tie, TextKey.FailExcludeHoldsProtected, "tie", Now, Local("tie"));
        var failure = IssueTable.List(connection, pair).Single(issue => issue.Path == "dismissed");
        IssueTable.Dismiss(connection, pair, failure.Id);
        Execute(connection, null, "UPDATE issue SET subject_json='invalid' WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND path='ordinary';",
            (StateSchema.ParamPair, pair));
        var counts = IssueTable.Counts(connection, pair);
        Check.Require(counts.Count == 2 && counts[IssueKind.Failure] == 1 && counts[IssueKind.Tie] == 1,
            "counts exclude dismissed issues without decoding unrelated malformed subjects");
        Check.Require(IssueTable.HeldKinds(connection, pair).Count == 1,
            "held subjects exclude malformed unrelated failures before decoding");
        var filtered = IssueTable.List(connection, pair, [IssueKind.Tie]);
        Check.Require(filtered.Count == 1 && filtered[0].Path == "tie" &&
            IssueTable.List(connection, pair, []).Count == 0 &&
            IssueTable.List(connection, "narrow-other", [IssueKind.Tie]).Count == 0 &&
            IssueTable.Counts(connection, "narrow-other").Count == 0,
            "filtered reads exclude unrelated malformed subjects and honor empty kinds and pair scope");
        using var transaction = connection.BeginTransaction();
        IssueTable.ReplaceSkipped(connection, transaction, pair, PairRow(connection, transaction, pair), Guid.Empty.ToByteArray(),
            [("skip", TextKey.SkipLink, new IssueSubject([Local("skip")], null)),
             ("skip", TextKey.SkipSpecial, new IssueSubject([Dropbox("skip"), Local("skip")], Side.Dropbox))], Now);
        transaction.Commit();
        using var command = connection.CreateCommand();
        command.CommandText = StateSchema.SelectIssueByPathKind;
        command.Parameters.AddWithValue(StateSchema.ParamPair, pair);
        command.Parameters.AddWithValue(StateSchema.ParamPath, "skip");
        command.Parameters.AddWithValue(StateSchema.ParamKind, (int)IssueKind.Skipped);
        using var reader = command.ExecuteReader();
        Check.Require(reader.Read() && reader.GetString(StateSchema.IssueCause) == TextKey.SkipLink.ToString(),
            "skipped reconciliation preserves the first cause despite malformed unrelated subjects");
        var subject = System.Text.Json.JsonSerializer.Deserialize<IssueSubject>(reader.GetString(StateSchema.IssueSubject))!;
        Check.Require(subject.Items.SequenceEqual([Local("skip"), Dropbox("skip")]) && subject.Incoming == Side.Dropbox,
            "skipped reconciliation preserves item order and first non-null incoming side");
    }

    private static void DismissCases(SqliteConnection connection)
    {
        const string pair = "dismiss";
        AddPair(connection, pair);
        foreach (var kind in new[] { IssueKind.Info, IssueKind.Tie, IssueKind.NameClash, IssueKind.Skipped })
        {
            Raise(connection, pair, kind, TextKey.InfoNewestWins, kind.ToString(), Now, Local(kind.ToString()));
            var issue = IssueTable.List(connection, pair).Single(value => value.Kind == kind);
            IssueTable.Dismiss(connection, "read-b", issue.Id);
            Check.Require(IssueTable.ById(connection, pair, issue.Id) is { Dismissed: false }, "dismiss is scoped to the pair");
            IssueTable.Dismiss(connection, pair, issue.Id);
            var remaining = IssueTable.ById(connection, pair, issue.Id);
            Check.Require(kind == IssueKind.Info ? remaining is null : remaining is { Dismissed: true },
                "Info dismissal deletes; Tie, NameClash and Skipped retain their dismissed rows");
        }
    }

    // Neutral fixture values identify separate lifecycle cases, not production configuration.
    private const string ContractPair = "issue-contract";
    private const string ContractPath = "Parent/Item";
    private const string ContractOther = "Sibling/Item";
    private const string ContractId = "item-id";
    private const string ReplacementId = "replacement-id";
    private const string ContractRoot = "C:\\Synthetic";
    private const string ContractRemote = "/Synthetic";
    private const string ContractConnection = "connection";
    private const string ContractDirectory = "syncshark-issue-contract-";
    private const string ContractDatabase = "state.db";
    private const string ContractGuidFormat = "N";
    private const string ContractAssertion = "issue-lifecycle-contract";

    private static void FirstRunPreservationCases(SqliteConnection connection)
    {
        foreach (var marker in Enum.GetValues<IssueSubject.FirstRunDeletionKind>())
        {
            var pair = ContractPair + marker;
            AddPair(connection, pair);
            var item = new IssueItem(Side.Local, ContractPath, ContractId);
            var original = new IssueSubject([item], null)
            {
                FirstRunDeletion = IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice
            };
            RaiseSubject(connection, pair, IssueKind.Failure, TextKey.FailChanged, ContractPath, Now, original);
            var issue = IssueTable.List(connection, pair).Single();
            IssueTable.Dismiss(connection, pair, issue.Id);
            foreach (var cause in new[] { TextKey.FailChanged, TextKey.SkipUnreadable, TextKey.FailFirstSyncCheckWaiting })
            {
                RaiseSubject(connection, pair, IssueKind.Failure, cause, ContractPath,
                    Now.AddTicks(1), original with { FirstRunDeletion = marker });
                var current = IssueTable.ById(connection, pair, issue.Id)!;
                Check.Require(current.Cause == TextKey.FailChanged && current.Dismissed && current.At == issue.At &&
                    current.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice,
                    ContractAssertion);
            }
            Close(connection, pair, ContractPath, IssueKind.Failure);
            Check.Require(IssueTable.ById(connection, pair, issue.Id) is not null, ContractAssertion);
            using (var transaction = connection.BeginTransaction())
            {
                IssueTable.AcknowledgeFirstRun(connection, transaction, pair, item with { Id = ReplacementId });
                transaction.Commit();
            }
            Check.Require(IssueTable.ById(connection, pair, issue.Id)!.Subject.FirstRunDeletion ==
                IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice, ContractAssertion);
            using (var transaction = connection.BeginTransaction())
            {
                IssueTable.AcknowledgeFirstRun(connection, transaction, pair, item);
                transaction.Rollback();
            }
            Check.Require(IssueTable.ById(connection, pair, issue.Id)!.Subject.FirstRunDeletion ==
                IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice, ContractAssertion);
            RaiseSubject(connection, pair, IssueKind.Failure, TextKey.FailEarlierReplace, ContractPath,
                Now.AddTicks(1), new IssueSubject([item with { Id = ReplacementId }], null));
            var replaced = IssueTable.ById(connection, pair, issue.Id)!;
            Check.Require(replaced.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.None &&
                !replaced.Dismissed && replaced.Cause == TextKey.FailEarlierReplace, ContractAssertion);
        }
    }

    private static void WithRealStore(Action<StateStore> action) => WithRealStore((store, _) => action(store));

    private static void WithRealStore(Action<StateStore, string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), ContractDirectory + Guid.NewGuid().ToString(ContractGuidFormat));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = StateStore.Open(Path.Combine(directory, ContractDatabase));
            store.SavePair(new PairConfig(ContractPair, ContractPair, ContractRoot, ContractRemote,
                ContractConnection, [], [], FirstRunPolicy.NewestWins, true, null));
            action(store, Path.Combine(directory, ContractDatabase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Refuses(Action action) => Refuses<InvalidOperationException>(action);

    private static void Refuses<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(ContractAssertion);
    }

    private static void PageWaitCases()
    {
        // Each exit calls the same exact Store carrier; transport/host tests own choosing the exit.
        foreach (var independentDebt in new[] { false, true })
        WithRealStore(store =>
        {
            var generation = store.ReadPairExecution(ContractPair)!.Value.Generation;
            var local = store.ReadSource(ContractPair, Side.Local)!.Value;
            var remote = store.ReadSource(ContractPair, Side.Dropbox)!.Value;
            var change = StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.RequiredRepairRetry, string.Empty, new IssueSubject([], null))]
            };
            if (independentDebt)
                store.RecordPrerequisiteFailure(ContractPair, generation, local.Epoch, remote.Epoch,
                    local.Revision, remote.Revision, null,
                    new Massdex.SyncShark.V2.FailureRetryOwner.State(0, Now), change);
            var retry = store.ReadPrerequisiteRetry(ContractPair);
            var operation = new object();
            var notice = store.RecordPrerequisitePageWait(ContractPair, generation, operation, change);
            var repeated = store.RecordPrerequisitePageWait(ContractPair, generation, operation, change);
            Check.Require(notice.Id == repeated.Id && store.Issues(ContractPair).Count == 1 &&
                store.ReadPrerequisiteRetry(ContractPair) == retry, ContractAssertion);
            Refuses(() => store.CompletePrerequisitePageWait(ContractPair, generation, new object(), repeated));
            Refuses(() => store.CompletePrerequisitePageWait(ContractPair, generation, operation,
                repeated with { Id = repeated.Id + 1 }));
            Refuses(() => store.CompletePrerequisitePageWait(ContractPair, generation + 1, operation, repeated));
            Check.Require(store.Issues(ContractPair).Count == 1, ContractAssertion);
            store.CompletePrerequisitePageWait(ContractPair, generation, operation, repeated);
            Check.Require(store.Issues(ContractPair).Any() == independentDebt &&
                store.ReadPrerequisiteRetry(ContractPair) == retry &&
                store.ReadSource(ContractPair, Side.Local) == local &&
                store.ReadSource(ContractPair, Side.Dropbox) == remote, ContractAssertion);
        });
    }

    private static void LiveSkippedCases()
    {
        foreach (var touched in Enum.GetValues<Side>())
        WithRealStore(store =>
        {
            var generation = store.ReadPairExecution(ContractPair)!.Value.Generation;
            var opposite = touched == Side.Local ? Side.Dropbox : Side.Local;
            var subject = new IssueSubject([new IssueItem(touched, ContractPath, ContractId)], null);
            var other = new IssueSubject([new IssueItem(opposite, ContractPath, ContractId)], null);
            var token = store.BeginScan(ContractPair, generation, includeDeleted: false);
            store.ObserveSkipped(ContractPair, generation, touched, ContractPath, null);
            store.ReplaceSkipped(ContractPair, token,
                [(ContractPath, TextKey.SkipReservedName, subject),
                 (ContractPath, TextKey.SkipLink, other)]);
            var issue = store.Issues(ContractPair).Single();
            Check.Require(issue.Subject.Items.SequenceEqual(other.Items), ContractAssertion);
            store.ObserveSkipped(ContractPair, generation, touched, ContractPath,
                (TextKey.SkipReservedName, subject));
            store.ReplaceSkipped(ContractPair, token, []);
            issue = store.Issues(ContractPair).Single();
            Check.Require(issue.Subject.Items.SequenceEqual(subject.Items) &&
                issue.Cause == TextKey.SkipReservedName, ContractAssertion);
            store.DismissIssue(ContractPair, issue.Id);
            store.ObserveSkipped(ContractPair, generation, touched, ContractPath,
                (TextKey.SkipReservedName, subject));
            Check.Require(store.IssueById(ContractPair, issue.Id) is { Dismissed: true }, ContractAssertion);
            var replacement = store.BeginScan(ContractPair, generation, includeDeleted: false);
            Refuses<LiveAdmissionStaleException>(() => store.ReplaceSkipped(ContractPair, token, []));
            store.ReplaceSkipped(ContractPair, replacement, []);
            Check.Require(store.Issues(ContractPair).Count == 0, ContractAssertion);
        });
    }

    private static void MutateFixture(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        StateStore.OpenConnection(connection);
        Execute(connection, null, sql);
    }

    private static void RejectContract<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(ContractAssertion);
    }

    private static void KeepFolderAlwaysCases() => WithRealStore((store, path) =>
    {
        var initial = store.ReadPairExecution(ContractPair)!.Value;
        var existingRule = new SyncIgnoreRule(SyncIgnoreKind.FileName, "retained.tmp");
        var addedRule = new SyncIgnoreRule(SyncIgnoreKind.FileExtension, ".tmp");
        var configured = initial.Pair with
        {
            IgnoreRules = [SyncIgnorePolicy.Format(existingRule)],
            IgnoredFolderKeepRules = [SyncIgnorePolicy.Format(existingRule)]
        };
        store.SavePairDecision(initial.Pair, initial.Generation, configured, null, false, null);
        var generation = store.ReadPairExecution(ContractPair)!.Value.Generation;
        var subject = new IssueSubject([new IssueItem(Side.Local, ContractPath, ContractId)], null);
        var saved = new SyncedEntry(PathNames.Key(ContractPath), ContractPath, PathNames.DropboxKey(ContractPath),
            ContractPath, true, ContractId, 0, 0, ContractId, string.Empty);
        store.Commit(ContractPair, StateChange.Empty with
        {
            Record = [saved],
            Raise = [(IssueKind.Info, TextKey.InfoIgnoredFolderKept, ContractPath, subject),
                (IssueKind.Failure, TextKey.FailChanged, ContractOther, subject)]
        });
        var notice = store.Issues(ContractPair).Single(issue => issue.Kind == IssueKind.Info);
        var local = store.ReadSource(ContractPair, Side.Local);
        var remote = store.ReadSource(ContractPair, Side.Dropbox);
        RejectContract<ArgumentException>(() => store.KeepFolderAlways(ContractPair, generation, notice, []));
        RejectContract<ArgumentException>(() => store.KeepFolderAlways(ContractPair, generation,
            notice with { Cause = TextKey.InfoNewestWins }, [addedRule]));
        RejectContract<LiveAdmissionStaleException>(() => store.KeepFolderAlways(ContractPair,
            generation + 1, notice, [addedRule]));
        Refuses(() => store.KeepFolderAlways(ContractPair, generation,
            notice with { Subject = new IssueSubject([new IssueItem(Side.Local, ContractPath, ReplacementId)], null) },
            [addedRule]));
        Refuses(() => store.KeepFolderAlways(ContractPair, generation,
            notice with { Path = ContractOther }, [addedRule]));
        Check.Require(store.IssueById(ContractPair, notice.Id) is not null &&
            store.Pair(ContractPair)!.IgnoredFolderKeepRules!.SequenceEqual(configured.IgnoredFolderKeepRules!),
            ContractAssertion);
        MutateFixture(path, "CREATE TRIGGER reject_keep BEFORE UPDATE OF def_json ON pair BEGIN SELECT RAISE(ABORT,'reject-keep'); END;");
        try { RejectContract<SqliteException>(() => store.KeepFolderAlways(ContractPair, generation, notice, [addedRule])); }
        finally { MutateFixture(path, "DROP TRIGGER reject_keep;"); }
        Check.Require(store.IssueById(ContractPair, notice.Id) is not null &&
            store.Pair(ContractPair)!.IgnoredFolderKeepRules!.SequenceEqual(configured.IgnoredFolderKeepRules!),
            ContractAssertion);
        store.KeepFolderAlways(ContractPair, generation, notice, [addedRule, addedRule]);
        store.KeepFolderAlways(ContractPair, generation, notice, [addedRule]);
        var expected = SyncIgnorePolicy.Normalize(new[] { existingRule, addedRule }).Select(SyncIgnorePolicy.Format);
        Check.Require(store.Pair(ContractPair)!.IgnoredFolderKeepRules!.SequenceEqual(expected) &&
            store.Pair(ContractPair)!.IgnoreRules.SequenceEqual(configured.IgnoreRules) &&
            store.Issues(ContractPair).Single().Kind == IssueKind.Failure &&
            store.SyncedByKey(ContractPair, Side.Local, saved.LocalKey) == saved &&
            store.ReadSource(ContractPair, Side.Local) == local && store.ReadSource(ContractPair, Side.Dropbox) == remote,
            ContractAssertion);
        using (var reopened = StateStore.OpenReadOnly(path)!)
            Check.Require(reopened.Pair(ContractPair)!.IgnoredFolderKeepRules!.SequenceEqual(expected) &&
                reopened.IssueById(ContractPair, notice.Id) is null, ContractAssertion);
        // One-off dismissal does not add selectors or clear an unrelated Failure.
        store.Commit(ContractPair, StateChange.Empty with
        {
            Raise = [(IssueKind.Info, TextKey.InfoIgnoredFolderKept, ContractPath, subject)]
        });
        store.DismissIssue(ContractPair, store.Issues(ContractPair).Single(issue => issue.Kind == IssueKind.Info).Id);
        Check.Require(store.Pair(ContractPair)!.IgnoredFolderKeepRules!.SequenceEqual(expected) &&
            store.Issues(ContractPair).Single().Kind == IssueKind.Failure, ContractAssertion);
        var current = store.ReadPairExecution(ContractPair)!.Value;
        store.SavePairDecision(current.Pair, current.Generation,
            current.Pair with { IgnoredFolderKeepRules = [] }, null, false, null);
        Check.Require((store.Pair(ContractPair)!.IgnoredFolderKeepRules?.Count ?? 0) == 0 &&
            store.Pair(ContractPair)!.IgnoreRules.SequenceEqual(configured.IgnoreRules), ContractAssertion);
    });

    private static void NoticeFailureAndConfigurationCases() => WithRealStore((store, path) =>
    {
        var execution = store.ReadPairExecution(ContractPair)!.Value;
        var local = store.ReadSource(ContractPair, Side.Local)!.Value;
        var remote = store.ReadSource(ContractPair, Side.Dropbox)!.Value;
        var noticeChange = StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.RequiredRepairRetry, string.Empty, new IssueSubject([], null))]
        };
        var operation = new object();
        MutateFixture(path, "CREATE TRIGGER reject_notice BEFORE INSERT ON issue BEGIN SELECT RAISE(ABORT,'reject-notice'); END;");
        try
        {
            RejectContract<SqliteException>(() => store.RecordPrerequisitePageWait(ContractPair,
                execution.Generation, operation, noticeChange));
            RejectContract<SqliteException>(() => store.RecordPrerequisiteFailure(ContractPair,
                execution.Generation, local.Epoch, remote.Epoch, local.Revision, remote.Revision, null,
                new Massdex.SyncShark.V2.FailureRetryOwner.State(0, Now), noticeChange));
        }
        finally { MutateFixture(path, "DROP TRIGGER reject_notice;"); }
        Check.Require(store.Issues(ContractPair).Count == 0 && store.ReadPrerequisiteRetry(ContractPair) is null &&
            store.ReadSource(ContractPair, Side.Local) == local && store.ReadSource(ContractPair, Side.Dropbox) == remote,
            ContractAssertion);
        var state = new Massdex.SyncShark.V2.FailureRetryOwner.State(0, Now);
        store.RecordPrerequisiteFailure(ContractPair, execution.Generation, local.Epoch, remote.Epoch,
            local.Revision, remote.Revision, null, state, noticeChange);
        var original = store.Issues(ContractPair).Single();
        store.Commit(ContractPair, StateChange.Empty with
        {
            Close = [(string.Empty, [IssueKind.Failure])],
            Raise = [(IssueKind.Failure, TextKey.FailChanged, string.Empty, new IssueSubject([], null))]
        });
        Check.Require(store.IssueById(ContractPair, original.Id)!.Cause == TextKey.RequiredRepairRetry,
            ContractAssertion);
        var next = execution.Pair with { Enabled = false };
        MutateFixture(path, "CREATE TRIGGER reject_generation BEFORE UPDATE OF execution_generation ON pair BEGIN SELECT RAISE(ABORT,'reject-generation'); END;");
        try { RejectContract<SqliteException>(() => store.SavePairDecision(execution.Pair, execution.Generation, next, null, false, null)); }
        finally { MutateFixture(path, "DROP TRIGGER reject_generation;"); }
        Check.Require(store.IssueById(ContractPair, original.Id) is not null &&
            store.ReadPairExecution(ContractPair)!.Value.Generation == execution.Generation &&
            store.ReadPrerequisiteRetry(ContractPair) == state &&
            store.ReadSource(ContractPair, Side.Local) == local && store.ReadSource(ContractPair, Side.Dropbox) == remote,
            ContractAssertion);
        store.SavePairDecision(execution.Pair, execution.Generation, next, null, false, null);
        var successor = store.Issues(ContractPair).Single();
        Check.Require(successor.Cause == TextKey.RequiredRepairRetry &&
            store.ReadPrerequisiteRetry(ContractPair) == state &&
            store.ReadPairExecution(ContractPair)!.Value.Generation == execution.Generation + 1 &&
            store.ReadSource(ContractPair, Side.Local)!.Value.Obligation is not null &&
            store.ReadSource(ContractPair, Side.Dropbox)!.Value.Obligation is not null, ContractAssertion);
        RejectContract<LiveAdmissionStaleException>(() => store.RecordPrerequisiteFailure(ContractPair,
            execution.Generation, local.Epoch, remote.Epoch, local.Revision, remote.Revision,
            state, state, noticeChange));
        using var reopened = StateStore.OpenReadOnly(path)!;
        Check.Require(reopened.ReadPrerequisiteRetry(ContractPair) == state &&
            reopened.Issues(ContractPair).Single().Cause == TextKey.RequiredRepairRetry, ContractAssertion);
    });

    public static void VisibilityCases()
    {
        var folder = Directory.CreateTempSubdirectory("issue-visibility-").FullName;
        var path = Path.Combine(folder, "state.db");
        try
        {
            long previous;
            Issue previousIssue;
            using (var store = StateStore.Open(path))
            {
                store.SavePair(Pair("pair-a", "account"));
                Raise(store, "pair-a", IssueKind.Failure, TextKey.FailChanged, "a.txt");
                var original = store.Issues("pair-a").Single();
                previous = original.Id;
                Check.Require(!store.SetIssueIgnored("other", original, true) &&
                    !store.SetIssueIgnored("pair-a", original with { Id = previous + 1 }, true), "visibility refuses foreign and missing ids");
                Check.Require(store.SetIssueIgnored("pair-a", original, true) &&
                    store.SetIssueIgnored("pair-a", original, true), "ignore is idempotent");
                Check.Require(System.Text.Json.JsonSerializer.Serialize(store.IssueById("pair-a", previous)) ==
                    System.Text.Json.JsonSerializer.Serialize(original with { Ignored = true }),
                    "ignore preserves every other persisted issue field");
                Check.Require(store.OpenIssuePage("pair-a", null, null, null, 10, 0).Total == 0 &&
                    store.OpenIssuePage("pair-a", null, null, null, 10, 0, true).Total == 1 &&
                    store.IssueCounts("pair-a").Values.Sum() == 1 &&
                    store.IssueCounts("pair-a", false).Count == 0, "presentation hides, safety still sees");
                Raise(store, "pair-a", IssueKind.Failure, TextKey.FailChanged, "a.txt");
                Check.Require(store.IssueById("pair-a", previous)!.Ignored, "identical cause stays ignored");
                store.DismissIssue("pair-a", previous);
                Check.Require(store.OpenIssuePage("pair-a", null, null, null, 10, 0, true).Total == 0,
                    "dismiss wins over ignored visibility");
                Raise(store, "pair-a", IssueKind.Failure, TextKey.FailDropboxNameRejected, "a.txt");
                Check.Require(!store.IssueById("pair-a", previous)!.Ignored, "changed cause resets ignore and dismiss");
                Check.Require(!store.SetIssueIgnored("pair-a", original, true), "a replacement between read and visibility write refuses stale authority");
                Check.Require(!store.IssueById("pair-a", previous)!.Ignored, "refused visibility choice leaves the replacement unchanged");
                Raise(store, "pair-a", IssueKind.Failure, TextKey.FailChanged, "a.txt");
                original = store.IssueById("pair-a", previous)!;
                Check.Require(store.SetIssueIgnored("pair-a", original, false) &&
                    store.SetIssueIgnored("pair-a", original, false), "restore is idempotent");
                store.SetIssueIgnored("pair-a", original, true);
                Raise(store, "pair-a", IssueKind.Failure, TextKey.FailDropboxNameRejected, "a.txt");
                Check.Require(!store.IssueById("pair-a", previous)!.Ignored, "changed cause restores visibility");
                original = store.IssueById("pair-a", previous)!;
                store.SetIssueIgnored("pair-a", original, true);
                store.Commit("pair-a", StateChange.Empty with { Close = [("a.txt", [IssueKind.Failure])] });
                Check.Require(!store.SetIssueIgnored("pair-a", original, false), "closed notice cannot resurrect");
                Raise(store, "pair-a", IssueKind.Failure, TextKey.FailChanged, "b.txt");
                var next = store.Issues("pair-a").Single();
                Check.Require(next.Id > previous && !next.Ignored, "new episode gets fresh active identity");
                previous = next.Id;
                previousIssue = next;
                store.Commit("pair-a", StateChange.Empty with { Close = [("b.txt", [IssueKind.Failure])] });
            }
            using var reopened = StateStore.Open(path);
            using (var connection = StateStore.Connect(path, SqliteOpenMode.ReadWrite))
            using (var transaction = connection.BeginTransaction())
            {
                IssueTable.Raise(connection, transaction, "pair-a", IssueKind.Info, TextKey.FailChanged, "same", Subject(), Now);
                using var maximum = StorageCommand.Create(connection, transaction, "SELECT max(issue_id) FROM issue;");
                var first = (long)maximum.ExecuteScalar()!;
                IssueTable.Close(connection, transaction, "pair-a", "same", [IssueKind.Info]);
                IssueTable.Raise(connection, transaction, "pair-a", IssueKind.Info, TextKey.FailChanged, "same", Subject(), Now);
                Check.Require((long)maximum.ExecuteScalar()! > first, "same transaction never reuses a deleted id");
                IssueTable.Close(connection, transaction, "pair-a", "same", [IssueKind.Info]);
                transaction.Commit();
            }
            Raise(reopened, "pair-a", IssueKind.Failure, TextKey.FailChanged, "c.txt");
            Check.Require(reopened.Issues("pair-a").Single().Id > previous &&
                !reopened.SetIssueIgnored("pair-a", previousIssue, true), "restart retains the high-water mark");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        StateStore.OpenConnection(connection);
        Execute(connection, null, StateSchema.Pragmas);
        Execute(connection, null, StateSchema.Create);
        Execute(connection, null, StateSchema.SchemaAdditions);
        Execute(connection, null, StateSchema.DeletedHistorySchema + StateSchema.RebuildIssues + StateSchema.DeletedHistoryBlocks + StateSchema.DropDeletedHistoryNames + StateSchema.SpentDeletedHistory + StateSchema.LocalPublishedColumns + StateSchema.DiscoveryRequiredColumn + StateSchema.IssuePathKeyIndex + StateSchema.RenameContinuationColumns);
        Execute(connection, null, StateSchema.AttachScan, (StateSchema.ParamPath, StateSchema.EmptyScan));
        Execute(connection, null, StateSchema.CreateSkipTouched);
        return connection;
    }

    private static long PairRow(SqliteConnection connection, SqliteTransaction transaction, string pairId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = StateSchema.PairFence;
        command.Parameters.AddWithValue(StateSchema.ParamPair, pairId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void AddPair(SqliteConnection connection, string pairId) =>
        Execute(connection, null, StateSchema.UpsertPair,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamDef, "{}"));

    private static void Raise(SqliteConnection connection, string pairId, IssueKind kind, TextKey cause, string path,
        DateTimeOffset now, params IssueItem[] items) =>
        RaiseSubject(connection, pairId, kind, cause, path, now, Subject(items));

    private static void RaiseSubject(SqliteConnection connection, string pairId, IssueKind kind, TextKey cause, string path,
        DateTimeOffset now, IssueSubject subject)
    {
        using var transaction = connection.BeginTransaction();
        IssueTable.Raise(connection, transaction, pairId, kind, cause, path, subject, now);
        transaction.Commit();
    }

    private static void Close(SqliteConnection connection, string pairId, string path, params IssueKind[] kinds)
    {
        using var transaction = connection.BeginTransaction();
        IssueTable.Close(connection, transaction, pairId, path, kinds);
        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] arguments)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static IssueSubject Subject(params IssueItem[] items) => new(items, null);

    private static IssueItem Local(string path) => new(Side.Local, path, path);

    private static IssueItem Dropbox(string path) => new(Side.Dropbox, path, path);
}
