using System.Reflection;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks for SelectionPass (F062): the captured selection generation reaches every admitted sync route
/// unchanged, a stale admission refuses without an effect or settlement, SaveBefore precedes coverage capture, and
/// the committed effective Issue reaches the exact selection-bound residual continuation.</summary>
public static class ChangeRunnerChecks
{
    private const string PairId = "pair-a";
    // The pair's local root serial (1) is the journal domain the executor's evidence reads.
    private const string VolumeKey = "1";
    private const long Generation = 0;
    private const string Item = "item";
    private const string Folder = "folder";
    private const string OldPath = "old-item";
    private const string SourcePath = "src-item";
    private const string DestinationPath = "dst-item";
    private const string LocalId = "local-id";
    private const string DropboxId = "dropbox-id";
    private const string OccupantId = "occupant-id";
    private const string Cursor = "cursor-a";
    private const int LogBufferCharacters = 256;
    private const string CommitFault = "commit-fixture";
    private const string AttachFault = "attach-fixture";
    private const int OccupantReadOrdinal = 3;

    private static readonly DateTimeOffset Stamp = DateTimeOffset.UnixEpoch.AddDays(1);

    public static void RunStaleFreshness() => StaleFreshnessAsync().GetAwaiter().GetResult();

    public static void ReleasedHistory() => ReleasedHistoryAsync().GetAwaiter().GetResult();

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    public static void RunContainment() => ContainmentAsync().GetAwaiter().GetResult();

    public static void RunMoveOutSafety() => SavedUnderAddedFolderAsync().GetAwaiter().GetResult();

    public static void RunRepairHeld() => RepairHeldAsync().GetAwaiter().GetResult();

    // R6-Q1/Q-R6-2: while Local skipped-coverage or lost-interval debt is owed, a Local edit inside that interval
    // keeps its size and stamp, so a live Dropbox update or delete must not overwrite or recycle it; the work stays
    // pending until Repair clears the debt. A Dropbox-only effect of the same pair still proceeds.
    private static async Task RepairHeldAsync()
    {
        foreach (var debt in new[] { SourceObligation.LocalSkippedCoverage, SourceObligation.NormalRepair })
        foreach (var kind in new[] { ChangeKind.Changed, ChangeKind.Deleted })
        {
            using var fixture = new Fixture(realStore: true);
            var local = LocalEntry(Item, LocalId);
            var remote = RemoteEntry(Item, DropboxId);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
            fixture.Local.Seed(local, [2]);
            var edited = remote with { Revision = "rev-2", ContentHash = DropboxContentHash.Compute(new byte[] { 3 }) };
            if (kind == ChangeKind.Changed) fixture.Dropbox.Seed(edited, [3]);
            var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
            fixture.Inner.AcquireJournalPage(VolumeKey, null, new JournalPosition(1, 1),
                [(PairId, Generation, source.Epoch, source.Revision, false)], []);
            var selected = SeedRemote(fixture, edited, new ChangeNotice(Side.Dropbox, kind, Item, null, DropboxId,
                kind == ChangeKind.Changed ? edited : null, false, false));
            RepairDone(fixture);
            ClearObligations(fixture);
            OweLocal(fixture, debt);
            var localCounter = new CountingSide(fixture.Local);
            fixture.LocalView = localCounter;
            LiveAdmissionStaleException? refused = null;
            try { await RunSelectionAsync(fixture, selected); }
            catch (LiveAdmissionStaleException error) { refused = error; }
            Check.Require(localCounter.Stages == 0,
                "R6-7 already-refused download stages zero bytes: " + debt + "/" + kind +
                "; stages=" + localCounter.Stages);
            Check.Require(refused is { Reason: LiveAdmissionStaleReason.CoverageChanged } && LocalWrites(localCounter) == 0 &&
                PendingAt(fixture, Side.Dropbox, Item).Count == 1,
                "R6 Local " + debt + " debt refuses a live Dropbox " + kind + " over the Local item and keeps the work: refused=" +
                refused?.Reason + "; localEffects=" + LocalWrites(localCounter));
            // Only Repair's matching completion clears skipped coverage; it covers the retained work.
            var through = fixture.Inner.ReadWorkWatermark();
            var token = fixture.Inner.BeginScan(PairId, Guid.NewGuid(), Generation, false);
            var gap = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
            var cursor = fixture.Inner.ReadSource(PairId, Side.Dropbox)!.Value;
            fixture.Inner.FinishRepair(PairId, Storage.CompactStorageChecks.Summary(), Generation, token,
                new LocalCatchUp(PairId, Generation, gap.Epoch, new NtfsUsnCheckpoint(1, 1, 1, 1), through),
                new DropboxCatchUp(PairId, Generation, cursor.Epoch, cursor.Cursor!, through));
            fixture.Inner.EndScanAsync(PairId, token, 1).GetAwaiter().GetResult();
            Check.Require(fixture.Inner.ReadLiveRefusal(PairId) == LiveRefusal.None,
                "R6 Repair's completion lifts the Local-write refusal: " + debt + "/" + kind);
            var later = remote with { Revision = "rev-3", ContentHash = DropboxContentHash.Compute(new byte[] { 4 }) };
            fixture.Dropbox.Seed(later, [4]);
            var next = SeedRemote(fixture, later);
            Check.Require(await RunSelectionAsync(fixture, next) && localCounter.Work > 0,
                "R6 after Repair's completion a live Dropbox change writes Local again: " + debt + "/" + kind);
        }
        {
            using var fixture = new Fixture(realStore: true);
            var local = LocalEntry(Item, LocalId);
            var remote = RemoteEntry(Item, DropboxId);
            fixture.Dropbox.Seed(remote, [1]);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
            var changed = local with { Size = 2, Modified = Stamp.AddSeconds(5) };
            fixture.Local.Seed(changed, [2, 2]);
            var selected = Selected(fixture, Item, changed);
            RepairDone(fixture);
            ClearObligations(fixture);
            OweLocal(fixture, SourceObligation.LocalSkippedCoverage);
            var remoteCounter = new CountingSide(fixture.Dropbox);
            fixture.DropboxView = remoteCounter;
            Check.Require(await RunSelectionAsync(fixture, selected) && remoteCounter.Work > 0,
                "R6 a Dropbox-only effect proceeds while Local gap debt is owed");
        }
        await RepairHeldBatchAsync(fallback: false);
        await RepairHeldBatchAsync(fallback: true);
        await RepairHeldRenameAdmissionAsync();
        await RepairHeldFolderAdmissionAsync();
        await RepairHeldRenameOwnerAsync(caseRename: true);
        await RepairHeldRenameOwnerAsync(caseRename: false);
        await RepairHeldFreshnessAsync();
        foreach (var boundary in new[] { "stage", "replace-turn", "delete-turn" })
            await RepairHeldEffectBoundaryAsync(boundary);
        await RepairHeldRenameParentAsync();
        foreach (var twoParents in new[] { false, true })
            await RepairHeldFolderParentAsync(twoParents);
        await RepairHeldCaptureAsync(afterFreeze: false);
        await RepairHeldCaptureAsync(afterFreeze: true);
        await RepairHeldSiblingAsync();
        await RepairHeldConcurrentPassAsync();
    }

    private static async Task RepairHeldConcurrentPassAsync()
    {
        foreach (var debt in new[] { SourceObligation.LocalSkippedCoverage, SourceObligation.NormalRepair })
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var fixture = new Fixture(realStore: true);
            WorkSelection? opportunity = null;
            foreach (var path in new[] { "First", "Second" })
            {
                var local = LocalEntry(path, "local-" + path);
                var remote = RemoteEntry(path, "remote-" + path);
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
                fixture.Local.Seed(local, [2]);
                var edited = remote with { Revision = "rev-2", ContentHash = DropboxContentHash.Compute(new byte[] { 3 }) };
                fixture.Dropbox.Seed(edited, [3]);
                var work = SeedRemote(fixture, edited);
                opportunity ??= work;
            }
            SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
            RepairDone(fixture);
            ClearObligations(fixture);
            OweLocal(fixture, debt);
            var localCalls = new CountingSide(fixture.Local);
            var remoteCalls = new CountingSide(fixture.Dropbox);
            fixture.LocalView = localCalls;
            fixture.DropboxView = remoteCalls;
            var rounds = 0;
            var captures = 0;
            var refusals = 0;
            var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var boundary = new Boundary(_ => new Lease())
            {
                Capture = async (selection, _, token) =>
                {
                    if (Interlocked.Increment(ref captures) == 2) both.TrySetResult();
                    await both.Task.WaitAsync(token);
                    return Coverage(selection);
                }
            };
            var store = Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore.Wrap(fixture.Inner);
            ((Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore)store).Before = method =>
            {
                if (method.Name == nameof(IStateStore.EndWorkPass)) Interlocked.Increment(ref rounds);
            };
            var context = fixture.Context(opportunity!, new FakeAdmission { Level = 2 }) with
            {
                Store = store,
                Events = value =>
                {
                    if (value.Error is LiveAdmissionStaleException error && ItemSync.LiveWriteRefused(error))
                        Interlocked.Increment(ref refusals);
                }
            };
            var syncer = new Syncer(context, new SyncPorts(new RecoveryBoundary(), boundary,
                RefusingExecutionBoundary.Instance, 1));
            // A later wake may try the pending work again, but debt is never contention within either wake.
            for (var wake = 1; wake <= 2; wake++)
            {
                await syncer.SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
                Check.Require(rounds == wake && captures == 2 * wake && refusals == 2 * wake &&
                    LocalWrites(localCalls) == 0 && PendingAt(fixture, Side.Dropbox, "First").Count == 1 &&
                    PendingAt(fixture, Side.Dropbox, "Second").Count == 1,
                    "Local debt does not repeat concurrent work: debt=" + debt + "; wake=" + wake +
                    "; rounds=" + rounds + "; captures=" + captures + "; refusals=" + refusals);
            }
            Console.WriteLine("Local debt two-lane counts: debt=" + debt + "; wakes=2; rounds=" + rounds +
                "; localReads=" + localCalls.Reads + "; remoteReads=" + remoteCalls.Reads +
                "; localWrites=" + LocalWrites(localCalls));
        }
    }

    // R6-3: a Local loss that arrives during the admitted effect's freshness read still refuses the Local write.
    private static async Task RepairHeldFreshnessAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var local = LocalEntry(Item, LocalId);
        var remote = RemoteEntry(Item, DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
        fixture.Local.Seed(local, [2]);
        var edited = remote with { Revision = "rev-2", ContentHash = DropboxContentHash.Compute(new byte[] { 3 }) };
        fixture.Dropbox.Seed(edited, [3]);
        var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
        fixture.Inner.AcquireJournalPage(VolumeKey, null, new JournalPosition(1, 1),
            [(PairId, Generation, source.Epoch, source.Revision, false)], []);
        var selected = SeedRemote(fixture, edited);
        RepairDone(fixture);
        ClearObligations(fixture);
        var localCounter = new CountingSide(fixture.Local);
        fixture.LocalView = localCounter;
        var admitted = false;
        var lost = false;
        // The first Dropbox read inside the admission is the effect's freshness read (IsFreshAsync).
        fixture.DropboxView = new ProxySide(fixture.Dropbox)
        {
            BeforeRead = (path, _) =>
            {
                if (admitted && !lost && path == Item)
                {
                    lost = true;
                    OweLocal(fixture, SourceObligation.NormalRepair);
                }
                return Task.CompletedTask;
            }
        };
        LiveAdmissionStaleException? refused = null;
        try
        {
            Check.Require(await Runner(fixture, selected, new Boundary(_ =>
            {
                admitted = true;
                return new Lease();
            }), (selection, _, _) => Task.FromResult(Coverage(selection))).RunAsync(selected, default) is not null, "Selection must be admitted.");
        }
        catch (LiveAdmissionStaleException error) { refused = error; }
        Check.Require(lost && refused is not null && ItemSync.LiveWriteRefused(refused) &&
            LocalWrites(localCounter) == 0 && PendingAt(fixture, Side.Dropbox, Item).Count == 1,
            "R6-3 a Local loss during the freshness read refuses the live Local write and keeps the work: lost=" + lost +
            "; refused=" + refused?.Detail + "; localWrites=" + LocalWrites(localCounter));
    }

    private static async Task RepairHeldEffectBoundaryAsync(string boundary)
    {
        using var fixture = new Fixture(realStore: true);
        SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
        var local = LocalEntry(Item, LocalId);
        var remote = RemoteEntry(Item, DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
        fixture.Local.Seed(local, [1]);
        var edited = remote with { Revision = "rev-2", ContentHash = DropboxContentHash.Compute(new byte[] { 3 }) };
        var deleting = boundary == "delete-turn";
        if (!deleting) fixture.Dropbox.Seed(edited, [3]);
        var selected = SeedRemote(fixture, edited, new ChangeNotice(Side.Dropbox,
            deleting ? ChangeKind.Deleted : ChangeKind.Changed, Item, null, DropboxId,
            deleting ? null : edited, false, false));
        RepairDone(fixture);
        ClearObligations(fixture);
        var localCounter = new CountingSide(fixture.Local);
        var remoteCounter = new CountingSide(fixture.Dropbox);
        fixture.DropboxView = remoteCounter;
        var lost = false;
        void Lose()
        {
            lost = true;
            OweLocal(fixture, SourceObligation.NormalRepair);
        }
        fixture.LocalView = new ProxySide(localCounter)
        {
            AfterStage = _ => { if (boundary == "stage") Lose(); }
        };
        IAsyncDisposable? held = boundary == "stage" ? null : await fixture.Runtime.EnterAsync(default);
        LiveAdmissionStaleException? refused = null;
        try
        {
            var run = RunSelectionAsync(fixture, selected);
            if (held is not null)
            {
                Check.Require(!run.IsCompleted && (deleting || localCounter.Stages == 1),
                    "R6-3 fixture reaches the held Local turn after preparation: " + boundary);
                Lose();
                await held.DisposeAsync();
                held = null;
            }
            try { await run; }
            catch (LiveAdmissionStaleException error) { refused = error; }
        }
        finally { if (held is not null) await held.DisposeAsync(); }
        var effects = localCounter.Publishes + localCounter.Ensures + localCounter.Deletes + localCounter.Moves;
        Check.Require(lost && refused is not null && ItemSync.LiveWriteRefused(refused) &&
            effects == 0 && PendingAt(fixture, Side.Dropbox, Item).Count == 1 &&
            await fixture.Local.ReadOneAsync(Item, default) == local &&
            fixture.Inner.Pending(PairId) is NoPending &&
            fixture.Local.StagesDisposed == (deleting ? 0 : 1) &&
            remoteCounter.Opens == remoteCounter.Disposals,
            "R6-3 late loss refuses the Local effect, retains selection and releases unowned stage/source custody: " +
            boundary + "; refused=" + refused?.Detail + "; effects=" + effects +
            "; disposed=" + fixture.Local.StagesDisposed);
        // A refused Local turn neither leaks the replacement slot nor prevents independent Dropbox work.
        await using (await fixture.Runtime.EnterAsync(default)) { }
        var sibling = LocalEntry("independent", "independent-id");
        fixture.Local.Seed(sibling, [1]);
        Check.Require(await RunSelectionAsync(fixture, Selected(fixture, sibling.Path, sibling, page: 2)) &&
            await fixture.Dropbox.ReadOneAsync(sibling.Path, default) is not null,
            "R6-3 independent Dropbox work continues after late Local refusal: " + boundary);
    }

    private static async Task RepairHeldRenameParentAsync()
    {
        using var fixture = new Fixture(realStore: true);
        SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
        var old = LocalEntry(OldPath, LocalId);
        var remote = RemoteEntry(OldPath, DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(old, remote)] });
        fixture.Local.Seed(old, [1]);
        var moved = remote with { Path = Folder + "/renamed" };
        fixture.Dropbox.Seed(FolderEntry(Folder, "parent-id"), null);
        fixture.Dropbox.Seed(moved, [1]);
        var selected = SeedRemote(fixture, moved,
            new ChangeNotice(Side.Dropbox, ChangeKind.Renamed, moved.Path, OldPath, DropboxId, null, false, false));
        RepairDone(fixture);
        ClearObligations(fixture);
        var localCounter = new CountingSide(fixture.Local);
        var prepared = false;
        fixture.LocalView = new ProxySide(localCounter)
        {
            BlockEnsure = path =>
            {
                if (path == Folder)
                {
                    prepared = true;
                    OweLocal(fixture, SourceObligation.NormalRepair);
                }
                return null;
            }
        };
        LiveAdmissionStaleException? refused = null;
        try { await RunSelectionAsync(fixture, selected); }
        catch (LiveAdmissionStaleException error) { refused = error; }
        Check.Require(prepared && refused is not null && ItemSync.LiveWriteRefused(refused) &&
            localCounter.Moves == 0 && localCounter.Deletes == 0 &&
            await fixture.Local.ReadOneAsync(OldPath, default) == old &&
            PendingAt(fixture, Side.Dropbox, moved.Path).Count == 1,
            "R6-3 loss during rename parent preparation refuses the subsequent move and retains selection: " +
            "prepared=" + prepared + "; refused=" + refused?.Detail + "; moves=" + localCounter.Moves);
    }

    private static async Task RepairHeldFolderParentAsync(bool twoParents)
    {
        using var fixture = new Fixture(realStore: true);
        SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
        var parent = twoParents ? Folder + "/Parent" : Folder;
        var target = FolderEntry(parent + "/Created", "created-id");
        fixture.Dropbox.Seed(FolderEntry(Folder, "ancestor-id"), null);
        if (twoParents) fixture.Dropbox.Seed(FolderEntry(parent, "parent-id"), null);
        fixture.Dropbox.Seed(target, null);
        var selected = SeedRemote(fixture, target, new ChangeNotice(Side.Dropbox, ChangeKind.Changed,
            target.Path, null, target.Id, target, false, false));
        RepairDone(fixture);
        ClearObligations(fixture);
        var localCounter = new CountingSide(fixture.Local);
        var prepared = false;
        fixture.LocalView = new ProxySide(localCounter)
        {
            BlockEnsure = path =>
            {
                if (path == Folder)
                {
                    prepared = true;
                    OweLocal(fixture, SourceObligation.NormalRepair);
                }
                return null;
            }
        };
        LiveAdmissionStaleException? refused = null;
        try { await RunSelectionAsync(fixture, selected); }
        catch (LiveAdmissionStaleException error) { refused = error; }
        Check.Require(prepared && refused is not null && ItemSync.LiveWriteRefused(refused) &&
            localCounter.Ensures == 1 && await fixture.Local.ReadOneAsync(target.Path, default) is null &&
            (!twoParents || await fixture.Local.ReadOneAsync(parent, default) is null) &&
            PendingAt(fixture, Side.Dropbox, target.Path).Count == 1,
            "R6-O4-3 loss during folder parent preparation refuses subsequent creates and retains selection: " +
            "twoParents=" + twoParents + "; prepared=" + prepared + "; refused=" + refused?.Detail +
            "; ensures=" + localCounter.Ensures);
    }

    // The freeze separates new live authority from the plan's already-owned removal authority.
    private static async Task RepairHeldCaptureAsync(bool afterFreeze)
    {
        using var fixture = new Fixture(realStore: true);
        SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
        var localFolder = FolderEntry(Folder, FolderId);
        var remoteFolder = FolderEntry(Folder, DropboxId);
        var child = LocalEntry(Plain, LocalId);
        var remoteChild = RemoteEntry(Plain, "remote-child");
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Record = [SyncedEntries.From(localFolder, remoteFolder), SyncedEntries.From(child, remoteChild)]
        });
        fixture.Local.Seed(localFolder, null);
        fixture.Local.Seed(child, [1]);
        var selected = SeedRemote(fixture, remoteFolder);
        RepairDone(fixture);
        ClearObligations(fixture);
        var localCounter = new CountingSide(fixture.Local);
        fixture.LocalView = localCounter;
        var lost = false;
        fixture.Hooks.BeforeCall = method =>
        {
            if (lost || method.Name != (afterFreeze
                ? nameof(IStateStore.ReadyFolderEffects) : nameof(IStateStore.OpenFolderPlan))) return;
            if (afterFreeze && fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == 0) return;
            lost = true;
            OweLocal(fixture, SourceObligation.NormalRepair);
        };
        LiveAdmissionStaleException? refused = null;
        try
        {
            Check.Require(await Runner(fixture, selected, new Boundary(_ => new Lease()),
                (selection, _, _) => Task.FromResult(Coverage(selection))).RunAsync(selected, default) is not null, "Selection must be admitted.");
        }
        catch (LiveAdmissionStaleException error) { refused = error; }
        fixture.Hooks.BeforeCall = null;
        var frozen = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
        if (afterFreeze)
        {
            Check.Require(lost && frozen == 1 && refused is null && localCounter.Deletes > 0 &&
                await fixture.Local.ReadOneAsync(Folder, default) is null &&
                await fixture.Local.ReadOneAsync(Plain, default) is null &&
                fixture.Inner.Pending(PairId) is NoPending,
                "R6-O4-3 debt after a frozen live plan preserves owned Local removal: lost=" + lost +
                "; frozen=" + frozen + "; refused=" + refused?.Detail + "; deletes=" + localCounter.Deletes);
            return;
        }
        Check.Require(lost && refused is not null && ItemSync.LiveWriteRefused(refused) && frozen == 0 &&
            !fixture.Inner.ActiveFolderRemovals(PairId, default).Any() && LocalWrites(localCounter) == 0 &&
            await fixture.Local.ReadOneAsync(Plain, default) is not null,
            "R6-4 a Local loss during a live plan's subtree capture freezes no Local-removal operation: lost=" + lost +
            "; refused=" + refused?.Detail + "; frozen=" + frozen + "; localWrites=" + LocalWrites(localCounter));
    }

    // R6-5: a refused batch child does not stop its independent Dropbox-only sibling; the batch's work stays pending.
    private static async Task RepairHeldSiblingAsync()
    {
        const string sibling = Folder + "/sibling";
        using var fixture = new Fixture(realStore: true);
        var root = FolderEntry(Folder, FolderId);
        var child = LocalEntry(Plain, LocalId);
        var remoteChild = RemoteEntry(Plain, DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(child, remoteChild)] });
        fixture.Local.Seed(root, null);
        fixture.Dropbox.Seed(FolderEntry(Folder, "remote-folder"), null);
        fixture.Local.Seed(child, [2]);
        fixture.Dropbox.Seed(remoteChild with
            { Revision = "rev-2", ContentHash = DropboxContentHash.Compute(new byte[] { 3 }) }, [3]);
        // Ordered after the refused child, so the base code's cancellation of the batch never reaches it.
        fixture.Local.Seed(LocalEntry(sibling, "sibling-id"), [4]);
        var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null, FolderId, root, false, false));
        HeldPair(fixture);
        var localCounter = new CountingSide(fixture.Local);
        fixture.LocalView = localCounter;
        LiveAdmissionStaleException? refused = null;
        try { Check.Require(await Counted(fixture, selected, () => { }).RunAsync(selected, default) is not null, "Selection must be admitted."); }
        catch (LiveAdmissionStaleException error) { refused = error; }
        var uploaded = await fixture.Dropbox.ReadOneAsync(sibling, default) is not null;
        Check.Require(refused is not null && ItemSync.LiveWriteRefused(refused) && LocalWrites(localCounter) == 0 &&
            uploaded && PendingAt(fixture, Side.Local, Folder).Count == 1,
            "R6-5 a refused batch child keeps the folder work and its independent sibling still uploads: refused=" +
            refused?.Detail + "; uploaded=" + uploaded + "; localWrites=" + LocalWrites(localCounter));
    }

    // Content effects on the Local provider; reading Local content to upload it is not a Local write.
    private static int LocalWrites(CountingSide side) => side.Stages + side.Publishes + side.Ensures + side.Deletes + side.Moves;

    private static void HeldPair(Fixture fixture)
    {
        RepairDone(fixture);
        ClearObligations(fixture);
        OweLocal(fixture, SourceObligation.LocalSkippedCoverage);
    }

    // R6-F3: a live folder batch carries the refusal to every child it dispatches. A saved child on both sides whose
    // Local edit kept its size and stamp is not overwritten by its Dropbox update, either when first planned or when
    // an upload's freshness read replans it after the Dropbox side changed.
    private static async Task RepairHeldBatchAsync(bool fallback)
    {
        using var fixture = new Fixture(realStore: true);
        var root = FolderEntry(Folder, FolderId);
        var child = LocalEntry(Plain, LocalId);
        var remoteChild = RemoteEntry(Plain, DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(child, remoteChild)] });
        fixture.Local.Seed(root, null);
        fixture.Dropbox.Seed(FolderEntry(Folder, "remote-folder"), null);
        var edited = remoteChild with { Revision = "rev-2", ContentHash = DropboxContentHash.Compute(new byte[] { 3 }) };
        if (fallback)
        {
            fixture.Local.Seed(child with { Size = 2, Modified = Stamp.AddSeconds(5) }, [2, 2]);
            fixture.Dropbox.Seed(remoteChild, [1]);
            var listed = false;
            fixture.DropboxView = new ProxySide(fixture.Dropbox)
            {
                OnListing = () => listed = true,
                BeforeRead = (path, _) =>
                {
                    if (listed && path == Plain) fixture.Dropbox.Seed(edited, [3]);
                    return Task.CompletedTask;
                }
            };
        }
        else
        {
            fixture.Local.Seed(child, [2]);
            fixture.Dropbox.Seed(edited, [3]);
        }
        var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null, FolderId, root, false, false));
        HeldPair(fixture);
        var localCounter = new CountingSide(fixture.Local);
        fixture.LocalView = localCounter;
        var batches = 0;
        LiveAdmissionStaleException? refused = null;
        try { Check.Require(await Counted(fixture, selected, () => batches++).RunAsync(selected, default) is not null, "Selection must be admitted."); }
        catch (LiveAdmissionStaleException error) { refused = error; }
        Check.Require(batches == 1 && refused is { Reason: LiveAdmissionStaleReason.CoverageChanged } &&
            LocalWrites(localCounter) == 0 && PendingAt(fixture, Side.Local, Folder).Count == 1,
            "R6-F3 a live folder batch refuses a Local write under Local gap debt and keeps the work: fallback=" + fallback +
            "; batches=" + batches + "; refused=" + refused?.Reason + "; localWrites=" + LocalWrites(localCounter));
    }

    // R6-F2: a Dropbox rename that moves the Local item is refused when the Local debt appears while its coverage is
    // captured, because the check runs on the decided effect inside admitted execution.
    private static async Task RepairHeldRenameAdmissionAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var old = LocalEntry(OldPath, LocalId);
        var remote = RemoteEntry(OldPath, DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(old, remote)] });
        // Local evidence needs an acquired journal boundary; without one every read refuses for that reason alone.
        SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
        fixture.Local.Seed(old, [1]);
        var moved = remote with { Path = Item };
        fixture.Dropbox.Seed(moved, [1]);
        var selected = SeedRemote(fixture, moved,
            new ChangeNotice(Side.Dropbox, ChangeKind.Renamed, Item, OldPath, DropboxId, null, false, false));
        RepairDone(fixture);
        ClearObligations(fixture);
        var localCounter = new CountingSide(fixture.Local);
        fixture.LocalView = localCounter;
        LiveAdmissionStaleException? refused = null;
        try
        {
            Check.Require(await Runner(fixture, selected, new Boundary(_ => new Lease()), (selection, _, _) =>
            {
                OweLocal(fixture, SourceObligation.LocalSkippedCoverage);
                return Task.FromResult(Coverage(selection));
            }).RunAsync(selected, default) is not null, "Selection must be admitted.");
        }
        catch (LiveAdmissionStaleException error) { refused = error; }
        Check.Require(refused is { Reason: LiveAdmissionStaleReason.CoverageChanged } && LocalWrites(localCounter) == 0 &&
            await fixture.Local.ReadOneAsync(OldPath, default) is not null &&
            fixture.Inner.SyncedById(PairId, Side.Dropbox, DropboxId)?.DropboxPath == OldPath,
            "R6-F2 Local debt installed during rename coverage capture refuses the Local move: refused=" + refused?.Reason +
            "; localWrites=" + LocalWrites(localCounter));
    }

    // R6-F2: a live folder plan that removes the Local folder is refused when the Local debt appears while the plan's
    // admission is pending, because the check runs after admission and re-observation.
    private static async Task RepairHeldFolderAdmissionAsync()
    {
        using var fixture = new Fixture(realStore: true);
        SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
        var localFolder = FolderEntry(Folder, FolderId);
        var remoteFolder = FolderEntry(Folder, DropboxId);
        var child = LocalEntry(Plain, LocalId);
        var remoteChild = RemoteEntry(Plain, "remote-child");
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Record = [SyncedEntries.From(localFolder, remoteFolder), SyncedEntries.From(child, remoteChild)]
        });
        fixture.Local.Seed(localFolder, null);
        fixture.Local.Seed(child, [1]);
        var selected = SeedRemote(fixture, remoteFolder);
        RepairDone(fixture);
        ClearObligations(fixture);
        var localCounter = new CountingSide(fixture.Local);
        fixture.LocalView = localCounter;
        LiveAdmissionStaleException? refused = null;
        // The first admission runs the deferred folder row; the second is the folder plan's own (SelectionPass.cs:234-246).
        var admissions = 0;
        try
        {
            Check.Require(await Runner(fixture, selected, new Boundary(_ =>
            {
                if (++admissions == 2) OweLocal(fixture, SourceObligation.LocalSkippedCoverage);
                return new Lease();
            }), (selection, _, _) => Task.FromResult(Coverage(selection))).RunAsync(selected, default) is not null, "Selection must be admitted.");
        }
        catch (LiveAdmissionStaleException error) { refused = error; }
        Check.Require(admissions == 2 && refused is { Reason: LiveAdmissionStaleReason.CoverageChanged } &&
            LocalWrites(localCounter) == 0 && await fixture.Local.ReadOneAsync(Plain, default) is not null,
            "R6-F2 Local debt installed while a folder plan's admission is pending refuses the Local removal: admissions=" +
            admissions + "; refused=" + refused?.Reason + "; localWrites=" + LocalWrites(localCounter));
    }

    // R6-F4: under Local debt a Dropbox rename notice is judged by RenameDecision.WrittenSide(): finishing a case
    // rename writes only Dropbox, and a split writes nothing, so neither is refused.
    private static async Task RepairHeldRenameOwnerAsync(bool caseRename)
    {
        using var fixture = new Fixture(realStore: true);
        var local = LocalEntry(Item, LocalId);
        var remote = RemoteEntry(Item, DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
        SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
        var moved = remote with
        {
            Path = caseRename ? PathNames.CaseRenamePath(Item, fixture.Settings.CaseRenameSuffix) : OldPath
        };
        // The split case has no Local counterpart left at the saved path.
        if (caseRename) fixture.Local.Seed(local, [1]);
        fixture.Dropbox.Seed(moved, [1]);
        var selected = SeedRemote(fixture, moved,
            new ChangeNotice(Side.Dropbox, ChangeKind.Renamed, moved.Path, Item, DropboxId, null, false, false));
        HeldPair(fixture);
        var localCounter = new CountingSide(fixture.Local);
        fixture.LocalView = localCounter;
        LiveAdmissionStaleException? refused = null;
        try { await RunSelectionAsync(fixture, selected); }
        catch (LiveAdmissionStaleException error) { refused = error; }
        var saved = fixture.Inner.SyncedById(PairId, Side.Dropbox, DropboxId);
        Check.Require(LocalWrites(localCounter) == 0 && (caseRename
                ? saved?.DropboxPath == Item && await fixture.Dropbox.ReadOneAsync(moved.Path, default) is null
                : saved is null && fixture.Inner.Issues(PairId).Any(issue => issue.Cause == TextKey.InfoMovedDeleted)),
            "R6-F4 a Dropbox rename that writes no Local item proceeds under Local debt: caseRename=" + caseRename +
            "; refused=" + refused?.Reason + "; localWrites=" + LocalWrites(localCounter));
    }

    private static void OweLocal(Fixture fixture, SourceObligation obligation)
    {
        var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
        fixture.Inner.AdvanceSource(PairId, Side.Local, Generation, source.Epoch, source.Revision,
            source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance, source.Cursor,
            obligation);
    }

    private static async Task ContainmentAsync()
    {
        foreach (var parents in new[] { false, true }) await BatchCrashAsync(parents);
        await CrossBoundaryFolderEffectsAsync();
        await MovedIntoBatchAsync();
        await DraggedFolderAsync();
        await BoundDuringReobservationAsync();
        foreach (var sameIds in new[] { true, false }) await MidBatchBindingAsync(sameIds);
        await NewFolderOneBatchAsync();
        await SavedUnderAddedFolderAsync();
        await BatchFailureWakeAsync();
        await BatchIncompleteFinalizationAsync();
        var failures = new List<Exception>();
        foreach (var check in new Func<Task>[] { BatchReplacementAsync, BatchRootReadFailureAsync, FixedSubtreeCountsAsync })
        {
            try { await check(); }
            catch (Exception error) { failures.Add(new InvalidOperationException(check.Method.Name, error)); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
        foreach (var (baseline, matching) in new[] { (false, false), (true, true), (true, false) })
        {
            using var fixture = new Fixture(realStore: true);
            var local = FolderEntry(Folder, LocalId);
            var remote = FolderEntry(Folder, DropboxId);
            fixture.Local.Seed(local, null);
            fixture.Dropbox.Seed(remote, null);
            fixture.Local.Seed(LocalEntry(Folder + "/Child", "child-local"), [1]);
            if (baseline) fixture.Inner.Commit(PairId, StateChange.Empty with
            { Record = [SyncedEntries.From(matching ? local : local with { Id = "previous-folder-id" }, remote)] });
            var outsideLocal = LocalEntry("Outside/Missing", "outside-local");
            var outsideRemote = RemoteEntry("Outside/Missing", "outside-remote");
            var outsideSaved = SyncedEntries.From(outsideLocal, outsideRemote);
            SeedFolders(fixture, "Outside");
            fixture.Dropbox.Seed(outsideRemote, [1]);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [outsideSaved] });
            var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null, LocalId, local, false, false));
            var batches = 0;
            Check.Require(await Counted(fixture, selected, () => batches++).RunAsync(selected, default) is true, "folder selection completes");
            Check.Require(batches == (matching ? 0 : 1),
                "unknown and replaced folders batch; a plain touch of a known folder is entry-only");
            Check.Require((await fixture.Dropbox.ReadOneAsync(Folder + "/Child", default) is not null) == !matching,
                "unknown and recreated folders sync descendants; plain touches leave children untouched");
            Check.Require(await fixture.Dropbox.ReadOneAsync(outsideRemote.Path, default) == outsideRemote &&
                fixture.Inner.SyncedByKey(PairId, Side.Local, outsideSaved.LocalKey) == outsideSaved,
                $"an outside baseline-backed delete candidate is neither executed nor forgotten, baseline={baseline} matching={matching}");
        }
    }

    /// <summary>A pass whose batch port counts its calls; the views set on the fixture beforehand are the context's sides.</summary>
    private static SelectionPass Counted(Fixture fixture, WorkSelection selection, Action onBatch,
        Func<Task>? afterBatch = null, bool repaired = true, ExecutorContext? context = null)
    {
        RepairDone(fixture, repaired);
        var boundary = new Boundary(_ => new Lease());
        boundary.Capture = (selected, _, _) => Task.FromResult(Coverage(selected));
        context ??= fixture.Context(selection);
        var ports = new SyncPorts(new RecoveryBoundary(), boundary, RefusingExecutionBoundary.Instance, 0);
        var jobs = SyncJobs.Create(context, ports);
        return new SelectionPass(context, jobs.Items, jobs.Ancestors, boundary, jobs.Actions,
            jobs.Items.Rows, (path, _) => context.Events(RunEvent.Stage(TextKey.StageRows, inProgress: true, path: path)),
            async (selected, side, scope, retries, settles, ct) =>
        {
            onBatch();
            await SyncJobs.RunBatchAsync(context, ports, selected, scope, ct);
            if (afterBatch is not null) await afterBatch();
        });
    }

    // AutoSync batches only after a pair's first Repair; the proxy answers it, the database keeps it for a reopened store.
    private static void RepairDone(Fixture fixture, bool done = true)
    {
        fixture.Hooks.RepairDone = done;
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.DatabasePath + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE pair SET first_repair_done = $done WHERE pair_id = $p;";
        command.Parameters.AddWithValue("$done", done ? 1 : 0);
        command.Parameters.AddWithValue("$p", PairId);
        command.ExecuteNonQuery();
    }

    private static void SeedFolders(Fixture fixture, params string[] paths)
    {
        foreach (var path in paths)
        {
            fixture.Local.Seed(FolderEntry(path, "local-" + path), null);
            fixture.Dropbox.Seed(FolderEntry(path, "remote-" + path), null);
        }
    }

    private static async Task BatchFailureWakeAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var root = FolderEntry(Folder, LocalId);
        var failedPath = Folder + "/Failed";
        var goodPath = Folder + "/Good";
        fixture.Local.Seed(root, null);
        fixture.Local.Seed(LocalEntry(failedPath, "failed-id"), [1]);
        fixture.Local.Seed(LocalEntry(goodPath, "good-id"), [1]);
        fixture.DropboxView = new HookedSide(fixture.Dropbox, path =>
        {
            if (path == failedPath) throw new ItemFailedException(path, Text.ReadFailedOn(Side.Dropbox), new IOException());
        });
        var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed,
            Folder, null, LocalId, root, false, false));
        var batches = 0;
        Check.Require(await Counted(fixture, selected, () => batches++).RunAsync(selected, default) is not null, "Selection must be admitted.");
        Check.Require(batches == 1 && await fixture.Dropbox.ReadOneAsync(goodPath, default) is not null &&
            await fixture.Dropbox.ReadOneAsync(failedPath, default) is null,
            "a failed descendant does not stop its sibling and is not uploaded");
        var issue = fixture.Inner.Issues(PairId).Single(value => value.Path == failedPath);
        fixture.DropboxView = null;
        await new Syncer(fixture.Context(selected), new SyncPorts(new RecoveryBoundary(), new Boundary(_ => new Lease()),
            RefusingExecutionBoundary.Instance, 0)).ResolveAsync(issue, ResolutionChoice.Retry, null, null, default);
        Check.Require(fixture.Inner.IssueById(PairId, issue.Id) is null &&
            await fixture.Dropbox.ReadOneAsync(failedPath, default) is not null, "Retry resolves and uploads the descendant");
    }

    private static async Task BatchReplacementAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var root = FolderEntry(Folder, LocalId);
        var path = Folder + "/Sub";
        var oldLocal = FolderEntry(path, "sub-local");
        var remote = FolderEntry(path, "sub-remote");
        var incoming = LocalEntry(path, "replacement-local");
        var child = RemoteEntry(path + "/New", "unbound-child");
        fixture.Local.Seed(root, null);
        fixture.Local.Seed(incoming, [1]);
        fixture.Dropbox.Seed(FolderEntry(Folder, DropboxId), null);
        fixture.Dropbox.Seed(remote, null);
        fixture.Dropbox.Seed(child, [1]);
        var saved = SyncedEntries.From(oldLocal, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var selected = Selected(fixture, Folder, root);
        Check.Require(await Counted(fixture, selected, () => { }).RunAsync(selected, default) is not null, "Selection must be admitted.");
        Check.Require(await fixture.Dropbox.ReadOneAsync(child.Path, default) == child &&
            await fixture.Dropbox.ReadOneAsync(path, default) == remote &&
            fixture.Inner.SyncedByKey(PairId, Side.Local, saved.LocalKey) == saved,
            "a left batch folder-to-file replacement preserves its unbound remote child and baseline");
        // DESIGN-100-5 B1 leaves this row before replacement planning; it does not manufacture a Failure.
        var leftRow = new Row(saved.LocalKey, incoming, remote, saved, [], null, RowContext.Neutral);
        var leftJobs = SyncJobs.Create(fixture.Context(selected) with { BatchScope = Folder },
            new SyncPorts(new RecoveryBoundary(), new Boundary(_ => new Lease()), RefusingExecutionBoundary.Instance, 0));
        var left = await leftJobs.Items.PrepareAsync(leftRow, default);
        var unfinished = new SyncOutcome(left.Kind, left.None, null, null, false);
        Check.Require(left.None == NoneReason.OutOfScope && !unfinished.Succeeded() &&
            unfinished.OwesWork(left.Row, fixture.Inner, PairId) == true,
            "left replacement stays unfinished rather than reporting completion");

        // The pre-removal guard also covers a mapped footprint outside the listed root.
        using var crossing = new Fixture(realStore: true);
        var outside = remote with { Path = "Outside/Sub" };
        crossing.Local.Seed(root, null);
        crossing.Local.Seed(incoming, [1]);
        SeedFolders(crossing, "Outside");
        crossing.Dropbox.Seed(outside, null);
        var crossSaved = SyncedEntries.From(oldLocal, outside);
        crossing.Inner.Commit(PairId, StateChange.Empty with { Record = [crossSaved] });
        var selection = Selected(crossing, Folder, root);
        Check.Require(crossing.Inner.BeginSelection(selection), "Selection setup must be admitted.");
        var context = crossing.Context(selection) with { BatchScope = Folder };
        var boundary = new Boundary(_ => new Lease());
        boundary.Capture = (work, _, _) => Task.FromResult(Coverage(work));
        var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(), boundary,
            RefusingExecutionBoundary.Instance, 0));
        var row = new Row(crossSaved.LocalKey, incoming, outside, crossSaved, [], null, RowContext.Neutral);
        await ThrowsAsync<ChangedUnderneathException>(() => jobs.Items.DispatchPreparedAsync(
            new Massdex.SyncShark.Engine.Planner.SyncAction(ActionKind.ReplaceKind, row,
                Side: Side.Dropbox, Target: outside.Path),
            default, selection: selection));
        Check.Require(boundary.Calls == 0 &&
            crossing.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == 0 &&
            await crossing.Dropbox.ReadOneAsync(outside.Path, default) == outside,
            "an out-of-batch replacement refuses before admission, planning or removal");
    }

    private static async Task BatchIncompleteFinalizationAsync()
    {
        foreach (var partial in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: true);
            var root = FolderEntry(Folder, LocalId);
            fixture.Local.Seed(root, null);
            fixture.Dropbox.Seed(FolderEntry(Folder, DropboxId), null);
            var listed = LocalEntry(Folder + "/A", "listed-id");
            var omitted = LocalEntry(Folder + "/Z", "omitted-id");
            fixture.Local.Seed(listed, [1]);
            fixture.Local.Seed(omitted, [1]);
            fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, Folder,
                    new IssueSubject([new IssueItem(Side.Local, Folder, LocalId)], null,
                        FirstRunDeletion: IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice))]
            });
            var incomplete = new InventoryIncompleteException("partial first-run fixture", null);
            if (partial) fixture.Local.FailWalkAfter(listed.Path, incomplete);
            var finalizing = false;
            var finalizerReads = 0;
            fixture.DropboxView = new ProxySide(fixture.Dropbox)
            {
                OnRead = path => { if (finalizing && path == Folder) finalizerReads++; }
            };
            var selection = Selected(fixture, Folder, root);
            Check.Require(fixture.Inner.BeginSelection(selection), "Selection setup must be admitted.");
            var context = fixture.Context(selection) with
            {
                Events = item => { if (item.StageKey == TextKey.StageFolderPlans) finalizing = true; }
            };
            var boundary = new Boundary(_ => new Lease());
            boundary.Capture = (work, _, _) => Task.FromResult(Coverage(work));
            var ports = new SyncPorts(new RecoveryBoundary(), boundary, RefusingExecutionBoundary.Instance, 0);
            if (partial)
            {
                var thrown = await ThrowsAsync<InventoryIncompleteException>(() =>
                    SyncJobs.RunBatchAsync(context, ports, selection, Folder, default));
                Check.Require(ReferenceEquals(thrown, incomplete), "usable partial work still surfaces its original listing failure");
            }
            else await SyncJobs.RunBatchAsync(context, ports, selection, Folder, default);
            Check.Require(await fixture.Dropbox.ReadOneAsync(listed.Path, default) is not null &&
                (await fixture.Dropbox.ReadOneAsync(omitted.Path, default) is null) == partial,
                "partial listing completes the safe listed child without inventing the omitted child");
            Check.Require(finalizing && (finalizerReads == 0) == partial &&
                fixture.Inner.Issues(PairId).Any(issue => issue.Path == Folder && issue.Kind == IssueKind.Failure) == partial,
                "only a complete listing reaches first-run finalization and closes the retained folder failure");
        }
    }

    private static async Task BatchRootReadFailureAsync()
    {
        using var fixture = UnreadableFixture();
        var root = FolderEntry(Folder, FolderId);
        UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
        var listing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var local = new ProxySide(fixture.Local) { OnListing = () => listing.TrySetResult() };
        fixture.LocalView = local;
        var incomplete = new InventoryIncompleteException("scoped root fixture",
            new ItemFailedException(Folder, TextKey.MoveFailed, null));
        fixture.DropboxView = new ProxySide(fixture.Dropbox)
        {
            BeforeRead = (_, ct) => listing.Task.WaitAsync(ct),
            ReadFault = path => path == Folder ? incomplete : null
        };
        var selection = Selected(fixture, Folder, root);
        Check.Require(fixture.Inner.BeginSelection(selection), "Selection setup must be admitted.");
        var ports = new SyncPorts(new RecoveryBoundary(), new Boundary(_ => new Lease()),
            RefusingExecutionBoundary.Instance, 0);
        var thrown = await ThrowsAsync<InventoryIncompleteException>(() =>
            SyncJobs.RunBatchAsync(fixture.Context(selection), ports, selection, Folder, default));
        Check.Require(ReferenceEquals(thrown, incomplete), "a scoped root failure preserves the original exception");
        Check.Require(local.Finished, "a scoped root failure joins the started opposite listing");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.ReleaseUnreadable)) == 0 && Touches(fixture, Member),
            "a scoped root failure preserves unreadable membership");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.ScanRows)) == 0,
            "a scoped root failure never executes the aborted scan");
    }

    // Doubling unrelated bindings exposes pair-wide work while the involved subtree stays at two children.
    private static async Task FixedSubtreeCountsAsync()
    {
        foreach (var fail in new[] { false, true })
        {
            long[]? previous = null;
            Dictionary<string, long>? previousCalls = null;
            foreach (var background in new[] { 8, 16 })
            {
                using var fixture = new Fixture(realStore: true);
                var root = FolderEntry(Folder, LocalId);
                var first = LocalEntry(Folder + "/A", "first-child");
                var last = LocalEntry(Folder + "/Z", "unannounced-child");
                fixture.Local.Seed(root, null);
                fixture.Local.Seed(first, [1]);
                fixture.Local.Seed(last, [1]);
                fixture.Inner.Commit(PairId, StateChange.Empty with
                {
                    Record = Enumerable.Range(0, background).Select(index => SyncedEntries.From(
                        LocalEntry("Outside/f" + index, "outside-local-" + index),
                        RemoteEntry("Outside/f" + index, "outside-remote-" + index))).ToArray()
                });
                // This case enters AutoSync's real selector, so both source bases must be ready too.
                foreach (var side in new[] { Side.Local, Side.Dropbox })
                {
                    var source = fixture.Inner.ReadSource(PairId, side)!.Value;
                    fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                        source.Provenance, source.Cursor, source.Epoch, source.Revision + 1,
                        source.Provenance, source.Cursor, null);
                }
                if (fail) fixture.Local.FailWalkAfter(first.Path,
                    new InventoryIncompleteException("partial listing fixture", null));
                var selected = Selected(fixture, Folder, root);
                var store = new StoreCounters(fixture.Store);
                var local = new CountingSide(fixture.Local);
                var remote = new CountingSide(fixture.Dropbox);
                var context = fixture.Context(selected) with { Store = store, Local = local, Dropbox = remote };
                var batches = 0;
                Check.Require(await Counted(fixture, selected, () => batches++, context: context).RunAsync(selected, default) is not null, "Selection must be admitted.");
                if (fail)
                {
                    Check.Require(await fixture.Dropbox.ReadOneAsync(first.Path, default) is not null &&
                        await fixture.Dropbox.ReadOneAsync(last.Path, default) is null,
                        "partial discovery syncs the listed child but has no event for the undiscovered child");
                    var issue = fixture.Inner.Issues(PairId).Single(value => value.Path == Folder);
                    await new Syncer(context, new SyncPorts(new RecoveryBoundary(), new Boundary(_ => new Lease()),
                        RefusingExecutionBoundary.Instance, 0)).ResolveAsync(issue, ResolutionChoice.Retry, null, null, default);
                    Check.Require(fixture.Inner.IssueById(PairId, issue.Id) is null,
                        "user Retry resolves the root and releases its retained discovery work");
                    var boundary = new Boundary(_ => new Lease());
                    boundary.Capture = (work, _, _) => Task.FromResult(Coverage(work));
                    var ports = new SyncPorts(new RecoveryBoundary(), boundary, RefusingExecutionBoundary.Instance, 0);
                    await new Syncer(context, ports with
                    {
                        RunBatchAsync = async (work, side, scope, retries, settles, ct) =>
                        {
                            batches++;
                            await SyncJobs.RunBatchAsync(context, ports, work, scope, ct);
                        }
                    }).SyncWorkAsync(default);
                }
                Check.Require(batches == (fail ? 2 : 1) &&
                    await fixture.Dropbox.ReadOneAsync(last.Path, default) is not null &&
                    fixture.Inner.Issues(PairId).Count == 0,
                    "the next AutoSync selection completes retained discovery, not a root-only success");
                var counts = new long[] { local.Listings, remote.Listings, local.Yielded, remote.Yielded,
                    local.Reads, remote.Reads, local.Hashes, remote.Hashes, local.Opens, remote.Opens,
                    local.Stages, remote.Stages, local.Publishes, remote.Publishes, local.Deletes, remote.Deletes,
                    store.Commands, store.CommitCalls, store.ReadConnections, store.RowsRead, store.ScanAccepted };
                Check.Require(local.Listings == batches && remote.Listings == batches &&
                    remote.Publishes == 2 && local.Deletes + remote.Deletes == 0 &&
                    store.RowsRead <= batches * 3 && store.ScanAccepted <= batches * 6,
                    "fixed subtree absolute budgets: one bilateral listing per batch, two publications, no removals or outside scan rows");
                if (previous is not null)
                {
                    Check.Require(counts.SequenceEqual(previous), "fixed subtree counts do not grow with the pair");
                    Check.Require(store.MemberCalls.Count == previousCalls!.Count && store.MemberCalls.All(call =>
                        previousCalls.GetValueOrDefault(call.Key) == call.Value),
                        "each counted store operation is independent of background pair size");
                }
                Console.WriteLine($"FIX97 counts background={background} involved=3 retry={fail} values=" + string.Join(",", counts));
                Console.WriteLine("FIX97 store " + string.Join(",", store.MemberCalls.OrderBy(call => call.Key)
                    .Select(call => call.Key + "=" + call.Value)));
                previous = counts;
                previousCalls = store.MemberCalls.ToDictionary(call => call.Key, call => call.Value);
            }
        }
    }

    // A binding that crosses the root is saved, so the add-only batch neither duplicates, deletes nor forgets it.
    private static async Task CrossBoundaryFolderEffectsAsync()
    {
        foreach (var present in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: true);
            var root = FolderEntry("Parent", LocalId);
            fixture.Local.Seed(root, null);
            fixture.Local.Seed(LocalEntry("Parent/Ordinary", "ordinary-id"), [1]);
            var outside = LocalEntry("Outside/Child", "child-local");
            var inside = RemoteEntry("Parent/Child", "child-remote");
            var saved = SyncedEntries.From(outside, inside);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
            if (present) fixture.Dropbox.Seed(inside, [1]);
            var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed,
                root.Path, null, LocalId, root, false, false));
            var batches = 0;
            var pass = Counted(fixture, selection, () => batches++);
            var owed = fixture.Inner.Obligated(PairId);
            Check.Require(await pass.RunAsync(selection, default) is true && batches == 1,
                "a crossing binding still takes one batch, present=" + present);
            Check.Require(fixture.Inner.Obligated(PairId) == owed && fixture.Inner.Issues(PairId).Count == 0,
                "a crossing binding requests no Repair and raises no notice, present=" + present + " issues=" +
                string.Join(",", fixture.Inner.Issues(PairId).Select(issue => issue.Path + ":" + issue.Cause)));
            Check.Require(await fixture.Local.ReadOneAsync(inside.Path, default) is null &&
                await fixture.Dropbox.ReadOneAsync("Parent/Ordinary", default) is not null &&
                (await fixture.Dropbox.ReadOneAsync(inside.Path, default) is not null) == present &&
                fixture.Inner.SyncedByKey(PairId, Side.Local, saved.LocalKey) == saved,
                "the batch uploads the new child with no duplicate, deletion or baseline forgetting of the crossing binding");
        }
    }

    // D-1105-22: a folder added or re-added over saved records only adds; no listing absence becomes a deletion.
    private static async Task SavedUnderAddedFolderAsync()
    {
        foreach (var readded in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: true);
            var root = FolderEntry("Parent", readded ? "readded-root" : LocalId);
            var remoteRoot = FolderEntry("Parent", DropboxId);
            var kept = SyncedEntries.From(LocalEntry("Parent/Kept", "kept-local"), RemoteEntry("Parent/Kept", "kept-remote"));
            var localGone = SyncedEntries.From(LocalEntry("Parent/LocalGone", "gone-local"), RemoteEntry("Parent/LocalGone", "gone-remote"));
            var remoteGone = SyncedEntries.From(LocalEntry("Parent/RemoteGone", "left-local"), RemoteEntry("Parent/RemoteGone", "left-remote"));
            var records = new List<SyncedEntry> { kept, localGone, remoteGone };
            // Added: nothing saved at the root itself; re-added: the root's saved binding names a previous folder.
            if (readded) records.Add(SyncedEntries.From(FolderEntry("Parent", LocalId), remoteRoot));
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = records });
            fixture.Local.Seed(root, null);
            fixture.Dropbox.Seed(remoteRoot, null);
            fixture.Local.Seed(LocalEntry("Parent/Kept", "kept-local"), [1]);
            fixture.Dropbox.Seed(RemoteEntry("Parent/Kept", "kept-remote"), [1]);
            fixture.Dropbox.Seed(RemoteEntry("Parent/LocalGone", "gone-remote"), [1]);
            fixture.Local.Seed(LocalEntry("Parent/RemoteGone", "left-local"), [1]);
            fixture.Local.Seed(LocalEntry("Parent/New", "new-local"), [1]);
            var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, root.Path, null, root.Id, root, false, false));
            var batches = 0;
            Check.Require(await Counted(fixture, selection, () => batches++).RunAsync(selection, default) is not null, "Selection must be admitted.");
            Check.Require(batches == 1 && await fixture.Dropbox.ReadOneAsync("Parent/New", default) is not null,
                "SAFETY: the folder's new contents are added, readded=" + readded);
            Check.Require(await fixture.Dropbox.ReadOneAsync("Parent/LocalGone", default) is not null &&
                await fixture.Local.ReadOneAsync("Parent/RemoteGone", default) is not null &&
                await fixture.Dropbox.ReadOneAsync("Parent/Kept", default) is not null &&
                await fixture.Local.ReadOneAsync("Parent/Kept", default) is not null,
                "SAFETY: nothing is deleted on either side from a listing, readded=" + readded);
            Check.Require(fixture.Inner.SyncedByKey(PairId, Side.Local, localGone.LocalKey) == localGone &&
                fixture.Inner.SyncedByKey(PairId, Side.Local, remoteGone.LocalKey) == remoteGone &&
                fixture.Inner.SyncedByKey(PairId, Side.Local, kept.LocalKey) == kept,
                "SAFETY: saved records with an absent side are neither forgotten nor rewritten, readded=" + readded);
        }
    }

    // A dragged-in synced item with no extra event: the batch writes nothing for its bound row and deletes nothing.
    private static async Task MovedIntoBatchAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var root = FolderEntry("Parent", LocalId);
        var original = LocalEntry("Outside/Child", "moved-local");
        var remote = RemoteEntry("Outside/Child", "moved-remote");
        var saved = SyncedEntries.From(original, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        SeedFolders(fixture, "Outside");
        fixture.Local.Seed(root, null);
        fixture.Local.Seed(original with { Path = "Parent/Child" }, [1]);
        fixture.Dropbox.Seed(remote, [1]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed,
            root.Path, null, LocalId, root, false, false));
        var batches = 0;
        Check.Require(await Counted(fixture, selection, () => batches++).RunAsync(selection, default) is true,
            "moved-in selection completes");
        Check.Require(batches == 1 && await fixture.Dropbox.ReadOneAsync("Parent/Child", default) is null,
            "the batch writes nothing for a bound row");
        Check.Require(await fixture.Dropbox.ReadOneAsync(remote.Path, default) == remote &&
            fixture.Inner.SyncedByKey(PairId, Side.Local, saved.LocalKey) == saved && fixture.Inner.Issues(PairId).Count == 0,
            "the moved item's Dropbox copy and binding are untouched: no duplicate, no deletion");
    }

    // A new folder holding a dragged-in synced folder and new files, with no extra event.
    private static async Task DraggedFolderAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var subLocal = FolderEntry("Outside/Sub", "sub-local");
        var subRemote = FolderEntry("Outside/Sub", "sub-remote");
        var xLocal = LocalEntry("Outside/Sub/x", "x-local");
        var xRemote = RemoteEntry("Outside/Sub/x", "x-remote");
        fixture.Inner.Commit(PairId, StateChange.Empty with
        { Record = [SyncedEntries.From(subLocal, subRemote), SyncedEntries.From(xLocal, xRemote)] });
        SeedFolders(fixture, "Outside");
        fixture.Dropbox.Seed(subRemote, null);
        fixture.Dropbox.Seed(xRemote, [1]);
        var root = FolderEntry("G", LocalId);
        fixture.Local.Seed(root, null);
        fixture.Local.Seed(LocalEntry("G/new", "new-local"), [1]);
        fixture.Local.Seed(subLocal with { Path = "G/Sub" }, null);
        fixture.Local.Seed(xLocal with { Path = "G/Sub/x" }, [1]);
        fixture.Local.Seed(LocalEntry("G/Sub/fresh", "fresh-local"), [1]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, root.Path, null, LocalId, root, false, false));
        var batches = 0;
        Check.Require(await Counted(fixture, selection, () => batches++).RunAsync(selection, default) is true,
            "dragged-folder selection completes");
        Check.Require(batches == 1 && await fixture.Dropbox.ReadOneAsync("G/Sub", default) is null &&
            await fixture.Dropbox.ReadOneAsync("G/new", default) is not null,
            "the batch holds the bound folder and its subtree and uploads the new sibling");
        Check.Require(await fixture.Dropbox.ReadOneAsync(subRemote.Path, default) == subRemote &&
            await fixture.Dropbox.ReadOneAsync(xRemote.Path, default) == xRemote,
            "SAFETY: the dragged-in folder's Dropbox copy is neither deleted nor duplicated");
    }

    // A bound folder replaces a NEW folder between scan and execution; the refusal holds its subtree.
    private static async Task BoundDuringReobservationAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var subLocal = FolderEntry("Outside/Sub", "sub-local");
        var subRemote = FolderEntry("Outside/Sub", "sub-remote");
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(subLocal, subRemote)] });
        SeedFolders(fixture, "Outside");
        fixture.Local.Seed(subLocal, null);
        fixture.Dropbox.Seed(subRemote, null);
        var root = FolderEntry("Parent", LocalId);
        var newSub = FolderEntry("Parent/Sub", "new-sub");
        fixture.Local.Seed(root, null);
        fixture.Local.Seed(newSub, null);
        fixture.Local.Seed(LocalEntry("Parent/Sub/x", "x-local"), [1]);
        var moved = false;
        fixture.LocalView = new HookedSide(fixture.Local, beforeRead: path =>
        {
            if (moved || path != newSub.Path) return;
            moved = true;
            fixture.Local.DeleteAsync(newSub, null, default).GetAwaiter().GetResult();
            fixture.Local.MoveAsync(subLocal, newSub.Path, default).GetAwaiter().GetResult();
            fixture.Local.Seed(LocalEntry("Parent/Sub/x", "x-local"), [1]);
        });
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, root.Path, null, LocalId, root, false, false));
        var batches = 0;
        Check.Require(await Counted(fixture, selection, () => batches++).RunAsync(selection, default) is not null, "Selection must be admitted.");
        Check.Require(moved && batches == 1 && await fixture.Dropbox.ReadOneAsync(newSub.Path, default) is null &&
            await fixture.Dropbox.ReadOneAsync("Parent/Sub/x", default) is null,
            "a folder bound during reobservation is refused and held: no Dropbox Parent/Sub and no Parent/Sub/x");
        Check.Require(await fixture.Dropbox.ReadOneAsync(subRemote.Path, default) == subRemote,
            "SAFETY: the moved-in folder's Dropbox copy is not deleted");
    }

    // A binding committed mid-batch at a NEW row is neither overwritten nor duplicated by the batch.
    private static async Task MidBatchBindingAsync(bool sameIds)
    {
        using var fixture = new Fixture(realStore: true);
        SeedFolders(fixture, "Outside");
        var root = FolderEntry("Parent", LocalId);
        var item = LocalEntry("Parent/a", "a-local");
        fixture.Local.Seed(root, null);
        fixture.Local.Seed(item, [1]);
        var bound = sameIds
            ? SyncedEntries.From(item with { Path = "Outside/a" }, RemoteEntry("Outside/a", "a-remote"))
            : SyncedEntries.From(LocalEntry(item.Path, "other-local"), RemoteEntry(item.Path, "other-remote"));
        var occupant = RemoteEntry(bound.DropboxPath, bound.DropboxId!);
        var committed = false;
        fixture.LocalView = new HookedSide(fixture.Local, beforeRead: path =>
        {
            if (committed || path != item.Path) return;
            committed = true;
            fixture.Dropbox.Seed(occupant, [2]);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [bound] });
        });
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, root.Path, null, LocalId, root, false, false));
        var batches = 0;
        try { Check.Require(await Counted(fixture, selection, () => batches++).RunAsync(selection, default) is not null, "Selection must be admitted."); }
        catch (LiveAdmissionStaleException) { }
        var afterBatch = await fixture.Dropbox.ReadOneAsync(occupant.Path, default);
        var uploaded = await fixture.Dropbox.ReadOneAsync(item.Path, default);
        Check.Require(committed && batches == 1 && afterBatch == occupant && (!sameIds || uploaded is null),
            "a mid-batch binding leaves the bound Dropbox item unchanged and is never uploaded twice, sameIds=" + sameIds +
            $" committed={committed} batches={batches} after={afterBatch} uploaded={uploaded}");
    }

    // One new folder of N files is one batch; its Dropbox reads stay within two per item and below the 0.3.75 walk's,
    // measured as that walk's per-item branches: one live selection per file under an already synced root.
    private static async Task NewFolderOneBatchAsync()
    {
        const int Files = 8;
        var reads = new int[2];
        foreach (var walk in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: true);
            var root = FolderEntry("G", LocalId);
            fixture.Local.Seed(root, null);
            for (var index = 0; index < Files; index++) fixture.Local.Seed(LocalEntry("G/f" + index, "f-" + index), [1]);
            if (walk)
            {
                var remoteRoot = FolderEntry("G", DropboxId);
                fixture.Dropbox.Seed(remoteRoot, null);
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(root, remoteRoot)] });
            }
            var count = 0;
            fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeRead: _ => Interlocked.Increment(ref count));
            var batches = 0;
            var notices = walk
                ? Enumerable.Range(0, Files).Select(index => LocalEntry("G/f" + index, "f-" + index)).ToArray()
                : [root];
            for (var index = 0; index < notices.Length; index++)
            {
                var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, notices[index].Path, null,
                    notices[index].Id, notices[index], false, false), index + 1);
                Check.Require(await Counted(fixture, selection, () => batches++).RunAsync(selection, default) is true,
                    "every selection completes, walk=" + walk);
            }
            Check.Require(batches == (walk ? 0 : 1), "a new folder is one batch, walk=" + walk);
            for (var index = 0; index < Files; index++)
                Check.Require(await fixture.Dropbox.ReadOneAsync("G/f" + index, default) is not null, "every file uploads, walk=" + walk);
            reads[walk ? 1 : 0] = count;
        }
        Check.Require(reads[0] <= 2 * Files + 3 && reads[0] < reads[1],
            $"batched Dropbox reads: {reads[0]} for {Files} files, per-item walk branches: {reads[1]}");
    }

    // A crash after the root is recorded and before its children finish: the restart deletes nothing and keeps the root.
    // The unfinished children then come from their own notices or Repair (D-1105-22).
    private static async Task BatchCrashAsync(bool throughParents)
    {
        using var fixture = new Fixture(realStore: true);
        var root = FolderEntry(Folder, LocalId);
        fixture.Local.Seed(root, null);
        fixture.Local.Seed(LocalEntry(Folder + "/Child", "child-local"), [1]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null, LocalId, root, false, false));
        RepairDone(fixture);
        var boundary = new Boundary(_ => new Lease());
        boundary.Capture = (selected, _, _) => Task.FromResult(Coverage(selected));
        var context = fixture.Context(selection);
        var ports = new SyncPorts(new RecoveryBoundary(), boundary, RefusingExecutionBoundary.Instance, 0);
        var jobs = SyncJobs.Create(context, ports);
        var first = new SelectionPass(context, jobs.Items, jobs.Ancestors, boundary, jobs.Actions,
            jobs.Items.Rows, (path, _) => context.Events(RunEvent.Stage(TextKey.StageRows, inProgress: true, path: path)),
            async (selected, side, scope, retries, settles, ct) =>
            {
                if (throughParents)
                    await new Parents(context).EnsureAsync(Side.Dropbox, Folder + "/Child", ct);
                else
                {
                    fixture.DropboxView = new HookedSide(fixture.Dropbox, _ => throw new OperationCanceledException());
                    await SyncJobs.RunBatchAsync(fixture.Context(selected!), ports, selected, scope, ct);
                }
                throw new OperationCanceledException();
            });
        await ThrowsAsync<OperationCanceledException>(() => first.RunAsync(selection, default));
        var rootBinding = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Folder));
        Check.Require(rootBinding is not null && await fixture.Dropbox.ReadOneAsync(Folder + "/Child", default) is null,
            "crash occurs after the root is recorded but before child completion, throughParents=" + throughParents);
        fixture.DropboxView = null;
        using var reopened = Massdex.SyncShark.Engine.Storage.StateStore.Open(fixture.DatabasePath!);
        var resumedContext = fixture.Context(selection) with { Store = reopened };
        var resumedJobs = SyncJobs.Create(resumedContext, ports);
        var resumed = new SelectionPass(resumedContext, resumedJobs.Items, resumedJobs.Ancestors, boundary, resumedJobs.Actions,
            resumedJobs.Items.Rows, (path, _) => resumedContext.Events(RunEvent.Stage(TextKey.StageRows, inProgress: true, path: path)),
            (selected, side, scope, retries, settles, ct) => SyncJobs.RunBatchAsync(resumedContext, ports, selected, scope, ct));
        Check.Require(await resumed.RunAsync(selection, default) is true, "restart retires the anchor, throughParents=" + throughParents);
        Check.Require(reopened.SyncedByKey(PairId, Side.Local, PathNames.Key(Folder)) == rootBinding &&
            await fixture.Local.ReadOneAsync(Folder + "/Child", default) is not null &&
            await fixture.Dropbox.ReadOneAsync(Folder, default) is not null,
            "SAFETY: restart keeps the root binding and deletes nothing, throughParents=" + throughParents);
    }

    public static void RunPeers()
    {
        PeerCasesAsync().GetAwaiter().GetResult();
        HeldRepeatedNoticeAsync().GetAwaiter().GetResult();
        PeerRetentionCasesAsync().GetAwaiter().GetResult();
        PeerPreimageAdvance();
        FailedSelectionReleasesClaim();
        FolderFallbackPeerAsync().GetAwaiter().GetResult();
        foreach (var move in new[] { false, true })
            FolderRecordReplacementScopeAsync(move).GetAwaiter().GetResult();
    }

    private static async Task FolderRecordReplacementScopeAsync(bool move)
    {
        using var fixture = new Fixture();
        var local = FolderEntry(Folder, LocalId);
        var remote = FolderEntry(Folder, DropboxId);
        var saved = move ? null : SyncedEntries.From(local, remote);
        fixture.Local.Seed(local, null);
        fixture.Local.Seed(LocalEntry(Plain, MemberId), [2]);
        if (saved is not null) fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null,
            LocalId, local, false, false));
        var row = new Row(PathNames.Key(Folder), local, remote, saved, [], null, RowContext.Neutral);
        var prepared = new Massdex.SyncShark.Engine.Planner.SyncAction(ActionKind.Record, row);
        var scopes = LockKeys.ForScopes(fixture.Pair, LockKeys.ForLiveAction(prepared, fixture.Pair, null));
        var lease = new Lease { AllowedScopes = scopes, AllowedKeys = scopes.Select(scope => scope.Key).ToArray() };
        var changed = move ? remote with { Path = Folder.ToUpperInvariant() } :
            RemoteEntry(Folder, DropboxId) with { Modified = Stamp.AddDays(1) };
        fixture.Dropbox.Seed(changed, move ? null : [1]);
        var boundary = new Boundary(_ => lease);
        var jobs = SyncJobs.Create(fixture.Context(selected), new SyncPorts(new RecoveryBoundary(), boundary,
            RefusingExecutionBoundary.Instance, 0));
        var error = await ThrowsAsync<LiveAdmissionStaleException>(() => jobs.Items.RunAdmittedAsync(row, default,
            preparedAction: prepared, execution: lease, selection: selected));
        Check.Require(error.Reason == LiveAdmissionStaleReason.FootprintChanged && lease.ScopeCalls == 1 &&
            (await fixture.Local.ReadOneAsync(Folder, default)) is { IsFolder: true } &&
            await fixture.Local.ReadOneAsync(Plain, default) is not null && fixture.Inner.SyncedByKey(PairId, Side.Local, row.Key) == saved &&
            await fixture.Dropbox.ReadOneAsync(Folder, default) == changed,
            "RP-02/RP-04: folder Record reobserved as replacement or case-only Move refuses exact lease without effects; move=" + move);
    }

    private static async Task FolderFallbackPeerAsync()
    {
        using var fixture = new Fixture(realStore: true);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var source = fixture.Inner.ReadSource(PairId, side)!.Value;
            fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance, source.Cursor, null);
        }
        var local = FolderEntry(Folder, LocalId);
        var remote = FolderEntry(Folder, DropboxId);
        var before = SyncedEntries.From(local, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [before] });
        fixture.Dropbox.Seed(remote, null);
        var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Deleted, Folder, null,
            LocalId, null, false, true));
        var peer = SeedRemote(fixture, remote);
        Check.Require(fixture.Inner.BeginSelection(peer), "Selection setup must be admitted.");
        var admissions = 0;
        var boundary = new Boundary(request =>
        {
            fixture.Inner.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint);
            // Folder timestamps do not trigger Record; changed spelling does, at the same join key.
            if (++admissions == 2) fixture.Local.Seed(local with { Path = Folder.ToUpperInvariant() }, null);
            return new Lease { AllowedKeys = LockKeys.ForScopes(fixture.Pair, request.Footprint).Select(scope => scope.Key).ToArray() };
        });
        Check.Require(await Runner(fixture, selected, boundary, (_, _, _) => Task.FromResult(Coverage(selected)))
            .RunAsync(selected, default) is true, "PR-01: reappeared folder completes its deferred-plan fallback");
        var saved = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Folder));
        // DESIGN-114 §3.5: the fallback cannot advance a peer preimage while that peer has a live claim.
        Check.Require(admissions == 2 && saved != before && fixture.Inner.ReadWork(peer).Single().Before == before &&
            fixture.Hooks.CommittedSelections.Any(work => work.Peer == boundary.Requests[0][0].Coverage.Peer),
            "PR-01/M: fallback Record preserves the live peer preimage; admissions=" + admissions +
            "; changed=" + (saved != before) + "; peerUnchanged=" + (fixture.Inner.ReadWork(peer).Single().Before == before) +
            "; commits=" + fixture.Hooks.CommittedSelections.Count);
        await RunPeerSelectionAsync(fixture, peer);
        Check.Require(!PendingWork.ByIdentity(fixture.Inner, Side.Dropbox, peer.Domain, DropboxId).Any(),
            "PR-01: kept folder peer settles in its next selection without Repair");
    }

    private static void PeerPreimageAdvance()
    {
        foreach (var mode in new[] { "matching", "different", "later" })
        {
            using var fixture = new Fixture(realStore: true);
            var local = LocalEntry(Item, LocalId);
            var remote = RemoteEntry(Item, DropboxId);
            var before = SyncedEntries.From(local, remote);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [before] });
            var own = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null,
                LocalId, local, true, false));
            Check.Require(fixture.Inner.BeginSelection(own), "Selection setup must be admitted.");
            var watermark = fixture.Inner.ReadWorkWatermark();
            var other = SeedRemote(fixture, remote);
            var peerBefore = mode == "different" ? SyncedEntries.From(local, remote with { ContentHash = DropboxContentHash.Compute(new byte[] { 3 }) }) : before;
            if (mode == "different") fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [peerBefore] });
            Check.Require(fixture.Inner.BeginSelection(other), "Selection setup must be admitted.");
            if (mode == "different") fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [before] });
            var selected = own with { Peer = new SelectionPeer(PathNames.KeyFor(Side.Dropbox, Item),
                mode == "later" ? watermark : fixture.Inner.ReadWorkWatermark()) };
            var after = SyncedEntries.From(local, remote with { ContentHash = DropboxContentHash.Compute(new byte[] { 2 }) });
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [after] }, selected, null);
            // DESIGN-114 section 3.5: AdvanceBefore cannot rewrite another live claim, even with a matching preimage.
            Check.Require(fixture.Inner.ReadWork(other).Single().Before == peerBefore,
                "M/G18: live peer preimage remains owned by its claim: " + mode);
            fixture.Inner.CompleteWork(selected, selected.Peer);
            Check.Require(PendingWork.ByIdentity(fixture.Inner, Side.Dropbox, other.Domain, DropboxId).Any(),
                "M/G18: every live peer stays unconsumed: " + mode);
        }
    }

    private static async Task PeerRetentionCasesAsync()
    {
        var failures = new List<Exception>();
        foreach (var mode in new[] { "held", "failed", "skipped", "out-of-scope", "residuals", "folder" })
        {
            try { await PeerRetentionAsync(mode); }
            catch (Exception error) { failures.Add(new InvalidOperationException(mode, error)); }
        }
        foreach (var foreign in new[] { false, true })
        {
            try { await FolderPeerOnDiskAsync(foreign); }
            catch (Exception error) { failures.Add(new InvalidOperationException("folder-on-disk " + foreign, error)); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static async Task FolderPeerOnDiskAsync(bool foreign)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, Folder));
            await File.WriteAllBytesAsync(Path.Combine(root, Plain), [2]);
            using var fixture = new Fixture(realStore: true, localRoot: root);
            foreach (var side in new[] { Side.Local, Side.Dropbox })
            {
                var source = fixture.Inner.ReadSource(PairId, side)!.Value;
                fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                    source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance, source.Cursor, null);
            }
            var remote = FolderEntry(Folder, DropboxId);
            fixture.Dropbox.Seed(remote, null);
            var selected = SeedRemote(fixture, remote);
            var local = new Massdex.SyncShark.Engine.Local.LocalSide(fixture.Pair,
                new PathScope(fixture.Pair, [fixture.Pair]), fixture.Context(selected).Settings,
                new PendingNotes(fixture.Inner, fixture.Pair.PairId, 100, mutation: false),
                root, (_, _, _, _) => throw new NotSupportedException());
            fixture.LocalView = local;
            var folder = (await local.ReadOneAsync(Folder, default))!;
            Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null, folder.Id, folder, false, false));
            // The first selection's add-only batch lists both sides and observes the child in its listing, so the
            // foreign notice arrives once the listing has yielded the child, before its upload.
            if (foreign)
            {
                var injected = false;
                fixture.LocalView = new ProxySide(local)
                {
                    ListingFault = under =>
                    {
                        if (under != Folder || injected) return null;
                        injected = true;
                        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null,
                            null, folder, false, false), 2);
                        return null;
                    }
                };
                var error = await ThrowsAsync<LiveAdmissionStaleException>(() => RunPeerSelectionAsync(fixture, selected));
                Check.Require(injected && error.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged &&
                    await fixture.Dropbox.ReadOneAsync(Plain, default) is null,
                    "G18: foreign folder notice arriving before child refuses without uploading");
                return;
            }
            await RunPeerSelectionAsync(fixture, selected);
            var kept = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, folder.Id).Single();
            await RunPeerSelectionAsync(fixture, kept);
            Check.Require((await fixture.Dropbox.ReadOneAsync(Plain, default))?.ContentHash ==
                DropboxContentHash.Compute(new byte[] { 2 }) &&
                !PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, folder.Id).Any(),
                "G18-03: Local disk walk uploads an unannounced child and settles without Repair");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task PeerRetentionAsync(string mode)
    {
        using var fixture = new Fixture(realStore: true);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var source = fixture.Inner.ReadSource(PairId, side)!.Value;
            fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance, source.Cursor, null);
        }
        var folder = mode == "folder";
        var path = folder ? Folder : Item;
        var local = folder ? FolderEntry(path, LocalId) : LocalEntry(path, LocalId);
        var remote = folder ? FolderEntry(path, DropboxId) : RemoteEntry(path, DropboxId);
        fixture.Local.Seed(local, folder ? null : [2]);
        fixture.Dropbox.Seed(remote, folder ? null : [1]);
        if (folder) fixture.Local.Seed(LocalEntry(Plain, MemberId), [2]);
        else fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
        var observedLocal = new ProxySide(fixture.Local);
        fixture.LocalView = observedLocal;
        var localWork = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, path, null,
            LocalId, local, !folder, false));
        if (mode is "held" or "residuals")
            fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, path,
                    new IssueSubject([new IssueItem(Side.Local, path, LocalId)], null))]
            });
        if (mode == "residuals") await RunPeerSelectionAsync(fixture, localWork);
        var remoteWork = SeedRemote(fixture, remote);
        if (mode == "failed") fixture.DropboxView = new ProxySide(fixture.Dropbox)
        {
            BlockPublish = (_, _) => throw new ItemFailedException(path, TextKey.FailDropboxNoPermission, null)
        };
        if (mode == "skipped") fixture.Local.Seed(local with { Skip = "fixture-skip" }, [2]);
        var context = fixture.Context(remoteWork);
        if (mode == "out-of-scope")
            context = context with { Scope = new PathScope(fixture.Pair with { IgnoreRules = ["FileName:" + Item] }, [fixture.Pair]) };
        await RunPeerSelectionAsync(fixture, remoteWork, context);
        if (mode == "residuals")
        {
            fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(path, new[] { IssueKind.Failure })] });
            var residual = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Single();
            var hashes = observedLocal.Hashes;
            await RunPeerSelectionAsync(fixture, residual);
            Check.Require(observedLocal.Hashes > hashes &&
                !PendingWork.ByIdentity(fixture.Inner, Side.Dropbox, remoteWork.Domain, DropboxId).Any() &&
                !PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Any(),
                "G18-02: one released residual selection hashes the dirty Local edit and settles both sides");
            return;
        }
        Check.Require(PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Any(),
            "G18-01/G18-03: unconsumed Local evidence remains reachable: " + mode);
        if (mode is "held" or "failed")
        {
            var kept = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Single();
            await RunPeerSelectionAsync(fixture, kept);
            var residual = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Single();
            Check.Require(fixture.Inner.ReadWork(residual).Any(work => work.Notice.LocalContentDirty) &&
                fixture.Inner.Issues(PairId).Any(issue => issue.Kind == IssueKind.Failure),
                "G18-01: the kept dirty row reaches its own visible holder");
            fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(path, new[] { IssueKind.Failure })] });
        }
        fixture.DropboxView = null;
        if (mode == "skipped") fixture.Local.Seed(local, [2]);
        var next = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Single();
        await RunPeerSelectionAsync(fixture, next);
        Check.Require((await fixture.Dropbox.ReadOneAsync(folder ? Plain : path, default))!.ContentHash ==
            DropboxContentHash.Compute(new byte[] { 2 }),
            "G18-01/G18-03: kept evidence uploads the edit or discovers the child without Repair: " + mode);
        Check.Require(!PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Any() &&
            !PendingWork.ByIdentity(fixture.Inner, Side.Dropbox, remoteWork.Domain, DropboxId).Any(),
            "G18-01/G18-03: both sides settle without spinning: " + mode);
    }

    public static void FailedSelectionReleasesClaim() => FailedSelectionReleasesClaimAsync().GetAwaiter().GetResult();

    private static async Task FailedSelectionReleasesClaimAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var local = LocalEntry(Item, LocalId);
        fixture.Local.Seed(local, [2]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed,
            Item, null, LocalId, local, false, false));
        var peer = new SelectionPeer(PathNames.DropboxKey(Item), selection.ThroughWorkId);
        var failure = new InvalidOperationException(AttachFault);
        var context = fixture.Context(selection) with
        {
            Events = _ =>
            {
                fixture.Inner.ClaimSelectionPeer(selection, peer);
                throw failure;
            }
        };
        Check.Require(ReferenceEquals(await ThrowsAsync<InvalidOperationException>(
            () => RunPeerSelectionAsync(fixture, selection, context)), failure),
            "the executor propagates the injected mid-selection failure");
        await ThrowsAsync<LiveAdmissionStaleException>(() =>
        {
            fixture.Inner.ClaimSelectionPeer(selection, peer);
            return Task.CompletedTask;
        });
        Check.Require(fixture.Inner.ReadWork(selection).Single().WorkId == selection.AnchorWorkId,
            "executor failure releases its claim without retiring unfinished work");
    }

    private static async Task RunPeerSelectionAsync(Fixture fixture, WorkSelection selection,
        ExecutorContext? context = null)
    {
        var boundary = new Boundary(request =>
        {
            fixture.Inner.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint);
            return new Lease { AllowedKeys = LockKeys.ForScopes(fixture.Pair, request.Footprint).Select(scope => scope.Key).ToArray() };
        });
        context ??= fixture.Context(selection);
        var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(), boundary,
            RefusingExecutionBoundary.Instance, 0));
        boundary.Capture = (_, _, _) => Task.FromResult(Coverage(selection));
        var runner = new SelectionPass(context, jobs.Items, jobs.Ancestors, boundary, jobs.Actions,
            jobs.Items.Rows, (path, _) => context.Events(RunEvent.Stage(TextKey.StageRows, inProgress: true, path: path)),
            (selected, side, scope, retries, settles, ct) => SyncJobs.RunBatchAsync(context, new SyncPorts(new RecoveryBoundary(), boundary,
                RefusingExecutionBoundary.Instance, 0), selected, scope, ct));
        Check.Require(await runner.RunAsync(selection, default) is true, "G18: production selection completes");
    }

    private static async Task HeldRepeatedNoticeAsync()
    {
        using var fixture = new Fixture();
        var local = LocalEntry(Item, LocalId);
        fixture.Local.Seed(local, [2]);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, Item,
                new IssueSubject([new IssueItem(Side.Local, Item, LocalId)], null))]
        });
        var first = new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, null, false, false);
        var selection = Seed(fixture, first);
        var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
        var position = fixture.Inner.ReadJournal(VolumeKey)!;
        var through = fixture.Inner.AcquireJournalPage(VolumeKey, position, position,
            [(PairId, Generation, source.Epoch, source.Revision, false)],
            [(PairId, first with { LocalContentDirty = true })])!.Value;
        selection = selection with { ThroughWorkId = through };
        var reads = new CountingSide(fixture.Local);
        fixture.LocalView = reads;
        Check.Require(await Runner(fixture, selection, new Boundary(_ => new Lease()),
            (_, _, _) => throw new InvalidOperationException("a held branch needs no coverage"))
            .RunAsync(selection, default) is true, "two notices for a held item attach without stopping the opportunity");
        Check.Require(reads.Reads == 1, "106 a repeated primary visit neither reads nor supplies another observation");
        var residual = PendingAt(fixture, Side.Local, Item).Single();
        Check.Require(residual.AnchorWorkId > through && fixture.Inner.ReadLocalDirty(PairId, Generation,
            source.Epoch, VolumeKey, LocalId, PathNames.Key(Item), null).Dirty,
            "one residual preserves the dirty fact from either held notice");
    }

    private static async Task PeerCasesAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var tie in new[] { false, true })
        {
            using var fixture = new Fixture();
            var local = LocalEntry(Item, LocalId) with { Modified = Stamp.AddDays(-2) };
            var remote = RemoteEntry(Item, DropboxId) with { Modified = local.Modified };
            if (!tie)
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
            if (tie)
            {
                local = local with { Modified = DateTimeOffset.UnixEpoch };
                remote = remote with { Modified = local.Modified, ContentHash = DropboxContentHash.Compute(new byte[] { 3 }) };
            }
            fixture.Local.Seed(local, [2]);
            fixture.Dropbox.Seed(remote, tie ? [3] : [1]);
            var localWork = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null,
                LocalId, local, true, false));
            var remoteWork = SeedRemote(fixture, remote);
            var selection = side == Side.Local ? localWork : remoteWork;
            var watermark = fixture.Inner.ReadWorkWatermark();
            var boundary = new Boundary(_ => new Lease());
            Check.Require(await Runner(fixture, selection, boundary, (_, _, _) =>
                Task.FromResult(Coverage(selection))).RunAsync(selection, default) is true,
                "A41/A42/A72: one bilateral selection settles the observed peer");
            Check.Require(boundary.Requests.Single().Single().Coverage.Peer ==
                new SelectionPeer(PathNames.KeyFor(PathNames.Other(side), Item), watermark),
                "the peer carries the exact other-side path and pre-observation watermark");
            if (!tie)
                Check.Require(fixture.Hooks.CommittedSelections.Any(committed => committed with { Peer = null } == selection &&
                    committed.Peer == boundary.Requests.Single().Single().Coverage.Peer),
                    "G18: commit carries the exact observed peer without changing opportunity identity");
            Check.Require(PendingAt(fixture, Side.Local, Item)
                .All(work => work.AnchorWorkId > watermark), "the original Local acquired rows retire");
            if (!tie || side == Side.Dropbox)
                Check.Require(!fixture.Inner.ReadLocalDirty(PairId, Generation, localWork.SourceEpoch, VolumeKey,
                    LocalId, PathNames.Key(Item), null).Dirty, "consumed Local dirty evidence retires");
            if (tie)
            {
                Check.Require(fixture.Inner.Issues(PairId).Single().Kind == IssueKind.Tie &&
                    fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1,
                    "A41: both edits raise and attach the Tie without refusing: " +
                    string.Join(",", fixture.Inner.Issues(PairId).Select(issue => issue.Kind + ":" + issue.Cause)) +
                    "; attachments=" + fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)));
            }
            else
            {
                Check.Require((await fixture.Dropbox.ReadOneAsync(Item, default))!.ContentHash ==
                    DropboxContentHash.Compute(new byte[] { 2 }),
                    "A72: the Local peer with identical id, size and time is hashed and uploaded");
                var echo = SeedRemote(fixture, (await fixture.Dropbox.ReadOneAsync(Item, default))!);
                Check.Require(await Runner(fixture, echo, new Boundary(_ => new Lease()), (_, _, _) =>
                    Task.FromResult(Coverage(echo))).RunAsync(echo, default) is true,
                    "A42: the next upload echo is processed normally");
                Check.Require(!PendingAt(fixture, Side.Dropbox, Item).Any(),
                    "the echo and consumed peer rows retire");
            }
        }
    }

    private static WorkSelection SeedRemote(Fixture fixture, Entry remote, ChangeNotice? notice = null, int copies = 1)
    {
        var source = fixture.Inner.ReadSource(PairId, Side.Dropbox)!.Value;
        var roots = fixture.Pair.Roots!.Dropbox;
        var provenance = source.Provenance ?? new SourceProvenance(roots.AccountId, roots.RootNamespaceId,
            roots.FolderId, fixture.Pair.DropboxRoot, true, true, true, true, Generation, source.Epoch);
        if (source.Provenance is null)
        {
            fixture.Inner.AdvanceSource(PairId, Side.Dropbox, Generation, source.Epoch, source.Revision,
                null, source.Cursor, source.Epoch, source.Revision, provenance, source.Cursor, source.Obligation);
            source = fixture.Inner.ReadSource(PairId, Side.Dropbox)!.Value;
        }
        var work = fixture.Inner.AcquireDropboxPage(PairId, Generation, source.Epoch, source.Revision,
            provenance, source.Cursor,
            Enumerable.Repeat(notice ?? new ChangeNotice(Side.Dropbox, ChangeKind.Changed, remote.Path, null, remote.Id, remote, false, false), copies).ToArray(), Cursor);
        return new WorkSelection(PairId, Generation, source.Epoch, Side.Dropbox, provenance.NamespaceId,
            remote.Id, PathNames.KeyFor(Side.Dropbox, remote.Path), work - copies + 1, work);
    }

    // PendingAncestors omits held work unless a coverage watermark is supplied; 0 is below every work id, so the
    // in-memory fixture sees exactly the acquired rows the retired identity-keyed read returned.
    private static IReadOnlyList<WorkSelection> PendingAt(Fixture fixture, Side side, params string[] paths) =>
        fixture.Inner.PendingAncestors(PairId, Generation, side,
            paths.Select(path => PathNames.KeyFor(side, path)).ToArray(), 0).ToArray();

    public static void RunLiveReplaceSafety() => LiveFolderReplaceOverUnreadableChildAsync().GetAwaiter().GetResult();

    public static void RunRaisedHoldSafety() => RaisedHoldCoversSelectionChildrenAsync().GetAwaiter().GetResult();

    public static void RunHeldRenameSafety() => RenameUnderHeldFolderHoldsAsync().GetAwaiter().GetResult();

    private static void TrustEchoSources(Fixture fixture)
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var source = fixture.Inner.ReadSource(PairId, side)!.Value;
            var root = fixture.Pair.Roots!.Dropbox;
            var provenance = side == Side.Local ? source.Provenance : new SourceProvenance(root.AccountId,
                root.RootNamespaceId, root.FolderId, fixture.Pair.DropboxRoot, true, true, true, true, Generation, source.Epoch);
            fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, source.Epoch, source.Revision, provenance,
                side == Side.Dropbox ? Cursor : source.Cursor, null);
        }
    }

    private static async Task FreshDropboxSelectionAsync()
    {
        foreach (var mode in new[] { "present", "absent", "failure", "identity-deleted", "three-rows" })
        {
            using var fixture = new Fixture(realStore: true);
            var local = LocalEntry(Item, LocalId);
            var remote = RemoteEntry(Item, DropboxId);
            var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
            fixture.Inner.AcquireJournalPage(VolumeKey, null, new JournalPosition(1, 1),
                [(PairId, Generation, source.Epoch, source.Revision, false)], []);
            if (mode is "present" or "three-rows")
            {
                fixture.Local.Seed(local, [1]);
                fixture.Dropbox.Seed(remote, [1]);
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
            }
            if (mode == "three-rows") fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, Item,
                    new IssueSubject([new IssueItem(Side.Dropbox, Item, DropboxId)], null))]
            });
            var notice = new ChangeNotice(Side.Dropbox, ChangeKind.Changed, Item, null, DropboxId,
                remote with { Revision = "stale", Size = 9 }, false, mode == "identity-deleted");
            var selected = SeedRemote(fixture, remote, notice, mode == "three-rows" ? 3 : 1);
            if (mode == "three-rows")
                Check.Require(fixture.Inner.ReadWork(selected).Count() == 3, "121 fixture has three same-identity rows");
            if (mode == "failure") fixture.Dropbox.FailNext(Item,
                new ItemFailedException(Item, TextKey.ReadDropboxFailed, null));
            var localCounter = new CountingSide(fixture.Local);
            var remoteCounter = new CountingSide(fixture.Dropbox);
            fixture.LocalView = localCounter;
            fixture.DropboxView = remoteCounter;
            Check.Require(await RunSelectionAsync(fixture, selected), "121 selection completes or retains failure: " + mode);
            Check.Require(remoteCounter.Reads == 1 && localCounter.Reads == (mode == "failure" ? 0 : 1),
                "121 primary Dropbox read counts: " + mode + "; Dropbox=" + remoteCounter.Reads + "; Local=" + localCounter.Reads);
            Check.Require(localCounter.Work == 0 && remoteCounter.Work == 0, "121 observation has zero effects: " + mode);
            var issues = fixture.Inner.Issues(PairId);
            Console.WriteLine($"121 selection={mode}; issueCount={issues.Count}; kinds={string.Join(",", issues.Select(issue => issue.Kind))}");
            // In-sync observations close a stale NameClash, even when repeated acquired rows name it.
            Check.Require(mode == "failure" ? issues.Count == 1 && issues[0].Kind == IssueKind.Failure : issues.Count == 0,
                "121 failure remains a Failure; absent or in-sync observations leave no stale notice: " + mode);
            Console.WriteLine($"121 selection={mode}; Dropbox={remoteCounter.Reads}; Local={localCounter.Reads}");
        }
    }

    private static async Task DropboxDeleteFreshnessAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var local = LocalEntry(Item, LocalId);
        var remote = RemoteEntry(Item, DropboxId);
        fixture.Local.Seed(local, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
        var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
        fixture.Inner.AcquireJournalPage(VolumeKey, null, new JournalPosition(1, 1),
            [(PairId, Generation, source.Epoch, source.Revision, false)], []);
        var selected = SeedRemote(fixture, remote,
            new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, Item, null, DropboxId, null, false, false));
        var remoteCounter = new CountingSide(fixture.Dropbox);
        var localCounter = new CountingSide(fixture.Local);
        fixture.DropboxView = remoteCounter;
        fixture.LocalView = localCounter;
        var boundary = new Boundary(_ =>
        {
            fixture.Dropbox.Seed(remote with { Revision = "recreated" }, [1]);
            return new Lease();
        });
        var ready = await Runner(fixture, selected, boundary, (_, _, _) => Task.FromResult(Coverage(selected)))
            .RunAsync(selected, default);
        Check.Require(ready is true && remoteCounter.Reads == 3 && localCounter.Reads == 3 &&
            localCounter.Deletes == 0 && remoteCounter.Deletes == 0 &&
            await fixture.Local.ReadOneAsync(Item, default) == local &&
            await fixture.Dropbox.ReadOneAsync(Item, default) == remote with { Revision = "recreated" } &&
            fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Item)) == SyncedEntries.From(local, remote) &&
            PendingAt(fixture, Side.Dropbox, Item).Count == 0 && fixture.Inner.Issues(PairId).Count == 0,
            "121 recreated Dropbox item converges after one reobservation, preserving both files without deletes");
        Console.WriteLine($"121 delete-race Dropbox={remoteCounter.Reads}; deletes={localCounter.Deletes + remoteCounter.Deletes}");
    }

    private static async Task DropboxPostRenameFailureAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var old = LocalEntry(OldPath, LocalId);
        var remote = RemoteEntry(OldPath, DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(old, remote)] });
        fixture.Local.Seed(old, [1]);
        var moved = remote with { Path = Item, Size = 2, Revision = "rev-2", ContentHash = DropboxContentHash.Compute(new byte[] { 2, 2 }) };
        fixture.Dropbox.Seed(moved, [2, 2]);
        var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
        fixture.Inner.AcquireJournalPage(VolumeKey, null, new JournalPosition(1, 1),
            [(PairId, Generation, source.Epoch, source.Revision, false)], []);
        var selected = SeedRemote(fixture, moved,
            new ChangeNotice(Side.Dropbox, ChangeKind.Renamed, Item, OldPath, DropboxId, null, false, false));
        var reads = 0;
        fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeRead: path => { if (path == Item) reads++; });
        var failed = false;
        fixture.LocalView = new ProxySide(fixture.Local)
        {
            BlockPublish = (_, _) =>
            {
                if (!failed)
                {
                    failed = true;
                    throw new ItemFailedException(Item, TextKey.ReadLocalFailed, null);
                }
                return Task.CompletedTask;
            }
        };
        Check.Require(await RunSelectionAsync(fixture, selected), "121 post-rename failure is retained");
        Check.Require(reads == 3 && failed, "121 Dropbox rename reads initial, post-rename and admitted metadata once each");
        var after = fixture.Inner.SyncedById(PairId, Side.Dropbox, DropboxId)!;
        Check.Require(after.DropboxPath == Item && fixture.Inner.Issues(PairId).Any(issue => issue.Kind == IssueKind.Failure),
            "121 Dropbox rename commits before download failure");
        var residual = PendingWork.ByIdentity(fixture.Inner, Side.Dropbox, selected.Domain, DropboxId).Single();
        Check.Require(fixture.Inner.ReadWork(residual).All(work => work.Before == after), "121 residual retains advanced baseline");
        Console.WriteLine($"121 post-rename Dropbox={reads}");
        fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(Item, IssueClosure.AfterEffect)] });
        Check.Require(await RunSelectionAsync(fixture, residual), "121 post-rename residual retries to completion");
    }

    private static async Task FreshLocalSelectionAsync()
    {
        foreach (var mode in new[] { "absent", "identity-deleted", "dropbox", "old-path", "walked", "known-folder" })
        {
            using var fixture = new Fixture(realStore: true);
            var folder = mode is "walked" or "known-folder";
            if (mode == "walked") fixture.Hooks.RepairDone = false;
            var path = folder ? Folder : Item;
            var entry = folder ? FolderEntry(path, FolderId) : LocalEntry(path, LocalId);
            if (mode != "absent")
            {
                var remote = entry.IsFolder ? FolderEntry(path, DropboxId) : RemoteEntry(path, DropboxId);
                fixture.Local.Seed(entry, entry.IsFolder ? null : [1]);
                fixture.Dropbox.Seed(remote, remote.IsFolder ? null : [1]);
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(entry, remote)] });
            }
            if (folder)
            {
                var child = LocalEntry(Plain, LocalId);
                var remoteChild = RemoteEntry(Plain, DropboxId + "-child");
                fixture.Local.Seed(child, [1]);
                fixture.Dropbox.Seed(remoteChild, [1]);
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(child, remoteChild)] });
            }
            var side = mode == "dropbox" ? Side.Dropbox : Side.Local;
            var notice = new ChangeNotice(side, ChangeKind.Changed, path,
                mode == "old-path" ? OldPath : null, null, entry, false, mode == "identity-deleted");
            if (mode == "dropbox")
            {
                var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
                fixture.Inner.AcquireJournalPage(VolumeKey, null, new JournalPosition(1, 1),
                    [(PairId, Generation, source.Epoch, source.Revision, false)], []);
            }
            var selected = mode == "dropbox" ? SeedRemote(fixture, RemoteEntry(path, DropboxId)) : Seed(fixture, notice);
            var changedChild = LocalEntry(Plain, LocalId) with { Size = 2, Modified = Stamp.AddMinutes(1) };
            WorkSelection? childSelection = null;
            if (mode == "known-folder")
            {
                fixture.Local.Seed(changedChild, [2, 3]);
                childSelection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Plain, null,
                    LocalId, changedChild, true, false), 2);
            }
            var reads = new List<string>();
            var counted = new CountingSide(fixture.Local);
            fixture.LocalView = new HookedSide(counted, beforeRead: reads.Add);
            Console.WriteLine("106 observation case=" + mode);
            await RunSelectionAsync(fixture, selected);
            Check.Require(reads.Count(value => value == path) == 1,
                "106 primary observation is fresh, including absent and identity-deleted: " + mode + "; reads=" + string.Join(",", reads));
            if (mode == "old-path") Check.Require(reads.Contains(OldPath), "106 old path reads independently");
            if (mode == "walked") Check.Require(reads.Contains(Plain), "106 walked children read independently");
            if (childSelection is { } childWork)
            {
                Check.Require(counted.Listings == 0 && !reads.Contains(Plain) && PendingAt(fixture, Side.Local, Folder).Count == 0 &&
                    PendingAt(fixture, Side.Local, Plain).Count == 1,
                    "A known folder touch retires without walking or consuming its child's own event");
                Check.Require(await RunSelectionAsync(fixture, childWork), "The independently queued child completes");
                var syncedChild = await fixture.Dropbox.ReadOneAsync(Plain, default);
                Check.Require(reads.Contains(Plain) && syncedChild?.ContentHash == DropboxContentHash.Compute(new byte[] { 2, 3 }) &&
                    fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Plain)) == SyncedEntries.From(changedChild, syncedChild!) &&
                    PendingAt(fixture, Side.Local, Plain).Count == 0,
                    "A child event acquired beside the folder touch is read, synced and retired independently");
            }
        }
    }

    private static async Task LocalEchoCasesAsync()
    {
        foreach (var mode in new[] { "echo", "queued-remote", "later-remote", "completion-remote", "local-edit", "dirty", "engine-write", "deleted",
            "cursor", "provenance", "local-obligation", "remote-obligation", "issue", "ancestor", "unrelated", "retry" })
        {
            using var fixture = new Fixture(realStore: true);
            TrustEchoSources(fixture);
            var path = Folder + "/" + Item;
            fixture.Local.Seed(FolderEntry(Folder, FolderId), null);
            fixture.Dropbox.Seed(FolderEntry(Folder, "remote-parent"), null);
            var local = LocalEntry(path, LocalId);
            var remote = RemoteEntry(path, DropboxId);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
            fixture.Dropbox.Seed(remote, [1]);
            if (mode != "deleted") fixture.Local.Seed(local, mode == "local-edit" ? [2] : [1]);
            // UsnPaths resolves the stage id at its final name after MoveStage; content-write reasons stay dirty.
            var selected = Seed(fixture, new ChangeNotice(Side.Local,
                mode == "deleted" ? ChangeKind.Deleted : ChangeKind.Changed, path, null, LocalId,
                mode == "deleted" ? null : local, mode is "local-edit" or "dirty" or "engine-write", mode == "deleted"));
            if (mode == "queued-remote")
            {
                var changed = remote with { ContentHash = DropboxContentHash.Compute(new byte[] { 2 }), Revision = "rev-2" };
                fixture.Dropbox.Seed(changed, [2]);
                SeedRemote(fixture, changed);
            }
            if (mode is "cursor" or "provenance" or "local-obligation" or "remote-obligation")
            {
                var side = mode == "local-obligation" ? Side.Local : Side.Dropbox;
                var source = fixture.Inner.ReadSource(PairId, side)!.Value;
                fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                    source.Provenance, source.Cursor, source.Epoch, source.Revision,
                    mode == "provenance" ? null : source.Provenance, mode is "cursor" or "provenance" ? null : source.Cursor,
                    mode.EndsWith("obligation", StringComparison.Ordinal) ? SourceObligation.NormalRepair : null);
            }
            if (mode is "issue" or "ancestor" or "unrelated" or "retry")
            {
                var issuePath = mode == "ancestor" ? Folder.ToUpperInvariant() : mode == "unrelated" ? "elsewhere" : path;
                fixture.Inner.Commit(PairId, StateChange.Empty with
                {
                    Raise = [(mode == "retry" ? IssueKind.Failure : IssueKind.Info,
                        mode == "retry" ? TextKey.RequiredRepairRetry : TextKey.InfoNewestWins, issuePath,
                        new IssueSubject([], null))]
                });
            }
            if (mode == "completion-remote") fixture.Hooks.BeforeCall = method =>
            {
                if (method.Name != nameof(IStateStore.CompleteWork)) return;
                fixture.Hooks.BeforeCall = null;
                SeedRemote(fixture, remote);
            };
            var localReads = new CountingSide(fixture.Local);
            fixture.LocalView = localReads;
            var reads = 0;
            fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeRead: _ => reads++);
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)fixture.Inner.GetType().GetField("_connection",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Inner)!;
            var statements = 0;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, _) => statements++), null);
            try { await RunSelectionAsync(fixture, selected); }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
            Console.WriteLine("90a mode=" + mode + "; sql=" + statements + "; providerReads=" + reads);
            if (mode == "echo")
                Check.Require(localReads.Reads == 1 && reads == 0,
                    "106 clean Local echo reads Local once and never calls Dropbox");
            var shortcut = mode is "echo" or "later-remote" or "completion-remote" or "unrelated";
            if (mode == "completion-remote")
                Check.Require(PendingAt(fixture, Side.Dropbox, path).Any(),
                    "90a Local-only completion never retires an unobserved Dropbox arrival");
            Check.Require(shortcut ? reads == 0 : reads > 0, "90a provider-read guard: " + mode + "; reads=" + reads);
            Check.Require(!PendingAt(fixture, Side.Local, path).Any(), "90a Local notice retires: " + mode);
            if (mode == "queued-remote")
                Check.Require(await fixture.Local.ContentHashAsync((await fixture.Local.ReadOneAsync(path, default))!, default) ==
                    DropboxContentHash.Compute(new byte[] { 2 }), "90A-S1: pre-acquired remote B reaches Local");
            if (mode == "local-edit")
                Check.Require((await fixture.Dropbox.ReadOneAsync(path, default))!.ContentHash ==
                    DropboxContentHash.Compute(new byte[] { 2 }), "90a genuine Local edit reaches Dropbox");
            if (mode == "deleted")
                Check.Require(await fixture.Dropbox.ReadOneAsync(path, default) is null, "90a deletion is not an echo");
            if (mode == "later-remote")
            {
                var changed = remote with { ContentHash = DropboxContentHash.Compute(new byte[] { 2 }), Revision = "rev-2" };
                fixture.Dropbox.Seed(changed, [2]);
                var later = SeedRemote(fixture, changed);
                await RunSelectionAsync(fixture, later);
                Check.Require(reads > 0 && await fixture.Local.ContentHashAsync(
                    (await fixture.Local.ReadOneAsync(path, default))!, default) == DropboxContentHash.Compute(new byte[] { 2 }),
                    "90a a remote edit acquired after Local completion remains reachable");
            }
        }
    }

    private static async Task LocalEchoResponsibilityAsync()
    {
        foreach (var mode in new[] { "pending", "folder", "alias", "retry", "children" })
        {
            var attempt = Guid.NewGuid();
            using var fixture = new Fixture(realStore: true, registeredAttempt: attempt);
            TrustEchoSources(fixture);
            var path = Folder + "/" + Item;
            var local = LocalEntry(path, LocalId);
            var saved = SyncedEntries.From(local, RemoteEntry(path, DropboxId));
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
            var notice = new ChangeNotice(Side.Local, ChangeKind.Changed, path, null, LocalId, local, false, false);
            var selected = Seed(fixture, notice);
            Check.Require(fixture.Inner.BeginSelection(selected), "Selection setup must be admitted.");
            var unheld = fixture.Inner.CreateLocalEchoProbe(selected);
            if (mode == "pending")
            {
                var probe = fixture.Inner.CreateLocalEchoProbe(selected);
                Check.Require(probe(saved, selected.ThroughWorkId), "90a initially no pending effect");
                var pending = new PendingEffect(1, "operation", 0, PendingKind.LocalDelete, PendingPhase.Armed,
                    path, new RemovalProof(local, VolumeKey, saved.DropboxContentHash), saved,
                    null, null, null, null, false, null);
                fixture.Inner.ArmPending(PairId, pending);
                Check.Require(!probe(saved, selected.ThroughWorkId), "90a pending effect is checked fresh after snapshot");
                fixture.Inner.SettlePending(PairId, pending, StateChange.Empty);
                Check.Require(probe(saved, selected.ThroughWorkId), "90a settled pending effect permits echo");
                continue;
            }
            if (mode == "folder")
            {
                var root = FolderEntry(Folder, DropboxId);
                using var plan = fixture.Inner.OpenFolderPlan(PairId,
                    Row.ForOneSide(Side.Dropbox, PathNames.Key(Folder), root, null, [], null, RowContext.Neutral), Side.Dropbox);
                await plan.CaptureAsync(fixture.Dropbox.ReadAllAsync(Folder,
                    new ListOptions(true, false, false), default), default);
                foreach (var child in plan.Children(default))
                    plan.MarkClassified(child.Key, child.NativeKey,
                        new FolderChild(child.Entry, child.Synced, FolderVerdict.Removed), false, default);
                plan.Complete(true, default);
                fixture.Inner.BeginFolderRemoval(PairId, plan, 1, default);
            }
            else if (mode == "retry")
            {
                var subject = new IssueSubject([new IssueItem(Side.Local, path, LocalId)], null);
                var episode = fixture.Inner.RecordInUseFailure(PairId, Generation, selected.SourceEpoch, path,
                    subject, TextKey.FailLocalInUse, selected, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
                Check.Require(!unheld(saved, selected.ThroughWorkId),
                    "90a newly raised retry responsibility is checked fresh after snapshot");
                Check.Require(!fixture.Inner.CreateLocalEchoProbe(selected)(saved, selected.ThroughWorkId),
                    "90a real in-use episode prevents settlement");
                Check.Require(fixture.Inner.TryClaimItemRetry(episode, attempt), "90a retry is claimed by registered attempt");
                fixture.Inner.CompleteItemAttempt(episode, attempt, StateChange.Empty, null, selected);
                Check.Require(fixture.Inner.ReadItemRetryState(PairId, [episode.Token.IssueId])[episode.Token.IssueId] is null,
                    "90a successful retry leaves no automatic episode");
                continue;
            }
            else
            {
                fixture.Inner.Commit(PairId, StateChange.Empty with { Raise =
                    [(mode == "alias" ? IssueKind.Info : IssueKind.Failure, TextKey.FailChanged, "elsewhere",
                        new IssueSubject([new IssueItem(Side.Dropbox, path.ToUpperInvariant(), DropboxId)], null))] });
                if (mode == "children")
                {
                    var issue = fixture.Inner.Issues(PairId).Single();
                    fixture.Inner.AttachIssueWork(selected, issue, [(notice, false, saved)]);
                    Check.Require(!unheld(saved, selected.ThroughWorkId),
                        "90a acquired child responsibility blocks even with a pre-issue snapshot");
                }
            }
            Check.Require(!fixture.Inner.CreateLocalEchoProbe(selected)(saved, selected.ThroughWorkId),
                "90a responsibility guard: " + mode);
            if (mode == "children")
            {
                fixture.Inner.CompleteWork(selected, null);
                var residual = PendingAt(fixture, Side.Local, path).Single();
                Check.Require(!fixture.Inner.CreateLocalEchoProbe(residual)(saved, residual.ThroughWorkId),
                    "90a residual issue work is not an echo");
            }
        }
    }

    private static async Task LocalEchoReadFailureAsync()
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        var local = LocalEntry(Item, LocalId);
        var remote = RemoteEntry(Item, DropboxId);
        var saved = SyncedEntries.From(local, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        fixture.Local.Seed(local with { Size = 2 }, [1, 2]);
        fixture.Dropbox.FailNext(Item, new ItemFailedException(Item, Text.ReadFailedOn(Side.Dropbox), null));
        var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId,
            local, true, false));
        Check.Require(await RunSelectionAsync(fixture, selected), "90a read failure retains its original responsibility");
        var residual = fixture.Inner.ReadWork(PendingAt(fixture, Side.Local, Item).Single()).Single();
        Check.Require(residual.Notice.Observed == local && residual.Before == saved,
            "90a failed bilateral read retains notice observation and work.Before, not partial Local observation");
    }

    private static async Task LocalEchoFolderAsync()
    {
        // Two siblings distinguish subtree discovery from a single-item shortcut; the walk itself is unchanged.
        foreach (var guarded in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: true);
            TrustEchoSources(fixture);
            // This case exercises per-file echo decisions in the pre-Repair child walk, not folder-batch admission.
            fixture.Hooks.RepairDone = false;
            var local = FolderEntry(Folder, LocalId);
            var remote = FolderEntry(Folder, DropboxId);
            fixture.Local.Seed(local, null);
            fixture.Dropbox.Seed(remote, null);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
            foreach (var name in new[] { "first", "second" })
            {
                var child = LocalEntry(Folder + "/" + name, LocalId + name);
                var counterpart = RemoteEntry(child.Path, DropboxId + name);
                fixture.Local.Seed(child, [1]);
                fixture.Dropbox.Seed(counterpart, [1]);
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(child, counterpart)] });
            }
            if (guarded) fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Info, TextKey.InfoNewestWins, (Folder + "/first").ToUpperInvariant(), new IssueSubject([], null))]
            });
            var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null, LocalId,
                local, false, false));
            var reads = new List<string>();
            fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeRead: path => reads.Add(path));
            await RunSelectionAsync(fixture, selected);
            Check.Require(guarded ? reads.Contains(Folder) && reads.Contains(Folder + "/first") &&
                !reads.Contains(Folder + "/second") : reads.Count == 0,
                "90a touched folder and unchanged discovered children skip provider reads except overlapping issues");
        }
    }

    private static void LocalEchoIssueCounts()
    {
        // A small fixture distinguishes one streamed snapshot from repeated per-item scans.
        foreach (var count in new[] { 1, 3 })
        {
            using var fixture = new Fixture(realStore: true);
            TrustEchoSources(fixture);
            var local = LocalEntry(Item, LocalId);
            var remote = RemoteEntry(Item, DropboxId);
            var saved = SyncedEntries.From(local, remote);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved],
                Raise = Enumerable.Range(0, count).Select(index => (IssueKind.Info, TextKey.InfoNewestWins,
                    "unrelated/" + index, new IssueSubject([], null))).ToArray() });
            var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, local, false, false));
            Check.Require(fixture.Inner.BeginSelection(selected), "Selection setup must be admitted.");
            const int repetitions = 3; // Repeated callers in one pass, not a timing benchmark.
            fixture.Inner.SelectWork(PairId, Generation, new PathScope(fixture.Pair, [fixture.Pair]));
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)fixture.Inner.GetType().GetField("_connection",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Inner)!;
            var snapshots = 0;
            var pairReads = 0;
            var localReads = 0;
            var dropboxReads = 0;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, sql) =>
            {
                var text = sql.utf8_to_string();
                if (text.StartsWith("SELECT path,subject_json,NULL ", StringComparison.Ordinal)) snapshots++;
                if (text.StartsWith("SELECT id,execution_generation,first_repair_done,def_json FROM pair ", StringComparison.Ordinal)) pairReads++;
                if (text.StartsWith("SELECT source_epoch,provenance_json,cursor,repair_reason,repair_revision FROM source_state ", StringComparison.Ordinal))
                {
                    if (text.EndsWith("side=0;", StringComparison.Ordinal)) localReads++;
                    if (text.EndsWith("side=1;", StringComparison.Ordinal)) dropboxReads++;
                }
            }), null);
            try
            {
                for (var repeat = 0; repeat < repetitions; repeat++)
                {
                    var probe = fixture.Inner.CreateLocalEchoProbe(selected);
                    pairReads = localReads = dropboxReads = 0;
                    Check.Require(probe(saved, selected.ThroughWorkId), "90a finite pass reuses responsibility snapshot");
                    Check.Require(pairReads == 1 && localReads == 1 && dropboxReads == 1,
                        "106 each echo guard reads the pair and each source fence once");
                    Console.WriteLine($"106 guard pair={pairReads}; local={localReads}; dropbox={dropboxReads}");
                }
                Check.Require(snapshots == 1, "90a responsibility streams once per finite pass");
                fixture.Inner.EndWorkPass(PairId);
                fixture.Inner.Commit(PairId, StateChange.Empty with { Raise =
                    [(IssueKind.Info, TextKey.InfoNewestWins, Item, new IssueSubject([], null))] });
                fixture.Inner.SelectWork(PairId, Generation, new PathScope(fixture.Pair, [fixture.Pair]));
                Check.Require(!fixture.Inner.CreateLocalEchoProbe(selected)(saved, selected.ThroughWorkId),
                    "90a the next pass observes newly raised responsibility");
                Check.Require(snapshots == 2, "90a next pass refreshes responsibility snapshot");
                Console.WriteLine("90a snapshot openIssues=" + count + "; firstPassCalls=" + repetitions +
                    "; passes=2; snapshots=" + snapshots);
            }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
        }
    }

    private static async Task CrossBoundDeletionAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            var local = LocalEntry(side == Side.Local ? "saved-copy" : Item, LocalId);
            var remote = RemoteEntry(side == Side.Dropbox ? "saved-copy" : Item, DropboxId);
            fixture.Local.Seed(local, [1]);
            fixture.Dropbox.Seed(remote, [1]);
            var saved = SyncedEntries.From(local, remote);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
            var notice = new ChangeNotice(side, ChangeKind.Deleted, Item, null, "deleted-id", null, false, false);
            WorkSelection selected;
            if (side == Side.Local) selected = Seed(fixture, notice);
            else
            {
                SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
                selected = SeedRemote(fixture, RemoteEntry(Item, "deleted-id"));
                var source = fixture.Inner.ReadSource(PairId, side)!.Value;
                fixture.Inner.AcquireDropboxPage(PairId, Generation, source.Epoch, source.Revision,
                    source.Provenance!, source.Cursor, [notice], Cursor);
            }
            var countedLocal = new CountingSide(fixture.Local);
            var countedDropbox = new CountingSide(fixture.Dropbox);
            var events = new List<RunEvent>();
            var context = fixture.Context(selected) with
            {
                Local = countedLocal, Dropbox = countedDropbox, Events = events.Add
            };
            var boundary = new Boundary(_ => new Lease())
            {
                Capture = (selection, _, _) => Task.FromResult(Coverage(selection))
            };
            var syncer = new Syncer(context, new SyncPorts(new RecoveryBoundary(), boundary,
                RefusingExecutionBoundary.Instance, 0));
            await syncer.SyncWorkAsync(default);
            Check.Require(!PendingAt(fixture, side, Item).Any() &&
                fixture.Inner.SelectWork(PairId, Generation, context.Scope) is null,
                "A deletion whose counterpart belongs elsewhere retires from live selection");
            Check.Require(countedLocal.Work == 0 && countedDropbox.Work == 0 &&
                fixture.Inner.Issues(PairId).Count == 0 && events.All(e => e.Error is null),
                "Retiring the unrelated deletion has zero file effects, failures or notices");
            Check.Require(fixture.Inner.SyncedByKey(PairId, Side.Local, saved.LocalKey) == saved &&
                await fixture.Local.ReadOneAsync(local.Path, default) == local &&
                await fixture.Dropbox.ReadOneAsync(remote.Path, default) == remote,
                "The cross-name binding and both saved files remain unchanged");
        }
    }

    private static async Task RunAsync()
    {
        var failures = new List<Exception>();
        try { await StaleFreshnessAsync(); }
        catch (Exception error) { failures.Add(error); }
        try { await OrdinarySelectionsAsync(); }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static async Task OrdinarySelectionsAsync()
    {
        await AbsentObservationRetiresAsync();
        await UnboundCaseFailureClosesAsync();
        await ReleasedHistoryAsync();
        await FreshDropboxSelectionAsync();
        await DropboxDeleteFreshnessAsync();
        await HeldRepeatedNoticeAsync();
        await FreshLocalSelectionAsync();
        await LocalEchoCasesAsync();
        await LocalEchoResponsibilityAsync();
        await LocalEchoReadFailureAsync();
        await LocalEchoFolderAsync();
        LocalEchoIssueCounts();
        await EmptyOpportunityAsync();
        await CrossBoundDeletionAsync();
        await SelectedReadFailuresAsync();
        await ObservationReadOutcomesAsync();
        await GenerationForwardedAsync();
        await StaleGenerationRefusesAsync();
        await BeginSelectionBeforeCaptureAsync();
        await RetainedIssueAsync();
        await ProviderFreeNoticeRetainsAsync();
        await OccupantReadFailureStillRetainsAsync();
        await CommitFailureDoesNotRetainAsync();
        await AttachmentFailureRetainsParentAsync();
        await WrongSelectionRefusesAsync();
        await HeldBranchAttachesBeforeAdmissionAsync(closeBeforeAttach: false);
        await HeldBranchAttachesBeforeAdmissionAsync(closeBeforeAttach: true);
        RunMappedHolds();
        foreach (var rootState in new[] { RootState.Same, RootState.Replaced, RootState.Absent, RootState.Unreadable })
            await RetainedAncestorIdentityAsync(rootState);
        await PostRenameFailureRetainsAdvancedBeforeAsync();
        await DropboxPostRenameFailureAsync();
        await OldAliasFailureRetainsAdvancedBeforeAsync();
        await EmptyBranchPreservesLaterArrivalAsync();
        await SeparateWalkWorkAsync();
        await OwnSelectionFenceAsync();
        await CounterpartAncestorFirstAsync();
        await RefusalMakesNoProviderCallsAsync();
        await FailedAncestorNoticeAsync();
        await FolderDeletionCoversItsChildrenAsync();
        await FolderSurvivorCycleAsync();
        await DeferredSurvivorRestartAsync();
        await FolderCycleDependenciesAsync();
        var moveOutFailures = new List<Exception>();
        foreach (var side in new[] { Side.Local, Side.Dropbox })
            try { await MovedOutOfDeletedFolderAsync(side); }
            catch (Exception error) { moveOutFailures.Add(error); }
        try { await FolderDeletionCoversItsChildrenAsync(refused: true); }
        catch (Exception error) { moveOutFailures.Add(error); }
        if (moveOutFailures.Count != 0) throw new AggregateException(moveOutFailures);
        await MappedAncestorFirstAsync();
        await OldPathAncestorFirstAsync();
        await DeletedDestinationFolderAsync();
        await NestedAncestorOrderAsync();
        await DependencyReadCountAsync();
        await UnreadableReleaseAsync();
        await WrappedWalkFailureAsync();
        await OrdinaryWalkFailureAsync();
        await FolderSurvivorFailureIdentityAsync();
        await LiveReobserveRevokesDirectAsync();
    }

    private static async Task UnboundCaseFailureClosesAsync()
    {
        using var fixture = new Fixture(realStore: true);
        const string oldPath = "Parent/file.txt";
        const string path = "Parent/FILE.txt";
        var local = LocalEntry(path, LocalId);
        var remote = RemoteEntry(path, DropboxId);
        fixture.Local.Seed(local, [1]);
        fixture.Dropbox.Seed(remote, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Raise =
            [(IssueKind.Failure, TextKey.FailChanged, oldPath,
                new IssueSubject([new IssueItem(Side.Local, oldPath, LocalId)], null))] });
        var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, path, null, LocalId, local, false, false));
        ClearObligations(fixture);
        Check.Require(await RunSelectionAsync(fixture, selected) && fixture.Inner.Issues(PairId).Count == 0 &&
            fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(path)) is not null,
            "An unbound case-renamed file settles and closes its old-spelling Failure without Retry or Repair.");
    }

    private static async Task AbsentObservationRetiresAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var residual in new[] { false, true })
        foreach (var path in new[] { "Parent/MixedChild.csv", "PARENT/UPPERCHILD.CSV" })
        foreach (var firstRun in new[] { false, true })
        foreach (var changed in new[] { false, true })
        {
            IReadOnlyDictionary<string, long>? firstCounts = null;
            // 1 and 2 are the smallest distinct positive backgrounds exposing per-binding work for a fixed subject.
            foreach (var background in new[] { 1, 2 })
            {
                using var fixture = new Fixture(realStore: true);
                var local = LocalEntry(path, LocalId);
                var remote = RemoteEntry(path, DropboxId);
                var saved = SyncedEntries.From(local, remote);
                fixture.Inner.Commit(PairId, StateChange.Empty with
                {
                    Record = [SyncedEntries.From(FolderEntry("Parent", "local-parent"),
                        FolderEntry("Parent", "remote-parent")), saved,
                        .. Enumerable.Range(0, background).Select(index => SyncedEntries.From(
                            LocalEntry("Background/Child" + index, "local-" + index),
                            RemoteEntry("Background/Child" + index, "remote-" + index)))],
                    Raise = [(IssueKind.Failure, TextKey.ReadLocalFailed, path,
                        new IssueSubject([new IssueItem(firstRun ? Side.Local : side, path,
                            firstRun || side == Side.Local ? LocalId : DropboxId)], null,
                            FirstRunDeletion: firstRun ? IssueSubject.FirstRunDeletionKind.UnscannedSubtree :
                                IssueSubject.FirstRunDeletionKind.None)),
                        (IssueKind.Info, TextKey.InfoDropboxHistoryUnchecked, "Other/Child", new IssueSubject([], null))]
                });
                var issue = fixture.Inner.Issues(PairId).Single(value => value.Path == path);
                var unrelated = fixture.Inner.Issues(PairId).Single(value => value.Path == "Other/Child");
                var notice = new ChangeNotice(side, ChangeKind.Deleted, path, null,
                    side == Side.Local ? LocalId : DropboxId, null, false, false);
                var selected = side == Side.Local ? Seed(fixture, notice) : SeedRemote(fixture, remote, notice);
                Check.Require(fixture.Inner.BeginSelection(selected) &&
                    fixture.Inner.AttachIssueWork(selected, issue, [(notice, false, saved)]),
                    "Absent-row setup attaches the exact issue to acquired work.");
                if (residual) fixture.Inner.CompleteWork(selected, null);
                fixture.Inner.EndSelection(selected);
                fixture.Inner.EndWorkPass(PairId);
                if (changed)
                {
                    fixture.Inner.Commit(PairId, StateChange.Empty with { Raise =
                        [(issue.Kind, issue.Cause, issue.Path,
                            issue.Subject with { LocalHash = new LocalHashProof(1, 1, "changed-proof") })] });
                    var replacement = fixture.Inner.IssueById(PairId, issue.Id)!;
                    Check.Require(!replacement.Matches(issue) && !fixture.Inner.SetIssueIgnored(PairId, issue, true) &&
                        fixture.Inner.IssueById(PairId, issue.Id)?.Matches(replacement) == true,
                        "A changed issue snapshot rejects a stale mutation without changing the current issue.");
                    issue = replacement;
                }
                fixture.Inner.Commit(PairId, StateChange.Empty with
                { ForgetBothSubtrees = [(PathNames.Key("Parent"), PathNames.DropboxKey("Parent"))] });
                ClearObligations(fixture);
                Check.Require(fixture.Inner.SyncedByKey(PairId, side, PathNames.KeyFor(side, path)) is null &&
                    PendingAt(fixture, side, path).Count == 1,
                    "The forgotten subtree leaves one unbound issue-linked selection.");
                var localReads = new CountingSide(fixture.Local);
                var remoteReads = new CountingSide(fixture.Dropbox);
                var store = new StoreCounters(fixture.Store);
                var context = fixture.Context(selected) with { Store = store, Local = localReads, Dropbox = remoteReads };
                var syncer = new Syncer(context, new SyncPorts(new RecoveryBoundary(),
                    new Boundary(_ => throw new NotSupportedException()), RefusingExecutionBoundary.Instance, 0));
                // ChangeTable.cs:710-726 separates the residual; Syncer.cs:158-163 replays it if still protected after retirement.
                var initialObservations = residual ? 1 : firstRun ? 3 : 2;
                await syncer.SyncWorkAsync(default);
                if (firstRun)
                {
                    Check.Require(fixture.Inner.IssueById(PairId, issue.Id)?.Matches(issue) == true &&
                        PendingAt(fixture, side, path).Count == 1 && localReads.Reads == initialObservations &&
                        remoteReads.Reads == initialObservations && store.CommitCalls == initialObservations,
                        "Absent-row closure preserves the current first-run responsibility and its work.");
                    fixture.Inner.Commit(PairId, StateChange.Empty with { FirstRunAcknowledged = issue.Subject.Items });
                    await syncer.SyncWorkAsync(default);
                }
                var observations = initialObservations + (firstRun ? 1 : 0);
                Check.Require(fixture.Inner.IssueById(PairId, issue.Id) is null &&
                    PendingAt(fixture, side, path).Count == 0,
                    "Absent-row settlement closes the current exact-path issue and retires work once responsibility permits.");
                Check.Require(localReads.Reads == observations && remoteReads.Reads == observations &&
                    store.CommitCalls == observations &&
                    localReads.Listings + remoteReads.Listings + localReads.Work + remoteReads.Work +
                    localReads.Hashes + remoteReads.Hashes == 0 && store.RowsRead == 0,
                    "Absent-row opportunities observe and settle each selection exactly without listing, content work or a pair scan.");
                Check.Require(firstCounts is null || firstCounts.Count == store.MemberCalls.Count &&
                    firstCounts.All(value => store.MemberCalls.GetValueOrDefault(value.Key) == value.Value),
                    "Absent-row storage call counts depend on the involved subject, not background bindings.");
                firstCounts = store.MemberCalls.ToDictionary(value => value.Key, value => value.Value);
                fixture.RestartStore();
                store.Reset();
                foreach (var later in new[] { 1, 2 }) await syncer.SyncWorkAsync(default);
                Check.Require(localReads.Reads == observations && remoteReads.Reads == observations && store.CommitCalls == 0 &&
                    fixture.Inner.IssueById(PairId, unrelated.Id)?.Matches(unrelated) == true &&
                    fixture.Inner.SelectWork(PairId, Generation, context.Scope) is null,
                    "Restart and later opportunities make no further provider reads or commits and preserve unrelated issues.");
                fixture.Inner.EndWorkPass(PairId);
            }
        }
    }

    private static async Task ReleasedHistoryAsync()
    {
        foreach (var acquiredBefore in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: true);
            SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
            ClearObligations(fixture);
            SeedFolders(fixture, "Parent");
            var partner = LocalEntry("Parent/Partner", "partner-id");
            var sibling = LocalEntry("Parent/Sibling", "sibling-id");
            var local = LocalEntry("Original", "original-local");
            var remote = RemoteEntry("Original", "original-remote");
            fixture.Local.Seed(partner, [1]);
            fixture.Local.Seed(sibling, [1]);
            fixture.Local.Seed(local, [1]);
            fixture.Dropbox.Seed(remote, [1]);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(partner, remote)] });
            async Task AcquireHistoryAsync()
            {
                var token = fixture.Inner.BeginScan(PairId, Generation, true);
                var sink = fixture.Inner.OpenScan(PairId, Side.Dropbox, ScanTestSettings.BatchRows);
                await sink.AddAsync(new Listed("Parent", null), default);
                await sink.CompleteAsync();
                fixture.Inner.SaveDeletedHistory(PairId, token, Cursor);
                await fixture.Inner.EndScanAsync(PairId, token, fixture.Settings.StoragePageSize);
            }
            if (acquiredBefore) await AcquireHistoryAsync();
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
            Check.Require(fixture.Inner.DeletedNameSpent(PairId, PathNames.Key(partner.Path)) &&
                fixture.Inner.DeletedHistoryOwed(PairId) != acquiredBefore,
                "returning-original retirement spends the released Local endpoint in Complete and Owed history");
            if (!acquiredBefore) await AcquireHistoryAsync();
            fixture.RestartStore();
            var selection = fixture.Inner.SelectWork(PairId, Generation, new PathScope(fixture.Pair, [fixture.Pair]))!;
            Check.Require(selection is not null &&
                fixture.Inner.ReadWork(selection).Single() is { Before: null, Notice.Side: Side.Local } work &&
                work.Notice.Path == partner.Path && work.Notice.Id == partner.Id &&
                fixture.Inner.DeletedNameSpent(PairId, PathNames.Key(partner.Path)) &&
                fixture.Inner.SavedDeletedNameExact(PairId, PathNames.Key("Parent")),
                "restart before selection preserves exact release custody and its negative override over ancestor history");
            Check.Require(await RunSelectionAsync(fixture, selection!) && await SyncedAsync(fixture, partner.Path) &&
                await fixture.Local.ReadOneAsync(partner.Path, default) is not null,
                "released Local content uploads through ordinary selection without asking for deletion proof");
            var evidenceReads = 0;
            var context = fixture.Context(selection!) with
            {
                ReadDeletionEvidenceAsync = (path, _, _) =>
                {
                    Check.Require(path == sibling.Path, "spent partner never reaches deletion evidence");
                    evidenceReads++;
                    return Task.FromResult<DeletionEvidence>(new DeletionEvidence.DeletedFile(path, "domain",
                        "deleted-sibling", "revision", Stamp.AddDays(1), null, remote.ContentHash, null));
                }
            };
            ((FakeTimeProvider)context.Time).Advance(Stamp.AddDays(2) - context.Time.GetUtcNow());
            var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
                new Boundary(_ => new Lease()), RefusingExecutionBoundary.Instance, 0));
            var rows = jobs.Items.ScanContexts(true, null, null,
                key => fixture.Inner.SavedDeletedNameExact(PairId, key));
            var row = new Row(rows.For, PathNames.Key(sibling.Path), sibling, null, null, [], null);
            Check.Require(row.Context.DeletionEligible, "an unrelated sibling retains ancestor deletion eligibility");
            var outcome = await jobs.Items.RunAdmittedAsync(row, default, execution: new Lease());
            Check.Require(outcome.Kind == ActionKind.DeleteLocal && outcome.Succeeded() &&
                evidenceReads == 1 && await fixture.Local.ReadOneAsync(sibling.Path, default) is null,
                "an unrelated ancestor-matching sibling still takes T09 evidence and checked removal: " +
                $"outcome={outcome}; eligible={row.Context.DeletionEligible}; evidenceReads={evidenceReads}");
        }
    }

    private static async Task StaleFreshnessAsync()
    {
        var failures = new List<Exception>();
        foreach (var test in new Func<Task>[]
        {
            ReplacedParentAsync, LivePreparationFreshnessAsync, FirstRunFolderReadFreshnessAsync, DeletionEvidenceRefusalAsync,
            BatchReacquisitionFreshnessAsync, BatchChangingChildAsync, HashFreshnessAsync, BatchScopeFreshnessAsync, RecordFreshnessAsync,
            DeferredFolderFreshnessAsync,
            ExecutionFreshnessAsync, SelectionObservationFreshnessAsync, FirstRunFolderFreshnessAsync,
            SelectionFreshnessAsync
        })
        {
            try { await test(); }
            catch (Exception error) { failures.Add(new InvalidOperationException(test.Method.Name, error)); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static async Task ReplacedParentAsync()
    {
        Console.WriteLine("B66 native settlement: started");
        var failures = new List<Exception>();
        int? previousReads = null;
        // Reuse the historical budget sizes; affected children stay fixed as unrelated saved state grows.
        foreach (var background in new[] { 5_110, 20_440 })
        {
            var root = Directory.CreateTempSubdirectory(nameof(ReplacedParentAsync)).FullName;
            try
            {
                using var fixture = new Fixture(realStore: true, localRoot: root);
                ClearObligations(fixture);
                var native = new LocalSide(fixture.Pair, new PathScope(fixture.Pair, [fixture.Pair]), fixture.Settings,
                    new PendingNotes(fixture.Store, PairId, fixture.Settings.StoragePageSize, mutation: false), root,
                    (_, _, _, _) => throw new InvalidOperationException("B66 inventory must be readable."));
                var parent = Directory.CreateDirectory(Path.Combine(root, "Parent")).FullName;
                var saved = new List<Entry>();
                WorkSelection? selection = null;
                long page = 0;
                foreach (var name in new[] { "First", "Second" })
                {
                    File.WriteAllBytes(Path.Combine(parent, name), [1]);
                    var entry = (await native.ReadOneAsync("Parent/" + name, default))!;
                    saved.Add(entry);
                    var remote = RemoteEntry(entry.Path, "remote-" + name);
                    fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(entry, remote)] });
                    selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, entry.Path, null,
                        entry.Id, entry, false, false), ++page);
                    SeedRemote(fixture, remote);
                    fixture.Inner.Commit(PairId, StateChange.Empty with
                    {
                        Raise = [(IssueKind.Failure, TextKey.ReadLocalFailed, entry.Path,
                            new IssueSubject([new IssueItem(Side.Local, entry.Path, entry.Id)], null))]
                    });
                }
                fixture.Inner.Commit(PairId, StateChange.Empty with
                {
                    Record = Enumerable.Range(0, background).Select(index => SyncedEntries.From(
                        LocalEntry("Background/" + index, "local-" + index),
                        RemoteEntry("Background/" + index, "remote-" + index))).ToArray()
                });
                var sibling = LocalEntry("Unrelated", "unrelated-local");
                var siblingRemote = RemoteEntry(sibling.Path, "unrelated-remote");
                // Both absent, but saved: independent progress must not amplify the child failures.
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(sibling, siblingRemote)] });
                Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Deleted, sibling.Path, null,
                    sibling.Id, null, false, false), ++page);
                Directory.Delete(parent, true);
                File.WriteAllText(parent, "Replacement");
                foreach (var entry in saved)
                {
                    try
                    {
                        var changed = await ThrowsAsync<ChangedUnderneathException>(() => native.MoveAsync(entry, "Moved", default));
                        Check.Require(changed.StackTrace?.Contains("Recheck", StringComparison.Ordinal) == true &&
                            !File.Exists(Path.Combine(root, "Moved")), "B66 real recheck refuses without mutation.");
                        Console.WriteLine("B66 native recheck: passed; path=" + entry.Path);
                    }
                    catch (Exception error) { failures.Add(error); }
                    try
                    {
                        var allocation = new StageAllocation("operation", 1, "allocation", Path.Combine(root, entry.Path));
                        Check.Require(await native.InspectRecoveryAllocationAsync(allocation, default) ==
                            ILocalRecovery.AllocationOccupancy.Absent, "B66 allocation under a proven file parent is absent.");
                        Console.WriteLine("B66 native recovery occupancy: passed; path=" + entry.Path);
                    }
                    catch (Exception error) { failures.Add(error); }
                }
                var localCalls = new CountingSide(native);
                var remoteCalls = new CountingSide(fixture.Dropbox);
                fixture.LocalView = localCalls;
                fixture.DropboxView = remoteCalls;
                var events = new List<RunEvent>();
                await LiveSyncer(fixture, selection!, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
                var settledReads = localCalls.Reads + remoteCalls.Reads;
                Check.Require(previousReads is null || previousReads == settledReads,
                    "B66 fixed child work does not grow with unrelated saved state.");
                previousReads = settledReads;
                var settled = fixture.Inner.Issues(PairId).Count == 0 && saved.All(entry =>
                    fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(entry.Path)) is null &&
                    PendingAt(fixture, Side.Local, entry.Path).Count == 0 &&
                    PendingAt(fixture, Side.Dropbox, entry.Path).Count == 0);
                fixture.RestartStore();
                await LiveSyncer(fixture, selection!, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
                await LiveSyncer(fixture, selection!, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
                Check.Require(settled && fixture.Inner.Issues(PairId).Count == 0 &&
                    localCalls.Reads + remoteCalls.Reads == settledReads &&
                    localCalls.Listings + remoteCalls.Listings + localCalls.Work + remoteCalls.Work == 0 &&
                    events.All(item => item.Error is not ItemFailedException { Cause: TextKey.ReadLocalFailed }),
                    "B66 settles child issues and both sources once, with no reads after restart/repeated opportunities.");
                Check.Require(File.ReadAllText(parent) == "Replacement" &&
                    fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(sibling.Path)) is null &&
                    fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key("Background/0")) is not null,
                    "B66 preserves the parent and unrelated saved state while independent work progresses.");
                Console.WriteLine($"B66 native settlement: passed; background={background}; children=2; reads={settledReads}; subsequentReads=0; effects=0; issues=0");
            }
            catch (Exception error) { failures.Add(error); }
            finally { Directory.Delete(root, true); }
        }
        if (failures.Count != 0) throw new AggregateException("B66 native settlement failures", failures);
    }

    private static async Task SelectionFreshnessAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var mode in new[] { "race", "unchanged", "changing", "footprint" })
        {
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            var entry = side == Side.Local ? LocalEntry(Item, LocalId) : RemoteEntry(Item, DropboxId);
            var source = side == Side.Local ? fixture.Local : fixture.Dropbox;
            source.Seed(entry, [1]);
            var selection = side == Side.Local ? Selected(fixture, Item, entry) : SeedRemote(fixture, entry);
            var reads = 0;
            var hook = new HookedSide(source, beforeRead: path =>
            {
                if (path != Item) return;
                reads++;
                if (reads == 2 || reads == 4 && mode is "unchanged" or "changing")
                    source.Seed(entry with { Modified = Stamp.AddSeconds(reads) }, [1]);
                if (reads == 3 && mode == "unchanged") source.Seed(entry, [1]);
                if (reads == 3 && mode == "footprint") source.Seed(FolderEntry(Item, entry.Id), null);
            });
            var calls = new CountingSide(hook);
            var targetCalls = new CountingSide(side == Side.Local ? fixture.Dropbox : fixture.Local);
            fixture.LocalView = side == Side.Local ? calls : targetCalls;
            fixture.DropboxView = side == Side.Dropbox ? calls : targetCalls;
            bool? ready = false;
            LiveAdmissionStaleException? refused = null;
            try
            {
                ready = await Runner(fixture, selection, new Boundary(request => new Lease
                {
                    AllowedScopes = mode == "footprint" ? [] : null
                }), (_, _, _) => Task.FromResult(Coverage(selection))).RunAsync(selection, default);
            }
            catch (LiveAdmissionStaleException error) { refused = error; }
            Console.WriteLine($"stale {side} {mode}: reads={reads}, targetReads={targetCalls.Reads}, hashes={calls.Hashes}, effects={calls.Work + targetCalls.Work}");
            if (mode == "race")
            {
                Check.Require(ready is true && refused is null && fixture.Inner.Issues(PairId).Count == 0 &&
                    await SyncedAsync(fixture, Item), "A stale race reobserves and converges without a notice: " + side);
                continue;
            }
            if (mode is "changing" or "footprint")
            {
                Check.Require(refused?.Reason == (mode == "changing" ? LiveAdmissionStaleReason.RelevantEvidenceChanged :
                    LiveAdmissionStaleReason.FootprintChanged) && fixture.Inner.Issues(PairId).Count == 0 &&
                    PendingAt(fixture, side, Item).Count == 1 && calls.Work + targetCalls.Work == 0,
                    "Changing evidence stays pending without effects or a notice: " + side + mode);
                Check.Require(reads - 2 <= 2, "Observation plus reobservation allow at most two freshness reads.");
                continue;
            }
            var issue = fixture.Inner.Issues(PairId).Single();
            Check.Require(ready is true && refused is null && issue.Cause == TextKey.FailChanged &&
                PendingAt(fixture, side, Item).Count == 1 && calls.Work + targetCalls.Work == 0 &&
                fixture.Inner.SyncedByKey(PairId, side, PathNames.KeyFor(side, Item)) is null,
                "Unchanged stale evidence raises one notice and retains responsibility without effects.");
            Check.Require(reads == 4 && targetCalls.Reads == 4,
                "Stale diagnosis reuses the branch observation, then reobserves once with two freshness checks.");
            var sibling = LocalEntry("sibling", "sibling-id");
            fixture.Local.Seed(sibling, [1]);
            var other = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, sibling.Path, null,
                sibling.Id, sibling, false, false), side == Side.Local ? 2 : 1);
            await RunSelectionAsync(fixture, other);
            Check.Require(await SyncedAsync(fixture, sibling.Path), "A healthy sibling completes despite the stale notice.");
            source.Seed(entry, [1]);
            reads = 0;
            var residual = PendingAt(fixture, side, Item).Single();
            await RunSelectionAsync(fixture, residual);
            Check.Require(fixture.Inner.Issues(PairId).Single().Id == issue.Id &&
                PendingAt(fixture, side, Item).Count == 1, "A second opportunity reuses the notice and its one residual.");
            fixture.LocalView = fixture.Local;
            fixture.DropboxView = fixture.Dropbox;
            await RunSelectionAsync(fixture, PendingAt(fixture, side, Item).Single());
            Check.Require(await SyncedAsync(fixture, Item) && fixture.Inner.Issues(PairId).Count == 0 &&
                PendingAt(fixture, side, Item).Count == 0, "Settled evidence closes the notice and retires the residual.");
        }
    }

    private static async Task BatchReacquisitionFreshnessAsync()
    {
        foreach (var restart in new[] { false, true })
        foreach (var settlement in new[] { "none", "peer", "retry" })
            await BatchReacquisitionFreshnessAsync(restart, settlement);
    }

    private static async Task BatchReacquisitionFreshnessAsync(bool restart, string settlement)
    {
        var attempt = Guid.NewGuid();
        using var fixture = new Fixture(realStore: true, registeredAttempt: attempt);
        ClearObligations(fixture);
        var folder = FolderEntry(Folder, LocalId);
        fixture.Local.Seed(folder, null);
        fixture.Dropbox.Seed(FolderEntry(Folder, DropboxId), null);
        var children = new[] { Folder + "/First", Folder + "/Second" };
        foreach (var path in children)
        {
            fixture.Local.Seed(LocalEntry(path, "local-" + path), [1]);
            fixture.Dropbox.Seed(RemoteEntry(path, "remote-" + path), [1]);
        }
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder,
            null, folder.Id, settlement == "none" ? folder : null, false, false));
        foreach (var (selected, workId) in new[]
        {
            (selection with { PairGeneration = selection.PairGeneration + 1 }, selection.AnchorWorkId),
            (selection with { SourceEpoch = selection.SourceEpoch + 1 }, selection.AnchorWorkId),
            (selection, selection.ThroughWorkId + 1)
        })
            await ThrowsAsync<LiveAdmissionStaleException>(() =>
            {
                fixture.Inner.RequireDiscovery(selected, workId);
                return Task.CompletedTask;
            });
        Check.Require(fixture.Inner.ReadWork(selection).All(work => !work.DiscoveryRequired),
            "Discovery cannot be armed by a stale generation, stale source or work outside exact selection membership.");
        var local = new ProxySide(fixture.Local);
        var remote = new ProxySide(fixture.Dropbox);
        fixture.DropboxView = remote;
        // A revoked batch must retain discovery; an ordinary repeated child race now finishes with its own Failure.
        fixture.LocalView = new HookedSide(local, hash: (_, _) =>
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged));
        var batches = 0;
        var stale = await ThrowsAsync<LiveAdmissionStaleException>(() =>
            Counted(fixture, selection, () => batches++).RunAsync(selection, default));
        Check.Require(stale.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged && batches == 1 &&
            fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Folder)) is not null &&
            fixture.Inner.ReadWork(selection).Any(work => work.DiscoveryRequired) && fixture.Inner.Issues(PairId).Count == 0,
            "A stale Changed-folder batch retains its anchor after binding the folder, without a notice.");
        var unfinished = children.Where(path => fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(path)) is null).ToArray();
        Check.Require(unfinished.Length != 0, "The stale batch leaves children unfinished for reacquisition.");
        fixture.LocalView = local;
        fixture.Inner.EndWorkPass(PairId);
        if (restart) fixture.RestartStore();
        if (settlement == "peer")
        {
            var other = SeedRemote(fixture, FolderEntry(Folder, DropboxId));
            fixture.Inner.BeginSelection(other);
            Check.Require(fixture.Inner.CompleteWork(other,
                new SelectionPeer(PathNames.KeyFor(Side.Local, Folder), fixture.Inner.ReadWorkWatermark())),
                "The other side's exact-path selection completes independently.");
            Check.Require(PendingAt(fixture, Side.Local, Folder).Any(work => work.AnchorWorkId == selection.AnchorWorkId) &&
                fixture.Inner.ReadWork(selection).Single() is { DiscoveryRequired: true, Notice.Observed: null },
                "Peer completion preserves discovery armed after reading a folder notice with no observed entry.");
        }
        else if (settlement == "retry")
        {
            var subject = new IssueSubject([new IssueItem(Side.Local, Folder, folder.Id)], null);
            var retry = fixture.Inner.RecordInUseFailure(PairId, Generation, selection.SourceEpoch, Folder,
                subject, TextKey.FailLocalInUse, selection, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
            Check.Require(retry.Token.WorkId == selection.AnchorWorkId && fixture.Inner.TryClaimItemRetry(retry, attempt),
                "The explicit root-only Retry claims the marked acquired anchor.");
            fixture.Inner.CompleteItemAttempt(retry, attempt, StateChange.Empty with
            {
                Close = [(Folder, IssueClosure.AfterEffect)]
            }, null, null);
            Check.Require(fixture.Inner.ReadItemRetryState(PairId, [retry.Token.IssueId])[retry.Token.IssueId] is null &&
                PendingAt(fixture, Side.Local, Folder).Any(work => work.AnchorWorkId == selection.AnchorWorkId) &&
                fixture.Inner.ReadWork(selection).Single().DiscoveryRequired &&
                unfinished.All(path => fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(path)) is null),
                "A successful root-only Retry closes its episode but preserves the anchor and all unfinished discovery.");
        }
        var localListings = local.Listings;
        var remoteListings = remote.Listings;
        var next = fixture.Inner.SelectWork(PairId, Generation, new PathScope(fixture.Pair, [fixture.Pair]));
        Check.Require(next?.AnchorWorkId == selection.AnchorWorkId, "The next acquisition selects the same durable folder anchor.");
        var completed = await Counted(fixture, next!, () => batches++).RunAsync(next!, default);
        var missing = unfinished.Where(path => fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(path)) is null).ToArray();
        fixture.Inner.EndWorkPass(PairId);
        var pending = fixture.Inner.SelectWork(PairId, Generation, new PathScope(fixture.Pair, [fixture.Pair]));
        Console.WriteLine($"batch reacquisition: firstUnfinished={unfinished.Length}, missing={missing.Length}, batches={batches}, completed={completed}, retained={pending is not null}");
        Check.Require(missing.Length == 0 && completed is true && pending is null && batches == 2 &&
            local.Listings - localListings == 1 && remote.Listings - remoteListings == 1,
            "Reacquiring a bound Changed-folder anchor completes every unfinished child with one listing per side, including after restart.");
    }

    private static async Task BatchChangingChildAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        var folder = FolderEntry(Folder, LocalId);
        fixture.Local.Seed(folder, null);
        fixture.Dropbox.Seed(FolderEntry(Folder, DropboxId), null);
        var changing = Folder + "/Changing";
        var sibling = Folder + "/Sibling";
        foreach (var path in new[] { changing, sibling })
        {
            fixture.Local.Seed(LocalEntry(path, "local-" + path), [1]);
            fixture.Dropbox.Seed(RemoteEntry(path, "remote-" + path), [1]);
        }
        var selection = Selected(fixture, Folder, folder);
        var hashes = 0;
        fixture.LocalView = new HookedSide(fixture.Local, hash: (entry, ct) =>
        {
            if (entry.Path != changing) return fixture.Local.ContentHashAsync(entry, ct);
            fixture.Local.Seed(entry with { Modified = Stamp.AddSeconds(++hashes) }, [1]);
            throw new ChangedUnderneathException(entry.Path);
        });
        var batches = 0;
        var completed = await Counted(fixture, selection, () => batches++).RunAsync(selection, default);
        var issue = fixture.Inner.Issues(PairId).Single();
        Check.Require(completed is true && batches == 1 && hashes == 2 && issue.Path == changing &&
            issue.Cause == TextKey.FailChanged && await SyncedAsync(fixture, sibling) &&
            fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(changing)) is null &&
            fixture.Events.Count(item => item.Kind == RunEventKind.Wait && item.What == nameof(TextKey.RetryChanged) &&
                item.Path == changing) == 1,
            "A continuously changing child gets one quiet retry then one Failure while its sibling and batch finish.");
        fixture.Inner.EndWorkPass(PairId);
        Check.Require(PendingAt(fixture, Side.Local, Folder).All(work => work.AnchorWorkId != selection.AnchorWorkId),
            "The finished batch retires its discovery anchor despite the child's visible Failure.");
        fixture.LocalView = fixture.Local;
        var current = (await fixture.Local.ReadOneAsync(changing, default))!;
        await RunSelectionAsync(fixture, Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, changing,
            null, current.Id, current, false, false), 2));
        Check.Require(await SyncedAsync(fixture, changing) && fixture.Inner.Issues(PairId).Count == 0 && batches == 1,
            "The child's own stable change closes its Failure without rediscovering the folder.");
    }

    private static async Task HashFreshnessAsync()
    {
        foreach (var route in new[] { "batch", "prepare", "admitted", "live" })
        foreach (var repeat in new[] { false, true })
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
            try
            {
                using var fixture = new Fixture(realStore: true, localRoot: root);
                ClearObligations(fixture);
                Directory.CreateDirectory(Path.Combine(root, Folder));
                var path = Folder + "/Child";
                var full = Path.Combine(root, Folder, "Child");
                File.WriteAllBytes(full, [1]);
                File.SetLastWriteTimeUtc(full, Stamp.UtcDateTime);
                var native = new LocalSide(fixture.Pair, new PathScope(fixture.Pair, [fixture.Pair]), fixture.Settings,
                    new PendingNotes(fixture.Store, PairId, fixture.Settings.StoragePageSize, mutation: false), root,
                    (_, _, _, _) => throw new InvalidOperationException("Synthetic hash inventory must be readable."));
                var local = (await native.ReadOneAsync(path, default))!;
                var folder = (await native.ReadOneAsync(Folder, default))!;
                var remote = RemoteEntry(path, DropboxId) with { Modified = local.Modified };
                fixture.Dropbox.Seed(FolderEntry(Folder, "remote-folder"), null);
                fixture.Dropbox.Seed(remote, [1]);
                var selection = Selected(fixture, route == "batch" ? Folder : path, route == "batch" ? folder : local);
                var hashes = 0;
                var races = new List<ChangedUnderneathException>();
                fixture.LocalView = new HookedSide(native, hash: async (entry, ct) =>
                {
                    hashes++;
                    Check.Require(hashes <= 2 && fixture.Inner.Issues(PairId).Count == 0,
                        "A hash race has one quiet diagnostic retry and no transient notice.");
                    if (repeat || hashes == 1)
                    {
                        // Keep the old inode alive so replacement cannot reuse its identity.
                        File.Move(full, Path.Combine(root, "retired-" + hashes));
                        File.WriteAllBytes(full, [1]);
                        File.SetLastWriteTimeUtc(full, Stamp.UtcDateTime);
                    }
                    try { return await native.ContentHashAsync(entry, ct); }
                    catch (ChangedUnderneathException error)
                    {
                        races.Add(error);
                        Console.WriteLine($"hash race {route} repeat={repeat}: {error.StackTrace}");
                        throw;
                    }
                });
                var context = fixture.Context(selection);
                var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
                    RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
                var row = new Row(PathNames.Key(path), local, remote, null, [], null, RowContext.Neutral);
                LiveAdmissionStaleException? stale = null;
                SyncOutcome? outcome = null;
                try
                {
                    if (route == "batch") Check.Require(await Counted(fixture, selection, () => { }).RunAsync(selection, default) is not null, "Selection must be admitted.");
                    else if (route == "prepare") outcome = await jobs.Items.RunRowAttemptAsync(row, default);
                    else outcome = await jobs.Items.RunAdmittedAsync(row, default,
                        selection: route == "live" ? selection : null);
                }
                catch (LiveAdmissionStaleException error) { stale = error; }
                Check.Require(hashes == 2 && races.Count == (repeat ? 2 : 1) &&
                    races.All(error => error.StackTrace?.Contains("Recheck", StringComparison.Ordinal) == true),
                    "Replacing a listed child reaches the actual LocalSide.Recheck throw during hashing.");
                var issues = fixture.Inner.Issues(PairId);
                var waits = fixture.Events.Count(item => item.Kind == RunEventKind.Wait && item.What == nameof(TextKey.RetryChanged));
                Console.WriteLine($"hash retry events {route} repeat={repeat}: waits={waits}, paths={string.Join(",", fixture.Events.Where(item => item.Kind == RunEventKind.Wait && item.What == nameof(TextKey.RetryChanged)).Select(item => item.Path))}");
                Check.Require(waits == 1, "Prepare and admitted hash races announce exactly one diagnostic retry.");
                if (repeat && route == "live")
                    Check.Require(stale?.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged && issues.Count == 0 &&
                        fixture.Inner.ReadWork(selection).Any(), "Repeated changing live hashes retain work without a Failure notice.");
                else if (repeat)
                    Check.Require(stale is null && (route == "batch" || outcome?.Succeeded() == false) &&
                        issues.Single().Cause == TextKey.FailChanged,
                        "A repeated Repair or batch-child hash race terminates with one FailChanged notice.");
                else
                    Check.Require(stale is null && issues.Count == 0 &&
                        fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(path)) is not null,
                        "A single native hash race converges without a notice.");
                Console.WriteLine($"hash freshness {route} repeat={repeat}: hashes={hashes}, races={races.Count}, waits={waits}, notices={issues.Count}, stale={stale?.Reason}");
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task BatchScopeFreshnessAsync()
    {
        foreach (var ancestor in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            var path = ancestor ? Folder + "/Child" : Item;
            if (ancestor) fixture.Local.Seed(FolderEntry(Folder, FolderId), null);
            var entry = LocalEntry(path, LocalId);
            fixture.Local.Seed(entry, [1]);
            var selection = Selected(fixture, path, entry);
            var local = new CountingSide(fixture.Local);
            var remote = new CountingSide(fixture.Dropbox);
            fixture.LocalView = local;
            fixture.DropboxView = remote;
            var jobs = SyncJobs.Create(fixture.Context(selection) with { BatchScope = Folder }, new SyncPorts(
                new RecoveryBoundary(), RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
            if (ancestor)
            {
                // Listing coverage reaches ItemSync's refusal rather than the fence's earlier unlisted-work throw.
                jobs.Ancestors.CoverListedWork(selection.ThroughWorkId);
                var later = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Deleted, Folder,
                    null, FolderId, null, true, false), page: 2);
                Check.Require(later.AnchorWorkId > selection.ThroughWorkId,
                    "The ancestor refusal comes from work acquired after the covered selection.");
            }
            var row = Row.ForOneSide(Side.Local, PathNames.Key(path), entry, null, [], null, RowContext.Neutral);
            var action = new Massdex.SyncShark.Engine.Planner.SyncAction(ActionKind.Upload, row, Side: Side.Dropbox, Target: path);
            LiveAdmissionStaleException? stale = null;
            SyncOutcome? outcome = null;
            try { outcome = await jobs.Items.RunAdmittedAsync(row, default, preparedAction: action); }
            catch (LiveAdmissionStaleException error) { stale = error; }
            var issues = fixture.Inner.Issues(PairId);
            Check.Require(ancestor
                ? stale?.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged && issues.Count == 0
                : stale is null && outcome?.Succeeded() == false && issues.Single().Cause == TextKey.FailChanged &&
                    issues.Single().Path == path,
                "A later ancestor makes the batch stale; only an out-of-batch item raises its own Failure.");
            var effects = local.Work + remote.Work;
            var pending = fixture.Inner.ReadWork(selection).Any();
            var binding = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(path));
            var original = await fixture.Local.ReadOneAsync(path, default);
            var published = await fixture.Dropbox.ReadOneAsync(path, default);
            Check.Require(effects == 0 && pending && binding is null && original == entry && published is null,
                "Both refusals preserve pending work and the original item without effects or a saved binding: " +
                $"ancestor={ancestor}, effects={effects}, pending={pending}, binding={binding}, original={original}, expected={entry}, published={published}.");
        }
    }

    private static async Task RecordFreshnessAsync()
    {
        using (var fixture = new Fixture(realStore: true))
        {
            ClearObligations(fixture);
            var local = LocalEntry(Item, LocalId);
            var remote = RemoteEntry(Item, DropboxId);
            fixture.Local.Seed(local, [1]);
            fixture.Dropbox.Seed(remote, [1]);
            var selection = Selected(fixture, Item, local);
            var context = fixture.Context(selection);
            var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
                RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
            var row = new Row(PathNames.Key(Item), local, remote, null, [], remote.ContentHash, RowContext.Neutral);
            var saved = SyncedEntries.From(local, remote);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
            var (subject, expected) = jobs.Items.CaptureAttempt(row);
            var outcome = await context.ItemAttempts.ExecuteAsync(selection, expected, subject, async (attempt, token) =>
            {
                var prepared = await jobs.Items.PrepareRowAsync(row, token, attemptId: attempt, expectedAttempt: expected);
                Check.Require(prepared.Action.Kind == ActionKind.Record && prepared.Completed is null,
                    "The captured unbound Record refuses the binding committed after its observation.");
                return await jobs.Items.DispatchPreparedAsync(prepared.Action, token,
                    attemptId: attempt, expectedAttempt: expected);
            }, default);
            Check.Require(outcome.Succeeded() &&
                fixture.Events.Count(item => item.Kind == RunEventKind.Wait && item.What == nameof(TextKey.RetryChanged)) == 1 &&
                fixture.Inner.Issues(PairId).Count == 0 &&
                fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Item)) == saved &&
                fixture.Inner.SyncedByKey(PairId, Side.Dropbox, PathNames.Key(Item)) == saved,
                "A refused Record emits its one shared retry event and converges on the current matching binding.");
        }
    }

    private static async Task DeferredFolderFreshnessAsync()
    {
        foreach (var repeat in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            var folder = FolderEntry(Item, LocalId);
            fixture.Local.Seed(folder, null);
            var selection = Selected(fixture, Item, folder);
            var attempts = 0;
            fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeEnsure: path =>
            {
                attempts++;
                Check.Require(attempts <= 2 && fixture.Inner.Issues(PairId).Count == 0, "A deferred folder gets one quiet retry.");
                if (repeat || attempts == 1) throw new ChangedUnderneathException(path);
            });
            var jobs = SyncJobs.Create(fixture.Context(selection), new SyncPorts(new RecoveryBoundary(),
                RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
            var row = Row.ForOneSide(Side.Local, PathNames.Key(Item), folder, null, [], null, RowContext.Neutral);
            var action = new Massdex.SyncShark.Engine.Planner.SyncAction(ActionKind.DeferFolder, row);
            var outcome = await jobs.Items.RunAdmittedAsync(row, default, preparedAction: action);
            Check.Require(attempts == 2 && fixture.Events.Count(item => item.Kind == RunEventKind.Wait &&
                item.What == nameof(TextKey.RetryChanged)) == 1 && outcome.Succeeded() == !repeat,
                "Initial deferred-folder observation is not a retry; its first execution race retries quietly.");
        }
    }

    private static async Task LivePreparationFreshnessAsync()
    {
        foreach (var mode in new[] { "once", "changing", "shared-budget" })
        {
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            var local = LocalEntry(Item, LocalId) with { Modified = Stamp.AddHours(1) };
            fixture.Local.Seed(local, [2]);
            fixture.Dropbox.Seed(RemoteEntry(Item, DropboxId), [1]);
            var selection = Selected(fixture, Item, local);
            var hashes = 0;
            var stages = 0;
            fixture.LocalView = new HookedSide(fixture.Local, hash: async (entry, ct) =>
            {
                hashes++;
                Check.Require(hashes <= 2 && fixture.Inner.Issues(PairId).Count == 0,
                    "Live preparation has one quiet retry before admission.");
                if (hashes == 1 || mode == "changing") throw new ChangedUnderneathException(entry.Path);
                return await fixture.Local.ContentHashAsync(entry, ct);
            });
            fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeStage: path =>
            {
                stages++;
                if (mode == "shared-budget") throw new ChangedUnderneathException(path);
            });
            Task<bool?> Run(WorkSelection selected)
            {
                var context = fixture.Context(selected);
                ((FakeTimeProvider)context.Time).Advance(Stamp.AddDays(1) - context.Time.GetUtcNow());
                return Counted(fixture, selected, () => throw new InvalidOperationException("A file never starts a batch."),
                    context: context).RunAsync(selected, default);
            }
            LiveAdmissionStaleException? stale = null;
            try { Check.Require(await Run(selection) is not null, "Selection must be admitted."); }
            catch (LiveAdmissionStaleException error) { stale = error; }
            var waits = fixture.Events.Count(item => item.Kind == RunEventKind.Wait && item.What == nameof(TextKey.RetryChanged));
            var issues = fixture.Inner.Issues(PairId);
            Check.Require(hashes == 2 && waits == 1,
                "Real selection preparation retries once before coverage capture, with exactly one RetryChanged.");
            if (mode == "shared-budget")
                Check.Require(stages == 1 && stale is null && issues.Single().Cause == TextKey.FailChanged,
                    "A preparation race followed by an execution race shares one retry and ends in stale-retry Failure.");
            else if (mode == "changing")
                Check.Require(stages == 0 && stale?.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged &&
                    issues.Count == 0 && PendingAt(fixture, Side.Local, Item).Count == 1,
                    "Repeated changing preparation evidence retains live work without a second retry.");
            else
                Check.Require(stages == 1 && stale is null,
                    "A single live preparation race reaches publication without a second refusal.");
            if (mode != "once")
            {
                fixture.LocalView = fixture.Local;
                fixture.DropboxView = fixture.Dropbox;
                Check.Require(await Run(PendingAt(fixture, Side.Local, Item).Single()) is not null, "Selection must be admitted.");
            }
            var published = await fixture.Dropbox.ReadOneAsync(Item, default);
            var settled = fixture.Inner.Issues(PairId);
            Check.Require(published is not null && await fixture.Dropbox.ContentHashAsync(published, default) ==
                await fixture.Local.ContentHashAsync(local, default) &&
                fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Item)) == SyncedEntries.From(local, published) &&
                settled.Count == 1 && settled[0].Kind == IssueKind.Info && settled[0].Cause == TextKey.InfoNewestWins &&
                PendingAt(fixture, Side.Local, Item).Count == 0,
                "The preparation retry publishes and records matching content, closes its Failure, and keeps only the required newest-wins Info.");
        }
    }

    private static async Task FirstRunFolderReadFreshnessAsync()
    {
        foreach (var phase in new[] { "observation", "preparation", "freshness" })
        foreach (var repeat in new[] { false, true })
        foreach (var batch in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            var path = phase == "preparation" ? Folder + "/" + Item : Item;
            if (phase == "preparation") SeedFolders(fixture, Folder);
            var local = FolderEntry(path, LocalId);
            fixture.Local.Seed(local, null);
            var selection = Selected(fixture, path, local);
            var reads = 0;
            var probes = 0;
            var preparing = false;
            var races = new List<ChangedUnderneathException>();
            void Race()
            {
                var error = new ChangedUnderneathException(path);
                races.Add(error);
                Check.Require(races.Count <= 2, "First-run folder reads cannot loop after a diagnostic retry.");
                throw error;
            }
            fixture.LocalView = new HookedSide(fixture.Local, beforeRead: target =>
            {
                if (target != path) return;
                reads++;
                if (phase == "observation" && (repeat || reads == 1) ||
                    phase == "freshness" && (reads == 2 || repeat && reads == 4)) Race();
            });
            if (phase == "preparation") fixture.Hooks.BeforeCall = method =>
            {
                if (method.Name == nameof(IStateStore.UnreadableTouches)) preparing = true;
                if (!preparing || method.Name != nameof(IStateStore.SyncedByKey)) return;
                preparing = false;
                probes++;
                // The child barrier precedes PrepareAsync's mapped-parent lookup, after observation has finished.
                if (repeat || probes == 1) Race();
            };
            var jobs = SyncJobs.Create(fixture.Context(selection) with { BatchScope = batch ? path : null },
                new SyncPorts(new RecoveryBoundary(), RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
            var row = Row.ForOneSide(Side.Local, PathNames.Key(path), local, null, [], null, RowContext.Neutral);
            ChangedUnderneathException? stale = null;
            SyncOutcome? outcome = null;
            try { outcome = await jobs.FirstRunFolders.FinalizeFirstRunFolderAsync(row, false, default, null); }
            catch (ChangedUnderneathException error) { stale = error; }
            Check.Require(races.Count == (repeat ? 2 : 1) && fixture.Events.Count(item =>
                item.Kind == RunEventKind.Wait && item.What == nameof(TextKey.RetryChanged)) == 1,
                $"First-run folder {phase} announces exactly one diagnostic retry.");
            if (phase == "preparation") Check.Require(races.All(error =>
                error.StackTrace?.Contains("PrepareAsync", StringComparison.Ordinal) == true),
                "The first-run preparation fixture reaches PrepareAsync, not its preceding observation.");
            Check.Require(repeat ? stale is not null : stale is null && outcome?.Succeeded() == true,
                $"First-run folder {phase} converges or exposes its terminal stale failure.");
        }
    }

    private static async Task DeletionEvidenceRefusalAsync()
    {
        foreach (var route in new[] { "repair", "batch", "live" })
        foreach (var initialRace in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            var token = fixture.Inner.BeginScan(PairId, Generation, true);
            var sink = fixture.Inner.OpenScan(PairId, Side.Dropbox, ScanTestSettings.BatchRows);
            await sink.AddAsync(new Listed(Item, null), default);
            await sink.CompleteAsync();
            fixture.Inner.SaveDeletedHistory(PairId, token, Cursor);
            await fixture.Inner.EndScanAsync(PairId, token, fixture.Settings.StoragePageSize);
            var local = LocalEntry(Item, LocalId);
            fixture.Local.Seed(local, [1]);
            var selection = Selected(fixture, Item, local);
            var evidenceReads = 0;
            var live = route == "live";
            var context = fixture.Context(selection) with
            {
                BatchScope = route == "batch" ? Item : null,
                BatchSettlesFirstRun = route == "batch",
                ReadDeletionEvidenceAsync = (path, _, _) =>
                {
                    evidenceReads++;
                    if (initialRace && evidenceReads == 1) throw new ChangedUnderneathException(path);
                    return Task.FromResult<DeletionEvidence>(new DeletionEvidence.LiveElsewhere(path,
                        new IdentityLocation("Dropbox", DestinationPath, RemoteEntry(DestinationPath, DropboxId))));
                }
            };
            var boundary = new Boundary(_ => new Lease());
            boundary.Capture = (selected, _, _) => Task.FromResult(Coverage(selected));
            var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(), boundary,
                RefusingExecutionBoundary.Instance, 0));
            var rows = jobs.Items.ScanContexts(true, null, null, _ => true);
            var row = new Row(rows.For, PathNames.Key(Item), local, null, null, [], null);
            LiveAdmissionStaleException? stale = null;
            SyncOutcome? outcome = null;
            try
            {
                if (live)
                    Check.Require(await new SelectionPass(context, jobs.Items, jobs.Ancestors, boundary, jobs.Actions,
                        jobs.Items.Rows, (path, _) => context.Events(RunEvent.Stage(TextKey.StageRows, inProgress: true, path: path)),
                        (_, _, _, _, _, _) => throw new InvalidOperationException("A file refusal never starts a batch."))
                        .RunAsync(selection, default) is not null, "Selection must be admitted.");
                else outcome = await jobs.Items.RunRowAttemptAsync(row, default);
            }
            catch (LiveAdmissionStaleException error) { stale = error; }
            var waits = fixture.Events.Count(item => item.Kind == RunEventKind.Wait && item.What == nameof(TextKey.RetryChanged));
            Check.Require(evidenceReads == (initialRace ? 2 : 1) && waits == (initialRace ? 1 : 0),
                "LiveElsewhere is never replayed; an ordinary evidence-read race on the same item gets one retry: " +
                $"route={route}, initialRace={initialRace}, evidenceReads={evidenceReads}, expectedReads={(initialRace ? 2 : 1)}, waits={waits}, expectedWaits={(initialRace ? 1 : 0)}.");
            var issues = fixture.Inner.Issues(PairId);
            Check.Require(live ? stale?.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged && issues.Count == 0
                : stale is null && outcome?.Succeeded() == false && issues.Single().Cause == TextKey.FailChanged,
                "LiveElsewhere preserves Repair and batch-child Failure and AutoSync selection retention.");
            Check.Require(await fixture.Local.ReadOneAsync(Item, default) == local,
                "Deletion evidence elsewhere never removes the original local item.");
        }
    }

    private static async Task ExecutionFreshnessAsync()
    {
        foreach (var live in new[] { true, false })
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var mode in new[] { "ordinary", "once", "unchanged", "changing" })
        {
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            var entry = side == Side.Local ? LocalEntry(Item, LocalId) : RemoteEntry(Item, DropboxId);
            var source = side == Side.Local ? fixture.Local : fixture.Dropbox;
            var target = side == Side.Local ? fixture.Dropbox : fixture.Local;
            source.Seed(entry, [1]);
            var selection = side == Side.Local ? Selected(fixture, Item, entry) : SeedRemote(fixture, entry);
            var stages = 0;
            var sourceCalls = new CountingSide(source);
            var targetCalls = new CountingSide(new HookedSide(target, beforeStage: path =>
            {
                stages++;
                Check.Require(fixture.Inner.Issues(PairId).Count == 0 &&
                    fixture.Events.All(item => item.Kind != RunEventKind.Issue),
                    "The first execution race must not publish even a transient notice before retry.");
                if (mode == "ordinary" || mode == "once" && stages > 1) return;
                if (mode != "unchanged") source.Seed(entry with { Modified = Stamp.AddSeconds(stages) }, [1]);
                throw new ChangedUnderneathException(path);
            }));
            fixture.LocalView = side == Side.Local ? sourceCalls : targetCalls;
            fixture.DropboxView = side == Side.Dropbox ? sourceCalls : targetCalls;
            var ready = false;
            LiveAdmissionStaleException? stale = null;
            try
            {
                if (live) ready = await RunSelectionAsync(fixture, selection);
                else
                {
                    var context = fixture.Context(selection);
                    var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
                        RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
                    var row = Row.ForOneSide(side, PathNames.KeyFor(side, Item), entry, null, [], null, RowContext.Neutral);
                    ready = (await jobs.Items.RunRowAttemptAsync(row, default)).Succeeded();
                }
            }
            catch (LiveAdmissionStaleException error) { stale = error; }
            var issues = fixture.Inner.Issues(PairId);
            var waits = fixture.Events.Count(item => item.Kind == RunEventKind.Wait && item.What == nameof(TextKey.RetryChanged));
            Console.WriteLine($"execution freshness live={live} {side} {mode}: stages={stages}, reads={sourceCalls.Reads}/{targetCalls.Reads}, notices={issues.Count}, waits={waits}");
            Check.Require(stages == (mode == "ordinary" ? 1 : 2), "Execution gets its original attempt and only one diagnostic retry.");
            Check.Require(sourceCalls.Reads == (live ? 2 : 1) + (mode == "ordinary" ? 0 : 2) &&
                targetCalls.Reads == sourceCalls.Reads, "Exception-only reobservation adds two point reads per side, no ordinary-row work.");
            Check.Require(waits == (mode == "ordinary" ? 0 : 1), "Each deliberate retry emits one output/log wait, not a notice.");
            if (mode is "ordinary" or "once")
                Check.Require(ready && stale is null && issues.Count == 0 && await SyncedAsync(fixture, Item),
                    "The first execution race converges without a notice on Repair and live selection.");
            else if (live && mode == "changing")
                Check.Require(stale?.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged && issues.Count == 0 &&
                    PendingAt(fixture, side, Item).Count == 1, "Changing live evidence retains exactly its own selection without a notice.");
            else
                Check.Require(stale is null && issues.Single().Cause == TextKey.FailChanged &&
                    fixture.Events.Count(item => item.Kind == RunEventKind.Issue) == 1,
                    "An unchanged stale repeat, or a repeated Repair race, raises exactly one failure.");
            if (mode is "ordinary" or "once") continue;
            fixture.LocalView = fixture.Local;
            fixture.DropboxView = fixture.Dropbox;
            await RunSelectionAsync(fixture, PendingAt(fixture, side, Item).Single());
            Check.Require(await SyncedAsync(fixture, Item) && fixture.Inner.Issues(PairId).Count == 0 &&
                PendingAt(fixture, side, Item).Count == 0, "The item's own next opportunity converges and closes its failure and work.");
        }
    }

    private static async Task FirstRunFolderFreshnessAsync()
    {
        foreach (var mode in new[] { "once", "unchanged", "changing" })
        {
            var repeat = mode != "once";
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            var entry = FolderEntry(Item, LocalId);
            fixture.Local.Seed(entry, null);
            var selection = Selected(fixture, Item, entry);
            var attempts = 0;
            fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeEnsure: path =>
            {
                if (path != Item) return;
                attempts++;
                Check.Require(attempts <= 2 && fixture.Inner.Issues(PairId).Count == 0,
                    "First-run folder retry is bounded and has no transient notice.");
                if (mode == "changing") fixture.Local.Seed(entry with { Modified = Stamp.AddSeconds(attempts) }, null);
                if (repeat || attempts == 1) throw new ChangedUnderneathException(path);
            });
            var jobs = SyncJobs.Create(fixture.Context(selection), new SyncPorts(new RecoveryBoundary(),
                RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
            var row = Row.ForOneSide(Side.Local, PathNames.Key(Item), entry, null, [], null, RowContext.Neutral);
            ChangedUnderneathException? stale = null;
            SyncOutcome? outcome = null;
            try { outcome = await jobs.FirstRunFolders.FinalizeFirstRunFolderAsync(row, false, default, null); }
            catch (ChangedUnderneathException error) { stale = error; }
            Check.Require(attempts == 2 && fixture.Events.Count(item =>
                item.Kind == RunEventKind.Wait && item.What == nameof(TextKey.RetryChanged)) == 1,
                "First-run folder finalization announces its one stale retry in output and logs.");
            Check.Require(repeat ? stale is not null : stale is null && outcome?.Succeeded() == true,
                "First-run folder execution converges or exposes its terminal stale failure to the finalizer.");
            Console.WriteLine($"first-run folder execution repeat={repeat}: attempts={attempts}, waits=1, stale={stale is not null}");
        }
    }

    private static async Task SelectionObservationFreshnessAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            var entry = side == Side.Local ? LocalEntry(Item, LocalId) : RemoteEntry(Item, DropboxId);
            var source = side == Side.Local ? fixture.Local : fixture.Dropbox;
            source.Seed(entry, [1]);
            var selection = side == Side.Local ? Selected(fixture, Item, entry) : SeedRemote(fixture, entry);
            var raced = new HookedSide(source, beforeRead: path => throw new ChangedUnderneathException(path));
            if (side == Side.Local) fixture.LocalView = raced;
            else fixture.DropboxView = raced;
            LiveAdmissionStaleException? stale = null;
            try { await RunSelectionAsync(fixture, selection); }
            catch (LiveAdmissionStaleException error) { stale = error; }
            Check.Require(stale?.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged &&
                fixture.Inner.Issues(PairId).Count == 0 && PendingAt(fixture, side, Item).Count == 1,
                "A race in the initial selection read retains its existing work without a notice.");
            fixture.LocalView = fixture.Local;
            fixture.DropboxView = fixture.Dropbox;
            await RunSelectionAsync(fixture, PendingAt(fixture, side, Item).Single());
            Check.Require(await SyncedAsync(fixture, Item) && PendingAt(fixture, side, Item).Count == 0,
                "The next opportunity completes the retained initial-observation race.");
        }
    }

    internal static async Task ConcurrentCoverageCapacityAsync()
    {
        foreach (var cancelHeld in new[] { false, true })
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
            using var fixture = new Fixture(realStore: true);
            var admission = new FakeAdmission { Level = 1 };
            const int hashSlots = 1;
            var ordinary = ScanPass.RowCapacity(admission, hashSlots);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var captures = 0;
            var active = 0;
            var rounds = 0;
            var selected = 0;
            var progress = new List<long>();
            WorkSelection? opportunity = null;
            for (var index = 0; index <= ordinary; index++)
            {
                var entry = RemoteEntry("Download" + index, "remote-" + index);
                fixture.Dropbox.Seed(entry, [1]);
                var seeded = SeedRemote(fixture, entry);
                opportunity ??= seeded;
            }
            ClearObligations(fixture);
            fixture.DropboxView = new ProxySide(fixture.Dropbox)
            {
                OnRead = path => { if (path == "Download" + ordinary) read.TrySetResult(); }
            };
            var boundary = new Boundary(_ => new Lease())
            {
                Capture = async (selection, _, token) =>
                {
                    if (selection.Key != PathNames.DropboxKey("Download" + ordinary))
                    {
                        Interlocked.Increment(ref active);
                        if (Interlocked.Increment(ref captures) == ordinary) entered.TrySetResult();
                        try { await release.Task.WaitAsync(token); }
                        finally { Interlocked.Decrement(ref active); }
                    }
                    return Coverage(selection);
                }
            };
            var store = Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore.Wrap(fixture.Inner);
            var hook = (Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore)store;
            hook.Before = method =>
            {
                if (method.Name == nameof(IStateStore.SelectWork)) Interlocked.Increment(ref selected);
                if (method.Name == nameof(IStateStore.EndWorkPass))
                {
                    Check.Require(Volatile.Read(ref active) == 0, "I: EndWorkPass follows every lane's join");
                    Interlocked.Increment(ref rounds);
                }
            };
            var context = fixture.Context(opportunity!, admission) with
            {
                Store = store,
                Events = value =>
                {
                    if (value.StageKey == TextKey.StageRows && value.Count is { } count)
                        lock (progress) progress.Add(count);
                }
            };
            var syncer = new Syncer(context, new SyncPorts(new RecoveryBoundary(), boundary,
                RefusingExecutionBoundary.Instance, hashSlots));
            var run = syncer.SyncWorkAsync(cancel.Token);
            try
            {
                await (await Task.WhenAny(entered.Task, run).WaitAsync(guard.Token));
                Check.Require(entered.Task.IsCompletedSuccessfully && Volatile.Read(ref selected) == ordinary &&
                    !read.Task.IsCompleted && admission.Admitted == 0,
                    "A4(c), L5-open: R non-upload captures hold the producer without account permits");
                if (cancelHeld)
                {
                    cancel.Cancel();
                    await ThrowsAsync<OperationCanceledException>(() => run);
                    Check.Require(Volatile.Read(ref active) == 0 && Volatile.Read(ref rounds) == 1,
                        "I: cancellation joins all coverage waiters before ending the pass");
                    Check.Require(fixture.Inner.SelectWork(PairId, Generation, context.Scope) is not null,
                        "I: cancelled claims remain selectable for a later attempt");
                    fixture.Inner.EndWorkPass(PairId);
                }
                else
                {
                    release.TrySetResult();
                    await run.WaitAsync(guard.Token);
                    Check.Require(read.Task.IsCompleted && Volatile.Read(ref rounds) == 2,
                        "A4(c): releasing held captures lets X finish in the same work-bearing round");
                    lock (progress)
                        Check.Require(progress.Zip(progress.Skip(1)).All(pair => pair.First <= pair.Second) &&
                            progress.Last() == ordinary + 1,
                            "K: delivered progress is monotonic and counts each branch exactly once");
                    for (var index = 0; index <= ordinary; index++)
                        Check.Require(fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key("Download" + index)) is not null,
                            "A4(c): every download commits after its capture gate opens");
                }
            }
            finally
            {
                release.TrySetResult();
                cancel.Cancel();
                try { await run; } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            }
        }
    }

    internal static async Task ConcurrentMissingParentAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        fixture.Local.Seed(FolderEntry("Parent", "local-parent"), null);
        WorkSelection? opportunity = null;
        for (var index = 0; index < 2; index++)
        {
            var entry = LocalEntry("Parent/Child" + index, "local-child-" + index);
            fixture.Local.Seed(entry, [1]);
            var work = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed,
                entry.Path, null, entry.Id, entry, false, false), index + 1);
            opportunity ??= work;
        }
        ClearObligations(fixture);
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captures = 0;
        var boundary = new Boundary(_ => new Lease())
        {
            Capture = async (selection, _, token) =>
            {
                if (Interlocked.Increment(ref captures) == 2) both.TrySetResult();
                await both.Task.WaitAsync(token);
                return Coverage(selection);
            }
        };
        var context = fixture.Context(opportunity!, new FakeAdmission { Level = 2 });
        await new Syncer(context, new SyncPorts(new RecoveryBoundary(), boundary,
            RefusingExecutionBoundary.Instance, 1)).SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var parent = await fixture.Dropbox.ReadOneAsync("Parent", guard.Token);
        var saved = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key("Parent"));
        Check.Require(parent is { IsFolder: true } && saved?.DropboxId == parent.Id,
            "H: concurrent siblings share one coherent parent binding");
        for (var index = 0; index < 2; index++)
            Check.Require(await fixture.Dropbox.ReadOneAsync("Parent/Child" + index, guard.Token) is not null,
                "H: both children publish after shared parent creation");
        Check.Require(!fixture.Inner.Issues(PairId).Any(), "H: shared missing parent creates no duplicate failure");
    }

    internal static async Task SustainedArrivalRoundsAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var first = RemoteEntry("Arrival0", "remote-0");
        fixture.Dropbox.Seed(first, [1]);
        var opportunity = SeedRemote(fixture, first);
        ClearObligations(fixture);
        const int arrivals = 3;
        var rounds = 0;
        var remaining = arrivals;
        var store = Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore.Wrap(fixture.Inner);
        ((Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore)store).Before = method =>
        {
            if (method.Name != nameof(IStateStore.EndWorkPass)) return;
            var round = Interlocked.Increment(ref rounds);
            if (Interlocked.Decrement(ref remaining) < 0) return;
            var next = RemoteEntry("Arrival" + round, "remote-" + round);
            fixture.Dropbox.Seed(next, [1]);
            SeedRemote(fixture, next);
        };
        var boundary = new Boundary(_ => new Lease())
        { Capture = (selection, _, _) => Task.FromResult(Coverage(selection)) };
        var context = fixture.Context(opportunity) with { Store = store };
        await new Syncer(context, new SyncPorts(new RecoveryBoundary(), boundary,
            RefusingExecutionBoundary.Instance, 1)).SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        Check.Require(rounds == arrivals + 2,
            "N8, G4-open: sustained arrivals extend the same call until the feed stops, then one empty pass ends it");
        for (var index = 0; index <= arrivals; index++)
            Check.Require(fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key("Arrival" + index)) is not null,
                "N7/N8: every finite arrival remains reachable and commits");
    }

    internal static async Task ConcurrentRefusalRedispatchAsync()
    {
        foreach (var makesProgress in new[] { false, true })
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var fixture = new Fixture(realStore: true);
            var admission = new FakeAdmission { Level = 2 };
            WorkSelection? opportunity = null;
            foreach (var path in new[] { "First", "Second" })
            {
                var entry = RemoteEntry(path, "remote-" + path);
                fixture.Dropbox.Seed(entry, [1]);
                var work = SeedRemote(fixture, entry);
                opportunity ??= work;
            }
            ClearObligations(fixture);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var captures = 0;
            var rounds = 0;
            var attempts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            var boundary = new Boundary(_ => new Lease())
            {
                Capture = async (selection, _, token) =>
                {
                    var attempt = attempts.AddOrUpdate(selection.Key, 1, (_, count) => count + 1);
                    if (attempt == 1)
                    {
                        if (Interlocked.Increment(ref captures) == 2) entered.TrySetResult();
                        await entered.Task.WaitAsync(token);
                        if (!makesProgress || selection.Key == PathNames.DropboxKey("Second"))
                            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
                    }
                    return Coverage(selection);
                }
            };
            var store = Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore.Wrap(fixture.Inner);
            ((Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore)store).Before = method =>
            {
                if (method.Name == nameof(IStateStore.EndWorkPass)) Interlocked.Increment(ref rounds);
            };
            var context = fixture.Context(opportunity!, admission) with { Store = store };
            await new Syncer(context, new SyncPorts(new RecoveryBoundary(), boundary,
                RefusingExecutionBoundary.Instance, 1)).SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
            Check.Require(rounds == 3 && attempts[PathNames.DropboxKey("Second")] == 2 &&
                attempts[PathNames.DropboxKey("First")] == (makesProgress ? 1 : 2),
                "N: a refused selection retries in the same call, including a no-progress serial fallback");
            Check.Require(fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key("First")) is not null &&
                fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key("Second")) is not null,
                "N: both selections retire without relying on an external kick");
        }
    }

    internal static Task SelectionRefusalReasonsAsync()
    {
        foreach (var real in new[] { false, true })
        foreach (var mismatch in new[] { false, true })
        foreach (var occupied in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: real);
            var local = LocalEntry(OldPath, LocalId);
            var remote = RemoteEntry(OldPath, DropboxId);
            var saved = SyncedEntries.From(local, remote);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
            var moved = remote with { Path = DestinationPath };
            var selected = SeedRemote(fixture, moved,
                new ChangeNotice(Side.Dropbox, ChangeKind.Renamed, DestinationPath, OldPath,
                    DropboxId, moved, false, false));
            SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
            ClearObligations(fixture);
            Check.Require(fixture.Inner.BeginSelection(selected), "B84 reason fixture admits its initial selection.");
            var coverage = fixture.Inner.CaptureLiveCoverage(selected,
                Coverage(selected).Local, Coverage(selected).Dropbox);
            if (mismatch)
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved with { LocalId = OccupantId }] });
            if (occupied)
                fixture.Inner.Commit(PairId, StateChange.Empty with
                {
                    Record = [SyncedEntries.From(LocalEntry(DestinationPath, "other-local"),
                        RemoteEntry(DestinationPath, "other-remote"))]
                });
            LiveAdmissionStaleException? refused = null;
            try
            {
                fixture.Inner.ValidateLiveCoverage(selected, coverage,
                    [new LiveEffectScope(Side.Dropbox,
                        PathNames.RootedDropboxPath(fixture.Pair.DropboxRoot, DestinationPath), false)]);
            }
            catch (LiveAdmissionStaleException error) { refused = error; }
            LiveAdmissionStaleReason? expected = mismatch ? LiveAdmissionStaleReason.BaselineChanged
                : occupied ? LiveAdmissionStaleReason.SelectionDestinationOccupied : null;
            Check.Require(refused?.Reason == expected,
                $"B84-05/17: baseline mismatch precedes selection occupancy; real={real}, mismatch={mismatch}, occupied={occupied}, expected={expected}, actual={refused?.Reason}.");
        }
        return Task.CompletedTask;
    }

    internal static async Task AttemptedAncestorProgressAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var parent = FolderEntry("Parent", "remote-parent");
        var child = RemoteEntry("Parent/Child", "remote-child");
        fixture.Dropbox.Seed(parent, null);
        fixture.Dropbox.Seed(child, [1]);
        var opportunity = SeedRemote(fixture, parent);
        SeedRemote(fixture, child);
        ClearObligations(fixture);
        var skipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore.Wrap(fixture.Store);
        var hook = (Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore)store;
        hook.After = method =>
        {
            if (method.Name == nameof(IStateStore.WorkAttempted)) skipped.TrySetResult();
        };
        var boundary = new Boundary(_ => new Lease())
        {
            Capture = async (selection, _, token) =>
            {
                if (selection.AnchorWorkId == opportunity.AnchorWorkId)
                    await skipped.Task.WaitAsync(token);
                return Coverage(selection);
            }
        };
        var context = fixture.Context(opportunity, new FakeAdmission { Level = 2 }) with { Store = store };
        await new Syncer(context, new SyncPorts(new RecoveryBoundary(), boundary,
            RefusingExecutionBoundary.Instance, 1) { RunBatchAsync = (_, _, _, _, _, _) => Task.CompletedTask })
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        Check.Require(skipped.Task.IsCompletedSuccessfully &&
            fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(child.Path)) is not null &&
            PendingAt(fixture, Side.Dropbox, child.Path).Count == 0,
            "B84-19: a skipped dependent completes in the same run after its in-flight ancestor retires.");
    }

    internal static Task AttemptedAncestorSkipsAsync() => AttemptedAncestorSkipsAsync(held: false);

    internal static Task AttemptedSkipReplaysAsync() => AttemptedAncestorSkipsAsync(held: true);

    private static async Task AttemptedAncestorSkipsAsync(bool held)
    {
        // Two dependents exercise repeated admission; doubling catches duplicate failures proportional to fan-out.
        foreach (var dependents in new[] { 2, 4 })
        {
            using var fixture = new Fixture(realStore: true);
            var parent = FolderEntry("Parent", "remote-parent");
            fixture.Dropbox.Seed(parent, null);
            if (held)
            {
                var localParent = FolderEntry(parent.Path, "local-parent");
                fixture.Local.Seed(localParent, null);
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(localParent, parent)] });
            }
            var opportunity = SeedRemote(fixture, parent);
            var children = Enumerable.Range(0, dependents)
                .Select(index => RemoteEntry("Parent/Child" + index, "remote-child-" + index)).ToArray();
            foreach (var child in children)
            {
                fixture.Dropbox.Seed(child, [1]);
                SeedRemote(fixture, child);
            }
            ClearObligations(fixture);
            var local = new CountingSide(fixture.Local);
            var remote = new CountingSide(fixture.Dropbox);
            fixture.LocalView = local;
            fixture.DropboxView = remote;
            var rounds = 0;
            var attempts = 0;
            var returned = 0;
            var admissions = 0;
            var ended = 0;
            var completions = 0;
            var failures = new List<RunEvent>();
            var refusal = new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
            var boundary = new Boundary(_ => throw new InvalidOperationException("Refused coverage grants no effects."))
            {
                Capture = (selection, _, _) =>
                {
                    Check.Require(!held && selection.AnchorWorkId == opportunity.AnchorWorkId,
                        "Only the unheld pending ancestor captures coverage, never its descendants.");
                    admissions++;
                    return Task.FromException<LiveCoverage>(refusal);
                }
            };
            var store = Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore.Wrap(fixture.Store);
            var hook = (Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore)store;
            hook.Before = method =>
            {
                if (method.Name == nameof(IStateStore.BeginSelection))
                {
                    if (held && attempts == 0)
                        fixture.Inner.Commit(PairId, StateChange.Empty with
                        {
                            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, parent.Path,
                                new IssueSubject([new IssueItem(Side.Dropbox, parent.Path, parent.Id)], null))]
                        });
                    attempts++;
                }
                if (held && method.Name == nameof(IStateStore.AttachIssueWork))
                {
                    admissions++;
                    fixture.Inner.Commit(PairId, StateChange.Empty with
                    {
                        Close = [(parent.Path, new[] { IssueKind.Failure })]
                    });
                }
                if (method.Name == nameof(IStateStore.EndSelection)) ended++;
                if (method.Name == nameof(IStateStore.CompleteWork)) completions++;
                if (method.Name != nameof(IStateStore.EndWorkPass)) return;
                Check.Require(attempts == 1 && returned == attempts && admissions == 1 && ended == 1,
                    "B84-01: known-attempted ancestors never reenter admission or increment duplicate contention: " +
                    $"held={held}, dependents={dependents}, round={rounds}, attempts={attempts}, " +
                    $"returned={returned}, admissions={admissions}, ended={ended}.");
                Check.Require(held ? failures.Count == 0 : failures.Count == 1 && ReferenceEquals(failures[0].Error, refusal),
                    "Only the original coverage refusal logs Failed; held ancestors and duplicate encounters log none.");
                Check.Require(completions == 0 && local.Work == 0 && remote.Work == 0 &&
                    children.All(child => PendingAt(fixture, Side.Dropbox, child.Path).Count == 1),
                    "Duplicate skips credit no retirement and retain every descendant without effects.");
                rounds++;
                attempts = returned = admissions = ended = 0;
                failures.Clear();
            };
            hook.After = method => { if (method.Name == nameof(IStateStore.BeginSelection)) returned++; };
            var context = fixture.Context(opportunity, new FakeAdmission { Level = 2 }) with
            {
                Store = store,
                Events = value => { if (value.Kind == RunEventKind.Failed) failures.Add(value); }
            };
            await new Syncer(context, new SyncPorts(new RecoveryBoundary(), boundary,
                RefusingExecutionBoundary.Instance, 1)).SyncWorkAsync(default);
            Check.Require(rounds == (held ? 1 : 2) && completions == 0 && fixture.Inner.Issues(PairId).Count == 0 &&
                PendingAt(fixture, Side.Dropbox, parent.Path).Count == 1,
                "B84-01: only genuine refusal causes contention replay; skipped dependencies credit no retirement.");
        }
    }

    internal static async Task DuplicateSelectionKeepsClaimAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var entry = RemoteEntry(Item, DropboxId);
        fixture.Dropbox.Seed(entry, [1]);
        var opportunity = SeedRemote(fixture, entry);
        ClearObligations(fixture);
        var selection = fixture.Inner.SelectWork(PairId, Generation, fixture.Context(opportunity).Scope)!;
        Check.Require(fixture.Inner.BeginSelection(selection), "The original lane owns the admitted claim.");
        var runner = Runner(fixture, selection,
            new Boundary(_ => throw new InvalidOperationException("A duplicate cannot execute.")),
            (_, _, _) => throw new InvalidOperationException("A duplicate cannot capture coverage."));
        Check.Require(await runner.RunAsync(selection, default) is null &&
            fixture.Hooks.Count(nameof(IStateStore.EndSelection)) == 0 &&
            fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 0,
            "The duplicate lane skips without ending or completing the original selection.");
        var peer = new SelectionPeer(PathNames.Key(Item), selection.ThroughWorkId);
        fixture.Inner.ClaimSelectionPeer(selection, peer);
        fixture.Inner.EndSelection(selection);
        var released = await ThrowsAsync<LiveAdmissionStaleException>(() =>
        {
            fixture.Inner.ClaimSelectionPeer(selection, peer);
            return Task.CompletedTask;
        });
        Check.Require(released.Reason == LiveAdmissionStaleReason.SelectionChanged,
            "The original claim stays valid through the skip and is released only by its owner.");
        Check.Require(!fixture.Inner.EndWorkPass(PairId), "Neither claim admission nor release earns retirement.");
    }

    internal static async Task ConcurrentSelectionIsolationAsync()
    {
        using var fixture = new Fixture();
        var entry = LocalEntry("Parent/Child", "child");
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed,
            entry.Path, null, entry.Id, entry, false, false));
        var jobs = SyncJobs.Create(fixture.Context(selection), new SyncPorts(new RecoveryBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        var first = jobs.Items.Rows.ForConcurrentSelection();
        var second = jobs.Items.Rows.ForConcurrentSelection();
        Check.Require(!ReferenceEquals(first, second), "K: concurrent selections never share mutable row facts");
        first.SkippedLocal.Add(PathNames.Key("Parent"));
        Check.Require(!second.SkippedLocal.Covers(PathNames.Key(entry.Path)),
            "K: a lane's skipped subtree never leaks to another lane");
        jobs.Items.SelectRows(first);
        await Task.Run(() =>
        {
            jobs.Items.SelectRows(second);
            Check.Require(ReferenceEquals(jobs.Items.Rows, second), "K: the nested flow selects its own rows");
        });
        Check.Require(ReferenceEquals(jobs.Items.Rows, first), "K: nested flow selection leaves the caller's rows intact");
    }

    internal static async Task ConcurrentLaneFailureAsync()
    {
        foreach (var unexpected in new[] { false, true })
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var fixture = new Fixture(realStore: true);
            var admission = new FakeAdmission { Level = 2 };
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new InvalidOperationException("synthetic infrastructure failure");
            var active = 0;
            WorkSelection? opportunity = null;
            foreach (var path in new[] { "Held", "Failing", "Independent" })
            {
                var entry = RemoteEntry(path, "remote-" + path);
                fixture.Dropbox.Seed(entry, [1]);
                var selected = SeedRemote(fixture, entry);
                opportunity ??= selected;
            }
            ClearObligations(fixture);
            fixture.LocalView = new ProxySide(fixture.Local)
            {
                BeforeStage = async (path, token) =>
                {
                    if (path == "Held")
                    {
                        Interlocked.Increment(ref active);
                        held.TrySetResult();
                        try { await release.Task.WaitAsync(token); }
                        finally { Interlocked.Decrement(ref active); }
                    }
                    if (path == "Failing")
                    {
                        await held.Task.WaitAsync(token);
                        failed.TrySetResult();
                        if (unexpected) throw failure;
                        throw new ItemFailedException(path, TextKey.ReadLocalFailed, null);
                    }
                }
            };
            var boundary = new Boundary(_ => new Lease())
            { Capture = (selection, _, _) => Task.FromResult(Coverage(selection)) };
            var store = Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore.Wrap(fixture.Inner);
            var hook = (Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore)store;
            hook.Before = method =>
            {
                if (method.Name == nameof(IStateStore.EndWorkPass))
                    Check.Require(Volatile.Read(ref active) == 0, "J: infrastructure failure joins active siblings before pass disposal");
            };
            var context = fixture.Context(opportunity!, admission) with { Store = store };
            var run = new Syncer(context, new SyncPorts(new RecoveryBoundary(), boundary,
                RefusingExecutionBoundary.Instance, 1)).SyncWorkAsync(guard.Token);
            try
            {
                await failed.Task.WaitAsync(guard.Token);
                if (unexpected)
                {
                    var caught = await ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(guard.Token));
                    Check.Require(ReferenceEquals(caught, failure), "J: the first infrastructure fault survives sibling cancellation");
                    Check.Require(Volatile.Read(ref active) == 0, "J: infrastructure fault joins cancelled siblings");
                }
                else
                {
                    Check.Require(Volatile.Read(ref active) == 1 && !run.IsCompleted,
                        "J: an expected item failure does not cancel an unrelated active lane");
                    release.TrySetResult();
                    await run.WaitAsync(guard.Token);
                    Check.Require(await fixture.Local.ReadOneAsync("Independent", guard.Token) is not null &&
                        await fixture.Local.ReadOneAsync("Held", guard.Token) is not null &&
                        fixture.Inner.Issues(PairId).Count(issue => issue.Path == "Failing" && issue.Kind == IssueKind.Failure) == 1,
                        "J: siblings finish and the failed item owns one deduplicated notice");
                }
            }
            finally
            {
                release.TrySetResult();
                guard.Cancel();
                try { await run; }
                catch (OperationCanceledException) when (guard.IsCancellationRequested) { }
                catch (InvalidOperationException caught) when (ReferenceEquals(caught, failure)) { }
            }
        }
    }

    internal static async Task ConcurrentUploadCapacityAsync()
    {
        foreach (var saturated in new[] { false, true })
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var fixture = new Fixture(realStore: true);
            var admission = saturated
                ? new FakeAdmission { Level = 1, UploadCapacity = 1, WorkCapacity = 2 }
                : new FakeAdmission { Level = 1, UploadCapacity = 1 };
            const int hashSlots = 1;
            var total = ScanPass.TotalCapacity(admission, hashSlots);
            var queued = saturated ? total - 1 : ScanPass.RowCapacity(admission, hashSlots) + 1;
            var staging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var selections = 0;
            var rounds = 0;
            var readRound = -1;
            WorkSelection? opportunity = null;
            for (var index = 0; index <= queued; index++)
            {
                var entry = LocalEntry("Upload" + index, "local-" + index);
                fixture.Local.Seed(entry, [1]);
                opportunity ??= Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed,
                    entry.Path, null, entry.Id, entry, false, false), index + 1);
                if (index > 0) Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed,
                    entry.Path, null, entry.Id, entry, false, false), index + 1);
            }
            var download = RemoteEntry("Download", "remote-download");
            fixture.Dropbox.Seed(download, [1]);
            SeedRemote(fixture, download);
            ClearObligations(fixture);
            fixture.DropboxView = new ProxySide(fixture.Dropbox)
            {
                BeforeStage = (path, token) =>
                {
                    if (PathNames.Key(path) != PathNames.Key("Upload0")) return Task.CompletedTask;
                    staging.TrySetResult();
                    return release.Task.WaitAsync(token);
                },
                OnRead = path =>
                {
                    if (path != download.Path) return;
                    Interlocked.CompareExchange(ref readRound, Volatile.Read(ref rounds), -1);
                    read.TrySetResult();
                }
            };
            fixture.LocalView = new ProxySide(fixture.Local)
            {
                AfterPublish = entry => { if (entry.Path == download.Path) published.TrySetResult(); }
            };
            var store = Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore.Wrap(fixture.Inner);
            var hook = (Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore)store;
            hook.Before = method =>
            {
                if (method.Name == nameof(IStateStore.SelectWork)) Interlocked.Increment(ref selections);
                if (method.Name == nameof(IStateStore.EndWorkPass)) Interlocked.Increment(ref rounds);
            };
            hook.After = method =>
            {
                if (method.Name == nameof(IStateStore.CompleteWork) && published.Task.IsCompleted)
                    completed.TrySetResult();
            };
            var boundary = new Boundary(_ => new Lease())
            { Capture = (selection, _, _) => Task.FromResult(Coverage(selection)) };
            // Preserve the fixture's completed-first-repair projection while instrumenting the real store.
            hook.Inner = fixture.Store;
            var events = new System.Collections.Concurrent.ConcurrentQueue<RunEvent>();
            var context = fixture.Context(opportunity!, admission) with { Store = store, Events = events.Enqueue };
            var syncer = new Syncer(context, new SyncPorts(new RecoveryBoundary(), boundary,
                RefusingExecutionBoundary.Instance, hashSlots));
            var run = syncer.SyncWorkAsync(guard.Token);
            try
            {
                await (await Task.WhenAny(staging.Task, run).WaitAsync(guard.Token));
                Check.Require(staging.Task.IsCompletedSuccessfully,
                    "A4: U0 reaches staging; events=" + string.Join(" | ", events.Select(value => value.Path + ":" + value.Error)));
                var blocked = admission.WaitBlockedAsync(queued);
                await (await Task.WhenAny(blocked, run).WaitAsync(guard.Token));
                Check.Require(blocked.IsCompletedSuccessfully, "A4: queued uploads reach the pool gate");
                if (saturated)
                    Check.Require(Volatile.Read(ref selections) == total && !read.Task.IsCompleted,
                        "A4(b), L5-open: the total ceiling holds X before its selection");
                else
                {
                    await read.Task.WaitAsync(guard.Token);
                    await published.Task.WaitAsync(guard.Token);
                    await completed.Task.WaitAsync(guard.Token);
                    Check.Require(admission.Blocked == queued && admission.StageActive == 1 &&
                        Volatile.Read(ref rounds) == 0,
                        "A4(a): X retires while U0 and every queued upload remain held");
                }
                release.TrySetResult();
                await run.WaitAsync(guard.Token);
                Check.Require(read.Task.IsCompleted && published.Task.IsCompleted &&
                    Volatile.Read(ref readRound) == 0 && Volatile.Read(ref rounds) == 2,
                    "A4: X starts in the first round, followed by the trailing empty round");
                Check.Require(admission.StageAcquired == queued + 1 && admission.Blocked == 0 &&
                    fixture.Inner.SelectWork(PairId, Generation, context.Scope) is null &&
                    fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(download.Path)) is not null,
                    "A4: every item, including X after saturation, durably completes");
                fixture.Inner.EndWorkPass(PairId);
            }
            finally
            {
                release.TrySetResult();
                guard.Cancel();
                try { await run; } catch (OperationCanceledException) when (guard.IsCancellationRequested) { }
            }
        }
    }

    private static async Task EmptyOpportunityAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var store = new StoreCounters(fixture.Inner);
        var local = new CountingSide(fixture.Local);
        var remote = new CountingSide(fixture.Dropbox);
        var events = new List<RunEvent>();
        var opportunity = new WorkSelection(PairId, Generation, 0, Side.Local, VolumeKey, null,
            string.Empty, 0, 0);
        var context = fixture.Context(opportunity) with
        { Store = store, Local = local, Dropbox = remote, Events = events.Add };
        var syncer = new Syncer(context, new SyncPorts(new RecoveryBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        await syncer.SyncWorkAsync(default);
        Check.Require(store.MemberCalls.GetValueOrDefault(nameof(IStateStore.FirstRepairDone)) == 0 &&
            store.MemberCalls.GetValueOrDefault(nameof(IStateStore.SelectWork)) == 1 &&
            store.MemberCalls.GetValueOrDefault(nameof(IStateStore.BeginSelection)) == 0,
            "an empty opportunity selects once without reading FirstRepairDone or starting a selection");
        Check.Require(local.Reads + local.Listings + local.Opens + local.Hashes +
            remote.Reads + remote.Listings + remote.Opens + remote.Hashes == 0,
            "an empty opportunity performs no provider observation");
        Check.Require(events.Count(value => value.StageKey == TextKey.StageRows) == 2 &&
            events.Single(value => value.StageKey == TextKey.StageRows && value.Ended).Count == 0,
            "an empty opportunity closes its one row stage with zero completed branches");
    }

    // A live selection runs with no work scope of its own, so an unrelated sole single-file scope is the only way
    // SelectDirect could turn true; the reobserve turn must withdraw it so the replacement takes the session route.
    private static async Task LiveReobserveRevokesDirectAsync()
    {
        using var fixture = new Fixture();
        var network = new FakeNetworkSignal();
        network.SetAvailable(true);
        // This path never backs off; the values only satisfy the limiter's constructor.
        var limiter = new AdaptiveLimiter(1, 1, TimeProvider.System, network, _ => { },
            new FailureRetryOwner.BackoffSettings(1, 1, 0, 1));
        var folder = FolderEntry(Folder, FolderId);
        fixture.Local.Seed(folder, null);
        fixture.Dropbox.Seed(folder, null);
        await using var unrelated = await limiter.EnterWorkAsync(true, default);
        Check.Require(limiter.SelectDirect(1),
            "An unrelated sole single-file scope would otherwise let the replacement stage direct.");
        var selection = Selected(fixture, Folder, folder);
        var ready = await Runner(fixture, selection, new Boundary(_ => new Lease()),
            (_, _, _) => Task.FromResult(Coverage(selection)), limiter).RunAsync(selection, default);
        Check.Require(ready is true, "The live reobserve completes.");
        Check.Require(!limiter.SelectDirect(1),
            "A live reobserve withdraws direct while an unrelated sole scope is outstanding.");
    }

    // Engine 8.7: descendants stream through the ordinary path, so a selection never refuses on its own work; a later
    // arrival at its path, or another selection's pending ancestor, still does.
    private static async Task SeparateWalkWorkAsync()
    {
        using var fixture = UnreadableFixture(withMemberFolder: false);
        var folder = FolderEntry(Folder, FolderId);
        var parent = Selected(fixture, Folder, folder);
        var child = Selected(fixture, Plain, LocalEntry(Plain, LocalId), page: 2);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, Folder,
                new IssueSubject([new IssueItem(Side.Local, Folder, FolderId)], null))]
        });
        Check.Require(await RunSelectionAsync(fixture, parent), "parent attaches its own held residual and completes");
        Check.Require(fixture.Inner.ReadWork(child).Single().Before is null,
            "a separately acquired walked child retains its own preimage while parent is held");
        fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(Folder, IssueClosure.AfterEffect)] });
        var released = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, FolderId).Single();
        Check.Require(await RunSelectionAsync(fixture, released), "released parent does not claim separately acquired child work");
        Check.Require(await RunSelectionAsync(fixture, child) && await SyncedAsync(fixture, Plain),
            "separately acquired child progresses after held parent releases");
    }

    private static async Task OwnSelectionFenceAsync()
    {
        var folder = FolderEntry(Folder, FolderId);
        using (var fixture = UnreadableFixture(withMemberFolder: false))
        {
            Check.Require(await RunNoticeAsync(fixture, Folder, folder) && await SyncedAsync(fixture, Plain),
                "A folder selection's child passes the fence on the selection's own work");
            var queries = fixture.Hooks.AncestorQueries;
            Check.Require(queries.Any(query => query.Through is not null) && queries.Any(query => query.Count == 0),
                "The fixture exercises both own-only and empty ancestor answers.");
            for (var index = 0; index < queries.Count; index++)
                if (queries[index].Through is not null)
                    Check.Require(index > 0 && queries[index - 1].Count > 0 &&
                        queries[index - 1].Side == queries[index].Side,
                        "Only a nonempty own-ancestor answer warrants a later-work query.");
        }
        using (var fixture = UnreadableFixture(withMemberFolder: false))
        {
            var selection = Selected(fixture, Folder, folder);
            Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null, FolderId, folder, false, false), 2);
            var refused = await ThrowsAsync<LiveAdmissionStaleException>(() => RunSelectionAsync(fixture, selection));
            Check.Require(refused.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged &&
                !await SyncedAsync(fixture, Plain), "Work arriving after the selection still refuses its descendants");
            var owners = fixture.Inner.PendingAncestors(PairId, Generation, Side.Local, [selection.Key]).ToArray();
            Check.Require(owners.Any(owner => owner.AnchorWorkId > selection.ThroughWorkId) &&
                fixture.Hooks.AncestorQueries.Any(query => query.Through is null && query.Count > 0),
                "After the own Record advances its preimage, the later arrival is a separate owner and the first query refuses it.");
        }
        using (var fixture = UnreadableFixture(withMemberFolder: false))
        {
            Selected(fixture, Folder, folder);
            var child = LocalEntry(Plain, LocalId);
            var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Plain, null, child.Id, child, false, false), 2);
            await ThrowsAsync<LiveAdmissionStaleException>(() => RunSelectionAsync(fixture, selection));
            Check.Require(!await SyncedAsync(fixture, Plain), "Another selection's pending ancestor still refuses");
            Check.Require(fixture.Hooks.AncestorQueries.All(query => query.Through is null),
                "A foreign anchor refuses without a second query.");
        }
    }

    private const string Member = Folder + "/member";
    private const string Plain = Folder + "/plain";
    private const string FolderId = "1";
    private const string MemberId = "2";

    // Bug 108: the fence checks both sides, so the ancestor must be run from both. The Local children sort ahead of
    // the Dropbox folder row by work id; each selection pre-runs that counterpart row before its own branch.
    private static async Task CounterpartAncestorFirstAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        fixture.Local.Seed(FolderEntry(Folder, FolderId), null);
        fixture.Dropbox.Seed(FolderEntry(Folder, DropboxId), null);
        var children = new[] { Folder + "/a", Folder + "/b", Folder + "/c" };
        var selections = new List<WorkSelection>();
        for (var index = 0; index < children.Length; index++)
        {
            var entry = LocalEntry(children[index], "local-" + index);
            fixture.Local.Seed(entry, [(byte)index]);
            selections.Add(Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, children[index], null,
                entry.Id, entry, false, false), index + 1));
        }
        SeedRemote(fixture, FolderEntry(Folder, DropboxId));
        Check.Require(PendingAt(fixture, Side.Dropbox, Folder).Any(),
            "A108a: the fixture holds a pending counterpart-side ancestor");
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, selections[0], new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        Check.Require(events.All(item => item.Error is not LiveAdmissionStaleException),
            "A108a: a counterpart-side ancestor leaves no refusal behind");
        var firstAncestor = events.FindIndex(item => item.Path == Folder);
        var firstChild = events.FindIndex(item => item.Path == children[0]);
        Check.Require(firstAncestor >= 0 && firstChild > firstAncestor,
            "A108a: the counterpart-side ancestor runs before its children; ancestor=" + firstAncestor +
            ", child=" + firstChild + "; paths=" + string.Join(",", events.Where(item => item.Path.Length > 0)
                .Select(item => item.Kind + ":" + item.Path)));
        foreach (var child in children)
            Check.Require(await fixture.Dropbox.ReadOneAsync(child, default) is not null,
                "A108a: every child of the pre-run ancestor syncs, " + child);
        Check.Require(!PendingAt(fixture, Side.Dropbox, Folder).Any(),
            "A108a: the counterpart-side ancestor's own work retires");
    }

    // A fresh real store owes a source obligation; clearing both sides is what lets SelectWork offer seeded work.
    private static void ClearObligations(Fixture fixture)
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var source = fixture.Inner.ReadSource(PairId, side)!.Value;
            fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance,
                source.Cursor, null);
        }
    }

    // The fence now refuses a live branch before its observation, so a refusal costs no provider call.
    private static async Task RefusalMakesNoProviderCallsAsync()
    {
        using var fixture = new Fixture(realStore: true);
        fixture.Local.Seed(FolderEntry(Folder, FolderId), null);
        var child = LocalEntry(Plain, LocalId);
        fixture.Local.Seed(child, [1]);
        var selection = Selected(fixture, Plain, child);
        fixture.Dropbox.Seed(FolderEntry(Folder, DropboxId), null);
        SeedRemote(fixture, FolderEntry(Folder, DropboxId));
        var local = new CountingSide(fixture.Local);
        var remote = new CountingSide(fixture.Dropbox);
        fixture.LocalView = local;
        fixture.DropboxView = remote;
        var error = await ThrowsAsync<LiveAdmissionStaleException>(() =>
            RunSelectionAsync(fixture, selection));
        Check.Require(error.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged &&
            error.Detail?.Contains(PathNames.Key(Folder), StringComparison.Ordinal) == true,
            "A108b: the refusal names the blocking ancestor key");
        Check.Require(local.Reads + remote.Reads + local.Listings + remote.Listings +
            local.Work + remote.Work == 0,
            "A108b: a refused branch makes no provider call");
    }

    // An ancestor that fails leaves the user one deduplicated notice; the child is neither lost nor silently stuck.
    private static async Task FailedAncestorNoticeAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        fixture.Local.Seed(FolderEntry(Folder, FolderId), null);
        var child = LocalEntry(Plain, LocalId);
        fixture.Local.Seed(child, [1]);
        var selection = Selected(fixture, Plain, child);
        fixture.Dropbox.Seed(FolderEntry(Folder, DropboxId), null);
        SeedRemote(fixture, FolderEntry(Folder, DropboxId));
        fixture.Dropbox.FailNext(Folder, new ItemFailedException(Folder, TextKey.FailDropboxNoPermission, null));
        var events = new List<RunEvent>();
        var boundary = new Boundary(_ => new Lease());
        await LiveSyncer(fixture, selection, boundary, events).SyncWorkAsync(default);
        await LiveSyncer(fixture, selection, boundary, events).SyncWorkAsync(default);
        var notices = fixture.Inner.Issues(PairId, [IssueKind.Failure]).Where(issue => issue.Path == Folder).ToArray();
        Check.Require(notices.Length == 1,
            "A108c: a failed ancestor leaves exactly one deduplicated notice; found " + notices.Length + " at " +
            string.Join(",", fixture.Inner.Issues(PairId, [IssueKind.Failure]).Select(issue => issue.Path + "/" + issue.Cause)));
        var synced = await SyncedAsync(fixture, Plain);
        var held = fixture.Inner.HeldCovers(PairId, PathNames.Key(Plain), null);
        var retained = PendingAt(fixture, Side.Local, Plain).Any();
        Check.Require(!synced && held && retained,
            "A108c: the child waits on that notice with its work retained; synced=" + synced + ", held=" + held +
            ", retained=" + retained);
    }

    // #109: a pending folder deletion is not reordered behind its children; it runs the survivor-aware folder plan,
    // which covers the descendants' pending evidence. The plan keeps changed content and removes unchanged content.
    private static async Task MovedOutOfDeletedFolderAsync(Side side)
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        var localFolder = FolderEntry("Folder", "local-folder");
        var remoteFolder = FolderEntry("Folder", "remote-folder");
        var local = LocalEntry("Folder/File", LocalId);
        var remote = RemoteEntry("Folder/File", DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        { Record = [SyncedEntries.From(localFolder, remoteFolder), SyncedEntries.From(local, remote)] });
        fixture.Local.Seed(side == Side.Local ? local with { Path = "Other/File" } : local, [1]);
        fixture.Dropbox.Seed(side == Side.Dropbox ? remote with { Path = "Other/File" } : remote, [1]);
        (side == Side.Local ? fixture.Dropbox : fixture.Local).Seed(side == Side.Local ? remoteFolder : localFolder, null);
        (side == Side.Local ? fixture.Local : fixture.Dropbox).Seed(FolderEntry("Other", "other-folder"), null);
        var entry = side == Side.Local ? local : remote;
        var notices = new[]
        {
            new ChangeNotice(side, ChangeKind.Renamed, "Other/File", "Folder/File", entry.Id,
                entry with { Path = "Other/File" }, false, false),
            new ChangeNotice(side, ChangeKind.Deleted, "Folder", null,
                side == Side.Local ? localFolder.Id : remoteFolder.Id, null, false, true)
        };
        WorkSelection selection;
        if (side == Side.Local)
        {
            selection = Seed(fixture, notices[0]);
            Seed(fixture, notices[1], 2);
        }
        else
        {
            var localSource = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
            fixture.Inner.AcquireJournalPage(VolumeKey, null, new JournalPosition(1, 1),
                [(PairId, Generation, localSource.Epoch, localSource.Revision, false)], []);
            selection = SeedRemote(fixture, entry with { Path = "Other/File" });
            var source = fixture.Inner.ReadSource(PairId, side)!.Value;
            fixture.Inner.AcquireDropboxPage(PairId, Generation, source.Epoch, source.Revision,
                source.Provenance!, source.Cursor, notices, Cursor);
        }
        var events = new List<RunEvent>();
        var boundary = new Boundary(request =>
        {
            fixture.Inner.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint);
            return new Lease { AllowsRelease = true };
        });
        await LiveSyncer(fixture, selection, boundary, events,
            new FakeRecovery { Evidence = new DriveEvidence(1, 1, 1) }).SyncWorkAsync(default);
        Check.Require(!PendingAt(fixture, side, "Folder", "Folder/File", "Other/File").Any(),
            $"move-out {side}: both selections retire; " + string.Join(" | ", events.Select(e =>
                e.Path + ":" + e.Error + ":" + (e.Error as LiveAdmissionStaleException)?.Detail)) +
            "; issues=" + string.Join(" | ", fixture.Inner.Issues(PairId).Select(issue => issue.Path + ":" + issue.Cause)));
        foreach (var endpoint in new[] { fixture.Local, fixture.Dropbox })
        {
            var survivor = await endpoint.ReadOneAsync("Other/File", default);
            Check.Require(survivor is not null && await endpoint.ContentHashAsync(survivor, default) == remote.ContentHash &&
                await endpoint.ReadOneAsync("Folder", default) is null,
                $"move-out {side}: original bytes survive at new path on {endpoint.Side}, deleted folder stays gone");
        }
    }

    private static async Task FolderCycleDependenciesAsync()
    {
        foreach (var mode in new[] { "old-path", "peer", "later-peer", "unrelated" })
        {
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            var ownEntry = FolderEntry(mode == "old-path" ? "Parent" : "Owner", "own");
            var remote = FolderEntry("Parent", "remote-parent");
            var branch = FolderEntry("Else/Branch", "branch");
            var remoteBranch = FolderEntry("Parent/Branch", "remote-branch");
            var saved = SyncedEntries.From(ownEntry, remote);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record =
                [saved, SyncedEntries.From(branch, remoteBranch),
                 SyncedEntries.From(FolderEntry("Else/Branch/Below", "below"),
                    FolderEntry("Parent/Branch/Below", "remote-below"))] });
            fixture.Dropbox.Seed(remote, null);
            fixture.Dropbox.Seed(remoteBranch, null);
            fixture.Dropbox.Seed(FolderEntry("Parent/Branch/Below", "remote-below"), null);
            foreach (var suffix in new[] { "Branch/Leaf", "Branch/Below/Leaf", "Last" })
            {
                var localPath = suffix == "Last" ? ownEntry.Path + "/Last" : "Else/" + suffix;
                var leaf = RemoteEntry("Parent/" + suffix, "remote-" + suffix);
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record =
                    [SyncedEntries.From(LocalEntry(localPath, "local-" + suffix), leaf)] });
                fixture.Dropbox.Seed(leaf with { Size = 2, Revision = "rev-2",
                    ContentHash = DropboxContentHash.Compute(new byte[] { 2, 2 }) }, [2, 2]);
            }
            var own = Seed(fixture, Deleting(ownEntry.Path, ownEntry.Id));
            var context = fixture.Context(own) with
            {
                CaptureFolderCursorAsync = _ => Task.FromResult(Cursor),
                ReadFolderChangesAsync = (_, ct) => NoFolderChangesAsync(ct)
            };
            var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
                new Boundary(_ => new Lease()), RefusingExecutionBoundary.Instance, 0));
            jobs.Ancestors.RunSelection(own);
            var id = await jobs.Items.Folders.PrepareFolderRemovalAsync(
                Row.ForOneSide(Side.Dropbox, saved.LocalKey, remote, saved, [], null, RowContext.Neutral),
                Side.Dropbox, new HashSet<string>(), new HashSet<string>(), default);
            await jobs.Items.Folders.RunPreparedFolderRemovalsAsync([id!], default);
            Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, branch.Path,
                mode == "old-path" ? "Parent/Branch" : null, branch.Id, branch, false, false), 2);
            if (mode is "peer" or "later-peer")
            {
                var peer = SeedRemote(fixture, remote);
                own = own with { Peer = new SelectionPeer(peer.Key,
                    mode == "later-peer" ? peer.AnchorWorkId - 1 : peer.ThroughWorkId) };
                jobs.Ancestors.RunSelection(own);
            }
            await jobs.Items.Folders.FinishFolderRemovalAsync(id!, own, default);
            var pending = fixture.Inner.PendingFolderFollowUps(PairId, id!, -1, int.MaxValue).ToArray();
            Check.Require(new[] { "Parent/Branch/Leaf", "Parent/Branch/Below/Leaf" }
                .All(path => pending.Any(item => item.Path == path)),
                "a refused branch and its dependent descendant remain fenced: " + mode);
            var failures = fixture.Inner.Issues(PairId).Where(issue => issue.Cause == TextKey.FailFolderDependencyCycle).ToArray();
            Check.Require(mode is "old-path" or "peer"
                ? failures.Any(issue => issue.Path == "Parent/Branch/Leaf") &&
                    failures.Any(issue => issue.Path == "Parent/Branch/Below/Leaf")
                : failures.Length == 0,
                "old-path and claimed-peer cycles publish Failures; unrelated and later peer work do not: " + mode +
                $"; own={own}; pending=" + string.Join(" | ", pending.Select(item => item.Path)) +
                "; issues=" + string.Join(" | ", fixture.Inner.Issues(PairId).Select(issue => issue.Path + ":" + issue.Cause)) +
                "; local-ancestors=" + string.Join(" | ", PendingAt(fixture, Side.Local, "Else/Branch", "Owner")) +
                "; remote-ancestors=" + string.Join(" | ", PendingAt(fixture, Side.Dropbox, "Parent/Branch", "Parent")));
            if (mode != "later-peer")
                Check.Require(await fixture.Local.ReadOneAsync(ownEntry.Path + "/Last", default) is not null,
                    "an independent survivor advances despite the refused branch and descendant: " + mode);
        }
    }

    private static async Task DeferredSurvivorRestartAsync()
    {
        foreach (var phase in new[] { "frozen", "accepted", "completed" })
        {
            var afterEffect = phase == "completed";
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            var local = FolderEntry("Parent", "local-parent");
            var remote = FolderEntry("Parent", "remote-parent");
            var child = FolderEntry("Parent/Child", "old-child");
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record =
                [SyncedEntries.From(local, remote), SyncedEntries.From(child with { Id = "local-child" }, child)] });
            fixture.Dropbox.Seed(remote, null);
            fixture.Dropbox.Seed(child with { Id = "replacement-child" }, null);
            var root = Seed(fixture, Deleting(local.Path, local.Id));
            ExecutorContext Context() => fixture.Context(root) with
            {
                CaptureFolderCursorAsync = _ => Task.FromResult(Cursor),
                ReadFolderChangesAsync = (_, ct) => NoFolderChangesAsync(ct)
            };
            (ItemSync Items, AncestorFence Ancestors) Jobs()
            {
                var composed = SyncJobs.Create(Context(), new SyncPorts(new RecoveryBoundary(),
                    new Boundary(_ => new Lease()), RefusingExecutionBoundary.Instance, 0));
                return (composed.Items, composed.Ancestors);
            }
            var jobs = Jobs();
            jobs.Ancestors.RunSelection(root);
            var saved = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(local.Path))!;
            // Live routes (FolderSync.cs:354/:399) compose this same live builder and durable freeze.
            string id;
            using (var plan = await new FolderPlanBuilder(Context()).BuildAsync(
                Row.ForOneSide(Side.Dropbox, saved.LocalKey, remote, saved, [], null, RowContext.Neutral),
                Side.Dropbox, new HashSet<string>(), live: true, default))
                id = fixture.Inner.BeginFolderRemoval(PairId, plan, fixture.Settings.StoragePageSize, default);
            await jobs.Items.Folders.RunPreparedFolderRemovalsAsync([id], default);
            var stopped = false;
            var submissions = 0;
            FolderSubmission? accepted = null;
            IReadOnlyList<FolderRemovalEffect>? acceptedEffects = null;
            if (phase == "accepted")
            {
                fixture.DropboxView = new HookedSide(fixture.Dropbox,
                    submit: (submission, effects, _, _) =>
                    {
                        accepted = submission;
                        acceptedEffects = effects;
                        return Task.FromResult<FolderSubmitResult>(new FolderSubmitResult.Accepted("child-job"));
                    },
                    receipt: async (_, _, attempt, token) =>
                    {
                        if (!stopped) { stopped = true; throw new OperationCanceledException(); }
                        var terminal = (FolderSubmitResult.Terminal)await fixture.Dropbox.SubmitFolderRemovalAsync(
                            accepted!, acceptedEffects!, attempt, token);
                        return new FolderJobResult.Terminal(terminal.Results);
                    });
                jobs = Jobs();
                jobs.Ancestors.RunSelection(root);
            }
            fixture.Hooks.BeforeCall = method =>
            {
                if (method.Name == nameof(IStateStore.ArmFolderSubmission)) submissions++;
                if (phase == "accepted" || stopped || (afterEffect ? method.Name != nameof(IStateStore.CompleteFolderFollowUp) :
                    method.Name != nameof(IStateStore.ReadyFolderEffects) ||
                    fixture.Inner.ActiveFolderRemovals(PairId, default).Count() != 2)) return;
                stopped = true;
                throw new OperationCanceledException();
            };
            await ThrowsAsync<OperationCanceledException>(() => jobs.Items.Folders.FinishFolderRemovalAsync(id!, root, default));
            Check.Require(stopped && (await fixture.Dropbox.ReadOneAsync(child.Path, default) is null) == afterEffect,
                "interruption occurs after child freeze, accepted receipt, or completed effect, before parent settlement: " + phase);
            if (phase == "accepted")
                Check.Require(fixture.Inner.PendingFolderSubmissions(PairId, default).Count() == 1,
                    "the interrupted child retains one accepted durable receipt");
            fixture.RestartStore();
            jobs = Jobs();
            jobs.Ancestors.RunSelection(root);
            await jobs.Items.Folders.FinishFolderRemovalAsync(id!, root, default);
            Check.Require(!fixture.Inner.ActiveFolderRemovals(PairId, default).Any() &&
                await fixture.Dropbox.ReadOneAsync(child.Path, default) is null && submissions == 1,
                "restart resumes the frozen child or observes its completion without replaying its single submission");
            fixture.Hooks.BeforeCall = null;
        }
    }

    private static async Task FolderSurvivorCycleAsync()
    {
        var previous = new Dictionary<(bool Mapped, int Survivors, int BlockerRows), Dictionary<string, long>>();
        foreach (var mapped in new[] { false, true })
        foreach (var survivorCount in new[] { 1, 3 })
        foreach (var blockerRows in new[] { 1, 3 })
        // Vary the involved survivors and blocker rows separately from the unrelated saved population.
        foreach (var background in new[] { 5_110, 20_440 })
        {
            using var fixture = new Fixture(realStore: true);
            ClearObligations(fixture);
            fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Record = Enumerable.Range(0, background).Select(index => SyncedEntries.From(
                    LocalEntry("Outside/Item" + index, "outside-local-" + index),
                    RemoteEntry("Outside/Item" + index, "outside-remote-" + index))).ToArray()
            });
            var remoteRoot = mapped ? "Remote" : "Parent";
            var localFolder = FolderEntry("Parent", "local-parent");
            var remoteFolder = FolderEntry(remoteRoot, "remote-parent");
            var localBranch = FolderEntry("Parent/Branch", "local-branch");
            var remoteBranch = FolderEntry(remoteRoot + "/Branch", "remote-branch");
            var localLeaf = LocalEntry("Parent/Branch/Leaf", "local-leaf");
            var remoteLeaf = RemoteEntry(remoteRoot + "/Branch/Leaf", "remote-leaf");
            fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Record = [SyncedEntries.From(localFolder, remoteFolder), SyncedEntries.From(localBranch, remoteBranch)]
            });
            fixture.Dropbox.Seed(remoteFolder, null);
            fixture.Dropbox.Seed(remoteBranch, null);
            for (var index = 0; index < survivorCount; index++)
            {
                var suffix = index == 0 ? "" : index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var local = localLeaf with { Path = localLeaf.Path + suffix, Id = localLeaf.Id + suffix };
                var remote = remoteLeaf with { Path = remoteLeaf.Path + suffix, Id = remoteLeaf.Id + suffix };
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
                fixture.Dropbox.Seed(remote with { Size = 2, Revision = "rev-2",
                    ContentHash = DropboxContentHash.Compute(new byte[] { 2, 2 }) }, [2, 2]);
            }
            var root = Seed(fixture, Deleting(localFolder.Path, localFolder.Id));
            var branch = Seed(fixture, Deleting(localBranch.Path, localBranch.Id), 2);
            for (var index = 1; index < blockerRows; index++)
            {
                var added = Seed(fixture, Deleting(localBranch.Path, localBranch.Id), 2 + index);
                branch = branch with { ThroughWorkId = added.ThroughWorkId };
            }
            Check.Require(fixture.Inner.ReadWork(branch).Count() == blockerRows,
                "the dependency selection carries the requested number of work rows");
            var events = new List<RunEvent>();
            var boundary = new Boundary(_ => new Lease { AllowsRelease = true });
            await LiveSyncer(fixture, root, boundary, events).SyncWorkAsync(default);
            var issues = fixture.Inner.Issues(PairId).Where(value => value.Cause == TextKey.FailFolderDependencyCycle).ToArray();
            var issue = issues.Single(value => value.Path == remoteLeaf.Path);
            Check.Require(issues.Length == survivorCount, "each refused survivor has its own cycle Failure");
            var operation = fixture.Inner.ActiveFolderRemovals(PairId, default).Single();
            var members = fixture.Inner.PendingFolderFollowUps(PairId, operation.OperationId, -1, int.MaxValue).ToArray();
            Check.Require(issue.Path == remoteLeaf.Path && members.Any(item => item.Path == remoteLeaf.Path) &&
                !fixture.Inner.HeldCovers(PairId, PathNames.Key(localLeaf.Path), null) &&
                PendingAt(fixture, Side.Local, localFolder.Path, localBranch.Path).Count == 2,
                "without a future event the actual selection cycle is visible, non-holding, and retains both work and survivor");
            var pendingWork = fixture.Inner.ReadWork(root).Count() + fixture.Inner.ReadWork(branch).Count();
            fixture.RestartStore();
            for (var attempt = 0; attempt < 2; attempt++)
                await LiveSyncer(fixture, root, boundary, events).SyncWorkAsync(default);
            Check.Require(fixture.Inner.Issues(PairId).Where(value => value.Cause == TextKey.FailFolderDependencyCycle)
                    .Select(value => value.Id).Order().SequenceEqual(issues.Select(value => value.Id).Order()) &&
                fixture.Inner.ActiveFolderRemovals(PairId, default).Single().OperationId == operation.OperationId &&
                fixture.Inner.ReadWork(root).Count() + fixture.Inner.ReadWork(branch).Count() == pendingWork &&
                fixture.Inner.PendingFolderFollowUps(PairId, operation.OperationId, -1, int.MaxValue).Count() == members.Length,
                "restart and repeated opportunities retain one issue and operation without residual or member growth");
            var calls = new CountingSide(fixture.Dropbox);
            var localCalls = new CountingSide(fixture.Local);
            var storeCalls = new StoreCounters(fixture.Store);
            fixture.DropboxView = calls;
            var context = fixture.Context(root) with
            {
                Store = storeCalls, Local = localCalls,
                CaptureFolderCursorAsync = _ => Task.FromResult(Cursor),
                ReadFolderChangesAsync = (_, ct) => NoFolderChangesAsync(ct)
            };
            var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(), boundary, RefusingExecutionBoundary.Instance, 0));
            fixture.Inner.Commit(PairId, StateChange.Empty with
                { Close = issues.Select(value => (value.Path, IssueClosure.AfterEffect)).ToArray() });
            await jobs.Items.Folders.FinishFolderRemovalAsync(operation.OperationId, null, default);
            Check.Require(!fixture.Inner.Issues(PairId).Any(value => value.Cause == TextKey.FailFolderDependencyCycle) &&
                fixture.Inner.PendingFolderFollowUps(PairId, operation.OperationId, -1, int.MaxValue).Any(),
                "null-selection continuation retains the survivor without inventing a cycle");
            var unrelated = Seed(fixture, Deleting("Other", "other-folder"), blockerRows + 2);
            jobs.Ancestors.RunSelection(unrelated);
            try { await jobs.Items.Folders.FinishFolderRemovalAsync(operation.OperationId, unrelated, default); }
            finally { jobs.Ancestors.RunSelection(null); }
            Check.Require(!fixture.Inner.Issues(PairId).Any(value => value.Cause == TextKey.FailFolderDependencyCycle),
                "an unrelated running selection is not the owner of the folder dependency cycle");
            jobs.Ancestors.RunSelection(root);
            storeCalls.Reset();
            var visited = new List<long>();
            fixture.Hooks.OnReadWork = selected => visited.Add(selected.AnchorWorkId);
            try { await jobs.Items.Folders.FinishFolderRemovalAsync(operation.OperationId, root, default); }
            finally { jobs.Ancestors.RunSelection(null); fixture.Hooks.OnReadWork = null; }
            Check.Require(calls.Reads + localCalls.Reads == 0 && calls.Listings + localCalls.Listings == 0,
                "saved-path refusal avoids survivor provider reads before recovery admission");
            var counts = storeCalls.MemberCalls.ToDictionary(call => call.Key, call => call.Value);
            var survivors = members.Count(member => member.Expected is { IsFolder: false });
            var walks = counts.GetValueOrDefault(nameof(IStateStore.ReadWork));
            var distinctAnchors = visited.Distinct().Count();
            Check.Require(survivors == survivorCount && distinctAnchors == 1 && visited.All(anchor => anchor == branch.AnchorWorkId) &&
                walks == 2 && walks <= 2 * distinctAnchors &&
                counts.GetValueOrDefault(nameof(IStateStore.PendingAncestors)) == survivors + 1 + 4 * blockerRows &&
                counts.GetValueOrDefault(nameof(IStateStore.FolderRemovalItemByKey)) == survivors,
                "one memoized and one fresh walk per blocker are independent of survivor count; each visits every reachable anchor once and probes every blocker row");
            var shape = (mapped, survivorCount, blockerRows);
            if (previous.TryGetValue(shape, out var earlier))
                Check.Require(counts.Count == earlier.Count && counts.All(call => earlier.GetValueOrDefault(call.Key) == call.Value),
                    "every store-query count per survivor is independent of unrelated background pair size");
            previous[shape] = counts;
            Console.WriteLine($"B74 counts background={background} mapped={mapped} survivors={survivors} blockerRows={blockerRows} distinctAnchors={distinctAnchors} providerReads=0 walks={walks} " +
                string.Join(",", counts.OrderBy(call => call.Key).Select(call => call.Key + "=" + call.Value)));
            if (blockerRows != 1) continue;
            if (!mapped)
            {
                fixture.Inner.Commit(PairId, StateChange.Empty with
                    { Close = issues.Select(value => (value.Path, IssueClosure.AfterEffect)).ToArray() });
                var raced = false;
                fixture.Hooks.BeforeCall = method =>
                {
                    if (method.Name != nameof(IStateStore.FolderRemovalItemByKey)) return;
                    fixture.Hooks.BeforeCall = null;
                    raced = fixture.Inner.CompleteWork(branch, null);
                };
                jobs.Ancestors.RunSelection(root);
                try { await jobs.Items.Folders.FinishFolderRemovalAsync(operation.OperationId, root, default); }
                finally { jobs.Ancestors.RunSelection(null); fixture.Hooks.BeforeCall = null; }
                Check.Require(raced && fixture.Inner.Issues(PairId).All(value => value.Cause != TextKey.FailFolderDependencyCycle),
                    "a blocker retired after group diagnosis but before the fresh per-anchor recheck creates no stale cycle Failure");
                Seed(fixture, Deleting(localFolder.Path, localFolder.Id), 4);
                jobs.Ancestors.RunSelection(root);
                try { await jobs.Items.Folders.FinishFolderRemovalAsync(operation.OperationId, root, default); }
                finally { jobs.Ancestors.RunSelection(null); }
                Check.Require(fixture.Inner.Issues(PairId).All(value => value.Cause != TextKey.FailFolderDependencyCycle),
                    "later work collapsed into the own root is an ordinary refusal, never a cycle");
            }
            jobs.Ancestors.CoverListedWork(fixture.Inner.ReadWorkWatermark());
            await jobs.Items.Folders.FinishFolderRemovalAsync(operation.OperationId, null, default);
            Check.Require(!fixture.Inner.FolderRemoval(PairId, operation.OperationId).Active &&
                fixture.Inner.Issues(PairId).All(value => value.Cause != TextKey.FailFolderDependencyCycle) &&
                await fixture.Local.ReadOneAsync(localLeaf.Path, default) is not null,
                "fresh Repair coverage reconciles the survivor and closes the deleting-side cycle issue");
        }
    }

    private static async Task FolderDeletionCoversItsChildrenAsync(bool refused = false)
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        var remoteFolder = FolderEntry(Folder, DropboxId);
        fixture.Dropbox.Seed(remoteFolder, null);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        { Record = [SyncedEntries.From(FolderEntry(Folder, FolderId), remoteFolder)] });
        var edited = Folder + "/edited";
        var unchanged = Folder + "/unchanged";
        var editedRemote = RemoteEntry(edited, "remote-edited");
        fixture.Inner.Commit(PairId, StateChange.Empty with
        { Record = [SyncedEntries.From(LocalEntry(edited, "local-edited"), editedRemote)] });
        var editedNow = editedRemote with
        {
            Size = 2, Modified = Stamp.AddHours(1),
            ContentHash = DropboxContentHash.Compute(new byte[] { 9, 9 })
        };
        fixture.Dropbox.Seed(editedNow, [9, 9]);
        var siblingRemote = RemoteEntry(unchanged, "remote-sibling");
        fixture.Inner.Commit(PairId, StateChange.Empty with
        { Record = [SyncedEntries.From(LocalEntry(unchanged, "local-sibling"), siblingRemote)] });
        fixture.Dropbox.Seed(siblingRemote, [1]);
        if (refused) fixture.DropboxView = new HookedSide(fixture.Dropbox, refuseRemoval: true);
        // Only the folder and the edited child have work: the unchanged sibling has no row of its own.
        var child = SeedRemote(fixture, editedNow);
        Seed(fixture, Deleting(Folder, FolderId), 1);
        var events = new List<RunEvent>();
        var boundary = new Boundary(request =>
        {
            fixture.Inner.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint);
            return new Lease { AllowsRelease = true };
        });
        await LiveSyncer(fixture, child, boundary, events).SyncWorkAsync(default);
        if (refused)
        {
            Check.Require(PendingAt(fixture, Side.Local, Folder).Any(), "refused member retains folder work");
            var notices = fixture.Inner.Issues(PairId, [IssueKind.Failure]).ToArray();
            Check.Require(notices.Length == 1, "refused member raises one notice; measured=" + notices.Length);
            Check.Require(await fixture.Dropbox.ReadOneAsync(unchanged, default) is not null,
                "refused member leaves unchanged bytes untouched before retry");
            fixture.DropboxView = null;
            fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(Folder, new[] { IssueKind.Failure })] });
            await LiveSyncer(fixture, child, boundary, events).SyncWorkAsync(default);
        }
        Check.Require(await fixture.Dropbox.ReadOneAsync(edited, default) is not null &&
            await fixture.Local.ReadOneAsync(edited, default) is not null,
            "A109: the edited child survives on both sides");
        Check.Require(await fixture.Dropbox.ReadOneAsync(unchanged, default) is null,
            "A109: the unchanged sibling with no row of its own is deleted on Dropbox");
        Check.Require(!PendingAt(fixture, Side.Local, Folder).Any(),
            "A109: the folder selection retires; failures=" + string.Join(" | ", events
                .Where(item => item.Error is not null).Select(item => item.Path + ":" + item.Error + ":" +
                    (item.Error as LiveAdmissionStaleException)?.Detail)));
        events.Clear();
        await LiveSyncer(fixture, child, boundary, events).SyncWorkAsync(default);
        Check.Require(events.All(item => item.Error is not LiveAdmissionStaleException),
            "A109: the next pass has zero stale refusals");
        Check.Require(events.All(item => item.EffectKind is null) &&
            await fixture.Dropbox.ReadOneAsync(unchanged, default) is null &&
            await fixture.Dropbox.ReadOneAsync(edited, default) is not null,
            "A109: nothing comes back and nothing is restored on the next pass");
    }

    // #108 finding 1: the pre-run uses the fence's whole dependency key set, so a mapped counterpart ancestor and a
    // two-level chain of blockers run before the item instead of refusing it forever.
    private static async Task MappedAncestorFirstAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        fixture.Local.Seed(FolderEntry("A", "local-a"), null);
        fixture.Dropbox.Seed(FolderEntry("B", "remote-b"), null);
        // The folder mapping Local A <-> Dropbox B is saved, so the child's destination maps through it.
        fixture.Inner.Commit(PairId, StateChange.Empty with
        { Record = [SyncedEntries.From(FolderEntry("A", "local-a"), FolderEntry("B", "remote-b"))] });
        var local = LocalEntry("A/C", "local-c");
        fixture.Local.Seed(local, [1]);
        var mapped = new SyncedEntry(PathNames.Key("A/C"), "A/C", PathNames.DropboxKey("B/C"), "B/C", false,
            local.Id, 1, Stamp.UtcTicks, "remote-c", DropboxContentHash.Compute(new byte[] { 1 }));
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [mapped] });
        var child = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, "A/C", null, local.Id,
            local, false, false));
        SeedRemote(fixture, FolderEntry("B", "remote-b"));
        var localCalls = new CountingSide(fixture.Local);
        var remoteCalls = new CountingSide(fixture.Dropbox);
        fixture.LocalView = localCalls;
        fixture.DropboxView = remoteCalls;
        await ThrowsAsync<LiveAdmissionStaleException>(() => RunSelectionAsync(fixture, child));
        Check.Require(localCalls.Reads + remoteCalls.Reads + localCalls.Listings + remoteCalls.Listings +
            localCalls.Work + remoteCalls.Work == 0, "A108: mapped refusal makes zero provider calls");
        fixture.Inner.EndWorkPass(PairId);
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, child, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        var refused = events.Select(item => item.Error).OfType<LiveAdmissionStaleException>().FirstOrDefault();
        Check.Require(refused is null, "A108a: the mapped counterpart ancestor is run, not refused; " +
            refused?.Detail + "; trail=" + string.Join(" | ", events.Where(item => item.Path.Length > 0)
                .Select(item => item.Kind + ":" + item.Path + ":" + item.EffectKind)));
        var ancestorAt = events.FindIndex(item => item.Path == "B");
        var childAt = events.FindIndex(item => item.Path == "A/C");
        Check.Require(ancestorAt >= 0 && childAt > ancestorAt, "A108a: the mapped ancestor runs before its child");
        Check.Require(await fixture.Dropbox.ReadOneAsync("B", default) is not null,
            "A108a: the mapped ancestor was executed, not only observed");
    }

    private static async Task DeletedDestinationFolderAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        var old = LocalEntry("Old", LocalId);
        var remote = RemoteEntry("Old", DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(old, remote)] });
        fixture.Local.Seed(FolderEntry("Target", FolderId), null);
        var moved = old with { Path = "Target/New" };
        fixture.Local.Seed(moved, [1]);
        fixture.Dropbox.Seed(remote, [1]);
        var folder = SeedRemote(fixture, FolderEntry("Target", "deleted-folder"));
        var source = fixture.Inner.ReadSource(PairId, Side.Dropbox)!.Value;
        var through = fixture.Inner.AcquireDropboxPage(PairId, Generation, source.Epoch, source.Revision,
            source.Provenance!, source.Cursor,
            [new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, "Target", null, "deleted-folder", null, false, true)], Cursor);
        folder = folder with { ThroughWorkId = through };
        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, moved.Path, "Old", moved.Id,
            moved, false, false));
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, folder, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        Check.Require(await fixture.Dropbox.ReadOneAsync("Target/New", default) is not null &&
            await fixture.Dropbox.ReadOneAsync("Old", default) is null &&
            await fixture.Local.ReadOneAsync("Target/New", default) is not null &&
            !PendingAt(fixture, Side.Dropbox, "Target").Any() &&
            events.All(item => item.Error is not LiveAdmissionStaleException),
            "CL03: folder selection drains before the incoming rename, preserving final contents");
    }

    private static async Task OldPathAncestorFirstAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        var entry = LocalEntry("New/Child", LocalId);
        fixture.Local.Seed(FolderEntry("New", FolderId), null);
        fixture.Local.Seed(entry, [1]);
        var child = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, entry.Path,
            "Old/Child", entry.Id, entry, false, false));
        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, "Earlier", null,
            "old-folder", FolderEntry("Earlier", "old-folder"), false, false), 2);
        // A later identity member is filtered by SelectWork; the exact old-path probe resolves its owner.
        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, "Old", null,
            "old-folder", FolderEntry("Old", "old-folder"), false, false), 3);
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, child, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        Check.Require(!PendingAt(fixture, Side.Local, "Old", "Earlier").Any() &&
            events.FindIndex(item => item.Path == "Earlier") >= 0 &&
            events.FindIndex(item => item.Path == "Earlier") < events.FindIndex(item => item.Path == "New/Child") &&
            events.All(item => item.Error is not LiveAdmissionStaleException),
            "A108: old-path owner retires before the renamed child without stale refusal");
    }

    private static async Task DependencyReadCountAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        const string path = "A/B/C/D/E/F/File";
        var entry = LocalEntry(path, LocalId);
        fixture.Local.Seed(entry, [1]);
        var selected = Selected(fixture, path, entry);
        var context = fixture.Context(selected);
        var fence = new AncestorFence(context);
        var before = fixture.Hooks.Count(nameof(IStateStore.ReadWork));
        Check.Require(fence.PendingAncestor(selected, new Parents(context)) is null,
            "depth-six fixture has no pending ancestor");
        var reads = fixture.Hooks.Count(nameof(IStateStore.ReadWork)) - before;
        Console.WriteLine($"ancestor-prerun depth=6 selections=1 ReadWork={reads}");
        Check.Require(reads == 1, "depth-six pre-run reads its selection once; measured=" + reads);
        await Task.CompletedTask;
    }

    private static async Task NestedAncestorOrderAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        fixture.Local.Seed(FolderEntry("A", "local-a"), null);
        fixture.Dropbox.Seed(FolderEntry("A", "remote-a"), null);
        fixture.Dropbox.Seed(FolderEntry("A/B", "remote-ab"), null);
        var local = LocalEntry("A/B/C", "local-c");
        fixture.Local.Seed(local, [1]);
        var child = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, "A/B/C", null, local.Id,
            local, false, false));
        SeedRemote(fixture, FolderEntry("A/B", "remote-ab"));
        SeedRemote(fixture, FolderEntry("A", "remote-a"));
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, child, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        var nestedRefused = events.Select(item => item.Error).OfType<LiveAdmissionStaleException>().FirstOrDefault();
        Check.Require(nestedRefused is null, "A108a: both nested ancestors are run, not refused; " +
            nestedRefused?.Detail + "; trail=" + string.Join(" | ", events.Where(item => item.Path.Length > 0)
                .Select(item => item.Kind + ":" + item.Path + ":" + item.EffectKind)));
        var outer = events.FindIndex(item => item.Path == "A");
        var inner = events.FindIndex(item => item.Path == "A/B");
        var leaf = events.FindIndex(item => item.Path == "A/B/C");
        Check.Require(outer >= 0 && inner > outer && leaf > inner,
            "A108a: nested ancestors resolve outside-in before the child");
        Check.Require(await fixture.Dropbox.ReadOneAsync("A/B/C", default) is not null,
            "A108a: the nested child syncs");
    }

    private static ChangeNotice Deleting(string path, string id) =>
        new(Side.Local, ChangeKind.Deleted, path, null, id, null, false, true);

    private static Syncer LiveSyncer(Fixture fixture, WorkSelection selection, Boundary boundary,
        List<RunEvent> events, FakeRecovery? recovery = null)
    {
        boundary.Capture = (chosen, _, _) => Task.FromResult(Coverage(chosen));
        var context = fixture.Context(selection) with
        {
            Events = events.Add,
            // A live folder removal records its own Dropbox window before the first arm.
            CaptureFolderCursorAsync = _ => Task.FromResult(Cursor),
            ReadFolderChangesAsync = (_, ct) => NoFolderChangesAsync(ct)
        };
        if (recovery is not null) context = context with { Recovery = recovery };
        var ports = new SyncPorts(new RecoveryBoundary(), boundary, RefusingExecutionBoundary.Instance, 0);
        return new Syncer(context, ports with
        {
            RunBatchAsync = (work, side, scope, retries, settles, ct) =>
                SyncJobs.RunBatchAsync(context, ports, work, scope, ct, side, retries, settles)
        });
    }

    private static async IAsyncEnumerable<Listed> NoFolderChangesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    private static Fixture UnreadableFixture(bool withMemberFolder = true)
    {
        var fixture = new Fixture(realStore: true);
        fixture.Local.Seed(FolderEntry(Folder, FolderId), null);
        fixture.Local.Seed(LocalEntry(Plain, LocalId), [1]);
        if (withMemberFolder) fixture.Local.Seed(FolderEntry(Member, MemberId), null);
        return fixture;
    }

    private static async Task<bool> RunNoticeAsync(Fixture fixture, string path, Entry entry, CancellationToken ct = default,
        long page = 1)
    {
        return await RunSelectionAsync(fixture, Selected(fixture, path, entry, page), ct);
    }

    private static WorkSelection Selected(Fixture fixture, string path, Entry entry, long page = 1)
    {
        return Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, path, null, entry.Id, entry, false, false), page);
    }

    private static async Task<bool> RunSelectionAsync(Fixture fixture, WorkSelection selection, CancellationToken ct = default) =>
        (await Runner(fixture, selection, new Boundary(_ => new Lease()),
            (_, _, _) => Task.FromResult(Coverage(selection))).RunAsync(selection, ct))
            ?? throw new InvalidOperationException("This fixture requires selection admission.");

    private static async Task FolderSurvivorFailureIdentityAsync()
    {
        foreach (var deletedSide in new[] { Side.Dropbox, Side.Local })
        {
            using var fixture = new Fixture(realStore: true);
            SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
            var localFolder = FolderEntry(Folder, FolderId);
            var remoteFolder = FolderEntry(Folder, DropboxId);
            var child = LocalEntry(Plain, LocalId);
            var remoteChild = RemoteEntry(Plain, "remote-child");
            fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Record = [SyncedEntries.From(localFolder, remoteFolder), SyncedEntries.From(child, remoteChild)]
            });
            var survivorSide = deletedSide == Side.Dropbox ? fixture.Local : fixture.Dropbox;
            survivorSide.Seed(deletedSide == Side.Dropbox ? localFolder : remoteFolder, null);
            survivorSide.Seed((deletedSide == Side.Dropbox ? child : remoteChild) with
                { Size = 2, ContentHash = DropboxContentHash.Compute(new byte[] { 2, 2 }) }, [2, 2]);
            var failedDestination = new ProxySide(deletedSide == Side.Dropbox ? fixture.Dropbox : fixture.Local)
            {
                BlockPublish = (_, _) => throw new ItemFailedException(Plain, TextKey.FailChanged, null)
            };
            if (deletedSide == Side.Dropbox) fixture.DropboxView = failedDestination;
            else fixture.LocalView = failedDestination;
            var selection = deletedSide == Side.Dropbox ? SeedRemote(fixture, remoteFolder) :
                Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Deleted, Folder, null, FolderId, null, false, true), page: 2);
            await RunSelectionAsync(fixture, selection);
            var last = fixture.Inner.ReadWorkWatermark();
            Check.Require(last > selection.ThroughWorkId, "F1: failed survivor actually registers new durable work");
            var residual = selection with { Identity = null, Key = PathNames.KeyFor(selection.Side, Plain),
                AnchorWorkId = last, ThroughWorkId = last };
            var work = fixture.Inner.ReadWork(residual).Single();
            Check.Require(work.Notice.Path == Plain && work.Notice.Side == selection.Side &&
                work.Notice.Id is null && work.Notice.Observed is null && work.Notice.Kind == ChangeKind.Changed && !work.Notice.IdentityDeleted,
                "F1/M4: survivor is reobserved at its counterpart path on the parent's source, never with the folder identity");
        }
    }

    private static async Task OrdinaryWalkFailureAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            using var fixture = new Fixture(realStore: true);
            var folder = FolderEntry(Folder, FolderId);
            var endpoint = side == Side.Local ? fixture.Local : fixture.Dropbox;
            var child = side == Side.Local ? LocalEntry(Plain, LocalId) : RemoteEntry(Plain, DropboxId);
            var cause = side == Side.Local ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed;
            endpoint.Seed(folder, null);
            endpoint.Seed(child, [1]);
            endpoint.FailWalkAfter(Plain, new InventoryIncompleteException("fixture", new ItemFailedException(Folder, cause, null)));
            var selection = side == Side.Local
                ? Seed(fixture, new ChangeNotice(side, ChangeKind.Changed, Folder, null, FolderId, folder, false, false))
                : SeedRemote(fixture, folder);
            await RunSelectionAsync(fixture, selection);
            Check.Require(fixture.Inner.Issues(PairId).Any(issue => issue.Path == Folder && issue.Cause == cause) &&
                fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) > 0,
                "H5: ordinary walk failure retains exact provider cause and work instead of stopping the host: " + side);
        }
    }

    private static async Task WrappedWalkFailureAsync()
    {
        foreach (var peer in new[] { false, true })
        {
            using var fixture = UnreadableFixture();
            var folder = FolderEntry(Folder, FolderId);
            if (peer)
            {
                var remote = FolderEntry(Folder, DropboxId);
                fixture.Dropbox.Seed(remote, null);
                SeedRemote(fixture, remote);
            }
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            var failure = new InventoryIncompleteException("fixture",
                new ItemFailedException(Folder, TextKey.MoveFailed, null));
            if (peer)
                fixture.LocalView = new ProxySide(fixture.Local) { ListingGate = () => throw failure };
            else fixture.Local.FailWalkAfter(Plain, failure);
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(Touches(fixture, Member) && fixture.Inner.Issues(PairId).Any(issue =>
                issue.Path == Folder && issue.Cause == TextKey.MoveFailed),
                "AJ-05: retained enumeration failure preserves the typed provider cause and unreadable protection.");
            Check.Require(fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) > 0,
                "RP-01: failed walk retains its Failure even when opposite-side work owns the path");
        }
    }

    private static bool Touches(Fixture fixture, string path) =>
        fixture.Inner.UnreadableTouches(PairId, PathNames.Key(path), false);

    private static async Task<bool> SyncedAsync(Fixture fixture, string path) =>
        await fixture.Dropbox.ReadOneAsync(path, default) is not null;

    // Rev3b section 5 and rev3d: release through production selection and store, doubles truly walking.
    private static async Task UnreadableReleaseAsync()
    {
        var rootPath = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(rootPath, Member));
            await File.WriteAllTextAsync(Path.Combine(rootPath, Plain), "content");
            using var fixture = new Fixture(realStore: true, localRoot: rootPath);
            var rootId = WindowsDirectoryListing.List(rootPath).Single().FileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var selection = Selected(fixture, Folder, FolderEntry(Folder, rootId));
            var local = new Massdex.SyncShark.Engine.Local.LocalSide(fixture.Pair,
                new PathScope(fixture.Pair, [fixture.Pair]), fixture.Context(selection).Settings,
                new PendingNotes(fixture.Inner, fixture.Pair.PairId, 100, mutation: false),
                rootPath, (_, _, _, _) => throw new NotSupportedException());
            var member = (await local.ReadOneAsync(Member, default))!;
            UnreadableMember.Record(fixture.Inner, PairId, Member, member.Id);
            fixture.LocalView = new HookedSide(local, failDisposal: true);
            await RunSelectionAsync(fixture, selection);
            Check.Require(Touches(fixture, Member) && await SyncedAsync(fixture, Plain) &&
                fixture.Inner.Issues(PairId).Any(issue => issue is { Kind: IssueKind.Failure, Path: Folder,
                    Cause: TextKey.ReadLocalFailed }),
                "Real Local acquisition followed by outer disposal failure retains the member and ordinary sibling progress.");
        }
        finally { Directory.Delete(rootPath, true); }
        var folder = FolderEntry(Folder, FolderId);
        // One and two recorded keys expose repeated scans; extra unmatched directories hold r fixed while d grows.
        foreach (var recorded in new[] { 1, 2 })
        foreach (var misses in new[] { 1, 2 })
        {
            using var fixture = UnreadableFixture(withMemberFolder: false);
            for (var index = 0; index < recorded + misses; index++)
            {
                var path = Folder + "/directory" + index;
                fixture.Local.Seed(FolderEntry(path, "directory" + index), null);
                if (index < recorded) UnreadableMember.Record(fixture.Inner, PairId, path, "unmatched identity");
            }
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(fixture.Hooks.RecordedKeyVisits == recorded,
                $"Recorded membership is traversed once, not per callback: r={recorded}, misses={misses}, visits={fixture.Hooks.RecordedKeyVisits}.");
        }
        await fixtureCase(withMember: false, async fixture =>
        {
            // A47, A93: F recorded and enumerated: its member goes after the walk, children sync in the continuation.
            UnreadableMember.Record(fixture.Inner, PairId, Folder, FolderId);
            Check.Require(await RunNoticeAsync(fixture, Folder, folder) && !Touches(fixture, Folder) &&
                await SyncedAsync(fixture, Plain) && fixture.Inner.Issues(PairId).Count == 0, "A47 release and sync");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A95: U protected, a ordinary: a syncs, U goes only on proof.
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            Check.Require(await RunNoticeAsync(fixture, Folder, folder) && !Touches(fixture, Member) &&
                await SyncedAsync(fixture, Plain), "A95 ordinary sibling and proved member");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A56: enumeration fails after a: member stays, a's completion stands, Failure on the walked folder.
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            fixture.Local.FailWalkAfter(Plain, new InventoryIncompleteException("fixture", null));
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(Touches(fixture, Member) && await SyncedAsync(fixture, Plain) &&
                fixture.Inner.Issues(PairId).Any(issue => issue is { Kind: IssueKind.Failure, Path: Folder,
                    Cause: TextKey.ReadLocalFailed }), "A56 failed walk releases nothing");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            fixture.LocalView = new HookedSide(fixture.Local, failDisposal: true);
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(Touches(fixture, Member) && await SyncedAsync(fixture, Plain) &&
                fixture.Inner.Issues(PairId).Any(issue => issue is { Kind: IssueKind.Failure, Path: Folder,
                    Cause: TextKey.ReadLocalFailed }), "Outer disposal failure retains protection and records the walked path.");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A57: cancellation stays cancellation and releases nothing.
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            var canceled = new OperationCanceledException();
            fixture.Local.FailWalkAfter(Plain, canceled);
            await ThrowsAsync<OperationCanceledException>(() => RunNoticeAsync(fixture, Folder, folder));
            Check.Require(Touches(fixture, Member), "A57 cancellation releases nothing");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A94: a link at the recorded path is not descended, so U stays while F's walk succeeds.
            fixture.Local.Seed(FolderEntry(Member, MemberId) with { Skip = nameof(TextKey.SkipLink) }, null);
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(Touches(fixture, Member), "A94 an unopened directory proves nothing");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A96: root and descendant proved: both go; the folder batch's one scoped listing covers both, no continuation.
            var counted = new CountingSide(fixture.Local);
            fixture.LocalView = counted;
            UnreadableMember.Record(fixture.Inner, PairId, Folder, FolderId);
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(!Touches(fixture, Folder) && !Touches(fixture, Member) && counted.Listings == 1,
                $"A96, A124: one batch listing, not one per member ({counted.Listings})");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A97: F proved under a still recorded P: F's member goes, its rows stay covered by P.
            fixture.Local.Seed(LocalEntry(Member + "/file", OccupantId), [1]);
            UnreadableMember.Record(fixture.Inner, PairId, Folder, FolderId);
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            await RunNoticeAsync(fixture, Member, FolderEntry(Member, MemberId));
            Check.Require(Touches(fixture, Folder) &&
                !fixture.Inner.UnreadableUnder(PairId, PathNames.Key(Folder)).Contains(PathNames.Key(Member)) &&
                !await SyncedAsync(fixture, Member + "/file"), "A97 an ancestor member keeps protecting");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A99, A124: a different item at the key: no release, no continuation, the row stays protected.
            var counted = new CountingSide(fixture.Local);
            fixture.LocalView = counted;
            UnreadableMember.Record(fixture.Inner, PairId, Member, "different");
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(Touches(fixture, Member) && counted.Listings == 1, "A99 a refused candidate has no continuation");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A48, A105, A106: a file notice under a member proves nothing: no effect, settled, notice stays, no growth.
            fixture.Local.Seed(LocalEntry(Member + "/file", OccupantId), [1]);
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            foreach (var page in new long[] { 1, 2 })
                Check.Require(await RunNoticeAsync(fixture, Member + "/file", LocalEntry(Member + "/file", OccupantId), page: page) &&
                    Touches(fixture, Member) && !await SyncedAsync(fixture, Member + "/file") &&
                    fixture.Inner.Issues(PairId).Single().Kind == IssueKind.Skipped, "A48 no release from a file");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A11: release proves access only from a Local notice; a Dropbox notice at the same folder releases nothing.
            UnreadableMember.Record(fixture.Inner, PairId, Folder, FolderId);
            var seeded = FolderEntry(Folder, FolderId);
            fixture.Dropbox.Seed(seeded, null);
            await RunSelectionAsync(fixture, SeedRemote(fixture, seeded));
            Check.Require(Touches(fixture, Folder), "A11 a Dropbox notice releases no recorded Local member");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A102: the released root is re-listed, while its link member is never opened and stays recorded; a later
            // dirty arrival under that member is then retained by the point-of-use exact-membership ask.
            fixture.Local.Seed(FolderEntry(Member, MemberId) with { Skip = nameof(TextKey.SkipLink) }, null);
            UnreadableMember.Record(fixture.Inner, PairId, Folder, FolderId);
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            fixture.Local.Seed(LocalEntry(Member + "/dirty", OccupantId), [1]);
            Check.Require(await RunNoticeAsync(fixture, Folder, folder) && !Touches(fixture, Folder) &&
                Touches(fixture, Member), "A102 the continuation retains the unopened member");
            await RunNoticeAsync(fixture, Member + "/dirty", LocalEntry(Member + "/dirty", OccupantId), page: 2);
            Check.Require(Touches(fixture, Member) && !await SyncedAsync(fixture, Member + "/dirty"),
                "A102 a later dirty arrival under a recorded member is retained by exact membership");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A104: the folder batch's one listing fails under the member: as a failed walk it releases nothing, the
            // folder keeps one Failure, and the ordinary sibling still syncs (there is no continuation listing).
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            fixture.Local.Seed(LocalEntry(Member + "/file", OccupantId), [1]);
            // The listing ends after the last entry, so every listed sibling is available to sync.
            fixture.Local.FailWalkAfter(Plain, new InventoryIncompleteException("fixture", null));
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(Touches(fixture, Member) && await SyncedAsync(fixture, Plain) &&
                !await SyncedAsync(fixture, Member + "/file") &&
                fixture.Inner.Issues(PairId).Any(issue => issue is { Kind: IssueKind.Failure, Path: Folder }),
                "A104 a failed batch listing keeps the member, its sibling progress and one Failure");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A122: cancelled after the first commit: it stays released, the replay is ordinary, the second goes then.
            UnreadableMember.Record(fixture.Inner, PairId, Folder, FolderId);
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            using var cancel = new CancellationTokenSource();
            fixture.Hooks.BeforeCall = method =>
            {
                if (method.Name == nameof(IStateStore.ReleaseUnreadable)) cancel.Cancel();
            };
            var selection = Selected(fixture, Folder, folder);
            await ThrowsAsync<OperationCanceledException>(() => RunSelectionAsync(fixture, selection, cancel.Token));
            fixture.Hooks.BeforeCall = null;
            Check.Require(fixture.Inner.UnreadableUnder(PairId, PathNames.Key(Folder)).Count == 1, "A122 one committed");
            Check.Require(await RunSelectionAsync(fixture, selection) &&
                fixture.Inner.UnreadableUnder(PairId, PathNames.Key(Folder)).Count == 0, "A122 replay finishes the rest");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A101: a crash after release and continuation, before CompleteWork: the parent is reselected, no repeat.
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            var crash = new InvalidOperationException(CommitFault);
            fixture.Hooks.BeforeCall = method =>
            {
                if (method.Name == nameof(IStateStore.CompleteWork)) throw crash;
            };
            var selection = Selected(fixture, Folder, folder);
            await ThrowsAsync<InvalidOperationException>(() => RunSelectionAsync(fixture, selection));
            fixture.Hooks.BeforeCall = null;
            var revision = (await fixture.Dropbox.ReadOneAsync(Plain, default))!.Revision;
            Check.Require(await RunSelectionAsync(fixture, selection) && !Touches(fixture, Member) &&
                (await fixture.Dropbox.ReadOneAsync(Plain, default))!.Revision == revision, "A101 no repeated effect");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A123. The dismissed visibility question is OPEN and held for the owner: this asserts only today's outcome,
            // it does not approve it. Dismissed, access recovered, no event, no Repair: the member stays, no effect, and
            // no open notice shows it. Then an ordinary relevant event releases it safely.
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            fixture.Inner.DismissIssue(PairId, fixture.Inner.Issues(PairId).Single().Id);
            Check.Require(Touches(fixture, Member) && fixture.Inner.Issues(PairId).All(issue => issue.Dismissed) &&
                !fixture.Inner.IssueCounts(PairId).ContainsKey(IssueKind.Skipped), "A123 present outcome, open question");
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(!Touches(fixture, Member), "A123 an ordinary event releases a dismissed member");
        });

        static async Task fixtureCase(bool withMember, Func<Fixture, Task> check)
        {
            using var fixture = UnreadableFixture(withMember);
            await check(fixture);
        }
    }

    // A13, A64, A65: an existing ancestor hold takes the branch before any capture or lease; a holder that closed
    // first leaves it unfinished and reachable; closing the notice brings the attached work back.
    private static async Task HeldBranchAttachesBeforeAdmissionAsync(bool closeBeforeAttach)
    {
        using var fixture = new Fixture();
        var folder = FolderEntry(Folder, LocalId + "-folder");
        var child = LocalEntry(Folder + "/child", LocalId);
        fixture.Local.Seed(folder, null);
        fixture.Local.Seed(child, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, Folder,
                new IssueSubject([new IssueItem(Side.Local, Folder, folder.Id)], null))]
        });
        void Close() => fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Close = [(Folder, new[] { IssueKind.NameClash })]
        });
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, child.Path, null, child.Id,
            child, false, false));
        if (closeBeforeAttach)
            fixture.Hooks.BeforeCall = method => { if (method.Name == nameof(IStateStore.AttachIssueWork)) Close(); };
        var captures = 0;
        var boundary = new Boundary(_ => new Lease());
        var settled = await Runner(fixture, selection, boundary, (_, _, _) =>
        {
            captures++;
            return Task.FromResult(Coverage(selection));
        }).RunAsync(selection, default);
        fixture.Hooks.BeforeCall = null;
        Check.Require(captures == 0 && boundary.Calls == 0, "A held branch asks for no coverage or lease.");
        Check.Require(settled == !closeBeforeAttach, closeBeforeAttach
            ? "A holder closed before attachment leaves the selection unfinished."
            : "A branch attached to its holder lets the selection complete.");
        if (!closeBeforeAttach) Close();
        Check.Require(PendingAt(fixture, Side.Local, child.Path).Any(),
            "Work held by a notice stays reachable by its exact identity.");
    }

    public static void RunMappedHolds()
    {
        var failures = new List<Exception>();
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var missing in new[] { false, true })
        foreach (var changedHolder in new[] { false, true })
        {
            try { MappedHeldBranchAsync(side, missing, changedHolder).GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                failures.Add(new InvalidOperationException($"mapped hold {side}, missing={missing}, changed={changedHolder}", error));
            }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static async Task MappedHeldBranchAsync(Side side, bool missing, bool changedHolder)
    {
        using var fixture = new Fixture(realStore: true);
        const string staging = "Staging";
        const string current = "Current";
        const string childName = "/Child";
        var sourceFolder = FolderEntry(staging, "source-folder");
        var targetFolder = FolderEntry(current, "target-folder");
        var source = side == Side.Local ? fixture.Local : fixture.Dropbox;
        var target = side == Side.Local ? fixture.Dropbox : fixture.Local;
        source.Seed(sourceFolder, null);
        target.Seed(targetFolder, null);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Record = [SyncedEntries.From(side, sourceFolder, targetFolder)],
            Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, current,
                new IssueSubject([new IssueItem(PathNames.Other(side), current, targetFolder.Id)], null))]
        });
        var child = side == Side.Local ? LocalEntry(staging + childName, LocalId) :
            RemoteEntry(staging + childName, DropboxId);
        child = child with { Size = 2, Modified = Stamp.AddDays(1), ContentHash = DropboxContentHash.Compute(new byte[] { 1, 1 }) };
        source.Seed(child, [1, 1]);
        if (!missing) target.Seed(side == Side.Local ? RemoteEntry(current + childName, DropboxId) :
            LocalEntry(current + childName, LocalId), [2]);
        var selection = side == Side.Local ? Seed(fixture,
            new ChangeNotice(side, ChangeKind.Changed, child.Path, null, child.Id, child, false, false)) :
            SeedRemote(fixture, child);
        void Close() => fixture.Inner.Commit(PairId, StateChange.Empty with
        { Close = [(current, new[] { IssueKind.NameClash })] });
        if (changedHolder)
            fixture.Hooks.BeforeCall = method => { if (method.Name == nameof(IStateStore.AttachIssueWork)) Close(); };
        var boundary = new Boundary(_ => throw new InvalidOperationException("held work cannot be admitted"));
        var complete = await Runner(fixture, selection, boundary,
            (_, _, _) => throw new InvalidOperationException("held work cannot capture coverage"))
            .RunAsync(selection, default);
        fixture.Hooks.BeforeCall = null;
        Check.Require(complete == !changedHolder && boundary.Calls == 0 &&
            fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1,
            "137: mapped/observed hold attaches before admission; a changed holder stays unfinished");
        Check.Require(await source.ReadOneAsync(child.Path, default) == child &&
            (await target.ReadOneAsync(current + childName, default) is null) == missing,
            "137: held source and counterpart are untouched");
        var sibling = LocalEntry("Sibling/Child", "sibling-id");
        fixture.Local.Seed(FolderEntry("Sibling", "sibling-folder"), null);
        fixture.Local.Seed(sibling, [3]);
        await RunNoticeAsync(fixture, sibling.Path, sibling, page: side == Side.Local ? 2 : 1);
        Check.Require(await fixture.Dropbox.ReadOneAsync(sibling.Path, default) is not null,
            "137: independent sibling continues");
        fixture.RestartStore();
        if (!changedHolder)
        {
            Check.Require(fixture.Inner.Issues(PairId).Single().Kind == IssueKind.NameClash,
                "137: occupied-name hold survives restart");
            Close();
        }
        var released = PendingAt(fixture, side, child.Path).Single();
        Check.Require(await RunSelectionAsync(fixture, released),
            "137: released work executes and completes after restart");
        var recorded = fixture.Inner.SyncedById(PairId, side, child.Id);
        Check.Require(recorded is not null &&
            (await target.ReadOneAsync(recorded.PathOn(PathNames.Other(side)), default))?.Size == child.Size &&
            !PendingAt(fixture, side, child.Path).Any(),
            "137: released work copies content, records the binding and retires");
    }

    private static async Task PostRenameFailureRetainsAdvancedBeforeAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var old = LocalEntry(OldPath, LocalId);
        var remote = RemoteEntry(OldPath, DropboxId);
        var saved = SyncedEntries.From(old, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        fixture.Local.Seed(old with { Path = Item, Size = 2 }, [2, 2]);
        fixture.Dropbox.Seed(remote, [1]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, Item, OldPath,
            LocalId, null, true, false));
        var primaryReads = 0;
        fixture.LocalView = new HookedSide(fixture.Local, beforeRead: path =>
        {
            if (path == Item) primaryReads++;
        });
        var failed = false;
        fixture.DropboxView = new ProxySide(fixture.Dropbox)
        {
            BlockPublish = (_, _) =>
            {
                if (!failed)
                {
                    failed = true;
                    throw new ItemFailedException(Item, TextKey.FailDropboxNoPermission, null);
                }
                return Task.CompletedTask;
            }
        };
        var boundary = new Boundary(_ => new Lease());
        Check.Require(await Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)))
            .RunAsync(selection, default) is true, "the post-rename failure is retained on its notice");
        Console.WriteLine("106 post-rename Local reads=" + primaryReads);
        Check.Require(primaryReads == 3, "106 rename reads initial, fresh reobservation and admitted freshness once each");
        var after = fixture.Inner.SyncedById(PairId, Side.Local, LocalId)!;
        Check.Require(after.LocalPath == Item && fixture.Inner.Issues(PairId).Any(issue => issue.Kind == IssueKind.Failure),
            "the rename commits before its ordinary branch fails");
        var released = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Single();
        Check.Require(fixture.Inner.ReadWork(released).All(work => work.Before == after),
            "post-rename failure residual retains the advanced baseline before release");
        fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(Item, IssueClosure.AfterEffect)] });
        Check.Require(await Runner(fixture, released!, new Boundary(_ => new Lease()), (_, _, _) => Task.FromResult(Coverage(released!)))
            .RunAsync(released!, default) is true, "released post-rename failure retries to completion");
    }

    private static async Task OldAliasFailureRetainsAdvancedBeforeAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var old = LocalEntry(OldPath, LocalId);
        var remote = RemoteEntry(OldPath, DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(old, remote)] });
        fixture.Local.Seed(old with { Path = Item }, [1]);
        fixture.Dropbox.Seed(remote, [1]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, Item, OldPath,
            LocalId, null, false, false));
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name != nameof(IStateStore.Commit)) return;
            fixture.Hooks.BeforeCall = null;
            fixture.Local.Seed(LocalEntry(OldPath, OccupantId), [2]);
        };
        var failed = false;
        fixture.DropboxView = new ProxySide(fixture.Dropbox)
        {
            BlockPublish = (path, _) =>
            {
                if (path == OldPath && !failed)
                {
                    failed = true;
                    throw new ItemFailedException(path, TextKey.FailDropboxNoPermission, null);
                }
                return Task.CompletedTask;
            }
        };
        Check.Require(await Runner(fixture, selection, new Boundary(_ => new Lease()), (_, _, _) => Task.FromResult(Coverage(selection)))
            .RunAsync(selection, default) is true, "F03: foreign old-alias failure is retained after the rename");
        Check.Require(failed, "F03: the foreign old-alias upload reached its failure");
        var after = fixture.Inner.SyncedById(PairId, Side.Local, LocalId)!;
        var residual = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Single();
        Check.Require(fixture.Inner.ReadWork(residual).All(work => work.Before == after),
            "F03: synthetic old-alias residual has the renamed identity's current binding before release");
        fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(OldPath, IssueClosure.AfterEffect)] });
        Check.Require(await Runner(fixture, residual, new Boundary(_ => new Lease()), (_, _, _) => Task.FromResult(Coverage(residual)))
            .RunAsync(residual, default) is true, "F03: old-alias residual retries after release");
    }

    private static async Task EmptyBranchPreservesLaterArrivalAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, Item, OldPath,
            LocalId, null, false, false));
        WorkSelection? later = null;
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name != nameof(IStateStore.CompleteWork)) return;
            fixture.Hooks.BeforeCall = null;
            fixture.Local.Seed(LocalEntry(OldPath, OccupantId), [1]);
            later = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, OldPath, null,
                OccupantId, null, true, false), page: 2);
        };
        var boundary = new Boundary(_ => new Lease());
        Check.Require(await Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)))
            .RunAsync(selection, default) is true, "empty primary and old-path branches complete without admission");
        Check.Require(boundary.Calls == 0 && later is not null && fixture.Inner.ReadWork(later).Single().Notice.Id == OccupantId,
            "a foreign old-path arrival after observation remains pending beyond selection completion");
    }

    private enum RootState { Same, Replaced, Absent, Unreadable }

    // A61, A62, A63: a covering first-run obligation binds only the ancestor it recorded, by a fresh Local read.
    private static async Task RetainedAncestorIdentityAsync(RootState state)
    {
        using var fixture = new Fixture();
        var folder = FolderEntry(Folder, LocalId + "-folder");
        var child = LocalEntry(Folder + "/child", LocalId);
        fixture.Local.Seed(child, [1]);
        if (state != RootState.Absent)
            fixture.Local.Seed(state == RootState.Replaced ? folder with { Id = OccupantId } : folder, null);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, Folder,
                new IssueSubject([new IssueItem(Side.Local, Folder, folder.Id)], null,
                    FirstRunDeletion: IssueSubject.FirstRunDeletionKind.UnscannedSubtree))]
        });
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, child.Path, null, child.Id,
            child, false, false));
        if (state == RootState.Unreadable)
            fixture.Local.FailNext(Folder, new ItemFailedException(Folder, TextKey.ReadLocalFailed, null));
        var captures = 0;
        var settled = await Runner(fixture, selection, new Boundary(_ => new Lease()), (_, _, _) =>
        {
            captures++;
            return Task.FromResult(Coverage(selection));
        }).RunAsync(selection, default);
        Check.Require(state switch
            {
                RootState.Same => captures == 0 && settled is false,
                RootState.Unreadable => captures == 0,
                RootState.Absent => captures == 0 && settled is true,
                _ => captures == 1
            }, "A retained ancestor binds its child only while its identity holds: " + state + "; captures=" + captures);
        if (state == RootState.Unreadable)
            Check.Require(fixture.Inner.Issues(PairId).Any(issue => issue.Cause == TextKey.ReadLocalFailed),
                "A failed ancestor read is the row's Failure, never absence.");
    }

    // R1-03: each row/path/rename work shape must drive the admitted callback with the request's captured generation,
    // not a generation re-read from Storage after admission.
    private static async Task GenerationForwardedAsync()
    {
        foreach (var (route, notice) in Shapes(includeRename: false))
        {
            using var fixture = new Fixture();
            var local = LocalEntry(Item, LocalId);
            fixture.Local.Seed(local, [1, 2]);
            var selection = Seed(fixture, notice);
            var lease = new Lease();
            var boundary = new Boundary(_ => lease);
            var captures = 0;
            var runner = Runner(fixture, selection, boundary,
                (selected, footprint, _) =>
                {
                    captures++;
                    Check.Require(ReferenceEquals(selected, selection),
                        $"{route}: captureCoverage receives the exact selected work.");
                    Check.Require(footprint.Any(scope => scope.Side == Side.Local && scope.NativePath.Contains(Item)),
                        $"{route}: the footprint declares the selected Local path.");
                    return Task.FromResult(Coverage(selection));
                });

            var ready = await runner.RunAsync(selection, default);

            Check.Require(ready is true, $"{route}: a completed selection settles.");
            Check.Require(lease.RequireCalls == 1 && lease.Generation == selection.PairGeneration,
                $"{route}: the borrowed lease requires the captured request generation g.");
            Check.Require(captures == 1 && boundary.Calls == 1,
                $"{route}: one source capture and one admitted callback per valid admission.");
            Check.Require(await fixture.Dropbox.ReadOneAsync(Item, default) is not null,
                $"{route}: the admitted effect publishes the selected item.");
            Check.Require(ReferenceEquals(fixture.Attempts.Opportunity, selection),
                $"{route}: the registered item attempt binds the exact admitted opportunity.");
            Check.Require(fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 1,
                $"{route}: only settled work retires its exact selection.");
        }
    }

    private static async Task StaleGenerationRefusesAsync()
    {
        foreach (var (route, notice) in Shapes(includeRename: true))
        {
            using var fixture = new Fixture(realStore: true);
            var local = LocalEntry(Item, LocalId);
            fixture.Local.Seed(local, [1, 2]);
            var selection = Seed(fixture, notice);
            var lease = new Lease();
            foreach (var side in new[] { Side.Local, Side.Dropbox })
            {
                var source = fixture.Inner.ReadSource(PairId, side)!.Value;
                fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                    source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance, source.Cursor, null);
            }
            var boundary = new Boundary(request =>
            {
                fixture.Inner.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint);
                AdvanceGeneration(fixture);
                fixture.Inner.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint);
                return lease;
            });
            var runner = Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)));

            var error = await ThrowsAsync<LiveAdmissionStaleException>(
                () => runner.RunAsync(selection, default));

            Check.Require(error.Reason == LiveAdmissionStaleReason.CoverageChanged,
                $"{route}: generation change invalidates previously validated coverage; actual={error.Reason}.");
            Check.Require(boundary.Requests.Single().Single().Selection.PairGeneration == selection.PairGeneration &&
                lease.RequireCalls == 0,
                $"{route}: the boundary validates captured g against Storage g+1 before invoking the admitted callback.");
            Check.Require(fixture.Inner.ReadPairExecution(PairId)!.Value.Generation == Generation + 1,
                $"{route}: the fixture actually advanced the pair generation before the refusal.");
            Check.Require(await fixture.Dropbox.ReadOneAsync(Item, default) is null,
                $"{route}: a stale admission performs no effect.");
            Check.Require(fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 0,
                $"{route}: a stale admission retires no work.");
            // DESIGN-114 section 3.3: these unbound notices resolve to ordinary uploads, including the rename notice.
            Check.Require(fixture.Attempts.Executions == 1,
                $"{route}: the ordinary upload reserves before admission regardless of its source notice kind.");
        }
    }

    // A2 order: required Before is persisted before the coverage capture, and the exact supplied coverage is
    // forwarded in the one execution request.
    private static async Task BeginSelectionBeforeCaptureAsync()
    {
        using var fixture = new Fixture();
        var local = LocalEntry(Item, LocalId);
        var remote = RemoteEntry(Item, DropboxId);
        fixture.Local.Seed(local, [1]);
        fixture.Dropbox.Seed(remote, [1]);
        var saved = SyncedEntries.From(local, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var selection = Seed(fixture,
            new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, local, false, false));
        fixture.Local.Seed(local with { Size = 2 }, [2, 2]);
        var lease = new Lease();
        var boundary = new Boundary(_ => lease);
        var captures = 0;
        SyncedEntry? persistedBefore = null;
        LiveCoverage? supplied = null;
        var runner = Runner(fixture, selection, boundary,
            (selected, _, _) =>
            {
                captures++;
                persistedBefore = fixture.Store.ReadWork(selected).Single().Before;
                supplied = Coverage(selected);
                return Task.FromResult(supplied);
            });

        var ready = await runner.RunAsync(selection, default);

        Check.Require(ready is true, "A changed row settles its selection.");
        Check.Require(persistedBefore == saved,
            "BeginSelection persists the identity's current preimage before effect coverage is captured.");
        Check.Require(captures == 1 && boundary.Calls == 1,
            "One coverage capture and one admitted callback serve the admission lifetime.");
        Check.Require(boundary.Requests[0][0].Coverage with { Peer = null } == supplied,
            "The execution request keeps the supplied source coverage and adds only the observed peer.");
        Check.Require(ReferenceEquals(boundary.Requests[0][0].Selection, selection),
            "The execution request carries the exact selected work.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 1,
            "A settled selection retires exactly once.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 0,
            "A successful upload retains no unfinished issue work.");
    }

    // Q17: the committed occupant-inclusive Issue and the effective row reach the selection-bound continuation once.
    private static async Task RetainedIssueAsync()
    {
        using var fixture = OccupiedFixture(out var selection, out var saved, out var occupant, out var boundary);
        var runner = Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)));

        var ready = await runner.RunAsync(selection, default);

        var issues = fixture.Inner.Issues(PairId);
        var clash = issues.SingleOrDefault(issue => issue.Kind == IssueKind.NameClash);
        Check.Require(ready is true, "A fully registered remainder still settles its exact selection.");
        Check.Require(clash is not null && clash.Path == DestinationPath,
            "The committed NameClash is raised at the actual occupied target.");
        Check.Require(clash!.Subject.Items.Any(item =>
                item.Side == Side.Dropbox && item.Path == DestinationPath && item.Id == occupant.Id),
            "The committed Issue includes the actual occupant found at the target.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1,
            "The exact unfinished branch is registered exactly once.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 1,
            "Registered responsibility permits retiring the parent selection.");
        var pending = PendingAt(fixture, Side.Local, SourcePath, DestinationPath).ToList();
        Check.Require(pending.Count == 1, "The registered residual remains reachable by its exact identity.");
        var residual = fixture.Inner.ReadWork(pending[0]).Single();
        Check.Require(residual.Notice.Path == DestinationPath,
            "The residual records the effective Issue path, not the initially scheduled path.");
        Check.Require(residual.Notice.Observed?.Path == SourcePath,
            "The residual records the effective observed row.");
        Check.Require(residual.Before == saved, "The residual retains the applicable preimage.");
    }

    // A provider-free Notice commits its own Issue, so the same exact continuation must register it.
    private static async Task ProviderFreeNoticeRetainsAsync()
    {
        using var fixture = new Fixture();
        fixture.Local.Seed(LocalEntry(Item, LocalId), [1]);
        fixture.Dropbox.Seed(LocalEntry(Item, DropboxId) with { IsFolder = true }, null);
        var selection = Seed(fixture,
            new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, null, false, false));
        var boundary = new Boundary(_ => new Lease());
        var runner = Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)));

        var ready = await runner.RunAsync(selection, default);

        var issue = fixture.Inner.Issues(PairId).Single(raised => raised.Kind == IssueKind.NameClash);
        Check.Require(ready is true && issue.Path == Item,
            "A notice-only decision still settles after registering its exact issue.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1,
            "A provider-free Notice reaches the retained-issue continuation once.");
        var pending = PendingAt(fixture, Side.Local, Item).Single();
        Check.Require(fixture.Inner.ReadWork(pending).Single().Notice.Path == Item,
            "The provider-free notice registers its effective subject.");
    }

    // The occupant read is itself fallible; its failure still commits an Issue that the continuation must register.
    private static async Task SelectedReadFailuresAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var ordinal in new[] { 1, 2 })
        {
            using var fixture = new Fixture();
            var entry = LocalEntry(Item, LocalId);
            fixture.Local.Seed(entry, [1]);
            var reads = 0;
            var cause = Text.ReadFailedOn(side);
            var faulted = new HookedSide(side == Side.Local ? fixture.Local : fixture.Dropbox,
                beforeRead: path =>
                {
                    if (path == Item && ++reads == ordinal)
                        throw new ItemFailedException(path, cause, null);
                });
            if (side == Side.Local) fixture.LocalView = faulted;
            else fixture.DropboxView = faulted;
            var selection = Seed(fixture,
                new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, entry, false, false));
            var boundary = new Boundary(_ => new Lease());
            var runner = Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)));
            Check.Require(await runner.RunAsync(selection, default) is true && reads >= ordinal,
                "A selected branch read failure is retained without faulting the run.");
            Check.Require(fixture.Inner.Issues(PairId).Single().Cause == cause &&
                fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1 &&
                await fixture.Dropbox.ReadOneAsync(Item, default) is null,
                "Prewalk, observation and admitted reads preserve the item and attach its exact Failure.");
        }
    }

    // Bug #95: a deleted-on-both-sides item is absence for the existing planner; a genuine permanent read failure
    // is one deduped notice, including a malformed single-path reply.
    private static async Task ObservationReadOutcomesAsync()
    {
        // Absent locally and absent on Dropbox: the planner's both-absent answer, with no Failure or notice.
        using (var fixture = new Fixture())
        {
            var selection = Seed(fixture,
                new ChangeNotice(Side.Local, ChangeKind.Deleted, Item, null, null, null, false, false));
            await RunSelectionAsync(fixture, selection);
            Check.Require(fixture.Inner.Issues(PairId).Count == 0,
                "a path absent on both sides settles through the planner with no notice");
        }
        // The same both-deleted outcome with saved history is the existing ForgetOnly decision.
        using (var fixture = new Fixture())
        {
            var saved = SyncedEntries.From(LocalEntry(Item, LocalId), RemoteEntry(Item, DropboxId));
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
            var selection = Seed(fixture,
                new ChangeNotice(Side.Local, ChangeKind.Deleted, Item, null, LocalId, null, false, false));
            await RunSelectionAsync(fixture, selection);
            Check.Require(fixture.Inner.Issues(PairId).Count == 0 &&
                fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Item)) is null,
                "both-deleted saved history settles by the existing ForgetOnly decision with no notice");
        }
        // Local present, Dropbox read not_found: the ordinary planner decision, never a Failure.
        using (var fixture = new Fixture())
        {
            fixture.Local.Seed(LocalEntry(Item, LocalId), [2]);
            var selection = Seed(fixture,
                new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, null, false, false));
            await RunSelectionAsync(fixture, selection);
            Check.Require(fixture.Inner.Issues(PairId).Count == 0,
                "a not_found Dropbox read under a present local item follows the ordinary planner decision");
        }
        // A genuine permanent read failure leaves exactly one Failure notice for the path (the store's Raise
        // dedupes by path and kind, and the selection's visited guard skips the duplicate notice). The malformed
        // variant carries the boundary's cause, never the trigger side's.
        foreach (var error in new Exception[]
        {
            new ItemFailedException(Item, TextKey.ReadDropboxFailed, null),
            new ItemFailedException(Item, TextKey.ReadDropboxFailed, new JsonException())
        })
        {
            using var fixture = new Fixture();
            fixture.Local.Seed(LocalEntry(Item, LocalId), [2]);
            fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeRead: path =>
            {
                if (PathNames.Key(path) == PathNames.Key(Item)) throw error;
            });
            var notice = new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, null, false, false);
            var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
            var watermark = fixture.Inner.AcquireJournalPage(VolumeKey, null, new JournalPosition(1, 1),
                [(PairId, Generation, source.Epoch, source.Revision, false)],
                [(PairId, notice), (PairId, notice)]);
            var selection = new WorkSelection(PairId, Generation, source.Epoch, Side.Local, VolumeKey, notice.Id,
                PathNames.KeyFor(Side.Local, notice.Path), watermark!.Value, watermark!.Value);
            await RunSelectionAsync(fixture, selection);
            var issues = fixture.Inner.Issues(PairId);
            Check.Require(issues.Count == 1 && issues[0].Kind == IssueKind.Failure && issues[0].Path == Item &&
                issues[0].Cause == TextKey.ReadDropboxFailed &&
                fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1,
                "a permanent single-path read failure creates exactly one retained Failure with this endpoint's cause: " +
                error.GetType().Name);
        }
    }

    private static async Task OccupantReadFailureStillRetainsAsync()
    {
        using var fixture = new Fixture();
        var prior = LocalEntry(SourcePath, LocalId);
        var remote = RemoteEntry(DestinationPath, DropboxId);
        var saved = SyncedEntries.From(prior, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        fixture.Local.Seed(LocalEntry(SourcePath, LocalId) with { Size = 2 }, [1, 2]);
        var occupant = RemoteEntry(DestinationPath, OccupantId);
        var reads = 0;
        fixture.DropboxView = new HookedSide(fixture.Dropbox,
            _ => fixture.Dropbox.Seed(occupant, [3]),
            path =>
            {
                if (path == DestinationPath && ++reads == OccupantReadOrdinal)
                    throw new ItemFailedException(path, TextKey.ReadDropboxFailed, null);
            });
        var selection = Seed(fixture,
            new ChangeNotice(Side.Local, ChangeKind.Changed, SourcePath, null, LocalId, null, false, false));
        var boundary = new Boundary(_ => new Lease());
        var runner = Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)));

        var ready = await runner.RunAsync(selection, default);

        var issue = fixture.Inner.Issues(PairId).Single(raised => raised.Kind == IssueKind.Failure);
        Check.Require(ready is true && issue.Path == SourcePath && issue.Cause == TextKey.ReadDropboxFailed,
            "A failed occupant read commits the row's own Failure and still settles its selection.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1,
            "The occupant-read failure reaches the retained-issue continuation once.");
        var pending = PendingAt(fixture, Side.Local, SourcePath).Single();
        Check.Require(fixture.Inner.ReadWork(pending).Single().Notice.Path == SourcePath,
            "The residual records the effective Failure subject.");
    }

    private static async Task CommitFailureDoesNotRetainAsync()
    {
        using var fixture = OccupiedFixture(out var selection, out _, out _, out var boundary);
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name == nameof(IStateStore.Commit)) throw new InvalidOperationException(CommitFault);
        };
        var runner = Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)));

        var error = await ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(selection, default));

        Check.Require(error.Message == CommitFault, "The failed commit propagates its own failure.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 0,
            "A failed commit invokes no committed-Issue callback.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 0,
            "A failed commit retires no selection.");
        Check.Require(fixture.Inner.Issues(PairId).All(issue => issue.Kind != IssueKind.NameClash),
            "A failed commit persists no issue.");
    }

    private static async Task AttachmentFailureRetainsParentAsync()
    {
        using var fixture = OccupiedFixture(out var selection, out _, out _, out var boundary);
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name == nameof(IStateStore.AttachIssueWork)) throw new InvalidOperationException(AttachFault);
        };
        var runner = Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)));

        var error = await ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(selection, default));

        Check.Require(error.Message == AttachFault, "A failed attachment propagates rather than settling.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1,
            "The failed attachment is attempted once.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 0,
            "A failed attachment never retires its parent.");
        Check.Require(fixture.Inner.Issues(PairId).Count(issue => issue.Kind == IssueKind.NameClash) == 1,
            "A failed attachment does not raise a second issue.");
        var parent = PendingAt(fixture, Side.Local, SourcePath).Single();
        Check.Require(fixture.Inner.ReadWork(parent).Single().Notice.Path == SourcePath,
            "A failed attachment leaves the parent selection responsible.");
    }

    private static async Task WrongSelectionRefusesAsync()
    {
        using var fixture = new Fixture();
        fixture.Local.Seed(LocalEntry(Item, LocalId), [1]);
        var selection = Seed(fixture,
            new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, null, false, false));
        var lease = new Lease();
        var boundary = new Boundary(_ => lease);
        var captures = 0;
        var runner = Runner(fixture, selection, boundary,
            (_, _, _) =>
            {
                captures++;
                return Task.FromResult(Coverage(selection));
            });

        var foreign = selection with { PairId = "other-pair" };
        var wrongPair = await ThrowsAsync<LiveAdmissionStaleException>(
            () => runner.RunAsync(foreign, default));

        Check.Require(wrongPair.Reason == LiveAdmissionStaleReason.SelectionChanged,
            "A foreign pair refuses as a changed selection.");
        Check.Require(captures == 0 && boundary.Calls == 0 && lease.RequireCalls == 0,
            "A refused admission captures no coverage and opens no boundary callback.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 0,
            "A refused admission retires nothing.");
        Check.Require(await fixture.Dropbox.ReadOneAsync(Item, default) is null,
            "A refused admission performs no effect.");
    }

    // D1: a live replacement of a Local folder is refused while a descendant is known unreadable, whole, with no effect.
    private static async Task LiveFolderReplaceOverUnreadableChildAsync()
    {
        using var fixture = new Fixture();
        var folder = FolderEntry(Folder, LocalId);
        var saved = SyncedEntries.From(folder, FolderEntry(Folder, DropboxId));
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var child = LocalEntry(Folder + "/locked", OccupantId) with { Skip = nameof(TextKey.SkipUnreadable) };
        fixture.Local.Seed(folder, null);
        fixture.Local.Seed(child, [1]);
        var replacement = RemoteEntry(Folder, "dropbox-file");
        fixture.Dropbox.Seed(replacement, [1]);
        // The unreadable fact lives in Storage's skip-member projection (the store's point-of-use answer), so it is
        // recorded through the side-observation owner, exactly as Repair records it; a hand-raised Skipped issue
        // alone leaves no member for the range answer.
        fixture.Inner.ObserveSkipped(PairId, Generation, Side.Local, child.Path,
            (TextKey.SkipUnreadable, new IssueSubject([new IssueItem(Side.Local, child.Path, child.Id)], null)));
        var selection = Seed(fixture,
            new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null, LocalId, folder, false, false));
        var runner = Runner(fixture, selection, new Boundary(_ => new Lease()),
            (_, _, _) => Task.FromResult(Coverage(selection)));

        Check.Require(await runner.RunAsync(selection, default) is not null, "Selection must be admitted.");

        Check.Require((await fixture.Local.ReadOneAsync(Folder, default))?.IsFolder == true &&
            await fixture.Local.ReadOneAsync(child.Path, default) is not null,
            "A live replacement leaves a folder with a known unreadable child in place.");
        Check.Require(fixture.Inner.Issues(PairId).Any(issue => issue.Kind == IssueKind.Failure &&
            issue.Path == Folder && issue.Cause == TextKey.ReadLocalFailed),
            "The refused live replacement raises the ordinary visible unreadable Failure.");
        Check.Require(fixture.Inner.SyncedByKey(PairId, Side.Local, saved.LocalKey) == saved,
            "The refused live replacement keeps the saved binding.");
    }

    // D2: a hold raised by a folder row covers the rest of the same selection, by the rule Repair uses.
    private static async Task RaisedHoldCoversSelectionChildrenAsync()
    {
        using var fixture = new Fixture();
        var folder = FolderEntry(Folder, LocalId);
        var child = LocalEntry(Folder + "/child", OccupantId);
        fixture.Local.Seed(folder, null);
        fixture.Local.Seed(child, [1]);
        fixture.Dropbox.Seed(RemoteEntry(Folder, DropboxId), [1]);
        var selection = Seed(fixture,
            new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null, LocalId, folder, false, false));
        var runner = Runner(fixture, selection, new Boundary(_ => new Lease()),
            (_, _, _) => Task.FromResult(Coverage(selection)));

        Check.Require(await runner.RunAsync(selection, default) is not null, "Selection must be admitted.");

        var issues = fixture.Inner.Issues(PairId);
        Check.Require(issues.Count == 1 && issues[0] is { Kind: IssueKind.NameClash, Cause: TextKey.ClashFileFolder },
            "The folder raises its NameClash and its child adds nothing while the clash is unresolved.");
        Check.Require(fixture.Admission.Completed == 0 &&
            await fixture.Dropbox.ReadOneAsync(child.Path, default) is null,
            "No action runs for a child of the folder that raised the hold.");
    }

    // D3: a rename under a held folder is held through the prefix rule, not only on an exact held key.
    private static async Task RenameUnderHeldFolderHoldsAsync()
    {
        using var fixture = new Fixture();
        var localFolder = FolderEntry(Folder, LocalId + "-folder");
        var remoteFolder = FolderEntry(Folder, DropboxId + "-folder");
        var before = LocalEntry(Folder + "/child", LocalId);
        var remote = RemoteEntry(Folder + "/child", DropboxId);
        var saved = SyncedEntries.From(before, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Record = [SyncedEntries.From(localFolder, remoteFolder), saved],
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, Folder,
                new IssueSubject([new IssueItem(Side.Local, Folder, localFolder.Id)], null))]
        });
        fixture.Local.Seed(localFolder, null);
        fixture.Dropbox.Seed(remoteFolder, null);
        fixture.Dropbox.Seed(remote, [1]);
        var moved = before with { Path = Folder + "/child2" };
        fixture.Local.Seed(moved, [1]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, moved.Path, before.Path,
            LocalId, moved, false, false));
        var runner = Runner(fixture, selection, new Boundary(_ => new Lease()),
            (_, _, _) => Task.FromResult(Coverage(selection)));

        Check.Require(await runner.RunAsync(selection, default) is not null, "Selection must be admitted.");

        Check.Require(await fixture.Dropbox.ReadOneAsync(remote.Path, default) is not null &&
            await fixture.Dropbox.ReadOneAsync(moved.Path, default) is null,
            "A rename under a held folder moves nothing.");
        Check.Require(fixture.Inner.SyncedById(PairId, Side.Local, LocalId) == saved,
            "A rename under a held folder keeps the saved binding.");
    }

    private static Fixture OccupiedFixture(out WorkSelection selection, out SyncedEntry saved, out Entry occupant,
        out Boundary boundary)
    {
        var fixture = new Fixture();
        var prior = LocalEntry(SourcePath, LocalId);
        var remote = RemoteEntry(DestinationPath, DropboxId);
        saved = SyncedEntries.From(prior, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var changed = LocalEntry(SourcePath, LocalId) with { Size = 2 };
        fixture.Local.Seed(changed, [1, 2]);
        occupant = RemoteEntry(DestinationPath, OccupantId) with { Size = 3, ContentHash = DropboxContentHash.Compute(new byte[] { 3 }) };
        var occupantEntry = occupant;
        var hooked = new HookedSide(fixture.Dropbox, _ => fixture.Dropbox.Seed(occupantEntry, [3]));
        selection = Seed(fixture,
            new ChangeNotice(Side.Local, ChangeKind.Changed, SourcePath, null, LocalId, changed, false, false));
        fixture.DropboxView = hooked;
        boundary = new Boundary(_ => new Lease());
        return fixture;
    }

    private static IEnumerable<(string Route, ChangeNotice Notice)> Shapes(bool includeRename)
    {
        var local = LocalEntry(Item, LocalId);
        yield return ("row",
            new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, local, false, false));
        yield return ("path",
            new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, null, null, false, false));
        if (!includeRename) yield break;
        yield return ("rename",
            new ChangeNotice(Side.Local, ChangeKind.Renamed, Item, OldPath, LocalId, local, false, false));
    }

    private static WorkSelection Seed(Fixture fixture, ChangeNotice notice, long page = 1)
    {
        var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
        var watermark = fixture.Inner.AcquireJournalPage(VolumeKey,
            page == 1 ? null : new JournalPosition(1, (ulong)page - 1), new JournalPosition(1, (ulong)page),
            [(PairId, Generation, source.Epoch, source.Revision, false)], [(PairId, notice)]);
        Check.Require(watermark is not null, "The journal page commits the selected work.");
        return new WorkSelection(PairId, Generation, source.Epoch, Side.Local, VolumeKey, notice.Id,
            PathNames.KeyFor(Side.Local, notice.Path), watermark!.Value, watermark!.Value);
    }

    private static void AdvanceGeneration(Fixture fixture)
    {
        var current = fixture.Inner.Pair(PairId)!;
        fixture.Inner.SavePairDecision(current, Generation, current with { Name = "Pair A reconfigured" },
            null, false, null);
    }

    private static Entry FolderEntry(string path, string id) => new(path, true, 0, Stamp, id, "", "", null);

    private static Entry LocalEntry(string path, string id) =>
        new(path, false, 1, Stamp, id, "", "", null);

    private static Entry RemoteEntry(string path, string id) =>
        new(path, false, 1, Stamp, id, "rev-1", DropboxContentHash.Compute(new byte[] { 1 }), null);

    private static LiveCoverage Coverage(WorkSelection selection) =>
        new(new LocalCatchUp(PairId, selection.PairGeneration, selection.SourceEpoch,
                new NtfsUsnCheckpoint(1, 1, 1, 1), selection.ThroughWorkId),
            new DropboxCatchUp(PairId, selection.PairGeneration, selection.SourceEpoch, Cursor,
                selection.ThroughWorkId));

    private static SelectionPass Runner(Fixture fixture, WorkSelection selection, Boundary boundary,
        Func<WorkSelection, IReadOnlyList<LiveEffectScope>, CancellationToken, Task<LiveCoverage>> capture,
        IAdmission? admission = null)
    {
        var context = fixture.Context(selection, admission);
        var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(), boundary,
            RefusingExecutionBoundary.Instance, 0));
        boundary.Capture = capture;
        return new SelectionPass(context, jobs.Items, jobs.Ancestors, boundary, jobs.Actions,
            jobs.Items.Rows, (path, _) => context.Events(RunEvent.Stage(TextKey.StageRows, inProgress: true, path: path)),
            (selected, side, scope, retries, settles, ct) => SyncJobs.RunBatchAsync(context,
                new SyncPorts(new RecoveryBoundary(), boundary, RefusingExecutionBoundary.Instance, 0), selected, scope, ct));
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T error)
        {
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class Fixture : IDisposable
    {
        public FakeSide Local { get; } = new(Side.Local);
        public FakeSide Dropbox { get; } = new(Side.Dropbox);
        public IStateStore Inner { get; private set; }
        private readonly string? _storeFolder;
        private readonly Guid? _registeredAttempt;
        public string? DatabasePath => _storeFolder is null ? null : Path.Combine(_storeFolder, "state.db");
        public IStateStore Store { get; }
        public ForwardStore Hooks { get; }
        public FakeAdmission Admission { get; } = new() { Level = 1 };
        public ItemAttempts Attempts { get; } = new();
        public ReplaceTurn Runtime { get; } = new();
        public PairConfig Pair { get; } = new(PairId, "Pair A", "C:/PairA", "/PairA", "connection-a", [], [],
            FirstRunPolicy.NewestWins, true, null,
            new RootBinding("C:/PairA", "/PairA", "connection-a", new DirectoryIdentity(1, 2, null),
                new DropboxRootIdentity("account-a", "namespace-a", "home-a", "folder-a")));
        public ISide? DropboxView { get; set; }
        public ISide? LocalView { get; set; }
        public System.Collections.Concurrent.ConcurrentQueue<RunEvent> Events { get; } = new();

        public Fixture(bool realStore = false, string? localRoot = null, Guid? registeredAttempt = null)
        {
            _registeredAttempt = registeredAttempt;
            if (realStore)
            {
                _storeFolder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
                Inner = OpenStore();
            }
            else Inner = new InMemoryStateStore();
            if (localRoot is not null) Pair = Pair with { LocalRoot = localRoot };
            Inner.SavePair(Pair);
            var proxy = DispatchProxy.Create<IStateStore, ForwardStore>();
            Hooks = (ForwardStore)(object)proxy;
            Hooks.Inner = Inner;
            Store = proxy;
        }

        private IStateStore OpenStore() => _registeredAttempt is { } attempt
            ? Massdex.SyncShark.Engine.Storage.StateStore.Open(DatabasePath!, (id, _, _, _, _) => id == attempt)
            : Massdex.SyncShark.Engine.Storage.StateStore.Open(DatabasePath!);

        public void RestartStore()
        {
            Inner.Dispose();
            Inner = OpenStore();
            Hooks.Inner = Inner;
        }

        public EngineSettings Settings { get; } = new("{name} ({side} {time}){ext}", "yyyyMMddHHmmss", "Local", "Dropbox",
            ".case", RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "", LogBufferCharacters, ScanTestSettings.BatchRows)
        {
            // Single-row pages exercise the folder survivor across page boundaries.
            StoragePageSize = 1
        };

        public ExecutorContext Context(WorkSelection selection, IAdmission? admission = null)
        {
            return new ExecutorContext(LocalView ?? Local, DropboxView ?? Dropbox,
                new FakeRecovery { Evidence = new DriveEvidence(1, null, null), Hash = Local.ContentHashAsync }, Store, Pair,
                new PathScope(Pair, [Pair]), Settings, Runtime, admission ?? Admission, false, Events.Enqueue,
                (_, _, _, _) => ValueTask.FromResult<IAsyncDisposable>(NoReservation.Instance),
                new FakeTimeProvider(),
                (_, _, _) => throw new NotSupportedException(),
                operation => new RequestAttempt(operation, AttemptOwner.Transfer),
                (_, _) => Task.FromResult(false),
                ("account-a", "route-a", "source-a"), Attempts, selection, Generation) { Live = true };
        }

        public void Dispose()
        {
            Inner.Dispose();
            if (_storeFolder is not null) Directory.Delete(_storeFolder, true);
        }
    }

    private sealed class NoReservation : IAsyncDisposable
    {
        public static readonly NoReservation Instance = new();
        public ValueTask DisposeAsync() => default;
    }

    /// <summary>One borrowed lease that records the generation it is required to authorize and can reject after
    /// the surrounding test advances Storage.</summary>
    private sealed class Lease : IAdmittedExecution
    {
        public long? Generation { get; private set; }
        public int RequireCalls { get; private set; }
        public bool Reject { get; set; }
        public Action<Lease>? OnRequire { get; set; }
        public IReadOnlyList<string>? AllowedKeys { get; init; }
        public IReadOnlyList<PathLockScope>? AllowedScopes { get; init; }
        public int ScopeCalls { get; private set; }

        public void Require(string pairId, long generation, IReadOnlyList<string> requiredLockKeys)
        {
            RequireCalls++;
            Generation = generation;
            OnRequire?.Invoke(this);
            if (AllowedKeys is { } allowed)
                Check.Require(requiredLockKeys.All(key => allowed.Contains(key, PathLocks.KeyComparer)),
                    "G18: admitted footprint supplies every required action lock");
            if (Reject) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        }

        public void RequireScopes(string pairId, long generation, IReadOnlyList<PathLockScope> scopes)
        {
            ScopeCalls++;
            Require(pairId, generation, scopes.Select(scope => scope.Key).ToArray());
            if (AllowedScopes is { } allowed && scopes.Any(required => !allowed.Any(scope =>
                PathLocks.KeyComparer.Equals(scope.Key, required.Key) && (!required.IncludesDescendants || scope.IncludesDescendants))))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
        }

        public void Revalidate() { }

        public ValueTask ReleaseAdmissionAsync() => AllowsRelease
            ? default : throw new NotSupportedException(nameof(ReleaseAdmissionAsync));

        public ValueTask DisposeAsync() => default;

        /// <summary>Uploads and folder removals release their outer admission before their independent work.</summary>
        public bool AllowsRelease { get; init; } = true;
    }

    private sealed class Boundary(Func<LiveExecutionRequest, IAdmittedExecution> leaseFor) : ILiveExecutionBoundary
    {
        public bool TryHoldGeneration(string pairId) => true;

        public List<IReadOnlyList<LiveExecutionRequest>> Requests { get; } = [];
        public int Calls => Requests.Count;
        public Func<WorkSelection, IReadOnlyList<LiveEffectScope>, CancellationToken, Task<LiveCoverage>> Capture { get; set; } =
            (_, _, _) => throw new NotSupportedException();

        public Task<T> ExecuteAsync<T>(IReadOnlyList<LiveExecutionRequest> requests,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            Requests.Add(requests);
            return execute(leaseFor(requests.Single()), ct);
        }

        public Task<LiveCoverage> CaptureCoverageAsync(WorkSelection selection,
            IReadOnlyList<LiveEffectScope> footprint, CancellationToken ct) => Capture(selection, footprint, ct);
    }

    private sealed class ItemAttempts : IItemAttempts
    {
        public object? Opportunity { get; private set; }
        public int Executions { get; private set; }

        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            Executions++;
            Opportunity = opportunity;
            return execute(Guid.NewGuid(), ct);
        }

        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path,
            IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
            PendingEffect? expectedPending, ItemRetryToken? expectedEpisode) => throw new NotSupportedException();

        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) =>
            throw new NotSupportedException();
    }

    private sealed class RecoveryBoundary : IRecoveryExecutionBoundary
    {
        public void RequireCurrent() { }

        public Task<T> ExecuteAsync<T>(FolderRemovalState operation, FolderRemovalItem member,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) => Run(execute, ct);

        public Task<T> ExecuteAsync<T>(IReadOnlyList<FolderRemovalState> operations,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) => Run(execute, ct);

        public Task<T> ExecuteAsync<T>(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) => Run(execute, ct);

        public Task ExecuteAsync(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task> execute, CancellationToken ct) =>
            Run(async (lease, token) => { await execute(lease, token); return true; }, ct);

        private static async Task<T> Run<T>(Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // A folder removal releases its outer permit before the send; recovery leases must allow that.
            await using var lease = new Lease { AllowsRelease = true };
            return await execute(lease, ct);
        }
    }

    /// <summary>Delegates every side call and runs one hook before staging, so an occupant can appear only after
    /// the decisive freshness read and before publication.</summary>
    private sealed class HookedSide(ISide inner, Action<string>? beforeStage = null,
        Action<string>? beforeRead = null, bool failDisposal = false, bool refuseRemoval = false,
        Action<string>? beforeEnsure = null,
        Func<Entry, CancellationToken, Task<string>>? hash = null,
        Func<FolderSubmission, IReadOnlyList<FolderRemovalEffect>, RequestAttempt, CancellationToken, Task<FolderSubmitResult>>? submit = null,
        Func<string, int, RequestAttempt, CancellationToken, Task<FolderJobResult>>? receipt = null) : ISide
    {
        public Side Side => inner.Side;

        public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct) =>
            failDisposal ? new FailingDisposal(inner.ReadAllAsync(under, options, ct)) :
                inner.ReadAllAsync(under, options, ct);

        private sealed class FailingDisposal(IAsyncEnumerable<Listed> source) : IAsyncEnumerable<Listed>
        {
            public IAsyncEnumerator<Listed> GetAsyncEnumerator(CancellationToken ct = default) =>
                new Enumerator(source.GetAsyncEnumerator(ct));

            private sealed class Enumerator(IAsyncEnumerator<Listed> inner) : IAsyncEnumerator<Listed>
            {
                public Listed Current => inner.Current;
                public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();
                public async ValueTask DisposeAsync()
                {
                    await inner.DisposeAsync();
                    throw new InventoryIncompleteException("outer disposal", null);
                }
            }
        }

        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
        {
            beforeRead?.Invoke(path);
            return inner.ReadOneAsync(path, ct);
        }

        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) =>
            inner.OpenReadAsync(entry, attempt, ct);

        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) =>
            hash is null ? inner.ContentHashAsync(entry, ct) : hash(entry, ct);

        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
        {
            beforeStage?.Invoke(path);
            return inner.StageAsync(path, content, source, ct);
        }

        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct) => inner.PublishAsync(staged, expected, selection, ct);

        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct)
        {
            beforeEnsure?.Invoke(path);
            return inner.EnsureFolderAsync(path, ct);
        }

        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) =>
            inner.DeleteAsync(expected, selection, ct);

        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) =>
            inner.MoveAsync(expected, newPath, ct);

        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
            submit is not null ? submit(submission, effects, attempt, ct) :
            refuseRemoval ? Task.FromResult<FolderSubmitResult>(new FolderSubmitResult.Terminal(
                effects.Select((_, index) => (FolderMemberResult)new FolderMemberResult.KnownNoEffect(index,
                    TextKey.FailChanged)).ToArray())) : inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);

        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount, RequestAttempt attempt,
            CancellationToken ct) => receipt is not null ? receipt(jobId, expectedCount, attempt, ct) :
                inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
    }

    /// <summary>Forwards every store call unchanged and counts method names, with one optional fault hook.</summary>
    public class ForwardStore : DispatchProxy
    {
        private readonly List<string> _calls = [];
        private IStateStore? _inner;

        public IStateStore Inner
        {
            get => _inner ?? throw new InvalidOperationException("The store double is unbound.");
            set => _inner = value;
        }

        public Action<MethodInfo>? BeforeCall { get; set; }
        public Action<WorkSelection>? OnReadWork { get; set; }
        public bool RepairDone { get; set; } = true;
        public List<WorkSelection> CommittedSelections { get; } = [];
        public int RecordedKeyVisits { get; private set; }
        public List<(Side Side, long? Through, int Count)> AncestorQueries { get; } = [];

        private sealed class CountedKeys(IReadOnlyList<string> keys, ForwardStore owner) : IReadOnlyList<string>
        {
            public int Count => keys.Count;
            public string this[int index] => keys[index];
            public IEnumerator<string> GetEnumerator()
            {
                foreach (var key in keys)
                {
                    owner.RecordedKeyVisits++;
                    yield return key;
                }
            }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public int Count(string method)
        {
            lock (_calls) return _calls.Count(call => call == method);
        }

        protected override object? Invoke(MethodInfo? target, object?[]? args)
        {
            var method = target ?? throw new InvalidOperationException("A store call must name its method.");
            args ??= Array.Empty<object?>();
            lock (_calls) _calls.Add(method.Name);
            if (method.Name == nameof(IStateStore.Commit) && args.Length == 4 && args[2] is WorkSelection selected)
                CommittedSelections.Add(selected);
            // AutoSync runs only after the first Repair; these fixtures start from that state.
            if (method.Name == nameof(IStateStore.FirstRepairDone)) return RepairDone;
            BeforeCall?.Invoke(method);
            if (method.Name == nameof(IStateStore.ReadWork)) OnReadWork?.Invoke((WorkSelection)args[0]!);
            try
            {
                var result = method.Invoke(Inner, args);
                if (method.Name == nameof(IStateStore.UnreadableUnder))
                    return new CountedKeys((IReadOnlyList<string>)result!, this);
                if (method.Name == nameof(IStateStore.PendingAncestors))
                {
                    var pending = ((IEnumerable<WorkSelection>)result!).ToArray();
                    AncestorQueries.Add(((Side)args[2]!, (long?)args[4], pending.Length));
                    return pending;
                }
                return result;
            }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
            {
                // The reflection boundary must not change a store/callback exception's type or message.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
