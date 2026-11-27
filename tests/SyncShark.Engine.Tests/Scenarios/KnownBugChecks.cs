using System.Net;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Codec = Massdex.SyncShark.Engine.Storage.Encoding;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>
/// Regression checks for sync behaviour. Unimplemented cases report their missing fixture.
/// The wrong-move / wrong-delete safety subset runs over the parity-checked in-memory store
/// (TestDoubles/StoreParityChecks) through the real Repair, ItemSync, Retries and ItemLocks paths.
/// </summary>
public static class KnownBugChecks
{
    // ---- Real assertions ------------------------------------------------

    /// <summary>B-207: with a saved binding and both inventories complete and empty, terminal absence is
    /// proven and the binding is forgotten through ordinary compare, not held and re-probed.</summary>
    public static Task B207BothSidesGoneAsync() =>
        ReconciliationChecks.CompareAsync(true, [], [], [], [], 0, []);

    /// <summary>B-223: an untagged HTTP 500 on upload-session start carries no proof of no effect. A dispatched
    /// write cannot be resent without that proof, so the failure stays UnknownEffect (no unsafe
    /// resend) rather than a temporary in-place retry. The host's normal later retry is a separate, later opportunity.</summary>
    public static Task B223Untagged500UploadSessionAsync()
    {
        using var body = JsonDocument.Parse("{}");
        var classified = new DropboxErrors().Classify(HttpStatusCode.InternalServerError, body.RootElement,
            DropboxProtocol.UploadSessionStart, expectedIsNull: false,
            new DropboxErrors.RequestEvidence(false, true, false, false));
        Check.Require(classified.Disposition == FailureDisposition.UnknownEffect && !classified.ProvenNoEffect,
            $"Untagged 500 on upload_session/start settled as {classified.Disposition}/{classified.Cause}; " +
            "a 500 is a failure, and a dispatched write is never resent without proof of no effect.");
        return Task.CompletedTask;
    }

    /// <summary>B-230: current member subjects persist enum names and reject integer ordinals, so reordering
    /// an enum cannot silently reinterpret a stored subject (product BC8; Encoding.cs:52-94).</summary>
    public static Task B230ReorderedEnumRejectsOrdinalsAsync()
    {
        var subject = new IssueSubject(new[] { new NoticeItem(Side.Dropbox, "a", "i") }, null);
        var encoded = Codec.Envelope(subject);
        Check.Require(encoded.Contains("\"Dropbox\"", StringComparison.Ordinal) &&
            !encoded.Contains("\"Side\":1", StringComparison.Ordinal),
            "The member envelope must persist enum names, not ordinals that a reorder would reinterpret");
        var decoded = Codec.DecodeMemberSubject(encoded);
        Check.Require(decoded.Items.Single().Side == Side.Dropbox, "Named-enum member envelope did not round trip");
        try { Codec.DecodeMemberSubject("""{"Version":1,"Value":{"Items":[{"Side":1,"Path":"a","Id":"i"}],"Incoming":null}}"""); }
        catch (InvalidDataException) { return Task.CompletedTask; }
        throw new InvalidDataException("A numeric enum ordinal was accepted for a current member subject");
    }

    /// <summary>L122: an extremely long provider Retry-After is waited in supported timer chunks and stays
    /// cancellable, instead of overflowing the delay API (EngineSettings.cs BackoffSettings.WaitAsync).</summary>
    public static async Task L122LongRetryAfterAsync()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await ItemChecks.Settings.Backoff.WaitAsync(TimeSpan.FromDays(3650), TimeProvider.System, cancelled.Token);
            throw new InvalidDataException("A long provider Retry-After did not observe cancellation");
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>B-212: a folder moved on one endpoint is carried across on its counterpart by identity, never
    /// recreated as an empty placeholder and never bound twice; reversing the direction behaves the same.</summary>
    public static async Task B212ReverseFolderMoveAsync()
    {
        foreach (var moved in new[] { Side.Local, Side.Dropbox })
        {
            var pair = ItemChecks.Pair();
            var lf = ItemChecks.File(Side.Local, "folder", 0, "local-folder", folder: true);
            var df = ItemChecks.File(Side.Dropbox, "folder", 0, "cloud-folder", folder: true);
            var saved = SyncedEntries.From(lf, df);
            using var store = new InMemoryStateStore();
            var local = new FakeSide(Side.Local);
            var remote = new FakeSide(Side.Dropbox);
            await store.SavePairAsync(pair, default);
            await store.CommitAsync(pair.PairId, null, saved, null, null, default);
            var movedEntry = moved == Side.Local ? lf with { Path = "folder-moved" } : df with { Path = "folder-moved" };
            if (moved == Side.Local) { local.Seed(movedEntry, []); remote.Seed(df, []); }
            else { local.Seed(lf, []); remote.Seed(movedEntry, []); }
            var paths = new PathScope(pair, [pair], DriveType.Fixed);
            var decision = await MoveDecision.DecideMoveAsync(pair, ItemChecks.Settings, paths,
                new[] { new RenameCandidate(moved, movedEntry, saved) }, local, remote, store, false, default);
            var expect = moved == Side.Local ? ActionKind.MoveDropbox : ActionKind.MoveLocal;
            Check.Require(decision.Kind == expect, $"{moved}: the one-sided folder move decided {decision.Kind} instead of {expect}");
            var outcome = await new Effects().ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, decision, local, remote, store,
                (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask, default);
            Check.Require(outcome == EffectOutcome.Applied, $"{moved}: the counterpart folder move was not applied");
            var binding = store.Records.Values.SingleOrDefault();
            Check.Require(store.Records.Count == 1 && binding is { IsFolder: true, LocalPath: "folder-moved", DropboxPath: "folder-moved" } &&
                binding!.LocalId == "local-folder" && binding!.DropboxId == "cloud-folder",
                $"{moved}: the moved folder was not bound once at the new name with both identities");
            Check.Require(local.Items.Count == 1 && local.Items["folder-moved"].Entry.Id == "local-folder" &&
                remote.Items.Count == 1 && remote.Items["folder-moved"].Entry.Id == "cloud-folder",
                $"{moved}: an empty placeholder or a duplicate folder was left at the old name");
            Check.Require(local.Requests[nameof(ISide.DeleteAsync)] == 0 && remote.Requests[nameof(ISide.DeleteAsync)] == 0 &&
                local.Requests[nameof(ISide.PublishAsync)] == 0 && remote.Requests[nameof(ISide.PublishAsync)] == 0,
                $"{moved}: the folder was deleted or recreated instead of moved");
        }
    }

    /// <summary>B-213: a saved folder identity that lives at another name must never authorize deleting the live
    /// folder; the stale binding selects a move, not a removal.</summary>
    public static async Task B213StaleFolderBindingAsync()
    {
        var pair = ItemChecks.Pair();
        var lf = ItemChecks.File(Side.Local, "orig", 0, "local-folder", folder: true);
        var df = ItemChecks.File(Side.Dropbox, "orig", 0, "cloud-folder", folder: true);
        var saved = SyncedEntries.From(lf, df);
        using var store = new InMemoryStateStore();
        var local = new FakeSide(Side.Local);
        var remote = new FakeSide(Side.Dropbox);
        await store.SavePairAsync(pair, default);
        await store.CommitAsync(pair.PairId, null, saved, null, null, default);
        local.Seed(lf, []);
        // Dropbox renamed the folder; the old name is absent while the identity is alive at the new one.
        var movedD = df with { Path = "moved" };
        remote.Seed(movedD, []);
        var paths = new PathScope(pair, [pair], DriveType.Fixed);
        var decision = await MoveDecision.DecideMoveAsync(pair, ItemChecks.Settings, paths,
            new[] { new RenameCandidate(Side.Dropbox, movedD, saved) }, local, remote, store, false, default);
        Check.Require(decision.Kind == ActionKind.MoveLocal,
            $"The stale binding decided {decision.Kind} instead of moving the live folder to its surviving name");
        var outcome = await new Effects().ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, decision, local, remote, store,
            (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask, default);
        Check.Require(outcome == EffectOutcome.Applied, "The stale folder binding's move was not applied");
        Check.Require(local.History.All(h => h.Entry.Id != "local-folder"),
            "The stale binding authorized a delete of the live folder that still exists at another name");
        Check.Require(store.Records.Count == 1 && store.Records.Values.Single() is { IsFolder: true, LocalPath: "moved", DropboxPath: "moved" },
            "The stale folder binding was not resolved to the surviving identity's single new name");
        Check.Require(local.Items.TryGetValue("moved", out var l) && l.Entry.Id == "local-folder" &&
            remote.Items.TryGetValue("moved", out var r) && r.Entry.Id == "cloud-folder",
            "The live folder was removed or recreated instead of moving with its identity");
        Check.Require(remote.Requests[nameof(ISide.DeleteAsync)] == 0 && local.Requests[nameof(ISide.DeleteAsync)] == 0,
            "The stale binding produced a delete on a live folder");
    }

    public static Task B255PathOnlyDeleteUnderRenameAsync() => global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async ct =>
    {
        await FolderChecks.PathOnlyDeleteUnderRenameAsync(false, ct);
        await FolderChecks.PathOnlyDeleteUnderRenameAsync(true, ct);
        await FolderChecks.PathOnlyDeleteUnderRenameAsync(null, ct);
        await FolderChecks.PathOnlyDeleteUnderRenameAsync(false, ct, sameFile: true);
        await FolderChecks.PathOnlyDeleteUnderRenameAsync(true, ct, sameFile: true);
        await FolderChecks.B255ScopedSettlementAsync();
    });

    /// <summary>L61: a child's in-use Failure notice names the concrete failed endpoint; once the parent folder is
    /// successfully removed the superseded child notice must close, and an unrelated change must still sync.</summary>
    public static async Task L61ChildInUseNoticeAfterParentAsync()
    {
        var pair = ItemChecks.Pair();
        var parentL = ItemChecks.File(Side.Local, "folder", 0, "pc-folder", folder: true);
        var parentD = ItemChecks.File(Side.Dropbox, "folder", 0, "db-folder", folder: true);
        var childL = ItemChecks.File(Side.Local, "folder/child", 1, "pc-child");
        var childD = ItemChecks.File(Side.Dropbox, "folder/child", 1, "db-child");
        var sibling = ItemChecks.File(Side.Local, "sibling", 2, "pc-sibling");
        var parentBinding = SyncedEntries.From(parentL, parentD);
        var childBinding = SyncedEntries.From(childL, childD);
        var (paths, queue, store, local, remote) = await BeginAsync(pair, true, async (s, p, d) =>
        {
            await s.CommitAsync(pair.PairId, null, parentBinding, null, null, default);
            await s.CommitAsync(pair.PairId, null, childBinding, null, null, default);
            p.Seed(parentL, []);
            p.Seed(childL, [1]);
            p.Seed(sibling, [2]);
            // The child's own removal failed in use; its concrete single-endpoint Failure notice is still open.
            await s.SaveNoticeAsync(pair.PairId, new Notice(0, NoticeKind.Failure, TextKey.FailLocalInUse, "folder/child",
                new([new(Side.Local, "folder/child", "pc-child")], Side.Local), DateTimeOffset.UnixEpoch, false), default);
        });
        // The parent is then removed successfully: both sides and their bound rows are gone.
        local.Items.Remove("folder");
        local.Items.Remove("folder/child");
        remote.Items.Remove("folder");
        await store.CommitAsync(pair.PairId, parentBinding, null, null, null, default);
        await store.CommitAsync(pair.PairId, childBinding, null, null, null, default);
        await RepairOnceAsync(pair, paths, queue, local, remote, store);
        Check.Require(!store.OpenNotices.Any(n => n.Kind == NoticeKind.Failure && n.Path == "folder/child"),
            "The child's in-use Failure notice survived the successful removal of its parent");
        Check.Require(store.Records.Values.All(r => r.LocalPath != "folder" && r.LocalPath != "folder/child"),
            "A removed parent or child binding survived");
        Check.Require(remote.Items.TryGetValue("sibling", out var synced) && synced.Bytes.SequenceEqual(new byte[] { 2 }),
            "An ordinary unrelated change stopped syncing after the folder removal");
    }

    /// <summary>L109: a crash after the counterpart move but before its save leaves the physical move applied and
    /// unsaved; a restart resolves it to one binding at the new name without losing or duplicating the version.</summary>
    public static async Task L109CrashAfterMoveBeforeSaveAsync()
    {
        var pair = ItemChecks.Pair();
        const string to = "renamed";
        var lf = ItemChecks.File(Side.Local, "item", 1, "pc-item");
        var df = ItemChecks.File(Side.Dropbox, "item", 1, "cloud-item");
        var saved = SyncedEntries.From(lf, df);
        using var store = new InMemoryStateStore();
        var local = new FakeSide(Side.Local);
        var remote = new FakeSide(Side.Dropbox);
        await store.SavePairAsync(pair, default);
        await store.CommitAsync(pair.PairId, null, saved, null, null, default);
        var movedL = lf with { Path = to };
        local.Seed(movedL, [1]);  // Local already renamed
        remote.Seed(df, [1]);     // Dropbox still at the old name
        var paths = new PathScope(pair, [pair], DriveType.Fixed);
        var decision = await MoveDecision.DecideMoveAsync(pair, ItemChecks.Settings, paths,
            new[] { new RenameCandidate(Side.Local, movedL, saved) }, local, remote, store, false, default);
        Check.Require(decision.Kind == ActionKind.MoveDropbox, "L109: the one-sided move did not select the counterpart move");
        // Crash the save after the physical counterpart move, before the binding is recorded.
        var crashed = false;
        var storeCrash = FolderDeleteChecks.Observe.Wrap<IStore>(store, (op, args) =>
        {
            if (op == nameof(IStore.CommitAsync) && !crashed) { crashed = true; throw new IOException("simulated crash after the physical move, before the save"); }
            return null;
        });
        try { await new Effects().ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, decision, local, remote, storeCrash,
            (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask, default); }
        catch (Exception error) when (error is IOException or AggregateException) { }
        Check.Require(crashed && remote.Items.ContainsKey(to) && store.Records.Values.All(r => r.LocalPath != to),
            "L109: the crash cut did not apply the physical counterpart move before the save");
        // Restart sees both identities at the new name and records the single binding.
        var restart = await MoveDecision.DecideMoveAsync(pair, ItemChecks.Settings, paths,
            new[] { new RenameCandidate(Side.Local, movedL, saved), new RenameCandidate(Side.Dropbox, remote.Items[to].Entry, saved) },
            local, remote, store, false, default);
        Check.Require(restart.Kind == ActionKind.Record, $"L109: restart decided {restart.Kind} instead of recording the applied move");
        var outcome = await new Effects().ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, restart, local, remote, store,
            (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask, default);
        Check.Require(outcome == EffectOutcome.Applied && store.Records.Count == 1, "L109: restart did not converge on one binding");
        var binding = store.Records.Values.Single();
        Check.Require(binding.LocalPath == to && binding.DropboxPath == to && binding.LocalId == "pc-item" && binding.DropboxId == "cloud-item",
            "L109: restart lost the moved identity or its single new name");
        Check.Require(local.Items.Count == 1 && remote.Items.Count == 1 && local.Items[to].Entry.Id == "pc-item" && remote.Items[to].Entry.Id == "cloud-item",
            "L109: the crash lost or duplicated a file version");
        Check.Require(local.Requests[nameof(ISide.PublishAsync)] == 0 && remote.Requests[nameof(ISide.PublishAsync)] == 0,
            "L109: restart recreated the moved file instead of recognizing the applied move");
    }

    /// <summary>L110/L115: an in-use removal fault records a concrete endpoint subject; on retry it resolves through
    /// ordinary comparison against the recorded side, never deleting the wrong side or throwing on an empty subject.</summary>
    public static async Task L110RetryInUseWrongSideAsync()
    {
        var pair = ItemChecks.Pair();
        var pcFile = ItemChecks.File(Side.Local, "item", 1, "pc-item");
        var dbFile = ItemChecks.File(Side.Dropbox, "item", 1, "cloud-item");
        var other = ItemChecks.File(Side.Dropbox, "other", 3, "other-id");
        var (paths, queue, store, local, remote) = await BeginAsync(pair, true, async (s, p, d) =>
        {
            await s.CommitAsync(pair.PairId, null, SyncedEntries.From(pcFile, dbFile), null, null, default);
            p.Seed(pcFile, [1]);
            d.Seed(dbFile, [1]);
            d.Seed(other, [3]);
        });
        var inUse = new ItemFailedException("item", TextKey.FailLocalInUse, null);
        var refusing = FolderDeleteChecks.Observe.On(local, (op, args) =>
        {
            if (op == nameof(ISide.DeleteAsync) && ((Entry)args[0]!).Path == "item") throw inUse;
            return null;
        });
        remote.Items.Remove("item"); // Dropbox reported the deletion the Local removal failed to apply.
        await ItemSync.ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths,
            new Change(Side.Dropbox, ChangeKind.Deleted, "item", null, dbFile.Id, null, false, false),
            refusing, remote, store, ItemChecks.Population(pair, paths, refusing, remote, store),
            ItemChecks.Hop(pair, paths, queue, refusing, remote, store), default, heldInput: _ => [], coversThrough: _ => true);
        var failure = store.OpenNotices.SingleOrDefault(n => n.Kind == NoticeKind.Failure && n.Path == "item");
        Check.Require(failure != null, "An in-use removal fault raised no Failure notice");
        Check.Require(failure!.Subject.Items.Any(i => i.Side == Side.Local && i.Path == "item" && i.Id == "pc-item"),
            "The in-use Failure notice lost the concrete Local subject that names the correct side");
        // The retry settles the ordinary comparison against a real acquisition fence.
        var mark = new JournalPosition(1, 42);
        local.Pages.Enqueue(new ChangePage([], null, mark));
        remote.Pages.Enqueue(new ChangePage([], "complete", null));
        bool Covered((JournalPosition? Local, string? Dropbox) fence) => fence.Local == mark && fence.Dropbox == "complete";
        await new Retries(new Unfinished()).RetryAsync(pair, ItemChecks.Settings, _ => { }, paths, failure!, local, remote, store,
            _ => [], ItemChecks.Population(pair, paths, local, remote, store), ItemChecks.Hop(pair, paths, queue, local, remote, store),
            default, coversThrough: Covered);
        Check.Require(remote.Requests[nameof(ISide.DeleteAsync)] == 0,
            "Retry deleted the Dropbox peer instead of the side named by the in-use subject");
        // The safety property: the version the in-use notice protected is still somewhere (live or recycled), and
        // the retry neither threw on an empty subject nor destroyed an unrelated item.
        Check.Require(local.Items.ContainsKey("item") || local.History.Any(h => h.Entry.Id == "pc-item"),
            "The version the in-use notice protected ended up nowhere after Retry");
        Check.Require(remote.Items.TryGetValue("other", out var kept) && kept.Bytes.SequenceEqual(new byte[] { 3 }),
            "Retry disturbed an unrelated item");
    }

    /// <summary>L116: a local version whose Dropbox name is in deleted history but whose deletion proof is missing is
    /// never deleted; the ordinary first-run change still reaches Dropbox, so no version ends up nowhere.</summary>
    public static async Task L116MissingDeletionEvidenceAsync()
    {
        var pair = ItemChecks.Pair();
        var file = ItemChecks.File(Side.Local, "item", 1, "pc-item");
        var (paths, queue, store, local, remote) = await BeginAsync(pair, false, (s, p, d) =>
        {
            p.Seed(file, [1]);
            // The Dropbox name is in deleted history, but there is no coherent evidence the deletion happened.
            d.DeletedHistory["item"] = new DeletionEvidence.NoRetainedHistory("item");
            return Task.CompletedTask;
        });
        await RepairOnceAsync(pair, paths, queue, local, remote, store);
        Check.Require(local.Items.TryGetValue("item", out var kept) && kept.Bytes.SequenceEqual(new byte[] { 1 }),
            "A local version without matching deletion evidence was removed (a file version ended up nowhere)");
        Check.Require(remote.Items.TryGetValue("item", out var copy) && copy.Bytes.SequenceEqual(new byte[] { 1 }),
            "The ordinary first-run change did not reach Dropbox once the deletion proof was missing");
    }

    /// <summary>L117: while a folder move holds its mapped subtree, an AutoSync touch of a child is excluded; an
    /// independent item still proceeds, so the move cannot race a destructive child effect.</summary>
    public static async Task L117AutoSyncTouchesChildDuringRepairMoveAsync()
    {
        var pair = ItemChecks.Pair();
        var localFolder = ItemChecks.File(Side.Local, "parent", 0, "pc-parent", folder: true);
        var cloudFolder = ItemChecks.File(Side.Dropbox, "parent", 0, "db-parent", folder: true);
        var row = new Row("parent", localFolder, cloudFolder, SyncedEntries.From(localFolder, cloudFolder), [], null, RowContext.Neutral);
        var move = new SyncAction(ActionKind.MoveLocal, row, Target: "moved");
        var locks = new ItemLocks();
        using (await locks.AcquireAsync(pair, move, [], default))
        {
            using var stop = new CancellationTokenSource();
            var child = locks.AcquireAsync([(Side.Local, ItemLocks.Physical(pair, Side.Local, "parent/child"), false)], stop.Token);
            var excluded = !child.IsCompletedSuccessfully;
            if (excluded) stop.Cancel();
            try { using var granted = await child; } catch (OperationCanceledException) { }
            Check.Require(excluded, "A child of the moving folder was acquirable while the parent move held its footprint");
            using var independent = await locks.AcquireAsync([(Side.Local, ItemLocks.Physical(pair, Side.Local, "independent"), false)], default);
        }
    }

    /// <summary>Restart at the physical exclusion delete, before its binding retirement.</summary>
    public static Task L119CrashAfterExclusionRemovalAsync() => global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async ct =>
    {
        var dir = Directory.CreateTempSubdirectory();
        var image = Path.Combine(dir.FullName, "fixture.sqlite");
        var exclusion = new Exclusion(Side.Dropbox, "Photos", false);
        var scope = new Scope("Pics", exclusion.Path);
        var pair = BoundPair("mapped", @"T:\Pair A", 1) with { Exclusions = [exclusion] };
        var pc = new FakeSide(Side.Local) { Root = (pair.Roots!.Local, null, null, DriveType.Fixed) };
        var db = new FakeSide(Side.Dropbox) { Root = (null, pair.Roots.Dropbox, null, null) };
        var folder = ItemChecks.File(Side.Local, scope.LocalPath, 0, "pc-folder", true);
        var peer = ItemChecks.File(Side.Dropbox, scope.DropboxPath, 0, "db-folder", true);
        var file = ItemChecks.File(Side.Local, "Pics/a", 1, "pc-file");
        var mate = ItemChecks.File(Side.Dropbox, "Photos/a", 1, "db-file");
        pc.Seed(folder, []); pc.Seed(file, [1]); db.Seed(peer, []); db.Seed(mate, [1]);
        var saved = SyncedEntries.From(file, mate);
        try
        {
            await CallBudgetChecks.CreateStoreImageAsync(image);
            using (var before = new global::Massdex.SyncShark.Engine.Storage.Store(image, false, ItemChecks.Settings))
            {
                await before.SavePairAsync(pair, ct);
                Check.Require(await before.FinishRepairAsync(pair.PairId, (0, 0), "{}", ct) is (true, true), "Missing completed baseline");
                await before.SavePairAsync(pair, ct, [(pair.PairId, scope)]);
                await before.CommitAsync(pair.PairId, null, SyncedEntries.From(folder, peer), null, null, ct);
                await before.CommitAsync(pair.PairId, null, saved, null, null, ct);
                // Interrupt the endpoint after it removed the selected tree, before its caller can commit the receipt.
                var cut = new OperationCanceledException("cut after physical deletion");
                db.AfterDelete = entry => { if (entry == peer) throw cut; };
                pc.IdleBoundary = ItemChecks.Journal([], 1); db.IdleBoundary = new([], "before", null);
                var excluded = (await before.PairAsync(pair.PairId, ct))!;
                try
                {
                    var result = await new Exclusions().ApplyAsync(excluded, ItemChecks.Settings, _ => { }, new PathScope(excluded, [excluded], DriveType.Fixed),
                        exclusion, pc, db, before, (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask, ct, _ => [], _ => true);
                    throw new InvalidDataException($"Exclusion returned {result} without reaching the physical deletion");
                }
                catch (OperationCanceledException e) when (ReferenceEquals(e, cut)) { }
                finally { db.AfterDelete = null; pc.IdleBoundary = null; db.IdleBoundary = null; }
                Check.Require(db.Items.Count == 0 && db.History.Any(v => v.Entry == mate && v.Bytes.SequenceEqual(new byte[] { 1 })),
                    "Crash occurred before the selected folder and file were removed");
            }
            var missingListingFailed = false;
            try { await foreach (var row in db.ReadAllAsync(peer.Path, new(true, true), ct)) throw new InvalidDataException("Removed folder yielded an entry"); }
            catch (InventoryIncompleteException) { missingListingFailed = true; }
            Check.Require(missingListingFailed, "Missing folder was silently listed as an empty folder");
            var store = new global::Massdex.SyncShark.Engine.Storage.Store(image, false, ItemChecks.Settings);
            var host = new Host((_, _) => Task.FromResult<IStore?>(store), (_, _, _, _) => (pc, db), ItemChecks.Settings,
                new global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.MemoryLogs(), TimeProvider.System, _ => Task.FromResult(""), (_, _) => Task.CompletedTask);
            try
            {
                Check.Require(await store.FindAsync(pair.PairId, Side.Local, saved.LocalKey, ct) == saved, "Crash cut did not retain the original file binding");
                var result = await host.SetExclusionAsync(pair.PairId, exclusion, true, false, ct);
                var included = (await store.PairAsync(pair.PairId, ct))!;
                Check.Require(result.Outcome == ExclusionOutcome.Applied && included.OwedScopes is {Count: 1} owed && owed[0] == scope,
                    "Re-inclusion lost the mapped PC scope after retiring the old Dropbox bindings");
                var paths = new PathScope(included, [included], DriveType.Fixed);
                var queue = await ItemChecks.StartQueueAsync(included, paths, pc, db, store);
                await Repair.RunAsync(included, ItemChecks.Settings, _ => { }, paths, queue, pc, db, store, scope, null, ct);
                Check.Require(pc.History.Count == 0 && pc.Items[file.Path].Entry == file && pc.Items[file.Path].Bytes.SequenceEqual(new byte[] { 1 }),
                    "Re-inclusion recycled the original PC version");
                Check.Require(await store.FindAsync(pair.PairId, Side.Local, saved.LocalKey, ct) is {} binding && binding.DropboxId != mate.Id &&
                    db.Items.TryGetValue(binding.DropboxPath, out var restored) && restored.Entry.Id == binding.DropboxId && restored.Bytes.SequenceEqual(new byte[] { 1 }),
                    "Scoped Repair did not restore and bind the kept bytes");
                Check.Require((await store.PairAsync(pair.PairId, ct))!.OwedScopes is not {Count: > 0}, "Scoped Repair did not settle re-inclusion");
            }
            finally { await host.StopAsync(default); }
        }
        finally { dir.Delete(true); }
    });

    // ---- Remaining DOUBLE / NI stubs with the gap -----------------------

    public static Task B200TempReplaceBudgetAsync() => throw new NotImplementedException(
        "MISSING CAPABILITY (not closed): a change source that scripts a temp-file write followed by a rename-over, so " +
        "the superseded remote read can be counted, is not present; the doubles expose terminal entries only and the " +
        "A26 safe-save fixture it names is itself still not implemented. Recommended disposition for designer approval: " +
        "build the temp-write/rename-over change source first (A26).");

    public static Task B211MalformedFrameListenerAsync() => throw new NotImplementedException(
        "LIVE-ONLY (RS-23): the malformed-frame-then-good-client listener check needs an authenticated named-pipe host " +
        "(HostServer.AuthenticatedPeer requires the signed product image); verification is pending under rehearsal step RS-23.");

    public static Task B214CWakeBehindEchoAsync() => throw new NotImplementedException(
        "MISSING CAPABILITY (not closed): the entry/tombstone echo budget checks now pass, but a fixture that replenishes " +
        "a folder-move echo prefix through AutoSync and proves an independent wake completes before the backlog clears " +
        "has not been built; the echo budget checks drive single changes, not a gated folder backlog. Recommended " +
        "disposition for designer approval: build the gated folder-echo backlog fixture before treating this as covered.");

    public static Task B229ScopedJournalRepairAsync() => throw new NotImplementedException(
        "PRODUCT-MISSING (closed): B-229-ii is not a defect (product decision); no scoped Repair path is required.");

    /// <summary>B-231: an unrelated successful sync of a different identity at a move notice's path must not close that
    /// notice. The close matches the compared identity (ItemSync.CloseComparedNoticesAsync), never the path alone.</summary>
    public static Task B231UnrelatedEffectKeepsMoveNoticeAsync() =>
        global::Massdex.SyncShark.Engine.Tests.Run.RepairChecks.WithRepairStore(async (store, pair, ct) =>
        {
            var local = new FakeSide(Side.Local);
            var remote = new FakeSide(Side.Dropbox);
            var paths = new PathScope(pair, [pair], DriveType.Fixed);
            // The original notice is the designed unknown-outcome move review, saved in the real SQLite store.
            var move = new IssueSubject.UnknownMove("original-id", "item", "target", null, false);
            var subject = new IssueSubject([new(Side.Dropbox, "item", "original-id")], Side.Dropbox) { Move = move };
            await store.SaveNoticeAsync(pair.PairId,
                new Notice(0, NoticeKind.Failure, TextKey.FailDropboxOutcomeUnknown, "item", subject, DateTimeOffset.UnixEpoch, false), ct);
            // The exact stored review, read back before the unrelated effect.
            Notice? before = null;
            await foreach (var notice in store.NoticesAsync(pair.PairId, ct))
                if (notice.Kind == NoticeKind.Failure && notice.Path == "item") before = notice;
            Check.Require(before is not null && before.Subject.Move == move,
                "B-231: the fixture did not persist the designed unknown-outcome move review");
            // A different identity arrives at the same path through the ordinary ItemSync comparison and syncs.
            var incoming = ItemChecks.File(Side.Dropbox, "item", 2, "other-id");
            remote.Seed(incoming, [2]);
            var change = new Change(Side.Dropbox, ChangeKind.Changed, "item", null, incoming.Id, incoming, false, false);
            await ItemSync.ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, change, local, remote, store,
                (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask, ct);
            // The unrelated bytes arrived...
            Check.Require(local.Items.TryGetValue("item", out var arrived) && arrived.Bytes.SequenceEqual(new byte[] { 2 }),
                "B-231: the different identity at the move notice's path did not sync");
            // ...and the exact original review is still open, identity and move evidence unchanged, never path-matched.
            var open = new List<Notice>();
            await foreach (var notice in store.NoticesAsync(pair.PairId, ct)) open.Add(notice);
            var kept = open.SingleOrDefault(n => n.Path == "item" && n.Kind == NoticeKind.Failure);
            Check.Require(kept is not null && kept.Matches(before!),
                "B-231: an unrelated successful sync changed or closed the exact original review (B-231)");
        });

    /// <summary>A newer Dropbox delete at a name whose re-run first Repair already consumed a delete must win, so the
    /// restored PC copy is recycled again. When the only delete there is the one already consumed, the restored copy is
    /// kept and uploaded with no edit notice. The spent-name mask survives a drive-change FirstSync reset.</summary>
    public static Task SpentDeletedNameAsync(bool newerDropboxDelete) =>
        global::Massdex.SyncShark.Engine.Tests.Run.RepairChecks.WithRepairStore(async (store, pair, ct) =>
        {
            // Declared roots and a saved drive serial let a detected drive change invalidate deleted history.
            pair = pair with
            {
                Drive = new DriveEvidence(1, 2, 10),
                Roots = new RootIdentity(pair.LocalRoot, pair.DropboxRoot, pair.ConnectionId,
                    new DirectoryIdentity(1, 2, null), new DropboxRootIdentity("account", "root", "home", "folder"))
            };
            await store.SavePairAsync(pair, ct);
            var pc = new FakeSide(Side.Local);
            var cloud = new FakeSide(Side.Dropbox) { ClockOffset = (TimeSpan.Zero, TimeSpan.Zero) };
            var paths = new PathScope(pair, [pair], DriveType.Fixed);
            var queue = await ItemChecks.StartQueueAsync(pair, paths, pc, cloud, store);
            const string path = "spent-file";

            // First Repair: the PC keeps X while Dropbox's head revision is deleted (D1), definitely later.
            var entry = ItemChecks.File(Side.Local, path, 1, "pc-x");
            pc.Seed(entry, [1]);
            cloud.DeletedHistory[path] = new DeletionEvidence.DeletedFile(
                path, "cloud", "prior-id", "r1", entry.Modified.AddDays(1), null, entry.ContentHash, null);
            FolderChecks.AdmitFullRepair(queue, pc, cloud);
            // The recycle checks the saved numeric volume key against the side's reported domain; the FakeSide reports
            // its own domain, so run the Repair without the saved root. The mask and decision paths do not read Roots.
            await Repair.RunAsync(pair with { Roots = null }, ItemChecks.Settings, _ => { }, paths, queue, pc, cloud, store, null, null, ct);
            // Setup: D1 was mirrored by a local recycle, spending the name and leaving no binding.
            Check.Require(!pc.Items.ContainsKey(path) && pc.History.Any(h => h.Entry == entry) && !cloud.Items.ContainsKey(path)
                && await store.FindAsync(pair.PairId, Side.Local, PathNames.Key(path), ct) is null
                && (await store.RepairStateAsync(pair.PairId, ct)).FirstRepairDone,
                "The first Repair did not recycle the PC copy on D1");

            // X is restored on the PC (same bytes, original modified time).
            pc.Items[path] = (entry, [1]);
            if (newerDropboxDelete)
                // Dropbox restores then deletes it again (D2, definitely later than the PC restore).
                cloud.DeletedHistory[path] = new DeletionEvidence.DeletedFile(
                    path, "cloud", "latest-id", "r2", entry.Modified.AddDays(2), null, entry.ContentHash, null);

            // One detected drive change resolved as FirstSync, then the full first Repair that re-acquires history.
            async Task FirstRepairAfterDriveChangeAsync(DriveEvidence drive, DirectoryIdentity localRoot)
            {
                var current = await store.PairAsync(pair.PairId, ct) ?? throw new InvalidDataException("The pair was not stored");
                Check.Require(!await new PairRunner().CheckRootsAsync(current, current.Roots, drive, store, ct),
                    "The drive change was admitted as current");
                Notice? change = null;
                await foreach (var notice in store.NoticesAsync(pair.PairId, ct))
                    if (notice.Kind == NoticeKind.DriveChange) change = notice;
                Check.Require(change is not null, "No DriveChange notice was persisted");
                var resolved = current with { Drive = drive, Roots = current.Roots! with { Local = localRoot } };
                Check.Require(await store.ResolveDriveAsync(resolved, change!, ResolutionChoice.FirstSync, ct),
                    "The FirstSync resolution was refused");
                FolderChecks.AdmitFullRepair(queue, pc, cloud);
                await Repair.RunAsync(resolved with { Roots = null }, ItemChecks.Settings, _ => { }, paths, queue, pc, cloud, store, null, null, ct);
            }

            async Task<bool> InfoEditNoticeAsync()
            {
                await foreach (var notice in store.NoticesAsync(pair.PairId, ct))
                    if (notice.Path == path && notice.Cause == TextKey.InfoEditBeatsDelete) return true;
                return false;
            }

            await FirstRepairAfterDriveChangeAsync(new DriveEvidence(2, 2, 10), new DirectoryIdentity(3, 2, null));

            if (!newerDropboxDelete)
            {
                // The only delete is the one already consumed: the restored copy is kept and uploaded, with no edit notice.
                Check.Require(pc.Items.TryGetValue(path, out var pcCopy) && pcCopy.Bytes.SequenceEqual(new byte[] { 1 }) &&
                    cloud.Items.TryGetValue(path, out var cloudCopy) && cloudCopy.Bytes.SequenceEqual(new byte[] { 1 }),
                    "The consumed deletion hid the kept copy: the restored file did not reach both sides");
                Check.Require(pc.History.Count(h => h.Entry == entry) == 1, "The consumed deletion recycled the restored copy again");
                Check.Require(!await InfoEditNoticeAsync(), "The kept restored copy raised an edit notice");
                return;
            }

            // The newer D2 must win: the restored PC copy is recycled again and never uploaded back to Dropbox.
            Check.Require(!pc.Items.ContainsKey(path) && !cloud.Items.ContainsKey(path),
                "The spent deleted-name mask hid the newer Dropbox delete D2; the restored PC copy was uploaded back instead of recycled");
            Check.Require(await store.ConsumedDeletionAsync(pair.PairId, PathNames.DropboxKey(path), ct) == entry.Modified.AddDays(2),
                "The newer Dropbox delete D2 was not recorded as the consumed deletion");
            Check.Require(pc.History.Count(h => h.Entry == entry && h.Bytes.SequenceEqual(new byte[] { 1 })) == 2,
                "The restored PC copy was not recycled on D2 with its bytes");

            // A third restore with D2 still the head is the consumed deletion once more: kept, uploaded, no third recycle.
            pc.Items[path] = (entry, [1]);
            await FirstRepairAfterDriveChangeAsync(new DriveEvidence(3, 2, 10), new DirectoryIdentity(4, 2, null));
            Check.Require(pc.Items.TryGetValue(path, out var restored) && restored.Bytes.SequenceEqual(new byte[] { 1 }) &&
                cloud.Items.TryGetValue(path, out var uploaded) && uploaded.Bytes.SequenceEqual(new byte[] { 1 }),
                "The consumed D2 hid the restored copy: the file did not reach both sides");
            Check.Require(pc.History.Count(h => h.Entry == entry && h.Bytes.SequenceEqual(new byte[] { 1 })) == 2,
                "The consumed D2 recycled the restored copy a third time");
            Check.Require(!await InfoEditNoticeAsync(), "The consumed D2 raised an edit notice");
        });

    /// <summary>B-240: a slow arrival flood must not take the opportunity from small unrelated arrivals; each of the five
    /// distinct small changes completes while the flood is still held, and the flood gate is released on every path.</summary>
    public static Task B240ArrivalFloodOpportunityAsync() =>
        global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async ct =>
        {
            var queue = new WorkQueue();
            var pair = ItemChecks.Pair();
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // The 128-entry page shape already used in Run/WorkQueueChecks, but 128 distinct new file paths (the slow flood).
            var flood = Enumerable.Range(0, 128)
                .Select(i => new Change(Side.Dropbox, ChangeKind.Changed, $"flood/{i}", null, $"flood{i}", null, false, false)).ToArray();
            var floodPage = queue.AdmitAsync(pair, new(flood, "flood", null), (_, _) => hold.Task, null, ct);
            // The bug report's shape: five small unrelated arrivals, each identified by its own change.
            var small = Enumerable.Range(0, 5)
                .Select(i => new Change(Side.Dropbox, ChangeKind.Changed, $"small/{i}", null, $"small{i}", null, false, false)).ToArray();
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            var smallSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var smallPage = queue.AdmitAsync(pair, new(small, "small", null),
                (change, _) => { lock (distinct) { distinct.Add(change.Path!); if (distinct.Count == small.Length) smallSignal.TrySetResult(); } return Task.CompletedTask; },
                null, ct);
            try
            {
                try { await smallSignal.Task.WaitAsync(ct); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new InvalidDataException("A small unrelated arrival waited behind an earlier arrival flood (B-240)"); }
                lock (distinct)
                    Check.Require(distinct.Count == small.Length && distinct.SetEquals(small.Select(s => s.Path!)),
                        "A duplicate dispatch satisfied the small-arrival count (B-240)");
                Check.Require(!hold.Task.IsCompleted, "A small unrelated arrival only ran once the flood was released (B-240)");
            }
            finally { hold.TrySetResult(); }
            await Task.WhenAll(floodPage, smallPage).WaitAsync(ct);
        });

    /// <summary>B-250: a foreign cancellation raised inside one pair's boundary wait must not end that pair or the host;
    /// the affected pair keeps retrying its loop and an unrelated pair still syncs, answers status and stops.
    /// Both pairs contend for the same provider slot.</summary>
    public static Task B250CapacityWaitCancellationAsync() =>
        global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async ct =>
        {
            var pairA = BoundPair("a", @"T:\A", 1);
            var pairB = BoundPair("b", @"T:\B", 2);
            using var store = new InMemoryStateStore();
            await store.SavePairAsync(pairA, ct);
            await store.SavePairAsync(pairB, ct);
            await store.FinishRepairAsync(pairA.PairId, (0, 0), "{}", ct);
            await store.FinishRepairAsync(pairB.PairId, (0, 0), "{}", ct);
            var limit = new RateLimit(1, ItemChecks.Settings.Backoff, _ => { });
            await using var occupied = await limit.AdmitAsync(ct);
            using var foreign = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var aQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var bQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var aBoundaries = new int[1];
            var aRetried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var bArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var bFile = ItemChecks.File(Side.Dropbox, "b", 7, "b-id");
            var sides = new Dictionary<string, (ISide Local, ISide Dropbox)>(StringComparer.Ordinal);
            global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.JournalProbe Probe(ISide side) =>
                (global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.JournalProbe)(object)side;
            // Pair A: its first boundary waits for an occupied provider slot; the pair must retry, not end.
            {
                var localSide = new FakeSide(Side.Local) { Root = (pairA.Roots!.Local, null, null, DriveType.Fixed) };
                var local = System.Reflection.DispatchProxy.Create<ISide, global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.JournalProbe>();
                var lp = Probe(local); lp.Quiet = true; lp.Local = localSide;
                var boundary = new JournalPosition(1, 1);
                localSide.Pages.Enqueue(new ChangePage([], null, boundary));
                lp.Boundary = async token => {
                    bool first = Interlocked.Increment(ref aBoundaries[0]) == 1;
                    var permit = limit.AdmitAsync(first ? foreign.Token : token);
                    if (first) { Check.Require(!permit.IsCompleted, "Pair A never queued for capacity"); aQueued.TrySetResult(); }
                    await using var granted = await permit;
                    if (first) throw new InvalidDataException("Canceled waiter acquired the occupied provider slot");
                    aRetried.TrySetResult();
                    return (null, boundary);
                };
                var remoteSide = new FakeSide(Side.Dropbox) { Root = (null, pairA.Roots.Dropbox, null, null) };
                var remote = System.Reflection.DispatchProxy.Create<ISide, global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.JournalProbe>();
                var rp = Probe(remote); rp.Quiet = true; rp.Local = remoteSide;
                sides[pairA.PairId] = (local, remote);
            }
            // Pair B: one page with a changed file "b" reaches its PC side.
            {
                var localSide = new FakeSide(Side.Local) { Root = (pairB.Roots!.Local, null, null, DriveType.Fixed) };
                localSide.AfterPublish = entry => { if (entry.Path == "b") bArrived.TrySetResult(); };
                var local = System.Reflection.DispatchProxy.Create<ISide, global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.JournalProbe>();
                var lp = Probe(local); lp.Quiet = true; lp.Local = localSide;
                var remoteSide = new FakeSide(Side.Dropbox) { Root = (null, pairB.Roots.Dropbox, null, null) };
                remoteSide.Seed(bFile, [7]);
                var remote = System.Reflection.DispatchProxy.Create<ISide, global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.JournalProbe>();
                var rp = Probe(remote); rp.Quiet = true; rp.Local = remoteSide;
                var served = new int[1];
                rp.Feed = async (_, token) =>
                {
                    if (Interlocked.Exchange(ref served[0], 1) == 0)
                    {
                        await aQueued.Task.WaitAsync(token);
                        var permit = limit.AdmitAsync(token);
                        Check.Require(!permit.IsCompleted, "Pair B never queued behind pair A");
                        bQueued.TrySetResult();
                        await using var granted = await permit;
                        return new ChangePage([new Change(Side.Dropbox, ChangeKind.Changed, bFile.Path, null, bFile.Id, bFile, false, false)], "b", null);
                    }
                    await Task.Delay(System.Threading.Timeout.Infinite, token);
                    throw new InvalidOperationException();
                };
                sides[pairB.PairId] = (local, remote);
            }
            var host = new Host((_, _) => Task.FromResult<IStore?>(store),
                (pair, _, _, _, _) => Task.FromResult(sides[pair.PairId]),
                ItemChecks.Settings, new global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.MemoryLogs(),
                TimeProvider.System, _ => Task.FromResult(""), (_, _) => Task.CompletedTask);
            try
            {
                await host.StartAsync(ct);
                await bQueued.Task.WaitAsync(ct);
                foreign.Cancel();
                await occupied.DisposeAsync();
                await bArrived.Task.WaitAsync(ct);
                var bLocal = Probe(sides[pairB.PairId].Local);
                Check.Require(bLocal.Local.Items.TryGetValue("b", out var received) && received.Bytes.SequenceEqual(new byte[] { 7 }),
                    "B-250: another pair stopped making progress while one pair's wait was canceled");
                var status = await host.StatusAsync(null, ct);
                Check.Require(status is not null, "B-250: the host stopped answering status while one pair's wait was canceled");
                // The affected pair's loop must retry past the foreign cancellation (its own retry delay is 1s) rather
                // than ending. Under the regression the loop faults after one boundary, so this wait never completes.
                await aRetried.Task.WaitAsync(ct);
                Check.Require(Volatile.Read(ref aBoundaries[0]) >= 2,
                    "B-250: a foreign cancellation inside one pair's wait ended that pair instead of retrying it (B-250)");
            }
            finally { await host.StopAsync(default); }
        });

    /// <summary>L60: held folder removals must not occupy a bounded continuation pool. More real endpoint removals than the
    /// engine's largest capacity (the Committer's StoreBatchRows slots) are left pending in flight; unrelated work still
    /// settles while they are pending, and every folder is removed on its own endpoint once released.</summary>
    public static Task L60FolderRemovalSaturationAsync() =>
        global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async ct =>
        {
            var queue = new WorkQueue();
            var pair = ItemChecks.Pair();
            // Derived, not invented: one more pending removal than the largest capacity the engine has.
            var held = ItemChecks.Settings.StoreBatchRows + 1;
            var removals = new Change[held];
            var removed = new bool[held];
            for (var i = 0; i < held; i++)
                removals[i] = new Change(Side.Dropbox, ChangeKind.Deleted, $"f{i}", null, $"id{i}", null, false, false);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = 0;
            async Task Apply(Change change, CancellationToken token)
            {
                await release.Task.WaitAsync(token);
                // One independent one-folder endpoint per deletion, rather than rescanning the other pending folders.
                var endpoint = new FakeSide(Side.Dropbox);
                var folder = ItemChecks.File(Side.Dropbox, change.Path!, 0, change.Id!, folder: true);
                endpoint.Seed(folder, []);
                await endpoint.DeleteAsync(folder, null, token);
                Check.Require(endpoint.Items.Count == 0 && endpoint.History.Single().Entry == folder, "Wrong folder removed");
                removed[int.Parse(change.Path!.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture)] = true;
                Interlocked.Increment(ref completed);
            }
            var page = queue.AdmitAsync(pair, new(removals, "removals", null), Apply, null, ct);
            // Unrelated work must settle while every folder removal is still pending (no room in a pool smaller than held).
            var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var unrelated = queue.AdmitAsync(pair,
                new([new Change(Side.Dropbox, ChangeKind.Changed, "unrelated", null, "unrelated-id", null, false, false)], "unrelated", null),
                (_, _) => { ran.TrySetResult(); return Task.CompletedTask; }, null, ct);
            try { await ran.Task.WaitAsync(ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new InvalidDataException("An unrelated change waited behind held folder removals (L60)"); }
            // Every held removal is still pending: none reached its endpoint before release.
            Check.Require(Volatile.Read(ref completed) == 0,
                "A held folder removal reached its endpoint before release (L60)");
            release.TrySetResult();
            await page.WaitAsync(ct);
            await unrelated.WaitAsync(ct);
            // The endpoint, not the input side, proves the removals completed.
            Check.Require(completed == held && removed.All(done => done),
                $"Not every held folder removal completed on its own endpoint ({completed}/{held}) (L60)");
        });

    /// <summary>L64/L100: a repeatedly refusing key that keeps receiving fresh same-key arrivals must not starve unrelated
    /// work, must not retry more often than its arrivals allow, and every fresh arrival must be applied exactly once once
    /// it is permitted - never a duplicate execution or a permanent retry of only the first attempt.</summary>
    public static Task L64ChurnOpportunityAsync() =>
        global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async ct =>
        {
            var queue = new WorkQueue();
            var pair = ItemChecks.Pair();
            var gate = new int[1]; // 0 = refusing; 1 = permitted
            const int arrivals = 4; // the bug report's four fresh overlapping arrivals on the same key
            var hotAttempts = 0;   // every apply attempt on the hot key, refused cancels and permitted applies alike
            var applied = new Dictionary<string, int>(StringComparer.Ordinal);
            void Apply(Change change)
            {
                Interlocked.Increment(ref hotAttempts);
                if (Volatile.Read(ref gate[0]) == 0) throw new WorkQueue.Unsettled();
                lock (applied) applied[change.Id!] = applied.GetValueOrDefault(change.Id!) + 1;
            }
            // The hot key is refused and retained, then keeps receiving fresh overlapping arrivals (same key, new news).
            var hot = new List<Task>(arrivals);
            var others = new List<Task>(arrivals);
            for (var r = 0; r < arrivals; r++)
            {
                var fresh = new Change(Side.Dropbox, ChangeKind.Changed, "hot", null, $"hot-{r}", null, false, false);
                hot.Add(queue.AdmitAsync(pair, new([fresh], $"h{r}", null), (c, _) => { Apply(c); return Task.CompletedTask; }, null, ct));
                var other = new Change(Side.Dropbox, ChangeKind.Changed, $"other{r}", null, $"other{r}", null, false, false);
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                others.Add(queue.AdmitAsync(pair, new([other], $"o{r}", null), (_, _) => { done.TrySetResult(); return Task.CompletedTask; }, null, ct));
                await done.Task.WaitAsync(ct);
                Check.Require(Volatile.Read(ref hotAttempts) <= 2 * hot.Count + 2, "Retries exceeded the evidence admitted so far");
            }
            lock (applied) Check.Require(applied.Count == 0, "A refused hot key ran while it was still refusing (L64/L100)");
            // Retry contract (WorkQueue.Attempt): each apply path is one attempt plus at most one immediate replay, and the
            // single held head is retried once per newer overlapping arrival, so attempts stay within two per arrival plus the
            // head's admission replay. A retry storm (the regression) is unbounded and exceeds this.
            Check.Require(Volatile.Read(ref hotAttempts) <= 2 * arrivals + 2,
                "A refused key was retried more often than its arrivals allow (L64/L100)");
            Volatile.Write(ref gate[0], 1);
            Check.Require(await queue.RetryHeldAsync(null, ct), "A refused key was dropped instead of retried (L64/L100)");
            await Task.WhenAll(hot).WaitAsync(ct);
            await Task.WhenAll(others).WaitAsync(ct);
            lock (applied)
            {
                // Exactly one successful execution per arrival id: a duplicate execution would push a count above one.
                Check.Require(applied.Count == arrivals &&
                    Enumerable.Range(0, arrivals).All(r => applied.GetValueOrDefault($"hot-{r}") == 1),
                    "Fresh news on a refused key was lost, duplicated, or only the original attempt was retried (L64/L100)");
            }
        });

    public static async Task L66SqlOrdinalsAndParserDepthsAsync()
    {
        await Storage.ScanTableChecks.ScanReaderColumnsAsync();
        using var input = new MemoryStream("""{"sharing_info":{"extra":{"nested":true},"shared_folder_id":"mount"},"path_display":"/Parent/Child","id":"one",".tag":"folder"}"""u8.ToArray());
        var listed = await DropboxReply.ReadAsync(input, DropboxReplyShapes.Listed(Dropbox.DropboxEntriesChecks.Pair), default);
        Check.Require(listed.Entry is { Id: "one", Path: "Child", SharedRoot: true }, "Streamed folder lost its direct shared-folder identity");
    }

    public static Task L84FolderScaleLookupsAsync() => throw new NotImplementedException(
        "L84: root-account read ownership; L94/L106: scoped Repair work at both populations; L113: unbound tombstone provider reads. " +
        "These are independent checks, not one folder-mapping fixture.");

    public static Task L87ScopeChangeAtIntakeAsync() => global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async ct =>
    {
        var pair = BoundPair("scope", @"T:\Scope", 1);
        using var store = new InMemoryStateStore(); await store.SavePairAsync(pair, ct); await store.FinishRepairAsync(pair.PairId, (0, 0), "{}", ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repaired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ISide Quiet(FakeSide endpoint)
        {
            var side = System.Reflection.DispatchProxy.Create<ISide, global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.JournalProbe>();
            var probe = (global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.JournalProbe)(object)side;
            probe.Local = endpoint; probe.Quiet = true; return side;
        }
        var pc = new FakeSide(Side.Local) { Root = (pair.Roots!.Local, null, null, DriveType.Fixed) };
        var db = new FakeSide(Side.Dropbox) { Root = (null, pair.Roots.Dropbox, null, null) };
        var local = Quiet(pc); var remote = Quiet(db);
        var file = ItemChecks.File(Side.Dropbox, "selected.sample", 1, "cloud-file");
        int read = 0;
        ((global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.JournalProbe)(object)remote).Feed = async (_, token) => {
            if (Interlocked.Increment(ref read) == 1)
            {
                entered.TrySetResult(); await send.Task.WaitAsync(token); db.Seed(file, [1]);
                return new([new(Side.Dropbox, ChangeKind.Changed, file.Path, null, file.Id, file, false, false)], "ignored", null);
            }
            await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException();
        };
        var ledger = System.Reflection.DispatchProxy.Create<IStore, global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.PublicationStore>();
        var tap = (global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.PublicationStore)(object)ledger; tap.Store = store;
        tap.CursorSaved = () => { if (store.CursorAsync(pair.PairId, default).GetAwaiter().GetResult() == "ignored") settled.TrySetResult(); };
        var host = new Host((_, _) => Task.FromResult<IStore?>(ledger), (_, _, _, _) => (local, remote), ItemChecks.Settings,
            new global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.MemoryLogs(), TimeProvider.System, _ => Task.FromResult(""), (_, _) => Task.CompletedTask);
        try
        {
            await host.StartAsync(ct); await entered.Task.WaitAsync(ct);
            await host.SetIgnoresAsync(pair.PairId, ["FileExtension:.sample"], ct); send.TrySetResult(); await settled.Task.WaitAsync(ct);
            Check.Require(!pc.Items.ContainsKey(file.Path), "An ignored arrival synced under the old scope");
            tap.Finished = () => repaired.TrySetResult();
            await host.SetIgnoresAsync(pair.PairId, [], ct); await repaired.Task.WaitAsync(ct);
            Check.Require(pc.Items.TryGetValue(file.Path, out var restored) && restored.Bytes.SequenceEqual(new byte[] { 1 }), "Lifting the ignore did not restore the file without manual Repair");
        }
        finally { send.TrySetResult(); await host.StopAsync(default); }
    });

    /// <summary>L98/L108: a child arriving before its parent folder, or a move into a counterpart parent that is removed
    /// after the move is decided but before it is applied, binds and moves by identity; the real providers create the
    /// missing parent (RS-59 confirms them).</summary>
    public static Task L98MissingMappedParentChildAsync() =>
        global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async ct =>
        {
            // (a) child arriving before its parent folder: nothing is bound yet.
            {
                var pair = ItemChecks.Pair();
                var (paths, queue, store, local, remote) = await BeginAsync(pair, true);
                var child = ItemChecks.File(Side.Dropbox, "newp/child", 1, "child-id");
                var parent = ItemChecks.File(Side.Dropbox, "newp", 0, "folder-id", folder: true);
                remote.Seed(parent, []);
                remote.Seed(child, [1]);
                var page = new ChangePage([
                    new Change(Side.Dropbox, ChangeKind.Changed, "newp/child", null, child.Id, child, false, false),
                    new Change(Side.Dropbox, ChangeKind.Changed, "newp", null, parent.Id, parent, false, false)], "c", null);
                await AutoSync.ApplyPageAsync(pair, ItemChecks.Settings, _ => { }, paths, queue, Side.Dropbox, page, local, remote, store, ct);
                Check.Require(local.Items.TryGetValue("newp/child", out var got) && got.Bytes.SequenceEqual(new byte[] { 1 }),
                    "A child arriving before its parent folder did not reach the PC (L98/L108)");
                var childBinding = store.Records.Values.SingleOrDefault(r => r.DropboxPath == "newp/child");
                Check.Require(childBinding is { LocalPath: "newp/child", DropboxPath: "newp/child", DropboxId: "child-id", LocalId: var lid } &&
                    lid == local.Items["newp/child"].Entry.Id,
                    "The child-before-parent binding did not record the exact paths and identities (L98/L108)");
                var parentBinding = store.Records.Values.SingleOrDefault(r => r.DropboxPath == "newp");
                Check.Require(parentBinding is { LocalPath: "newp", DropboxPath: "newp", DropboxId: "folder-id", IsFolder: true },
                    "The parent folder arriving after its child was not bound with its identity (L98/L108)");
                Check.Require(!store.OpenNotices.Any(n => n.Kind == NoticeKind.Failure), "A child before its parent raised a Failure notice (L98/L108)");
            }
            // (b) parent gone on the counterpart after the move is decided, before it is applied: the file still moves by
            // identity. The local and Dropbox folders are genuinely mapped (different spellings).
            {
                var pair = ItemChecks.Pair();
                var folderL = ItemChecks.File(Side.Local, "LocalF", 0, "lf-F", folder: true);
                var folderD = ItemChecks.File(Side.Dropbox, "RemoteF", 0, "df-F", folder: true);
                var xL = ItemChecks.File(Side.Local, "x", 1, "lf-x");
                var xD = ItemChecks.File(Side.Dropbox, "x", 1, "df-x");
                using var store = new InMemoryStateStore();
                var local = new FakeSide(Side.Local);
                var remote = new FakeSide(Side.Dropbox);
                await store.SavePairAsync(pair, default);
                await store.CommitAsync(pair.PairId, null, SyncedEntries.From(folderL, folderD), null, null, default);
                await store.CommitAsync(pair.PairId, null, SyncedEntries.From(xL, xD), null, null, default);
                local.Seed(folderL, []);
                var movedL = xL with { Path = "LocalF/x" };
                local.Seed(movedL, [1]);
                remote.Seed(folderD, []);
                remote.Seed(xD, [1]);
                var paths = new PathScope(pair, [pair], DriveType.Fixed);
                var decision = await MoveDecision.DecideMoveAsync(pair, ItemChecks.Settings, paths,
                    new[] { new RenameCandidate(Side.Local, movedL, SyncedEntries.From(xL, xD)) }, local, remote, store, false, ct);
                Check.Require(decision.Kind == ActionKind.MoveDropbox,
                    $"L98/L108: the file move into a mapped folder decided {decision.Kind} instead of moving the counterpart");
                // Remove the counterpart parent AFTER the move is decided and BEFORE it is applied, so the engine's own
                // admission checks saw the parent present while the effect runs with it gone.
                remote.Items.Remove(folderD.Path);
                Check.Require(!remote.Items.ContainsKey(folderD.Path),
                    "L98/L108: the fixture did not remove the counterpart parent before the effect");
                var outcome = await new Effects().ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths, decision, local, remote, store,
                    (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask, ct);
                Check.Require(outcome == EffectOutcome.Applied, "L98/L108: the file move was not applied when its counterpart parent vanished");
                Check.Require(remote.Items.TryGetValue("RemoteF/x", out var moved) && moved.Entry.Id == xD.Id && moved.Bytes.SequenceEqual(new byte[] { 1 }),
                    "L98/L108: the file was not moved by identity and bytes under the missing counterpart parent");
                var binding = store.Records.Values.SingleOrDefault(r => r.LocalPath == "LocalF/x");
                Check.Require(binding is { DropboxPath: "RemoteF/x", LocalId: "lf-x", DropboxId: "df-x" },
                    "L98/L108: the moved binding did not record the mapped destinations or identities");
                Check.Require(remote.Requests[nameof(ISide.PublishAsync)] == 0, "L98/L108: the file was re-uploaded instead of moved");
                Check.Require(!store.OpenNotices.Any(n => n.Kind == NoticeKind.Failure), "L98/L108: a missing counterpart parent raised a Failure notice");
            }
        });

    public static Task L101ExcludedDescendantFloodAsync() => throw new NotImplementedException(
        "MISSING CAPABILITY (not closed): the tombstone echo now passes, but a fixture that scripts excluded null-Id " +
        "tombstones for an excluded range after a folder move, while checking bounded reads and unrelated convergence, is " +
        "not built; the echo budget checks do not script exclusion-scoped tombstones. Recommended disposition for designer " +
        "approval: author the exclusion-scoped tombstone fixture.");

    public static Task L102MissingRootPointerAsync() => throw new NotImplementedException(
        "Deployment fixture F47 checks the missing-pointer guard; the engine does not discover a detached predecessor.");

    public static Task L107AcceptedJobPollingAsync() => throw new NotImplementedException(
        "MISSING CAPABILITY (not closed): evidence-based accepted-job checking without fixed polling needs a live job " +
        "opportunity gate plus Running/Terminal replies; the doubles script discrete JobReplies without a runnable " +
        "opportunity gate. Recommended disposition for designer approval: add the job-opportunity gate.");

    public static Task L112HighChurnTreeAsync() => throw new NotImplementedException(
        "MISSING CAPABILITY (not closed): a selected high-churn nested tree syncing without persistent refusals needs " +
        "sustained replenished churn, which the finite doubles cannot drive. L64 now proves the bounded-churn property at " +
        "the queue level, so this live-shape check is best proven by rehearsal step RS-53. Recommended disposition for " +
        "designer approval: rely on RS-53 for the live shape.");

    // ---- Shared scenario helpers ---------------------------------------

    /// <summary>Start a pair with the parity-checked in-memory store and the finite queue; the seed commits bindings
    /// and entries before the first source boundary is taken.</summary>
    private static async Task<(PathScope Paths, WorkQueue Queue, InMemoryStateStore Store, FakeSide Local, FakeSide Remote)>
        BeginAsync(PairConfig pair, bool firstRunDone, Func<InMemoryStateStore, FakeSide, FakeSide, Task>? seed = null)
    {
        var local = new FakeSide(Side.Local);
        var remote = new FakeSide(Side.Dropbox);
        var store = new InMemoryStateStore();
        await store.SavePairAsync(pair, default);
        if (firstRunDone)
            await store.FinishRepairAsync(pair.PairId, (0, await store.OweRepairAsync(pair.PairId, Side.Dropbox, "requested", default)), "{}", default);
        if (seed != null) await seed(store, local, remote);
        var paths = new PathScope(pair, [pair], DriveType.Fixed);
        var queue = await ItemChecks.StartQueueAsync(pair, paths, local, remote, store);
        return (paths, queue, store, local, remote);
    }

    private static Task RepairOnceAsync(PairConfig pair, PathScope paths, WorkQueue queue, FakeSide local, FakeSide remote, InMemoryStateStore store) =>
        Repair.RunAsync(pair, ItemChecks.Settings, _ => { }, paths, queue, local, remote, store, null, null, default);

    /// <summary>A bound pair with declared roots, matching HostChecks.Bound, for the two-pair Host fixture.</summary>
    private static PairConfig BoundPair(string id, string path, ulong serial)
    {
        var p = ItemChecks.Pair() with { PairId = id, LocalRoot = path, DropboxRoot = "/" + id };
        return p with { Roots = new(p.LocalRoot, p.DropboxRoot, p.ConnectionId, new(serial, 2, null), new("account", "root", "home", id)) };
    }
}
