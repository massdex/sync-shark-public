using System.Text.Json;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Maintained issue answers match independent durable reads after mutations and failed publication.</summary>
public static class IssueFactsChecks
{
    private static string _database = string.Empty;

    public static void Run()
    {
        Counts();
        InMemoryFacts();
        UnknownMoveCases();
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var path = Path.Combine(directory, "state.db");
        try
        {
            using var store = StateStore.Open(path, static (_, _, _, _, _) => true);
            _database = path;
            store.SavePair(new PairConfig("pair", "Pair", "X:/Parent", "/Parent", "account", [], [],
                FirstRunPolicy.NewestWins, true, null));
            AssertFacts(store);
            var subject = new IssueSubject([new IssueItem(Side.Local, "Parent", "item")], null,
                FirstRunDeletion: IssueSubject.FirstRunDeletionKind.UnscannedSubtree);
            Raise(IssueKind.Failure, TextKey.FailDropboxNameRejected, subject);
            var hidden = store.Issues("pair").Single();
            var heldBefore = store.RetainedFirstRun("pair");
            store.SetIssueIgnored("pair", hidden, true);
            Check.Require(store.RetainedFirstRun("pair") == heldBefore && store.HeldCovers("pair", PathNames.Key("Parent/Child"), null),
                "Ignore preserves held and first-run responsibility projections");
            store.SetIssueIgnored("pair", hidden, false);
            AssertFacts(store);
            var callbackFailure = new IOException("committed callback");
            try
            {
                store.Commit("pair", StateChange.Empty with { Raise =
                    [(IssueKind.Tie, TextKey.FailChanged, "Other", new IssueSubject([], null))] }, _ =>
                {
                    AssertFacts(store);
                    throw callbackFailure;
                });
                throw new InvalidOperationException("callback failure was not exercised");
            }
            catch (IOException error) when (ReferenceEquals(error, callbackFailure)) { AssertFacts(store); }
            store.Commit("pair", StateChange.Empty with { Close = [("Other", IssueClosure.AfterHeldInSync)] });
            AssertFacts(store);
            var snapshot = store.RetainedFirstRun("pair");
            store.Commit("pair", StateChange.Empty with { Close = [("Parent", IssueClosure.AfterHeldInSync)] });
            AssertFacts(store);
            Check.Require(store.Issues("pair").Count == 1, "first-run work refuses generic closure");
            store.Commit("pair", StateChange.Empty with { FirstRunAcknowledged = subject.Items });
            AssertFacts(store);
            Check.Require(snapshot.Count == 1 && store.RetainedFirstRun("pair").Count == 0,
                "a retained snapshot stays immutable after acknowledgement");
            Raise(IssueKind.Failure, TextKey.FailDropboxNoPermission, new IssueSubject([], null));
            store.DismissIssue("pair", store.Issues("pair").Single().Id);
            AssertFacts(store);
            Raise(IssueKind.Tie, TextKey.FailChanged, subject);
            Raise(IssueKind.NameClash, TextKey.ClashMappedNames, subject);
            var failure = store.Issues("pair").First(issue => issue.Kind == IssueKind.Failure);
            AssertFacts(store, new HashSet<long> { failure.Id });
            store.ForgetAllSynced("pair");
            AssertFacts(store);
            store.Commit("pair", StateChange.Empty with { Close = [("Parent", IssueClosure.AfterHeldInSync)] });
            AssertFacts(store);

            using var inspect = StateStore.Connect(path, SqliteOpenMode.ReadWrite);
            Execute(inspect, "CREATE TRIGGER refuse_issue AFTER INSERT ON issue WHEN NEW.path='Fail' " +
                "BEGIN SELECT RAISE(ABORT,'refuse'); END;");
            var rolledBackCallbacks = 0;
            try
            {
                store.Commit("pair", StateChange.Empty with { Raise =
                    [(IssueKind.Failure, TextKey.FailDropboxNameRejected, "Parent", subject),
                     (IssueKind.Tie, TextKey.FailChanged, "Fail", subject)] }, _ => rolledBackCallbacks++);
                throw new InvalidOperationException("rollback was not exercised");
            }
            catch (SqliteException)
            {
                Check.Require(rolledBackCallbacks == 0, "rolled-back issues have no committed callback");
                AssertFacts(store);
            }
            Execute(inspect, "DROP TRIGGER refuse_issue;");

            Raise(IssueKind.Failure, TextKey.FailDropboxNameRejected, subject);
            Execute(inspect, "CREATE TRIGGER corrupt_subject AFTER UPDATE OF subject_json ON issue " +
                "BEGIN UPDATE issue SET subject_json='invalid' WHERE issue_id=NEW.issue_id; END;");
            try
            {
                store.Commit("pair", StateChange.Empty with { FirstRunAcknowledged = subject.Items });
                throw new InvalidOperationException("publication failure was not exercised");
            }
            catch (JsonException) { }
            Execute(inspect, "DROP TRIGGER corrupt_subject;");
            using (var repair = inspect.CreateCommand())
            {
                repair.CommandText = "UPDATE issue SET subject_json=$subject;";
                repair.Parameters.AddWithValue("$subject", JsonSerializer.Serialize(subject with
                    { FirstRunDeletion = IssueSubject.FirstRunDeletionKind.None }));
                repair.ExecuteNonQuery();
            }
            AssertFacts(store);
            store.RemovePair("pair");
            AssertFacts(store);
            store.SavePair(new PairConfig("pair", "Pair", "X:/Parent", "/Parent", "account", [], [],
                FirstRunPolicy.NewestWins, true, null));
            AssertFacts(store);
            Raise(IssueKind.Tie, TextKey.FailChanged, subject);
            var saved = new SyncedEntry(PathNames.Key("Parent"), "Parent", PathNames.DropboxKey("Parent"),
                "Parent", false, "local", 0, DateTimeOffset.UnixEpoch.UtcTicks, "remote",
                new string('0', StateSchema.HashHexLength));
            store.CommitRecordAsync("pair", StateChange.Empty with
                { Record = [saved], Close = [("Parent", IssueClosure.AfterHeldInSync)] }, null).GetAwaiter().GetResult();
            AssertFacts(store);

            var pair = store.Pair("pair")!;
            var bound = pair with { Roots = new RootBinding("X:/Parent", "/Parent", "account",
                new DirectoryIdentity(1, 0, null), new DropboxRootIdentity("account", "namespace", "namespace", "remote")) };
            store.SavePair(bound);
            store.Commit("pair", StateChange.Empty with { Raise =
                [(IssueKind.Tie, TextKey.FailChanged, "Exact",
                    new IssueSubject([new IssueItem(Side.Local, "Exact", "exact")], null))] });
            AssertFacts(store);
            store.SavePairDecision(bound, store.ReadPairExecution("pair")!.Value.Generation, bound,
                store.Issues("pair").Single(issue => issue.Path == "Exact"), true, null);
            AssertFacts(store);
            var generation = store.ReadPairExecution("pair")!.Value.Generation;
            var epoch = store.ReadSource("pair", Side.Local)!.Value.Epoch;
            var attempt = Guid.NewGuid();
            var retry = store.RecordInUseFailure("pair", generation, epoch, "Parent",
                subject with { FirstRunDeletion = IssueSubject.FirstRunDeletionKind.None },
                TextKey.FailLocalInUse, null, ItemRetryDispatch.LocalBranch, null, null, attempt);
            AssertFacts(store);
            store.CompleteItemAttempt(retry, attempt, StateChange.Empty with { Raise =
                [(IssueKind.Failure, TextKey.FailLocalInUseOrAccessDenied, "Parent", retry.Token.Subject)] }, null, null);
            AssertFacts(store);
            var currentRetry = store.ReadItemRetryState("pair", [retry.Token.IssueId])[retry.Token.IssueId]!;
            store.CompleteItemAttempt(currentRetry, attempt, StateChange.Empty with
                { Close = [("Parent", IssueClosure.AfterEffect)] }, null, null);
            AssertFacts(store);
            InvalidJob(store).GetAwaiter().GetResult();
            foreach (var fail in new[] { false, true })
                Grouped(Path.Combine(directory, "group-" + fail + ".db"), fail).GetAwaiter().GetResult();

            void Raise(IssueKind kind, TextKey cause, IssueSubject value)
            {
                var callback = false;
                store.Commit("pair", StateChange.Empty with { Raise = [(kind, cause, "Parent", value)] }, _ =>
                {
                    AssertFacts(store);
                    callback = true;
                });
                Check.Require(callback, "committed callback observes published facts");
                AssertFacts(store);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void InMemoryFacts()
    {
        using var store = new InMemoryStateStore();
        store.SavePair(new PairConfig("pair", "Pair", "X:/Parent", "/Parent", "account", [], [],
            FirstRunPolicy.NewestWins, true, null));
        var subject = new IssueSubject([new IssueItem(Side.Local, "Parent", "item")], null,
            FirstRunDeletion: IssueSubject.FirstRunDeletionKind.UnscannedSubtree);
        var row = new Row(PathNames.Key("Parent/Child"), null, null, null, [], null, RowContext.Neutral);
        Check.Require(store.HeldKind("pair", row, null) is null && store.HoldingIssue("pair", row) is null &&
            store.RetainedFirstRun("pair").Count == 0, "empty in-memory facts have no hold or obligation");
        store.Commit("pair", StateChange.Empty with { Raise =
            [(IssueKind.Failure, TextKey.FailDropboxNoPermission, "Parent", subject)] });
        var issue = store.Issues("pair").Single();
        var retained = store.RetainedFirstRun("pair");
        Check.Require(store.HeldKind("pair", row, null) == IssueKind.Failure &&
            store.HeldCovers("pair", row.Key, null) && store.HoldingIssue("pair", row) == issue &&
            retained[PathNames.Key("Parent")] == subject, "in-memory facts expose committed ancestors");
        Check.Require(store.HeldKind("pair", row, new HashSet<long> { issue.Id }) is null &&
            !store.HeldCovers("pair", row.Key, new HashSet<long> { issue.Id }),
            "selected Failure retries omit only that hold");
        store.DismissIssue("pair", issue.Id);
        Check.Require(store.HoldingIssue("pair", row)?.Dismissed == true &&
            store.HeldKind("pair", row, null) == IssueKind.Failure, "dismissal preserves the exact current hold");
        store.Commit("pair", StateChange.Empty with { FirstRunAcknowledged = subject.Items,
            Close = [("Parent", IssueClosure.AfterHeldInSync)] });
        Check.Require(retained.Count == 1 && store.RetainedFirstRun("pair").Count == 0 &&
            store.HoldingIssue("pair", row) is null, "closure changes fresh answers, not retained immutable views");
    }

    // Unknown-move holds (BRIEF-UNKNOWN-MOVE-IMPL U3, U3-case, U3b, U3e', K14-2, K14-3, R3) on both stores. The double
    // cannot open folder plans, so its R3 plan is captured by a real store, as StoredFolderPlanChecks does.
    private static void UnknownMoveCases()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            _database = Path.Combine(directory, "unknown.db");
            using (var real = StateStore.Open(_database)) UnknownMoveHolds(real);
            using (var real = StateStore.Open(Path.Combine(directory, "debt.db"))) UnknownMoveDebt(real);
            using (var real = StateStore.Open(Path.Combine(directory, "job.db")))
                UnknownMoveInvalidJob(real, real).GetAwaiter().GetResult();
            using (var capture = StateStore.Open(Path.Combine(directory, "capture.db")))
            using (var memory = new InMemoryStateStore())
            {
                SaveUnknownMovePair(capture);
                UnknownMoveInvalidJob(memory, capture).GetAwaiter().GetResult();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        using (var memory = new InMemoryStateStore()) UnknownMoveHolds(memory);
        using (var memory = new InMemoryStateStore()) UnknownMoveDebt(memory);
    }

    private static Row At(string path) => new(PathNames.Key(path), null, null, null, [], null, RowContext.Neutral);

    /// <summary>B-164: a late-recycled warning is held until the user dismisses it, on both stores; only a new raise of
    /// that cause reopens a dismissed one, and it takes precedence over a held row but not over an unknown move.</summary>
    public static void RunRecycledLateHolds()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            using (var real = StateStore.Open(Path.Combine(directory, "late.db"), static (_, _, _, _, _) => true))
                RecycledLateHolds(real);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        using var memory = new InMemoryStateStore();
        RecycledLateHolds(memory);
    }

    private static void RecycledLateHolds(IStateStore store)
    {
        var bound = new PairConfig("pair", "Pair", "X:/Parent", "/Parent", "account", [], [], FirstRunPolicy.NewestWins,
            true, null, new RootBinding("X:/Parent", "/Parent", "account", new DirectoryIdentity(1, 0, null),
                new DropboxRootIdentity("account", "namespace", "namespace", "remote")));
        store.SavePair(bound);
        var name = Named(store);
        IssueSubject Late(string path, string id, IssueSubject.FirstRunDeletionKind firstRun =
            IssueSubject.FirstRunDeletionKind.None) => new([new IssueItem(Side.Local, path, id)], null, FirstRunDeletion: firstRun);
        (Issue Issue, bool IsNew) Raise(string path, TextKey cause, IssueSubject subject)
        {
            (Issue, bool)? settled = null;
            store.Commit("pair", StateChange.Empty with { Raise = [(IssueKind.Failure, cause, path, subject)] }, settledIssue:
                (issue, isNew) => settled = (issue, isNew));
            return settled ?? throw new InvalidOperationException("the raise settled no issue at " + path);
        }
        void Close(string path, IReadOnlyList<IssueKind> kinds) =>
            store.Commit("pair", StateChange.Empty with { Close = [(path, kinds)] });
        Issue? At(string path) => store.Issues("pair").SingleOrDefault(issue => issue.Path == path);
        SyncedEntry Binding(string path, string id) => new(PathNames.Key(path), path, PathNames.DropboxKey(path), path,
            false, id, 1, 0, "remote-" + id, new string('0', StateSchema.HashHexLength));

        // (1) R2-1's interrupted case: an old binding is still saved at the path.
        store.Commit("pair", StateChange.Empty with { Record = [Binding("P", "old")] });
        var held = Raise("P", TextKey.FailRecycledLate, Late("P", "late")).Issue;
        Close("P", IssueClosure.AfterEffect);
        Close("P", IssueClosure.AfterInSync);
        Raise("P", TextKey.FailChanged, Late("P", "late"));
        Check.Require(At("P") is { Cause: TextKey.FailRecycledLate } kept && kept.Id == held.Id &&
            store.RetryNoticeProtected("pair", held.Id),
            "(1) closure and another cause leave a late-recycled warning held" + name);
        // (2) A different file synced at the path does not count as recovery.
        store.Commit("pair", StateChange.Empty with { Record = [Binding("P", "other")] });
        Close("P", IssueClosure.AfterInSync);
        Check.Require(At("P")?.Id == held.Id, "(2) a rebound path does not release the warning" + name);
        // (3) Only dismissal ends it; the next close then removes the row.
        store.DismissIssue("pair", held.Id);
        Close("P", IssueClosure.AfterInSync);
        Check.Require(At("P") is null, "(3) a dismissed warning is removed by the next close" + name);

        // (4) Precedence: it replaces a held in-use row, but an unknown move is kept.
        var execution = store.ReadPairExecution("pair")!.Value;
        var epoch = store.ReadSource("pair", Side.Local)?.Epoch ?? 0;
        var inUse = store.RecordInUseFailure("pair", execution.Generation, epoch, "Q", Late("Q", "busy"),
            TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null, Guid.NewGuid());
        Check.Require(store.RetryNoticeProtected("pair", inUse.Token.IssueId), "(4) fixture: the in-use row is held" + name);
        Raise("Q", TextKey.FailRecycledLate, Late("Q", "late"));
        Check.Require(At("Q")?.Cause == TextKey.FailRecycledLate, "(4) a late-recycled raise replaces a held in-use row" + name);
        var moved = new IssueSubject([new IssueItem(Side.Local, "R", "moved")], null)
            { Move = new IssueSubject.UnknownMove("moved", "R", "R-moved", null, false) };
        Raise("R", TextKey.FailChanged, moved);
        Raise("R", TextKey.FailRecycledLate, Late("R", "late"));
        Check.Require(At("R") is { Cause: TextKey.FailChanged } unknown && unknown.Subject.Move is not null,
            "(4) an unknown-move row is kept" + name);

        // (5a)/(10) A new raise of the same item reopens a dismissed warning as the same row.
        foreach (var path in new[] { "S", "Z" })
        {
            var first = Raise(path, TextKey.FailRecycledLate, Late(path, "x")).Issue;
            store.DismissIssue("pair", first.Id);
            var again = Raise(path, TextKey.FailRecycledLate, Late(path, "x"));
            Check.Require(again.Issue.Id == first.Id && !again.Issue.Dismissed && again.IsNew &&
                At(path) is { Dismissed: false }, "(5a)/(10) a repeat incident reopens the dismissed warning" + name);
        }
        // (5b) A raise naming a different item reopens it with that item.
        var named = Raise("T", TextKey.FailRecycledLate, Late("T", "x")).Issue;
        store.DismissIssue("pair", named.Id);
        Raise("T", TextKey.FailRecycledLate, Late("T", "y"));
        Check.Require(At("T") is { Dismissed: false } renamed && renamed.Subject.Items.Single().Id == "y",
            "(5b) a repeat incident for another item reopens it with that item" + name);
        // (5c) Other causes keep today's continuation: a dismissed row stays dismissed.
        var other = Raise("U", TextKey.FailChanged, Late("U", "x")).Issue;
        store.DismissIssue("pair", other.Id);
        Raise("U", TextKey.FailChanged, Late("U", "x"));
        Check.Require(At("U") is { Dismissed: true }, "(5c) a dismissed row of another cause stays dismissed" + name);

        // (6) Dismissal hides the warning but does not erase its first-run obligation.
        var firstRun = Late("V", "x", IssueSubject.FirstRunDeletionKind.Item);
        var owed = Raise("V", TextKey.FailRecycledLate, firstRun).Issue;
        store.DismissIssue("pair", owed.Id);
        Close("V", IssueClosure.AfterEffect);
        Check.Require(At("V") is not null, "(6) a dismissed warning with first-run work stays as a hidden row" + name);
        store.Commit("pair", StateChange.Empty with { FirstRunAcknowledged = firstRun.Items });
        Close("V", IssueClosure.AfterEffect);
        Check.Require(At("V") is null, "(6) after acknowledgement the next close removes it" + name);

        // (7)/(8) An unrelated raise for the same item never revives a dismissed warning or adopts its cause.
        foreach (var (path, cause) in new[] { ("W", TextKey.SkipUnreadable), ("X2", TextKey.FailFirstSyncCheckWaiting) })
        {
            var closed = Raise(path, TextKey.FailRecycledLate, Late(path, "x")).Issue;
            store.DismissIssue("pair", closed.Id);
            var unrelated = Raise(path, cause, Late(path, "x"));
            Check.Require(!store.Issues("pair").Any(issue => issue.Path == path &&
                    issue is { Cause: TextKey.FailRecycledLate, Dismissed: false }) &&
                At(path)?.Cause == cause && unrelated.IsNew,
                "(7)/(8) " + cause + " keeps its own cause beside a dismissed warning" + name);
        }
        // (9) An undismissed warning still lends its cause and holds against a wider subject.
        var open = Raise("Y", TextKey.FailRecycledLate, Late("Y", "x")).Issue;
        Raise("Y", TextKey.SkipUnreadable, new IssueSubject(
            [new IssueItem(Side.Local, "Y", "x"), new IssueItem(Side.Local, "Y/other", "y")], null));
        Check.Require(At("Y") is { Cause: TextKey.FailRecycledLate } lent && lent.Subject.Items.Single().Id == "x" &&
            store.RetryNoticeProtected("pair", open.Id),
            "(9) an open warning keeps its cause, subject and hold" + name);
    }

    private static string Named(IStateStore store) => store is StateStore ? " real" : " in-memory";

    private static void SaveUnknownMovePair(IStateStore store) =>
        store.SavePair(new PairConfig("pair", "Pair", "X:/Parent", "/Parent", "account", [], [],
            FirstRunPolicy.NewestWins, true, null));

    private static void UnknownMoveHolds(IStateStore store)
    {
        SaveUnknownMovePair(store);
        // Read first so the real store's projection is loaded; the Raise below then reaches it only through the trigger.
        Check.Require(store.HeldKind("pair", At("Pair/Destination/child"), null) is null,
            "U3e' nothing is held before the unknown move is raised" + Named(store));
        var subject = new IssueSubject([new IssueItem(Side.Local, "Pair/Item", "item")], null)
            { Move = new IssueSubject.UnknownMove("moved", "Pair/Source", "Pair/Destination", "Pair/Middle", false) };
        store.Commit("pair", StateChange.Empty with { Raise =
            [(IssueKind.Failure, TextKey.FailChanged, "Pair/Path", subject),
             (IssueKind.Failure, TextKey.FailDropboxNameRejected, "Ordinary",
                new IssueSubject([new IssueItem(Side.Local, "Ordinary", "ordinary")], null))] });
        var unknown = store.Issues("pair").Single(issue => issue.Path == "Pair/Path");
        var ordinary = store.Issues("pair").Single(issue => issue.Path == "Ordinary");
        foreach (var edge in new[] { "Pair/Source", "Pair/Destination", "Pair/Middle", "Pair/Path", "Pair/Item" })
            Check.Require(store.HeldKind("pair", At(edge), null) == IssueKind.Failure &&
                store.HeldKind("pair", At(edge + "/child"), null) == IssueKind.Failure,
                "U3 an unknown move holds its footprint edge " + edge + Named(store));
        Check.Require(store.HeldKind("pair", At("Pair/destination/child"), null) == IssueKind.Failure,
            "U3-case a case-variant destination child is held by key" + Named(store));
        Check.Require(store.HeldKind("pair", At("Pair"), null) is null && store.HeldKind("pair", At("Pair/Other"), null) is null,
            "U3 an unknown move holds only its footprint" + Named(store));
        var retry = new HashSet<long> { unknown.Id, ordinary.Id };
        var destination = At("Pair/Destination");
        Check.Require(store.HeldKind("pair", destination, retry) == IssueKind.Failure &&
            store.HeldCovers("pair", destination.Key, retry), "U3b-retry an explicit Retry does not exempt an unknown move" + Named(store));
        Check.Require(store.HeldByOtherIssue("pair", destination, unknown.Id),
            "U3b-sel selecting an unknown move does not exempt it" + Named(store));
        Check.Require(store.HeldKind("pair", At("Ordinary"), retry) is null && !store.HeldByOtherIssue("pair", At("Ordinary"), ordinary.Id),
            "an ordinary retried or selected Failure stays exempt" + Named(store));
        // X1: the expected identity is held wherever it is observed, and nothing else there is.
        var observed = Observed("Elsewhere/New", "moved");
        Check.Require(store.HeldKind("pair", observed, retry) == IssueKind.Failure &&
            store.HoldingIssue("pair", observed)?.Id == unknown.Id && store.HeldByOtherIssue("pair", observed, ordinary.Id) &&
            store.HeldKind("pair", Observed("Elsewhere/New", "other"), null) is null &&
            store.HeldKind("pair", At("Elsewhere"), null) is null,
            "an unknown move holds its expected identity at any observed path" + Named(store));
        if (store is StateStore real)
        {
            // The comparison read skips every selected Failure before decoding it, so parity excludes the unknown move.
            AssertFacts(real, new HashSet<long> { ordinary.Id });
            using var authority = StateStore.Connect(_database, SqliteOpenMode.ReadOnly);
            Check.Require(!IssueTable.HeldKinds(authority, "pair", retry).ContainsKey(destination.Key),
                "IssueTable.HeldKinds, the comparison read, omits a selected unknown move before decoding it");
        }
        store.Commit("pair", StateChange.Empty with { Close = [("Pair/Path", [IssueKind.Failure])] });
        Check.Require(store.HeldKind("pair", observed, null) is null && store.HoldingIssue("pair", observed) is null &&
            store.HeldKind("pair", At("Pair/Destination"), null) is null,
            "closing the unknown move releases its identity and its path footprint" + Named(store));
    }

    private static Row Observed(string path, string id) => Row.ForOneSide(Side.Dropbox, PathNames.DropboxKey(path),
        new Entry(path, false, 1, DateTimeOffset.UnixEpoch, id, "revision", "hash", null), null, [], null,
        RowContext.Neutral);

    private static void UnknownMoveDebt(IStateStore store)
    {
        SaveUnknownMovePair(store);
        var local = new IssueItem(Side.Local, "Folder", "folder");
        var child = At("Folder/child");
        store.Commit("pair", StateChange.Empty with { Raise = [(IssueKind.Failure, TextKey.FailChanged, "Folder",
            new IssueSubject([local], null, FirstRunDeletion: IssueSubject.FirstRunDeletionKind.UnscannedSubtree))] });
        var moved = new IssueSubject([local, new IssueItem(Side.Dropbox, "Folder", "remote")], null)
            { Move = new IssueSubject.UnknownMove("remote", "Folder", "Moved", null, true) };
        store.Commit("pair", StateChange.Empty with { Raise = [(IssueKind.Failure, TextKey.FailChanged, "Folder", moved)] });
        var stored = store.Issues("pair").Single();
        Check.Require(stored.Subject.Move == moved.Move &&
            stored.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.UnscannedSubtree &&
            store.HeldKind("pair", child, null) is null, "K14-2 carried-forward first-run debt keeps a move unheld" + Named(store));
        store.Commit("pair", StateChange.Empty with { FirstRunAcknowledged = [local] });
        stored = store.Issues("pair").Single();
        Check.Require(stored.Subject.Move == moved.Move &&
            stored.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.None &&
            store.HeldKind("pair", child, null) == IssueKind.Failure,
            "K14-3 an acknowledgement-only commit makes the retained move hold" + Named(store));
    }

    private static async Task UnknownMoveInvalidJob(IStateStore store, IStateStore capture)
    {
        SaveUnknownMovePair(store);
        var (plan, accepted) = await AcceptedEmptyFolder(store, capture, static () => { });
        using var removal = plan;
        var subject = new IssueSubject([new IssueItem(Side.Dropbox, "Folder", "folder")], null)
            { Move = new IssueSubject.UnknownMove("folder", "Folder", "Moved", null, true) };
        store.Commit("pair", StateChange.Empty with { Raise = [(IssueKind.Failure, TextKey.FailChanged, "Folder", subject)] });
        var unknown = store.Issues("pair").Single();
        store.RecordFolderJobObservation(accepted, null, TextKey.FailDropboxDeleteJobInvalid);
        Check.Require(store.Issues("pair").Single().Matches(unknown),
            "R3 an invalid job at the same root keeps the unknown move" + Named(store));
        store.Commit("pair", StateChange.Empty with { Close = [("Folder", [IssueKind.Failure])] });
        Check.Require(store.Issues("pair").Single() is { Cause: TextKey.FailDropboxDeleteJobInvalid } projected &&
            projected.Subject.Move is null && projected.Id != unknown.Id,
            "R3 the invalid-job projection appears once the unknown move closes" + Named(store));
    }

    private static async Task Grouped(string path, bool failPublication)
    {
        using var store = StateStore.Open(path);
        _database = path;
        store.SavePair(new PairConfig("pair", "Pair", "X:/Parent", "/Parent", "account", [], [],
            FirstRunPolicy.NewestWins, true, null));
        store.SaveLimiterLevel("count", 0);
        var subjects = new[] { "Parent/One", "Parent/Two" }.Select(name =>
            new IssueSubject([new IssueItem(Side.Local, name, name)], null,
                FirstRunDeletion: IssueSubject.FirstRunDeletionKind.Item)).ToArray();
        foreach (var subject in subjects)
        {
            store.Commit("pair", StateChange.Empty with { Raise =
                [(IssueKind.Failure, TextKey.FailDropboxNameRejected, subject.Items[0].Path, subject)] });
            AssertFacts(store);
        }
        using var inspect = StateStore.Connect(path, SqliteOpenMode.ReadWrite);
        Execute(inspect, "CREATE TRIGGER count_records AFTER INSERT ON synced_entry BEGIN " +
            "UPDATE account SET limiter_level=limiter_level+1 WHERE connection_id='count'; END;");
        if (failPublication)
            Execute(inspect, "CREATE TRIGGER corrupt_group AFTER UPDATE OF subject_json ON issue " +
                "BEGIN UPDATE issue SET subject_json='invalid' WHERE issue_id=NEW.issue_id; END;");
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enumerations = 0;
        var first = Task.Run(() => store.CommitRecordAsync("pair", StateChange.Empty with
        {
            Record = new StateStoreChecks.ObservedList<SyncedEntry>([Saved("Barrier")], () =>
            {
                if (Interlocked.Increment(ref enumerations) != 2) return;
                entered.TrySetResult();
                release.Wait();
            })
        }, null));
        var queued = new List<Task>();
        try
        {
            await Task.WhenAny(entered.Task, first);
            Check.Require(entered.Task.IsCompletedSuccessfully, "first application holds the queue barrier");
            foreach (var subject in subjects)
            {
                var pending = store.CommitRecordAsync("pair", StateChange.Empty with
                {
                    Record = [Saved(subject.Items[0].Path)], FirstRunAcknowledged = subject.Items
                }, null);
                Check.Require(!pending.IsCompleted, "both members are queued in one group");
                queued.Add(pending);
            }
        }
        finally { release.Set(); }
        await first;
        try { await Task.WhenAll(queued); }
        catch (JsonException) when (failPublication) { }
        Check.Require(queued.All(task => failPublication ? task.IsFaulted : task.IsCompletedSuccessfully),
            "grouped publication result reaches every committed member without replay");
        Check.Require(store.LimiterLevel("count") == subjects.Length + 1,
            "each grouped record is written once even when post-commit publication fails");
        if (failPublication)
        {
            Execute(inspect, "DROP TRIGGER corrupt_group;");
            foreach (var subject in subjects)
            {
                using var repair = inspect.CreateCommand();
                repair.CommandText = "UPDATE issue SET subject_json=$subject WHERE path=$path;";
                repair.Parameters.AddWithValue("$path", subject.Items[0].Path);
                repair.Parameters.AddWithValue("$subject", JsonSerializer.Serialize(subject with
                    { FirstRunDeletion = IssueSubject.FirstRunDeletionKind.None }));
                repair.ExecuteNonQuery();
            }
        }
        AssertFacts(store);

        static SyncedEntry Saved(string name) => new(PathNames.Key(name), name, PathNames.DropboxKey(name), name,
            false, name, 0, DateTimeOffset.UnixEpoch.UtcTicks, name, new string('0', StateSchema.HashHexLength));
    }

    private static void Counts()
    {
        using var probe = new CostProbe(":memory:");
        probe.CreateSchema();
        probe.Execute(StateSchema.UpsertPair, (StateSchema.ParamPair, "pair"), (StateSchema.ParamDef, "{}"));
        var ordinary = probe.Execute(StateSchema.UpsertLimiterLevel,
            (StateSchema.ParamConnection, "account"), (StateSchema.ParamLevel, 1));
        Check.Require(ordinary.ProbePrepares == 1 && ordinary.IssueFactCalls == 0,
            "no-issue write adds zero statements and zero projection callbacks");
        foreach (var kind in Enum.GetValues<IssueKind>())
        {
            var cost = probe.Execute(StateSchema.InsertIssue, (StateSchema.ParamPair, "pair"),
                (StateSchema.ParamKind, (int)kind), (StateSchema.ParamPath, "Parent"),
                (StateSchema.ParamCause, nameof(TextKey.FailChanged)),
                (StateSchema.ParamSubject, JsonSerializer.Serialize(new IssueSubject([], null))),
                (StateSchema.ParamAt, DateTimeOffset.UnixEpoch.UtcTicks));
            var expected = IssuePolicy.HoldingKinds.Contains(kind) ? 1 : 0;
            Check.Require(cost.IssueFactCalls == expected && cost.ProbePrepares == 1,
                "one callback for projected kinds and no extra prepared statement");
            Console.WriteLine($"IssueFacts {kind}: VM={cost.VmSteps}, callbacks={cost.IssueFactCalls}, prepares={cost.ProbePrepares}");
        }
        var publication = probe.Execute(StateSchema.SelectIssueFact, (StateSchema.ParamId, 1));
        Check.Require(publication.ProbePrepares == 1 && publication.PathKeyCalls == 0,
            "publication is one primary-key read with no SQL path computation");
        Console.WriteLine($"IssueFacts publish: VM={publication.VmSteps}, prepares={publication.ProbePrepares}; no-issue additional statements=0");
    }

    private static async Task InvalidJob(StateStore store)
    {
        var (plan, accepted) = await AcceptedEmptyFolder(store, store, () => AssertFacts(store));
        using var removal = plan;
        store.RecordFolderJobObservation(accepted, null, TextKey.FailDropboxDeleteJobInvalid);
        AssertFacts(store);
        store.RecordFolderJobObservation(store.FolderSubmission(accepted.SubmissionId), new FolderJobResult.Terminal(
            [new FolderMemberResult.KnownNoEffect(0, null)]), null);
        AssertFacts(store);
    }

    /// <summary>Accepts the removal job of one empty Dropbox folder in store, with the plan captured by capture,
    /// running check once the removal begins and once the job is accepted. The caller owns the returned plan.</summary>
    private static async Task<(FolderPlan Plan, FolderSubmission Accepted)> AcceptedEmptyFolder(IStateStore store,
        IStateStore capture, Action check)
    {
        var entry = new Entry("Folder", true, 0, DateTimeOffset.UnixEpoch, "folder", "revision", "", null);
        var row = Row.ForOneSide(Side.Dropbox, PathNames.Key(entry.Path), entry, null, [], null,
            new RowContext(false, false, false, null, false, false, false, false));
        var plan = capture.OpenFolderPlan("pair", row, Side.Dropbox);
        try
        {
            await plan.CaptureAsync(Empty(), CancellationToken.None);
            Check.Require(!plan.Children(CancellationToken.None).Any(), "empty folder has no child classifications");
            plan.Complete(true, CancellationToken.None);
            var operation = store.BeginFolderRemoval("pair", plan, StateSchema.OneRow, CancellationToken.None);
            check();
            var id = store.ArmFolderSubmission("pair", store.ReadyFolderEffects("pair", operation, -1, 1).ToArray());
            var submitted = store.FolderSubmission(id);
            var accepted = submitted with { Generation = submitted.Generation + 1,
                Phase = FolderSubmission.SubmissionPhase.Accepted, JobId = "job" };
            store.AdvanceFolderSubmission(submitted, accepted, []);
            check();
            return (plan, accepted);
        }
        catch
        {
            plan.Dispose();
            throw;
        }

        static async IAsyncEnumerable<Listed> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static void AssertFacts(StateStore store, IReadOnlySet<long>? except = null)
    {
        using var authority = StateStore.Connect(_database, SqliteOpenMode.ReadOnly);
        var kinds = IssueTable.HeldKinds(authority, "pair", except);
        var prefixes = new PrefixSet();
        foreach (var key in kinds.Keys) prefixes.Add(key);
        var subjects = new Dictionary<string, IssueSubject>(StringComparer.Ordinal);
        foreach (var issue in store.Issues("pair")) IssuePolicy.AddFirstRunSubject(subjects, issue);
        var actual = store.RetainedFirstRun("pair");
        Check.Require(actual.Count == subjects.Count && subjects.All(pair =>
            actual.TryGetValue(pair.Key, out var subject) &&
            JsonSerializer.Serialize(subject) == JsonSerializer.Serialize(pair.Value)), "retained subjects equal fresh reads");
        var paths = new[] { "", "Parent", "Parent/Child", "Parent0", "Other", "Folder" }
            .Concat(store.Issues("pair").SelectMany(issue => IssuePolicy.HeldKeys(issue)))
            .Distinct(StringComparer.Ordinal).ToArray();
        var mapped = new SyncedEntry(PathNames.Key("Other"), "Other", PathNames.DropboxKey("Parent"), "Parent",
            false, "local", 0, DateTimeOffset.UnixEpoch.UtcTicks, "remote", new string('0', StateSchema.HashHexLength));
        foreach (var saved in new SyncedEntry?[] { null, mapped, mapped with
            { LocalKey = PathNames.Key("Parent"), LocalPath = "Parent", DropboxKey = PathNames.DropboxKey("Other"), DropboxPath = "Other" } })
        foreach (var path in paths.Concat(paths.Select(path => path + "/Child")))
        {
            var row = new Row(PathNames.Key(path), null, null, saved, [], null,
                new RowContext(false, false, false, null, false, false, false, false));
            Check.Require(store.HeldKind("pair", row, except) == IssuePolicy.HeldKind(row, prefixes, kinds),
                "held kind equals fresh read");
            Check.Require(store.HeldCovers("pair", row.Key, except) == prefixes.Covers(row.Key),
                "held coverage equals fresh read");
            var holder = store.HoldingIssue("pair", row);
            var expectedHolder = store.Issues("pair").FirstOrDefault(issue =>
            {
                if (!IssuePolicy.Holds(issue.Kind, issue.Cause, issue.Subject)) return false;
                var keys = new PrefixSet();
                foreach (var key in IssuePolicy.HeldKeys(issue)) keys.Add(key);
                return keys.Covers(row.Key) || row.Synced is { } linked &&
                    (keys.Covers(linked.LocalKey) || keys.Covers(linked.DropboxKey));
            });
            Check.Require(JsonSerializer.Serialize(holder) == JsonSerializer.Serialize(expectedHolder),
                "holding issue is the earliest exact current row, including dismissed state");
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
