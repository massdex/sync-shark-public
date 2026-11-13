using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

// B-212 execution witnesses (SPEC-B212-6 9.3, SPEC-B212-7 9.1): each store starts as a genuine version-14 file whose
// one-time step creates the duplicate-folder obligation, then one public SyncWorkAsync call executes it.
public static partial class ChangeRunnerChecks
{
    // T1's names: X is the Local folder at Renamed; A = X@Old <-> R@Old (content), D = X@Renamed <-> E@Renamed
    // (empty). Old sorts before Renamed, so the attempt checks A first (the store's key order).
    private const string RepairOld = "Old";
    private const string RepairNew = "Renamed";
    private const string RepairChild = "Old/child";
    private const string RepairX = "folder-x";
    private const string RepairR = "folder-r";
    private const string RepairE = "folder-e";
    private const string RepairThird = "Third";
    private const string RepairZed = "Zed";

    public static void RunDuplicateRepair() => RunDuplicateRepairAsync().GetAwaiter().GetResult();

    // Every witness runs, so one cycle reports all independent failures together (standard 4.1).
    private static async Task RunDuplicateRepairAsync()
    {
        var failures = new List<Exception>();
        foreach (var witness in new Func<Task>[]
        {
            RepairDropboxVictimAsync, RepairHandDeletedVictimAsync, RepairLateDescendantAsync, RepairObsoleteHeldAsync,
            RepairChangedShapeAsync, RepairLinkedElsewhereAsync, RepairHeldThenRetryAsync, RepairOwnRetainedAsync
        })
        {
            try { await witness(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    // E1 (T1): one Dropbox delete, the settle through the operation path (-7 2), then W renames R onto Renamed.
    private static async Task RepairDropboxVictimAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = RepairFixture(victimPresent: true);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, RepairNew, RepairOld,
            RepairX, null, false, false));
        var (local, dropbox, submits) = RepairViews(fixture);
        var calls = new List<string>();
        string? atFirstSelection = null;
        fixture.Hooks.BeforeCall = method =>
        {
            lock (calls)
            {
                calls.Add(method.Name);
                if (method.Name != nameof(IStateStore.BeginSelection) || atFirstSelection is not null) return;
                atFirstSelection = $"localReads={local.Reads} localListings={local.Listings} dropboxReads={dropbox.Reads} " +
                    $"dropboxListings={dropbox.Listings} submits={submits.Count} " +
                    $"victimBound={fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(RepairNew)) is not null} " +
                    $"preimage={fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(RepairOld)) == RepairBinding(RepairOld, RepairR)}";
            }
        };
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        fixture.Hooks.BeforeCall = null;
        var settled = calls.IndexOf(nameof(IStateStore.CompleteFolderRemoval));
        var renamed = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(RepairNew));
        Check.Require(submits.Count == 1 && await fixture.Dropbox.ReadOneAsync(RepairNew, default) is { Id: RepairR } &&
            dropbox.Moves == 1 && LocalEffects(local) == 0 && fixture.Inner.Issues(PairId).Count == 0 &&
            RepairRows(fixture) == 0 && fixture.Inner.UnresolvedFolderRemovals(PairId, default).Count() == 0 &&
            renamed is { LocalId: RepairX, DropboxId: RepairR } &&
            settled >= 0 && settled < calls.IndexOf(nameof(IStateStore.BeginSelection)) &&
            atFirstSelection is not null && atFirstSelection.Contains("victimBound=False", StringComparison.Ordinal) &&
            atFirstSelection.Contains("preimage=True", StringComparison.Ordinal) &&
            fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark()) == (0, 0),
            "B212-E1: E is deleted once, the obligation settles before W's first selection, and W moves R onto Renamed: " +
            "submits=" + submits.Count + "; dropboxMoves=" + dropbox.Moves + "; localEffects=" + LocalEffects(local) +
            "; issues=" + fixture.Inner.Issues(PairId).Count + "; obligations=" + RepairRows(fixture) +
            "; renamed=" + renamed + "; settledAt=" + settled + "; atFirstSelection=" + atFirstSelection);
        Console.WriteLine("B212-E1 attempt provider calls before W's first selection: " + atFirstSelection);
    }

    // E7: E deleted by hand, so D4p settles state-only (-7 1.4): no local delete, no submission, and the handoff alone
    // moves R onto Renamed with no other pending work.
    private static async Task RepairHandDeletedVictimAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = RepairFixture(victimPresent: false);
        var (local, dropbox, submits) = RepairViews(fixture);
        await LiveSyncer(fixture, RepairOpportunity(fixture), new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var renamed = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(RepairNew));
        Check.Require(local.Deletes == 0 && LocalEffects(local) == 0 && submits.Count == 0 && dropbox.Moves == 1 &&
            await fixture.Dropbox.ReadOneAsync(RepairNew, default) is { Id: RepairR } &&
            renamed is { LocalId: RepairX, DropboxId: RepairR } && RepairRows(fixture) == 0 &&
            fixture.Inner.Issues(PairId).Count == 0 &&
            fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark()) == (0, 0),
            "B212-E7: the state-only settle forgets D, and the handoff moves R onto Renamed: localDeletes=" + local.Deletes +
            "; localEffects=" + LocalEffects(local) + "; submits=" + submits.Count + "; dropboxMoves=" + dropbox.Moves +
            "; renamed=" + renamed + "; obligations=" + RepairRows(fixture) + "; issues=" + fixture.Inner.Issues(PairId).Count);
    }

    // E3: a saved descendant committed under the victim's key before completion is refused by the folder validation
    // and the pre-forget P2 (-7 2): the victim stays bound, the operation is Retained (active, FailChanged, the existing
    // refused-complete contract of FolderDeleteRunnerChecks), and the obligation is kept.
    private static async Task RepairLateDescendantAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = RepairFixture(victimPresent: true);
        RepairViews(fixture);
        var late = RepairNew + "/late";
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name != nameof(IStateStore.CompleteFolderRemoval)) return;
            fixture.Hooks.BeforeCall = null;
            fixture.Inner.Commit(PairId, StateChange.Empty with
                { Record = [SyncedEntries.From(LocalEntry(late, "late-local"), RemoteEntry(late, "late-dropbox"))] });
        };
        await LiveSyncer(fixture, RepairOpportunity(fixture), new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var operations = fixture.Inner.UnresolvedFolderRemovals(PairId, default).ToArray();
        Check.Require(fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(RepairNew)) == RepairBinding(RepairNew, RepairE) &&
            operations is [{ Active: true, Phase: FolderRemovalPhase.Retained, Cause: TextKey.FailChanged,
                Authority: FolderRemovalAuthority.Repair }] && RepairRows(fixture) == 1,
            "B212-E3: the late descendant refuses the settle; the victim stays bound and the operation is Retained: operations=" +
            string.Join(" | ", operations.Select(operation => operation.Phase + ":" + operation.Active + ":" + operation.Authority)) +
            "; obligations=" + RepairRows(fixture));
    }

    // E9 (T5): a held obligation whose bindings changed is obsolete: the row goes, its Failure closes, the obligation
    // makes no provider call, and W runs.
    private static async Task RepairObsoleteHeldAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = RepairFixture(victimPresent: false);
        RepairHold(fixture, TextKey.ReasonHistory, TextKey.ReasonNotEmpty);
        fixture.Inner.Commit(PairId, StateChange.Empty with { ForgetSelf = [(Side.Local, PathNames.Key(RepairNew))] });
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, RepairNew, RepairOld,
            RepairX, null, false, false));
        var (local, dropbox, submits) = RepairViews(fixture);
        int? beforeLanes = null;
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name == nameof(IStateStore.BeginSelection))
                beforeLanes ??= local.Reads + local.Listings + dropbox.Reads + dropbox.Listings + submits.Count;
        };
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        fixture.Hooks.BeforeCall = null;
        var renamed = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(RepairNew));
        Check.Require(RepairRows(fixture) == 0 && fixture.Inner.Issues(PairId).Count == 0 && beforeLanes == 0 &&
            dropbox.Moves == 1 && renamed is { LocalId: RepairX, DropboxId: RepairR },
            "B212-E9: the obsolete obligation settles with no provider call, its Failure closes, and W runs: obligations=" +
            RepairRows(fixture) + "; issues=" + fixture.Inner.Issues(PairId).Count + "; callsBeforeLanes=" + beforeLanes +
            "; dropboxMoves=" + dropbox.Moves + "; renamed=" + renamed);
    }

    // E9c: X bound to a third folder after the step: the changed shape settles the obligation with one
    // FailDuplicateFolderLinks and deletes nothing.
    private static async Task RepairChangedShapeAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = RepairFixture(victimPresent: true);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [RepairBinding(RepairThird, "folder-f")] });
        fixture.Dropbox.Seed(FolderEntry(RepairThird, "folder-f"), null);
        var (local, _, submits) = RepairViews(fixture);
        await LiveSyncer(fixture, RepairOpportunity(fixture), new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var issues = fixture.Inner.Issues(PairId);
        Check.Require(RepairRows(fixture) == 0 && issues is [{ Cause: TextKey.FailDuplicateFolderLinks }] &&
            submits.Count == 0 && local.Deletes == 0,
            "B212-E9c: a changed shape settles with one links notice and no delete: obligations=" + RepairRows(fixture) +
            "; issues=" + string.Join(" | ", issues.Select(issue => issue.Cause + ":" + issue.Path)) +
            "; submits=" + submits.Count + "; localDeletes=" + local.Deletes);
    }

    // E13: E's id is bound elsewhere (Z<->E): no delete, one Failure naming ReasonLinkedElsewhere for both items, and
    // the obligation is held by it.
    private static async Task RepairLinkedElsewhereAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = RepairFixture(victimPresent: true);
        fixture.Inner.Commit(PairId, StateChange.Empty with
            { Record = [SyncedEntries.From(FolderEntry(RepairZed, "folder-z"), FolderEntry(RepairZed, RepairE))] });
        var (local, _, submits) = RepairViews(fixture);
        await LiveSyncer(fixture, RepairOpportunity(fixture), new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var issues = fixture.Inner.Issues(PairId);
        Check.Require(submits.Count == 0 && local.Deletes == 0 && issues is [{ Cause: TextKey.FailDuplicateFolderBinding,
                Path: RepairOld } failure] && failure.Subject.Folders ==
            new IssueSubject.DuplicateFolders(TextKey.ReasonLinkedElsewhere, TextKey.ReasonLinkedElsewhere) &&
            RepairHeldBy(fixture) == failure.Id,
            "B212-E13: a linked-elsewhere partner holds one Failure and deletes nothing: submits=" + submits.Count +
            "; issues=" + string.Join(" | ", issues.Select(issue => issue.Cause + ":" + issue.Path + ":" + issue.Subject.Folders)) +
            "; heldBy=" + RepairHeldBy(fixture));
    }

    // E5 (T7): A fails P2 and D fails P6, so one Failure holds the obligation and W, with no delete. After E's file
    // is moved away, Retry releases the hold without reading X's paths, and the next opportunity gives E1's outcome.
    private static async Task RepairHeldThenRetryAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = RepairFixture(victimPresent: true);
        var inner = RemoteEntry(RepairNew + "/inner", "inner-dropbox");
        fixture.Dropbox.Seed(inner, [1]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, RepairNew, RepairOld,
            RepairX, null, false, false));
        var (local, dropbox, submits) = RepairViews(fixture);
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var issues = fixture.Inner.Issues(PairId);
        Check.Require(submits.Count == 0 && dropbox.Moves == 0 && issues is [{ Cause: TextKey.FailDuplicateFolderBinding } held] &&
            held.Subject.Folders == new IssueSubject.DuplicateFolders(TextKey.ReasonHistory, TextKey.ReasonNotEmpty) &&
            RepairHeldBy(fixture) == held.Id &&
            fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark()) != (0, 0),
            "B212-E5: one Failure holds the obligation and W, with no delete: submits=" + submits.Count +
            "; dropboxMoves=" + dropbox.Moves + "; issues=" +
            string.Join(" | ", issues.Select(issue => issue.Cause + ":" + issue.Path + ":" + issue.Subject.Folders)) +
            "; heldBy=" + RepairHeldBy(fixture));
        await fixture.Dropbox.DeleteAsync(inner, null, default);
        var reads = local.Reads + local.Listings + dropbox.Reads + dropbox.Listings;
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), [])
            .ResolveAsync(issues[0], ResolutionChoice.Retry, null, null, guard.Token).WaitAsync(guard.Token);
        var retryReads = local.Reads + local.Listings + dropbox.Reads + dropbox.Listings - reads;
        Check.Require(retryReads == 0 && fixture.Inner.IssueById(PairId, issues[0].Id) is null && RepairHeldBy(fixture) is null &&
            RepairRows(fixture) == 1,
            "B212-E5: Retry releases the hold and reads none of X's paths: reads=" + retryReads +
            "; heldBy=" + RepairHeldBy(fixture) + "; obligations=" + RepairRows(fixture));
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var renamed = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(RepairNew));
        Check.Require(submits.Count == 1 && dropbox.Moves == 1 && LocalEffects(local) == 0 && RepairRows(fixture) == 0 &&
            fixture.Inner.Issues(PairId).Count == 0 && renamed is { LocalId: RepairX, DropboxId: RepairR },
            "B212-E5: after Retry the next opportunity gives the E1 outcome: submits=" + submits.Count +
            "; dropboxMoves=" + dropbox.Moves + "; localEffects=" + LocalEffects(local) +
            "; obligations=" + RepairRows(fixture) + "; issues=" + fixture.Inner.Issues(PairId).Count + "; renamed=" + renamed);
    }

    // E2b (T15): a child appears in E after the freeze, so the send refuses and the Repair operation is Retained with
    // its own notice; row 3 (Own, inactive) then does nothing, so no FailDuplicateFolderBinding is raised.
    private static async Task RepairOwnRetainedAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = RepairFixture(victimPresent: true);
        var (_, _, submits) = RepairViews(fixture);
        fixture.Hooks.AfterCall = (name, _, _) =>
        {
            if (name != nameof(IStateStore.BeginFolderRemoval)) return;
            fixture.Hooks.AfterCall = null;
            fixture.Dropbox.Seed(RemoteEntry(RepairNew + "/added", "added-dropbox"), [1]);
        };
        for (var opportunity = 0; opportunity < 2; opportunity++)
            await LiveSyncer(fixture, RepairOpportunity(fixture), new Boundary(_ => new Lease()), [])
                .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var operations = fixture.Inner.UnresolvedFolderRemovals(PairId, default).ToArray();
        var issues = fixture.Inner.Issues(PairId);
        Check.Require(submits.Count == 0 &&
            operations is [{ Active: false, Phase: FolderRemovalPhase.Retained, Authority: FolderRemovalAuthority.Repair }] &&
            RepairRows(fixture) == 1 && RepairHeldBy(fixture) is null && issues.Count >= 1 &&
            issues.All(issue => issue.Cause != TextKey.FailDuplicateFolderBinding),
            "B212-E2b: the changed endpoint retains the own operation and raises no duplicate-folder Failure: submits=" +
            submits.Count + "; operations=" +
            string.Join(" | ", operations.Select(operation => operation.Phase + ":" + operation.Active + ":" + operation.Authority)) +
            "; obligations=" + RepairRows(fixture) + "; heldBy=" + RepairHeldBy(fixture) +
            "; issues=" + string.Join(" | ", issues.Select(issue => issue.Cause + ":" + issue.Path)) +
            "; rows=" + RepairOperationRows(fixture));
    }

    // The runner's own hold commit (-6 3.5.4), made directly: the Failure at A's shared-side path and the CAS hold.
    private static void RepairHold(Fixture fixture, TextKey first, TextKey second)
    {
        var subject = IssueSubject.DuplicateFolderItems(Side.Local,
            [RepairBinding(RepairOld, RepairR), RepairBinding(RepairNew, RepairE)]) with
            { Folders = new IssueSubject.DuplicateFolders(first, second) };
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDuplicateFolderBinding, RepairOld, subject)],
            HoldRepair = (Side.Local, RepairX, null)
        });
        Check.Require(RepairHeldBy(fixture) is not null, "B212 fixture: the hold commit binds the obligation to its Failure");
    }

    // The T1 store: the two duplicate bindings and A's saved child, committed and then made a genuine version-14 file
    // (the two v15 additions removed), so reopening runs the one-time step. Its own schema check refuses any other shape.
    private static Fixture RepairFixture(bool victimPresent)
    {
        var fixture = new Fixture(realStore: true);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Record = [RepairBinding(RepairOld, RepairR), RepairBinding(RepairNew, RepairE),
                SyncedEntries.From(LocalEntry(RepairChild, "child-local"), RemoteEntry(RepairChild, "child-dropbox"))]
        });
        fixture.Local.Seed(FolderEntry(RepairNew, RepairX), null);
        fixture.Local.Seed(LocalEntry(RepairNew + "/child", "child-local"), [1]);
        fixture.Dropbox.Seed(FolderEntry(RepairOld, RepairR), null);
        fixture.Dropbox.Seed(RemoteEntry(RepairChild, "child-dropbox"), [1]);
        if (victimPresent) fixture.Dropbox.Seed(FolderEntry(RepairNew, RepairE), null);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.DatabasePath + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE binding_repair;ALTER TABLE folder_removal DROP COLUMN authority;PRAGMA user_version=14;";
            command.ExecuteNonQuery();
        }
        fixture.RestartStore();
        RepairDone(fixture);
        ClearObligations(fixture);
        Check.Require(RepairRows(fixture) == 1, "B212 fixture: the one-time step records one obligation for X's two bindings");
        return fixture;
    }

    private static SyncedEntry RepairBinding(string path, string dropboxId) =>
        SyncedEntries.From(FolderEntry(path, RepairX), FolderEntry(path, dropboxId));

    private static (CountingSide Local, CountingSide Dropbox, List<int> Submits) RepairViews(Fixture fixture)
    {
        var local = new CountingSide(fixture.Local);
        var dropbox = new CountingSide(fixture.Dropbox);
        var submits = new List<int>();
        fixture.LocalView = local;
        fixture.DropboxView = new ProxySide(dropbox) { OnSubmit = effects => submits.Add(effects.Count) };
        return (local, dropbox, submits);
    }

    private static int LocalEffects(CountingSide local) =>
        local.Stages + local.Publishes + local.Ensures + local.Deletes + local.Moves;

    // The context's opportunity only; a witness with no seeded notice selects nothing through it.
    private static WorkSelection RepairOpportunity(Fixture fixture)
    {
        var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
        return new WorkSelection(PairId, Generation, source.Epoch, Side.Local, VolumeKey, RepairX,
            PathNames.KeyFor(Side.Local, RepairNew), 0, 0);
    }

    // Diagnostic only: every folder_removal row's phase, active flag, cause, disposition and authority.
    private static string RepairOperationRows(Fixture fixture)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.DatabasePath + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(phase||':'||active||':'||coalesce(cause,'')||':'||" +
            "coalesce(retirement_disposition,'')||':'||authority,' | ') FROM folder_removal;";
        return command.ExecuteScalar() as string ?? "none";
    }

    private static long? RepairHeldBy(Fixture fixture)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.DatabasePath + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT issue_id FROM binding_repair;";
        return command.ExecuteScalar() is long id ? id : null;
    }

    private static long RepairRows(Fixture fixture)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.DatabasePath + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM binding_repair;";
        return (long)command.ExecuteScalar()!;
    }
}
