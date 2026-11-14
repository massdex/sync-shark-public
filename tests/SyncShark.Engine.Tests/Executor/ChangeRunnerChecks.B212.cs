using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
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
            RepairChangedShapeAsync, RepairLinkedElsewhereAsync, RepairHeldThenRetryAsync, RepairOwnRetainedAsync,
            RepairRefusedCompleteCostAsync, RepairOtherCustodyAsync, RepairAncestorWaitAsync, VacateExactAsync,
            VacateReuseAsync, VacateSupersedeAsync, VacateReuseArmedAsync, VacateReuseEvidenceAsync, VacateKeepChosenAsync,
            VacateSupersedeNotWholeAsync, VacateSupersedeGapAsync, VacateSupersedeCostAsync, VacateForeignAsync,
            VacateGoneAsync, VacateGonePriorAsync, ReplaceDeferredJobAsync
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
        // I-8, -7 7.2's exact E1 count (measured in run 43526845916): D2 reads both sides of both bindings (2 + 2), the
        // send's EligibleAsync reads one per side (2), P6, D8 and the send's inventory list E once each (3), and one
        // delete_batch; the in-memory provider completes synchronously, so there is no poll.
        const string attemptCalls = "localReads=3 localListings=0 dropboxReads=3 dropboxListings=3 submits=1 ";
        var renamed = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(RepairNew));
        Check.Require(submits.Count == 1 && await fixture.Dropbox.ReadOneAsync(RepairNew, default) is { Id: RepairR } &&
            dropbox.Moves == 1 && LocalEffects(local) == 0 && fixture.Inner.Issues(PairId).Count == 0 &&
            RepairRows(fixture) == 0 && fixture.Inner.UnresolvedFolderRemovals(PairId, default).Count() == 0 &&
            renamed is { LocalId: RepairX, DropboxId: RepairR } &&
            settled >= 0 && settled < calls.IndexOf(nameof(IStateStore.BeginSelection)) &&
            atFirstSelection is not null && atFirstSelection.StartsWith(attemptCalls, StringComparison.Ordinal) &&
            atFirstSelection.Contains("victimBound=False", StringComparison.Ordinal) &&
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

    // E2b (T15, coordinator ruling 1 on writer-7): a child appears in E after the freeze, so the send refuses before
    // anything is sent and the Repair operation retires as no-effect with its own FailChanged notice. The binding stays
    // duplicated, so the next opportunity holds it with one FailDuplicateFolderBinding (History, NotEmpty) offering
    // Retry and Ignore; a later opportunity raises no second duplicate-folder notice.
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
        var notices = new List<string>();
        for (var opportunity = 0; opportunity < 3; opportunity++)
        {
            await LiveSyncer(fixture, RepairOpportunity(fixture), new Boundary(_ => new Lease()), [])
                .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
            notices.Add(string.Join(" | ", fixture.Inner.Issues(PairId)
                .Select(issue => issue.Id + ":" + issue.Cause + ":" + issue.Path + ":" + issue.Subject.Folders)));
        }
        var issues = fixture.Inner.Issues(PairId);
        var changed = issues.Where(issue => issue.Cause == TextKey.FailChanged).ToArray();
        var duplicates = issues.Where(issue => issue.Cause == TextKey.FailDuplicateFolderBinding).ToArray();
        Check.Require(submits.Count == 0 && !fixture.Inner.UnresolvedFolderRemovals(PairId, default).Any() &&
            RepairRows(fixture) == 1 && issues.Count == 2 &&
            changed is [{ Kind: IssueKind.Failure, Path: RepairNew }] &&
            !notices[0].Contains(nameof(TextKey.FailDuplicateFolderBinding), StringComparison.Ordinal) &&
            duplicates is [{ Kind: IssueKind.Failure, Path: RepairOld } duplicate] &&
            duplicate.Subject.Folders == new IssueSubject.DuplicateFolders(TextKey.ReasonHistory, TextKey.ReasonNotEmpty) &&
            Resolutions.Allows(duplicate.Kind, duplicate.Cause, ResolutionChoice.Retry) &&
            Resolutions.Allows(duplicate.Kind, duplicate.Cause, ResolutionChoice.Ignore) &&
            RepairHeldBy(fixture) == duplicate.Id && notices[1] == notices[2],
            "B212-E2b: the refused operation's FailChanged, then one held duplicate-folder Failure offering Retry and " +
            "Ignore, and no second one: submits=" + submits.Count + "; obligations=" + RepairRows(fixture) +
            "; heldBy=" + RepairHeldBy(fixture) + "; perOpportunity=" + string.Join(" || ", notices) +
            "; rows=" + RepairOperationRows(fixture));
        Console.WriteLine("B212-E2b notices: " + notices[^1]);
    }

    // Coordinator ruling 3 on writer-7: E3's operation stays active and Retained while the late descendant keeps
    // refusing its complete, so row 3 resumes it at every opportunity. That resume must be flat in store size and make
    // no provider call while its inputs are unchanged. Two store sizes (8 and 32 unrelated synced files); per
    // opportunity, every provider call (reads, listings, content, effects, submissions, cursor captures and change
    // windows) and the store calls by member, each store call being one fixed SQL recipe (paged reads are calls).
    private static async Task RepairRefusedCompleteCostAsync()
    {
        const int smaller = 8;
        var profiles = new List<(int Size, int Provider, string Store)>();
        foreach (var size in new[] { smaller, smaller * 4 })
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var fixture = RepairFixture(victimPresent: true);
            fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Record = Enumerable.Range(0, size).Select(index => SyncedEntries.From(
                    LocalEntry("Unrelated/f" + index, "unrelated-local-" + index),
                    RemoteEntry("Unrelated/f" + index, "unrelated-dropbox-" + index))).ToArray()
            });
            var (local, dropbox, submits) = RepairViews(fixture);
            var windows = 0;
            ExecutorContext Counted(ExecutorContext context) => context with
            {
                CaptureFolderCursorAsync = _ =>
                {
                    windows++;
                    return Task.FromResult(Cursor);
                },
                ReadFolderChangesAsync = (_, token) =>
                {
                    windows++;
                    return NoFolderChangesAsync(token);
                }
            };
            fixture.Hooks.BeforeCall = method =>
            {
                if (method.Name != nameof(IStateStore.CompleteFolderRemoval)) return;
                fixture.Hooks.BeforeCall = null;
                fixture.Inner.Commit(PairId, StateChange.Empty with
                {
                    Record = [SyncedEntries.From(LocalEntry(RepairNew + "/late", "late-local"),
                        RemoteEntry(RepairNew + "/late", "late-dropbox"))]
                });
            };
            await LiveSyncer(fixture, RepairOpportunity(fixture), new Boundary(_ => new Lease()), [], configure: Counted)
                .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
            var calls = new List<string>();
            fixture.Hooks.BeforeCall = method =>
            {
                lock (calls) calls.Add(method.Name);
            };
            for (var opportunity = 0; opportunity < 2; opportunity++)
            {
                calls.Clear();
                var before = ProviderCalls(local, dropbox, submits) + windows;
                await LiveSyncer(fixture, RepairOpportunity(fixture), new Boundary(_ => new Lease()), [], configure: Counted)
                    .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
                profiles.Add((size, ProviderCalls(local, dropbox, submits) + windows - before, string.Join(",",
                    calls.GroupBy(name => name).OrderBy(group => group.Key, StringComparer.Ordinal)
                        .Select(group => group.Key + "=" + group.Count()))));
            }
            fixture.Hooks.BeforeCall = null;
            Check.Require(fixture.Inner.UnresolvedFolderRemovals(PairId, default).ToArray() is
                [{ Active: true, Phase: FolderRemovalPhase.Retained, Authority: FolderRemovalAuthority.Repair }] &&
                RepairRows(fixture) == 1, "B212-E3 cost: the refused complete keeps the operation active and Retained; size=" + size);
        }
        var described = string.Join(" ; ", profiles.Select(profile =>
            "size=" + profile.Size + " provider=" + profile.Provider + " store=" + profile.Store));
        Check.Require(profiles.All(profile => profile.Provider == 0 && profile.Store == profiles[0].Store),
            "B212-E3 cost: each resume of the refused Retained operation makes no provider call and the same store " +
            "calls at both store sizes: " + described);
        Console.WriteLine("B212-E3 per-opportunity resume cost: " + described);
    }

    // E-busy (T14): an unrelated Inferred removal over R's subtree is Retained without a disposition, so the
    // obligation waits on that custody (row 6): no notice, no delete, W held. After the user's recovery choice retires
    // that operation, the next opportunity repairs as E1.
    private static async Task RepairOtherCustodyAsync()
    {
        const string sub = RepairOld + "/sub";
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = RepairFixture(victimPresent: true);
        var nested = SyncedEntries.From(FolderEntry(sub, "sub-local"), FolderEntry(sub, "sub-dropbox"));
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [nested] });
        fixture.Dropbox.Seed(FolderEntry(sub, "sub-dropbox"), null);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, RepairNew, RepairOld,
            RepairX, null, false, false));
        string foreign;
        using (var plan = await new FolderPlanBuilder(fixture.Context(selection)).BuildAsync(Row.ForOneSide(Side.Dropbox,
            nested.LocalKey, FolderEntry(sub, "sub-dropbox"), nested, [], null, RowContext.Neutral), Side.Dropbox,
            new HashSet<string>(), live: true, guard.Token))
            foreign = fixture.Inner.BeginFolderRemoval(PairId, plan, FolderRemovalAuthority.Inferred,
                fixture.Settings.StoragePageSize, guard.Token);
        var frozen = fixture.Inner.FolderRemoval(PairId, foreign);
        fixture.Inner.AdvanceFolderRemoval(PairId, foreign, frozen.Generation, frozen.Phase, frozen with
            { Generation = frozen.Generation + 1, Phase = FolderRemovalPhase.Retained, Cause = TextKey.FailChanged });
        var (local, dropbox, submits) = RepairViews(fixture);
        var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        Check.Require(submits.Count == 0 && local.Deletes == 0 && dropbox.Moves == 0 &&
            fixture.Inner.Issues(PairId).Count == 0 && RepairRows(fixture) == 1 && RepairHeldBy(fixture) is null &&
            fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == begun &&
            fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark()) != (0, 0),
            "B212-E-busy: other custody over R makes the obligation wait silently, with W held: submits=" + submits.Count +
            "; issues=" + fixture.Inner.Issues(PairId).Count + "; obligations=" + RepairRows(fixture) +
            "; begun=" + (fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) - begun));
        var retained = fixture.Inner.FolderRemoval(PairId, foreign);
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), [])
            .RecoverAsync(new RecoveryTarget(foreign, retained.Generation, null, null), RecoveryChoice.AcceptCurrent,
                guard.Token).WaitAsync(guard.Token);
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var renamed = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(RepairNew));
        Check.Require(submits.Count == 1 && dropbox.Moves == 1 && LocalEffects(local) == 0 && RepairRows(fixture) == 0 &&
            renamed is { LocalId: RepairX, DropboxId: RepairR } &&
            fixture.Inner.FolderRemoval(PairId, foreign) is { Active: false, RetirementDisposition: not null },
            "B212-E-busy: after the recovery choice the next opportunity repairs: submits=" + submits.Count +
            "; dropboxMoves=" + dropbox.Moves + "; obligations=" + RepairRows(fixture) + "; renamed=" + renamed +
            "; rows=" + RepairOperationRows(fixture));
    }

    // T16 (E-anc, B5-4): E's parent folder has pending ordinary work that the lanes settle state-only. The first lane
    // iteration's attempt waits on that ancestor (D3); the lanes retire its work, the existing retired repeat runs the
    // cleanup again, and the same SyncWorkAsync call deletes E once and moves R onto Parent/Renamed.
    private static async Task RepairAncestorWaitAsync()
    {
        const string parent = "Parent";
        var renamedPath = PathNames.Child(parent, RepairNew);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = RepairFixture(victimPresent: true, parent);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, parent, null, "parent-local",
            FolderEntry(parent, "parent-local"), false, false));
        var (local, dropbox, submits) = RepairViews(fixture);
        var calls = new List<string>();
        fixture.Hooks.BeforeCall = method =>
        {
            lock (calls) calls.Add(method.Name);
        };
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        fixture.Hooks.BeforeCall = null;
        var firstPass = calls.IndexOf(nameof(IStateStore.EndWorkPass));
        var frozen = calls.IndexOf(nameof(IStateStore.BeginFolderRemoval));
        var renamed = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(renamedPath));
        Check.Require(submits.Count == 1 && dropbox.Moves == 1 && LocalEffects(local) == 0 && RepairRows(fixture) == 0 &&
            fixture.Inner.Issues(PairId).Count == 0 && renamed is { LocalId: RepairX, DropboxId: RepairR } &&
            await fixture.Dropbox.ReadOneAsync(renamedPath, default) is { Id: RepairR } &&
            firstPass >= 0 && frozen > firstPass &&
            fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark()) == (0, 0),
            "B212-T16: the ancestor wait clears in the same SyncWorkAsync call, then E is deleted once and R moves: " +
            "submits=" + submits.Count + "; dropboxMoves=" + dropbox.Moves + "; obligations=" + RepairRows(fixture) +
            "; renamed=" + renamed + "; firstPass=" + firstPass + "; frozenAt=" + frozen +
            "; issues=" + string.Join(" | ", fixture.Inner.Issues(PairId).Select(issue => issue.Cause + ":" + issue.Path)));
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
    // With a parent, D sits at parent/Renamed under a synced parent folder (T16).
    private static Fixture RepairFixture(bool victimPresent, string? parent = null)
    {
        var renamed = parent is null ? RepairNew : PathNames.Child(parent, RepairNew);
        var fixture = new Fixture(realStore: true);
        var records = new List<SyncedEntry> { RepairBinding(RepairOld, RepairR), RepairBinding(renamed, RepairE),
            SyncedEntries.From(LocalEntry(RepairChild, "child-local"), RemoteEntry(RepairChild, "child-dropbox")) };
        if (parent is not null)
            records.Add(SyncedEntries.From(FolderEntry(parent, "parent-local"), FolderEntry(parent, "parent-dropbox")));
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = records });
        if (parent is not null)
        {
            fixture.Local.Seed(FolderEntry(parent, "parent-local"), null);
            fixture.Dropbox.Seed(FolderEntry(parent, "parent-dropbox"), null);
        }
        fixture.Local.Seed(FolderEntry(renamed, RepairX), null);
        fixture.Local.Seed(LocalEntry(renamed + "/child", "child-local"), [1]);
        fixture.Dropbox.Seed(FolderEntry(RepairOld, RepairR), null);
        fixture.Dropbox.Seed(RemoteEntry(RepairChild, "child-dropbox"), [1]);
        if (victimPresent) fixture.Dropbox.Seed(FolderEntry(renamed, RepairE), null);
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

    // Every counted provider call on both sides: reads, listings, content reads, effects and folder submissions.
    private static int ProviderCalls(CountingSide local, CountingSide dropbox, List<int> submits) =>
        local.Reads + local.Listings + local.ContentReads + local.Work +
        dropbox.Reads + dropbox.Listings + dropbox.ContentReads + dropbox.Work + submits.Count;

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
