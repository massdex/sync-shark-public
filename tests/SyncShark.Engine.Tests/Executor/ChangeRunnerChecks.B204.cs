using System.Reflection;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Executor;

// B-204: one Storage-certified, rolled-back completion refusal becomes that exact item's pending work plus one
// current Failure while healthy siblings finish; every unclassified store failure still escapes unchanged.
public static partial class ChangeRunnerChecks
{
    private const string FailingCompletion = "failing-completion";
    private const string HealthyCompletion = "healthy-completion";
    private const string PathCompletion = "path-completion";
    private const string OutOfScopeCompletion = "out-of-scope-completion";
    private const string RollbackCompletion = "rollback-completion";
    private const string FaultChild = "b204_child";
    private const string FaultParent = "b204_parent";
    private const string FaultTrigger = "b204_fk_fault";
    // A parent row that is never inserted: the trigger's child insert is a real foreign-key refusal.
    private const long MissingParent = 900;
    private const int ConstraintPrimary = 19;
    private const int ForeignKeyExtended = 787;

    public static void CompletionFailureContained() => CompletionFailureContainedAsync().GetAwaiter().GetResult();

    public static void CompletionFailureCost() => CompletionFailureCostAsync().GetAwaiter().GetResult();

    private static async Task CompletionFailureContainedAsync()
    {
        await CompletionFailureRealRefusalAsync();
        CompletionFailureRollback();
        await CompletionFailureSuppressionAsync();
        await CompletionFailureConcurrentAsync();
        await CompletionFailureRestartAsync();
        await CompletionFailureOutOfScopeAsync();
        await CompletionFailureNoticeWriteAsync();
        await CompletionFailureBoundariesAsync();
        await CompletionFailureChurnAsync();
    }

    // 9.1: a real 19/787 inside CompleteWork's transaction contains the failure and a healthy sibling still completes.
    private static async Task CompletionFailureRealRefusalAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var (failing, healthy) = SeedCompletionPair(fixture);
        InstallCompletionFkFault(fixture.DatabasePath!, failing.AnchorWorkId);
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        var certificate = events.Select(item => item.Error).OfType<ItemStoreMutationException>().ToArray();
        Check.Require(certificate.Length == 1 &&
            certificate[0].PrimaryCode == ConstraintPrimary && certificate[0].ExtendedCode == ForeignKeyExtended,
            "B204-01: the run reports one real 19/787 certified completion refusal");
        Check.Require(certificate[0].Selection.AnchorWorkId == failing.AnchorWorkId &&
            certificate[0].Selection.Key == PathNames.KeyFor(Side.Local, FailingCompletion),
            "B204-01: the certificate keeps the exact acquired selection");
        Check.Require(PendingAt(fixture, Side.Local, FailingCompletion).Count == 1 &&
            PendingAt(fixture, Side.Local, HealthyCompletion).Count == 0,
            "B204-01: the failed item stays pending and the healthy sibling's work retires");
        Check.Require((await fixture.Dropbox.ReadOneAsync(HealthyCompletion, default))?.ContentHash ==
            DropboxContentHash.Compute(new byte[] { 2 }),
            "B204-01: the healthy sibling's destination content is correct");
        var issues = fixture.Inner.Issues(PairId, [IssueKind.Failure]);
        Check.Require(issues.Count == 1 && issues[0].Cause == TextKey.FailStoreMutation &&
            issues[0].Path == PathNames.KeyFor(Side.Local, FailingCompletion),
            "B204-01: exactly one current Failure describes the failing item's saved-state update");
        Check.Require(fixture.Inner.ReadWork(failing).Single().WorkId == failing.AnchorWorkId,
            "B204-01: the acquired evidence stays durable after the rollback");
        Check.Require(!events.Any(item => item.Error is { } error && error is not ItemStoreMutationException &&
                error is not ItemFailedException),
            "B204-01: no unclassified failure accompanies the contained refusal");
    }

    // 9.1/4.3: the retirement's earlier clear of a rename continuation is restored by the explicit rollback.
    private static void CompletionFailureRollback()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        var saved = LocalEntry(RollbackCompletion, LocalId);
        var remote = RemoteEntry(RollbackCompletion, DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(saved, remote)] });
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, RollbackCompletion, null,
            saved.Id, saved with { Modified = Stamp.AddSeconds(1) }, false, false));
        var continuation = StateEncoding.Envelope(new RenameContinuation(SyncedEntries.From(saved, remote), null));
        ExecuteOnStore(fixture, "UPDATE change_work SET rename_continuation_json=$json WHERE work_id=$work;",
            ("$json", continuation), ("$work", selection.AnchorWorkId));
        InstallCompletionFkFault(fixture.DatabasePath!, selection.AnchorWorkId);
        ItemStoreMutationException? certified = null;
        try { fixture.Inner.CompleteWork(selection, null); }
        catch (ItemStoreMutationException error) { certified = error; }
        catch (Exception error) { throw new InvalidOperationException("B204-02: completion must not escape raw", error); }
        Check.Require(certified is { PrimaryCode: ConstraintPrimary, ExtendedCode: ForeignKeyExtended },
            "B204-02: the transaction owner certifies the 19/787 refusal after rolling back");
        Check.Require(ReadRenameContinuationText(fixture, selection.AnchorWorkId) == continuation,
            "B204-02: the earlier statement's cleared rename continuation is restored by the rollback");
        Check.Require(fixture.Inner.ReadWork(selection).Single().WorkId == selection.AnchorWorkId,
            "B204-02: the anchor row survives the rolled-back deletion");
        Check.Require((long)ExecuteScalarOnStore(fixture,
                "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name=$name;", ("$name", FaultTrigger))! == 1,
            "B204-02: fixture-only fault installation is a real trigger");
    }

    // 9.2: a healthy retirement justifies another pass, but the failed responsibility is attempted only once.
    private static async Task CompletionFailureSuppressionAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var (failing, _) = SeedCompletionPair(fixture);
        InstallCompletionFkFault(fixture.DatabasePath!, failing.AnchorWorkId);
        var attempts = new List<long>();
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            if (method.Name == nameof(IStateStore.CompleteWork)) lock (attempts) attempts.Add(((WorkSelection)args[0]!).AnchorWorkId);
        };
        var events = new List<RunEvent>();
        try
        {
            await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        }
        finally { fixture.Hooks.BeforeCallWith = null; }
        Check.Require(attempts.Count(id => id == failing.AnchorWorkId) == 1,
            "B204-03: within one run the failed responsibility reaches the faulting completion mutation once");
        Check.Require(attempts.Count > 1,
            "B204-03: the healthy sibling's retirement did justify another pass");
        Check.Require(PendingAt(fixture, Side.Local, FailingCompletion).Count == 1,
            "B204-03: the suppressed item stays pending for a real next opportunity");
        RemoveCompletionFkFault(fixture.DatabasePath!);
        await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        Check.Require(PendingAt(fixture, Side.Local, FailingCompletion).Count == 0 &&
            fixture.Inner.Issues(PairId).Count == 0,
            "B204-03: the next ordinary opportunity completes the pending work and closes its Failure");
    }

    // 9.3: the fault arrives while an admitted healthy lane is mid-upload; that lane is neither cancelled nor stalled.
    private static async Task CompletionFailureConcurrentAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var (failing, _) = SeedCompletionPair(fixture);
        InstallCompletionFkFault(fixture.DatabasePath!, failing.AnchorWorkId);
        var admission = new FakeAdmission { Level = 2 };
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.DropboxView = new ProxySide(fixture.Dropbox)
        {
            BeforeStage = async (path, token) =>
            {
                if (path != HealthyCompletion) return;
                held.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var events = new List<RunEvent>();
        var run = LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events, configure: context => context with
        {
            Admission = admission,
            Events = item =>
            {
                lock (events) events.Add(item);
                if (item.Error is ItemStoreMutationException) faulted.TrySetResult();
            }
        }).SyncWorkAsync(guard.Token);
        try
        {
            await held.Task.WaitAsync(guard.Token);
            await faulted.Task.WaitAsync(guard.Token);
            Check.Require(!run.IsCompleted,
                "B204-04: the contained refusal does not cancel or stall the admitted healthy lane");
            release.TrySetResult();
            await run.WaitAsync(guard.Token);
        }
        finally { release.TrySetResult(); }
        Check.Require((await fixture.Dropbox.ReadOneAsync(HealthyCompletion, default))?.ContentHash ==
            DropboxContentHash.Compute(new byte[] { 2 }),
            "B204-04: the healthy lane finishes with its correct destination content");
        Check.Require(PendingAt(fixture, Side.Local, FailingCompletion).Count == 1 &&
            fixture.Inner.Issues(PairId, [IssueKind.Failure]).Count == 1,
            "B204-04: only the failing lane stays pending with one Failure");
    }

    // 9.4: a restart preserves the pending work and one current Failure, and containment still holds.
    private static async Task CompletionFailureRestartAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var failing = SeedSingleCompletion(fixture, FailingCompletion, LocalId);
        InstallCompletionFkFault(fixture.DatabasePath!, failing.AnchorWorkId);
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        // The test-only tables would fail the store's exact schema validation on reopen; reinstall them after it.
        RemoveCompletionFkFault(fixture.DatabasePath!);
        fixture.RestartStore();
        InstallCompletionFkFault(fixture.DatabasePath!, failing.AnchorWorkId);
        await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        Check.Require(PendingAt(fixture, Side.Local, FailingCompletion).Count == 1,
            "B204-05: a restart keeps the uncertified work pending");
        Check.Require(fixture.Inner.Issues(PairId, [IssueKind.Failure]).Count == 1,
            "B204-05: a restart never stores a duplicate current Failure");
        Check.Require(events.Count(item => item.Error is ItemStoreMutationException) == 2,
            "B204-05: the reopened store contains the refusal again");
        RemoveCompletionFkFault(fixture.DatabasePath!);
        await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        Check.Require(PendingAt(fixture, Side.Local, FailingCompletion).Count == 0 &&
            fixture.Inner.Issues(PairId).Count == 0,
            "B204-05: without the fault the same pending work completes and the notice closes");
    }

    // 9.5: CompleteOutOfScope reaches the same completion owner and receives the same containment.
    private static async Task CompletionFailureOutOfScopeAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var failing = SeedSingleCompletion(fixture, OutOfScopeCompletion, LocalId);
        InstallCompletionFkFault(fixture.DatabasePath!, failing.AnchorWorkId);
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events, configure: context => context with
        {
            Scope = new PathScope(fixture.Pair with { IgnoreRules = ["FileName:" + OutOfScopeCompletion] }, [fixture.Pair])
        }).SyncWorkAsync(default);
        Check.Require(events.Any(item => item.Error is ItemStoreMutationException { PrimaryCode: ConstraintPrimary }),
            "B204-06: an out-of-scope completion is contained through the same transaction owner");
        Check.Require(PendingAt(fixture, Side.Local, OutOfScopeCompletion).Count == 1 &&
            fixture.Inner.Issues(PairId, [IssueKind.Failure]).Count == 1,
            "B204-06: out-of-scope containment leaves the work pending and one Failure");
    }

    // 9.6: a failure inside the notice-only Commit escapes and claims no durable notice.
    private static async Task CompletionFailureNoticeWriteAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var failing = SeedSingleCompletion(fixture, FailingCompletion, LocalId);
        InstallCompletionFkFault(fixture.DatabasePath!, failing.AnchorWorkId);
        var noticeWrites = 0;
        var injected = new SqliteException("injected-notice-write", ConstraintPrimary);
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            // Only the notice-only Commit: no selection, and it raises this run's certified cause.
            if (method.Name != nameof(IStateStore.Commit) || args.Length != 4 ||
                args[1] is not StateChange change || !change.Raise.Any(raised => raised.Cause == TextKey.FailStoreMutation))
                return;
            Interlocked.Increment(ref noticeWrites);
            throw injected;
        };
        var events = new List<RunEvent>();
        Exception? escaped = null;
        try { await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default); }
        catch (Exception error) { escaped = error; }
        finally { fixture.Hooks.BeforeCallWith = null; }
        Check.Require(escaped is not null && Flattened(escaped).Contains(injected),
            "B204-07: the notice-write error escapes instead of being swallowed");
        Check.Require(Volatile.Read(ref noticeWrites) == 1,
            "B204-07: no recursive second notice attempt is made");
        Check.Require(fixture.Inner.Issues(PairId).Count == 0,
            "B204-07: no durable notice success is claimed");
        Check.Require(PendingAt(fixture, Side.Local, FailingCompletion).Count == 1 &&
            events.Any(item => item.Error is ItemStoreMutationException),
            "B204-07: the failed item remains pending and its certificate is still reported");
    }

    // 9.7: unclassified raw failures reach selection dispatch unchanged; Storage refuses non-constraint refusals.
    private static async Task CompletionFailureBoundariesAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var failing = SeedSingleCompletion(fixture, FailingCompletion, LocalId);
        var raw = new SqliteException("synthetic-read-failure", ConstraintPrimary);
        fixture.Hooks.OnReadWork = _ => throw raw;
        Exception? escaped = null;
        try { await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), []).SyncWorkAsync(default); }
        catch (Exception error) { escaped = error; }
        finally { fixture.Hooks.OnReadWork = null; }
        Check.Require(escaped is not null && Flattened(escaped).Contains(raw) &&
            fixture.Inner.Issues(PairId).Count == 0 &&
            PendingAt(fixture, Side.Local, FailingCompletion).Count == 1,
            "B204-08: a raw store-read failure escapes without a fabricated notice or lost work");

        using var readOnly = new Fixture(realStore: true);
        var blocked = SeedSingleCompletion(readOnly, RollbackCompletion, LocalId);
        var connection = StoreConnection(readOnly);
        ExecuteConnection(connection, "PRAGMA query_only=ON;");
        Exception? forbidden = null;
        try { readOnly.Inner.CompleteWork(blocked, null); }
        catch (Exception error) { forbidden = error; }
        finally { ExecuteConnection(connection, "PRAGMA query_only=OFF;"); }
        int[] sqliteCodes = forbidden is null ? [] : Flattened(forbidden).OfType<SqliteException>()
            .Select(error => error.SqliteErrorCode).Distinct().ToArray();
        Check.Require(forbidden is not null && sqliteCodes.SequenceEqual([8]) &&
            !Flattened(forbidden).OfType<ItemStoreMutationException>().Any(),
            "B204-08: a real non-constraint write refusal (native code 8) is not contained");

        using var wrongShape = new Fixture(realStore: true);
        var unmatched = SeedSingleCompletion(wrongShape, PathCompletion, LocalId);
        ExecuteOnStore(wrongShape,
            "CREATE TRIGGER b204_wrong_shape AFTER DELETE ON change_work WHEN old.work_id=" + unmatched.AnchorWorkId +
            " BEGIN INSERT INTO b204_absent_table(value) VALUES(1); END;");
        Exception? wrong = null;
        try { wrongShape.Inner.CompleteWork(unmatched, null); }
        catch (Exception error) { wrong = error; }
        finally
        {
            ExecuteOnStore(wrongShape, "DROP TRIGGER IF EXISTS b204_wrong_shape;");
        }
        Check.Require(wrong is not null && Flattened(wrong).OfType<SqliteException>().Any(error => error.SqliteErrorCode == 1) &&
            !Flattened(wrong).OfType<ItemStoreMutationException>().Any(),
            "B204-08: an in-transaction non-constraint SQL error (code 1) is not contained either");
    }

    // 9.9: Phase A documents notice churn between real opportunities but no automatic extra opportunity.
    private static async Task CompletionFailureChurnAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var failing = SeedSingleCompletion(fixture, FailingCompletion, LocalId);
        InstallCompletionFkFault(fixture.DatabasePath!, failing.AnchorWorkId);
        var completes = 0;
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            if (method.Name == nameof(IStateStore.CompleteWork)) Interlocked.Increment(ref completes);
        };
        var events = new List<RunEvent>();
        try
        {
            await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
            Check.Require(fixture.Inner.Issues(PairId, [IssueKind.Failure]).Count == 1,
                "B204-09: the first contained failure stores one current Failure");
            RemoveCompletionFkFault(fixture.DatabasePath!);
            await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
            Check.Require(fixture.Inner.Issues(PairId).Count == 0,
                "B204-09: ordinary success closes the generic Failure");
            var again = SeedSingleCompletion(fixture, FailingCompletion, LocalId, page: 2);
            InstallCompletionFkFault(fixture.DatabasePath!, again.AnchorWorkId);
            await LiveSyncer(fixture, again, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
            var current = fixture.Inner.Issues(PairId, [IssueKind.Failure]);
            Check.Require(current.Count == 1 && PendingAt(fixture, Side.Local, FailingCompletion).Count == 1,
                "B204-09: a later refusal still stores exactly one current Failure for the pending work");
            Check.Require(Volatile.Read(ref completes) == 3,
                "B204-09: no automatic additional opportunity attempts the completion again");
        }
        finally { fixture.Hooks.BeforeCallWith = null; }
    }

    // 11: print-only successful-path counts for three completion shapes; no threshold, no native timing claim.
    private static async Task CompletionFailureCostAsync()
    {
        foreach (var shape in new[] { "identity", "path-only", "out-of-scope" })
        {
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            var saved = LocalEntry(PathCompletion, LocalId);
            var remote = RemoteEntry(PathCompletion, DropboxId);
            fixture.Dropbox.Seed(remote, [1]);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(saved, remote)] });
            var changed = saved with { Modified = Stamp.AddSeconds(1) };
            fixture.Local.Seed(changed, [2]);
            var notice = new ChangeNotice(Side.Local, ChangeKind.Changed, PathCompletion, null,
                shape == "path-only" ? null : saved.Id, changed, false, false);
            var selection = Seed(fixture, notice);
            var calls = new Dictionary<string, int>(StringComparer.Ordinal);
            fixture.Hooks.BeforeCall = method => calls[method.Name] = calls.GetValueOrDefault(method.Name) + 1;
            var connection = StoreConnection(fixture);
            var statements = 0;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, _) => statements++), null);
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            bool completed;
            try
            {
                completed = shape == "out-of-scope"
                    ? fixture.Inner.CompleteOutOfScope(selection,
                        new PathScope(fixture.Pair with { IgnoreRules = ["FileName:" + PathCompletion] }, [fixture.Pair]))
                    : fixture.Inner.CompleteWork(selection, null);
            }
            finally
            {
                allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null);
                fixture.Hooks.BeforeCall = null;
            }
            Console.WriteLine($"b204-cost shape={shape} completed={completed} store-calls={calls.Values.Sum()} " +
                $"complete-work={calls.GetValueOrDefault(nameof(IStateStore.CompleteWork))} sql-statements={statements} " +
                $"allocated-bytes={allocated}");
        }
        await Task.CompletedTask;
    }

    private static (WorkSelection Failing, WorkSelection Healthy) SeedCompletionPair(Fixture fixture)
    {
        ClearObligations(fixture);
        var failing = SavedCompletion(fixture, FailingCompletion, LocalId, page: 1);
        var healthy = SavedCompletion(fixture, HealthyCompletion, "healthy-id", page: 2);
        return (failing, healthy);
    }

    private static WorkSelection SeedSingleCompletion(Fixture fixture, string path, string localId, long page = 1)
    {
        ClearObligations(fixture);
        return SavedCompletion(fixture, path, localId, page);
    }

    private static WorkSelection SavedCompletion(Fixture fixture, string path, string localId, long page)
    {
        var saved = LocalEntry(path, localId);
        var remote = RemoteEntry(path, "remote-" + localId);
        fixture.Dropbox.Seed(remote, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(saved, remote)] });
        var changed = saved with { Modified = Stamp.AddSeconds(1) };
        fixture.Local.Seed(changed, [2]);
        return Selected(fixture, path, changed, page);
    }

    // A test-only child table's missing parent is a real SQLite foreign-key refusal on the chosen work row.
    private static void InstallCompletionFkFault(string database, long workId) => ExecuteDatabase(database,
        "CREATE TABLE " + FaultParent + "(id INTEGER PRIMARY KEY);" +
        "CREATE TABLE " + FaultChild + "(parent_id INTEGER NOT NULL REFERENCES " + FaultParent + "(id) ON DELETE NO ACTION);" +
        "CREATE TRIGGER " + FaultTrigger + " AFTER DELETE ON change_work WHEN old.work_id=" + workId +
        " BEGIN INSERT INTO " + FaultChild + "(parent_id) VALUES(" + MissingParent + "); END;");

    private static void RemoveCompletionFkFault(string database) => ExecuteDatabase(database,
        "DROP TRIGGER IF EXISTS " + FaultTrigger + ";" +
        "DROP TABLE IF EXISTS " + FaultChild + ";" +
        "DROP TABLE IF EXISTS " + FaultParent + ";");

    private static void ExecuteDatabase(string database, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        ExecuteConnection(connection, sql);
    }

    private static void ExecuteConnection(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExecuteOnStore(Fixture fixture, string sql, params (string Name, object? Value)[] arguments)
    {
        using var command = StoreConnection(fixture).CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static object? ExecuteScalarOnStore(Fixture fixture, string sql, params (string Name, object? Value)[] arguments)
    {
        using var command = StoreConnection(fixture).CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar();
    }

    private static string? ReadRenameContinuationText(Fixture fixture, long workId) =>
        ExecuteScalarOnStore(fixture,
            "SELECT rename_continuation_json FROM change_work WHERE work_id=$work;", ("$work", workId)) as string;

    private static SqliteConnection StoreConnection(Fixture fixture) =>
        (SqliteConnection)fixture.Inner.GetType().GetField("_connection",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Inner)!;

    private static IReadOnlyList<Exception> Flattened(Exception error) =>
        error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [error];
}
