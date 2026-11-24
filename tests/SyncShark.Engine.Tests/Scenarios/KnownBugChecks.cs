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
/// CI regression checks for the open-bug list.
/// Each named check asserts the correct behaviour the bug report and the product decisions require,
/// so it fails while the bug exists and passes once it is fixed. Rows that could not be exercised on the
/// F1 doubles are stubbed (NI/DOUBLE) with the gap named. No assertion is weakened to pass.
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
            var decision = await Effects.DecideMoveAsync(pair, ItemChecks.Settings, paths,
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
        var decision = await Effects.DecideMoveAsync(pair, ItemChecks.Settings, paths,
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

    /// <summary>B-255: a path-only delete under a renamed folder must follow the item, not leave the old-name
    /// child as an extra copy. Still blocked by the F1 harness, see the DOUBLE note.</summary>
    public static Task B255PathOnlyDeleteUnderRenameAsync() => throw new NotImplementedException(
        "DOUBLE: a path-only delete under a renamed folder arrives as a feed tombstone (Change.Id null) that the " +
        "listing-based Repair never reads; the F1 mapped-range-before-repath acquisition is only a stub " +
        "(CallBudgetChecks.MappedRangeBeforeRepathAsync throws). Honest coverage needs a feed fixture scripting the " +
        "ordered rename + path-only tombstone that the doubles do not have.");

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
        var decision = await Effects.DecideMoveAsync(pair, ItemChecks.Settings, paths,
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
        var restart = await Effects.DecideMoveAsync(pair, ItemChecks.Settings, paths,
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

    /// <summary>L119: a crash after a no-keep exclusion removal before commit must not recycle the copy meant to stay
    /// on the PC. Still unmodelled on the doubles, see the DOUBLE note.</summary>
    public static Task L119CrashAfterExclusionRemovalAsync() => throw new NotImplementedException(
        "DOUBLE: the exclusion path has no crash-cut injection at F1 and the cross-case receipt (crash after the " +
        "no-keep removal, before commit, then un-exclude) is not a state the F1 Exclusions double can hold; the " +
        "bug list itself records L119 as UNCLEAR. Honest coverage needs a durable store plus a fault point between " +
        "removal and commit that the doubles do not have.");

    // ---- Remaining DOUBLE / NI stubs with the gap -----------------------

    public static Task B200TempReplaceBudgetAsync() => throw new NotImplementedException(
        "DOUBLE: temp-and-replace refusal call budget needs a change source that scripts a temp-file write " +
        "followed by a rename-over; the doubles expose only terminal entries, so the superseded remote read cannot be counted.");

    public static Task B211MalformedFrameListenerAsync() => throw new NotImplementedException(
        "DOUBLE: the malformed-frame-then-good-client listener check needs an authenticated named-pipe host " +
        "(HostServer.AuthenticatedPeer requires the signed product image), which the doubles cannot provide.");

    public static Task B214CWakeBehindEchoAsync() => throw new NotImplementedException(
        "DOUBLE: non-starvation behind a folder-move echo backlog needs continuous echo pages; " +
        "F1 CloudEntryEcho/CloudTombstoneEcho are stubs.");

    public static Task B229ScopedJournalRepairAsync() => throw new NotImplementedException(
        "NI: scoped Repair for a verified isolatable journal loss is not implemented (Monitor.cs only separates a " +
        "verified reset from a failed read); there is no scoped-recovery path to assert.");

    public static Task B231UnrelatedEffectKeepsMoveNoticeAsync() => throw new NotImplementedException(
        "DOUBLE: the unrelated-effect notice protection lives in SQLite NoticeTable; the in-memory double keeps a " +
        "single slot by path/kind and cannot reproduce the mapped-move subject rejection (NoticeTable.cs:119).");

    public static Task B240ArrivalFloodOpportunityAsync() => throw new NotImplementedException(
        "DOUBLE: an arrival flood with small unrelated arrivals needs per-change opportunity accounting that the " +
        "change-page doubles do not expose.");

    public static Task B250CapacityWaitCancellationAsync() => throw new NotImplementedException(
        "DOUBLE: injected mid-wait cancellation followed by another active pair needs concurrent pair execution, " +
        "which the single-pair doubles do not model.");

    public static Task L60FolderRemovalSaturationAsync() => throw new NotImplementedException(
        "DOUBLE: pending folder removals occupying every continuation slot needs a bounded continuation pool; " +
        "the doubles run removals serially.");

    public static Task L64ChurnOpportunityAsync() => throw new NotImplementedException(
        "DOUBLE: sustained churn starvation needs replenished refused work across attempts; the double pages are finite.");

    public static Task L66SqlOrdinalsAndParserDepthsAsync() => throw new NotImplementedException(
        "DOUBLE: hand-coded SQL ordinals and provider parser depths need the real SQLite NoticeTable and a reordered " +
        "provider payload; the in-memory double cannot express either.");

    public static Task L84FolderScaleLookupsAsync() => throw new NotImplementedException(
        "DOUBLE: folder-scale account/descendant read counts need echo/mapped-range acquisition; " +
        "the F1 echo and mapped-range methods are stubs.");

    public static Task L87ScopeChangeAtIntakeAsync() => throw new NotImplementedException(
        "DOUBLE: a scope change taking effect at intake needs a live scope mutation between pages; " +
        "the doubles seed a fixed PathScope.");

    public static Task L98MissingMappedParentChildAsync() => throw new NotImplementedException(
        "DOUBLE: a bound-elsewhere/missing mapped parent with a child arriving before it needs AutoSync intake; " +
        "the doubles seed terminal entries.");

    public static Task L101ExcludedDescendantFloodAsync() => throw new NotImplementedException(
        "DOUBLE: the excluded-descendant read flood after a folder move needs tombstone routing under exclusion; " +
        "the doubles do not script tombstones for excluded ranges.");

    public static Task L102MissingRootPointerAsync() => throw new NotImplementedException(
        "NI: missing product-root pointer recovery is not implemented at F1 (RecoveryChecks and scenario proof incomplete).");

    public static Task L107AcceptedJobPollingAsync() => throw new NotImplementedException(
        "DOUBLE: evidence-based accepted-job checking without fixed polling needs a live job feed; " +
        "the doubles script discrete job replies.");

    public static Task L112HighChurnTreeAsync() => throw new NotImplementedException(
        "DOUBLE: a selected high-churn nested tree syncing without persistent refusals needs sustained churn; " +
        "the doubles are finite.");

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
}
