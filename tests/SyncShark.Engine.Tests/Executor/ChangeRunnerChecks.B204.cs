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
    private const string RollbackProtected = "rollback-protected";
    private const string FaultChild = "b204_child";
    private const string FaultParent = "b204_parent";
    private const string FaultTrigger = "b204_fk_fault";
    // A parent row that is never inserted: the trigger's child insert is a real foreign-key refusal.
    private const long MissingParent = 900;
    private const int ConstraintPrimary = 19;
    private const int ForeignKeyExtended = 787;
    private const int ReadOnlyPrimary = 8;
    // Phase A section 11 amendment (round-5 coordinator ruling): with no temporary record the empty-set check makes
    // the ordinary path allocation-free and probe-free; when a temporary record exists TemporaryThisRun probes the
    // exact responsibility and the settlement-stable tuple, at two call sites, against one probe per call at
    // 709113313. Ordinary selections therefore never exceed the base count.
    private const int TemporaryLookupCallSites = 2;
    private const int TemporaryLookupsPerCallOrdinary = 0;
    private const int TemporaryLookupsPerCallWithTemporary = 2;
    private const int TemporaryLookupsPerCallBase = 1;
    private const string TransactionBegin = "<BEGIN>";
    private const string TransactionCommit = "<COMMIT>";
    private const string TransactionRollback = "<ROLLBACK>";

    public static void CompletionFailureContained() => CompletionFailureContainedAsync().GetAwaiter().GetResult();

    public static void CompletionFailureCost() => CompletionFailureCostAsync().GetAwaiter().GetResult();

    private static async Task CompletionFailureContainedAsync()
    {
        await CompletionFailureRealRefusalAsync();
        CompletionFailureRollback();
        await CompletionFailureSuppressionAsync();
        CompletionFailureRefreshedSelection();
        await CompletionFailureAncestorDependentAsync();
        await CompletionFailureConcurrentAsync();
        await CompletionFailureRefusalSignalAsync();
        await CompletionFailureRestartAsync();
        await CompletionFailureOutOfScopeAsync();
        await CompletionFailureNoticeWriteAsync();
        await CompletionFailureBoundariesAsync();
        CompletionFailureEpisodeOwnedAnchor();
        await CompletionFailureChurnAsync();
    }

    // 9.1: a real 19/787 inside CompleteWork's transaction contains the failure and a healthy sibling still completes.
    private static async Task CompletionFailureRealRefusalAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var (failing, _) = SeedCompletionPair(fixture);
        RequireForeignKeys(fixture);
        Check.Require(WorkRows(fixture, failing.AnchorWorkId) == 1,
            "B204-01 setup: the faulted work row exists before the run");
        InstallCompletionFkFault(fixture.DatabasePath!, failing.AnchorWorkId);
        Check.Require(TriggerVisible(fixture), "B204-01 setup: the store connection sees the fault trigger");
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        var certificate = Certificates(events);
        Check.Require(certificate.Count == 1,
            "B204-01a: the run reports exactly one certified refusal; observed=" + certificate.Count +
            "; codes=" + string.Join(",", certificate.Select(item => item.PrimaryCode + "/" + item.ExtendedCode)) +
            "; work-row=" + WorkRows(fixture, failing.AnchorWorkId) +
            "; pending=" + PendingAt(fixture, Side.Local, FailingCompletion).Count +
            "; events=" + string.Join(" | ", events.Select(Describe)));
        Check.Require(certificate[0].PrimaryCode == ConstraintPrimary && certificate[0].ExtendedCode == ForeignKeyExtended,
            "B204-01: observed codes " + certificate[0].PrimaryCode + "/" + certificate[0].ExtendedCode);
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

    // 9.1/4.3: the retirement's earlier clear of a rename continuation is restored by the explicit rollback, and a
    // later same-identity row owned by an episode and a member survives the contested completion.
    private static void CompletionFailureRollback()
    {
        var attempt = Guid.NewGuid();
        using var fixture = new Fixture(realStore: true, registeredAttempt: attempt);
        ClearObligations(fixture);
        var saved = LocalEntry(RollbackCompletion, LocalId);
        var remote = RemoteEntry(RollbackCompletion, DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(saved, remote)] });
        var first = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, RollbackCompletion, null,
            saved.Id, saved with { Modified = Stamp.AddSeconds(1) }, false, false), page: 1);
        var second = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, RollbackProtected, null,
            saved.Id, saved with { Modified = Stamp.AddSeconds(2) }, false, false), page: 2);
        Check.Require(second.AnchorWorkId > first.AnchorWorkId && second.Identity == first.Identity,
            "B204-02 setup: the protected row is a later row of the same identity");
        var continuation = StateEncoding.Envelope(new RenameContinuation(SyncedEntries.From(saved, remote), null));
        ExecuteOnStore(fixture, "UPDATE change_work SET rename_continuation_json=$json WHERE work_id=$work;",
            ("$json", continuation), ("$work", first.AnchorWorkId));
        var protectedIssue = fixture.Inner.RecordInUseFailure(PairId, Generation, first.SourceEpoch, RollbackProtected,
            new IssueSubject([new IssueItem(Side.Local, RollbackProtected, saved.Id)], null), TextKey.FailLocalInUse,
            second, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        var memberRows = MemberRows(fixture, second.AnchorWorkId);
        InstallCompletionFkFault(fixture.DatabasePath!, first.AnchorWorkId);
        var contested = first with { ThroughWorkId = second.AnchorWorkId };
        ItemStoreMutationException? certified = null;
        try { fixture.Inner.CompleteWork(contested, null); }
        catch (ItemStoreMutationException error) { certified = error; }
        catch (Exception error) { throw new InvalidOperationException("B204-02: completion must not escape raw", error); }
        Check.Require(certified is { PrimaryCode: ConstraintPrimary, ExtendedCode: ForeignKeyExtended },
            "B204-02: the transaction owner certifies the 19/787 refusal after rolling back");
        Check.Require(ReadRenameContinuationText(fixture, first.AnchorWorkId) == continuation,
            "B204-02: the earlier statement's cleared rename continuation is restored by the rollback");
        Check.Require(WorkRows(fixture, first.AnchorWorkId) == 1 && WorkRows(fixture, second.AnchorWorkId) == 1,
            "B204-02: both rows of the identity range survive the rolled-back deletion");
        Check.Require(fixture.Inner.Issues(PairId).Any(issue => issue.Id == protectedIssue.Token.IssueId) &&
            fixture.Inner.ReadItemEpisodeIssues(PairId).Contains(protectedIssue.Token.IssueId) &&
            memberRows == 1 && MemberRows(fixture, second.AnchorWorkId) == 1,
            "B204-02: the protected episode and member rows survive the contested completion and its rollback");
    }

    // 9.2: a healthy retirement justifies another pass, but the failed responsibility is attempted only once.
    private static async Task CompletionFailureSuppressionAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var (failing, healthy) = SeedCompletionPair(fixture);
        InstallCompletionFkFault(fixture.DatabasePath!, failing.AnchorWorkId);
        var attempts = new List<long>();
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            if (method.Name == nameof(IStateStore.CompleteWork))
                lock (attempts) attempts.Add(((WorkSelection)args[0]!).AnchorWorkId);
        };
        var events = new List<RunEvent>();
        try
        {
            await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        }
        finally { fixture.Hooks.BeforeCallWith = null; }
        Check.Require(attempts.Count(id => id == failing.AnchorWorkId) == 1 &&
            attempts.Count(id => id == healthy.AnchorWorkId) == 1,
            "B204-03: each responsibility reaches its completion mutation once");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) >= 2,
            "B204-03: the healthy sibling's retirement did justify a later pass");
        Check.Require(PendingAt(fixture, Side.Local, FailingCompletion).Count == 1,
            "B204-03: the suppressed item stays pending for a real next opportunity");
        RemoveCompletionFkFault(fixture.DatabasePath!);
        await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        Check.Require(PendingAt(fixture, Side.Local, FailingCompletion).Count == 0 &&
            fixture.Inner.Issues(PairId).Count == 0,
            "B204-03: the next ordinary opportunity completes the pending work and closes its Failure");
    }

    // 9.2/finding 1(1): admission refreshes the selection after it was taken. The certification belongs to the
    // begun responsibility, so only the explicit RecordTemporary(error.Selection) suppresses the later pass.
    private static void CompletionFailureRefreshedSelection()
    {
        using var fixture = new Fixture(realStore: true);
        var (failing, healthy) = SeedCompletionPair(fixture);
        var changed = LocalEntry(FailingCompletion, LocalId) with { Modified = Stamp.AddSeconds(1) };
        var remote = RemoteEntry(FailingCompletion, "remote-" + LocalId);
        InstallCompletionFkFault(fixture.DatabasePath!, failing.AnchorWorkId);
        var refreshed = false;
        var attempts = 0;
        WorkSelection? frame = null;
        var events = new List<RunEvent>();
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            if (method.Name == nameof(IStateStore.CompleteWork))
                lock (events) attempts += ((WorkSelection)args[0]!).AnchorWorkId == failing.AnchorWorkId ? 1 : 0;
            if (method.Name != nameof(IStateStore.BeginSelection) ||
                args[0] is not WorkSelection argument || argument.AnchorWorkId != failing.AnchorWorkId) return;
            lock (events)
            {
                frame ??= argument;
                if (refreshed) return;
                refreshed = true;
            }
            // A row settlement between selection and admission advances the preimage, so admission recomputes a
            // different obligation for the same work: the begun responsibility is not the frame's responsibility.
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(changed, remote)] });
        };
        try
        {
            LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default)
                .GetAwaiter().GetResult();
        }
        finally { fixture.Hooks.BeforeCallWith = null; }
        var certificate = Certificates(events);
        Check.Require(refreshed, "B204-10 setup: admission refreshed the selection after it was taken");
        Check.Require(certificate.Count == 1 && frame is { } admitted &&
            certificate[0].Selection.AnchorWorkId == admitted.AnchorWorkId &&
            certificate[0].Selection.Key == admitted.Key &&
            certificate[0].Selection.Identity == admitted.Identity &&
            certificate[0].Selection.Obligation != admitted.Obligation,
            "B204-10: the certificate names the refreshed responsibility, not the exact frame BeginSelection received");
        Check.Require(Volatile.Read(ref attempts) == 1,
            "B204-10: the actual failed responsibility executes once, although the healthy retirement forced another pass");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) >= 2 &&
            PendingAt(fixture, Side.Local, FailingCompletion).Count == 1 &&
            PendingAt(fixture, Side.Local, HealthyCompletion).Count == 0,
            "B204-10: the later pass skips the refreshed responsibility and the failed work stays pending");
    }

    // 9.2/finding 1(2): a completion failure on an ancestor frame retains and closes the dependent child's stack
    // without executing the child in this run; a retired sibling still forces the later pass. A bound folder has a
    // saved binding, so NeedsBatch does not schedule a discovery batch; the unbound variant documents the
    // legitimate early child publication and asserts nothing is published after the certification.
    private static async Task CompletionFailureAncestorDependentAsync()
    {
        foreach (var bound in new[] { true, false })
        {
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            fixture.Local.Seed(FolderEntry(Folder, FolderId), null);
            var child = LocalEntry(Plain, LocalId);
            fixture.Local.Seed(child, [1]);
            fixture.Dropbox.Seed(FolderEntry(Folder, DropboxId), null);
            if (bound)
                fixture.Inner.Commit(PairId, StateChange.Empty with
                {
                    Record = [SyncedEntries.From(FolderEntry(Folder, FolderId), FolderEntry(Folder, DropboxId))]
                });
            var selection = Selected(fixture, Plain, child);
            var ancestor = SeedRemote(fixture, FolderEntry(Folder, DropboxId));
            var other = RemoteEntry("Other.txt", "other-remote");
            fixture.Dropbox.Seed(other, [1]);
            SeedRemote(fixture, other);
            Check.Require(await fixture.Dropbox.ReadOneAsync(Plain, default) is null,
                "B204-11 setup: the remote child is absent before the first run; bound=" + bound);
            InstallCompletionFkFault(fixture.DatabasePath!, ancestor.AnchorWorkId);
            var attempts = new List<long>();
            var sequence = new List<string>();
            var publishes = 0;
            var publishesAfterCertificate = 0;
            var certified = false;
            fixture.Hooks.BeforeCallWith = (method, args) =>
            {
                if (method.Name == nameof(IStateStore.EndSelection))
                    lock (sequence) sequence.Add("end:" + ((WorkSelection)args[0]!).Key);
                else if (method.Name == nameof(IStateStore.EndWorkPass))
                    lock (sequence) sequence.Add("pass");
                else if (method.Name == nameof(IStateStore.CompleteWork))
                    lock (attempts) attempts.Add(((WorkSelection)args[0]!).AnchorWorkId);
            };
            fixture.DropboxView = new ProxySide(fixture.Dropbox)
            {
                AfterPublish = entry =>
                {
                    if (!PathNames.Same(entry.Path, Plain)) return;
                    Interlocked.Increment(ref publishes);
                    if (Volatile.Read(ref certified)) Interlocked.Increment(ref publishesAfterCertificate);
                }
            };
            var events = new List<RunEvent>();
            try
            {
                await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), events, configure: context => context with
                {
                    Events = item =>
                    {
                        lock (events) events.Add(item);
                        if (item.Error is ItemStoreMutationException) Volatile.Write(ref certified, true);
                    }
                }).SyncWorkAsync(default);
            }
            finally { fixture.Hooks.BeforeCallWith = null; }
            Check.Require(Certificates(events).Any(error => error.Selection.AnchorWorkId == ancestor.AnchorWorkId),
                "B204-11: the ancestor frame's completion is the exact certified selection; bound=" + bound);
            Check.Require(WorkRows(fixture, ancestor.AnchorWorkId) == 1,
                "B204-11: the ancestor work row remains; bound=" + bound);
            if (bound)
            {
                var firstPass = sequence.IndexOf("pass");
                var dependentEnded = sequence.IndexOf("end:" + selection.Key);
                Check.Require(dependentEnded >= 0 && firstPass >= 0 && dependentEnded < firstPass,
                    "B204-11: the dependent selection is cleaned up before the first EndWorkPass; bound=" + bound +
                    ": " + string.Join(",", sequence));
                Check.Require(publishes == 0 &&
                    await fixture.Dropbox.ReadOneAsync(Plain, default) is null &&
                    WorkRows(fixture, selection.AnchorWorkId) == 1 &&
                    PendingAt(fixture, Side.Local, Plain).Count == 1 &&
                    PendingAt(fixture, Side.Dropbox, Folder).Count == 1 &&
                    !attempts.Contains(selection.AnchorWorkId) &&
                    events.All(item => item.Error is not LiveAdmissionStaleException),
                    "B204-11: the dependent stays pending and retained, never a prerequisite clash; bound=" + bound +
                    "; publishes=" + publishes + "; pending=" + PendingAt(fixture, Side.Local, Plain).Count);
                RemoveCompletionFkFault(fixture.DatabasePath!);
                await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
                Check.Require(WorkRows(fixture, selection.AnchorWorkId) == 0 &&
                    WorkRows(fixture, ancestor.AnchorWorkId) == 0 &&
                    await fixture.Dropbox.ReadOneAsync(Plain, default) is not null,
                    "B204-11: after the fault is removed both original work ids retire and the child exists remotely");
            }
            else
            {
                Check.Require(publishes == 1 && publishesAfterCertificate == 0 &&
                    await fixture.Dropbox.ReadOneAsync(Plain, default) is not null,
                    "B204-11: an unbound folder publishes the child before its completion fault and never after the certification; " +
                    "publishes=" + publishes + "; after-certificate=" + publishesAfterCertificate);
            }
        }
    }


    // 9.3/finding 2: the fault is installed only once the admitted healthy lane is active at its staging barrier;
    // the failing lane waits for that barrier before its completion. Healthy then finishes without cancellation.
    private static async Task CompletionFailureConcurrentAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var (failing, _) = SeedCompletionPair(fixture);
        RequireForeignKeys(fixture);
        var admission = new FakeAdmission { Level = 2 };
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var staged = 0;
        fixture.DropboxView = new ProxySide(fixture.Dropbox)
        {
            BeforeStage = async (path, token) =>
            {
                if (path == FailingCompletion)
                {
                    // The completion fault exists only after the healthy lane reached its own barrier.
                    await held.Task.WaitAsync(token);
                    return;
                }
                if (path != HealthyCompletion) return;
                Interlocked.Increment(ref active);
                if (Interlocked.Exchange(ref staged, 1) == 0)
                {
                    InstallCompletionFkFault(fixture.DatabasePath!, failing.AnchorWorkId);
                    held.TrySetResult();
                }
                try { await release.Task.WaitAsync(token); }
                finally { Interlocked.Decrement(ref active); }
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
            await faulted.Task.WaitAsync(guard.Token);
            Check.Require(Volatile.Read(ref active) == 1,
                "B204-04: the healthy lane is active at the barrier when the fault is observed");
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

    // 9.2/finding 1(3): two concurrent, non-retiring completions fail; the run performs exactly one work pass, so a
    // failure-generated refusal signal would show up as an extra EndWorkPass.
    private static async Task CompletionFailureRefusalSignalAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        var first = SavedCompletion(fixture, FailingCompletion, LocalId, page: 1);
        var second = SavedCompletion(fixture, HealthyCompletion, "healthy-id", page: 2);
        InstallCompletionFkFault(fixture.DatabasePath!, first.AnchorWorkId, second.AnchorWorkId);
        var admission = new FakeAdmission { Level = 2 };
        var attempts = new List<long>();
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            if (method.Name == nameof(IStateStore.CompleteWork))
                lock (attempts) attempts.Add(((WorkSelection)args[0]!).AnchorWorkId);
        };
        var events = new List<RunEvent>();
        try
        {
            await LiveSyncer(fixture, first, new Boundary(_ => new Lease()), events, configure: context => context with
            {
                Admission = admission
            }).SyncWorkAsync(default);
        }
        finally { fixture.Hooks.BeforeCallWith = null; }
        Check.Require(Certificates(events).Count == 2,
            "B204-12: both concurrent non-retiring completions are contained");
        Check.Require(attempts.Count(id => id == first.AnchorWorkId) == 1 &&
            attempts.Count(id => id == second.AnchorWorkId) == 1,
            "B204-12: neither failed responsibility is re-executed");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) == 1,
            "B204-12: a contained failure raises no refusal signal and starts no extra work pass");
        Check.Require(PendingAt(fixture, Side.Local, FailingCompletion).Count == 1 &&
            PendingAt(fixture, Side.Local, HealthyCompletion).Count == 1 &&
            fixture.Inner.Issues(PairId, [IssueKind.Failure]).Count == 2,
            "B204-12: both failed items stay pending with one Failure each");
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
        Check.Require(Certificates(events).Count == 2,
            "B204-05: the reopened store contains the refusal again");
        RemoveCompletionFkFault(fixture.DatabasePath!);
        await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        Check.Require(PendingAt(fixture, Side.Local, FailingCompletion).Count == 0 &&
            fixture.Inner.Issues(PairId).Count == 0,
            "B204-05: without the fault the same pending work completes and the notice closes");
    }

    // 9.5/finding 4(c): out-of-scope completion is contained and suppressed across a justified later pass, with one
    // completion attempt for the failing responsibility.
    private static async Task CompletionFailureOutOfScopeAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        var failing = SavedCompletion(fixture, OutOfScopeCompletion, LocalId, page: 1);
        var healthy = SavedCompletion(fixture, HealthyCompletion, "healthy-id", page: 2);
        InstallCompletionFkFault(fixture.DatabasePath!, failing.AnchorWorkId);
        var candidates = new List<long>();
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            if (method.Name == nameof(IStateStore.CompleteOutOfScope))
                lock (candidates) candidates.Add(((WorkSelection)args[0]!).AnchorWorkId);
        };
        var events = new List<RunEvent>();
        try
        {
            await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events, configure: context => context with
            {
                Scope = new PathScope(fixture.Pair with { Exclusions = [new Exclusion(Side.Local, OutOfScopeCompletion, false)] }, [fixture.Pair])
            }).SyncWorkAsync(default);
        }
        finally { fixture.Hooks.BeforeCallWith = null; }
        Check.Require(events.Any(item => item.Error is ItemStoreMutationException { PrimaryCode: ConstraintPrimary }),
            "B204-06: an out-of-scope completion is contained through the same transaction owner");
        Check.Require(candidates.Count(id => id == failing.AnchorWorkId) == 1,
            "B204-06: the out-of-scope responsibility reaches its completion mutation once");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) >= 2 &&
            PendingAt(fixture, Side.Local, HealthyCompletion).Count == 0,
            "B204-06: the healthy sibling retires and justifies the later pass");
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

    // 9.7/9.8/finding 3: unclassified raw failures reach selection dispatch unchanged; a real code-8 refusal is
    // reached after transaction creation and validation (the DELETE itself); an aggregate carrying a certificate is
    // never unpacked into a contained success.
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
        var statements = new List<string>();
        ExecuteConnection(connection, "PRAGMA query_only=ON;");
        StartTrace(connection, statements);
        Exception? forbidden = null;
        try { readOnly.Inner.CompleteWork(blocked, null); }
        catch (Exception error) { forbidden = error; }
        finally
        {
            StopTrace(connection);
            ExecuteConnection(connection, "PRAGMA query_only=OFF;");
        }
        int[] sqliteCodes = forbidden is null ? [] : Flattened(forbidden).OfType<SqliteException>()
            .Select(error => error.SqliteErrorCode).Distinct().ToArray();
        Check.Require(forbidden is not null && sqliteCodes.SequenceEqual([ReadOnlyPrimary]) &&
            !Flattened(forbidden).OfType<ItemStoreMutationException>().Any(),
            "B204-08: a real non-constraint write refusal (native code 8) is not contained");
        string[] validation =
        [
            TransactionBegin, StateSchema.PairFence, StateSchema.SourceFence, StateSchema.Anchor,
            StateSchema.WorkIssue, StateSchema.ItemEpisodeByWork, StateSchema.ReadRenameContinuation,
            StateSchema.CompleteIdentitySelection, TransactionRollback
        ];
        var traced = NormalizedTraced(statements, validation);
        Check.Require(traced.SequenceEqual(validation) || traced.SequenceEqual(validation[..^1]),
            "B204-08: the transaction began, every validation read ran, and the code-8 refusal reached the retirement DELETE: " +
            string.Join(" | ", traced));

        using var aggregateFixture = new Fixture(realStore: true);
        var aggregated = SeedSingleCompletion(aggregateFixture, FailingCompletion, LocalId);
        InstallCompletionFkFault(aggregateFixture.DatabasePath!, aggregated.AnchorWorkId);
        var firstRun = new List<RunEvent>();
        await LiveSyncer(aggregateFixture, aggregated, new Boundary(_ => new Lease()), firstRun).SyncWorkAsync(default);
        var certificate = Certificates(firstRun).Single();
        var before = aggregateFixture.Inner.Issues(PairId, [IssueKind.Failure]).Single();
        var secondary = new InvalidOperationException("synthetic-cleanup-failure");
        aggregateFixture.Hooks.OnReadWork = _ => throw new AggregateException(certificate, secondary);
        Exception? aggregateEscaped = null;
        try { await LiveSyncer(aggregateFixture, aggregated, new Boundary(_ => new Lease()), []).SyncWorkAsync(default); }
        catch (Exception error) { aggregateEscaped = error; }
        finally { aggregateFixture.Hooks.OnReadWork = null; }
        IReadOnlyList<Exception> flattened = aggregateEscaped is null ? [] : Flattened(aggregateEscaped);
        Check.Require(flattened.Contains(certificate) && flattened.Contains(secondary),
            "B204-08: a certificate nested in a cleanup aggregate is delivered as both failures, never unpacked");
        Check.Require(aggregateFixture.Inner.Issues(PairId, [IssueKind.Failure]).Single().Id == before.Id &&
            PendingAt(aggregateFixture, Side.Local, FailingCompletion).Count == 1,
            "B204-08: the aggregate opens no new notice settlement and keeps the work retained");

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

    // 10/finding 6(A): an anchor a live episode names, with no issue member row, is retained by CompleteWork.
    private static void CompletionFailureEpisodeOwnedAnchor()
    {
        var attempt = Guid.NewGuid();
        using var fixture = new Fixture(realStore: true, registeredAttempt: attempt);
        var selection = SeedSingleCompletion(fixture, FailingCompletion, LocalId);
        var issue = fixture.Inner.RecordInUseFailure(PairId, Generation, selection.SourceEpoch, FailingCompletion,
            new IssueSubject([new IssueItem(Side.Local, FailingCompletion, LocalId)], null), TextKey.FailLocalInUse,
            selection, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        ExecuteOnStore(fixture, "DELETE FROM issue_member WHERE work_id=$work;", ("$work", selection.AnchorWorkId));
        Check.Require(MemberRows(fixture, selection.AnchorWorkId) == 0 &&
            fixture.Inner.ReadItemEpisodeIssues(PairId).Contains(issue.Token.IssueId),
            "B204-13 setup: the anchor is episode-owned with no issue member row");
        bool completed;
        try { completed = fixture.Inner.CompleteWork(selection, null); }
        catch (Exception error) { throw new InvalidOperationException("B204-13: an episode-owned anchor must be refused", error); }
        Check.Require(!completed && WorkRows(fixture, selection.AnchorWorkId) == 1,
            "B204-13: the episode-owned anchor returns false and stays retained for its own retry");
    }

    // 9.9/finding 4(a): the fault survives a new ordinary opportunity, so the row settlement closes the generic
    // Failure and the still-pending responsibility fails again. No automatic extra opportunity is created.
    private static async Task CompletionFailureChurnAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var failing = SeedSingleCompletion(fixture, FailingCompletion, LocalId);
        InstallCompletionFkFault(fixture.DatabasePath!, failing.AnchorWorkId);
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        var first = fixture.Inner.Issues(PairId, [IssueKind.Failure]).Single();
        Check.Require(PendingAt(fixture, Side.Local, FailingCompletion).Count == 1 &&
            fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) == 1,
            "B204-09: the first contained failure stores one current Failure in one work pass");
        // The next ordinary opportunity's successful row settlement must remove that Failure before the retried
        // completion faults again; the witness observes the store between the two events.
        var closedBeforeFault = false;
        fixture.Hooks.AfterCall = (name, args, _) =>
        {
            if (name != nameof(IStateStore.Commit) || args.Length != 5 ||
                args[1] is not StateChange settlement || settlement.Record.Count == 0 || closedBeforeFault) return;
            closedBeforeFault = !fixture.Inner.Issues(PairId, [IssueKind.Failure]).Any(issue => issue.Id == first.Id);
        };
        try
        {
            await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        }
        finally { fixture.Hooks.AfterCall = null; }
        var current = fixture.Inner.Issues(PairId, [IssueKind.Failure]);
        Check.Require(closedBeforeFault,
            "B204-09: the successful row settlement removes the old generic Failure before the next completion fault");
        Check.Require(current.Count == 1 && current[0].Cause == TextKey.FailStoreMutation &&
            PendingAt(fixture, Side.Local, FailingCompletion).Count == 1,
            "B204-09: a new ordinary opportunity closes and re-raises one current Failure while the work stays pending");
        Check.Require(Certificates(events).Count == 2 &&
            fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) == 2,
            "B204-09: completion failed twice, and no automatic additional opportunity ran");
        RemoveCompletionFkFault(fixture.DatabasePath!);
        await LiveSyncer(fixture, failing, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        Check.Require(PendingAt(fixture, Side.Local, FailingCompletion).Count == 0 &&
            fixture.Inner.Issues(PairId).Count == 0,
            "B204-09: after the fault is removed the pending work completes and the notice closes");
    }

    // 11/finding 5: five completion shapes through the forwarding store, each warmed once and measured once. The
    // expected ordered SQL lists are derived from 709113313 source (round-3 report); the probe asserts the candidate
    // equals them exactly, with exact delete counts and completed/retained row counts. Allocations are
    // candidate-only: at the base a successful completion issues one BEGIN/COMMIT, reads no sqlite_sequence row, and
    // the ownership refusal opens a transaction but commits and deletes nothing.
    private static async Task CompletionFailureCostAsync()
    {
        Console.WriteLine($"b204-temporary-lookups call-sites={TemporaryLookupCallSites} " +
            $"per-call-ordinary={TemporaryLookupsPerCallOrdinary} " +
            $"per-call-with-temporary={TemporaryLookupsPerCallWithTemporary} " +
            $"per-selection-ordinary={TemporaryLookupCallSites * TemporaryLookupsPerCallOrdinary} " +
            $"per-selection-with-temporary={TemporaryLookupCallSites * TemporaryLookupsPerCallWithTemporary} " +
            $"per-selection-base={TemporaryLookupCallSites * TemporaryLookupsPerCallBase}");
        Check.Require(TemporaryLookupCallSites * TemporaryLookupsPerCallOrdinary == 0 &&
            TemporaryLookupCallSites * TemporaryLookupsPerCallWithTemporary == 4 &&
            TemporaryLookupCallSites * TemporaryLookupsPerCallBase == 2,
            "B204-14: ordinary selections stay at zero probes; a run with a temporary record performs two per call at two call sites");
        foreach (var shape in new[] { "identity", "path-only", "out-of-scope", "owned-peers", "ownership-refusal" })
        {
            var measured = CostShape(shape);
            var expected = ExpectedCompletionSql(shape);
            var identified = measured.Sql.Select(IdentifySql).ToList();
            var (completedRows, retainedRows, deletedRows) = shape switch
            {
                "owned-peers" => (2, 0, 2),
                "ownership-refusal" => (0, 1, 0),
                // StateStore.CompleteOutOfScope short-circuits on CoversKey, which ignores ignore rules, so this
                // ignore-rule scope performs no SQL and retains the row (base 709113313: StateStore.cs:4028).
                "out-of-scope" => (0, 1, 0),
                _ => (1, 0, 1)
            };
            Console.WriteLine($"b204-cost shape={shape} calls={measured.Calls} returns={measured.Returned} " +
                $"sql-statements={identified.Count} begin={identified.Count(item => item == TransactionBegin)} " +
                $"commit={identified.Count(item => item == TransactionCommit)} " +
                $"rollback={identified.Count(item => item == TransactionRollback)} " +
                $"deletes={identified.Count(item => item.Contains("DELETE FROM change_work", StringComparison.OrdinalIgnoreCase))} " +
                $"completed-rows={measured.CompletedRows} retained-rows={measured.RetainedRows} " +
                $"provider-effects={measured.ProviderEffects} allocated-bytes-candidate-only={measured.Allocated}");
            for (var index = 0; index < identified.Count; index++)
                Console.WriteLine($"b204-sql shape={shape} #{index} {identified[index]}");
            Check.Require(measured.Calls == (shape == "out-of-scope" ? "CompleteOutOfScope" : "CompleteWork"),
                "B204-14/" + shape + ": the forwarding store saw exactly its own completion call");
            Check.Require(measured.CompletedRows == completedRows && measured.RetainedRows == retainedRows,
                "B204-14/" + shape + ": completed and retained rows are " + completedRows + "/" + retainedRows);
            // Only transaction markers and the legacy trigger comments are trace artifacts; every other statement
            // must name a source-derived template or the test fails. Trigger-step repeats are collapsed only when
            // the run of identical statements is followed by trigger comment artifacts.
            var authored = NormalizedTraced(measured.Sql, expected);
            if (expected.Length == 0)
                Check.Require(measured.Sql.Count == 0,
                    "B204-14/" + shape + ": the source-derived budget is zero SQL; captured: " + string.Join(" | ", measured.Sql));
            Check.Require(authored.SequenceEqual(expected),
                "B204-14/" + shape + ": the Storage-authored ordered statements equal the 709113313 derivation; captured: " +
                string.Join(" | ", authored) + "; expected: " + string.Join(" | ", expected));
            Check.Require(authored.Count(item => item.Contains("DELETE FROM change_work", StringComparison.OrdinalIgnoreCase)) == deletedRows,
                "B204-14/" + shape + ": the retirement delete count is exactly " + deletedRows);
            Check.Require(measured.ProviderEffects == 0,
                "B204-14/" + shape + ": a store-only completion makes no provider or filesystem call");
        }
        await Task.CompletedTask;
    }

    // The ordered statements each shape must execute, derived from 709113313 (round-3 report table). Transaction
    // markers normalize the provider BEGIN/COMMIT/ROLLBACK text.
    private static string[] ExpectedCompletionSql(string shape)
    {
        var completed = shape == "path-only" ? StateSchema.CompletePathSelection : StateSchema.CompleteIdentitySelection;
        return shape switch
        {
            // The ignore-rule scope does not cover the key, so the store returns false before any SQL.
            "out-of-scope" => [],
            "owned-peers" =>
            [
                TransactionBegin, StateSchema.PairFence, StateSchema.SourceFence, StateSchema.Anchor,
                StateSchema.WorkIssue, StateSchema.ItemEpisodeByWork, StateSchema.ReadRenameContinuation,
                StateSchema.CompleteIdentitySelection, StateSchema.SourceFence, StateSchema.OwnedPeerPathPage,
                StateSchema.ReadRenameContinuation, StateSchema.DeleteWork, StateSchema.OwnedPeerPathPage, TransactionCommit
            ],
            "ownership-refusal" =>
            [
                TransactionBegin, StateSchema.PairFence, StateSchema.SourceFence, StateSchema.Anchor,
                StateSchema.WorkIssue, TransactionRollback
            ],
            _ =>
            [
                TransactionBegin, StateSchema.PairFence, StateSchema.SourceFence, StateSchema.Anchor,
                StateSchema.WorkIssue, StateSchema.ItemEpisodeByWork, StateSchema.ReadRenameContinuation,
                completed, TransactionCommit
            ]
        };
    }

    private static (string Calls, bool Returned, List<string> Sql, int CompletedRows, int RetainedRows,
        long ProviderEffects, long Allocated) CostShape(string shape)
    {
        // One warm-up fixture and one measured fixture: each shape gets exactly one warm-up and one measure.
        _ = CostRun(shape);
        return CostRun(shape);
    }

    private static (string Calls, bool Returned, List<string> Sql, int CompletedRows, int RetainedRows,
        long ProviderEffects, long Allocated) CostRun(string shape)
    {
        using var fixture = new Fixture(realStore: true, registeredAttempt: AttemptId);
        var local = new CountingSide(fixture.Local);
        var remote = new CountingSide(fixture.Dropbox);
        fixture.LocalView = local;
        fixture.DropboxView = remote;
        var scope = new PathScope(fixture.Pair with { IgnoreRules = ["FileName:" + PathCompletion] }, [fixture.Pair]);
        // Set the shape up before tracing: only the completion call itself is measured.
        Func<bool> call;
        long[] involved;
        switch (shape)
        {
            case "identity":
            {
                var selection = SavedCompletion(fixture, PathCompletion, LocalId, page: 1);
                involved = [selection.AnchorWorkId];
                call = () => fixture.Store.CompleteWork(selection, null);
                break;
            }
            case "path-only":
            {
                SavePathBaseline(fixture);
                var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, PathCompletion, null,
                    null, LocalEntry(PathCompletion, LocalId) with { Modified = Stamp.AddSeconds(1) }, false, false));
                involved = [selection.AnchorWorkId];
                call = () => fixture.Store.CompleteWork(selection, null);
                break;
            }
            case "out-of-scope":
            {
                var selection = SavedCompletion(fixture, PathCompletion, LocalId, page: 1);
                involved = [selection.AnchorWorkId];
                call = () => fixture.Store.CompleteOutOfScope(selection, scope);
                break;
            }
            case "owned-peers":
            {
                var (selection, peer) = PeerSelection(fixture);
                involved = [selection.AnchorWorkId, peer];
                call = () => fixture.Store.CompleteWork(selection, selection.Peer);
                break;
            }
            case "ownership-refusal":
            {
                var selection = SavedCompletion(fixture, PathCompletion, LocalId, page: 1);
                fixture.Inner.RecordInUseFailure(PairId, Generation, selection.SourceEpoch, PathCompletion,
                    new IssueSubject([new IssueItem(Side.Local, PathCompletion, LocalId)], null),
                    TextKey.FailLocalInUse, selection, ItemRetryDispatch.OrdinaryItem, null, null, AttemptId);
                involved = [selection.AnchorWorkId];
                call = () => fixture.Store.CompleteWork(selection, null);
                break;
            }
            default: throw new ArgumentException(nameof(shape));
        }
        var before = involved.Count(id => WorkRows(fixture, id) == 1);
        var calls = new List<string>();
        fixture.Hooks.BeforeCallWith = (method, _) => { lock (calls) calls.Add(method.Name); };
        var sql = new List<string>();
        var connection = StoreConnection(fixture);
        StartTrace(connection, sql);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        bool returned;
        try { returned = call(); }
        finally
        {
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            StopTrace(connection);
            fixture.Hooks.BeforeCallWith = null;
        }
        var after = involved.Count(id => WorkRows(fixture, id) == 1);
        var providerEffects = (long)local.Reads + local.Listings + local.Hashes + local.Work +
            remote.Reads + remote.Listings + remote.Hashes + remote.Work;
        return (string.Join(",", calls), returned, sql, before - after, after, providerEffects, allocated);
    }

    // A path-only row has no identity; its saved baseline is by key.
    private static void SavePathBaseline(Fixture fixture)
    {
        var saved = LocalEntry(PathCompletion, LocalId);
        var remote = RemoteEntry(PathCompletion, "remote-" + LocalId);
        fixture.Dropbox.Seed(remote, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(saved, remote)] });
        fixture.Local.Seed(saved with { Modified = Stamp.AddSeconds(1) }, [2]);
    }

    // The peer shape of PeerPreimageAdvance: the peer is acquired before the watermark, so it is inside the fence,
    // and no foreign claim excludes it. Both the primary and the peer row retire.
    private static (WorkSelection Selected, long PeerWorkId) PeerSelection(Fixture fixture)
    {
        ClearObligations(fixture);
        var local = LocalEntry(Item, LocalId);
        var remote = RemoteEntry(Item, DropboxId);
        var before = SyncedEntries.From(local, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [before] });
        var own = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, local, true, false));
        Check.Require(fixture.Inner.BeginSelection(own) is not null, "B204-14 setup: the own selection is admitted");
        var other = SeedRemote(fixture, remote);
        var watermark = fixture.Inner.ReadWorkWatermark();
        Check.Require(watermark == other.AnchorWorkId, "B204-14 setup: the peer watermark is the acquired peer row");
        var selected = own with { Peer = new SelectionPeer(PathNames.KeyFor(Side.Dropbox, Item), watermark) };
        var after = SyncedEntries.From(local, remote with { ContentHash = DropboxContentHash.Compute(new byte[] { 2 }) });
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [after] }, selected, null);
        return (selected, other.AnchorWorkId);
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

    private static List<ItemStoreMutationException> Certificates(IEnumerable<RunEvent> events) =>
        events.Select(item => item.Error).OfType<ItemStoreMutationException>().ToList();

    // A test-only child table's missing parent is a real SQLite foreign-key refusal on the chosen work rows.
    private static void InstallCompletionFkFault(string database, params long[] workIds) => ExecuteDatabase(database,
        "CREATE TABLE " + FaultParent + "(id INTEGER PRIMARY KEY);" +
        "CREATE TABLE " + FaultChild + "(parent_id INTEGER NOT NULL REFERENCES " + FaultParent + "(id) ON DELETE NO ACTION);" +
        "CREATE TRIGGER " + FaultTrigger + " AFTER DELETE ON change_work WHEN " +
        string.Join(" OR ", workIds.Select(id => "old.work_id=" + id)) +
        " BEGIN INSERT INTO " + FaultChild + "(parent_id) VALUES(" + MissingParent + "); END;");

    private static void RemoveCompletionFkFault(string database) => ExecuteDatabase(database,
        "DROP TRIGGER IF EXISTS " + FaultTrigger + ";" +
        "DROP TABLE IF EXISTS " + FaultChild + ";" +
        "DROP TABLE IF EXISTS " + FaultParent + ";");

    private static long MemberRows(Fixture fixture, long workId) => (long)ExecuteScalarOnStore(fixture,
        "SELECT count(*) FROM issue_member WHERE work_id=$work;", ("$work", workId))!;

    private static long WorkRows(Fixture fixture, long workId) => (long)ExecuteScalarOnStore(fixture,
        "SELECT count(*) FROM change_work WHERE work_id=$work;", ("$work", workId))!;

    private static string IdentifySql(string sql)
    {
        var text = sql.TrimStart();
        if (text.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)) return TransactionBegin;
        if (text.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)) return TransactionCommit;
        if (text.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase)) return TransactionRollback;
        return sql;
    }

    // sqlite3_trace expands bound values, so a traced statement is identified by the template prefix before its
    // first parameter. The longest matching expected template wins.
    private static string AuthoredIdentity(string sql, string[] expected)
    {
        var text = sql.TrimStart();
        if (text.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)) return TransactionBegin;
        if (text.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)) return TransactionCommit;
        if (text.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase)) return TransactionRollback;
        var width = 0;
        var match = "";
        foreach (var candidate in expected)
        {
            var prefix = ParameterPrefix(candidate);
            if (prefix.Length < 8 || prefix.Length <= width || !text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            width = prefix.Length;
            match = candidate;
        }
        return match;
    }

    private static string ParameterPrefix(string sql)
    {
        var index = sql.IndexOf('$', StringComparison.Ordinal);
        return index < 0 ? sql : sql[..index];
    }

    // Legacy sqlite3_trace expands bound values. Its only non-statement artifacts are the transaction markers and,
    // for a statement that fires a trigger, the outer-statement repeats followed by "-- TRIGGER" / "-- <program>"
    // comment lines. Any other unmatched statement fails the probe; repeats without those artifacts are real
    // re-executions and are kept, so the ordered sequence and count differ.
    private static List<string> NormalizedTraced(List<string> raw, string[] expected)
    {
        var entries = new List<(string Identity, bool Artifact)>();
        var unexpected = new List<string>();
        foreach (var statement in raw)
        {
            var text = statement.TrimStart();
            if (text.Length == 0 || text.StartsWith("--", StringComparison.Ordinal))
            {
                entries.Add((string.Empty, true));
                continue;
            }
            var identity = AuthoredIdentity(statement, expected);
            if (identity.Length == 0) unexpected.Add(statement);
            else entries.Add((identity, false));
        }
        Check.Require(unexpected.Count == 0,
            "B204 trace: application SQL outside the source derivation: " + string.Join(" ; ", unexpected));
        var normalized = new List<string>();
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].Artifact) continue;
            var identity = entries[index].Identity;
            var run = index + 1;
            while (run < entries.Count && !entries[run].Artifact && entries[run].Identity == identity) run++;
            var triggerEnd = run;
            while (triggerEnd < entries.Count && entries[triggerEnd].Artifact) triggerEnd++;
            if (triggerEnd > run)
            {
                normalized.Add(identity);
                index = triggerEnd - 1;
            }
            else
            {
                for (var repeat = index; repeat < run; repeat++) normalized.Add(identity);
                index = run - 1;
            }
        }
        return normalized;
    }

    private static void RequireForeignKeys(Fixture fixture) => Check.Require(
        (long)ExecuteScalarOnStore(fixture, "PRAGMA foreign_keys;")! == 1,
        "B204 setup: the store connection enforces foreign keys");

    private static bool TriggerVisible(Fixture fixture) => (long)ExecuteScalarOnStore(fixture,
        "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name=$name;", ("$name", FaultTrigger))! == 1;

    private static string Describe(RunEvent item) =>
        item.Kind + ":" + (item.Error?.GetType().Name ?? "-") + ":" + item.Path;

    private static void StartTrace(SqliteConnection connection, List<string> statements) => SQLitePCL.raw.sqlite3_trace(
        connection.Handle, (SQLitePCL.delegate_trace)((_, sql) =>
        {
            var text = sql.utf8_to_string();
            lock (statements) statements.Add(text);
        }), null);

    private static void StopTrace(SqliteConnection connection) =>
        SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null);

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

    private static readonly Guid AttemptId = Guid.NewGuid();
}
